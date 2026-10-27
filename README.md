# oneaudio-csharp

Audio decode, encode, resample, mix and analyse for .NET 10, written from the specifications with **no dependencies beyond the base class library**. Built first for game audio: the mixer's render callback allocates nothing and takes no locks. NativeAOT- and trim-clean.

Package id `RecursiveAI.OneAudio`, namespace `OneAudio`. License: MIT OR Apache-2.0 (see `LICENSE-MIT`, `LICENSE-APACHE`). Install with `dotnet add package RecursiveAI.OneAudio`.

## What works today

- **One-call layer** (`Audio`): `Load`, `Open`, `mixer.Play(path)` / `PlayStream`, `Transcode`, `Resample`, `Measure`, `Normalize`, with every codec registered by default.
- Format-detecting open (`AudioDecoder.Open`) with explicit registration of containers and codecs (`AddWav`, `AddOgg`, `AddVorbis`, `AddOpus`, `AddFlac`, `AddMp3`), so unused codecs trim away.
- **Decode:** WAV / RF64 (PCM 8/16/24/32-bit, IEEE float 32/64, `WAVE_FORMAT_EXTENSIBLE`), Ogg (chained streams, seeking), Vorbis, Opus, FLAC (native and in Ogg, bit-exact integer reads, STREAMINFO MD5 check) and MP3 (MPEG-1/2/2.5 Layer III, ID3v2, Xing/LAME gapless). Streaming into caller-provided spans, sample-accurate seeking everywhere.
- **Encode:** WAV / RF64 (`WavEncoder`), FLAC levels 0 to 8 (`FlacEncoder`) and Opus (`OpusEncoder`, `OpusMultistreamEncoder`, Ogg Opus writer `OpusFileWriter`). Encoders produce identical bytes for identical input on a platform.
- **Resampling:** `Polyphase` (Kaiser-windowed sinc), `PolyphaseFast` and `Linear`, independent of how the input is split into blocks.
- **The mixer** (`AudioMixer`): voices, a bus graph with sub-mixes, ducking, multichannel mapping, per-voice pitch, gain, pan, loops, fades, crossfade, voice cap with priority, streaming sources with a decode-ahead pump and underrun accounting, and low-pass / high-pass filters, a reverb and a lookahead limiter. The render callback allocates nothing and takes no locks.
- **Loudness** (`LoudnessMeter`): BS.1770-5 / EBU R 128 momentary, short-term, integrated, loudness range and true peak, any sample rate, allocation-free; a normalisation gain helper.
- Not included: device output, spatial audio, MIDI, AAC, MP3 Layers I and II, MP3 and Vorbis encoding.

## Quick look

```csharp
using OneAudio;

var clip = Audio.Load("hit.wav");                          // any supported format, decoded to float (WAV, Ogg, Vorbis, Opus, FLAC, MP3)
var mixer = Audio.CreateMixer();                           // 48 kHz stereo, 64 voices, limiter on the master
mixer.Play("hit.wav", new PlayOptions { Gain = 0.8f, Pan = -0.3f });   // short effect: loaded and played
using var music = mixer.PlayStream("music.ogg", loop: true);           // long sound: decoded ahead on a background thread

// On the audio device thread (no allocation, no locks):   mixer.Render(deviceBuffer);

Audio.Transcode("take.wav", "take.flac");                  // format from the extension: .wav, .flac, .opus
var loudness = Audio.Measure("take.flac");                 // loudness.IntegratedLufs, .LoudnessRange, .TruePeakDbtp
Audio.Normalize("take.wav", "take-14.wav");                // -14 LUFS, true peak at most -1 dBTP
```

`Audio` is the one-call layer; it is built on the low-level API below and returns the same types. Every default is tunable through an options record (`LoadOptions`, `PlayOptions`, `TranscodeOptions`, `NormalizeOptions`).

### Low-level access

```csharp
var registry = new AudioCodecRegistry().AddWav();          // opt in to formats; nothing is found by reflection
using var decoder = AudioDecoder.OpenFile("hit.wav", registry);
var clip = AudioClip.FromDecoder(decoder);

var mixer = new AudioMixer(new MixerOptions { SampleRate = 48000, Channels = 2 });
var voice = mixer.Play(new PlayParams(clip) { Gain = 0.8f, Pan = -0.3f });
mixer.Render(deviceBuffer);                                // interleaved float, frames = length / channels
```

### Trimming and NativeAOT

The one-call layer uses `Audio.Kit`, a registry with **every** codec, so an app that calls `Audio.Load`, `mixer.Play(path)` and friends carries all of them. To keep unused codecs out of a published app, use an `AudioKit` over your own registry (same methods, same defaults) or the low-level API:

```csharp
var kit = new AudioKit(new AudioCodecRegistry().AddWav().AddVorbis());
var clip = kit.Load("hit.wav");
kit.Play(mixer, "hit.wav");
```

`Audio.Resample`, `Audio.Measure(clip)`, `Audio.Normalize(samples, ...)` and `Audio.CreateMixer` never touch the default registry, so the codecs trim away around them. NativeAOT size of the tiny app in `tests/OneAudio.AotSize` (linux-x64, .NET 10, Release, zero trim or AOT warnings; WAV in, resample to 44.1 kHz, measure, play one voice):

| Variant | Size |
|---|---|
| `Audio.Load` (default registry, every decoder) | 1 963 464 bytes |
| `AudioKit` over a WAV-only registry | 1 448 920 bytes |
| low-level API with a WAV-only registry | 1 436 504 bytes |

The encoders are linked only by the apps that call `Transcode` or `Normalize` to a file.

The API is described in [docs/api.md](docs/api.md).

## Layout

| Path | Contents |
|---|---|
| `src/OneAudio/` | the library; `Kernels/` is the only place unsafe code may live |
| `tests/OneAudio.Tests/` | xUnit v3 tests |
| `tests/OneAudio.AotSmoke/` | NativeAOT console app: open WAV, decode, mix |
| `tests/OneAudio.AotSize/` | three tiny NativeAOT apps for comparing published size (`-p:Variant=` default, kit or wav) |
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

BenchmarkDotNet 0.15.8, default job, `[MemoryDiagnoser]`, Release. The ratio column is relative to this library (below 1.00 means the other row is faster, above 1.00 means this library is faster). "x real" is audio seconds divided by mean time, so higher is faster. Native rows (`flac`, `mpg123`, the RFC 6716 `opus_demo`) are child processes: they include process start and file I/O, which this library's in-process rows do not.

**Method.** One worker, run in a single session on 2026-10-25 (01:03 to 01:49 local), one benchmark class at a time. AMD Ryzen 9 7950X3D (16 cores, 32 threads), Linux Mint 22.3, kernel 7.0, CPU governor `powersave`, .NET SDK 10.0.402 with runtime 10.0.13 (X64 RyuJIT, AVX-512 available). Load average (1 min) was 1.52 before, between 0.99 and 3.45 during (median 1.09; the benchmark itself is one busy thread, the rest is desktop background) and 1.14 after; no other heavy job ran on the machine during the session. Incumbents: NVorbis 0.10.5, Concentus 2.2.2, NLayer 1.16.0, PureFlac 1.0.0, BunLabs.NAudio.Flac 2.0.1, NAudio.Core 2.2.1 (WDL resampler), R128Net 1.1.2; native `flac` 1.4.3, `mpg123` 1.33.7, and the RFC 6716 reference `opus_demo` (with the RFC 8251 changes). The raw BenchmarkDotNet output is not committed.

#### Decode

| Format | Input | This library | Incumbent / native | Ratio | Allocated (ours / theirs) |
|---|---|---|---|---|---|
| WAV 16-bit stereo 48 kHz | 60 s | 3.48 ms, 17,300x real | - | - | 8 KB |
| Vorbis | 1.0.1-test, 186 s stereo | 196 ms, 947x | NVorbis 240 ms, 776x | 1.22 | 686 KB / 77.9 MB |
| Vorbis | 48k-mono, 10.7 s | 5.21 ms, 2,061x | NVorbis 6.51 ms, 1,647x | 1.25 | 497 KB / 2.8 MB |
| Vorbis | rc3-test, 21.2 s stereo | 24.8 ms, 855x | NVorbis 29.8 ms, 711x | 1.20 | 441 KB / 9.5 MB |
| Opus (CELT) | testvector01, 29.5 s | 82.0 ms, 359x | Concentus 87.9 ms, 335x; `opus_demo` 83.2 ms, 354x | 1.07; 1.01 | 0 / 50 MB; 41 KB |
| Opus (SILK) | testvector04, 26.6 s | 29.2 ms, 913x | Concentus 35.0 ms, 761x; `opus_demo` 38.0 ms, 702x | 1.20; 1.30 | 0 / 24 MB; 41 KB |
| Opus (hybrid) | testvector05, 27.2 s | 67.3 ms, 404x | Concentus 73.3 ms, 370x; `opus_demo` 73.9 ms, 368x | 1.09; 1.10 | 1.5 KB / 52 MB; 41 KB |
| MP3 44.1 kHz stereo 128k | 30 s | 37.0 ms, 811x | NLayer 107 ms, 281x; **`mpg123` 14.2 ms, 2,115x** | 2.89; **0.38** | 132 KB / 36 MB; 41 KB |
| MP3 44.1 kHz stereo 320k | 30 s | 45.7 ms, 656x | NLayer 124 ms, 243x; **`mpg123` 18.3 ms, 1,639x** | 2.70; **0.40** | 132 KB / 35 MB; 41 KB |
| MP3 22.05 kHz mono 64k | 30 s | 11.6 ms, 2,583x | **`mpg123` 6.5 ms, 4,633x**; NLayer see note | **0.56** | 121 KB / 41 KB |
| FLAC 16-bit 44.1 kHz | 7.0 s | 4.39 ms, 1,594x | PureFlac 5.99 ms; `flac -d` 5.16 ms; NAudio.Flac 35.5 ms | 1.36; 1.17; 8.09 | 97 KB / 113 KB; 26 KB; 10 MB |
| FLAC 24-bit 96 kHz | 8.7 s | 14.9 ms, 586x | PureFlac 23.4 ms; `flac -d` 17.6 ms; NAudio.Flac 30.9 ms | 1.57; 1.19; 2.07 | 98 KB / 121 KB; 26 KB; 69 MB |
| FLAC 24-bit 192 kHz | 8.0 s | 30.7 ms, 261x | PureFlac 43.8 ms; `flac -d` 33.5 ms; NAudio.Flac 70.7 ms | 1.43; 1.09; 2.30 | 97 KB / 121 KB; 26 KB; 129 MB |

What this says:
- **Wins:** faster than every managed incumbent except NLayer on one MP3 file (note below), with allocations two to four orders of magnitude lower (decode allocates a fixed few hundred KB, independent of length; the Opus decoder allocates nothing per packet). Faster than `flac -d` and the RFC `opus_demo` decoder on these files, though those two include process start.
- **Losses:** **MP3 is the clear loss.** The native `mpg123` decodes 2.5x to 2.6x faster than this library on stereo and 1.8x faster on mono. On the mono 22.05 kHz file NLayer reports 2.3 ms, but it returned only 56,064 of the 661,500 frames in the file (the other rows return the full length within 0.1%), so that row is not a like-for-like timing and is left out of the ratio. There is clear room to speed it up.
- The FLAC "verify MD5" mode, which decodes the whole file and hashes it, costs 1.4x to 1.5x a plain decode.

#### Resampling

One second of 44.1 kHz stereo to 48 kHz per call (and 48 to 44.1), 0 bytes allocated on every row.

| Resampler | Mean | x real | Ratio to Polyphase |
|---|---|---|---|
| Linear | 133 us | 7,500x | 0.11 |
| PolyphaseFast (44 taps, about 75 dB stopband) | 693 us | 1,442x | 0.58 |
| Polyphase (144 taps, at least 100 dB stopband) 44.1 to 48 | 1,194 us | 837x | 1.00 |
| Polyphase 48 to 44.1 | 1,156 us | 865x | 0.97 |
| **NAudio WDL resampler** (44.1 to 48) | **1,028 us** | 973x | **0.86** |

The WDL resampler is 14% faster than this library's default high-quality mode on this input; this library's `PolyphaseFast` is faster than WDL but with a shallower stopband. The two filters have different designs, so the rows compare speed, not quality. Two ratio changes at run time (`SetRatio` down and back, each redesigning the filter bank) cost 562 us together.

#### Mixer, effects, meters

One `Render` call of 1024 frames at 48 kHz stereo output, looping voices, 0 bytes allocated on every row.

| Voices (source) | Mean | ns per frame per voice |
|---|---|---|
| 32 mono | 6.14 us | 0.19 |
| 32 stereo | 10.5 us | 0.32 |
| 128 mono | 20.1 us | 0.15 |
| 128 stereo | 36.4 us | 0.28 |
| 256 mono | 39.6 us | 0.15 |
| 256 stereo | 71.0 us | 0.27 |

Effects on the master output (one voice plus one effect; the 744 ns baseline without effects is subtracted, then divided by 1024 frames):

| Effect | ns per frame |
|---|---|
| Low-pass | 5.7 |
| High-pass | 5.7 |
| Low-pass and high-pass | 11.4 |
| Low-pass with the cutoff gliding on every call | 6.8 |
| Limiter (5 ms lookahead) | 10.5 |
| Reverb | 54.0 |

Game-style mix (64 looping voices on 4 buses, low-pass and high-pass on each bus, one reverb, master limiter): 184.7 us without effects, 311.6 us with them (+69%), which is 0.9% and 1.5% of one core's time for a 21.3 ms block.

Per-bus loudness meters (32 voices on 4 buses, 1024 frames): 10.0 us with none enabled, 42.1 us with one, 129.1 us with four. **A meter is not free**: each enabled bus meter adds about 30 us per block, three times the cost of the rest of that mix. Enable them only on the buses being displayed.

#### Loudness

One second of 48 kHz audio per call, all measurements on (momentary, short-term, integrated, range) unless stated; 0 bytes allocated.

| Meter | Mean | x real | Ratio to ours with true peak |
|---|---|---|---|
| This library, stereo, true peak on | 1,939 us | 516x | 1.00 |
| This library, stereo, true peak off | 242 us | 4,133x | 0.12 |
| This library, 6 channels, true peak on | 5,302 us | 189x | 2.73 |
| R128Net, stereo, all modes | 475 us | 2,107x | 0.24 |
| R128Net, stereo, no true peak | 337 us | 2,967x | 0.17 |

**Loss:** with true peak on (4x oversampling), this library is 4.1x slower than R128Net with all modes on. With true peak off it is 1.4x faster than R128Net with it off. Loudness range costs nothing measurable on top of the other modes.

#### Encoders

FLAC: 413 s of 16-bit stereo 44.1 kHz music (the stereo 44.1 kHz Xiph Vorbis test vectors, decoded and rounded to 16 bit) per call, written to a discarding sink. `flac` is a child process.

| Level | This library | `flac` 1.4.3 | Ratio | Allocated (ours / flac) |
|---|---|---|---|---|
| 0 | 266 ms, 1,553x | 227 ms, 1,819x | 0.85 | 108 KB / 26 KB |
| 2 | 388 ms, 1,064x | 252 ms, 1,640x | 0.65 | 108 KB / 27 KB |
| 5 | 578 ms, 715x | 316 ms, 1,309x | 0.55 | 331 KB / 27 KB |
| 8 | 2,263 ms, 183x | 824 ms, 502x | 0.36 | 413 KB / 27 KB |

**Loss:** this library's FLAC encoder is slower than `flac` at every level, 1.2x at level 0 and 2.7x at level 8 (outputs are within 0.4% in size at these levels, and decode bit-exact).

Opus: 10 s of generated stereo music-like 48 kHz audio in 20 ms frames, Audio application, unconstrained VBR. The RFC row is `opus_demo -e` as a child process.

| Bitrate | Complexity | This library | Concentus | RFC `opus_demo` | Allocated (ours / Concentus) |
|---|---|---|---|---|---|
| 16 kbit/s | 10 | 84.5 ms | 103.9 ms (1.23) | 83.0 ms (0.98) | 0 / 47 MB |
| 16 kbit/s | 5 | 36.6 ms | 49.4 ms (1.35) | 38.6 ms (1.06) | 0 / 40 MB |
| 64 kbit/s | 10 | 56.0 ms | 60.5 ms (1.08) | 58.1 ms (1.04) | 0 / 55 MB |
| 64 kbit/s | 5 | 56.5 ms | 59.4 ms (1.05) | 57.9 ms (1.03) | 0 / 54 MB |
| 128 kbit/s | 10 | 62.2 ms | 64.7 ms (1.04) | 63.2 ms (1.02) | 0 / 56 MB |
| 128 kbit/s | 5 | 62.2 ms | 63.5 ms (1.02) | 63.0 ms (1.01) | 0 / 56 MB |

The ratio in parentheses is that row's time divided by this library's. At 64 and 128 kbit/s all three encoders do about the same work and finish within 8% of each other (this library is 100 to 180x real time); at low rates the speed depends on which coding mode each encoder picks. At 32 kbit/s (not tabled) this library chose a different mode than Concentus (SILK/hybrid configuration 27 versus 31), so that row is not like-for-like. Complexity 5 is not measurably faster than 10 at 64 and 128 kbit/s in any of the three encoders on this input.

WAV encode, 60 s of 48 kHz stereo float input: 16-bit 2.8 ms (21,100x real), 24-bit 4.8 ms, 32-bit float 2.1 ms. With TPDF dither the 16-bit and 24-bit paths cost 18.2 ms and 20.2 ms (dither is off by default).
