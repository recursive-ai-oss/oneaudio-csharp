using static OneAudio.Opus.SilkMath;
using static OneAudio.Opus.SilkEncMath;

namespace OneAudio.Opus;

/// <summary>Long-term prediction analysis and gain quantisation of the SILK encoder (floating-point reference path).</summary>
internal static class SilkLtpEnc
{
    private const int LtpOrder = 5;
    private const int MaxNbSubfr = 4;
    private const float LtpDamping = 0.05f;
    private const float LtpSmoothing = 0.1f;
    private const float FindLtpCondFac = 1e-5f;
    private const float RegularizationFactor = 1e-8f;
    private const int LtpGainMiddleAvgRdQ14 = 12304;

    private static void LDL(Span<float> A, int M, Span<float> L, Span<float> Dinv)
    {
        Span<float> v = stackalloc float[16];
        Span<float> D = stackalloc float[16];
        double diagMinValue = FindLtpCondFac * 0.5f * (A[0] + A[M * M - 1]);
        int err = 1;
        for (int loopCount = 0; loopCount < M && err == 1; loopCount++)
        {
            err = 0;
            for (int j = 0; j < M; j++)
            {
                int p1 = j * M;
                double temp = A[j * M + j];
                for (int i = 0; i < j; i++)
                {
                    v[i] = L[p1 + i] * D[i];
                    temp -= (float)(L[p1 + i] * v[i]);
                }
                if (temp < diagMinValue)
                {
                    temp = (loopCount + 1) * diagMinValue - temp;
                    for (int i = 0; i < M; i++) A[i * M + i] += (float)temp;
                    err = 1;
                    break;
                }
                D[j] = (float)temp;
                Dinv[j] = (float)(1.0f / temp);
                L[j * M + j] = 1.0f;
                int pa = j * M;
                int p2 = (j + 1) * M;
                for (int i = j + 1; i < M; i++)
                {
                    temp = 0.0;
                    for (int k = 0; k < j; k++) temp += (float)(L[p2 + k] * v[k]);
                    L[i * M + j] = (float)((A[pa + i] - temp) * Dinv[j]);
                    p2 += M;
                }
            }
        }
    }

    private static void SolveLdl(Span<float> A, int M, ReadOnlySpan<float> b, Span<float> x)
    {
        Span<float> L = stackalloc float[16 * 16];
        Span<float> T = stackalloc float[16];
        Span<float> Dinv = stackalloc float[16];
        L.Clear();
        LDL(A, M, L, Dinv);
        // Lower triangular solve with unit diagonal
        for (int i = 0; i < M; i++)
        {
            float temp = 0;
            for (int j = 0; j < i; j++) temp += L[i * M + j] * T[j];
            T[i] = b[i] - temp;
        }
        for (int i = 0; i < M; i++) T[i] = T[i] * Dinv[i];
        // Upper triangular (transpose of L) solve
        for (int i = M - 1; i >= 0; i--)
        {
            float temp = 0;
            for (int j = M - 1; j > i; j--) temp += L[j * M + i] * x[j];
            x[i] = T[i] - temp;
        }
    }

    public static float ResidualEnergyCovar(ReadOnlySpan<float> c, Span<float> wXX, ReadOnlySpan<float> wXx, float wxx, int D)
    {
        float nrg = 0.0f;
        float regularization = RegularizationFactor * (wXX[0] + wXX[D * D - 1]);
        int k;
        for (k = 0; k < 10; k++)
        {
            nrg = wxx;
            float tmp = 0.0f;
            for (int i = 0; i < D; i++) tmp += wXx[i] * c[i];
            nrg -= 2.0f * tmp;
            for (int i = 0; i < D; i++)
            {
                tmp = 0.0f;
                for (int j = i + 1; j < D; j++) tmp += wXX[i + D * j] * c[j];
                nrg += c[i] * (2.0f * tmp + wXX[i + D * i] * c[i]);
            }
            if (nrg > 0) break;
            for (int i = 0; i < D; i++) wXX[i + D * i] += regularization;
            regularization *= 2.0f;
        }
        if (k == 10) nrg = 1.0f;
        return nrg;
    }

    /// <summary>silk_find_LTP_FLP: per-subframe LTP filter taps by weighted least squares plus the prediction coding gain.</summary>
    public static void FindLtp(Span<float> b, Span<float> wLtp, out float ltpredCodGain, ReadOnlySpan<float> rLpc,
        ReadOnlySpan<int> lag, ReadOnlySpan<float> wght, int subfrLength, int nbSubfr, int memOffset)
    {
        Span<float> d = stackalloc float[MaxNbSubfr];
        Span<float> w = stackalloc float[MaxNbSubfr];
        Span<float> nrg = stackalloc float[MaxNbSubfr];
        Span<float> rr = stackalloc float[MaxNbSubfr];
        Span<float> Rr = stackalloc float[LtpOrder];
        Span<float> deltaB = stackalloc float[LtpOrder];
        int bOff = 0, wOff = 0, rOff = memOffset;
        for (int k = 0; k < nbSubfr; k++)
        {
            int lagOff = rOff - (lag[k] + LtpOrder / 2);
            var wl = wLtp.Slice(wOff, LtpOrder * LtpOrder);
            SilkFlp.CorrMatrix(rLpc.Slice(lagOff), subfrLength, LtpOrder, wl);
            SilkFlp.CorrVector(rLpc.Slice(lagOff), rLpc.Slice(rOff), subfrLength, LtpOrder, Rr);
            rr[k] = (float)SilkFlp.Energy(rLpc.Slice(rOff), subfrLength);
            float regu = 1.0f + rr[k] + wl[0] + wl[(LtpOrder - 1) * LtpOrder + LtpOrder - 1];
            regu *= LtpDamping / 3;
            SilkFlp.RegularizeCorrelations(wl, rr.Slice(k), regu, LtpOrder);
            SolveLdl(wl, LtpOrder, Rr, b.Slice(bOff, LtpOrder));
            nrg[k] = ResidualEnergyCovar(b.Slice(bOff, LtpOrder), wl, Rr, rr[k], LtpOrder);
            float temp = wght[k] / (nrg[k] * wght[k] + 0.01f * subfrLength);
            SilkFlp.ScaleVector(wl, temp, LtpOrder * LtpOrder);
            w[k] = wl[(LtpOrder / 2) * LtpOrder + LtpOrder / 2];
            rOff += subfrLength;
            bOff += LtpOrder;
            wOff += LtpOrder * LtpOrder;
        }

        float lpcLtpResNrg = 1e-6f;
        float lpcResNrg = 0.0f;
        for (int k = 0; k < nbSubfr; k++)
        {
            lpcResNrg += rr[k] * wght[k];
            lpcLtpResNrg += nrg[k] * wght[k];
        }
        ltpredCodGain = 3.0f * SilkFlp.Log2(lpcResNrg / lpcLtpResNrg);

        bOff = 0;
        for (int k = 0; k < nbSubfr; k++)
        {
            d[k] = 0;
            for (int i = 0; i < LtpOrder; i++) d[k] += b[bOff + i];
            bOff += LtpOrder;
        }
        float tmp = 1e-3f;
        for (int k = 0; k < nbSubfr; k++) tmp += w[k];
        float m = 0;
        for (int k = 0; k < nbSubfr; k++) m += d[k] * w[k];
        m = m / tmp;

        bOff = 0;
        for (int k = 0; k < nbSubfr; k++)
        {
            float g = LtpSmoothing / (LtpSmoothing + w[k]) * (m - d[k]);
            float temp = 0;
            for (int i = 0; i < LtpOrder; i++)
            {
                deltaB[i] = b[bOff + i] > 0.1f ? b[bOff + i] : 0.1f;
                temp += deltaB[i];
            }
            temp = g / temp;
            for (int i = 0; i < LtpOrder; i++) b[bOff + i] = b[bOff + i] + deltaB[i] * temp;
            bOff += LtpOrder;
        }
    }

    /// <summary>silk_LTP_analysis_filter_FLP.</summary>
    public static void LtpAnalysisFilter(Span<float> ltpRes, ReadOnlySpan<float> x, int xOff, ReadOnlySpan<float> B, ReadOnlySpan<int> pitchL,
        ReadOnlySpan<float> invGains, int subfrLength, int nbSubfr, int preLength)
    {
        Span<float> btmp = stackalloc float[LtpOrder];
        int xp = xOff, rp = 0;
        for (int k = 0; k < nbSubfr; k++)
        {
            int xl = xp - pitchL[k];
            float invGain = invGains[k];
            for (int i = 0; i < LtpOrder; i++) btmp[i] = B[k * LtpOrder + i];
            for (int i = 0; i < subfrLength + preLength; i++)
            {
                ltpRes[rp + i] = x[xp + i];
                for (int j = 0; j < LtpOrder; j++) ltpRes[rp + i] -= btmp[j] * x[xl + LtpOrder / 2 - j];
                ltpRes[rp + i] *= invGain;
                xl++;
            }
            rp += subfrLength + preLength;
            xp += subfrLength;
        }
    }

    /// <summary>silk_residual_energy_FLP.</summary>
    public static void ResidualEnergy(Span<float> nrgs, ReadOnlySpan<float> x, ReadOnlySpan<float> a0, ReadOnlySpan<float> a1,
        ReadOnlySpan<float> gains, int subfrLength, int nbSubfr, int lpcOrder)
    {
        Span<float> lpcRes = stackalloc float[(320 + 4 * 16) / 2];
        int shift = lpcOrder + subfrLength;
        SilkFlp.LpcAnalysisFilter(lpcRes, a0, x, 2 * shift, lpcOrder);
        var resPtr = lpcRes.Slice(lpcOrder);
        nrgs[0] = (float)(gains[0] * gains[0] * SilkFlp.Energy(resPtr, subfrLength));
        nrgs[1] = (float)(gains[1] * gains[1] * SilkFlp.Energy(resPtr.Slice(shift), subfrLength));
        if (nbSubfr == MaxNbSubfr)
        {
            SilkFlp.LpcAnalysisFilter(lpcRes, a1, x.Slice(2 * shift), 2 * shift, lpcOrder);
            nrgs[2] = (float)(gains[2] * gains[2] * SilkFlp.Energy(resPtr, subfrLength));
            nrgs[3] = (float)(gains[3] * gains[3] * SilkFlp.Energy(resPtr.Slice(shift), subfrLength));
        }
    }

    private static void VqWMatEc(out sbyte ind, out int rateDistQ14, ReadOnlySpan<short> inQ14, ReadOnlySpan<int> wQ18,
        sbyte[] cbQ7, byte[] clQ5, int muQ9, int L)
    {
        Span<short> diff = stackalloc short[5];
        rateDistQ14 = int.MaxValue;
        ind = 0;
        int row = 0;
        for (int k = 0; k < L; k++)
        {
            for (int i = 0; i < 5; i++) diff[i] = (short)(inQ14[i] - (cbQ7[row + i] << 7));
            int sum1 = SMulBB(muQ9, clQ5[k]);
            int sum2 = SMulWB(wQ18[1], diff[1]);
            sum2 = SMlaWB(sum2, wQ18[2], diff[2]);
            sum2 = SMlaWB(sum2, wQ18[3], diff[3]);
            sum2 = SMlaWB(sum2, wQ18[4], diff[4]);
            sum2 <<= 1;
            sum2 = SMlaWB(sum2, wQ18[0], diff[0]);
            sum1 = SMlaWB(sum1, sum2, diff[0]);

            sum2 = SMulWB(wQ18[7], diff[2]);
            sum2 = SMlaWB(sum2, wQ18[8], diff[3]);
            sum2 = SMlaWB(sum2, wQ18[9], diff[4]);
            sum2 <<= 1;
            sum2 = SMlaWB(sum2, wQ18[6], diff[1]);
            sum1 = SMlaWB(sum1, sum2, diff[1]);

            sum2 = SMulWB(wQ18[13], diff[3]);
            sum2 = SMlaWB(sum2, wQ18[14], diff[4]);
            sum2 <<= 1;
            sum2 = SMlaWB(sum2, wQ18[12], diff[2]);
            sum1 = SMlaWB(sum1, sum2, diff[2]);

            sum2 = SMulWB(wQ18[19], diff[4]);
            sum2 <<= 1;
            sum2 = SMlaWB(sum2, wQ18[18], diff[3]);
            sum1 = SMlaWB(sum1, sum2, diff[3]);

            sum2 = SMulWB(wQ18[24], diff[4]);
            sum1 = SMlaWB(sum1, sum2, diff[4]);

            if (sum1 < rateDistQ14)
            {
                rateDistQ14 = sum1;
                ind = (sbyte)k;
            }
            row += LtpOrder;
        }
    }

    /// <summary>silk_quant_LTP_gains_FLP: picks the codebook (periodicity) and per-subframe entries by rate-distortion.</summary>
    public static void QuantLtpGains(Span<float> B, Span<sbyte> cbkIndex, out sbyte periodicityIndex, ReadOnlySpan<float> W,
        int muQ9, bool lowComplexity, int nbSubfr)
    {
        Span<short> bQ14 = stackalloc short[MaxNbSubfr * LtpOrder];
        Span<int> wQ18 = stackalloc int[MaxNbSubfr * LtpOrder * LtpOrder];
        Span<sbyte> tempIdx = stackalloc sbyte[MaxNbSubfr];
        for (int i = 0; i < nbSubfr * LtpOrder; i++) bQ14[i] = (short)SilkFlp.Float2Int(B[i] * 16384.0f);
        for (int i = 0; i < nbSubfr * LtpOrder * LtpOrder; i++) wQ18[i] = SilkFlp.Float2Int(W[i] * 262144.0f);

        sbyte[][] cbks = [SilkTables.LTPGainVq0, SilkTables.LTPGainVq1, SilkTables.LTPGainVq2];
        byte[][] bits = [SilkEncTables.LTPGainBitsQ50, SilkEncTables.LTPGainBitsQ51, SilkEncTables.LTPGainBitsQ52];
        int[] sizes = [8, 16, 32];
        int minRateDistQ14 = int.MaxValue;
        periodicityIndex = 0;
        for (int k = 0; k < 3; k++)
        {
            int rateDistQ14 = 0;
            for (int j = 0; j < nbSubfr; j++)
            {
                VqWMatEc(out sbyte idx, out int rdSub, bQ14.Slice(j * LtpOrder, LtpOrder), wQ18.Slice(j * LtpOrder * LtpOrder, 25),
                    cbks[k], bits[k], muQ9, sizes[k]);
                tempIdx[j] = idx;
                rateDistQ14 = AddPosSat32(rateDistQ14, rdSub);
            }
            rateDistQ14 = Math.Min(int.MaxValue - 1, rateDistQ14);
            if (rateDistQ14 < minRateDistQ14)
            {
                minRateDistQ14 = rateDistQ14;
                periodicityIndex = (sbyte)k;
                tempIdx.Slice(0, nbSubfr).CopyTo(cbkIndex);
            }
            if (lowComplexity && rateDistQ14 < LtpGainMiddleAvgRdQ14) break;
        }
        var cb = cbks[periodicityIndex];
        for (int j = 0; j < nbSubfr; j++)
            for (int k = 0; k < LtpOrder; k++)
                bQ14[j * LtpOrder + k] = (short)(cb[cbkIndex[j] * LtpOrder + k] << 7);
        for (int i = 0; i < nbSubfr * LtpOrder; i++) B[i] = bQ14[i] * (1.0f / 16384.0f);
    }
}
