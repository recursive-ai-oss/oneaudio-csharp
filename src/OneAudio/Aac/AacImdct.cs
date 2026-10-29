namespace OneAudio.Aac;

/// <summary>
/// Inverse MDCT of one block (ISO/IEC 14496-3 4.6.11.3.1) through a DCT-IV that is computed with an M/2-point complex FFT.
/// For M spectral lines the output has 2M samples:
/// <c>x[n] = (1/M) * sum_k X[k] * cos(pi/M * (n + 1/2 + M/2) * (k + 1/2))</c>, which equals the standard's
/// <c>(2/N) sum X[k] cos(2 pi/N (n + n0)(k + 1/2))</c> with N = 2M and n0 = (M + 1)/2.
/// The windowing and overlap-add are done by the caller.
/// </summary>
internal sealed class AacImdct
{
    private readonly int _m;
    private readonly int _half;       // M/2, the FFT size
    private readonly float[] _twRe;   // exp(-i*pi*(j+1/8)/M) for j < M/2
    private readonly float[] _twIm;
    private readonly float[] _fftRe;  // FFT twiddles exp(-2 pi i k / (M/2)), k < M/4
    private readonly float[] _fftIm;
    private readonly int[] _rev;
    private readonly float[] _re;
    private readonly float[] _im;
    private readonly float[] _u;
    private readonly float _scale;

    public AacImdct(int m)
    {
        _m = m;
        _half = m / 2;
        _twRe = new float[_half];
        _twIm = new float[_half];
        for (int j = 0; j < _half; j++)
        {
            double a = -Math.PI * (8 * j + 1) / (8.0 * m); // the extra 1/4 step of the DCT-IV phase is split between pre and post twiddle
            _twRe[j] = (float)Math.Cos(a);
            _twIm[j] = (float)Math.Sin(a);
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
            for (int b = 0; b < bits; b++) if ((i & (1 << b)) != 0) r |= 1 << (bits - 1 - b);
            _rev[i] = r;
        }
        _re = new float[_half];
        _im = new float[_half];
        _u = new float[m];
        _scale = 1f / m;
    }

    /// <summary>Computes the 2M output samples (including the 1/M scale) for M spectral lines.</summary>
    public void Inverse(ReadOnlySpan<float> spectrum, Span<float> output)
    {
        int m = _m;
        Dct4(spectrum.Slice(0, m));
        var u = _u;
        int h = m / 2;
        float s = _scale;
        for (int n = 0; n < h; n++) output[n] = u[n + h] * s;
        for (int n = h; n < h + m; n++) output[n] = -u[3 * h - 1 - n] * s;
        for (int n = h + m; n < 2 * m; n++) output[n] = -u[n - 3 * h] * s;
    }

    // u[k] = sum_n x[n] cos(pi/M (n + 1/2)(k + 1/2)), via a complex FFT of half the length.
    private void Dct4(ReadOnlySpan<float> x)
    {
        int m = _m, half = _half;
        var re = _re;
        var im = _im;
        for (int j = 0; j < half; j++)
        {
            float a = x[2 * j], b = x[m - 1 - 2 * j];
            int r = _rev[j];
            re[r] = a * _twRe[j] - b * _twIm[j];
            im[r] = a * _twIm[j] + b * _twRe[j];
        }
        Fft(re, im);
        var u = _u;
        for (int k = 0; k < half; k++)
        {
            float yr = re[k] * _twRe[k] - im[k] * _twIm[k];
            float yi = re[k] * _twIm[k] + im[k] * _twRe[k];
            u[2 * k] = yr;
            u[m - 1 - 2 * k] = -yi;
        }
    }

    // In-place radix-2 decimation-in-time FFT on bit-reversed input.
    private void Fft(float[] re, float[] im)
    {
        int n = _half;
        for (int size = 2; size <= n; size <<= 1)
        {
            int halfSize = size >> 1;
            int step = n / size;
            for (int start = 0; start < n; start += size)
            {
                for (int k = 0; k < halfSize; k++)
                {
                    float wr = _fftRe[k * step], wi = _fftIm[k * step];
                    int i = start + k, j = i + halfSize;
                    float tr = re[j] * wr - im[j] * wi;
                    float ti = re[j] * wi + im[j] * wr;
                    re[j] = re[i] - tr;
                    im[j] = im[i] - ti;
                    re[i] += tr;
                    im[i] += ti;
                }
            }
        }
    }

    /// <summary>Reference implementation of the same transform, O(M^2), used by tests.</summary>
    internal static void InverseDirect(ReadOnlySpan<float> spectrum, Span<float> output, int m)
    {
        for (int n = 0; n < 2 * m; n++)
        {
            double sum = 0;
            for (int k = 0; k < m; k++)
                sum += spectrum[k] * Math.Cos(Math.PI / m * (n + 0.5 + m / 2.0) * (k + 0.5));
            output[n] = (float)(sum / m);
        }
    }
}
