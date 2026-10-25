namespace OneAudio.Opus;

/// <summary>
/// Inverse MDCT for CELT built on a mixed-radix (2, 3, 4, 5) complex FFT, following the reference decoder in
/// RFC 6716 (Simplified BSD licence, see THIRD-PARTY-NOTICES.md). Sizes are fixed by the 48 kHz CELT mode: a
/// 1920-point MDCT for 20 ms frames and its 1/2, 1/4 and 1/8 divisions for shorter frames.
/// </summary>
internal sealed partial class CeltMdct
{
    private const int N = 1920;
    private const int MaxShift = 3;

    /// <summary>The shared immutable tables.</summary>
    public static readonly CeltMdct Instance = new();

    internal readonly float[] _twiddles; // interleaved cos, sin of -2 pi i / 480
    internal readonly float[] _trig;     // cos(2 pi i / N), i in [0, N/4]
    internal readonly short[][] _bitrev = new short[MaxShift + 1][];
    internal readonly short[][] _factors = new short[MaxShift + 1][];
    internal readonly int[] _nfft = new int[MaxShift + 1];

    private CeltMdct()
    {
        int baseN = N >> 2;
        _twiddles = CeltMdctTables.RefTwiddles;
        _trig = CeltMdctTables.RefTrig;
        for (int s = 0; s <= MaxShift; s++)
        {
            int nfft = baseN >> s;
            _nfft[s] = nfft;
            var factors = new short[16];
            Factor(nfft, factors);
            _factors[s] = factors;
            var bitrev = new short[nfft];
            ComputeBitrev(0, bitrev, 0, 1, 1, factors, 0);
            _bitrev[s] = bitrev;
        }
    }

    private static void Factor(int n, short[] facbuf)
    {
        int p = 4, k = 0;
        do
        {
            while (n % p != 0)
            {
                switch (p)
                {
                    case 4: p = 2; break;
                    case 2: p = 3; break;
                    default: p += 2; break;
                }
                if (p > 32000 || p * p > n) p = n;
            }
            n /= p;
            facbuf[k++] = (short)p;
            facbuf[k++] = (short)n;
        } while (n > 1);
    }

    // Depth is bounded by the number of prime factors of the fixed FFT size, not by input.
    private static void ComputeBitrev(int fout, short[] f, int fIdx, int fstride, int inStride, short[] factors, int fac)
    {
        int p = factors[fac];
        int m = factors[fac + 1];
        if (m == 1)
        {
            for (int j = 0; j < p; j++)
            {
                f[fIdx] = (short)(fout + j);
                fIdx += fstride * inStride;
            }
        }
        else
        {
            for (int j = 0; j < p; j++)
            {
                ComputeBitrev(fout, f, fIdx, fstride * p, inStride, factors, fac + 2);
                fIdx += fstride * inStride;
                fout += m;
            }
        }
    }

    /// <summary>Unscaled inverse FFT of <c>nfft</c> complex values (interleaved re, im) for the given shift.</summary>
    private void Ifft(int shiftIdx, float[] fin, float[] fout)
    {
        int nfft = _nfft[shiftIdx];
        var bitrev = _bitrev[shiftIdx];
        var factors = _factors[shiftIdx];
        Span<int> fstride = stackalloc int[9];
        for (int i = 0; i < nfft; i++)
        {
            int b = bitrev[i];
            fout[2 * b] = fin[2 * i];
            fout[2 * b + 1] = fin[2 * i + 1];
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
        int shift = shiftIdx; // twiddles are shared; smaller transforms stride through them
        for (int i = l - 1; i >= 0; i--)
        {
            int m2 = i != 0 ? factors[2 * i - 1] : 1;
            int fs = fstride[i] << shift;
            switch (factors[2 * i])
            {
                case 2: Bfly2(fout, fs, m, fstride[i], m2); break;
                case 4: Bfly4(fout, fs, m, fstride[i], m2); break;
                case 3: Bfly3(fout, fs, m, fstride[i], m2); break;
                case 5: Bfly5(fout, fs, m, fstride[i], m2); break;
            }
            m = m2;
        }
    }

    private void Bfly2(float[] f, int fstride, int m, int n, int mm)
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
                float tr = xr * wr + xi * wi;
                float ti = xi * wr - xr * wi;
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

    private void Bfly4(float[] f, int fstride, int m, int n, int mm)
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
                float s0r = f[a1] * w1r + f[a1 + 1] * w1i, s0i = f[a1 + 1] * w1r - f[a1] * w1i;
                float s1r = f[a2] * w2r + f[a2 + 1] * w2i, s1i = f[a2 + 1] * w2r - f[a2] * w2i;
                float s2r = f[a3] * w3r + f[a3 + 1] * w3i, s2i = f[a3 + 1] * w3r - f[a3] * w3i;
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
                f[a1] = s5r - s4i;
                f[a1 + 1] = s5i + s4r;
                f[a3] = s5r + s4i;
                f[a3 + 1] = s5i - s4r;
                o += 2;
            }
        }
    }

    private void Bfly3(float[] f, int fstride, int m, int n, int mm)
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
                float s1r = f[a1] * w1r + f[a1 + 1] * w1i, s1i = f[a1 + 1] * w1r - f[a1] * w1i;
                float s2r = f[a2] * w2r + f[a2 + 1] * w2i, s2i = f[a2 + 1] * w2r - f[a2] * w2i;
                float s3r = s1r + s2r, s3i = s1i + s2i;
                float s0r = s1r - s2r, s0i = s1i - s2i;
                t1 += 2 * fstride;
                t2 += 4 * fstride;
                f[a1] = f[o] - s3r * 0.5f;
                f[a1 + 1] = f[o + 1] - s3i * 0.5f;
                s0r *= -epi3i;
                s0i *= -epi3i;
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

    private void Bfly5(float[] f, int fstride, int m, int n, int mm)
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
                float s1r = f[o1] * tw[t1] + f[o1 + 1] * tw[t1 + 1], s1i = f[o1 + 1] * tw[t1] - f[o1] * tw[t1 + 1];
                float s2r = f[o2] * tw[t2] + f[o2 + 1] * tw[t2 + 1], s2i = f[o2 + 1] * tw[t2] - f[o2] * tw[t2 + 1];
                float s3r = f[o3] * tw[t3] + f[o3 + 1] * tw[t3 + 1], s3i = f[o3 + 1] * tw[t3] - f[o3] * tw[t3 + 1];
                float s4r = f[o4] * tw[t4] + f[o4 + 1] * tw[t4 + 1], s4i = f[o4 + 1] * tw[t4] - f[o4] * tw[t4 + 1];

                float s7r = s1r + s4r, s7i = s1i + s4i;
                float s10r = s1r - s4r, s10i = s1i - s4i;
                float s8r = s2r + s3r, s8i = s2i + s3i;
                float s9r = s2r - s3r, s9i = s2i - s3i;

                f[o0] += s7r + s8r;
                f[o0 + 1] += s7i + s8i;

                float s5r = s0r + s7r * yar + s8r * ybr;
                float s5i = s0i + s7i * yar + s8i * ybr;
                float s6r = -s10i * yai - s9i * ybi;
                float s6i = s10r * yai + s9r * ybi;
                f[o1] = s5r - s6r;
                f[o1 + 1] = s5i - s6i;
                f[o4] = s5r + s6r;
                f[o4 + 1] = s5i + s6i;

                float s11r = s0r + s7r * ybr + s8r * yar;
                float s11i = s0i + s7i * ybr + s8i * yar;
                float s12r = s10i * ybi - s9i * yai;
                float s12i = -s10r * ybi + s9r * yai;
                f[o2] = s11r + s12r;
                f[o2 + 1] = s11i + s12i;
                f[o3] = s11r - s12r;
                f[o3 + 1] = s11i - s12i;

                o0 += 2;
            }
        }
    }

    /// <summary>
    /// Backward MDCT with windowed overlap-add (clt_mdct_backward). <paramref name="input"/> holds
    /// <c>N/2</c> coefficients spaced <paramref name="stride"/> apart, and the result is accumulated into
    /// <paramref name="output"/> starting at <paramref name="outOff"/> (which may sit at the front of the
    /// overlap region, hence the negative offsets used internally).
    /// </summary>
    public void Backward(float[] input, int inOff, float[] output, int outOff, ReadOnlySpan<float> window,
        int overlap, int shift, int stride, float[] f, float[] f2)
    {
        int n = N >> shift;
        int n2 = n >> 1;
        int n4 = n >> 2;
        float sine = 2f * 3.141592653f * 0.125f / n;
        var t = _trig;

        // Pre-rotation
        {
            int xp1 = inOff;
            int xp2 = inOff + stride * (n2 - 1);
            int yp = 0;
            for (int i = 0; i < n4; i++)
            {
                float a = input[xp2], b = input[xp1];
                float yr = -a * t[i << shift] + b * t[(n4 - i) << shift];
                float yi = -a * t[(n4 - i) << shift] - b * t[i << shift];
                f2[yp++] = yr - yi * sine;
                f2[yp++] = yi + yr * sine;
                xp1 += 2 * stride;
                xp2 -= 2 * stride;
            }
        }

        Ifft(shift, f2, f);

        // Post-rotation
        {
            int fp = 0;
            for (int i = 0; i < n4; i++)
            {
                float re = f[fp], im = f[fp + 1];
                float yr = re * t[i << shift] - im * t[(n4 - i) << shift];
                float yi = im * t[i << shift] + re * t[(n4 - i) << shift];
                f[fp++] = yr - yi * sine;
                f[fp++] = yi + yr * sine;
            }
        }

        // De-interleave
        {
            int fp1 = 0, fp2 = n2 - 1, yp = 0;
            for (int i = 0; i < n4; i++)
            {
                f2[yp++] = -f[fp1];
                f2[yp++] = f[fp2];
                fp1 += 2;
                fp2 -= 2;
            }
        }

        // Windowed overlap-add into the output
        int o = outOff - ((n2 - overlap) >> 1);
        int half = overlap / 2;
        {
            int fp1 = n4 - 1;
            int xp1 = o + n2 - 1;
            int yp1 = o + n4 - half;
            int wp1 = 0, wp2 = overlap - 1;
            int i;
            for (i = 0; i < n4 - half; i++)
                output[xp1--] = f2[fp1--];
            for (; i < n4; i++)
            {
                float x1 = f2[fp1--];
                output[yp1++] += -window[wp1] * x1;
                output[xp1--] += window[wp2] * x1;
                wp1++;
                wp2--;
            }
        }
        {
            int fp2 = n4;
            int xp2 = o + n2;
            int yp2 = o + n - 1 - (n4 - half);
            int wp1 = 0, wp2 = overlap - 1;
            int i;
            for (i = 0; i < n4 - half; i++)
                output[xp2++] = f2[fp2++];
            for (; i < n4; i++)
            {
                float x2 = f2[fp2++];
                output[yp2--] = window[wp1] * x2;
                output[xp2++] = window[wp2] * x2;
                wp1++;
                wp2--;
            }
        }
    }
}
