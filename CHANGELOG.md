# Changelog

All notable changes to `RecursiveAI.OneAudio` are recorded here. The format follows [Keep a Changelog](https://keepachangelog.com/en/1.1.0/);
versions follow [Semantic Versioning](https://semver.org/) once 1.0.0 is released. Until then, every `0.x` release may change the API.

## [Unreleased]

## [1.4.0] - 2026-10-29

AAC-LC and AMR/AMR-WB decoding, with MP4/M4A/3GP, ADTS and AMR file containers. `RecursiveAI.OneAudio.Devices` changes only its version number. The API changes are additive.

### Added

- Containers for AAC and AMR (without an AAC codec registered, opening an AAC file throws `UnsupportedAudioFormatException` naming the missing codec):
  - `Mp4PacketSource` / `AddMp4()`: MP4, M4A, 3GP and 3G2 audio tracks (ISO/IEC 14496-12, 3GPP TS 26.244) with `mp4a` (the AudioSpecificConfig is passed on untouched), `samr` and `sawb` sample entries. Only the movie box is parsed, whether it comes before or after the media data; packets are read one sample at a time, with exact per-packet timestamps and durations, sample-exact seeking, track selection (`Tracks`, `SelectTrack`), priming and trimming from the edit list or the iTunes `iTunSMPB` tag (the tag wins when well formed), and `Title`/`Artist`/`Comments` from iTunes `ilst` and 3GPP `udta` boxes. A forward-only stream works when the movie box comes first. Fragmented files are rejected with a typed error.
  - `AdtsPacketSource` / `AddAdts()`: raw AAC in ADTS framing, with the AudioSpecificConfig built from the frame header, ID3v2 skipping and seeking by scanning.
  - `AmrPacketSource` / `AddAmr()` and `AmrFrameTable`: AMR and AMR-WB storage-format files (RFC 4867 section 5) with 20 ms frame seeking.
  - `Audio.Open` and friends now recognise these containers.
- AAC-LC decoder (`AacDecoder`, `AacCodec`, registered by `AddAac()`, and part of `Audio`'s default set), written from 3GPP TS 26.401/26.410 and checked against ISO/IEC 14496-26: mono and stereo at 8 to 48 kHz from M4A/MP4/3GP and ADTS, with every AAC-LC tool (single channel and channel pair elements, program config elements for one or two front channels, all window sequences and both window shapes, pulse data, TNS, PNS, M/S and intensity stereo, fill and data stream elements skipped). Priming and trimming from the container give the true length; seeking pre-rolls one frame and is bit-identical to a sequential decode. A damaged or empty access unit is concealed (`ConcealErrors`, `ConcealedFrames`; the last spectrum fades out) instead of ending the stream, when asked. No allocation per frame after the first.
  - `AacAudioConfig` (full AudioSpecificConfig parse), `AacTools` (tools seen so far, `AacDecoder.ToolsSeen`) and `AacDecoder.SbrDataIgnored`: a plain LC stream that carries SBR fill data decodes as its core at the core rate, and the flag says so.
  - Typed `UnsupportedAudioFormatException` for more than two channels, LFE or coupling elements, sample rates above 48 kHz (and 7350 Hz), 960-sample frames, AAC Main, LTP, SSR, scalable and error-resilient/low-delay object types, and explicit SBR/PS signalling.
- AMR (narrowband) and AMR-WB speech decoders, written from 3GPP TS 26.071/26.090/26.091/26.092/26.101 and TS 26.171/26.190/26.191/26.192/26.201 with the fixed-point arithmetic of the TS 26.073/26.173 reference code, so output is bit-exact against it. All eight AMR modes (4.75 to 12.2 kbit/s) and all nine AMR-WB modes (6.60 to 23.85 kbit/s), comfort noise (SID) with dithering, no-data bridging, bad and lost frame concealment, and decoder homing frames.
  - `AmrNbDecoder` (8 kHz) and `AmrWbDecoder` (16 kHz): decode one storage-format frame (RFC 4867 section 5.3) to 16-bit or float PCM, `DecodeLost`, `Reset`; no allocation per frame. `AddAmrNb()` / `AddAmrWb()` register the packet-stream decoders, and `AddAmr()` now registers the containers and both decoders, so `.amr`, `.awb` and 3GP `samr`/`sawb` files play through `Audio.Open`.
  - Seeking resets the decoder and decodes 200 ms (10 frames) of preroll before the target: the position is exact, the audio converges on, but is not bit-identical to, a sequential decode (seeks within the first 10 frames are identical).
- Additive container API: `IPacketSource.CodecConfig`, `SampleRate`, `Channels`, `TimeScale` and `PrimingFrames` (default interface members), `PacketView.Timestamp`/`Duration`, `CodecId.Aac`/`AmrNb`/`AmrWb`, `IContainerFormat.Extensions` with `AudioCodecRegistry.DetectByExtension` (used by `AudioDecoder.OpenFile` when the start of the file is not recognised), and `AudioLimits.MaxSamples`.

### Checked

- AMR and AMR-WB: all 573 TS 26.074 and all 221 TS 26.174 decoder test sequences decode bit-exact, both through the reference test-format adapter and through storage-format frames; real sequences wrapped in `.amr`/`.awb` and re-muxed to `.amr`/`.awb`/3GP by ffmpeg decode identically to the 3GPP reference decoder; streams with injected bad, degraded, onset, lost, no-data and bad-SID frames match the reference decoder (the conformance sequences themselves contain only good, SID-first, SID-update and no-data frames); 120 s of fuzzing per decoder found no crash.
- AAC-LC: 90 of the ISO/IEC 14496-26 AAC-LC mono/stereo streams (8 to 48 kHz) decode within the full-accuracy limits we apply (RMS error below 2^-15/√12 and maximum error at most 2^-14 of 16-bit full scale; 72 streams have RMS 3.5e-8 to 1.4e-7 and maximum 1.4e-6 or less against the 24-bit reference waveforms). The 18 perceptual-noise-substitution streams are informative in the suite, because the noise waveform is left to the decoder; they match the reference band energy within 0.09 dB. The out-of-scope streams (multichannel, above 48 kHz, Main, LTP, SSR, low delay, scalable, explicit SBR/PS, 960-sample frames) give the typed error. Against ffmpeg 8.0.1 decoding of 11 files it encoded itself (8 to 48 kHz, mono and stereo, CBR and VBR, M4A/MP4/3GP/ADTS) the maximum difference is 2.4e-7 (SNR 136 dB) with equal lengths; 120 s of fuzzing found no crash (310158 runs). faad and fdkaac were not available for cross-checks.
- Packet bytes, sizes, timestamps and durations equal `ffprobe -show_packets` (ffmpeg 8.0.1) for every packet of our generated M4A (moov first and last), MP4, 3GP (AAC, AMR-NB, AMR-WB), iTunes-style gapless M4A, two-track MP4, ADTS (through an `-c copy` remux, which strips the ADTS headers) and AMR/AWB files, and of 7 of 9 ISO/IEC 14496-26 conformance MP4s (the other two cannot be opened by ffprobe at all; they are checked against their own sample tables). Seeking to random times returns the packet containing the time; 120 s of fuzzing per reader found no crash.

- WASAPI playback on real hardware: a Windows 11 laptop (build 26200) with Realtek High Definition Audio speakers, as the default output in shared mode (32-bit float, 480-frame period, 1056-frame buffer, 22 ms reported latency), both from a service session and from the logged-in desktop session. Over five seconds of a -34 dBFS tone `FramesPlayed` advanced about 48,000 frames per second from the device clock, `PeakPlayed` was 0.0200, `Underruns`, `DeviceUnderruns`, `CallbackFaults` and `RenderAllocatedBytes` were 0, the longest render took under 1 ms, suspend held the position and resume continued, and an unknown device id was refused with a reason. Not yet checked on real hardware: WASAPI exclusive mode, capture from a real microphone, and default-device and hot-plug changes.

## [1.3.0] - 2026-10-27

Adds an Android backend to `RecursiveAI.OneAudio.Devices`; the core package changes only its version number. The API changes are additive.

### Added

- `AAudioBackend` (`DeviceBackendKind.AAudio`): playback and capture through AAudio (`libaaudio.so`, Android 8.0 / API 26 or later) over P/Invoke. Shared, low-latency, 32-bit float streams driven by AAudio's data callback, so the engine renders straight into the device buffer. `FramesPlayed` comes from the device's timestamp (clamped to the frames it has read), `QueuedFrames` from frames written minus read, and the device's xrun count feeds `DeviceCounters.DeviceUnderruns`. A disconnected stream is reported as a lost device and the engine reopens it. It is chosen automatically in an Android process, including a NativeAOT program built for `linux-bionic`, which runs on the Linux runtime.
- AAudio cannot enumerate devices: `List` returns one entry, `default`; a specific device is opened by its decimal Android audio device id. The device must give the rate and channel count asked for, otherwise the open fails with a reason. Exclusive mode is refused.
- `tools/android-smoke`: a NativeAOT console program and `run.sh` that build for `linux-bionic-arm64` or `-x64`, push over adb and play a -34 dBFS tone, printing the device counters.

### Fixed

- On the Mono runtime for browser-wasm, `AudioMixer`'s render callback allocated a few hundred bytes the first time each feature ran (the first voice, a resampled voice, a filter, ducking, a bus meter, the end of a voice), so `DeviceCounters.RenderAllocatedBytes` read above zero in the browser. The cause is the interpreter's first execution of a method, which creates runtime type and generic-instantiation objects on the managed heap; compiled runtimes (CoreCLR, NativeAOT) do not. The mixer now runs a short throwaway session through every render-path feature when it is first created on an interpreting runtime (about a quarter of a second once per process in Chrome; nothing on CoreCLR or NativeAOT), and the counter reads 0 from the first callback. `tools/wasm-alloc-check` publishes a browser-wasm program and runs the mixer through voices, loops, crossfades, stops, buses, filters, reverb, ducking, meters and the limiter in headless Chrome, failing on any allocated byte; the `ci` workflow runs it.

### Checked

- On an Android 14 x86_64 emulator (no real sound output; the emulator's audio was disabled): five seconds of tone through the NativeAOT smoke, with `FramesPlayed` advancing 48,005 frames per second from the device clock, `PeakPlayed` 0.0200, `Underruns` 0, `DeviceUnderruns` 0, `RenderAllocatedBytes` 0, suspend holds the position and resume continues, and capture opened and delivered frames.
- On a Pixel 8 Pro (Android 17, arm64), playback through the speaker with the NativeAOT `linux-bionic-arm64` smoke: the stream opened on AAudio's low-latency MMAP path with a 96-frame period and a 960-frame buffer (22 ms reported latency); over five seconds `FramesPlayed` advanced about 48,000 frames per second from the device clock, `PeakPlayed` was 0.0200, `Underruns`, `DeviceUnderruns` and `RenderAllocatedBytes` were 0, the longest render took 0.26 ms, and suspend held the position and resume continued.
- **Not yet checked on real hardware:** capture from a real microphone (and its permission flow), Bluetooth or headphone route changes, and a real disconnect. The disconnect-and-reopen path is covered by a unit test of the stream's error handling and by the engine's existing reopen tests. An unknown device id opened the default device on both the emulator and the phone, rather than failing.
- The build produces no trim or AOT warnings for `linux-bionic-x64` and `linux-bionic-arm64`.

## [1.2.1] - 2026-10-27

A patch release for `RecursiveAI.OneAudio.Devices`; the core package changes only its version number. The API changes are additive.

What has been checked on real hardware is unchanged from 1.2.0 (see below); the fixes here are covered by tests over the manual backend and a stream that cannot name its device.

### Fixed

- A stream whose backend cannot name its device (`IOpenDevice.Device` is `null`, as in a browser audio context) was taken for a change of the default device on every poll, so the stream was reopened about once a second and counted as a default switch. `null` now means "unknown": the package compares the stream's device with the default only when both are known. A backend that sees the default change itself still says so with `NotifyDefaultChanged`, and that is honoured. The meaning of `null` is documented on `IOpenDevice.Device`.
- `DeviceCounters.RenderAllocatedBytes` could read a few hundred bytes on a host that steps a manual device in short blocks. We could not reproduce it: the package's render path allocates nothing after warm-up (now tested with 480-frame blocks, with and without rate conversion), so the bytes most likely come from first-use work in the program's own source or the runtime after the eight callbacks the probe skips. The skipped count is now an option, below.

### Added

- `IOpenDevice.StateText` (a default interface member returning `null`, so existing backends are unaffected): a short state word such as `awaiting_gesture`. It is exposed as `AudioOutputDevice.StateText` and `AudioInputDevice.StateText` and appended to `Detail` after the route extras.
- `DeviceStreamOptions.AllocationWarmupBlocks` (default 8, the previous fixed value): how many callbacks `RenderAllocatedBytes` ignores at the start of a stream.

## [1.2.0] - 2026-10-27

Device playback and capture, in the new `RecursiveAI.OneAudio.Devices` package (same version as the core package).

What has been checked on real hardware so far: ALSA playback and capture on Linux (PipeWire); Core Audio playback on macOS, including following a
change of the default output; and playback on an iPad (iPadOS 17, NativeAOT) through the speaker, with the device clock, peaks and zero
underruns and allocations confirmed, and a simulated audio-session interruption suspending and resuming the stream. Not yet on real hardware:
WASAPI (checked by unit tests and on Windows CI runners, which have no audio device), capture from a real microphone on macOS and iOS, and a
real system interruption on iOS (a call or another app taking the audio session).

### Added

- `IAudioSource` (sample rate, channels, `Render`), implemented by `AudioMixer`: anything an output device can pull audio from.
- A new package, `RecursiveAI.OneAudio.Devices`, versioned in step with the core package: device playback and capture. This first part is the backend-independent
  layer: `AudioOutputDevice` and `AudioInputDevice` (callbacks on the device thread, a limiter, an optional BS.1770 meter, default-device following, reopen after a
  lost device, silent real-time pacing while no device is open, events), `DeviceCounters` (frames rendered, written and played from the device's own position,
  played peak, underruns, losses, reopens), `AudioDevices`, the `IAudioDeviceBackend` seam, and `ManualBackend` for tests and offline runs. The one-call
  layer is `Audio.Play(mixer)`, `Audio.CreateOutput` and `Audio.CreateInput`, as C# 14 extension members on `Audio`.
- `AlsaBackend` (Linux playback through ALSA, including PipeWire and PulseAudio through their ALSA plug-ins): enumeration, period and buffer negotiation, float or 16-bit,
  underrun recovery, device position from `snd_pcm_delay`, and capture (`Audio.CreateInput`, callback or `Read`).
- `CoreAudioBackend` (macOS and iOS): device enumeration with default and hot-plug notifications, playback and capture through audio units with rate and channel conversion, a device-clock `FramesPlayed`, follow-the-default and device-loss handling, microphone-permission refusal with a reason, and on iOS the audio-session category, interruptions and route changes. `tools/ios-smoke` runs it in the iOS simulator.
- `WasapiBackend` (Windows playback and capture through WASAPI): MMDevice enumeration, shared mode with automatic rate and channel conversion, exclusive mode with format negotiation and a stated refusal, device position from `IAudioClock`, endpoint notifications for default and hot-plug changes, lost-device handling, and the "Pro Audio" scheduler class on the device thread. All COM calls are v-table function pointers, so it is trim and NativeAOT clean. Verified so far by unit tests over hand-built COM objects and by CI on Windows; not yet on real hardware.
- Single-threaded hosts: `IManualDevice` (public; `Advance(frames)`, optional `WantedFrames`) lets any backend whose `IsManual` is true be stepped by the caller, with no thread started by the package. `AudioOutputDevice.Advance` now also does the supervisor's work (reopen after a loss, default change, retry, events), so `Pump()` is optional; `AudioOutputDevice.WantedFrames` reads the stream's request.
- `DeviceStreamOptions.AllowRateConversion` (default true): an output device running at another sample rate than the source gets the source converted with the polyphase resampler; `AudioOutputDevice.DeviceFormat` reports the device's format and `ManualBackend.DeviceSampleRate` acts out such a device.
- `AudioOutputDevice.IsSuspended` and `IsSystemSuspended` (also on `AudioInputDevice`): readable suspension state; `Suspend` is documented to stop the source's clock and, once the queue has played out, the device's.
- `AudioOutputDevice.Muted`: feeds silence without rendering the source (its clock stands still); `DeviceCounters.MutedFrames` counts it.
- `DeviceCounters.DeviceUnderruns` (the device API's own count, reported through the optional `IDeviceEngine.NotifyDeviceUnderruns`) next to `Underruns`.
- `RenderAllocatedBytes` is always kept (measured at the noise of a callback's time); `DeviceStreamOptions.MeasureRenderAllocations` is now a no-op.
- `BlockHook` on both devices: a per-block callback with the frames handed over so far.
- `ManualBackend.Sink`: every output block streamed to a callback with no capacity limit.
- `AudioOutputDevice.Loudness` returns the core's `LoudnessSnapshot` (with the maxima) instead of a type of its own; `LoudnessReading` is gone.

## [1.1.0] - 2026-10-26

### Added

- A one-call layer on top of the existing API, nothing in it changes: `Audio` (and `AudioKit`, the same calls over a registry of your choosing) with
  `Open`, `Load` (decode to an `AudioClip`, optionally converting rate and channels), `Transcode` to WAV, FLAC or Ogg Opus, `Resample`,
  `Measure`, `Normalize` (samples, clips or file to file, default -14 LUFS with a -1 dBTP true-peak ceiling) and `CreateMixer`.
- `mixer.Play(path)` and `mixer.PlayStream(path, loop: true)` extension methods (`AudioMixerPlayExtensions`), with `PlayOptions` and a `StreamingVoice` handle.
- Options records `LoadOptions`, `TranscodeOptions`, `NormalizeOptions`, `PlayOptions`; results `TranscodeResult` and `NormalizeResult`; enum `AudioFileFormat`.
- `AudioClip.Samples`: a read-only view of the decoded samples.
- `tests/OneAudio.AotSize`: three tiny NativeAOT apps that compare published size with the default registry, a WAV-only `AudioKit` and the low-level API.

## [1.0.0] - 2026-10-25

The first stable release. From here on the public API follows Semantic Versioning.

- Decode WAV / RF64, Ogg (chained), Vorbis, Opus, FLAC (native and Ogg) and MP3 (MPEG-1/2/2.5 Layer III), streaming with exact seeking.
- Encode WAV / RF64, FLAC (levels 0 to 8) and Opus (including Ogg Opus files).
- Polyphase and linear resampling; the real-time mixer with buses, effects (filters, reverb, limiter) and per-bus loudness meters;
  EBU R 128 / BS.1770 loudness with true peak and normalisation.
- No changes to the API since 0.1.0-alpha.5. Symbol packages (`.snupkg`) are published alongside the package.

## [0.1.0-alpha.5] - 2026-10-25

### Fixed

- Ogg Vorbis: a short stream that fits on a single page, such as a brief click followed by digital silence, decoded to silence or lost its
  first or last samples. The one granule position on that page marks where the stream ends, but the decoder also read it as the position of the
  stream's first sample and cut the difference from the start. It is now taken as the end trim only. Streams whose first audio page is not the
  last page are unchanged.

## [0.1.0-alpha.4] - 2026-10-25

### Added

- `AudioMixer.TryEnableBusMeter(bus)`: enables a bus meter without throwing when no meter is free (or the mixer has none); `FreeBusMeters`
  tells "no slot" from "queue full" after a false. `AudioMixer.IsBusMetered(bus)` reports whether a bus currently holds a meter.
- Results in the README: conformance, fuzzing and benchmarks against other libraries and native tools, losses included.

## [0.1.0-alpha.3] - 2026-10-24

### Added

- Per-bus loudness metering in the mixer. `MixerOptions.MaxBusMeters` (0 to 64, default 0) preallocates that many BS.1770 / EBU R 128
  meters at construction; `AudioMixer.EnableBusMeter`, `DisableBusMeter` and `ResetBusMeter` turn one on, off or back to zero for any bus or
  for the master output (`AudioMixer.MasterBus`) through the command queue; `AudioMixer.TryGetBusLoudness` reads it from any thread without
  locking. The reading is a `LoudnessSnapshot` (momentary, short-term and integrated LUFS, true peak in dBTP, the two maxima, frames
  measured) published once per render pass, never torn. A bus is measured after its effects and gain; the master output is measured after the
  limiter. A mixer without enabled meters does no metering work, and the render path still allocates nothing with meters enabled and toggling.
- `AudioMixer.FreeBusMeters` and `AudioMixer.FreeReverbSlots`, the number of meter and reverb slots not yet in use.

### Changed

- **Behaviour change:** `AudioMixer.SetBusReverb` throws `InvalidOperationException` (naming `MixerOptions.MaxReverbs`) when the bus has no reverb
  and every reverb slot is taken, or the mixer has none. It used to return `false`, the same value as for a full command queue, which
  was easy to miss. `false` now means only that the command queue was full (and is counted in `DroppedCommands`). Retuning a bus that already
  has a reverb never throws. The signature is unchanged.

## [0.1.0-alpha.2] - 2026-10-24

### Added

- Decoders: FLAC (native and Ogg FLAC, bit-exact against the reference decoder on the public test corpus, exact seeking) and MP3 (MPEG-1, 2 and
  2.5 Layer III with ID3v2, Xing/LAME and VBRI handling, gapless trimming and exact seeking, within the ISO/IEC 11172-4 accuracy limits).
- Chained Ogg streams: consecutive links of the same format play as one stream, with exact seeking and length across links.
- Loudness: `LoudnessMeter` (ITU-R BS.1770-5 and EBU R 128: momentary, short-term, integrated, loudness range, true peak), `LoudnessAnalyzer` and
  `LoudnessNormalization`; the EBU Tech 3341 and 3342 test signals are inside their tolerances.
- Mixer effects: low-pass and high-pass filters on voices, buses and the master output, a reverb insert (`MixerOptions.MaxReverbs`), and a
  lookahead limiter on the master output (`MixerOptions.LimiterLookaheadMilliseconds`). All state is preallocated; the render path allocates nothing.
- Streaming sources report position and length; ducking can be removed again (`DuckingSettings.None`, `ClearDucking`).
- Encoders: `WavEncoder` (RIFF and RF64, extensible), `FlacEncoder` (levels 0 to 8, seek table, MD5; output decodes bit-exact), and the Opus
  encoder (SILK, hybrid and CELT, byte-identical to the RFC 6716 reference encoder on the tested configurations) with the Ogg Opus file writer
  (RFC 7845).

## [0.1.0-alpha.1] - 2026-10-24

### Added

- Decoders for WAV (PCM 8/16/24/32-bit, float, extensible), the Ogg container, Vorbis and Opus (all RFC 8251 test vectors pass), behind an
  explicit codec registry so unused codecs trim away. Streaming reads into caller-provided spans, with seeking.
- Resampling: polyphase (two quality tiers) and linear.
- The real-time mixer: voices and buses, gain, pan, sample-accurate start and loop points, crossfades, ducking, voice caps with priority, per-voice
  rate conversion and pitch, multichannel layouts, and a decode-ahead pump for streaming sources. The render callback is lock-free and
  allocates nothing. Vectorised mixing kernels with scalar reference paths.
- NativeAOT and trim clean; fuzz targets for the parsers and decoders.
