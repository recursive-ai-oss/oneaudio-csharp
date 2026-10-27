using OneAudio;

// The low-level API with a WAV-only registry.
var registry = new AudioCodecRegistry().AddWav();
using var decoder = AudioDecoder.Open(Wav.Make(), registry);
var clip = AudioClip.FromDecoder(decoder);
var loudness = OneAudio.Loudness.LoudnessAnalyzer.Measure(clip.Samples.Span, clip.Format);
var mixer = new AudioMixer(new MixerOptions { SampleRate = 48_000, Channels = 2 });
mixer.Play(new PlayParams(clip));
var block = new float[512 * 2];
mixer.Render(block);
Console.WriteLine($"{clip.Frames} frames, {loudness.IntegratedLufs:F1} LUFS, first sample {block[0]}");
