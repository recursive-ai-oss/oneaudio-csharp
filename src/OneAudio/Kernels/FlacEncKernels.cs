using System.Runtime.InteropServices;
using System.Runtime.Intrinsics;

namespace OneAudio.Kernels;

/// <summary>
/// Number-crunching loops of the FLAC encoder: autocorrelation for LPC analysis, the prediction residual, and the
/// statistics the Rice partition search needs. Each has a scalar reference and vector paths chosen once per call.
/// The integer kernels are exact, so every path returns identical values. The autocorrelation keeps one running sum
/// per lag, added in ascending sample order with no fused multiply-add, so its vector paths match the scalar loop
/// bit for bit and an encoded file does not depend on the hardware that wrote it.
/// </summary>
internal static class FlacEncKernels
{
    // ---- autocorrelation ------------------------------------------------------------------------------

    /// <summary>
    /// <c>r[l] = sum over i of x[i] * x[i + l]</c> for <c>l</c> in <c>0 .. r.Length - 1</c>, where <c>x</c> is read as zero at and
    /// beyond index <paramref name="n"/>. <paramref name="x"/> must hold at least <c>n + r.Length</c> rounded up to a multiple of 4
    /// values, the ones past <paramref name="n"/> being zero.
    /// </summary>
    public static void Autocorrelation(ReadOnlySpan<double> x, int n, Span<double> r)
    {
        int lagBlocks = (r.Length + 3) / 4;
        if (n < 0 || x.Length < n + lagBlocks * 4) throw new ArgumentException("Input is too short for the lag count.", nameof(x));
        if (Vector256.IsHardwareAccelerated) Autocorrelation256(x, n, r);
        else if (Vector128.IsHardwareAccelerated) Autocorrelation128(x, n, r);
        else AutocorrelationScalar(x, n, r);
    }

    /// <summary>The reference loop.</summary>
    public static void AutocorrelationScalar(ReadOnlySpan<double> x, int n, Span<double> r)
    {
        for (int l = 0; l < r.Length; l++)
        {
            double s = 0;
            for (int i = 0; i + l < n; i++) s += x[i] * x[i + l];
            r[l] = s;
        }
    }

    private static void Autocorrelation256(ReadOnlySpan<double> x, int n, Span<double> r)
    {
        ref double xr = ref MemoryMarshal.GetReference(x);
        int blocks = (r.Length + 3) / 4;
        Span<double> tmp = stackalloc double[16];
        int b = 0;
        while (b < blocks)
        {
            int lb = b * 4;
            int take = Math.Min(4, blocks - b);
            Vector256<double> x0 = default, x1 = default, x2 = default, x3 = default;
            if (take == 4)
            {
                for (int i = 0; i < n; i++)
                {
                    var xi = Vector256.Create(At(ref xr, i));
                    x0 += xi * Vector256.LoadUnsafe(ref xr, (nuint)(i + lb));
                    x1 += xi * Vector256.LoadUnsafe(ref xr, (nuint)(i + lb + 4));
                    x2 += xi * Vector256.LoadUnsafe(ref xr, (nuint)(i + lb + 8));
                    x3 += xi * Vector256.LoadUnsafe(ref xr, (nuint)(i + lb + 12));
                }
            }
            else
            {
                take = 1;
                for (int i = 0; i < n; i++)
                    x0 += Vector256.Create(At(ref xr, i)) * Vector256.LoadUnsafe(ref xr, (nuint)(i + lb));
            }
            x0.CopyTo(tmp); x1.CopyTo(tmp[4..]); x2.CopyTo(tmp[8..]); x3.CopyTo(tmp[12..]);
            for (int k = 0; k < take * 4 && lb + k < r.Length; k++) r[lb + k] = tmp[k];
            b += take;
        }
    }

    private static void Autocorrelation128(ReadOnlySpan<double> x, int n, Span<double> r)
    {
        ref double xr = ref MemoryMarshal.GetReference(x);
        int blocks = (r.Length + 3) / 4; // groups of four lags, two vectors each
        Span<double> tmp = stackalloc double[4];
        for (int b = 0; b < blocks; b++)
        {
            int lb = b * 4;
            Vector128<double> x0 = default, x1 = default;
            for (int i = 0; i < n; i++)
            {
                var xi = Vector128.Create(At(ref xr, i));
                x0 += xi * Vector128.LoadUnsafe(ref xr, (nuint)(i + lb));
                x1 += xi * Vector128.LoadUnsafe(ref xr, (nuint)(i + lb + 2));
            }
            x0.CopyTo(tmp); x1.CopyTo(tmp[2..]);
            for (int k = 0; k < 4 && lb + k < r.Length; k++) r[lb + k] = tmp[k];
        }
    }

    private static double At(ref double r, int i) => System.Runtime.CompilerServices.Unsafe.Add(ref r, i);

    // ---- prediction residual -----------------------------------------------------------------------------

    /// <summary>
    /// The residual of a quantized linear predictor in 32-bit arithmetic: for each <c>i</c> from <c>coefficients.Length</c> to
    /// <c>samples.Length - 1</c>, <c>residual[i - order] = samples[i] - ((sum of coefficients[j] * samples[i-1-j]) >> shift)</c>.
    /// The caller guarantees no 32-bit overflow (see <see cref="Fits32"/>); wrap-around would still agree across paths.
    /// </summary>
    public static void LpcResidual32(ReadOnlySpan<int> samples, ReadOnlySpan<int> coefficients, int shift, Span<int> residual)
    {
        int order = coefficients.Length;
        if (residual.Length < samples.Length - order) throw new ArgumentException("Residual buffer is too short.", nameof(residual));
        if (Vector256.IsHardwareAccelerated) LpcResidual256(samples, coefficients, shift, residual);
        else if (Vector128.IsHardwareAccelerated) LpcResidual128(samples, coefficients, shift, residual);
        else LpcResidualScalar(samples, coefficients, shift, residual, order);
    }

    /// <summary>The reference loop.</summary>
    public static void LpcResidualScalar(ReadOnlySpan<int> samples, ReadOnlySpan<int> coefficients, int shift, Span<int> residual, int start)
    {
        int order = coefficients.Length;
        for (int i = start; i < samples.Length; i++)
        {
            int pred = 0;
            for (int j = 0; j < order; j++) pred += coefficients[j] * samples[i - 1 - j];
            residual[i - order] = samples[i] - (pred >> shift);
        }
    }

    private static void LpcResidual256(ReadOnlySpan<int> s, ReadOnlySpan<int> c, int shift, Span<int> res)
    {
        int order = c.Length;
        ref int sr = ref MemoryMarshal.GetReference(s);
        ref int rr = ref MemoryMarshal.GetReference(res);
        int i = order;
        for (; i + 8 <= s.Length; i += 8)
        {
            var acc = Vector256<int>.Zero;
            for (int j = 0; j < order; j++)
                acc += Vector256.Create(c[j]) * Vector256.LoadUnsafe(ref sr, (nuint)(i - 1 - j));
            (Vector256.LoadUnsafe(ref sr, (nuint)i) - Vector256.ShiftRightArithmetic(acc, shift)).StoreUnsafe(ref rr, (nuint)(i - order));
        }
        LpcResidualScalar(s, c, shift, res, i);
    }

    private static void LpcResidual128(ReadOnlySpan<int> s, ReadOnlySpan<int> c, int shift, Span<int> res)
    {
        int order = c.Length;
        ref int sr = ref MemoryMarshal.GetReference(s);
        ref int rr = ref MemoryMarshal.GetReference(res);
        int i = order;
        for (; i + 4 <= s.Length; i += 4)
        {
            var acc = Vector128<int>.Zero;
            for (int j = 0; j < order; j++)
                acc += Vector128.Create(c[j]) * Vector128.LoadUnsafe(ref sr, (nuint)(i - 1 - j));
            (Vector128.LoadUnsafe(ref sr, (nuint)i) - Vector128.ShiftRightArithmetic(acc, shift)).StoreUnsafe(ref rr, (nuint)(i - order));
        }
        LpcResidualScalar(s, c, shift, res, i);
    }

    /// <summary>
    /// The same residual in 64-bit arithmetic, for samples and coefficients too wide for <see cref="LpcResidual32"/>.
    /// Returns false when a residual does not fit a FLAC residual (RFC 9639 9.2.7.1: 32 bits signed, not -2^31).
    /// </summary>
    public static bool LpcResidual64(ReadOnlySpan<int> samples, ReadOnlySpan<int> coefficients, int shift, Span<int> residual)
    {
        int order = coefficients.Length;
        if (residual.Length < samples.Length - order) throw new ArgumentException("Residual buffer is too short.", nameof(residual));
        for (int i = order; i < samples.Length; i++)
        {
            long pred = 0;
            for (int j = 0; j < order; j++) pred += (long)coefficients[j] * samples[i - 1 - j];
            long r = samples[i] - (pred >> shift);
            if (r <= int.MinValue || r > int.MaxValue) return false;
            residual[i - order] = (int)r;
        }
        return true;
    }

    /// <summary>
    /// True when a predictor with these coefficients cannot overflow 32 bits on samples of <paramref name="sampleBits"/> bits:
    /// the prediction sum, the shifted sum and the difference with the sample all stay inside int.
    /// </summary>
    public static bool Fits32(ReadOnlySpan<int> coefficients, int sampleBits)
    {
        long sumAbs = 1; // the sample itself in "sample - prediction"
        foreach (int c in coefficients) sumAbs += Math.Abs((long)c);
        return sumAbs << (sampleBits - 1) < (1L << 31);
    }

    // ---- Rice statistics -------------------------------------------------------------------------------

    /// <summary>
    /// Over a run of residuals: the sum of their zigzag-folded values (<c>(r &lt;&lt; 1) ^ (r >> 31)</c>, the number a Rice code
    /// stores) and the OR of <c>r ^ (r >> 31)</c>, whose bit length plus one is the width of a raw signed field that holds them all.
    /// </summary>
    public static void FoldedStats(ReadOnlySpan<int> residual, out ulong sum, out uint orBits)
    {
        if (Vector256.IsHardwareAccelerated && residual.Length >= 8) FoldedStats256(residual, out sum, out orBits);
        else FoldedStatsScalar(residual, out sum, out orBits);
    }

    /// <summary>The reference loop.</summary>
    public static void FoldedStatsScalar(ReadOnlySpan<int> residual, out ulong sum, out uint orBits)
    {
        ulong s = 0;
        uint o = 0;
        foreach (int r in residual)
        {
            s += (uint)((r << 1) ^ (r >> 31));
            o |= (uint)(r ^ (r >> 31));
        }
        sum = s;
        orBits = o;
    }

    private static void FoldedStats256(ReadOnlySpan<int> residual, out ulong sum, out uint orBits)
    {
        ref int rr = ref MemoryMarshal.GetReference(residual);
        var acc = Vector256<ulong>.Zero;
        var orAcc = Vector256<uint>.Zero;
        int i = 0;
        for (; i + 8 <= residual.Length; i += 8)
        {
            var v = Vector256.LoadUnsafe(ref rr, (nuint)i);
            var sign = Vector256.ShiftRightArithmetic(v, 31);
            var folded = ((v << 1) ^ sign).AsUInt32();
            orAcc |= (v ^ sign).AsUInt32();
            acc += Vector256.WidenLower(folded) + Vector256.WidenUpper(folded);
        }
        FoldedStatsScalar(residual[i..], out ulong ts, out uint to);
        sum = Vector256.Sum(acc) + ts;
        uint o = to;
        for (int k = 0; k < 8; k++) o |= orAcc.GetElement(k);
        orBits = o;
    }

    /// <summary>
    /// The sum of <c>folded >> k</c> over a run of residuals, where folded is <c>(r &lt;&lt; 1) ^ (r >> 31)</c>: the total length of the
    /// unary parts of a Rice code with parameter <paramref name="k"/> (each also costs one stop bit and k low bits).
    /// </summary>
    public static ulong SumShifted(ReadOnlySpan<int> residual, int k)
    {
        if (Vector256.IsHardwareAccelerated && residual.Length >= 8) return SumShifted256(residual, k);
        return SumShiftedScalar(residual, k);
    }

    /// <summary>The reference loop.</summary>
    public static ulong SumShiftedScalar(ReadOnlySpan<int> residual, int k)
    {
        ulong s = 0;
        foreach (int r in residual) s += (uint)((r << 1) ^ (r >> 31)) >> k;
        return s;
    }

    private static ulong SumShifted256(ReadOnlySpan<int> residual, int k)
    {
        ref int rr = ref MemoryMarshal.GetReference(residual);
        var acc = Vector256<ulong>.Zero;
        int i = 0;
        for (; i + 8 <= residual.Length; i += 8)
        {
            var v = Vector256.LoadUnsafe(ref rr, (nuint)i);
            var folded = Vector256.ShiftRightLogical(((v << 1) ^ Vector256.ShiftRightArithmetic(v, 31)).AsUInt32(), k);
            acc += Vector256.WidenLower(folded) + Vector256.WidenUpper(folded);
        }
        return Vector256.Sum(acc) + SumShiftedScalar(residual[i..], k);
    }

    /// <summary>
    /// <see cref="SumShifted"/> for the three shifts <paramref name="k"/>, <paramref name="k"/> + 1 and <paramref name="k"/> + 2 in one pass
    /// (<paramref name="k"/> + 2 must be at most 31): the sizes of a Rice code with the three neighbouring parameters.
    /// </summary>
    public static void SumShifted3(ReadOnlySpan<int> residual, int k, out ulong s0, out ulong s1, out ulong s2)
    {
        if (Vector256.IsHardwareAccelerated && residual.Length >= 8) SumShifted3_256(residual, k, out s0, out s1, out s2);
        else SumShifted3Scalar(residual, k, out s0, out s1, out s2);
    }

    /// <summary>The reference loop.</summary>
    public static void SumShifted3Scalar(ReadOnlySpan<int> residual, int k, out ulong s0, out ulong s1, out ulong s2)
    {
        ulong a = 0, b = 0, c = 0;
        foreach (int r in residual)
        {
            uint u = (uint)((r << 1) ^ (r >> 31));
            a += u >> k;
            b += u >> (k + 1);
            c += u >> (k + 2);
        }
        s0 = a; s1 = b; s2 = c;
    }

    private static void SumShifted3_256(ReadOnlySpan<int> residual, int k, out ulong s0, out ulong s1, out ulong s2)
    {
        ref int rr = ref MemoryMarshal.GetReference(residual);
        Vector256<ulong> a = default, b = default, c = default;
        int i = 0;
        for (; i + 8 <= residual.Length; i += 8)
        {
            var v = Vector256.LoadUnsafe(ref rr, (nuint)i);
            var u = ((v << 1) ^ Vector256.ShiftRightArithmetic(v, 31)).AsUInt32();
            var u0 = Vector256.ShiftRightLogical(u, k);
            var u1 = Vector256.ShiftRightLogical(u0, 1);
            var u2 = Vector256.ShiftRightLogical(u0, 2);
            a += Vector256.WidenLower(u0) + Vector256.WidenUpper(u0);
            b += Vector256.WidenLower(u1) + Vector256.WidenUpper(u1);
            c += Vector256.WidenLower(u2) + Vector256.WidenUpper(u2);
        }
        SumShifted3Scalar(residual[i..], k, out ulong ta, out ulong tb, out ulong tc);
        s0 = Vector256.Sum(a) + ta;
        s1 = Vector256.Sum(b) + tb;
        s2 = Vector256.Sum(c) + tc;
    }

    // ---- fixed-predictor ranking and windowing ---------------------------------------------------------------------

    /// <summary>
    /// One pass that ranks the fixed predictors: <c>sums[o]</c> is the sum of folded residuals of the order-<c>o</c> fixed predictor
    /// (o = 0 to 4, RFC 9639 9.2.8) over samples 4 to <c>samples.Length - 1</c>, so the orders compare over the same samples.
    /// Arithmetic wraps in 32 bits, so the caller only uses it for samples narrow enough that the order-4 residual fits.
    /// <paramref name="samples"/> needs at least 5 values.
    /// </summary>
    public static void FixedOrderSums(ReadOnlySpan<int> samples, Span<ulong> sums)
    {
        if (samples.Length < 5 || sums.Length < 5) throw new ArgumentException("Need at least 5 samples and 5 sums.");
        if (Vector256.IsHardwareAccelerated && samples.Length >= 12) FixedOrderSums256(samples, sums);
        else FixedOrderSumsScalar(samples, sums, 4);
    }

    /// <summary>The reference loop, starting at sample <paramref name="start"/> (4 for a whole run).</summary>
    public static void FixedOrderSumsScalar(ReadOnlySpan<int> s, Span<ulong> sums, int start)
    {
        if (start == 4) sums[..5].Clear();
        for (int i = start; i < s.Length; i++)
        {
            int x0 = s[i], x1 = s[i - 1], x2 = s[i - 2], x3 = s[i - 3], x4 = s[i - 4];
            int d1 = x0 - x1, d2 = x1 - x2, d3 = x2 - x3, d4 = x3 - x4;
            int r2 = d1 - d2;
            int e2 = d2 - d3, e3 = d3 - d4;
            int r3 = r2 - e2;
            int r4 = r3 - (e2 - e3);
            sums[0] += (uint)((x0 << 1) ^ (x0 >> 31));
            sums[1] += (uint)((d1 << 1) ^ (d1 >> 31));
            sums[2] += (uint)((r2 << 1) ^ (r2 >> 31));
            sums[3] += (uint)((r3 << 1) ^ (r3 >> 31));
            sums[4] += (uint)((r4 << 1) ^ (r4 >> 31));
        }
    }

    private static void FixedOrderSums256(ReadOnlySpan<int> s, Span<ulong> sums)
    {
        ref int sr = ref MemoryMarshal.GetReference(s);
        Vector256<ulong> t0 = default, t1 = default, t2 = default, t3 = default, t4 = default;
        int i = 4;
        for (; i + 8 <= s.Length; i += 8)
        {
            var x0 = Vector256.LoadUnsafe(ref sr, (nuint)i);
            var x1 = Vector256.LoadUnsafe(ref sr, (nuint)(i - 1));
            var x2 = Vector256.LoadUnsafe(ref sr, (nuint)(i - 2));
            var x3 = Vector256.LoadUnsafe(ref sr, (nuint)(i - 3));
            var x4 = Vector256.LoadUnsafe(ref sr, (nuint)(i - 4));
            var d1 = x0 - x1;
            var d2 = x1 - x2;
            var d3 = x2 - x3;
            var d4 = x3 - x4;
            var r2 = d1 - d2;
            var e2 = d2 - d3;
            var e3 = d3 - d4;
            var r3 = r2 - e2;
            var r4 = r3 - (e2 - e3);
            t0 += Widen(x0); t1 += Widen(d1); t2 += Widen(r2); t3 += Widen(r3); t4 += Widen(r4);
        }
        Span<ulong> tail = stackalloc ulong[5];
        tail.Clear();
        FixedOrderSumsScalar(s, tail, i);
        sums[0] = Vector256.Sum(t0) + tail[0];
        sums[1] = Vector256.Sum(t1) + tail[1];
        sums[2] = Vector256.Sum(t2) + tail[2];
        sums[3] = Vector256.Sum(t3) + tail[3];
        sums[4] = Vector256.Sum(t4) + tail[4];
    }

    private static Vector256<ulong> Widen(Vector256<int> v)
    {
        var folded = ((v << 1) ^ Vector256.ShiftRightArithmetic(v, 31)).AsUInt32();
        return Vector256.WidenLower(folded) + Vector256.WidenUpper(folded);
    }

    /// <summary>
    /// <c>destination[i] = (double)((float)samples[i] * window[i])</c>: the windowed block for the autocorrelation. The product is
    /// rounded once, in single precision, which is as exact as the inputs warrant and identical on every path.
    /// </summary>
    public static void ApplyWindow(ReadOnlySpan<int> samples, ReadOnlySpan<float> window, Span<double> destination)
    {
        int n = samples.Length;
        if (window.Length < n || destination.Length < n) throw new ArgumentException("Window and destination must be at least as long as the samples.");
        int i = 0;
        if (Vector256.IsHardwareAccelerated)
        {
            ref int sr = ref MemoryMarshal.GetReference(samples);
            ref float wr = ref MemoryMarshal.GetReference(window);
            ref double dr = ref MemoryMarshal.GetReference(destination);
            for (; i + 8 <= n; i += 8)
            {
                var p = Vector256.ConvertToSingle(Vector256.LoadUnsafe(ref sr, (nuint)i)) * Vector256.LoadUnsafe(ref wr, (nuint)i);
                Vector256.WidenLower(p).StoreUnsafe(ref dr, (nuint)i);
                Vector256.WidenUpper(p).StoreUnsafe(ref dr, (nuint)(i + 4));
            }
        }
        for (; i < n; i++) destination[i] = (double)((float)samples[i] * window[i]);
    }
}
