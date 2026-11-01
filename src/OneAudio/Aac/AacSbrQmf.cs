namespace OneAudio.Aac;

/// <summary>
/// The 32-band complex QMF analysis bank of SBR (ISO/IEC 14496-3 4.6.18.4.1). One call turns 32 core samples into one
/// time slot of 32 complex subband samples. The prototype is the 640-tap filter of AacSbrTables.QmfWindow stored as its
/// first 325 taps; the rest follows by symmetry.
/// </summary>
internal sealed class SbrQmfAnalysis
{
    private readonly float[] _state = new float[320];
    private readonly float[] _a = new float[64];
    private static readonly float[] Cos = BuildMatrix(false);
    private static readonly float[] Sin = BuildMatrix(true);

    private static float[] BuildMatrix(bool sine)
    {
        var m = new float[32 * 64];
        for (int k = 0; k < 32; k++)
            for (int n = 0; n < 64; n++)
            {
                // exp(i * pi/64 * (k + 1/2) * (2n - 1/2))
                double arg = Math.PI / 64.0 * (k + 0.5) * (2 * n - 0.5);
                m[k * 64 + n] = (float)(sine ? Math.Sin(arg) : Math.Cos(arg));
            }
        return m;
    }

    public void Reset() => Array.Clear(_state);

    /// <summary>Consumes 32 samples and writes the 32 complex subband samples of one time slot.</summary>
    public void Slot(ReadOnlySpan<float> input, Span<float> re, Span<float> im)
    {
        var s = _state;
        Array.Copy(s, 32, s, 0, 288);
        input.Slice(0, 32).CopyTo(s.AsSpan(288, 32));
        var w = AacSbrTables.QmfWindow;
        var a = _a;
        for (int k = 0; k < 32; k++)
        {
            float acc = 0;
            int b = 10 * k + 5;
            for (int p = 0; p < 5; p++) acc += w[b + p] * s[k + 64 * p];
            a[63 - k] = acc;
        }
        {
            float acc = 0;
            for (int p = 0; p < 5; p++) acc += w[320 + p] * s[63 + 64 * p];
            a[0] = acc;
        }
        for (int k = 0; k < 31; k++)
        {
            float acc = 0;
            int b = 309 - 10 * k;
            for (int p = 0; p < 5; p++) acc += w[b - p] * s[32 + k + 64 * p];
            a[31 - k] = acc;
        }
        for (int k = 0; k < 32; k++)
        {
            float sr = 0, si = 0;
            var c = Cos.AsSpan(k * 64, 64);
            var sn = Sin.AsSpan(k * 64, 64);
            for (int n = 0; n < 64; n++)
            {
                sr += a[n] * c[n];
                si += a[n] * sn[n];
            }
            re[k] = sr;
            im[k] = si;
        }
    }
}

/// <summary>
/// The complex QMF synthesis bank of SBR (4.6.18.4.2): 64 bands, or 32 bands in the downsampled mode where the output
/// stays at the core rate. One call turns one time slot of complex subband samples into 64 (or 32) output samples.
/// </summary>
internal sealed class SbrQmfSynthesis
{
    private readonly int _n;          // 64 or 32 bands
    private readonly float[] _state;  // 10 * n
    private readonly float[] _cos, _sin;
    private readonly float[] _tcos, _tsin; // 32-band twist
    private readonly float[] _re, _im, _c, _s, _rr, _ii;

    public SbrQmfSynthesis(int bands)
    {
        _n = bands;
        _state = new float[10 * bands];
        _cos = new float[bands * bands];
        _sin = new float[bands * bands];
        for (int n = 0; n < bands; n++)
            for (int k = 0; k < bands; k++)
            {
                double arg = Math.PI / bands * (n + 0.5) * (k + 0.5);
                _cos[n * bands + k] = (float)Math.Cos(arg);
                _sin[n * bands + k] = (float)Math.Sin(arg);
            }
        _tcos = new float[bands];
        _tsin = new float[bands];
        for (int k = 0; k < bands; k++)
        {
            double th = Math.PI * (2 * k + 1) / 256.0;
            _tcos[k] = (float)Math.Cos(th);
            _tsin[k] = (float)Math.Sin(th);
        }
        _re = new float[bands]; _im = new float[bands]; _c = new float[bands]; _s = new float[bands]; _rr = new float[bands]; _ii = new float[bands];
    }

    public void Reset() => Array.Clear(_state);

    /// <summary>
    /// Synthesizes one time slot. <paramref name="re"/> and <paramref name="im"/> hold <c>bands</c> values; bands from
    /// <paramref name="usb"/> up are treated as zero. Writes <c>bands</c> samples to <paramref name="output"/>.
    /// </summary>
    public void Slot(ReadOnlySpan<float> re, ReadOnlySpan<float> im, int usb, Span<float> output)
    {
        int n = _n;
        var xr = _re;
        var xi = _im;
        for (int k = 0; k < n; k++)
        {
            float r = k < usb ? re[k] : 0, i = k < usb ? im[k] : 0;
            if (n == 32)
            {
                float t = r;
                r = _tcos[k] * r + _tsin[k] * i;
                i = _tcos[k] * i - _tsin[k] * t;
            }
            xr[k] = -r;
            xi[k] = -i;
        }
        var c = _c; var s = _s;
        for (int m = 0; m < n; m++)
        {
            float sc = 0, ss = 0;
            int b = m * n;
            for (int k = 0; k < n; k++)
            {
                sc += _cos[b + k] * xr[k];
                ss += _sin[b + k] * xi[k];
            }
            c[m] = sc;
            s[m] = ss;
        }
        const float scale = 0.03125f;
        var rr = _rr; var ii = _ii;
        for (int m = 0; m < n; m++)
        {
            rr[m] = (c[m] - s[m]) * scale;
            ii[m] = -(c[n - 1 - m] + s[n - 1 - m]) * scale;
        }
        var w = AacSbrTables.QmfWindow;
        var st = _state;
        int stride = 2 * n;
        if (n == 64)
        {
            for (int j = 0; j < 64; j++)
            {
                float v = ii[63 - j];
                for (int p = 0; p < 5; p++) st[p * stride + j] += w[5 * j + p] * v;
            }
            float acc = 0;
            for (int p = 0; p < 5; p++) { acc = st[p * stride + 127] + w[320 + p] * rr[0]; st[p * stride + 127] = acc; }
            output[0] = acc;
            for (int j = 0; j < 63; j++)
            {
                float v = rr[63 - j];
                for (int p = 0; p < 5; p++) { acc = st[p * stride + 64 + j] + w[314 - 5 * j - p] * v; st[p * stride + 64 + j] = acc; }
                output[63 - j] = acc;
            }
        }
        else
        {
            for (int j = 0; j < 32; j++)
            {
                float v = ii[31 - j];
                for (int p = 0; p < 5; p++) st[p * stride + j] += w[10 * j + 5 + p] * v;
            }
            float acc = 0;
            for (int p = 0; p < 5; p++) { acc = st[p * stride + 63] + w[320 + p] * rr[0]; st[p * stride + 63] = acc; }
            output[0] = acc;
            for (int j = 0; j < 31; j++)
            {
                float v = rr[31 - j];
                for (int p = 0; p < 5; p++) { acc = st[p * stride + 32 + j] + w[309 - 10 * j - p] * v; st[p * stride + 32 + j] = acc; }
                output[31 - j] = acc;
            }
        }
        Array.Copy(st, 0, st, n, st.Length - n);
        Array.Clear(st, 0, n);
    }
}
