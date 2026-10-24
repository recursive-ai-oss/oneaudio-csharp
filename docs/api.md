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

## Containers, codecs, registration

- `IContainerFormat` recognises a format (`Matches`) and opens it. Containers that carry compressed packets expose them as an `IPacketSource` (`TryReadPacket`, `SeekToFrame`, `LengthFrames`) and look up the codec by `CodecId` in the registry. Raw-PCM containers (WAV) decode directly.
- `IAudioCodec.CreateDecoder(IPacketSource, AudioLimits)` turns packets into an `IAudioDecoder`.
- `AudioCodecRegistry` starts empty: `new AudioCodecRegistry().AddWav()` and, as they arrive, further `AddXxx()` extension methods. Unused formats are trimmed away.

## Errors and limits

`OneAudioException` is the base of everything the library says about data: `AudioFormatException` (malformed), `UnsupportedAudioFormatException` (valid but not supported / no codec registered), `AudioLimitExceededException` (names the limit, the maximum and the request). Besides these, public methods throw only argument exceptions, `NotSupportedException`, `ObjectDisposedException`, stream I/O exceptions and `OperationCanceledException`.

`AudioLimits` (immutable record, `AudioLimits.Default`) caps channels, sample rate, packet bytes, frame size, metadata bytes, codec setup bytes and container elements. Sizes read from input are checked before the allocation they guard.

## Resampling

`IResampler` (interface only for now): `Channels`, `Quality` (`Linear` or `Polyphase`), `Ratio` / `SetRatio`, `LatencyFrames`, `MaxOutputFrames`, `Process(input, output, out used, out written)`, `Flush`, `Reset`. Implementations allocate at construction and never in `Process`.

## Mixing

`AudioMixer` is the real-time mixer. The host creates it with `MixerOptions` (fixed for its lifetime): `SampleRate`, `Channels`, `OutputChannelMask` (default: the conventional layout for the channel count), `MaxVoices`, `MaxBuses`, `MaxSourceChannels` (8), `MaxBlockFrames`, `CommandCapacity`, `EventCapacity`, `SmoothingMilliseconds` (10) and `ResamplerFactory`.

- **Render (audio thread):** `Render(Span<float> output)` fills interleaved float at the mixer's rate and channel count; the frame count is `output.Length / Channels`. No allocation, no locks, no blocking, no exceptions, no callbacks. The mix is summed, not clipped. Work is done in passes of at most `MaxBlockFrames`; queued commands apply at pass boundaries.
- **Control (any other thread):** `Play(PlayParams)` returns a `VoiceHandle`; `Stop`, `StopAll`, `SetGain` (with ramp), `SetPan`, `SetPitch`, `SetLoop`, `Crossfade`; bus calls `SetBusGain`, `SetBusMute`, `SetBusOutput`, `SetDucking`, `ClearDucking`, `SetMasterGain`. Calls write into a preallocated lock-free ring that the render thread drains. A full ring drops the command, returns false (or an invalid handle) and counts it in `DroppedCommands`. Handles are never reused, so a stale handle is ignored.
- **`PlayParams`:** `Source`, `Gain`, `Pan`, `Loop` / `LoopStartFrame` / `LoopEndFrame`, `Bus`, `Priority`, `StartAtFrame` (frame-exact on the mixer timeline), `FadeInFrames`, `Pitch`, `VariablePitch`, `Resampling`.
- **Voice cap:** when all voices are busy, a new voice replaces the lowest-priority (oldest on ties) voice if that voice's priority is not higher; otherwise the new request is dropped.
- **Sources:** `AudioClip` (decoded PCM in memory, shareable between voices) and `StreamingSource` (a decoder read ahead by a decode thread, optionally looping between two decoder frames via `Seek`; the render thread only reads its preallocated ring). Sources have 1 to `MaxSourceChannels` channels at any sample rate.
- **Decode-ahead:** `StreamingPump` keeps any number of `StreamingSource` rings full from one background thread (`Start`) or from the host's own loop (`PumpOnce`). `Add` primes the ring so a voice starts without an underrun; finished or failed sources are dropped automatically (`StreamingSource.Fault`).
- **Channel mapping:** a mono source is panned across the output's front pair (constant power); matching speakers pass straight through; missing speakers fold (centre to front pair at -3 dB, surrounds and sides to each other or to the front pair at -3 dB); LFE is dropped unless the output has one; stereo on a larger layout plays on front left/right only. Pan on a non-mono source is a balance. Layouts come from `AudioFormat.ChannelMask` and `MixerOptions.OutputChannelMask`; counts without a conventional layout map by index.
- **Resampling and pitch:** a source whose rate differs from the mixer's, a `Pitch` other than 1 or `VariablePitch` gives the voice an `IResampler` from `MixerOptions.ResamplerFactory` (created on the control thread and recycled; the render thread only calls `Process`). `SetPitch` (0.0625 to 16, optional glide) changes speed and pitch. Without a factory such voices are rejected with `ArgumentException`.
- **Buses:** every voice feeds one bus; each bus feeds the master output (default) or another bus (`SetBusOutput`), so sub-mixes sum into their parent. Routes that would form a loop (including through ducking links) are rejected with `ArgumentException` on the control side. `SetDucking(target, source, DuckingSettings)` turns the target down while the source bus is loud (threshold, depth, attack and release). Pan, bus gain, bus mute and master gain changes ramp over `SmoothingMilliseconds` unless an explicit ramp is given.
- **Underruns:** if a streaming ring runs dry before the decoder is done, the voice renders silence for the gap. Counters are readable without locks from any thread: `AudioMixer.UnderrunEvents` / `UnderrunFrames`, and per source `StreamingSource.UnderrunEvents` / `UnderrunFrames`. Other counters: `DroppedCommands`, `DroppedPlays`, `DroppedEvents`, `StolenVoices`, `ActiveVoices`, `TimelineFrames`.
- **Events:** `TryGetEvent(out MixerEvent)` polls what the render thread noticed (`VoiceEnded`, `VoiceStolen`, `PlayDropped`, `Underrun`). Nothing is called back on the render thread.
