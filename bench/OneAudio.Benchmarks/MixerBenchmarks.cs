using BenchmarkDotNet.Attributes;

namespace OneAudio.Benchmarks;

/// <summary>
/// Render cost of the mixer: 1024 frames of 48 kHz stereo output with N looping clip voices spread over four buses, panned.
/// Divide the mean by <c>1024 * Voices</c> for nanoseconds per frame per voice. Allocated must read 0.
/// </summary>
[MemoryDiagnoser]
public class MixerRenderBenchmark
{
    private const int Frames = 1024;
    private AudioMixer _mixer = null!;
    private readonly float[] _out = new float[Frames * 2];

    [Params(32, 128, 256)]
    public int Voices { get; set; }

    [Params(1, 2)]
    public int SourceChannels { get; set; }

    [GlobalSetup]
    public void Setup()
    {
        _mixer = new AudioMixer(new MixerOptions { SampleRate = 48000, Channels = 2, MaxVoices = 256, MaxBuses = 4, MaxBlockFrames = Frames });
        var rng = new Random(7);
        var samples = new float[48000 * SourceChannels];
        for (int i = 0; i < samples.Length; i++) samples[i] = (float)(rng.NextDouble() * 2 - 1) * 0.01f;
        var clip = new AudioClip(new AudioFormat(48000, SourceChannels), samples);
        for (int i = 0; i < Voices; i++)
            _mixer.Play(new PlayParams(clip) { Loop = true, Pan = (i % 9 - 4) / 4f, Bus = i % 4, Gain = 0.5f });
        _mixer.Render(_out); // applies the play commands
    }

    /// <summary>One render call of 1024 frames.</summary>
    [Benchmark]
    public float Render()
    {
        _mixer.Render(_out);
        return _out[0];
    }
}
