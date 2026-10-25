namespace OneAudio.Opus;

// Forward MDCT for the CELT encoder (RFC 6716 reference, clt_mdct_forward), sharing tables with the inverse.
internal sealed partial class CeltMdct
{
    // FFT scale factors of the reference mode (1/nfft, as printed in its static tables).
    private static readonly float[] FftScale = [0.002083333f, 0.004166667f, 0.008333333f, 0.016666667f];

    /// <summary>
    /// Windowed forward MDCT of one block. <paramref name="input"/> holds N + overlap samples starting at
    /// <paramref name="inOff"/>; N/2 coefficients are written to <paramref name="output"/> at
    /// <paramref name="outOff"/> with the given stride. <paramref name="f"/> and <paramref name="f2"/> are scratch
    /// of at least N/2 floats.
    /// </summary>
    public void Forward(float[] input, int inOff, float[] output, int outOff, ReadOnlySpan<float> window,
        int overlap, int shift, int stride, float[] f, float[] f2)
    {
        int n = N >> shift;
        int n2 = n >> 1;
        int n4 = n >> 2;
        float sine = 2f * 3.141592653f * 0.125f / n;
        var t = _trig;

        // Window, shuffle, fold: the input is treated as four blocks [a, b, c, d].
        {
            int xp1 = inOff + (overlap >> 1);
            int xp2 = inOff + n2 - 1 + (overlap >> 1);
            int yp = 0;
            int wp1 = overlap >> 1;
            int wp2 = (overlap >> 1) - 1;
            int i;
            for (i = 0; i < (overlap >> 2); i++)
            {
                f2[yp++] = window[wp2] * input[xp1 + n2] + window[wp1] * input[xp2];
                f2[yp++] = window[wp1] * input[xp1] - window[wp2] * input[xp2 - n2];
                xp1 += 2; xp2 -= 2; wp1 += 2; wp2 -= 2;
            }
            wp1 = 0;
            wp2 = overlap - 1;
            for (; i < n4 - (overlap >> 2); i++)
            {
                f2[yp++] = input[xp2];
                f2[yp++] = input[xp1];
                xp1 += 2; xp2 -= 2;
            }
            for (; i < n4; i++)
            {
                f2[yp++] = -(window[wp1] * input[xp1 - n2]) + window[wp2] * input[xp2];
                f2[yp++] = window[wp2] * input[xp1] + window[wp1] * input[xp2 + n2];
                xp1 += 2; xp2 -= 2; wp1 += 2; wp2 -= 2;
            }
        }

        // Pre-rotation
        {
            int yp = 0;
            for (int i = 0; i < n4; i++)
            {
                float re = f2[yp], im = f2[yp + 1];
                float yr = -(re * t[i << shift]) - im * t[(n4 - i) << shift];
                float yi = -(im * t[i << shift]) + re * t[(n4 - i) << shift];
                f2[yp++] = yr + yi * sine;
                f2[yp++] = yi - yr * sine;
            }
        }

        Fft(shift, f2, f);

        // Post-rotation
        {
            int fp = 0;
            int yp1 = outOff;
            int yp2 = outOff + stride * (n2 - 1);
            for (int i = 0; i < n4; i++)
            {
                float yr = f[fp + 1] * t[(n4 - i) << shift] + f[fp] * t[i << shift];
                float yi = f[fp] * t[(n4 - i) << shift] - f[fp + 1] * t[i << shift];
                output[yp1] = yr - yi * sine;
                output[yp2] = yi + yr * sine;
                fp += 2;
                yp1 += 2 * stride;
                yp2 -= 2 * stride;
            }
        }
    }

    private void Fft(int shiftIdx, float[] fin, float[] fout)
    {
        int nfft = _nfft[shiftIdx];
        var bitrev = _bitrev[shiftIdx];
        var factors = _factors[shiftIdx];
        float scale = FftScale[shiftIdx];
        Span<int> fstride = stackalloc int[9];
        for (int i = 0; i < nfft; i++)
        {
            int b = bitrev[i];
            fout[2 * b] = fin[2 * i] * scale;
            fout[2 * b + 1] = fin[2 * i + 1] * scale;
        }
        fstride[0] = 1;
        int l = 0, p, m;
        do
        {
            p = factors[2 * l];
            m = factors[2 * l + 1];
            fstride[l + 1] = fstride[l] * p;
            l++;
        } while (m != 1);
        m = factors[2 * l - 1];
        for (int i = l - 1; i >= 0; i--)
        {
            int m2 = i != 0 ? factors[2 * i - 1] : 1;
            int fs = fstride[i] << shiftIdx;
            switch (factors[2 * i])
            {
                case 2: FBfly2(fout, fs, m, fstride[i], m2); break;
                case 4: FBfly4(fout, fs, m, fstride[i], m2); break;
                case 3: FBfly3(fout, fs, m, fstride[i], m2); break;
                case 5: FBfly5(fout, fs, m, fstride[i], m2); break;
            }
            m = m2;
        }
    }

    private void FBfly2(float[] f, int fstride, int m, int n, int mm)
    {
        var tw = _twiddles;
        for (int i = 0; i < n; i++)
        {
            int o1 = 2 * (i * mm);
            int o2 = o1 + 2 * m;
            int t = 0;
            for (int j = 0; j < m; j++)
            {
                float wr = tw[t], wi = tw[t + 1];
                float xr = f[o2], xi = f[o2 + 1];
                float tr = xr * wr - xi * wi;
                float ti = xr * wi + xi * wr;
                t += 2 * fstride;
                f[o2] = f[o1] - tr;
                f[o2 + 1] = f[o1 + 1] - ti;
                f[o1] += tr;
                f[o1 + 1] += ti;
                o1 += 2;
                o2 += 2;
            }
        }
    }

    private void FBfly4(float[] f, int fstride, int m, int n, int mm)
    {
        var tw = _twiddles;
        int m2 = 2 * m, m3 = 3 * m;
        for (int i = 0; i < n; i++)
        {
            int o = 2 * (i * mm);
            int t1 = 0, t2 = 0, t3 = 0;
            for (int j = 0; j < m; j++)
            {
                int a1 = o + 2 * m, a2 = o + 2 * m2, a3 = o + 2 * m3;
                float w1r = tw[t1], w1i = tw[t1 + 1], w2r = tw[t2], w2i = tw[t2 + 1], w3r = tw[t3], w3i = tw[t3 + 1];
                float s0r = f[a1] * w1r - f[a1 + 1] * w1i, s0i = f[a1] * w1i + f[a1 + 1] * w1r;
                float s1r = f[a2] * w2r - f[a2 + 1] * w2i, s1i = f[a2] * w2i + f[a2 + 1] * w2r;
                float s2r = f[a3] * w3r - f[a3 + 1] * w3i, s2i = f[a3] * w3i + f[a3 + 1] * w3r;
                float s5r = f[o] - s1r, s5i = f[o + 1] - s1i;
                float fr = f[o] + s1r, fi = f[o + 1] + s1i;
                float s3r = s0r + s2r, s3i = s0i + s2i;
                float s4r = s0r - s2r, s4i = s0i - s2i;
                f[a2] = fr - s3r;
                f[a2 + 1] = fi - s3i;
                t1 += 2 * fstride;
                t2 += 4 * fstride;
                t3 += 6 * fstride;
                f[o] = fr + s3r;
                f[o + 1] = fi + s3i;
                f[a1] = s5r + s4i;
                f[a1 + 1] = s5i - s4r;
                f[a3] = s5r - s4i;
                f[a3 + 1] = s5i + s4r;
                o += 2;
            }
        }
    }

    private void FBfly3(float[] f, int fstride, int m, int n, int mm)
    {
        var tw = _twiddles;
        int m2 = 2 * m;
        float epi3i = tw[2 * fstride * m + 1];
        for (int i = 0; i < n; i++)
        {
            int o = 2 * (i * mm);
            int t1 = 0, t2 = 0;
            for (int k = 0; k < m; k++)
            {
                int a1 = o + 2 * m, a2 = o + 2 * m2;
                float w1r = tw[t1], w1i = tw[t1 + 1], w2r = tw[t2], w2i = tw[t2 + 1];
                float s1r = f[a1] * w1r - f[a1 + 1] * w1i, s1i = f[a1] * w1i + f[a1 + 1] * w1r;
                float s2r = f[a2] * w2r - f[a2 + 1] * w2i, s2i = f[a2] * w2i + f[a2 + 1] * w2r;
                float s3r = s1r + s2r, s3i = s1i + s2i;
                float s0r = s1r - s2r, s0i = s1i - s2i;
                t1 += 2 * fstride;
                t2 += 4 * fstride;
                f[a1] = f[o] - s3r * 0.5f;
                f[a1 + 1] = f[o + 1] - s3i * 0.5f;
                s0r *= epi3i;
                s0i *= epi3i;
                f[o] += s3r;
                f[o + 1] += s3i;
                f[a2] = f[a1] + s0i;
                f[a2 + 1] = f[a1 + 1] - s0r;
                f[a1] -= s0i;
                f[a1 + 1] += s0r;
                o += 2;
            }
        }
    }

    private void FBfly5(float[] f, int fstride, int m, int n, int mm)
    {
        var tw = _twiddles;
        float yar = tw[2 * fstride * m], yai = tw[2 * fstride * m + 1];
        float ybr = tw[2 * fstride * 2 * m], ybi = tw[2 * fstride * 2 * m + 1];
        for (int i = 0; i < n; i++)
        {
            int o0 = 2 * (i * mm);
            for (int u = 0; u < m; u++)
            {
                int o1 = o0 + 2 * m, o2 = o0 + 4 * m, o3 = o0 + 6 * m, o4 = o0 + 8 * m;
                float s0r = f[o0], s0i = f[o0 + 1];
                int t1 = 2 * u * fstride, t2 = 2 * (2 * u * fstride), t3 = 2 * (3 * u * fstride), t4 = 2 * (4 * u * fstride);
                float s1r = f[o1] * tw[t1] - f[o1 + 1] * tw[t1 + 1], s1i = f[o1] * tw[t1 + 1] + f[o1 + 1] * tw[t1];
                float s2r = f[o2] * tw[t2] - f[o2 + 1] * tw[t2 + 1], s2i = f[o2] * tw[t2 + 1] + f[o2 + 1] * tw[t2];
                float s3r = f[o3] * tw[t3] - f[o3 + 1] * tw[t3 + 1], s3i = f[o3] * tw[t3 + 1] + f[o3 + 1] * tw[t3];
                float s4r = f[o4] * tw[t4] - f[o4 + 1] * tw[t4 + 1], s4i = f[o4] * tw[t4 + 1] + f[o4 + 1] * tw[t4];

                float s7r = s1r + s4r, s7i = s1i + s4i;
                float s10r = s1r - s4r, s10i = s1i - s4i;
                float s8r = s2r + s3r, s8i = s2i + s3i;
                float s9r = s2r - s3r, s9i = s2i - s3i;

                f[o0] += s7r + s8r;
                f[o0 + 1] += s7i + s8i;

                float s5r = s0r + s7r * yar + s8r * ybr;
                float s5i = s0i + s7i * yar + s8i * ybr;
                float s6r = s10i * yai + s9i * ybi;
                float s6i = -(s10r * yai) - s9r * ybi;
                f[o1] = s5r - s6r;
                f[o1 + 1] = s5i - s6i;
                f[o4] = s5r + s6r;
                f[o4 + 1] = s5i + s6i;

                float s11r = s0r + s7r * ybr + s8r * yar;
                float s11i = s0i + s7i * ybr + s8i * yar;
                float s12r = -(s10i * ybi) + s9i * yai;
                float s12i = s10r * ybi - s9r * yai;
                f[o2] = s11r + s12r;
                f[o2 + 1] = s11i + s12i;
                f[o3] = s11r - s12r;
                f[o3 + 1] = s11i - s12i;

                o0 += 2;
            }
        }
    }
}
