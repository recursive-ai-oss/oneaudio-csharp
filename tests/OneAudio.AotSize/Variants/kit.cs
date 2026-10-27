using OneAudio;

// Happy path over a WAV-only kit: no default registry, so the other codecs trim away.
var kit = new AudioKit(new AudioCodecRegistry().AddWav());
var clip = kit.Load(Wav.Make(), new LoadOptions { SampleRate = 44_100 });
var loudness = Audio.Measure(clip);
var mixer = Audio.CreateMixer();
mixer.Play(clip);
var block = new float[512 * 2];
mixer.Render(block);
Console.WriteLine($"{clip.Frames} frames, {loudness.IntegratedLufs:F1} LUFS, first sample {block[0]}");
