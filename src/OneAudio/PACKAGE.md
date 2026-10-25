# RecursiveAI.OneAudio

Audio decode, encode, resample, mix and analyse for .NET 10, written from the published specifications with **no dependencies beyond the base class library**. It is built first for game audio: the mixer's render callback allocates nothing and takes no locks. NativeAOT- and trim-clean (nothing uses reflection; formats are registered explicitly, so unused codecs trim away).

## Formats

| | Decode | Encode |
|---|---|---|
| WAV / RF64 | PCM 8/16/24/32, float 32/64, `WAVE_FORMAT_EXTENSIBLE` | yes |
| FLAC (native and Ogg) | all depths 4-32 bit, 1-8 channels, bit-exact | levels 0-8 |
| Ogg Vorbis | yes | no |
| Ogg Opus | yes | yes |
| MP3 (MPEG-1/2/2.5 Layer III) | yes, gapless with LAME headers | no |

Every decoder streams (`Read` into a span you provide, nothing allocated per call) and seeks to the exact frame. Malformed input raises a typed `OneAudioException`; sizes read from a file are checked against `AudioLimits` before anything is allocated.

## Decode

```csharp
using OneAudio;

var registry = new AudioCodecRegistry().AddOgg().AddVorbis().AddOpus().AddFlac().AddMp3().AddWav();
using var decoder = AudioDecoder.OpenFile("music.ogg", registry);   // the container is detected from the bytes

var buffer = new float[4096 * decoder.Format.Channels];             // interleaved float in [-1, 1]
int frames;
while ((frames = decoder.Read(buffer)) > 0)
{
    // use buffer[..(frames * decoder.Format.Channels)]
}
decoder.Seek(decoder.Format.SampleRate * 30);                       // frame 30 s in
```

## Mix

```csharp
var clip = AudioClip.FromDecoder(AudioDecoder.OpenFile("hit.wav", registry));   // short effect: decode once, play many times

var mixer = new AudioMixer(new MixerOptions { SampleRate = 48000, Channels = 2 });
var voice = mixer.Play(new PlayParams(clip) { Gain = 0.8f, Pan = -0.3f });

// On the audio device thread (no allocation, no locks):
mixer.Render(deviceBuffer);                                          // interleaved float; frames = length / channels
// On any other thread:
mixer.Stop(voice, fadeOutFrames: 480);
```

Voices, buses with sub-mixes, ducking, gain, pan, pitch, loops, crossfades, sample-accurate start, a voice cap with priorities, low-pass/high-pass filters, a reverb and a limiter, and streaming sources for music are included. Control calls reach the render thread through a preallocated lock-free queue.

## Encode

```csharp
using var input = AudioDecoder.OpenFile("take.wav", registry);
using var output = File.Create("take.flac");
FlacEncoder.Encode(input, output, new FlacEncoderSettings { CompressionLevel = 5 });   // decodes back bit-exact
```

`WavEncoder` works the same way. `OpusFileWriter` writes an Ogg Opus file from float PCM and resamples input that is not 48 kHz.

## Loudness

```csharp
using var decoder = AudioDecoder.OpenFile("mix.flac", registry);
LoudnessResult r = LoudnessAnalyzer.Measure(decoder);                // ITU-R BS.1770-5 / EBU R 128
Console.WriteLine($"{r.IntegratedLufs:F1} LUFS, true peak {r.TruePeakDbtp:F1} dBTP");
float gain = (float)r.GainFor(targetLufs: -23).Linear;               // cut back if the true peak would pass -1 dBTP
```

`LoudnessMeter` gives momentary, short-term and integrated loudness, loudness range and true peak on a live stream without allocating.

## Resample

`AudioResampler.Create(inRate, outRate, channels, quality)` returns a polyphase (144 or 44 taps) or linear resampler for interleaved float; output does not depend on how the input is split into blocks.

## Changes

What changed in each release is in [CHANGELOG.md](https://github.com/recursive-ai-oss/oneaudio-csharp/blob/main/CHANGELOG.md).

## More

- API overview: <https://github.com/recursive-ai-oss/oneaudio-csharp/blob/main/docs/api.md>
- Source and issues: <https://github.com/recursive-ai-oss/oneaudio-csharp>
- Licence: MIT OR Apache-2.0, © Recursive AI, LLC.
