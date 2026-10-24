namespace OneAudio.Vorbis;

/// <summary>
/// Inverse MDCT of one block size. The N/2 spectral values are turned into N time samples through a DCT-IV, which is
/// computed with an N/4-point complex FFT (pre-twiddle, radix-2 FFT, post-twiddle). The output is not windowed.
/// All tables are built once; <see cref="Compute"/> allocates nothing.
/// </summary>
internal sealed class VorbisImdct
{
    private readonly int _n;
    private readonly int _m;
    private readonly int _h;
    private readonly float[] _preCos, _preSin, _postCos, _postSin;
    private readonly float[] _twCos, _twSin;
    private readonly int[] _bitrev;
    private readonly float[] _re, _im, _u;

    /// <summary>Creates the transform for a window of <paramref name="n"/> samples (a power of two, at least 64).</summary>
    public VorbisImdct(int n)
    {
        _n = n;
        _m = n / 2;
        _h = n / 4;
        int m = _m, h = _h;
        _preCos = new float[h]; _preSin = new float[h];
        _postCos = new float[h]; _postSin = new float[h];
        for (int j = 0; j < h; j++)
        {
            double a = Math.PI * j / m;
            _preCos[j] = (float)Math.Cos(a);
            _preSin[j] = (float)Math.Sin(a);
            double b = Math.PI * (j + 0.25) / m;
            _postCos[j] = (float)Math.Cos(b);
            _postSin[j] = (float)Math.Sin(b);
        }
        _twCos = new float[Math.Max(h / 2, 1)];
        _twSin = new float[Math.Max(h / 2, 1)];
        for (int k = 0; k < h / 2; k++)
        {
            double a = 2 * Math.PI * k / h;
            _twCos[k] = (float)Math.Cos(a);
            _twSin[k] = (float)-Math.Sin(a);
        }
        _bitrev = new int[h];
        int bits = System.Numerics.BitOperations.Log2((uint)h);
        for (int i = 0; i < h; i++)
        {
            int r = 0;
            for (int b = 0; b < bits; b++) if ((i & (1 << b)) != 0) r |= 1 << (bits - 1 - b);
            _bitrev[i] = r;
        }
        _re = new float[h]; _im = new float[h]; _u = new float[m];
    }

    /// <summary>
    /// y[n] = sum over k of X[k] cos(2 pi / N (n + 1/2 + N/4)(k + 1/2)), the inverse MDCT of Vorbis I
    /// (specification section 4.3.7), without normalisation: the window and the 50 % overlap already make the transform
    /// reconstruct unit gain, and this scaling matches the reference decoder. For n in 0..N-1 and k in 0..N/2-1.
    /// </summary>
    public void Compute(ReadOnlySpan<float> x, Span<float> y)
    {
        int m = _m, h = _h;
        var re = _re;
        var im = _im;
        var bitrev = _bitrev;
        for (int j = 0; j < h; j++)
        {
            float a = x[2 * j];
            float b = x[m - 1 - 2 * j];
            float c = _preCos[j], s = _preSin[j];
            int d = bitrev[j];
            re[d] = a * c + b * s;
            im[d] = b * c - a * s;
        }

        // Iterative radix-2 FFT, forward direction, input already in bit-reversed order.
        for (int len = 2; len <= h; len <<= 1)
        {
            int half = len >> 1;
            int step = h / len;
            for (int start = 0; start < h; start += len)
            {
                int tw = 0;
                for (int k = 0; k < half; k++, tw += step)
                {
                    float wr = _twCos[tw], wi = _twSin[tw];
                    int p = start + k, q = p + half;
                    float tr = re[q] * wr - im[q] * wi;
                    float ti = re[q] * wi + im[q] * wr;
                    re[q] = re[p] - tr;
                    im[q] = im[p] - ti;
                    re[p] += tr;
                    im[p] += ti;
                }
            }
        }

        var u = _u;
        for (int k = 0; k < h; k++)
        {
            float c = _postCos[k], s = _postSin[k];
            float rr = re[k] * c + im[k] * s;
            float ri = im[k] * c - re[k] * s;
            u[2 * k] = rr;
            u[m - 1 - 2 * k] = -ri;
        }

        // Unfold the DCT-IV output into the N-sample IMDCT (symmetries of the cosine kernel).
        int half2 = m / 2;
        for (int n = 0; n < half2; n++) y[n] = u[n + half2];
        for (int n = half2; n < half2 + m; n++) y[n] = -u[3 * half2 - 1 - n];
        for (int n = half2 + m; n < _n; n++) y[n] = -u[n - 3 * half2];
    }
}
