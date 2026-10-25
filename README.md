# oneaudio-csharp

Audio decode, encode, resample, mix and analyse for .NET 10, written from the specifications with **no dependencies beyond the base class library**. Built first for game audio: the mixer's render callback allocates nothing and takes no locks. NativeAOT- and trim-clean.

Package id `RecursiveAI.OneAudio`, namespace `OneAudio`. License: MIT OR Apache-2.0 (see `LICENSE-MIT`, `LICENSE-APACHE`). Not published anywhere yet.

## What works today

- Format-detecting open (`AudioDecoder.Open`) with explicit registration of containers and codecs.
- WAV / RF64 decode: PCM 8/16/24/32-bit, IEEE float 32/64, `WAVE_FORMAT_EXTENSIBLE`; streaming, sample-accurate seeking, bit-exact integer reads.
- FLAC decode (`AddFlac()`): native `.flac` and FLAC in Ogg, including chained Ogg; bit-exact integer reads, CRC-checked frames, sample-accurate seeking (SEEKTABLE or frame-sync search), optional STREAMINFO MD5 verification (`FlacDecoder.VerifyMd5`).
- The mixer (`AudioMixer`): voices, a bus graph with sub-mixes, ducking and smoothing, multichannel mapping, per-voice pitch and rate conversion through `IResampler`, gain, pan, loops, fades, crossfade, voice cap with priority, streaming sources with a decode-ahead pump and underrun accounting.
- Loudness (`LoudnessMeter`): BS.1770-5 / EBU R 128 momentary, short-term, integrated, loudness range and true peak, any sample rate, allocation-free; normalisation gain helper. Passes the EBU Tech 3341 / 3342 minimum-requirement signals.
- Contracts (interfaces only) for packet-based containers, codecs and resamplers; the Ogg, Vorbis, Opus, FLAC, MP3 codecs and the resamplers are built against them.

## Quick look

```csharp
var registry = new AudioCodecRegistry().AddWav();          // opt in to formats; nothing is found by reflection
using var decoder = AudioDecoder.OpenFile("hit.wav", registry);
var clip = AudioClip.FromDecoder(decoder);                 // short effect: decode once, play many times

var mixer = new AudioMixer(new MixerOptions { SampleRate = 48000, Channels = 2 });
var voice = mixer.Play(new PlayParams(clip) { Gain = 0.8f, Pan = -0.3f });

// On the device thread:   mixer.Render(deviceBuffer);     // interleaved float, frames = length / channels
// On the game thread:     mixer.Stop(voice, fadeOutFrames: 480);
```

The API is described in [docs/api.md](docs/api.md).

## Layout

| Path | Contents |
|---|---|
| `src/OneAudio/` | the library; `Kernels/` is the only place unsafe code may live |
| `tests/OneAudio.Tests/` | xUnit v3 tests |
| `tests/OneAudio.AotSmoke/` | NativeAOT console app: open WAV, decode, mix |
| `bench/OneAudio.Benchmarks/` | BenchmarkDotNet |
| `fuzz/` | SharpFuzz targets |
| `tools/` | corpus fetch script, test-data generators |

## Building

`dotnet build OneAudio.slnx` and `dotnet test --project tests/OneAudio.Tests/OneAudio.Tests.csproj`. Build output goes to `$ONEAUDIO_ARTIFACTS` (default: `.oneaudio-artifacts` beside the checkout), never into the tree. Third-party test material is downloaded by `tools/fetch-corpus.sh` and never committed.

## Clean room

The codecs are written from the published specifications (RIFF/WAVE, RFC 3533, Vorbis I, RFC 6716/8251, RFC 9639, ISO/IEC 11172-3, ITU-R BS.1770). Other implementations are used only as black-box test oracles.
