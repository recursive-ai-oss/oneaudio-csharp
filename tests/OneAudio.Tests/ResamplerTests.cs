using OneAudio.Kernels;
using System.Runtime.Intrinsics;
using Xunit;

// Allocation tests assert process-wide GC counts, which any test running beside them would disturb.
[assembly: CollectionBehavior(CollectionBehavior.CollectionPerAssembly)]

namespace OneAudio.Tests;

/// <summary>Measurement helpers shared by the resampler tests.</summary>
internal static class Dsp
{
    public static float[] Tone(double freq, double rate, int n, double amp = 0.9, double phase = 0.3)
    {
        var x = new float[n];
        for (int i = 0; i < n; i++) x[i] = (float)(amp * Math.Sin(2 * Math.PI * freq * i / rate + phase));
        return x;
    }

    /// <summary>Converts a whole signal, feeding it in blocks of the given sizes (cycled) and then flushing.</summary>
    public static float[] Convert(IResampler r, float[] input, int[] blocks)
    {
        int c = r.Channels;
        var outp = new List<float>();
        var tmp = new float[8192 * c];
        int pos = 0, bi = 0;
        int totalFrames = input.Length / c;
        while (pos < totalFrames)
        {
            int n = Math.Min(blocks[bi++ % blocks.Length], totalFrames - pos);
            int fed = 0;
            while (fed < n)
            {
                r.Process(input.AsSpan((pos + fed) * c, (n - fed) * c), tmp, out int used, out int written);
                outp.AddRange(tmp.AsSpan(0, written * c).ToArray());
                fed += used;
            }
            pos += n;
        }
        int w;
        do
        {
            w = r.Flush(tmp);
            outp.AddRange(tmp.AsSpan(0, w * c).ToArray());
        } while (w > 0);
        return outp.ToArray();
    }

    public static float[] Convert(IResampler r, float[] input) => Convert(r, input, [4096]);

    /// <summary>Amplitude of the component at <paramref name="freq"/>, using a Kaiser(beta 14) window (sidelobes below -130 dB) so strong tones elsewhere do not leak in.</summary>
    public static double Level(float[] x, double rate, double freq, int skip)
    {
        int n = x.Length - 2 * skip;
        double re = 0, im = 0, wsum = 0, i0b = I0(14);
        double w0 = 2 * Math.PI * freq / rate;
        for (int i = 0; i < n; i++)
        {
            double r = 2.0 * i / (n - 1) - 1;
            double w = I0(14 * Math.Sqrt(Math.Max(0, 1 - r * r))) / i0b;
            double v = x[skip + i] * w;
            re += v * Math.Cos(w0 * (skip + i));
            im -= v * Math.Sin(w0 * (skip + i));
            wsum += w;
        }
        return 2 * Math.Sqrt(re * re + im * im) / wsum;
    }

    public static double Db(double ratio) => 20 * Math.Log10(Math.Max(ratio, 1e-30));

    /// <summary>Residual RMS after least-squares removal of a sine at <paramref name="freq"/>, relative to the sine's RMS, in dB.</summary>
    public static double ThdPlusN(float[] x, double rate, double freq, int skip)
    {
        int n = x.Length - 2 * skip;
        double ss = 0, cc = 0, sc = 0, xs = 0, xc = 0;
        double w0 = 2 * Math.PI * freq / rate;
        for (int i = 0; i < n; i++)
        {
            double s = Math.Sin(w0 * (skip + i)), c = Math.Cos(w0 * (skip + i)), v = x[skip + i];
            ss += s * s; cc += c * c; sc += s * c; xs += v * s; xc += v * c;
        }
        double det = ss * cc - sc * sc;
        double a = (xs * cc - xc * sc) / det, b = (xc * ss - xs * sc) / det;
        double res = 0, sig = 0;
        for (int i = 0; i < n; i++)
        {
            double fit = a * Math.Sin(w0 * (skip + i)) + b * Math.Cos(w0 * (skip + i));
            double e = x[skip + i] - fit;
            res += e * e; sig += fit * fit;
        }
        return Db(Math.Sqrt(res / sig));
    }

    /// <summary>Folds a frequency into 0..rate/2 as sampling at <paramref name="rate"/> would.</summary>
    public static double Fold(double f, double rate)
    {
        f %= rate;
        return f > rate / 2 ? rate - f : f;
    }

    private static double I0(double x)
    {
        double sum = 1, term = 1;
        for (int k = 1; k < 100; k++) { term *= x * x / (4.0 * k * k); sum += term; if (term < 1e-17 * sum) break; }
        return sum;
    }
}

public class ResamplerKernelTests
{
    private static readonly int[] Lengths = [1, 7, 8, 15, 16, 17, 31, 32, 33, 63, 64, 65, 144, 288, 576];

    [Fact]
    public void Report_HardwarePaths()
    {
        string s = $"Vector128={Vector128.IsHardwareAccelerated} Vector256={Vector256.IsHardwareAccelerated} Vector512={Vector512.IsHardwareAccelerated} " +
                   $"Avx2={System.Runtime.Intrinsics.X86.Avx2.IsSupported} Avx512F={System.Runtime.Intrinsics.X86.Avx512F.IsSupported} " +
                   $"AdvSimd={System.Runtime.Intrinsics.Arm.AdvSimd.IsSupported}";
        Console.WriteLine("RESAMPLE-KERNEL-PATHS " + s);
    }

    [Fact]
    public void DotBlend_AllPaths_MatchDoubleOracle_AtEdgeSizesAndOffsets()
    {
        var rng = new Random(7);
        foreach (int n in Lengths)
            for (int offset = 0; offset < 4; offset++)
            {
                var xs = new float[n + 8]; var aa = new float[n + 8]; var bb = new float[n + 8];
                for (int i = 0; i < xs.Length; i++)
                {
                    xs[i] = (float)(rng.NextDouble() * 2 - 1);
                    aa[i] = (float)(rng.NextDouble() * 2 - 1);
                    bb[i] = (float)(rng.NextDouble() * 2 - 1);
                }
                var x = xs.AsSpan(offset, n); var a = aa.AsSpan(offset + 1, n); var b = bb.AsSpan(offset + 2, n);
                float w = (float)rng.NextDouble();
                double oa = 0, ob = 0, mag = 0;
                for (int i = 0; i < n; i++) { oa += (double)x[i] * a[i]; ob += (double)x[i] * b[i]; mag += Math.Abs((double)x[i] * a[i]) + Math.Abs((double)x[i] * b[i]); }
                double oracle = oa + w * (ob - oa);
                // Float summation error bound: a few ulp of the sum of absolute products.
                double tol = 4e-7 * mag + 1e-9;

                void Check(string name, float got) => Assert.True(Math.Abs(got - oracle) <= tol, $"{name} n={n} off={offset}: {got} vs {oracle}");
                Check("scalar", ResampleKernels.DotBlendScalar(x, a, b, w));
                Check("dispatch", ResampleKernels.DotBlend(x, a, b, w));
                if (Vector128.IsHardwareAccelerated) Check("v128", ResampleKernels.DotBlendV128(x, a, b, w));
                if (Vector256.IsHardwareAccelerated) Check("v256", ResampleKernels.DotBlendV256(x, a, b, w));
                if (Vector512.IsHardwareAccelerated) Check("v512", ResampleKernels.DotBlendV512(x, a, b, w));
            }
    }

    [Fact]
    public void DotBlend_RejectsMismatchedLengths()
    {
        Assert.Throws<ArgumentException>(() => ResampleKernels.DotBlend(new float[4], new float[4], new float[3], 0.5f));
    }
}

public class ResamplerQualityTests
{
    // (input rate, output rate)
    public static TheoryData<int, int> Cases => new()
    {
        { 44100, 48000 },
        { 48000, 44100 },
        { 48000, 36923 },   // arbitrary: ratio 0.7692
        { 22050, 37485 },   // arbitrary: ratio 1.7
    };

    private static IResampler Make(int inRate, int outRate, ResamplerQuality q) => AudioResampler.Create(inRate, outRate, 1, q);

    [Theory, MemberData(nameof(Cases))]
    public void Polyphase_PassbandFlat_Within005dB(int inRate, int outRate)
    {
        double lowNyq = Math.Min(inRate, outRate) / 2.0;
        double worst = 0;
        for (int k = 1; k <= 12; k++)
        {
            double f = (k == 1 ? 100 : 0.9 * lowNyq * (k - 1) / 11);
            var y = Dsp.Convert(Make(inRate, outRate, ResamplerQuality.Polyphase), Dsp.Tone(f, inRate, inRate / 2, 0.9), [1000]);
            double err = Math.Abs(Dsp.Db(Dsp.Level(y, outRate, f, 600) / 0.9));
            worst = Math.Max(worst, err);
        }
        Console.WriteLine($"FIGURE passband {inRate}->{outRate}: worst deviation {worst:F5} dB up to 0.9 x {lowNyq} Hz");
        Assert.True(worst <= 0.05, $"deviation {worst} dB");
    }

    [Theory, MemberData(nameof(Cases))]
    public void Polyphase_Stopband_Rejects100dB(int inRate, int outRate)
    {
        double outNyq = outRate / 2.0, inNyq = inRate / 2.0;
        double worst = -300;
        foreach (double frac in new[] { 0.2, 0.5, 0.8, 0.9, 0.97 })
        {
            double f = frac * inNyq;
            var y = Dsp.Convert(Make(inRate, outRate, ResamplerQuality.Polyphase), Dsp.Tone(f, inRate, inRate / 2, 0.9), [777]);
            // Downsampling: the tone is above the output Nyquist frequency and folds back. Upsampling: the first spectral
            // image (inRate - f) lies above the input Nyquist frequency and folds into the output band.
            double u = f > outNyq ? Dsp.Fold(f, outRate) : Dsp.Fold(inRate - f, outRate);
            if (f <= outNyq && outRate <= inRate) continue;
            if (Math.Abs(u - f) < 50) continue;
            worst = Math.Max(worst, Dsp.Db(Dsp.Level(y, outRate, u, 700) / 0.9));
        }
        Console.WriteLine($"FIGURE stopband {inRate}->{outRate}: strongest alias/image {worst:F1} dB re tone");
        Assert.True(worst <= -100, $"worst {worst} dB");
    }

    [Theory, MemberData(nameof(Cases))]
    public void Polyphase_ThdPlusN_1kHz_Below_Minus100dB(int inRate, int outRate)
    {
        var y = Dsp.Convert(Make(inRate, outRate, ResamplerQuality.Polyphase), Dsp.Tone(1000, inRate, inRate, 0.5), [4096]);
        double thd = Dsp.ThdPlusN(y, outRate, 1000, 400);
        Console.WriteLine($"FIGURE thd+n {inRate}->{outRate}: {thd:F1} dB");
        Assert.True(thd <= -100, $"THD+N {thd} dB");
    }

    [Theory, MemberData(nameof(Cases))]
    public void PolyphaseFast_Figures(int inRate, int outRate)
    {
        double lowNyq = Math.Min(inRate, outRate) / 2.0, outNyq = outRate / 2.0, inNyq = inRate / 2.0;
        double worstPass = 0;
        for (int k = 1; k <= 8; k++)
        {
            double f = 0.8 * lowNyq * k / 8;
            var y = Dsp.Convert(Make(inRate, outRate, ResamplerQuality.PolyphaseFast), Dsp.Tone(f, inRate, inRate / 2, 0.9), [1000]);
            worstPass = Math.Max(worstPass, Math.Abs(Dsp.Db(Dsp.Level(y, outRate, f, 300) / 0.9)));
        }
        double worstStop = -300;
        foreach (double frac in new[] { 0.5, 0.8, 0.9, 0.97 })
        {
            double f = frac * inNyq;
            var y = Dsp.Convert(Make(inRate, outRate, ResamplerQuality.PolyphaseFast), Dsp.Tone(f, inRate, inRate / 2, 0.9), [1000]);
            double u, source;
            if (f > outNyq) { u = Dsp.Fold(f, outRate); source = f; } else { u = Dsp.Fold(inRate - f, outRate); source = inRate - f; }
            if (outRate > inRate || f > outNyq) { }
            else continue;
            if (source < Math.Min(inNyq, outNyq) * 1.0) continue;
            worstStop = Math.Max(worstStop, Dsp.Db(Dsp.Level(y, outRate, u, 300) / 0.9));
        }
        var t = Dsp.Convert(Make(inRate, outRate, ResamplerQuality.PolyphaseFast), Dsp.Tone(1000, inRate, inRate, 0.5), [4096]);
        double thd = Dsp.ThdPlusN(t, outRate, 1000, 200);
        Console.WriteLine($"FIGURE fast {inRate}->{outRate}: passband dev {worstPass:F4} dB to 0.8 Nyq, strongest alias {worstStop:F1} dB, thd+n {thd:F1} dB");
        Assert.True(worstPass <= 0.1);
        Assert.True(worstStop <= -70);
        Assert.True(thd <= -70);
    }

    [Fact]
    public void Linear_IsContinuous_AndExactOnRamp()
    {
        // A linear ramp is reproduced exactly by linear interpolation.
        var x = new float[200];
        for (int i = 0; i < x.Length; i++) x[i] = i * 0.01f;
        var r = AudioResampler.Create(1, ResamplerQuality.Linear, 1.7);
        var y = Dsp.Convert(r, x, [13]);
        for (int m = 0; m < y.Length - 2; m++)
            Assert.Equal(m / 1.7 * 0.01, y[m], 5);
    }
}

public class ResamplerStreamingTests
{
    [Theory]
    [InlineData(ResamplerQuality.Linear, 1, 1.088)]
    [InlineData(ResamplerQuality.Polyphase, 2, 0.919)]
    [InlineData(ResamplerQuality.Polyphase, 1, 1.7)]
    [InlineData(ResamplerQuality.PolyphaseFast, 3, 0.6)]
    public void Output_IsIdentical_ForAnyInputBlockSplit(ResamplerQuality q, int channels, double ratio)
    {
        var rng = new Random(11);
        var x = new float[channels * 20000];
        for (int i = 0; i < x.Length; i++) x[i] = (float)(rng.NextDouble() * 2 - 1);
        var whole = Dsp.Convert(AudioResampler.Create(channels, q, ratio), x, [100000]);
        var blocks = new[] { 1, 7, 64, 3, 255, 1000, 17, 513, 2, 4096 };
        var split = Dsp.Convert(AudioResampler.Create(channels, q, ratio), x, blocks);
        Assert.Equal(whole.Length, split.Length);
        Assert.True(whole.AsSpan().SequenceEqual(split), "outputs differ bitwise");

        // Tiny output buffers (input left unconsumed and re-offered) give the same result too.
        var r = AudioResampler.Create(channels, q, ratio);
        var small = new List<float>();
        var tmp = new float[5 * channels];
        int pos = 0;
        while (pos < x.Length)
        {
            r.Process(x.AsSpan(pos), tmp, out int used, out int w);
            small.AddRange(tmp.AsSpan(0, w * channels).ToArray());
            pos += used * channels;
        }
        int fw;
        do { fw = r.Flush(tmp); small.AddRange(tmp.AsSpan(0, fw * channels).ToArray()); } while (fw > 0);
        Assert.True(whole.AsSpan().SequenceEqual(small.ToArray()), "small-output-buffer run differs");
    }

    [Theory]
    [InlineData(ResamplerQuality.Linear, 2.0)]
    [InlineData(ResamplerQuality.Linear, 0.7)]
    [InlineData(ResamplerQuality.Polyphase, 2.0)]
    [InlineData(ResamplerQuality.Polyphase, 1.3)]
    [InlineData(ResamplerQuality.Polyphase, 0.7)]
    [InlineData(ResamplerQuality.PolyphaseFast, 0.7)]
    public void LatencyFrames_IsAccurate(ResamplerQuality q, double ratio)
    {
        var r = AudioResampler.Create(1, q, ratio);
        const int n0 = 1000;
        var x = new float[4000];
        x[n0] = 1;

        // The impulse is centred at output time n0 * ratio, whatever the filter's length.
        var y = Dsp.Convert(AudioResampler.Create(1, q, ratio), x);
        int peak = 0;
        for (int i = 0; i < y.Length; i++) if (Math.Abs(y[i]) > Math.Abs(y[peak])) peak = i;
        Assert.True(Math.Abs(peak - n0 * ratio) <= 1.0, $"peak at {peak}, expected {n0 * ratio}");

        // After N frames in, about N * ratio - LatencyFrames frames are out; the impulse's peak arrives once
        // the input has run LatencyFrames/ratio past it.
        var tmp = new float[16384];
        int total = 0;
        Span<float> one = stackalloc float[1];
        int firstWithPeak = -1;
        double lat = r.LatencyFrames;
        for (int fed = 1; fed <= 3000; fed++)
        {
            one[0] = x[fed - 1];
            r.Process(one, tmp.AsSpan(total), out _, out int w);
            total += w;
            if (fed > 100 && fed < 2900) Assert.True(Math.Abs(total - (fed * ratio - lat)) <= 2.0, $"fed {fed}: out {total}, expected {fed * ratio - lat}");
            if (firstWithPeak < 0 && total > peak) firstWithPeak = fed;
        }
        // The peak sample needs input up to n0 + half-width; LatencyFrames / ratio is that half-width.
        Assert.True(Math.Abs(firstWithPeak - (n0 + lat / ratio)) <= 2.0, $"peak released after {firstWithPeak} frames, expected {n0 + lat / ratio}");
    }

    [Theory]
    [InlineData(ResamplerQuality.Linear)]
    [InlineData(ResamplerQuality.Polyphase)]
    [InlineData(ResamplerQuality.PolyphaseFast)]
    public void SetRatio_MidStream_FollowsTheSignal_WithoutGlitches(ResamplerQuality q)
    {
        const double f = 1000, rate = 48000, amp = 0.5;
        var x = Dsp.Tone(f, rate, 200000, amp, 0);
        var r = AudioResampler.Create(1, q, 0.55);
        var outBuf = new float[64];
        double t = 0;                 // ideal input time of the next output
        double maxErr = 0, maxStep = 0, prev = double.NaN;
        int pos = 0, produced = 0;
        for (int call = 0; call < 3000; call++)
        {
            // glide 0.55 -> 2.1 -> 0.55 with one abrupt jump in the middle
            double u = call / 3000.0;
            double ratio = u < 0.5 ? 0.55 + 1.55 * (u / 0.5) : (u < 0.75 ? 2.1 - 1.55 * ((u - 0.5) / 0.25) : 0.7);
            if (call == 2200) ratio = 1.9;
            r.SetRatio(ratio);
            r.Process(x.AsSpan(pos), outBuf, out int used, out int w);
            pos += used;
            for (int i = 0; i < w; i++)
            {
                double ideal = amp * Math.Sin(2 * Math.PI * f * t / rate);
                if (produced > 300)
                {
                    maxErr = Math.Max(maxErr, Math.Abs(outBuf[i] - ideal));
                    if (!double.IsNaN(prev)) maxStep = Math.Max(maxStep, Math.Abs(outBuf[i] - prev) - amp * 2 * Math.PI * f / rate / 0.55);
                }
                prev = outBuf[i];
                t += 1.0 / ratio;
                produced++;
            }
        }
        Console.WriteLine($"FIGURE setratio {q}: max error vs ideal sine {maxErr:E2}, max step excess over slope bound {maxStep:E2}");
        // Linear interpolation of a 1 kHz tone at 48 kHz is off by up to (w^2/8) ~ 5e-4 of amplitude.
        Assert.True(maxErr <= (q == ResamplerQuality.Linear ? 1.5e-3 : 2e-4), $"error {maxErr}");
        Assert.True(maxStep <= 0, $"step exceeded the signal's own slope by {maxStep}");
    }

    [Fact]
    public void SetRatio_Range_CoversPlusMinusOneOctave()
    {
        // Default range 0.25..16: +-1 octave around any base ratio from 0.5 to 8.
        var r = AudioResampler.Create(2, ResamplerQuality.Polyphase, 1.0);
        r.SetRatio(0.25); r.SetRatio(16);
        Assert.Throws<ArgumentOutOfRangeException>(() => r.SetRatio(0.2));
        Assert.Throws<ArgumentOutOfRangeException>(() => r.SetRatio(17));
        Assert.Throws<ArgumentOutOfRangeException>(() => r.SetRatio(double.NaN));
        Assert.Throws<ArgumentOutOfRangeException>(() => r.SetRatio(0));
        Assert.Throws<ArgumentOutOfRangeException>(() => AudioResampler.Create(0, ResamplerQuality.Linear, 1));
        Assert.Throws<ArgumentOutOfRangeException>(() => AudioResampler.Create(1, ResamplerQuality.Linear, 100));
    }

    [Theory]
    [InlineData(ResamplerQuality.Linear)]
    [InlineData(ResamplerQuality.Polyphase)]
    [InlineData(ResamplerQuality.PolyphaseFast)]
    public void RealTimeCalls_AllocateNothing(ResamplerQuality q)
    {
        var r = AudioResampler.Create(2, q, 1.088);
        var rng = new Random(3);
        var input = new float[2 * 512];
        for (int i = 0; i < input.Length; i++) input[i] = (float)(rng.NextDouble() - 0.5);
        var output = new float[2 * r.MaxOutputFrames(512) + 64];

        void Round(int k)
        {
            r.SetRatio(0.6 + (k % 50) * 0.04);   // crosses redesign thresholds for the polyphase modes
            r.Process(input, output, out _, out _);
            if (k % 40 == 39) { r.Flush(output); r.Reset(); }
            if (k % 97 == 96) r.SetRatio(0.25);
        }
        for (int k = 0; k < 2000; k++) Round(k);   // warm-up: JIT and first redesigns

        long before = GC.GetAllocatedBytesForCurrentThread();
        for (int k = 0; k < 6000; k++) Round(k);
        long delta = GC.GetAllocatedBytesForCurrentThread() - before;
        Console.WriteLine($"FIGURE alloc {q}: {delta} bytes over 6000 rounds");
        Assert.Equal(0, delta);
    }

    [Fact]
    public void MaxOutputFrames_IsAnUpperBound()
    {
        foreach (var q in new[] { ResamplerQuality.Linear, ResamplerQuality.Polyphase })
            foreach (double ratio in new[] { 0.3, 0.919, 1.0, 1.088, 5.5 })
            {
                var r = AudioResampler.Create(2, q, ratio);
                var rng = new Random(5);
                var big = new float[2 * 100000];
                for (int round = 0; round < 50; round++)
                {
                    int frames = rng.Next(0, 3000);
                    int max = r.MaxOutputFrames(frames);
                    r.Process(big.AsSpan(0, frames * 2), big.AsSpan(0, 2 * max + 2000), out int used, out int w);
                    Assert.Equal(frames, used);
                    Assert.True(w <= max, $"{q} {ratio}: wrote {w}, bound {max}");
                }
            }
    }

    [Fact]
    public void Reset_RestoresInitialState()
    {
        var x = Dsp.Tone(1000, 48000, 5000);
        var r = AudioResampler.Create(1, ResamplerQuality.Polyphase, 0.9);
        var a = Dsp.Convert(r, x);
        var b = Dsp.Convert(r, x);   // Convert flushes, which resets
        Assert.True(a.AsSpan().SequenceEqual(b));
        var tmp = new float[100];
        r.Process(x.AsSpan(0, 300), tmp, out _, out _);
        r.Reset();
        Assert.True(Dsp.Convert(r, x).AsSpan().SequenceEqual(a));
    }

    [Fact]
    public void Flush_OutputLength_IsCeilOfInputTimesRatio()
    {
        foreach (var q in new[] { ResamplerQuality.Linear, ResamplerQuality.Polyphase })
        {
            var y = Dsp.Convert(AudioResampler.Create(1, q, 1.088), new float[10000]);
            Assert.InRange(y.Length, 10880, 10882);
        }
    }
}
