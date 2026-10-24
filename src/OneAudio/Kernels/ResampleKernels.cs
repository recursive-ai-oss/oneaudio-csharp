using System.Runtime.InteropServices;
using System.Runtime.Intrinsics;

namespace OneAudio.Kernels;

/// <summary>
/// Inner loop of the polyphase resampler. The caller (the resampler) has already sized every span; the kernel never
/// parses anything. <see cref="DotBlendScalar"/> is the reference; the vector versions add the same products in a
/// different order, so results agree to float rounding (a few ulp of the sum of absolute products), not bit for bit.
/// </summary>
internal static class ResampleKernels
{
    /// <summary>
    /// Dot product of an input window with the coefficient row obtained by blending two adjacent rows,
    /// <c>a + w * (b - a)</c>, which is how the resampler interpolates between filter phases. Picks the widest vector
    /// width the hardware accelerates, once per call.
    /// </summary>
    public static float DotBlend(ReadOnlySpan<float> x, ReadOnlySpan<float> a, ReadOnlySpan<float> b, float w)
    {
        Check(x, a, b);
        if (Vector512.IsHardwareAccelerated && x.Length >= 16) return DotBlendV512(x, a, b, w);
        if (Vector256.IsHardwareAccelerated && x.Length >= 8) return DotBlendV256(x, a, b, w);
        if (Vector128.IsHardwareAccelerated && x.Length >= 4) return DotBlendV128(x, a, b, w);
        return DotBlendScalar(x, a, b, w);
    }

    /// <summary>Reference implementation: four interleaved partial sums per row.</summary>
    public static float DotBlendScalar(ReadOnlySpan<float> x, ReadOnlySpan<float> a, ReadOnlySpan<float> b, float w)
    {
        Check(x, a, b);
        float a0 = 0, a1 = 0, a2 = 0, a3 = 0, b0 = 0, b1 = 0, b2 = 0, b3 = 0;
        int i = 0;
        for (; i + 4 <= x.Length; i += 4)
        {
            float x0 = x[i], x1 = x[i + 1], x2 = x[i + 2], x3 = x[i + 3];
            a0 += x0 * a[i]; a1 += x1 * a[i + 1]; a2 += x2 * a[i + 2]; a3 += x3 * a[i + 3];
            b0 += x0 * b[i]; b1 += x1 * b[i + 1]; b2 += x2 * b[i + 2]; b3 += x3 * b[i + 3];
        }
        for (; i < x.Length; i++) { a0 += x[i] * a[i]; b0 += x[i] * b[i]; }
        float sa = (a0 + a1) + (a2 + a3), sb = (b0 + b1) + (b2 + b3);
        return sa + w * (sb - sa);
    }

    /// <summary>128-bit version (SSE2, NEON).</summary>
    public static float DotBlendV128(ReadOnlySpan<float> x, ReadOnlySpan<float> a, ReadOnlySpan<float> b, float w)
    {
        Check(x, a, b);
        ref float rx = ref MemoryMarshal.GetReference(x);
        ref float ra = ref MemoryMarshal.GetReference(a);
        ref float rb = ref MemoryMarshal.GetReference(b);
        Vector128<float> a0 = default, a1 = default, b0 = default, b1 = default;
        int i = 0, n = x.Length;
        for (; i + 8 <= n; i += 8)
        {
            var x0 = Vector128.LoadUnsafe(ref rx, (nuint)i);
            var x1 = Vector128.LoadUnsafe(ref rx, (nuint)i + 4);
            a0 += x0 * Vector128.LoadUnsafe(ref ra, (nuint)i);
            a1 += x1 * Vector128.LoadUnsafe(ref ra, (nuint)i + 4);
            b0 += x0 * Vector128.LoadUnsafe(ref rb, (nuint)i);
            b1 += x1 * Vector128.LoadUnsafe(ref rb, (nuint)i + 4);
        }
        for (; i + 4 <= n; i += 4)
        {
            var x0 = Vector128.LoadUnsafe(ref rx, (nuint)i);
            a0 += x0 * Vector128.LoadUnsafe(ref ra, (nuint)i);
            b0 += x0 * Vector128.LoadUnsafe(ref rb, (nuint)i);
        }
        var va = a0 + a1;
        float sum = Vector128.Sum(va + Vector128.Create(w) * ((b0 + b1) - va));
        for (; i < n; i++) sum += x[i] * (a[i] + w * (b[i] - a[i]));
        return sum;
    }

    /// <summary>256-bit version (AVX2).</summary>
    public static float DotBlendV256(ReadOnlySpan<float> x, ReadOnlySpan<float> a, ReadOnlySpan<float> b, float w)
    {
        Check(x, a, b);
        ref float rx = ref MemoryMarshal.GetReference(x);
        ref float ra = ref MemoryMarshal.GetReference(a);
        ref float rb = ref MemoryMarshal.GetReference(b);
        Vector256<float> a0 = default, a1 = default, b0 = default, b1 = default;
        int i = 0, n = x.Length;
        for (; i + 16 <= n; i += 16)
        {
            var x0 = Vector256.LoadUnsafe(ref rx, (nuint)i);
            var x1 = Vector256.LoadUnsafe(ref rx, (nuint)i + 8);
            a0 += x0 * Vector256.LoadUnsafe(ref ra, (nuint)i);
            a1 += x1 * Vector256.LoadUnsafe(ref ra, (nuint)i + 8);
            b0 += x0 * Vector256.LoadUnsafe(ref rb, (nuint)i);
            b1 += x1 * Vector256.LoadUnsafe(ref rb, (nuint)i + 8);
        }
        for (; i + 8 <= n; i += 8)
        {
            var x0 = Vector256.LoadUnsafe(ref rx, (nuint)i);
            a0 += x0 * Vector256.LoadUnsafe(ref ra, (nuint)i);
            b0 += x0 * Vector256.LoadUnsafe(ref rb, (nuint)i);
        }
        var va = a0 + a1;
        float sum = Vector256.Sum(va + Vector256.Create(w) * ((b0 + b1) - va));
        for (; i < n; i++) sum += x[i] * (a[i] + w * (b[i] - a[i]));
        return sum;
    }

    /// <summary>512-bit version (AVX-512).</summary>
    public static float DotBlendV512(ReadOnlySpan<float> x, ReadOnlySpan<float> a, ReadOnlySpan<float> b, float w)
    {
        Check(x, a, b);
        ref float rx = ref MemoryMarshal.GetReference(x);
        ref float ra = ref MemoryMarshal.GetReference(a);
        ref float rb = ref MemoryMarshal.GetReference(b);
        Vector512<float> a0 = default, a1 = default, b0 = default, b1 = default;
        int i = 0, n = x.Length;
        for (; i + 32 <= n; i += 32)
        {
            var x0 = Vector512.LoadUnsafe(ref rx, (nuint)i);
            var x1 = Vector512.LoadUnsafe(ref rx, (nuint)i + 16);
            a0 += x0 * Vector512.LoadUnsafe(ref ra, (nuint)i);
            a1 += x1 * Vector512.LoadUnsafe(ref ra, (nuint)i + 16);
            b0 += x0 * Vector512.LoadUnsafe(ref rb, (nuint)i);
            b1 += x1 * Vector512.LoadUnsafe(ref rb, (nuint)i + 16);
        }
        for (; i + 16 <= n; i += 16)
        {
            var x0 = Vector512.LoadUnsafe(ref rx, (nuint)i);
            a0 += x0 * Vector512.LoadUnsafe(ref ra, (nuint)i);
            b0 += x0 * Vector512.LoadUnsafe(ref rb, (nuint)i);
        }
        var va = a0 + a1;
        float sum = Vector512.Sum(va + Vector512.Create(w) * ((b0 + b1) - va));
        for (; i < n; i++) sum += x[i] * (a[i] + w * (b[i] - a[i]));
        return sum;
    }

    private static void Check(ReadOnlySpan<float> x, ReadOnlySpan<float> a, ReadOnlySpan<float> b)
    {
        if (a.Length != x.Length || b.Length != x.Length) throw new ArgumentException("Window and coefficient rows must have the same length.");
    }
}
