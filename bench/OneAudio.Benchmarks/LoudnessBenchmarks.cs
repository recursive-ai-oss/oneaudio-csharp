using BenchmarkDotNet.Attributes;
using OneAudio.Loudness;

namespace OneAudio.Benchmarks;

/// <summary>
/// One second of stereo 48 kHz noise per invocation, fed in 480-frame blocks, so time / 48000 is ns per frame.
/// The R128Net rows are the R128Net 1.1.2 package run as a compiled black box with the same input and block size; its mode set
/// is chosen to match the row it sits beside (it has no Tech 3341 test-set results in its documentation, only agreement with a C reference).
/// </summary>
[MemoryDiagnoser]
public class LoudnessBenchmarks : IDisposable
{
    private const int Rate = 48000;
    private float[] _input = [];
    private LoudnessMeter _withPeak = null!, _noPeak = null!, _momentaryOnly = null!, _surround = null!;
    private float[] _surroundInput = [];
    private R128Net.LoudnessMeter _r128All = null!, _r128NoPeak = null!;

    [GlobalSetup]
    public void Setup()
    {
        var rng = new Random(1);
        _input = new float[Rate * 2];
        for (int i = 0; i < _input.Length; i++) _input[i] = (float)(rng.NextDouble() - 0.5) * 0.4f;
        _surroundInput = new float[Rate * 6];
        for (int i = 0; i < _surroundInput.Length; i++) _surroundInput[i] = (float)(rng.NextDouble() - 0.5) * 0.4f;
        _withPeak = new LoudnessMeter(Rate, 2);
        _noPeak = new LoudnessMeter(Rate, 2, truePeak: false);
        _momentaryOnly = new LoudnessMeter(Rate, 2, truePeak: false, loudnessRange: false);
        _surround = new LoudnessMeter(Rate, 6);
        _r128All = new R128Net.LoudnessMeter(2, Rate, R128Net.LoudnessModes.All);
        _r128NoPeak = new R128Net.LoudnessMeter(2, Rate, R128Net.LoudnessModes.Momentary | R128Net.LoudnessModes.ShortTerm | R128Net.LoudnessModes.Integrated | R128Net.LoudnessModes.LoudnessRange);
    }

    private static void Feed(R128Net.LoudnessMeter m, float[] x)
    {
        int block = 480 * 2;
        for (int i = 0; i < x.Length; i += block) m.AddFrames(x.AsSpan(i, block));
    }

    private static void Feed(LoudnessMeter m, float[] x, int channels)
    {
        int block = 480 * channels;
        for (int i = 0; i < x.Length; i += block) m.Process(x.AsSpan(i, block));
    }

    public void Dispose()
    {
        _r128All?.Dispose();
        _r128NoPeak?.Dispose();
        GC.SuppressFinalize(this);
    }

    [Benchmark(Baseline = true)] public void StereoTruePeakOn() => Feed(_withPeak, _input, 2);
    [Benchmark] public void StereoTruePeakOff() => Feed(_noPeak, _input, 2);
    [Benchmark] public void StereoTruePeakOffNoRange() => Feed(_momentaryOnly, _input, 2);
    [Benchmark] public void R128NetAllModes() => Feed(_r128All, _input);
    [Benchmark] public void R128NetNoPeak() => Feed(_r128NoPeak, _input);
    [Benchmark] public void Surround6TruePeakOn() => Feed(_surround, _surroundInput, 6);
}
