using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using System.Runtime.Intrinsics;

namespace OneAudio.Kernels;

/// <summary>
/// Inner loops of the mixer, over planar (one contiguous run per channel) float buffers. Every operation has a
/// <c>*Scalar</c> reference that the vector path must match exactly: the vector code performs the same
/// multiply-then-add per element in the same order and never fuses them, and ramp coefficients are computed as
/// <c>start + step * index</c> in both paths, so results are bit-identical rather than merely close.
/// The widest hardware-accelerated width is chosen with <c>IsHardwareAccelerated</c> checks the JIT folds away, then
/// narrower widths and a scalar loop finish the remainder.
/// Callers have validated sizes; these methods only check that the buffers are long enough for the count they are given.
/// </summary>
internal static class MixKernels
{
    private static void CheckLength(int have, int need)
    {
        if (have < need) throw new ArgumentException("Buffer shorter than the frame count.");
    }

    // ------------------------------------------------------------------ dst += src * c

    /// <summary>Scalar reference of <see cref="AddScaled"/>.</summary>
    public static void AddScaledScalar(Span<float> dst, ReadOnlySpan<float> src, float c)
    {
        CheckLength(dst.Length, src.Length);
        for (int i = 0; i < src.Length; i++) dst[i] += src[i] * c;
    }

    /// <summary>Adds <c>src[i] * c</c> to <c>dst[i]</c> for every element of <paramref name="src"/>.</summary>
    public static void AddScaled(Span<float> dst, ReadOnlySpan<float> src, float c)
    {
        int n = src.Length;
        CheckLength(dst.Length, n);
        ref float d = ref MemoryMarshal.GetReference(dst);
        ref float s = ref MemoryMarshal.GetReference(src);
        int i = 0;
        if (Vector512.IsHardwareAccelerated)
        {
            var vc = Vector512.Create(c);
            for (; i <= n - 16; i += 16)
                (Vector512.LoadUnsafe(ref d, (nuint)i) + Vector512.LoadUnsafe(ref s, (nuint)i) * vc).StoreUnsafe(ref d, (nuint)i);
        }
        if (Vector256.IsHardwareAccelerated)
        {
            var vc = Vector256.Create(c);
            for (; i <= n - 8; i += 8)
                (Vector256.LoadUnsafe(ref d, (nuint)i) + Vector256.LoadUnsafe(ref s, (nuint)i) * vc).StoreUnsafe(ref d, (nuint)i);
        }
        if (Vector128.IsHardwareAccelerated)
        {
            var vc = Vector128.Create(c);
            for (; i <= n - 4; i += 4)
                (Vector128.LoadUnsafe(ref d, (nuint)i) + Vector128.LoadUnsafe(ref s, (nuint)i) * vc).StoreUnsafe(ref d, (nuint)i);
        }
        for (; i < n; i++) dst[i] += src[i] * c;
    }

    // ------------------------------------------------------------------ dst += src * (c0 + dc * i)

    /// <summary>Scalar reference of <see cref="AddScaledRamp"/>.</summary>
    public static void AddScaledRampScalar(Span<float> dst, ReadOnlySpan<float> src, float c0, float dc)
    {
        CheckLength(dst.Length, src.Length);
        for (int i = 0; i < src.Length; i++) dst[i] += src[i] * (c0 + dc * i);
    }

    /// <summary>Adds <c>src[i] * (c0 + dc * i)</c> to <c>dst[i]</c>: a linear gain ramp starting at element 0.</summary>
    public static void AddScaledRamp(Span<float> dst, ReadOnlySpan<float> src, float c0, float dc)
    {
        int n = src.Length;
        CheckLength(dst.Length, n);
        ref float d = ref MemoryMarshal.GetReference(dst);
        ref float s = ref MemoryMarshal.GetReference(src);
        int i = 0;
        if (Vector512.IsHardwareAccelerated)
        {
            var v0 = Vector512.Create(c0);
            var vd = Vector512.Create(dc);
            var lane = Vector512.Create(0f, 1f, 2f, 3f, 4f, 5f, 6f, 7f, 8f, 9f, 10f, 11f, 12f, 13f, 14f, 15f);
            for (; i <= n - 16; i += 16)
            {
                var coef = v0 + vd * (Vector512.Create((float)i) + lane);
                (Vector512.LoadUnsafe(ref d, (nuint)i) + Vector512.LoadUnsafe(ref s, (nuint)i) * coef).StoreUnsafe(ref d, (nuint)i);
            }
        }
        if (Vector256.IsHardwareAccelerated)
        {
            var v0 = Vector256.Create(c0);
            var vd = Vector256.Create(dc);
            var lane = Vector256.Create(0f, 1f, 2f, 3f, 4f, 5f, 6f, 7f);
            for (; i <= n - 8; i += 8)
            {
                var coef = v0 + vd * (Vector256.Create((float)i) + lane);
                (Vector256.LoadUnsafe(ref d, (nuint)i) + Vector256.LoadUnsafe(ref s, (nuint)i) * coef).StoreUnsafe(ref d, (nuint)i);
            }
        }
        if (Vector128.IsHardwareAccelerated)
        {
            var v0 = Vector128.Create(c0);
            var vd = Vector128.Create(dc);
            var lane = Vector128.Create(0f, 1f, 2f, 3f);
            for (; i <= n - 4; i += 4)
            {
                var coef = v0 + vd * (Vector128.Create((float)i) + lane);
                (Vector128.LoadUnsafe(ref d, (nuint)i) + Vector128.LoadUnsafe(ref s, (nuint)i) * coef).StoreUnsafe(ref d, (nuint)i);
            }
        }
        for (; i < n; i++) dst[i] += src[i] * (c0 + dc * i);
    }

    // ------------------------------------------------------------------ dst += src * curve

    /// <summary>Scalar reference of <see cref="AddCurve"/>.</summary>
    public static void AddCurveScalar(Span<float> dst, ReadOnlySpan<float> src, ReadOnlySpan<float> curve)
    {
        CheckLength(dst.Length, src.Length);
        CheckLength(curve.Length, src.Length);
        for (int i = 0; i < src.Length; i++) dst[i] += src[i] * curve[i];
    }

    /// <summary>Adds <c>src[i] * curve[i]</c> to <c>dst[i]</c>: a per-frame gain such as a ducking envelope.</summary>
    public static void AddCurve(Span<float> dst, ReadOnlySpan<float> src, ReadOnlySpan<float> curve)
    {
        int n = src.Length;
        CheckLength(dst.Length, n);
        CheckLength(curve.Length, n);
        ref float d = ref MemoryMarshal.GetReference(dst);
        ref float s = ref MemoryMarshal.GetReference(src);
        ref float g = ref MemoryMarshal.GetReference(curve);
        int i = 0;
        if (Vector512.IsHardwareAccelerated)
        {
            for (; i <= n - 16; i += 16)
                (Vector512.LoadUnsafe(ref d, (nuint)i) + Vector512.LoadUnsafe(ref s, (nuint)i) * Vector512.LoadUnsafe(ref g, (nuint)i)).StoreUnsafe(ref d, (nuint)i);
        }
        if (Vector256.IsHardwareAccelerated)
        {
            for (; i <= n - 8; i += 8)
                (Vector256.LoadUnsafe(ref d, (nuint)i) + Vector256.LoadUnsafe(ref s, (nuint)i) * Vector256.LoadUnsafe(ref g, (nuint)i)).StoreUnsafe(ref d, (nuint)i);
        }
        if (Vector128.IsHardwareAccelerated)
        {
            for (; i <= n - 4; i += 4)
                (Vector128.LoadUnsafe(ref d, (nuint)i) + Vector128.LoadUnsafe(ref s, (nuint)i) * Vector128.LoadUnsafe(ref g, (nuint)i)).StoreUnsafe(ref d, (nuint)i);
        }
        for (; i < n; i++) dst[i] += src[i] * curve[i];
    }

    // ------------------------------------------------------------------ buf *= c, buf *= (c0 + dc * i)

    /// <summary>Scalar reference of <see cref="Scale"/>.</summary>
    public static void ScaleScalar(Span<float> buf, float c)
    {
        for (int i = 0; i < buf.Length; i++) buf[i] *= c;
    }

    /// <summary>Multiplies every element of <paramref name="buf"/> by <paramref name="c"/> in place.</summary>
    public static void Scale(Span<float> buf, float c)
    {
        int n = buf.Length;
        ref float d = ref MemoryMarshal.GetReference(buf);
        int i = 0;
        if (Vector512.IsHardwareAccelerated)
        {
            var vc = Vector512.Create(c);
            for (; i <= n - 16; i += 16) (Vector512.LoadUnsafe(ref d, (nuint)i) * vc).StoreUnsafe(ref d, (nuint)i);
        }
        if (Vector256.IsHardwareAccelerated)
        {
            var vc = Vector256.Create(c);
            for (; i <= n - 8; i += 8) (Vector256.LoadUnsafe(ref d, (nuint)i) * vc).StoreUnsafe(ref d, (nuint)i);
        }
        if (Vector128.IsHardwareAccelerated)
        {
            var vc = Vector128.Create(c);
            for (; i <= n - 4; i += 4) (Vector128.LoadUnsafe(ref d, (nuint)i) * vc).StoreUnsafe(ref d, (nuint)i);
        }
        for (; i < n; i++) buf[i] *= c;
    }

    /// <summary>Scalar reference of <see cref="ScaleRamp"/>.</summary>
    public static void ScaleRampScalar(Span<float> buf, float c0, float dc)
    {
        for (int i = 0; i < buf.Length; i++) buf[i] *= c0 + dc * i;
    }

    /// <summary>Multiplies <c>buf[i]</c> by <c>c0 + dc * i</c> in place.</summary>
    public static void ScaleRamp(Span<float> buf, float c0, float dc)
    {
        int n = buf.Length;
        ref float d = ref MemoryMarshal.GetReference(buf);
        int i = 0;
        if (Vector512.IsHardwareAccelerated)
        {
            var v0 = Vector512.Create(c0);
            var vd = Vector512.Create(dc);
            var lane = Vector512.Create(0f, 1f, 2f, 3f, 4f, 5f, 6f, 7f, 8f, 9f, 10f, 11f, 12f, 13f, 14f, 15f);
            for (; i <= n - 16; i += 16)
                (Vector512.LoadUnsafe(ref d, (nuint)i) * (v0 + vd * (Vector512.Create((float)i) + lane))).StoreUnsafe(ref d, (nuint)i);
        }
        if (Vector256.IsHardwareAccelerated)
        {
            var v0 = Vector256.Create(c0);
            var vd = Vector256.Create(dc);
            var lane = Vector256.Create(0f, 1f, 2f, 3f, 4f, 5f, 6f, 7f);
            for (; i <= n - 8; i += 8)
                (Vector256.LoadUnsafe(ref d, (nuint)i) * (v0 + vd * (Vector256.Create((float)i) + lane))).StoreUnsafe(ref d, (nuint)i);
        }
        if (Vector128.IsHardwareAccelerated)
        {
            var v0 = Vector128.Create(c0);
            var vd = Vector128.Create(dc);
            var lane = Vector128.Create(0f, 1f, 2f, 3f);
            for (; i <= n - 4; i += 4)
                (Vector128.LoadUnsafe(ref d, (nuint)i) * (v0 + vd * (Vector128.Create((float)i) + lane))).StoreUnsafe(ref d, (nuint)i);
        }
        for (; i < n; i++) buf[i] *= c0 + dc * i;
    }

    // ------------------------------------------------------------------ interleaved <-> planar

    /// <summary>Scalar reference of <see cref="Deinterleave"/>.</summary>
    public static void DeinterleaveScalar(ReadOnlySpan<float> interleaved, int channels, int frames, Span<float> planar, int stride)
    {
        CheckLayout(interleaved.Length, planar.Length, channels, frames, stride);
        for (int c = 0; c < channels; c++)
        {
            var row = planar.Slice(c * stride, frames);
            for (int f = 0; f < row.Length; f++) row[f] = interleaved[f * channels + c];
        }
    }

    /// <summary>
    /// Splits <paramref name="frames"/> interleaved frames into per-channel runs: channel <c>c</c> goes to
    /// <c>planar[c * stride ..][0 .. frames]</c>. Mono is a copy and stereo is vectorized; other layouts use the scalar loop.
    /// </summary>
    public static void Deinterleave(ReadOnlySpan<float> interleaved, int channels, int frames, Span<float> planar, int stride)
    {
        CheckLayout(interleaved.Length, planar.Length, channels, frames, stride);
        if (channels == 1) { interleaved[..frames].CopyTo(planar); return; }
        if (channels != 2 || !Vector128.IsHardwareAccelerated) { DeinterleaveScalar(interleaved, channels, frames, planar, stride); return; }

        ref float src = ref MemoryMarshal.GetReference(interleaved);
        ref float left = ref MemoryMarshal.GetReference(planar);
        ref float right = ref Unsafe.Add(ref left, stride);
        int f = 0;
        if (Vector256.IsHardwareAccelerated)
        {
            for (; f <= frames - 4; f += 4)
            {
                var x = Vector256.Shuffle(Vector256.LoadUnsafe(ref src, (nuint)(2 * f)), Vector256.Create(0, 2, 4, 6, 1, 3, 5, 7));
                x.GetLower().StoreUnsafe(ref left, (nuint)f);
                x.GetUpper().StoreUnsafe(ref right, (nuint)f);
            }
        }
        else
        {
            for (; f <= frames - 4; f += 4)
            {
                var a = Vector128.Shuffle(Vector128.LoadUnsafe(ref src, (nuint)(2 * f)), Vector128.Create(0, 2, 1, 3));
                var b = Vector128.Shuffle(Vector128.LoadUnsafe(ref src, (nuint)(2 * f + 4)), Vector128.Create(0, 2, 1, 3));
                Vector128.Create(a.GetLower(), b.GetLower()).StoreUnsafe(ref left, (nuint)f);
                Vector128.Create(a.GetUpper(), b.GetUpper()).StoreUnsafe(ref right, (nuint)f);
            }
        }
        for (; f < frames; f++)
        {
            planar[f] = interleaved[2 * f];
            planar[stride + f] = interleaved[2 * f + 1];
        }
    }

    /// <summary>Scalar reference of <see cref="Interleave"/>.</summary>
    public static void InterleaveScalar(ReadOnlySpan<float> planar, int stride, int channels, int frames, Span<float> interleaved)
    {
        CheckLayout(interleaved.Length, planar.Length, channels, frames, stride);
        for (int c = 0; c < channels; c++)
        {
            var row = planar.Slice(c * stride, frames);
            for (int f = 0; f < row.Length; f++) interleaved[f * channels + c] = row[f];
        }
    }

    /// <summary>The inverse of <see cref="Deinterleave"/>: overwrites <c>interleaved[0 .. frames * channels]</c>.</summary>
    public static void Interleave(ReadOnlySpan<float> planar, int stride, int channels, int frames, Span<float> interleaved)
    {
        CheckLayout(interleaved.Length, planar.Length, channels, frames, stride);
        if (channels == 1) { planar[..frames].CopyTo(interleaved); return; }
        if (channels != 2 || !Vector128.IsHardwareAccelerated) { InterleaveScalar(planar, stride, channels, frames, interleaved); return; }

        ref float dst = ref MemoryMarshal.GetReference(interleaved);
        ref float left = ref MemoryMarshal.GetReference(planar);
        ref float right = ref Unsafe.Add(ref left, stride);
        int f = 0;
        if (Vector256.IsHardwareAccelerated)
        {
            for (; f <= frames - 4; f += 4)
            {
                var x = Vector256.Create(Vector128.LoadUnsafe(ref left, (nuint)f), Vector128.LoadUnsafe(ref right, (nuint)f));
                Vector256.Shuffle(x, Vector256.Create(0, 4, 1, 5, 2, 6, 3, 7)).StoreUnsafe(ref dst, (nuint)(2 * f));
            }
        }
        else
        {
            for (; f <= frames - 4; f += 4)
            {
                var l = Vector128.LoadUnsafe(ref left, (nuint)f);
                var r = Vector128.LoadUnsafe(ref right, (nuint)f);
                Vector128.Shuffle(Vector128.Create(l.GetLower(), r.GetLower()), Vector128.Create(0, 2, 1, 3)).StoreUnsafe(ref dst, (nuint)(2 * f));
                Vector128.Shuffle(Vector128.Create(l.GetUpper(), r.GetUpper()), Vector128.Create(0, 2, 1, 3)).StoreUnsafe(ref dst, (nuint)(2 * f + 4));
            }
        }
        for (; f < frames; f++)
        {
            interleaved[2 * f] = planar[f];
            interleaved[2 * f + 1] = planar[stride + f];
        }
    }

    private static void CheckLayout(int interleavedLength, int planarLength, int channels, int frames, int stride)
    {
        if (channels < 1 || frames < 0 || stride < frames) throw new ArgumentException("Invalid planar layout.");
        if (interleavedLength < (long)frames * channels) throw new ArgumentException("Interleaved buffer too short.");
        if (planarLength < (long)(channels - 1) * stride + frames) throw new ArgumentException("Planar buffer too short.");
    }
}
