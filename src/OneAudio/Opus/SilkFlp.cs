namespace OneAudio.Opus;

/// <summary>
/// Floating-point signal-processing helpers of the SILK encoder. Operation order and the float/double mix follow the
/// reference exactly because the encoder's decisions (and therefore its output bytes) depend on them.
/// </summary>
internal static class SilkFlp
{
    public const float Pi = 3.1415926536f;

    public static float Sigmoid(float x) => (float)(1.0 / (1.0 + Math.Exp(-x)));

    public static float Log2(double x) => (float)(3.32192809488736 * Math.Log10(x));

    public static int Float2Int(float x) => (int)Math.Floor(0.5 + x);

    public static int Float2Int(double x) => (int)Math.Floor(0.5 + x);

    public static double InnerProduct(ReadOnlySpan<float> d1, ReadOnlySpan<float> d2, int size)
    {
        double result = 0.0;
        int size4 = size & 0xFFFC;
        int i;
        for (i = 0; i < size4; i += 4)
        {
            result += d1[i] * (double)d2[i] + d1[i + 1] * (double)d2[i + 1] + d1[i + 2] * (double)d2[i + 2] + d1[i + 3] * (double)d2[i + 3];
        }
        for (; i < size; i++) result += d1[i] * (double)d2[i];
        return result;
    }

    public static double Energy(ReadOnlySpan<float> d, int size)
    {
        double result = 0.0;
        int size4 = size & 0xFFFC;
        int i;
        for (i = 0; i < size4; i += 4)
        {
            result += d[i] * (double)d[i] + d[i + 1] * (double)d[i + 1] + d[i + 2] * (double)d[i + 2] + d[i + 3] * (double)d[i + 3];
        }
        for (; i < size; i++) result += d[i] * (double)d[i];
        return result;
    }

    public static void Autocorrelation(Span<float> results, ReadOnlySpan<float> input, int inputSize, int correlationCount)
    {
        if (correlationCount > inputSize) correlationCount = inputSize;
        for (int i = 0; i < correlationCount; i++)
            results[i] = (float)InnerProduct(input, input.Slice(i), inputSize - i);
    }

    public static void BwExpander(Span<float> ar, int d, float chirp)
    {
        float cfac = chirp;
        for (int i = 0; i < d - 1; i++)
        {
            ar[i] *= cfac;
            cfac *= chirp;
        }
        ar[d - 1] *= cfac;
    }

    public static void K2A(Span<float> a, ReadOnlySpan<float> rc, int order)
    {
        Span<float> atmp = stackalloc float[24];
        for (int k = 0; k < order; k++)
        {
            for (int n = 0; n < k; n++) atmp[n] = a[n];
            for (int n = 0; n < k; n++) a[n] += atmp[k - n - 1] * rc[k];
            a[k] = -rc[k];
        }
    }

    public static float LevinsonDurbin(Span<float> a, ReadOnlySpan<float> corr, int order)
    {
        float minNrg = 1e-12f * corr[0] + 1e-9f;
        float nrg = corr[0];
        nrg = minNrg > nrg ? minNrg : nrg;
        a[0] = corr[1] / nrg;
        nrg -= a[0] * corr[1];
        nrg = minNrg > nrg ? minNrg : nrg;
        for (int m = 1; m < order; m++)
        {
            float t = corr[m + 1];
            for (int i = 0; i < m; i++) t -= a[i] * corr[m - i];
            float km = t / nrg;
            nrg -= km * t;
            nrg = minNrg > nrg ? minNrg : nrg;
            int mHalf = m >> 1;
            for (int i = 0; i < mHalf; i++)
            {
                float atmp1 = a[i];
                float atmp2 = a[m - i - 1];
                a[m - i - 1] -= km * atmp1;
                a[i] -= km * atmp2;
            }
            if ((m & 1) != 0) a[mHalf] -= km * a[mHalf];
            a[m] = km;
        }
        return nrg;
    }

    public static float Schur(Span<float> reflCoef, ReadOnlySpan<float> autoCorr, int order)
    {
        Span<float> c0 = stackalloc float[17];
        Span<float> c1 = stackalloc float[17];
        for (int k = 0; k < order + 1; k++) c0[k] = c1[k] = autoCorr[k];
        for (int k = 0; k < order; k++)
        {
            float den = c1[0] > 1e-9f ? c1[0] : 1e-9f;
            float rcTmp = -c0[k + 1] / den;
            reflCoef[k] = rcTmp;
            for (int n = 0; n < order - k; n++)
            {
                float ctmp1 = c0[n + k + 1];
                float ctmp2 = c1[n];
                c0[n + k + 1] = ctmp1 + ctmp2 * rcTmp;
                c1[n] = ctmp2 + ctmp1 * rcTmp;
            }
        }
        return c1[0];
    }

    public static void ScaleVector(Span<float> data, float gain, int size)
    {
        for (int i = 0; i < size; i++) data[i] *= gain;
    }

    public static void ScaleCopyVector(Span<float> dataOut, ReadOnlySpan<float> dataIn, float gain, int size)
    {
        for (int i = 0; i < size; i++) dataOut[i] = gain * dataIn[i];
    }

    /// <summary>silk_LPC_analysis_filter_FLP: whitening filter; the first <paramref name="order"/> outputs are zero.</summary>
    public static void LpcAnalysisFilter(Span<float> rLpc, ReadOnlySpan<float> predCoef, ReadOnlySpan<float> s, int length, int order)
    {
        for (int ix = order; ix < length; ix++)
        {
            int p = ix - 1;
            float lpcPred = s[p] * predCoef[0];
            for (int j = 1; j < order; j++) lpcPred += s[p - j] * predCoef[j];
            rLpc[ix] = s[p + 1] - lpcPred;
        }
        rLpc.Slice(0, order).Clear();
    }

    public static void ApplySineWindow(Span<float> pxWin, ReadOnlySpan<float> px, int winType, int length)
    {
        float freq = Pi / (length + 1);
        float c = 2.0f - freq * freq;
        float s0, s1;
        if (winType < 2)
        {
            s0 = 0.0f;
            s1 = freq;
        }
        else
        {
            s0 = 1.0f;
            s1 = 0.5f * c;
        }
        for (int k = 0; k < length; k += 4)
        {
            pxWin[k] = px[k] * 0.5f * (s0 + s1);
            pxWin[k + 1] = px[k + 1] * s1;
            s0 = c * s1 - s0;
            pxWin[k + 2] = px[k + 2] * 0.5f * (s1 + s0);
            pxWin[k + 3] = px[k + 3] * s0;
            s1 = c * s0 - s1;
        }
    }

    public static void InsertionSortDecreasing(Span<float> a, Span<int> idx, int L, int K)
    {
        for (int i = 0; i < K; i++) idx[i] = i;
        for (int i = 1; i < K; i++)
        {
            float value = a[i];
            int j;
            for (j = i - 1; j >= 0 && value > a[j]; j--)
            {
                a[j + 1] = a[j];
                idx[j + 1] = idx[j];
            }
            a[j + 1] = value;
            idx[j + 1] = i;
        }
        for (int i = K; i < L; i++)
        {
            float value = a[i];
            if (value > a[K - 1])
            {
                int j;
                for (j = K - 2; j >= 0 && value > a[j]; j--)
                {
                    a[j + 1] = a[j];
                    idx[j + 1] = idx[j];
                }
                a[j + 1] = value;
                idx[j + 1] = i;
            }
        }
    }

    /// <summary>silk_burg_modified_FLP: Burg's method over several subframes with a prediction-gain limit; returns the residual energy.</summary>
    public static float BurgModified(Span<float> a, ReadOnlySpan<float> x, float minInvGain, int subfrLength, int nbSubfr, int d)
    {
        const float condFac = 1e-5f;
        Span<double> cFirstRow = stackalloc double[16];
        Span<double> cLastRow = stackalloc double[16];
        Span<double> caf = stackalloc double[17];
        Span<double> cab = stackalloc double[17];
        Span<double> af = stackalloc double[16];
        cFirstRow.Clear(); caf.Clear(); cab.Clear(); af.Clear();

        double c0 = Energy(x, nbSubfr * subfrLength);
        for (int s = 0; s < nbSubfr; s++)
        {
            var xp = x.Slice(s * subfrLength);
            for (int n = 1; n < d + 1; n++) cFirstRow[n - 1] += InnerProduct(xp, xp.Slice(n), subfrLength - n);
        }
        cFirstRow.CopyTo(cLastRow);

        cab[0] = caf[0] = c0 + condFac * c0 + 1e-9f;
        double invGain = 1.0f;
        bool reachedMaxGain = false;
        double tmp1, tmp2, atmp, rc, num, nrgF, nrgB;
        for (int n = 0; n < d; n++)
        {
            for (int s = 0; s < nbSubfr; s++)
            {
                var xp = x.Slice(s * subfrLength);
                tmp1 = xp[n];
                tmp2 = xp[subfrLength - n - 1];
                for (int k = 0; k < n; k++)
                {
                    cFirstRow[k] -= (float)(xp[n] * xp[n - k - 1]);
                    cLastRow[k] -= (float)(xp[subfrLength - n - 1] * xp[subfrLength - n + k]);
                    atmp = af[k];
                    tmp1 += xp[n - k - 1] * atmp;
                    tmp2 += xp[subfrLength - n + k] * atmp;
                }
                for (int k = 0; k <= n; k++)
                {
                    caf[k] -= tmp1 * xp[n - k];
                    cab[k] -= tmp2 * xp[subfrLength - n + k - 1];
                }
            }
            tmp1 = cFirstRow[n];
            tmp2 = cLastRow[n];
            for (int k = 0; k < n; k++)
            {
                atmp = af[k];
                tmp1 += cLastRow[n - k - 1] * atmp;
                tmp2 += cFirstRow[n - k - 1] * atmp;
            }
            caf[n + 1] = tmp1;
            cab[n + 1] = tmp2;

            num = cab[n + 1];
            nrgB = cab[0];
            nrgF = caf[0];
            for (int k = 0; k < n; k++)
            {
                atmp = af[k];
                num += cab[n - k] * atmp;
                nrgB += cab[k + 1] * atmp;
                nrgF += caf[k + 1] * atmp;
            }

            rc = -2.0 * num / (nrgF + nrgB);

            tmp1 = invGain * (1.0 - rc * rc);
            if (tmp1 <= minInvGain)
            {
                rc = Math.Sqrt(1.0 - minInvGain / invGain);
                if (num > 0) rc = -rc;
                invGain = minInvGain;
                reachedMaxGain = true;
            }
            else invGain = tmp1;

            for (int k = 0; k < (n + 1) >> 1; k++)
            {
                tmp1 = af[k];
                tmp2 = af[n - k - 1];
                af[k] = tmp1 + rc * tmp2;
                af[n - k - 1] = tmp2 + rc * tmp1;
            }
            af[n] = rc;

            if (reachedMaxGain)
            {
                for (int k = n + 1; k < d; k++) af[k] = 0.0;
                break;
            }

            for (int k = 0; k <= n + 1; k++)
            {
                tmp1 = caf[k];
                caf[k] += rc * cab[n - k + 1];
                cab[n - k + 1] += rc * tmp1;
            }
        }

        if (reachedMaxGain)
        {
            for (int k = 0; k < d; k++) a[k] = (float)-af[k];
            for (int s = 0; s < nbSubfr; s++) c0 -= Energy(x.Slice(s * subfrLength), d);
            nrgF = c0 * invGain;
        }
        else
        {
            nrgF = caf[0];
            tmp1 = 1.0;
            for (int k = 0; k < d; k++)
            {
                atmp = af[k];
                nrgF += caf[k + 1] * atmp;
                tmp1 += atmp * atmp;
                a[k] = (float)-atmp;
            }
            nrgF -= condFac * c0 * tmp1;
        }
        return (float)nrgF;
    }

    public static void WarpedAutocorrelation(Span<float> corr, ReadOnlySpan<float> input, float warping, int length, int order)
    {
        Span<double> state = stackalloc double[17];
        Span<double> c = stackalloc double[17];
        state.Clear(); c.Clear();
        for (int n = 0; n < length; n++)
        {
            double tmp1 = input[n];
            for (int i = 0; i < order; i += 2)
            {
                double tmp2 = state[i] + warping * (state[i + 1] - tmp1);
                state[i] = tmp1;
                c[i] += state[0] * tmp1;
                tmp1 = state[i + 1] + warping * (state[i + 2] - tmp2);
                state[i + 1] = tmp2;
                c[i + 1] += state[0] * tmp2;
            }
            state[order] = tmp1;
            c[order] += state[0] * tmp1;
        }
        for (int i = 0; i < order + 1; i++) corr[i] = (float)c[i];
    }

    public static void CorrVector(ReadOnlySpan<float> x, ReadOnlySpan<float> t, int L, int order, Span<float> xt)
    {
        int p1 = order - 1;
        for (int lag = 0; lag < order; lag++)
        {
            xt[lag] = (float)InnerProduct(x.Slice(p1), t, L);
            p1--;
        }
    }

    public static void CorrMatrix(ReadOnlySpan<float> x, int L, int order, Span<float> xx)
    {
        int p1 = order - 1;
        double energy = Energy(x.Slice(p1), L);
        xx[0] = (float)energy;
        for (int j = 1; j < order; j++)
        {
            float t = x[p1 - j] * x[p1 - j] - x[p1 + L - j] * x[p1 + L - j];
            energy += t;
            xx[j * order + j] = (float)energy;
        }
        int p2 = order - 2;
        for (int lag = 1; lag < order; lag++)
        {
            energy = InnerProduct(x.Slice(p1), x.Slice(p2), L);
            xx[lag * order] = (float)energy;
            xx[lag] = (float)energy;
            for (int j = 1; j < order - lag; j++)
            {
                float t = x[p1 - j] * x[p2 - j] - x[p1 + L - j] * x[p2 + L - j];
                energy += t;
                xx[(lag + j) * order + j] = (float)energy;
                xx[j * order + lag + j] = (float)energy;
            }
            p2--;
        }
    }

    public static void RegularizeCorrelations(Span<float> xx, Span<float> x0, float noise, int d)
    {
        for (int i = 0; i < d; i++) xx[i * d + i] += noise;
        x0[0] += noise;
    }

    /// <summary>silk_LPC_inverse_pred_gain_FLP: product of (1 - k^2) over the reflection coefficients, 0 when unstable.</summary>
    public static float LpcInversePredGain(ReadOnlySpan<float> a, int order)
    {
        const float rcThreshold = 0.9999f;
        Span<float> atmp0 = stackalloc float[16];
        Span<float> atmp1 = stackalloc float[16];
        Span<float> anew = (order & 1) == 0 ? atmp0 : atmp1;
        a.Slice(0, order).CopyTo(anew);
        double invGain = 1.0;
        double rc, rcMult1, rcMult2;
        for (int k = order - 1; k > 0; k--)
        {
            rc = -anew[k];
            if (rc > rcThreshold || rc < -rcThreshold) return 0.0f;
            rcMult1 = 1.0f - rc * rc;
            rcMult2 = 1.0f / rcMult1;
            invGain *= rcMult1;
            Span<float> aold = anew;
            anew = (k & 1) == 0 ? atmp0 : atmp1;
            for (int n = 0; n < k; n++)
                anew[n] = (float)((aold[n] - aold[k - n - 1] * rc) * rcMult2);
        }
        rc = -anew[0];
        if (rc > rcThreshold || rc < -rcThreshold) return 0.0f;
        rcMult1 = 1.0f - rc * rc;
        invGain *= rcMult1;
        return (float)invGain;
    }
}
