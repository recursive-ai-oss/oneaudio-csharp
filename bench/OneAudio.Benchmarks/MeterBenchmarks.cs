using BenchmarkDotNet.Attributes;

namespace OneAudio.Benchmarks;

/// <summary>
/// Render cost of per-bus loudness metering: 1024 frames of 48 kHz stereo output with 32 looping voices on four buses and
/// 0, 1 or 4 bus meters enabled (<see cref="AudioMixer.EnableBusMeter"/>). Subtract the 0-meter row from the others for the cost of
/// the meters; allocated must read 0.
/// </summary>
[MemoryDiagnoser]
public class MeterBenchmark
{
    private const int Frames = 1024;
    private AudioMixer _mixer = null!;
    private readonly float[] _out = new float[Frames * 2];

    /// <summary>Number of buses with a meter enabled.</summary>
    [Params(0, 1, 4)]
    public int Meters { get; set; }

    [GlobalSetup]
    public void Setup()
    {
        _mixer = new AudioMixer(new MixerOptions { SampleRate = 48000, Channels = 2, MaxVoices = 32, MaxBuses = 4, MaxBlockFrames = Frames, MaxBusMeters = 4 });
        var rng = new Random(7);
        var samples = new float[48000 * 2];
        for (int i = 0; i < samples.Length; i++) samples[i] = (float)(rng.NextDouble() * 2 - 1) * 0.05f;
        var clip = new AudioClip(new AudioFormat(48000, 2), samples);
        for (int i = 0; i < 32; i++)
            _mixer.Play(new PlayParams(clip) { Loop = true, Pan = (i % 9 - 4) / 4f, Bus = i % 4, Gain = 0.5f });
        for (int b = 0; b < Meters; b++) _mixer.EnableBusMeter(b);
        _mixer.Render(_out); // applies the commands
    }

    /// <summary>One render call of 1024 frames.</summary>
    [Benchmark]
    public float Render()
    {
        _mixer.Render(_out);
        return _out[0];
    }
}
