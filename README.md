# oneaudio-csharp

Audio decode, encode, resample, mix and analyse for .NET 10, written from the specifications with **no dependencies beyond the base class library**. Built first for game audio: the mixer's render callback allocates nothing and takes no locks. NativeAOT- and trim-clean.

Package id `RecursiveAI.OneAudio`, namespace `OneAudio`. License: MIT OR Apache-2.0 (see `LICENSE-MIT`, `LICENSE-APACHE`). Not published anywhere yet.

## What works today

- Format-detecting open (`AudioDecoder.Open`) with explicit registration of containers and codecs (`AddWav`, `AddOgg`, `AddVorbis`, `AddOpus`, `AddFlac`, `AddMp3`), so unused codecs trim away.
- **Decode:** WAV / RF64 (PCM 8/16/24/32-bit, IEEE float 32/64, `WAVE_FORMAT_EXTENSIBLE`), Ogg (chained streams, seeking), Vorbis, Opus, FLAC (native and in Ogg, bit-exact integer reads, STREAMINFO MD5 check) and MP3 (MPEG-1/2/2.5 Layer III, ID3v2, Xing/LAME gapless). Streaming into caller-provided spans, sample-accurate seeking everywhere.
- **Encode:** WAV / RF64 (`WavEncoder`), FLAC levels 0 to 8 (`FlacEncoder`) and Opus (`OpusEncoder`, `OpusMultistreamEncoder`, Ogg Opus writer `OpusFileWriter`). Encoders produce identical bytes for identical input on a platform.
- **Resampling:** `Polyphase` (Kaiser-windowed sinc), `PolyphaseFast` and `Linear`, independent of how the input is split into blocks.
- **The mixer** (`AudioMixer`): voices, a bus graph with sub-mixes, ducking, multichannel mapping, per-voice pitch, gain, pan, loops, fades, crossfade, voice cap with priority, streaming sources with a decode-ahead pump and underrun accounting, and low-pass / high-pass filters, a reverb and a lookahead limiter. The render callback allocates nothing and takes no locks.
- **Loudness** (`LoudnessMeter`): BS.1770-5 / EBU R 128 momentary, short-term, integrated, loudness range and true peak, any sample rate, allocation-free; a normalisation gain helper.
- Not included: device output, spatial audio, MIDI, AAC, MP3 Layers I and II, MP3 and Vorbis encoding.

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

## Results

### Conformance

Each row is checked against an outside oracle or a published vector set. Third-party material is fetched by `tools/fetch-corpus.sh` (pinned URLs and SHA-256) and never committed.

| Area | Check | Result |
|---|---|---|
| Opus decode | RFC 8251 test vectors, `opus_compare` at 48 kHz, plain and no-phase-inversion sets | all 12 vectors pass on both sets |
| Opus encode | packets compared byte for byte with the RFC 6716 reference encoder (CELT, low-rate SILK/hybrid, mode transitions, random configurations) | 269 of 269 configurations identical |
| Vorbis decode | Xiph test vectors against libvorbis 1.3.7 float output | frame counts equal; floor-1 streams within 2e-6 max abs and at least 125 dB SNR; floor-0 streams within 1e-4 and at least 85 dB (libvorbis approximates the floor-0 curve) |
| FLAC decode | IETF CELLAR flac-test-files against `flac` 1.4.3 and the STREAMINFO MD5 | `subset` 64 of 64 bit-exact; `uncommon` 11 of 11 as expected; `faulty` 11 of 11 cleanly rejected |
| FLAC encode | output decoded by `flac -d` and by this library | bit-exact; file size within 0.4% of `flac` at levels 0 to 8 |
| MP3 decode | ISO/IEC 11172-4 compliance streams against their reference PCM | 11 of 11 within the full-accuracy limits (RMS below 2^-15/sqrt(12), max abs at most 2^-14 of full scale) at 16-bit rounding; agrees with `mpg123` 1.33.7 to 1.2e-6 on 113 LAME/ffmpeg-encoded files, except where mpg123 itself departs from the reference (`he_mode`) |
| Loudness | EBU Tech 3341 cases 1 to 23 and Tech 3342 cases 1 to 6 | all within the stated tolerances |
| Tests | full Release suite | linux-x64 1417 passed, 0 failed; linux-arm64 under QEMU 0 failed; osx-arm64 (Apple M-series) 1187 passed, 0 failed; NativeAOT smoke app clean on all three |

Known gaps: the 8 kHz MPEG-2.5 mixed-block case has no reference stream; the Opus decoder's CELT packet-loss concealment follows its own rules rather than the reference's (conformance vectors do not exercise it).

### Fuzzing

SharpFuzz targets under libFuzzer (`tools/fuzz.sh`), one per parser, seeded from the test data and the fetched corpora; one worker per target, 660 s each, all six in parallel on one machine (AMD Ryzen 9 7950X3D, 32 threads, Linux 7.0, .NET 10.0.400).

| Target | Executions | Final corpus | Features | Crashes / hangs / OOM |
|---|---|---|---|---|
| wav | 25,721,301 | 158 files, 790 KB | 977 | 0 |
| ogg | 704,605 | 458 files, 11.7 MB | 2,598 | 0 |
| vorbis | 156,272 | 749 files, 28 MB | 10,503 | 0 |
| opus | 316,872 | 1,600 files, 2.5 MB | 24,182 | 0 |
| flac | 859,775 | 1,141 files, 42 MB | 8,623 | 0 |
| mp3 | 74,985 | 1,042 files, 26 MB | 8,516 | 0 |

(Features are libFuzzer's `ft:` counter. Corpus sizes are libFuzzer's end-of-run figures, rounded.)

### Benchmarks

BenchmarkDotNet tables for decode (against NVorbis, Concentus, NLayer, PureFlac, BunLabs.NAudio.Flac and the native `opus_demo`, `mpg123` and `flac`), resampling (against NAudio's WDL resampler), the mixer, effects, per-bus meters, loudness (against R128Net) and encoders (against `flac` and the RFC `opus_demo`) are **not yet published here**: they must run on a quiet machine and that run is pending. The classes are in `bench/OneAudio.Benchmarks`; run one with `dotnet run -c Release --project bench/OneAudio.Benchmarks -- --filter '*Name*'`. When the tables land they will state CPU, core count, load before, during and after each run, OS, .NET and tool versions, and every row where an incumbent wins.
