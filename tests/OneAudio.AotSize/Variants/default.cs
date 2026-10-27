using OneAudio;

// Happy path with the default registry: every codec is linked.
var clip = Audio.Load(Wav.Make(), new LoadOptions { SampleRate = 44_100 });
var loudness = Audio.Measure(clip);
var mixer = Audio.CreateMixer();
mixer.Play(clip);
var block = new float[512 * 2];
mixer.Render(block);
Console.WriteLine($"{clip.Frames} frames, {loudness.IntegratedLufs:F1} LUFS, first sample {block[0]}");
