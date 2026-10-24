using BenchmarkDotNet.Attributes;
using NAudio.Wave;
using NAudio.Wave.SampleProviders;

namespace OneAudio.Benchmarks;

/// <summary>
/// One second of stereo audio converted per invocation (44100 frames in, 48000 out for 44.1 to 48 kHz), so
/// time / 48000 is ns per output frame. The NAudio row is NAudio's managed resampling sample provider (WdlResamplingSampleProvider), fed from memory.
/// </summary>
[MemoryDiagnoser]
public class ResampleBenchmarks
{
    private const int InFrames = 44100;
    private float[] _input = [];
    private float[] _output = [];
    private IResampler _linear = null!, _poly = null!, _fast = null!, _polyDown = null!;
    private WdlResamplingSampleProvider _naudio = null!;
    private ArraySource _naudioSource = null!;

    [GlobalSetup]
    public void Setup()
    {
        var rng = new Random(1);
        _input = new float[InFrames * 2];
        for (int i = 0; i < _input.Length; i++) _input[i] = (float)(rng.NextDouble() - 0.5);
        _output = new float[2 * 50000];
        _linear = AudioResampler.Create(44100, 48000, 2, ResamplerQuality.Linear);
        _poly = AudioResampler.Create(44100, 48000, 2, ResamplerQuality.Polyphase);
        _fast = AudioResampler.Create(44100, 48000, 2, ResamplerQuality.PolyphaseFast);
        _polyDown = AudioResampler.Create(48000, 44100, 2, ResamplerQuality.Polyphase);
        _naudioSource = new ArraySource(_input);
        _naudio = new WdlResamplingSampleProvider(_naudioSource, 48000);
    }

    private int Run(IResampler r)
    {
        r.Process(_input, _output, out _, out int w);
        return w;
    }

    [Benchmark] public int Linear44To48() => Run(_linear);
    [Benchmark] public int PolyphaseFast44To48() => Run(_fast);
    [Benchmark(Baseline = true)] public int Polyphase44To48() => Run(_poly);
    [Benchmark] public int Polyphase48To44() => Run(_polyDown);
    [Benchmark] public int NAudioWdl44To48() => _naudio.Read(_output, 0, 2 * 48000);

    /// <summary>Cost of a ratio change that forces a filter redesign (polyphase, ratio below 1).</summary>
    [Benchmark] public void PolyphaseRedesignOnSetRatio()
    {
        _polyDown.SetRatio(0.5);
        _polyDown.SetRatio(0.919);
    }

    private sealed class ArraySource(float[] data) : ISampleProvider
    {
        private int _pos;
        public WaveFormat WaveFormat { get; } = WaveFormat.CreateIeeeFloatWaveFormat(44100, 2);
        public int Read(float[] buffer, int offset, int count)
        {
            for (int i = 0; i < count; i++)
            {
                buffer[offset + i] = data[_pos];
                if (++_pos == data.Length) _pos = 0;
            }
            return count;
        }
    }
}
