using System.Runtime.Intrinsics;

namespace OneAudio.Kernels;

/// <summary>
/// True-peak oversampling for loudness metering (ITU-R BS.1770-5 Annex 2): a 4x polyphase interpolator, 24 taps per phase.
/// The prototype is a Kaiser-windowed sinc cut at the input Nyquist frequency; each of the four phases is normalised to
/// unit gain at DC. Phase 0 is the input sample itself (the sinc is zero at every other sample position), so only the
/// three in-between phases cost a filter. The filter is causal: the interpolated points lag the input by <see cref="Delay"/> samples.
/// Both paths are safe code over spans; the vector path accumulates in float, the scalar reference in double.
/// </summary>
internal static class LoudnessKernels
{
    /// <summary>Oversampling factor.</summary>
    public const int Factor = 4;

    /// <summary>Taps per polyphase branch; a multiple of 4 so the vector path has no tail.</summary>
    public const int Taps = 24;

    /// <summary>Input samples between an input sample and the interpolated points computed from it.</summary>
    public const int Delay = Taps / 2;

    private const double KaiserBeta = 9.0;

    /// <summary>
    /// Builds the three non-trivial branch filters (phases 1 to 3), laid out oldest-sample-first so each is a plain dot
    /// product with the history window. The array holds 3 * <see cref="Taps"/> floats.
    /// </summary>
    public static float[] DesignBranches()
    {
        int n = Taps * Factor;
        int centre = Delay * Factor;
        var h = new double[n];
        double i0Beta = BesselI0(KaiserBeta);
        for (int i = 1; i < n; i++)
        {
            double t = (i - centre) / (double)Factor;
            double sinc = t == 0 ? 1.0 : Math.Sin(Math.PI * t) / (Math.PI * t);
            double r = (i - centre) / (double)centre;
            double w = BesselI0(KaiserBeta * Math.Sqrt(Math.Max(0.0, 1.0 - r * r))) / i0Beta;
            h[i] = sinc * w;
        }
        var branches = new float[(Factor - 1) * Taps];
        for (int k = 1; k < Factor; k++)
        {
            double sum = 0;
            for (int j = 0; j < Taps; j++) sum += h[j * Factor + k];
            for (int i = 0; i < Taps; i++)
            {
                // Window index i (oldest first) holds the input j = Taps - 1 - i samples back.
                branches[(k - 1) * Taps + i] = (float)(h[(Taps - 1 - i) * Factor + k] / sum);
            }
        }
        return branches;
    }

    private static double BesselI0(double x)
    {
        double sum = 1, term = 1, q = x * x / 4;
        for (int k = 1; k < 60; k++)
        {
            term *= q / ((double)k * k);
            sum += term;
            if (term < sum * 1e-17) break;
        }
        return sum;
    }

    /// <summary>
    /// Feeds one channel of an interleaved block through the interpolator and returns the largest absolute value seen,
    /// including the input samples themselves. <paramref name="history"/> is a doubled ring of 2 * <see cref="Taps"/>
    /// floats so the last <see cref="Taps"/> samples are always contiguous; <paramref name="pos"/> is the next write index.
    /// Non-finite inputs are treated as zero.
    /// </summary>
    public static float ProcessChannel(ReadOnlySpan<float> interleaved, int channel, int channels, Span<float> history, ref int pos,
        ReadOnlySpan<float> branches, float peak, bool vector)
    {
        int p = pos;
        for (int i = channel; i < interleaved.Length; i += channels)
        {
            float x = interleaved[i];
            if (!float.IsFinite(x)) x = 0f;
            float ax = MathF.Abs(x);
            if (ax > peak) peak = ax;
            history[p] = x;
            history[p + Taps] = x;
            if (++p == Taps) p = 0;
            var window = history.Slice(p, Taps);
            if (vector)
            {
                float a = Dot(window, branches);
                float b = Dot(window, branches.Slice(Taps, Taps));
                float c = Dot(window, branches.Slice(2 * Taps, Taps));
                a = MathF.Abs(a); b = MathF.Abs(b); c = MathF.Abs(c);
                if (a > peak) peak = a;
                if (b > peak) peak = b;
                if (c > peak) peak = c;
            }
            else
            {
                for (int k = 0; k < Factor - 1; k++)
                {
                    float v = MathF.Abs(DotScalar(window, branches.Slice(k * Taps, Taps)));
                    if (v > peak) peak = v;
                }
            }
        }
        pos = p;
        return peak;
    }

    /// <summary>Whether the vector dot product should be used on this host.</summary>
    public static bool UseVector => Vector128.IsHardwareAccelerated;

    /// <summary>Dot product of two <see cref="Taps"/>-long spans with 128-bit vectors (fixed summation order).</summary>
    public static float Dot(ReadOnlySpan<float> a, ReadOnlySpan<float> b)
    {
        var acc = Vector128<float>.Zero;
        for (int i = 0; i < Taps; i += 4)
            acc += Vector128.Create(a.Slice(i, 4)) * Vector128.Create(b.Slice(i, 4));
        return Vector128.Sum(acc);
    }

    /// <summary>Scalar reference for <see cref="Dot"/>, accumulating in double.</summary>
    public static float DotScalar(ReadOnlySpan<float> a, ReadOnlySpan<float> b)
    {
        double s = 0;
        for (int i = 0; i < Taps; i++) s += (double)a[i] * b[i];
        return (float)s;
    }
}
