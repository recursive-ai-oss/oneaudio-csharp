namespace OneAudio.Aac;

/// <summary>
/// Forward MDCT of one AAC block (ISO/IEC 14496-3 4.6.11.3.1, the inverse of what the decoder computes) for M spectral lines from
/// 2M windowed samples, <c>X[k] = 2 * sum_n z[n] cos(2 pi / N (n + n0)(k + 1/2))</c> with N = 2M and n0 = (M + 1)/2. The factor 2
/// makes windowed overlap-add of the decoder's 1/M-scaled inverse reconstruct the input. It folds the block into a DCT-IV and
/// computes that with an M/2-point complex FFT.
/// </summary>
internal sealed class AacEncMdct
{
    private readonly int _m, _half;
    private readonly float[] _preRe, _preIm, _postRe, _postIm, _fftRe, _fftIm;
    private readonly int[] _rev;
    private readonly float[] _re, _im, _u;

    public AacEncMdct(int m)
    {
        _m = m;
        _half = m / 2;
        _preRe = new float[_half];
        _preIm = new float[_half];
        _postRe = new float[_half];
        _postIm = new float[_half];
        for (int n = 0; n < _half; n++)
        {
            double a = -Math.PI * n / m;
            _preRe[n] = (float)Math.Cos(a);
            _preIm[n] = (float)Math.Sin(a);
            double b = -Math.PI * (n + 0.25) / m;
            _postRe[n] = (float)(2 * Math.Cos(b)); // the MDCT's factor 2 is folded in here
            _postIm[n] = (float)(2 * Math.Sin(b));
        }
        _fftRe = new float[_half / 2];
        _fftIm = new float[_half / 2];
        for (int k = 0; k < _half / 2; k++)
        {
            double a = -2.0 * Math.PI * k / _half;
            _fftRe[k] = (float)Math.Cos(a);
            _fftIm[k] = (float)Math.Sin(a);
        }
        _rev = new int[_half];
        int bits = System.Numerics.BitOperations.Log2((uint)_half);
        for (int i = 0; i < _half; i++)
        {
            int r = 0;
            for (int b = 0; b < bits; b++)
                if ((i & (1 << b)) != 0) r |= 1 << (bits - 1 - b);
            _rev[i] = r;
        }
        _re = new float[_half];
        _im = new float[_half];
        _u = new float[m];
    }

    /// <summary>Transforms <c>2M</c> windowed samples into M lines.</summary>
    public void Forward(ReadOnlySpan<float> x, Span<float> spectrum)
    {
        int m = _m, h = _half, m32 = 3 * m / 2;
        var u = _u;
        // Fold (n0 = M/2 + 1/2): the block (a b c d) of quarter-lengths becomes (-c_r - d, a - b_r).
        for (int j = 0; j < h; j++) u[j] = -x[m32 - 1 - j] - x[m32 + j];
        for (int j = h; j < m; j++) u[j] = x[j - h] - x[m32 - 1 - j];
        // DCT-IV: c[n] = u[2n] + i u[M-1-2n], FFT of c[n] e^{-i pi n / M}, then e^{-i pi (k + 1/4) / M}.
        for (int n = 0; n < h; n++)
        {
            float a = u[2 * n], b = u[m - 1 - 2 * n];
            int r = _rev[n];
            _re[r] = a * _preRe[n] - b * _preIm[n];
            _im[r] = a * _preIm[n] + b * _preRe[n];
        }
        Fft();
        for (int k = 0; k < h; k++)
        {
            float yr = _re[k] * _postRe[k] - _im[k] * _postIm[k];
            float yi = _re[k] * _postIm[k] + _im[k] * _postRe[k];
            spectrum[2 * k] = yr;
            spectrum[m - 1 - 2 * k] = -yi;
        }
    }

    // In-place radix-2 FFT on bit-reversed input.
    private void Fft()
    {
        int n = _half;
        var re = _re;
        var im = _im;
        for (int size = 2; size <= n; size <<= 1)
        {
            int half = size >> 1, step = n / size;
            for (int start = 0; start < n; start += size)
            {
                for (int j = 0, t = 0; j < half; j++, t += step)
                {
                    float wr = _fftRe[t], wi = _fftIm[t];
                    int p = start + j, q = p + half;
                    float xr = re[q] * wr - im[q] * wi;
                    float xi = re[q] * wi + im[q] * wr;
                    re[q] = re[p] - xr;
                    im[q] = im[p] - xi;
                    re[p] += xr;
                    im[p] += xi;
                }
            }
        }
    }
}
