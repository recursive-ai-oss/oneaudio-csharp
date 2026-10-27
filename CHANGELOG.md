# Changelog

All notable changes to `RecursiveAI.OneAudio` are recorded here. The format follows [Keep a Changelog](https://keepachangelog.com/en/1.1.0/);
versions follow [Semantic Versioning](https://semver.org/) once 1.0.0 is released. Until then, every `0.x` release may change the API.

## [Unreleased]

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
