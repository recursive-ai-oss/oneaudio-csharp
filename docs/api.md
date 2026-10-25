# API overview

Everything is in namespace `OneAudio`. Units: a **frame** is one sample per channel; a buffer of N frames holds N x channels interleaved samples. Gains are linear unless a name says dB.

## Formats

- `AudioFormat(SampleRate, Channels, ChannelMask, NativeType, BitsPerSample)` describes decoder output. `ChannelMask` uses the WAVE_FORMAT_EXTENSIBLE speaker bits.
- `NativeSampleType` says how the source stored samples (`PcmUnsigned8`, `PcmSigned16/24/32`, `IeeeFloat32/64`, or `Lossy`).

## Decoding

`IAudioDecoder` is a pull-model streaming decoder:

- `Format`, `LengthFrames` (-1 if unknown), `PositionFrames`.
- `int Read(Span<float>)` returns frames of interleaved float, never clipped; 0 means end. Short reads are normal.
- `int ReadInteger(Span<int>)` returns exact native integers for lossless PCM sources (`HasNativeIntegers`); 8-bit is re-centred to -128..127.
- `CanSeek` / `Seek(long frame)`: sample-accurate. Non-seekable sources (a pipe or socket) throw `NotSupportedException` from `Seek`.
- Decoders allocate nothing per read and are used from one thread at a time.

`AudioDecoder.Open(Stream, AudioCodecRegistry, AudioLimits?, leaveOpen, CancellationToken)` detects the container from the first 512 bytes and returns a decoder; `OpenFile` does the same for a path. Non-seekable streams are supported through a replay buffer.

## MP3

`new AudioCodecRegistry().AddMp3()` registers MPEG-1, MPEG-2 and MPEG-2.5 Layer III (`Mp3Format`, decoder type `Mp3Decoder`). Layers I and II raise `UnsupportedAudioFormatException`.

- **Files:** ID3v2 tags in front are skipped (larger than `AudioLimits.MaxMetadataBytes` is refused before anything is read); ID3v1, APE and Lyrics3 tags at the end are ignored. Fixed, variable and free-format bitrates; mono, stereo, joint (mid/side and intensity) and dual channel. The output channel count is the first frame's; a later mono/stereo switch is repeated or averaged to fit.
- **Gapless:** a Xing/Info header with the LAME extension (also written by ffmpeg) gives the encoder delay and padding; the decoder skips `delay + 529` samples and `LengthFrames` is `frames * samplesPerFrame - delay - padding`, which is the length of the source. The header frame is never decoded. Files without it are decoded in full and nothing is trimmed (a VBRI or Xing header without the extension only supplies the frame count).
- **Seeking:** exact and bit-identical to a sequential decode. The first seek away from the start of a seekable stream makes one pass over the frame headers (untagged files do it at open, to learn their length); decoding then resumes up to 48 frames before the target to rebuild the bit reservoir and filterbank memory. Xing and VBRI seek tables are not used.
- **Damaged data:** frames that cannot be decoded (missing reservoir after a cut, bad Huffman data) come out as silence and decoding goes on; lost sync is searched for with a two-header check.

## Containers, codecs, registration

- `IContainerFormat` recognises a format (`Matches`) and opens it. Containers that carry compressed packets expose them as an `IPacketSource` (`TryReadPacket`, `SeekToFrame`, `LengthFrames`) and look up the codec by `CodecId` in the registry. Raw-PCM containers (WAV) decode directly.
- `IAudioCodec.CreateDecoder(IPacketSource, AudioLimits)` turns packets into an `IAudioDecoder`.
- `AudioCodecRegistry` starts empty: `new AudioCodecRegistry().AddWav()` and, as they arrive, further `AddXxx()` extension methods. Unused formats are trimmed away.

## Errors and limits

`OneAudioException` is the base of everything the library says about data: `AudioFormatException` (malformed), `UnsupportedAudioFormatException` (valid but not supported / no codec registered), `AudioLimitExceededException` (names the limit, the maximum and the request). Besides these, public methods throw only argument exceptions, `NotSupportedException`, `ObjectDisposedException`, stream I/O exceptions and `OperationCanceledException`.

`AudioLimits` (immutable record, `AudioLimits.Default`) caps channels, sample rate, packet bytes, frame size, metadata bytes, codec setup bytes and container elements. Sizes read from input are checked before the allocation they guard.

## Resampling

`AudioResampler.Create(channels, quality, ratio, options?)` (or `Create(inRate, outRate, channels, quality, options?)`) returns an `IResampler` for interleaved float audio: `Channels`, `Quality`, `Ratio` / `SetRatio`, `LatencyFrames`, `MaxOutputFrames`, `Process(input, output, out used, out written)`, `Flush`, `Reset`. Everything is allocated at creation; `Process`, `SetRatio`, `Flush` and `Reset` allocate nothing. Output is identical however the input is split into blocks.

- **Modes:** `Linear` (two-tap, cheapest), `PolyphaseFast` (44-tap Kaiser-windowed sinc, flat to 0.8 of the lower Nyquist frequency, about 75 dB alias rejection) and `Polyphase` (144-tap, flat within 0.05 dB to 0.9 of the lower Nyquist frequency, 100 dB+ rejection, THD+N below -100 dB). Polyphase blends two of 512 coefficient phases per output frame; below ratio 1 the filter stretches so its cutoff follows the output Nyquist frequency.
- **Time alignment:** output frame m is the signal at input time m / ratio; filter delay is compensated. `LatencyFrames` is the look-ahead in output frames: after N input frames about N * ratio - LatencyFrames output frames exist. `Flush` returns the rest (call until it returns 0).
- **Ratio:** `ResamplerOptions.MinRatio` (default 0.25) and `MaxRatio` (default 16) bound `SetRatio` and size the preallocated state (polyphase memory grows as 1 / MinRatio). `SetRatio` is glitch-free: the read position is continuous. In polyphase modes, a ratio below 1 that moves the cutoff by more than a few percent redesigns the coefficient bank inside `SetRatio` (allocation-free, but roughly 0.3 ms of work at ratio 0.5 to 1), so bend pitch with ratio changes of a few percent per call rather than per frame.

## Loudness

`LoudnessMeter(sampleRate, channels, channelMask, truePeak, loudnessRange)` (or from an `AudioFormat`) measures per ITU-R BS.1770-5 and EBU R 128. Feed interleaved float blocks of any size to `Process`; results never depend on how the stream was split into blocks. Everything is allocated in the constructor, so a meter can run on a real-time thread (single thread at a time, no locks).

- **Readouts:** `MomentaryLufs` (400 ms), `ShortTermLufs` (3 s), `IntegratedLufs` (gated, -70 LUFS absolute and 10 LU relative), `MaxMomentaryLufs`, `MaxShortTermLufs`, `LoudnessRange` (EBU Tech 3342, LU), `TruePeak` / `TruePeakDbtp` (4x oversampling, all channels). No data yet reads as negative infinity (range: 0). `Reset()` clears everything.
- **Channels:** weights come from the channel mask (1.41 for surround and side channels, LFE left out of the loudness but not the true peak); without a mask the standard layouts are assumed, and 5 channels mean L R C Ls Rs.
- **Sample rates:** the K-weighting filters are designed for the actual rate (4 kHz and up) so that their response matches the 48 kHz table of the Recommendation. Non-finite input samples count as zero.
- **True peak:** the interpolation filter lags the input by 12 samples; a peak inside the last 12 frames fed shows up only after more audio arrives.
- **`LoudnessAnalyzer.Measure(decoder, truePeak, cancellationToken)`** reads a decoder to the end through a pooled buffer and returns a `LoudnessResult`; `Measure(span, format)` does the same for samples in memory.
- **Normalisation:** `LoudnessNormalization.ComputeGain(integratedLufs, truePeakDbtp, targetLufs = -23, ceilingDbtp = -1)` returns a `NormalizationGain` (`GainDb`, `Linear`, `LimitedByPeak`): the gain to reach the target, cut back if the true peak would pass the ceiling. Silence gives 0 dB. `LoudnessResult.GainFor(...)` is the shortcut.

## Mixing

`AudioMixer` is the real-time mixer. The host creates it with `MixerOptions` (fixed for its lifetime): `SampleRate`, `Channels`, `OutputChannelMask` (default: the conventional layout for the channel count), `MaxVoices`, `MaxBuses`, `MaxSourceChannels` (8), `MaxBlockFrames`, `CommandCapacity`, `EventCapacity`, `SmoothingMilliseconds` (10) and `ResamplerFactory`.

- **Render (audio thread):** `Render(Span<float> output)` fills interleaved float at the mixer's rate and channel count; the frame count is `output.Length / Channels`. No allocation, no locks, no blocking, no exceptions, no callbacks. The mix is summed, not clipped. Work is done in passes of at most `MaxBlockFrames`; queued commands apply at pass boundaries.
- **Control (any other thread):** `Play(PlayParams)` returns a `VoiceHandle`; `Stop`, `StopAll`, `SetGain` (with ramp), `SetPan`, `SetPitch`, `SetLoop`, `Crossfade`; bus calls `SetBusGain`, `SetBusMute`, `SetBusOutput`, `SetDucking`, `ClearDucking`, `SetMasterGain`. Calls write into a preallocated lock-free ring that the render thread drains. A full ring drops the command, returns false (or an invalid handle) and counts it in `DroppedCommands`. Handles are never reused, so a stale handle is ignored.
- **`PlayParams`:** `Source`, `Gain`, `Pan`, `Loop` / `LoopStartFrame` / `LoopEndFrame`, `Bus`, `Priority`, `StartAtFrame` (frame-exact on the mixer timeline), `FadeInFrames`, `Pitch`, `VariablePitch`, `Resampling`.
- **Voice cap:** when all voices are busy, a new voice replaces the lowest-priority (oldest on ties) voice if that voice's priority is not higher; otherwise the new request is dropped.
- **Sources:** `AudioClip` (decoded PCM in memory, shareable between voices) and `StreamingSource` (a decoder read ahead by a decode thread, optionally looping between two decoder frames via `Seek`; the render thread only reads its preallocated ring). Sources have 1 to `MaxSourceChannels` channels at any sample rate.
- **Decode-ahead:** `StreamingPump` keeps any number of `StreamingSource` rings full from one background thread (`Start`) or from the host's own loop (`PumpOnce`). `Add` primes the ring so a voice starts without an underrun; finished or failed sources are dropped automatically (`StreamingSource.Fault`).
- **Channel mapping:** a mono source is panned across the output's front pair (constant power); matching speakers pass straight through; missing speakers fold (centre to front pair at -3 dB, surrounds and sides to each other or to the front pair at -3 dB); LFE is dropped unless the output has one; stereo on a larger layout plays on front left/right only. Pan on a non-mono source is a balance. Layouts come from `AudioFormat.ChannelMask` and `MixerOptions.OutputChannelMask`; counts without a conventional layout map by index.
- **Resampling and pitch:** a source whose rate differs from the mixer's, a `Pitch` other than 1 or `VariablePitch` gives the voice an `IResampler` (the library's `AudioResampler` unless `MixerOptions.ResamplerFactory` says otherwise). Converters are pooled: `PreallocatedConverters` are built at mixer construction (which also builds the shared filter tables), more are created inside `Play` on the control thread up to `MaxConvertedVoices` and recycled; the render thread only calls `Process`. Memory per stereo converter: Polyphase 0.3 / 0.6 / 1.2 MB at `ConverterMinRatio` 1 / 0.5 (default) / 0.25, PolyphaseFast about a third of that, Linear 2 KB; the default mixer with its default reservations is about 2.6 MB. `PlayParams.Resampling` defaults to PolyphaseFast for plain rate conversion and Linear for pitched voices. A voice whose effective ratio is exactly 1 skips the converter. `SetPitch` (optional glide) clamps to `ConverterMinRatio`..`ConverterMaxRatio`. Polyphase ratio moves that make the converter redesign its filters share `RatioRedesignsPerPass` (default 2) across all voices, so a pitch bend on many voices cannot blow one callback's deadline. A converter's look-ahead is compensated and its tail is flushed when the source ends.
- **Buses:** every voice feeds one bus; each bus feeds the master output (default) or another bus (`SetBusOutput`), so sub-mixes sum into their parent. Routes that would form a loop (including through ducking links) are rejected with `ArgumentException` on the control side. `SetDucking(target, source, DuckingSettings)` turns the target down while the source bus is loud (threshold, depth, attack and release). Pan, bus gain, bus mute and master gain changes ramp over `SmoothingMilliseconds` unless an explicit ramp is given.
- **Underruns:** if a streaming ring runs dry before the decoder is done, the voice renders silence for the gap. Counters are readable without locks from any thread: `AudioMixer.UnderrunEvents` / `UnderrunFrames`, and per source `StreamingSource.UnderrunEvents` / `UnderrunFrames`. Other counters: `DroppedCommands`, `DroppedPlays`, `DroppedEvents`, `StolenVoices`, `ActiveVoices`, `TimelineFrames`.
- **Events:** `TryGetEvent(out MixerEvent)` polls what the render thread noticed (`VoiceEnded`, `VoiceStolen`, `PlayDropped`, `Underrun`). Nothing is called back on the render thread.
