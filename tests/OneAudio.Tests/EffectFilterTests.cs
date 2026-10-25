using System.Runtime.Intrinsics;
using System.Runtime.Intrinsics.Arm;
using System.Runtime.Intrinsics.X86;
using OneAudio.Effects;
using OneAudio.Kernels;
using Xunit;

namespace OneAudio.Tests;

/// <summary>Filters: measured response against the analog prototype, stability under fast parameter changes, and kernel equivalence.</summary>
public class EffectFilterTests
{
    private const int Rate = 48000;

    // Amplitude of the steady-state sine at f in y, by a Hann-windowed single-bin DFT.
    internal static double Amplitude(ReadOnlySpan<float> y, double f, int rate)
    {
        double re = 0, im = 0, ws = 0;
        int n = y.Length;
        for (int i = 0; i < n; i++)
        {
            double w = 0.5 - 0.5 * Math.Cos(2 * Math.PI * i / n);
            double ph = 2 * Math.PI * f * i / rate;
            re += w * y[i] * Math.Cos(ph);
            im -= w * y[i] * Math.Sin(ph);
            ws += w;
        }
        return 2 * Math.Sqrt(re * re + im * im) / ws;
    }

    private static double Analog(bool highPass, double omega, double q)
    {
        double d = Math.Sqrt((1 - omega * omega) * (1 - omega * omega) + omega * omega / (q * q));
        return (highPass ? omega * omega : 1) / d;
    }

    private static double MeasureDb(bool highPass, double fc, double q, double f)
    {
        var stage = new FilterStage(highPass, 1, Rate);
        stage.Set(fc, q, 0);
        int n = 32768 + 16384;
        var x = new float[n];
        for (int i = 0; i < n; i++) x[i] = (float)Math.Sin(2 * Math.PI * f * i / Rate);
        stage.Process(x, n, 1, n);
        return 20 * Math.Log10(Amplitude(x.AsSpan(16384), f, Rate));
    }

    public static IEnumerable<object[]> Settings() =>
    [
        [false, 1000.0, 0.7071], [false, 200.0, 2.0], [false, 5000.0, 5.0], [false, 80.0, 0.5], [false, 8000.0, 0.7071],
        [true, 1000.0, 0.7071], [true, 200.0, 2.0], [true, 5000.0, 5.0], [true, 80.0, 0.5], [true, 150.0, 10.0],
    ];

    [Theory]
    [MemberData(nameof(Settings))]
    public void Response_MatchesAnalogPrototype(bool highPass, double fc, double q)
    {
        double worst = 0, worstWarped = 0;
        foreach (double ratio in new[] { 0.05, 0.1, 0.25, 0.5, 0.8, 1.0, 1.25, 2.0, 4.0, 8.0 })
        {
            double f = fc * ratio;
            if (f > 0.4 * Rate || f < 10) continue;
            double db = MeasureDb(highPass, fc, q, f);
            double analog = 20 * Math.Log10(Analog(highPass, ratio, q));
            // The bilinear transform maps analog frequency W to digital f by W = tan(pi f / fs) / tan(pi fc / fs); with the cutoff
            // pre-warped that makes the digital response exactly the analog one at the warped frequency.
            double warped = Math.Tan(Math.PI * f / Rate) / Math.Tan(Math.PI * fc / Rate);
            double expected = 20 * Math.Log10(Analog(highPass, warped, q));
            // Ignore points where the response is below -90 dB, which float rounding of the 32-bit signal path swamps.
            if (expected < -90) continue;
            worstWarped = Math.Max(worstWarped, Math.Abs(db - expected));
            // Against the analog curve proper, only where frequency warping is small (f below a tenth of the sample rate).
            if (f <= Rate / 10.0) worst = Math.Max(worst, Math.Abs(db - analog));
        }
        Console.WriteLine($"{(highPass ? "HP" : "LP")} fc={fc} Q={q}: max error vs warped analog {worstWarped:F4} dB; vs analog (f <= fs/10) {worst:F3} dB");
        Assert.True(worstWarped < 0.02, $"bilinear-exact error {worstWarped} dB");
        Assert.True(worst < 0.7, $"analog error {worst} dB");
    }

    [Fact]
    public void Cutoff_IsMinusThreeDb_AtButterworthQ()
    {
        foreach (bool hp in new[] { false, true })
            foreach (double fc in new[] { 100.0, 1000.0, 10000.0 })
                Assert.InRange(MeasureDb(hp, fc, 1 / Math.Sqrt(2), fc), -3.05, -2.97);
    }

    [Fact]
    public void FastSweeps_StayFiniteAndBounded()
    {
        var rng = new Random(5);
        foreach (bool hp in new[] { false, true })
            foreach (int ramp in new[] { 0, 1, 7, 64, 2000 })
            {
                var stage = new FilterStage(hp, 2, Rate);
                var buf = new float[2 * 480];
                double peak = 0;
                stage.Set(1000, 0.7, 0);
                for (int pass = 0; pass < 4000; pass++)
                {
                    // Loud noise, and a new random target (cutoff over the full range, Q up to the limit) every pass.
                    for (int i = 0; i < buf.Length; i++) buf[i] = (float)(rng.NextDouble() * 2 - 1);
                    double cutoff = Math.Exp(rng.NextDouble() * Math.Log(23000 / 5.0)) * 5.0;
                    stage.Set(cutoff, rng.NextDouble() < 0.5 ? 24 : 0.1 + rng.NextDouble() * 10, ramp);
                    stage.Process(buf, 480, 2, 480);
                    foreach (float v in buf) { Assert.True(float.IsFinite(v)); peak = Math.Max(peak, Math.Abs(v)); }
                }
                Console.WriteLine($"{(hp ? "HP" : "LP")} ramp {ramp}: peak {peak:F1} for unit noise at Q up to 24");
                Assert.True(peak < 400, $"peak {peak}");
            }
    }

    [Fact]
    public void SwitchOff_GlidesAndStopsCosting()
    {
        var stage = new FilterStage(false, 1, Rate);
        stage.Set(500, 0.7, 0);
        var buf = new float[480];
        stage.Process(buf, 480, 1, 480);
        stage.Clear(240);
        Assert.True(stage.Active);
        Array.Fill(buf, 0.5f);
        stage.Process(buf, 480, 1, 480);
        Assert.False(stage.Active);
        // After the glide the signal is the unfiltered DC again.
        Assert.Equal(0.5f, buf[479], 1e-3f);
    }

    // ------------------------------------------------------------------ kernels

    private static readonly int[] Lengths = [1, 7, 8, 15, 16, 17, 31, 33, 100];
    private static readonly int[] ChannelCounts = [1, 2, 3, 4, 5, 7, 8, 9, 12, 16, 17];

    private static void PrintPaths(string name) =>
        Console.WriteLine($"{name}: Vector128 {Vector128.IsHardwareAccelerated}, Vector256 {Vector256.IsHardwareAccelerated}, Vector512 {Vector512.IsHardwareAccelerated}, " +
                          $"Avx2 {Avx2.IsSupported}, Avx512F {Avx512F.IsSupported}, AdvSimd {AdvSimd.IsSupported}");

    [Fact]
    public void Svf_VectorMatchesScalar_Exactly()
    {
        PrintPaths("svf");
        var rng = new Random(11);
        foreach (int ch in ChannelCounts)
            foreach (int frames in Lengths)
                foreach (bool hp in new[] { false, true })
                    foreach (int start in new[] { 0, 3 })
                    {
                        int stride = start + frames + 5;
                        var a = new float[ch * stride];
                        for (int i = 0; i < a.Length; i++) a[i] = (float)(rng.NextDouble() * 2 - 1);
                        var b = (float[])a.Clone();
                        var s1a = new float[ch]; var s2a = new float[ch];
                        for (int i = 0; i < ch; i++) { s1a[i] = (float)rng.NextDouble(); s2a[i] = (float)rng.NextDouble(); }
                        var s1b = (float[])s1a.Clone(); var s2b = (float[])s2a.Clone();
                        var c = new SvfCoefficients(rng.NextDouble() * 10000 + 20, 0.3 + rng.NextDouble() * 8, Rate);
                        // Two consecutive calls, so state carry-over is covered.
                        for (int rep = 0; rep < 2; rep++)
                        {
                            EffectKernels.Svf(a, stride, ch, start, frames, c, s1a, s2a, hp);
                            EffectKernels.SvfScalar(b, stride, ch, start, frames, c, s1b, s2b, hp);
                        }
                        Assert.True(a.AsSpan().SequenceEqual(b), $"data differs: ch {ch}, frames {frames}, start {start}, hp {hp}");
                        Assert.True(s1a.AsSpan().SequenceEqual(s1b) && s2a.AsSpan().SequenceEqual(s2b), "state differs");
                    }
    }

    [Fact]
    public void LimiterKernels_VectorMatchScalar_Exactly()
    {
        PrintPaths("limiter kernels");
        var rng = new Random(12);
        foreach (int frames in new[] { 1, 7, 8, 15, 16, 17, 31, 33, 100, 1000 })
            foreach (int ch in new[] { 1, 2, 5, 8 })
            {
                var data = new float[ch * (frames + 3)];
                for (int i = 0; i < data.Length; i++) data[i] = (float)(rng.NextDouble() * 4 - 2);
                var p1 = new float[frames]; var p2 = new float[frames];
                EffectKernels.PeakAcross(data, frames + 3, ch, frames, p1);
                EffectKernels.PeakAcrossScalar(data, frames + 3, ch, frames, p2);
                Assert.True(p1.AsSpan().SequenceEqual(p2));

                // Unaligned offsets: slice at 1.
                var blockA = new float[frames + 1]; var ringA = new float[frames + 1]; var gain = new float[frames + 1];
                for (int i = 0; i < blockA.Length; i++) { blockA[i] = (float)(rng.NextDouble() * 4 - 2); ringA[i] = (float)(rng.NextDouble() * 4 - 2); gain[i] = (float)rng.NextDouble(); }
                var blockB = (float[])blockA.Clone(); var ringB = (float[])ringA.Clone();
                EffectKernels.SwapScaleClamp(blockA.AsSpan(1), ringA.AsSpan(1), gain.AsSpan(1), 0.9f);
                EffectKernels.SwapScaleClampScalar(blockB.AsSpan(1), ringB.AsSpan(1), gain.AsSpan(1), 0.9f);
                Assert.True(blockA.AsSpan().SequenceEqual(blockB) && ringA.AsSpan().SequenceEqual(ringB));
            }
    }
}
