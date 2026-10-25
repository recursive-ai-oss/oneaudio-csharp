using System.Runtime.Intrinsics;
using System.Runtime.Intrinsics.Arm;
using System.Runtime.Intrinsics.X86;
using OneAudio.Kernels;
using Xunit;

namespace OneAudio.Tests;

/// <summary>
/// Vector-versus-scalar checks of the encoder kernels. All of them are exact (integer arithmetic, or floating-point summed in a
/// fixed order without fused operations), so the tests assert equality of every bit on whichever path the hardware takes.
/// Run them again with DOTNET_EnableAVX512=0, DOTNET_EnableAVX2=0 and DOTNET_EnableHWIntrinsic=0 to cover the narrower paths.
/// </summary>
public class EncoderKernelTests
{
    private static readonly int[] Lengths = [1, 2, 3, 4, 5, 7, 8, 9, 12, 13, 15, 16, 17, 31, 32, 33, 63, 65, 127, 1000, 4096];

    [Fact]
    public void ReportHardwareState()
    {
        string s = $"encoder kernels: Vector128={Vector128.IsHardwareAccelerated} Vector256={Vector256.IsHardwareAccelerated} Vector512={Vector512.IsHardwareAccelerated} " +
                   $"Avx2={Avx2.IsSupported} Avx512F={Avx512F.IsSupported} AdvSimd={AdvSimd.IsSupported} Arch={System.Runtime.InteropServices.RuntimeInformation.ProcessArchitecture}";
        Console.WriteLine(s);
        TestContext.Current.SendDiagnosticMessage(s);
    }

    private static int[] RandomInts(int n, int seed, int bits)
    {
        var rng = new Random(seed);
        var a = new int[n];
        for (int i = 0; i < n; i++)
            a[i] = bits == 32 ? rng.Next(int.MinValue, int.MaxValue) : rng.Next(-(1 << (bits - 1)), 1 << (bits - 1));
        return a;
    }

    [Fact]
    public void Autocorrelation_MatchesScalarBitForBit()
    {
        var rng = new Random(5);
        foreach (int n in Lengths)
            foreach (int lags in new[] { 1, 2, 3, 4, 5, 9, 13, 17, 33 })
                foreach (int offset in new[] { 0, 1, 3 })
                {
                    int padded = n + ((lags + 3) & ~3);
                    var buf = new double[offset + padded];
                    for (int i = 0; i < n; i++) buf[offset + i] = (rng.NextDouble() - 0.5) * 65536;
                    var x = buf.AsSpan(offset, padded);
                    var expected = new double[lags];
                    var actual = new double[lags];
                    FlacEncKernels.AutocorrelationScalar(x, n, expected);
                    FlacEncKernels.Autocorrelation(x, n, actual);
                    for (int l = 0; l < lags; l++)
                        Assert.True(BitConverter.DoubleToInt64Bits(expected[l]) == BitConverter.DoubleToInt64Bits(actual[l]), $"n={n} lags={lags} lag {l}: {expected[l]} vs {actual[l]}");
                }
    }

    [Fact]
    public void Autocorrelation_IsCorrect()
    {
        double[] x = [1, 2, 3, 4, 0, 0, 0, 0];
        var r = new double[4];
        FlacEncKernels.Autocorrelation(x, 4, r);
        Assert.Equal([30.0, 20.0, 11.0, 4.0], r);
    }

    [Fact]
    public void LpcResidual32_MatchesScalar()
    {
        foreach (int n in Lengths)
            foreach (int order in new[] { 0, 1, 2, 3, 4, 7, 8, 12, 16, 32 })
            {
                if (order >= n) continue;
                foreach (int offset in new[] { 0, 1, 3 })
                {
                    var buf = RandomInts(n + offset, n * 31 + order, 16);
                    var s = buf.AsSpan(offset, n);
                    var coefs = RandomInts(order, order + 7, 10);
                    foreach (int shift in new[] { 0, 5, 14 })
                    {
                        var expected = new int[n];
                        var actual = new int[n];
                        FlacEncKernels.LpcResidualScalar(s, coefs, shift, expected, order);
                        FlacEncKernels.LpcResidual32(s, coefs, shift, actual);
                        Assert.True(expected.AsSpan(0, n - order).SequenceEqual(actual.AsSpan(0, n - order)), $"n={n} order={order} shift={shift}");
                    }
                }
            }
    }

    [Fact]
    public void LpcResidual64_AgreesWith32_AndReportsOverflow()
    {
        var s = RandomInts(500, 3, 16);
        var coefs = RandomInts(12, 4, 12);
        var a = new int[500];
        var b = new int[500];
        FlacEncKernels.LpcResidual32(s, coefs, 9, a);
        Assert.True(FlacEncKernels.LpcResidual64(s, coefs, 9, b));
        Assert.True(a.AsSpan(0, 488).SequenceEqual(b.AsSpan(0, 488)));

        int[] wide = [int.MaxValue, int.MinValue, int.MaxValue, int.MinValue, 5, 6];
        Assert.False(FlacEncKernels.LpcResidual64(wide, [1], 0, new int[6])); // int.MinValue - int.MaxValue does not fit
        Assert.False(FlacEncKernels.Fits32([1 << 14, 1 << 14], 24));
        Assert.True(FlacEncKernels.Fits32([4, -6, 4, -1], 27));
        Assert.False(FlacEncKernels.Fits32([4, -6, 4, -1], 28));
    }

    [Fact]
    public void FoldedStatsAndSumShifted_MatchScalar()
    {
        foreach (int n in Lengths.Append(0))
            foreach (int bits in new[] { 4, 16, 32 })
            {
                var r = RandomInts(n, n + bits, bits);
                if (n > 2) { r[0] = int.MaxValue; r[1] = -1; r[2] = int.MinValue + 1; }
                FlacEncKernels.FoldedStatsScalar(r, out ulong s0, out uint o0);
                FlacEncKernels.FoldedStats(r, out ulong s1, out uint o1);
                Assert.Equal((s0, o0), (s1, o1));
                foreach (int k in new[] { 0, 1, 7, 15, 29 })
                {
                    Assert.Equal(FlacEncKernels.SumShiftedScalar(r, k), FlacEncKernels.SumShifted(r, k));
                    FlacEncKernels.SumShifted3Scalar(r, k, out ulong e0, out ulong e1, out ulong e2);
                    FlacEncKernels.SumShifted3(r, k, out ulong b0, out ulong b1, out ulong b2);
                    Assert.Equal((e0, e1, e2), (b0, b1, b2));
                    Assert.Equal((FlacEncKernels.SumShiftedScalar(r, k), FlacEncKernels.SumShiftedScalar(r, k + 1), FlacEncKernels.SumShiftedScalar(r, k + 2)), (e0, e1, e2));
                }
            }
        // A run of -1 has OR 0 but is not all zeros: the sum tells them apart.
        FlacEncKernels.FoldedStats([-1, -1, -1], out ulong sum, out uint or);
        Assert.Equal((3UL, 0u), (sum, or));
    }

    [Fact]
    public void FixedOrderSums_MatchScalar_AndTheResidualKernel()
    {
        foreach (int n in Lengths.Where(l => l >= 5))
            foreach (int bits in new[] { 8, 16, 27 })
            {
                var s = RandomInts(n, n * 3 + bits, bits);
                var expected = new ulong[5];
                var actual = new ulong[5];
                FlacEncKernels.FixedOrderSumsScalar(s, expected, 4);
                FlacEncKernels.FixedOrderSums(s, actual);
                Assert.Equal(expected, actual);
                // Same quantity as the residual of each fixed predictor summed over samples 4 onwards.
                int[][] fixedCoefs = [[], [1], [2, -1], [3, -3, 1], [4, -6, 4, -1]];
                for (int o = 0; o < 5; o++)
                {
                    var res = new int[n];
                    FlacEncKernels.LpcResidual32(s, fixedCoefs[o], 0, res);
                    FlacEncKernels.FoldedStatsScalar(res.AsSpan(4 - o, n - 4), out ulong sum, out _);
                    Assert.Equal(sum, actual[o]);
                }
            }
    }

    [Fact]
    public void ApplyWindow_MatchesScalar()
    {
        var rng = new Random(9);
        foreach (int n in Lengths)
            foreach (int offset in new[] { 0, 1, 3 })
            {
                var buf = RandomInts(n + offset, n, 24);
                var w = new float[n + offset];
                for (int i = 0; i < w.Length; i++) w[i] = (float)rng.NextDouble();
                var expected = new double[n];
                var actual = new double[n];
                for (int i = 0; i < n; i++) expected[i] = (double)((float)buf[offset + i] * w[offset + i]);
                FlacEncKernels.ApplyWindow(buf.AsSpan(offset, n), w.AsSpan(offset, n), actual);
                for (int i = 0; i < n; i++) Assert.True(BitConverter.DoubleToInt64Bits(expected[i]) == BitConverter.DoubleToInt64Bits(actual[i]), $"n={n} i={i}");
            }
    }

    [Fact]
    public void SampleQuantize_MatchesScalarOnEdgeValues()
    {
        var rng = new Random(11);
        foreach (int bits in new[] { 8, 12, 16, 20, 24, 25, 32 })
            foreach (int n in Lengths)
            {
                var f = new float[n + 3];
                for (int i = 0; i < f.Length; i++) f[i] = (float)((rng.NextDouble() * 2.4 - 1.2) * (rng.Next(4) == 0 ? 1e-4 : 1));
                float[] specials = [float.NaN, float.PositiveInfinity, float.NegativeInfinity, 1f, -1f, 0.5f / (1 << (bits - 1 > 23 ? 23 : bits - 1)), 1.5f / 128f, 2.5f / 128f, -2.5f / 128f, 0f, -0f];
                for (int i = 0; i < specials.Length && i < n; i++) f[1 + i] = specials[i];
                var expected = new int[n];
                var actual = new int[n];
                SampleQuantize.ToIntegerScalar(f.AsSpan(1, n), expected, bits);
                SampleQuantize.ToInteger(f.AsSpan(1, n), actual, bits);
                Assert.Equal(expected, actual);
                int lo = bits == 32 ? int.MinValue : -(1 << (bits - 1)), hi = bits == 32 ? int.MaxValue : (1 << (bits - 1)) - 1;
                foreach (int v in actual) Assert.InRange(v, lo, hi);
            }
    }
}
