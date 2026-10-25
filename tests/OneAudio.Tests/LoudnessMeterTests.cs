using System.Numerics;
using OneAudio.Kernels;
using OneAudio.Loudness;
using Xunit;

namespace OneAudio.Tests;

/// <summary>Loudness meter behaviour on generated signals: filters, weights, block independence, allocation, true-peak kernel, normalisation.</summary>
public class LoudnessMeterTests(Xunit.ITestOutputHelper output)
{
    private static float[] Stereo(int rate, double seconds, Func<double, double> left, Func<double, double> right)
    {
        int n = (int)(rate * seconds);
        var x = new float[n * 2];
        for (int i = 0; i < n; i++)
        {
            double t = (double)i / rate;
            x[2 * i] = (float)left(t);
            x[2 * i + 1] = (float)right(t);
        }
        return x;
    }

    [Fact]
    public void KWeighting_At48k_Reproduces_The_Published_Coefficients()
    {
        var k = new KWeighting(48000);
        var shelf = k.ShelfArray();
        var hp = k.HighPassArray();
        double worst = 0;
        for (int i = 0; i < 5; i++)
        {
            worst = Math.Max(worst, Math.Abs(shelf[i] - KWeighting.Table48kShelf[i]));
            worst = Math.Max(worst, Math.Abs(hp[i] - KWeighting.Table48kHighPass[i]));
        }
        LoudnessTestEnv.Log(output, $"max |designed - table| at 48 kHz = {worst:E2}");
        Assert.True(worst < 2e-8, $"coefficient deviation {worst:E2}");
    }

    /// <summary>
    /// The Recommendation defines other rates by "the same frequency response" as the 48 kHz table. Compare the designed
    /// filters' magnitude response with the table's response (a continuous function of frequency) up to 0.4 of each rate.
    /// A bilinear design cannot match a different rate's response exactly near its Nyquist frequency, hence the 0.4 limit.
    /// </summary>
    [Theory]
    [InlineData(32000, 0.02)]
    [InlineData(44100, 0.02)]
    [InlineData(88200, 0.02)]
    [InlineData(96000, 0.02)]
    [InlineData(192000, 0.02)]
    public void KWeighting_Other_Rates_Match_The_48k_Response(int rate, double toleranceDb)
    {
        var k = new KWeighting(rate);
        double worst = 0, atFreq = 0;
        for (double f = 20; f <= 0.4 * Math.Min(rate, 48000); f *= 1.05)
        {
            double want = KWeighting.ResponseDb(KWeighting.Table48kShelf, KWeighting.Table48kHighPass, f, 48000);
            double got = KWeighting.ResponseDb(k.ShelfArray(), k.HighPassArray(), f, rate);
            double d = Math.Abs(want - got);
            if (d > worst) { worst = d; atFreq = f; }
        }
        LoudnessTestEnv.Log(output, $"{rate} Hz: max response deviation from the 48 kHz table {worst:F4} dB at {atFreq:F0} Hz (limit {toleranceDb} dB)");
        Assert.True(worst < toleranceDb);
    }

    [Theory]
    [InlineData(48000)]
    [InlineData(44100)]
    [InlineData(96000)]
    [InlineData(22050)]
    [InlineData(16000)]
    public void Calibration_Sine_1kHz_Minus18_Reads_Minus18(int rate)
    {
        // Tech 3341 2.9: 1 kHz stereo sine at -18 dBFS peak, in phase, reads -18.0 LUFS.
        double a = Math.Pow(10, -18 / 20.0);
        var x = Stereo(rate, 20, t => a * Math.Sin(2 * Math.PI * 1000 * t), t => a * Math.Sin(2 * Math.PI * 1000 * t));
        var m = new LoudnessMeter(rate, 2);
        m.Process(x);
        LoudnessTestEnv.Log(output, $"{rate} Hz: I = {m.IntegratedLufs:F3}, M = {m.MomentaryLufs:F3}, S = {m.ShortTermLufs:F3}");
        Assert.InRange(m.IntegratedLufs, -18.1, -17.9);
        Assert.InRange(m.MomentaryLufs, -18.1, -17.9);
    }

    [Fact]
    public void Silence_And_Short_Input_Report_Negative_Infinity()
    {
        var m = new LoudnessMeter(48000, 2);
        Assert.Equal(double.NegativeInfinity, m.IntegratedLufs);
        Assert.Equal(double.NegativeInfinity, m.MomentaryLufs);
        Assert.Equal(double.NegativeInfinity, m.TruePeakDbtp);
        Assert.Equal(0.0, m.LoudnessRange);
        m.Process(new float[48000 * 2]);
        Assert.Equal(double.NegativeInfinity, m.IntegratedLufs);
        Assert.Equal(double.NegativeInfinity, m.MaxMomentaryLufs);
    }

    [Fact]
    public void Lfe_Is_Excluded_And_Surrounds_Are_Boosted()
    {
        const int rate = 48000;
        var tone = Enumerable.Range(0, rate * 5).Select(i => (float)(0.1 * Math.Sin(2 * Math.PI * 1000 * i / rate))).ToArray();
        float[] Place(int channels, int at)
        {
            var x = new float[tone.Length * channels];
            for (int i = 0; i < tone.Length; i++) x[i * channels + at] = tone[i];
            return x;
        }
        double Measure(int channels, int at)
        {
            var m = new LoudnessMeter(rate, channels);
            m.Process(Place(channels, at));
            return m.IntegratedLufs;
        }
        Assert.Equal(double.NegativeInfinity, Measure(6, 3));                       // LFE alone: nothing measured
        double front = Measure(6, 0);
        Assert.Equal(front, Measure(6, 1), 6);                                        // R equals L
        Assert.Equal(front, Measure(6, 2), 6);                                        // C equals L
        Assert.Equal(front + 10 * Math.Log10(1.41), Measure(6, 4), 3);                // Ls is +1.5 dB (1.41 power weight)
        Assert.Equal(front + 10 * Math.Log10(1.41), Measure(5, 3), 3);                // 5.0 without a mask: L R C Ls Rs
        // The LFE channel still counts for the true peak.
        var lfe = new LoudnessMeter(rate, 6);
        lfe.Process(Place(6, 3));
        Assert.InRange(lfe.TruePeakDbtp, -20.5, -19.5);
    }

    [Fact]
    public void Explicit_Channel_Mask_Drives_Weights()
    {
        var w = LoudnessMeter.ResolveWeights(4, ChannelMask.FrontLeft | ChannelMask.FrontRight | ChannelMask.LowFrequency | ChannelMask.SideLeft);
        Assert.Equal([1.0, 1.0, 0.0, 1.41], w);
        Assert.Equal([1.0], LoudnessMeter.ResolveWeights(1, ChannelMask.None));
        Assert.Equal([1.0, 1.0, 1.0, 1.41, 1.41], LoudnessMeter.ResolveWeights(5, ChannelMask.None));
        Assert.Equal(new double[7].Select(_ => 1.0).ToArray(), LoudnessMeter.ResolveWeights(7, ChannelMask.None));
    }

    [Fact]
    public void Results_Do_Not_Depend_On_Block_Size()
    {
        const int rate = 44100;
        var rng = new Random(7);
        // 14 s of programme: tone bursts at different levels plus noise, so every readout is non-trivial.
        var x = Stereo(rate, 14,
            t => (t % 5 < 3 ? 0.25 : 0.02) * Math.Sin(2 * Math.PI * 440 * t) + 0.01 * (rng.NextDouble() - 0.5),
            t => (t % 7 < 2 ? 0.5 : 0.05) * Math.Sin(2 * Math.PI * 3000 * t) + 0.01 * (rng.NextDouble() - 0.5));
        (double, double, double, double, double, double, double, double) Run(Func<int> block)
        {
            var m = new LoudnessMeter(rate, 2);
            int pos = 0;
            while (pos < x.Length)
            {
                int n = Math.Min(block() * 2, x.Length - pos);
                m.Process(x.AsSpan(pos, n));
                pos += n;
            }
            return (m.IntegratedLufs, m.LoudnessRange, m.MomentaryLufs, m.ShortTermLufs, m.MaxMomentaryLufs, m.MaxShortTermLufs, m.TruePeak, m.FramesProcessed);
        }
        var reference = Run(() => 100_000_000);
        LoudnessTestEnv.Log(output, $"I={reference.Item1:F3} LRA={reference.Item2:F3} M={reference.Item3:F3} S={reference.Item4:F3} maxM={reference.Item5:F3} maxS={reference.Item6:F3} TP={reference.Item7:F5}");
        Assert.Equal(reference, Run(() => 1));
        Assert.Equal(reference, Run(() => 7));
        var odd = new[] { 1, 3, 441, 1000, 4410, 17, 65537, 2 };
        int i = 0;
        Assert.Equal(reference, Run(() => odd[i++ % odd.Length]));
        Assert.Equal(reference, Run(() => 4096));
    }

    [Fact]
    public void Feeding_And_Reading_Allocate_Nothing()
    {
        const int rate = 48000;
        var m = new LoudnessMeter(rate, 6);
        var block = new float[480 * 6];
        var rng = new Random(3);
        for (int i = 0; i < block.Length; i++) block[i] = (float)(rng.NextDouble() - 0.5) * 0.3f;
        for (int w = 0; w < 200; w++) m.Process(block);   // warm up: JIT, tiering
        GC.Collect();
        long before = GC.GetAllocatedBytesForCurrentThread();
        int gcs = GC.CollectionCount(0);
        double sink = 0;
        for (int i = 0; i < 4000; i++)
        {
            m.Process(block);
            sink += m.MomentaryLufs + m.ShortTermLufs + m.IntegratedLufs + m.LoudnessRange + m.TruePeakDbtp + m.MaxMomentaryLufs;
        }
        long delta = GC.GetAllocatedBytesForCurrentThread() - before;
        LoudnessTestEnv.Log(output, $"allocated {delta} bytes over 4000 blocks (sink {sink:F1}); gen0 collections {GC.CollectionCount(0) - gcs}");
        Assert.Equal(0, delta);
        Assert.Equal(gcs, GC.CollectionCount(0));
    }

    [Fact]
    public void Non_Finite_Samples_Do_Not_Poison_The_Meter()
    {
        var m = new LoudnessMeter(48000, 1);
        var x = new float[48000];
        Array.Fill(x, 0.1f);
        x[100] = float.NaN; x[200] = float.PositiveInfinity;
        m.Process(x);
        Assert.True(double.IsFinite(m.MomentaryLufs));
        Assert.True(double.IsFinite(m.TruePeakDbtp));
    }

    [Fact]
    public void Argument_Checks()
    {
        Assert.Throws<ArgumentOutOfRangeException>(() => new LoudnessMeter(100, 2));
        Assert.Throws<ArgumentOutOfRangeException>(() => new LoudnessMeter(48000, 0));
        Assert.Throws<ArgumentException>(() => new LoudnessMeter(48000, 2).Process(new float[3]));
    }

    [Fact]
    public void Gating_Drops_Silence_And_Absolute_Gate_Holds()
    {
        const int rate = 48000;
        double a = Math.Pow(10, -23 / 20.0);
        // Steady -23 tone for 10 s, then 30 s at -80 dBFS: below the absolute gate and 57 LU under the tone.
        var loud = Stereo(rate, 10, t => a * Math.Sin(2 * Math.PI * 1000 * t), t => a * Math.Sin(2 * Math.PI * 1000 * t));
        var quiet = Stereo(rate, 30, t => 1e-4 * Math.Sin(2 * Math.PI * 1000 * t), t => 1e-4 * Math.Sin(2 * Math.PI * 1000 * t));
        var m = new LoudnessMeter(rate, 2);
        m.Process(loud); m.Process(quiet);
        // Blocks that straddle the end of the tone are partly loud and pass both gates, which is correct behaviour.
        Assert.InRange(m.IntegratedLufs, -23.1, -22.9);
    }

    // --- true-peak kernel ---

    [Fact]
    public void TruePeak_Kernel_Vector_Matches_Scalar_Reference()
    {
        LoudnessTestEnv.Log(output, $"Vector128.IsHardwareAccelerated = {System.Runtime.Intrinsics.Vector128.IsHardwareAccelerated}, Vector256 = {System.Runtime.Intrinsics.Vector256.IsHardwareAccelerated}, Avx2 = {System.Runtime.Intrinsics.X86.Avx2.IsSupported}");
        var branches = LoudnessKernels.DesignBranches();
        var rng = new Random(11);
        foreach (int frames in new[] { 1, 7, 8, 15, 16, 17, 31, 33, 1000 })
        foreach (int channels in new[] { 1, 2, 3, 6 })
        {
            var x = new float[frames * channels];
            for (int i = 0; i < x.Length; i++) x[i] = (float)(rng.NextDouble() * 2 - 1) * 0.9f;
            for (int c = 0; c < channels; c++)
            {
                int pv = 0, ps = 0;
                var hv = new float[2 * LoudnessKernels.Taps];
                var hs = new float[2 * LoudnessKernels.Taps];
                float v = LoudnessKernels.ProcessChannel(x, c, channels, hv, ref pv, branches, 0f, vector: true);
                float s = LoudnessKernels.ProcessChannel(x, c, channels, hs, ref ps, branches, 0f, vector: false);
                // Float accumulation of 24 products of magnitude <= 1 differs from the double reference by a few ulps.
                Assert.InRange(Math.Abs(v - s), 0f, 3e-6f);
                Assert.Equal(pv, ps);
            }
        }
    }

    [Fact]
    public void TruePeak_Interpolator_Branches_Have_Unit_DC_Gain_And_Flat_Passband()
    {
        var b = LoudnessKernels.DesignBranches();
        double worstDc = 0, worstRipple = 0;
        for (int k = 0; k < 3; k++)
        {
            double dc = 0;
            for (int i = 0; i < LoudnessKernels.Taps; i++) dc += b[k * LoudnessKernels.Taps + i];
            worstDc = Math.Max(worstDc, Math.Abs(dc - 1));
        }
        // Compose the full prototype response (all four phases) and look at 0..0.4 of the input rate.
        for (double f = 0.01; f <= 0.4; f += 0.01)
        {
            // Magnitude of branch k at input-normalised frequency f, phase-corrected by the fractional delay: use the branch sum
            // of the polyphase set |H_k(f)|; each must be near 1 across the passband.
            for (int k = 0; k < 3; k++)
            {
                Complex h = 0;
                for (int i = 0; i < LoudnessKernels.Taps; i++)
                    h += b[k * LoudnessKernels.Taps + i] * Complex.FromPolarCoordinates(1, -2 * Math.PI * f * i);
                double dev = Math.Abs(20 * Math.Log10(h.Magnitude));
                worstRipple = Math.Max(worstRipple, dev);
            }
        }
        LoudnessTestEnv.Log(output, $"DC gain error {worstDc:E2}; worst branch passband deviation to 0.4 fs {worstRipple:F4} dB");
        Assert.True(worstDc < 1e-6);
        Assert.True(worstRipple < 0.1);
    }

    [Theory]
    [InlineData(0.25, 0.0, 0.5, -6.0)]
    [InlineData(0.25, 45.0, 0.5, -6.0)]
    [InlineData(1.0 / 6, 60.0, 0.5, -6.0)]
    [InlineData(0.125, 67.5, 0.5, -6.0)]
    [InlineData(0.25, 45.0, 1.41, 2.98)]
    public void TruePeak_Of_Sines_At_Fraction_Of_Rate(double freqOverRate, double phaseDeg, double amp, double expectedDb)
    {
        // Tech 3341 cases 15 to 19 synthesised directly (with a 10 ms taper), at several sample rates.
        foreach (int rate in new[] { 44100, 48000, 96000 })
        {
            int n = rate / 2;
            var x = new float[n * 2];
            int taper = rate / 100;
            for (int i = 0; i < n; i++)
            {
                double env = Math.Min(1.0, Math.Min(i, n - 1 - i) / (double)taper);
                double v = amp * env * Math.Sin(2 * Math.PI * freqOverRate * i + phaseDeg * Math.PI / 180);
                x[2 * i] = x[2 * i + 1] = (float)v;
            }
            var m = new LoudnessMeter(rate, 2, loudnessRange: false);
            m.Process(x);
            LoudnessTestEnv.Log(output, $"{rate} Hz f/fs={freqOverRate:F3} phase {phaseDeg}: {m.TruePeakDbtp:F3} dBTP (expect {expectedDb})");
            Assert.InRange(m.TruePeakDbtp, expectedDb - 0.4, expectedDb + 0.2);
        }
    }

    // --- normalisation ---

    [Fact]
    public void Normalization_Gain()
    {
        var g = LoudnessNormalization.ComputeGain(-30.0, -12.0);
        Assert.Equal(7.0, g.GainDb, 9);        // loudness wants +7 dB, the -1 dBTP ceiling allows +11
        Assert.False(g.LimitedByPeak);
        g = LoudnessNormalization.ComputeGain(-30.0, -5.0);
        Assert.Equal(4.0, g.GainDb, 9);        // ceiling allows only +4 dB
        Assert.True(g.LimitedByPeak);
        g = LoudnessNormalization.ComputeGain(-30.0, -5.0, -23.0, null);
        Assert.Equal(7.0, g.GainDb, 9);
        Assert.Equal(0.0, LoudnessNormalization.ComputeGain(double.NegativeInfinity, double.NegativeInfinity).GainDb);
        Assert.Equal(Math.Pow(10, 7 / 20.0), LoudnessNormalization.ComputeGain(-30.0, -12.0).Linear, 9);
    }

    [Fact]
    public void Analyzer_Measures_A_Decoder_And_Honours_Cancellation()
    {
        const int rate = 48000;
        double a = Math.Pow(10, -20 / 20.0);
        var x = Stereo(rate, 8, t => a * Math.Sin(2 * Math.PI * 1000 * t), t => a * Math.Sin(2 * Math.PI * 1000 * t));
        var pcm = new short[x.Length];
        for (int i = 0; i < x.Length; i++) pcm[i] = (short)Math.Round(x[i] * 32768.0);
        var wav = WavBuilder.Build(1, 2, rate, 16, WavBuilder.Pcm16(pcm));
        using (var dec = AudioDecoder.Open(new MemoryStream(wav), LoudnessTestEnv.Registry))
        {
            var r = LoudnessAnalyzer.Measure(dec);
            LoudnessTestEnv.Log(output, r.ToString());
            Assert.InRange(r.IntegratedLufs, -20.1, -19.9);
            Assert.Equal(rate * 8, r.Frames);
            var gain = r.GainFor(-23.0);
            Assert.Equal(-3.0, gain.GainDb, 1);
        }
        using var dec2 = AudioDecoder.Open(new MemoryStream(wav), LoudnessTestEnv.Registry);
        using var cts = new CancellationTokenSource();
        cts.Cancel();
        Assert.Throws<OperationCanceledException>(() => LoudnessAnalyzer.Measure(dec2, cancellationToken: cts.Token));
    }
}
