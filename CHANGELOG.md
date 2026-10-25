# Changelog

All notable changes to `RecursiveAI.OneAudio` are recorded here. The format follows [Keep a Changelog](https://keepachangelog.com/en/1.1.0/);
versions follow [Semantic Versioning](https://semver.org/) once 1.0.0 is released. Until then, every `0.x` release may change the API.

## [Unreleased]

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
