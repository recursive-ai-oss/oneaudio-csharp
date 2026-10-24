using System.Runtime.Intrinsics;
using System.Runtime.Intrinsics.Arm;
using System.Runtime.Intrinsics.X86;
using OneAudio.Kernels;
using Xunit;

namespace OneAudio.Tests;

/// <summary>
/// Vector-versus-scalar checks of the mix kernels. The vector paths perform the same operations in the same order as the
/// scalar references, so the expected difference is exactly zero on every hardware path; the tests assert bit equality.
/// Run them again with DOTNET_EnableAVX512=0, DOTNET_EnableAVX2=0 and DOTNET_EnableHWIntrinsic=0 to cover the narrower paths.
/// </summary>
public class MixerKernelTests
{
    private static readonly int[] Lengths = [0, 1, 2, 3, 4, 5, 7, 8, 9, 15, 16, 17, 31, 32, 33, 63, 64, 65, 100, 255, 1000, 1023];

    [Fact]
    public void ReportHardwareState()
    {
        string s = $"mixkernels: Vector128={Vector128.IsHardwareAccelerated} Vector256={Vector256.IsHardwareAccelerated} Vector512={Vector512.IsHardwareAccelerated} " +
                   $"Avx2={Avx2.IsSupported} Avx512F={Avx512F.IsSupported} AdvSimd={AdvSimd.IsSupported} Arch={System.Runtime.InteropServices.RuntimeInformation.ProcessArchitecture}";
        Console.WriteLine(s);
        TestContext.Current.SendDiagnosticMessage(s);
    }

    private static float[] Random(int n, int seed)
    {
        var rng = new Random(seed);
        var a = new float[n];
        for (int i = 0; i < n; i++) a[i] = (float)(rng.NextDouble() * 2 - 1);
        return a;
    }

    private static void AssertSame(float[] expected, float[] actual)
    {
        Assert.Equal(expected.Length, actual.Length);
        for (int i = 0; i < expected.Length; i++)
            Assert.True(BitConverter.SingleToInt32Bits(expected[i]) == BitConverter.SingleToInt32Bits(actual[i]), $"index {i}: {expected[i]} vs {actual[i]}");
    }

    // Each case runs on slices at offsets 0..3 of a larger array, so the vector loads are unaligned.
    [Fact]
    public void AddScaled_MatchesScalar()
    {
        foreach (int n in Lengths)
            for (int off = 0; off < 4; off++)
            {
                var src = Random(n + 4, n * 7 + off);
                var a = Random(n + 4, n * 3 + 1);
                var b = (float[])a.Clone();
                MixKernels.AddScaled(a.AsSpan(off, n), src.AsSpan(off, n), 0.37f);
                MixKernels.AddScaledScalar(b.AsSpan(off, n), src.AsSpan(off, n), 0.37f);
                AssertSame(b, a); // also proves nothing outside the slice was written
            }
    }

    [Fact]
    public void AddScaledRamp_MatchesScalar()
    {
        foreach (int n in Lengths)
            for (int off = 0; off < 4; off++)
            {
                var src = Random(n + 4, n + off);
                var a = Random(n + 4, 5);
                var b = (float[])a.Clone();
                MixKernels.AddScaledRamp(a.AsSpan(off, n), src.AsSpan(off, n), 0.2f, -0.0007f);
                MixKernels.AddScaledRampScalar(b.AsSpan(off, n), src.AsSpan(off, n), 0.2f, -0.0007f);
                AssertSame(b, a);
            }
    }

    [Fact]
    public void AddCurve_MatchesScalar()
    {
        foreach (int n in Lengths)
            for (int off = 0; off < 4; off++)
            {
                var src = Random(n + 4, 11);
                var curve = Random(n + 4, 12);
                var a = Random(n + 4, 13);
                var b = (float[])a.Clone();
                MixKernels.AddCurve(a.AsSpan(off, n), src.AsSpan(off, n), curve.AsSpan(off, n));
                MixKernels.AddCurveScalar(b.AsSpan(off, n), src.AsSpan(off, n), curve.AsSpan(off, n));
                AssertSame(b, a);
            }
    }

    [Fact]
    public void Scale_And_ScaleRamp_MatchScalar()
    {
        foreach (int n in Lengths)
            for (int off = 0; off < 4; off++)
            {
                var a = Random(n + 4, 21);
                var b = (float[])a.Clone();
                MixKernels.Scale(a.AsSpan(off, n), 0.91f);
                MixKernels.ScaleScalar(b.AsSpan(off, n), 0.91f);
                AssertSame(b, a);
                MixKernels.ScaleRamp(a.AsSpan(off, n), 1.5f, -0.001f);
                MixKernels.ScaleRampScalar(b.AsSpan(off, n), 1.5f, -0.001f);
                AssertSame(b, a);
            }
    }

    [Fact]
    public void Deinterleave_And_Interleave_MatchScalar_ForOddChannelCounts()
    {
        foreach (int ch in new[] { 1, 2, 3, 5, 6, 8 })
            foreach (int frames in Lengths)
                for (int off = 0; off < 3; off++)
                {
                    int stride = frames + 5;
                    var inter = Random(frames * ch + 4, ch * 1000 + frames);
                    var p1 = new float[ch * stride + 3];
                    var p2 = new float[ch * stride + 3];
                    MixKernels.Deinterleave(inter.AsSpan(off, frames * ch), ch, frames, p1.AsSpan(off), stride);
                    MixKernels.DeinterleaveScalar(inter.AsSpan(off, frames * ch), ch, frames, p2.AsSpan(off), stride);
                    AssertSame(p2, p1);

                    var o1 = new float[frames * ch + 4];
                    var o2 = new float[frames * ch + 4];
                    MixKernels.Interleave(p1.AsSpan(off), stride, ch, frames, o1.AsSpan(off));
                    MixKernels.InterleaveScalar(p1.AsSpan(off), stride, ch, frames, o2.AsSpan(off));
                    AssertSame(o2, o1);
                    for (int i = 0; i < frames * ch; i++) Assert.Equal(inter[off + i], o1[off + i]); // round trip
                }
    }

    [Fact]
    public void Kernels_RejectShortBuffers()
    {
        Assert.Throws<ArgumentException>(() => MixKernels.AddScaled(new float[3], new float[4], 1f));
        Assert.Throws<ArgumentException>(() => MixKernels.Deinterleave(new float[7], 2, 4, new float[8], 4));
        Assert.Throws<ArgumentException>(() => MixKernels.Interleave(new float[7], 4, 2, 4, new float[8]));
    }
}
