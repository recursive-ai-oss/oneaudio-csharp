using static OneAudio.Opus.SilkMath;
using static OneAudio.Opus.SilkEncMath;

namespace OneAudio.Opus;

/// <summary>NLSF conversion, quantisation and interpolation used by the SILK encoder (RFC 6716 section 4.2.7.5).</summary>
internal static class SilkNlsf
{
    private static readonly byte[] Ordering16 = [0, 15, 8, 7, 4, 11, 12, 3, 2, 13, 10, 5, 6, 9, 14, 1];
    private static readonly byte[] Ordering10 = [0, 9, 6, 3, 4, 5, 8, 1, 2, 7];

    /// <summary>silk_NLSF2A: converts NLSFs to short-term predictor coefficients in Q12 with stability limiting.</summary>
    public static void Nlsf2A(Span<short> aQ12, ReadOnlySpan<short> nlsf, int d)
    {
        const int QA = 16;
        var ordering = d == 16 ? Ordering16 : Ordering10;
        Span<int> cosLsf = stackalloc int[MaxOrder];
        Span<int> polyP = stackalloc int[MaxOrder / 2 + 1];
        Span<int> polyQ = stackalloc int[MaxOrder / 2 + 1];
        Span<int> a32 = stackalloc int[MaxOrder];
        Span<int> scratch = stackalloc int[MaxOrder];
        for (int k = 0; k < d; k++)
        {
            int fInt = nlsf[k] >> (15 - 7);
            int fFrac = nlsf[k] - (fInt << (15 - 7));
            int cosVal = SilkTables.LSFCosTabFIXQ12[fInt];
            int delta = SilkTables.LSFCosTabFIXQ12[fInt + 1] - cosVal;
            cosLsf[ordering[k]] = RShiftRound((cosVal << 8) + delta * fFrac, 20 - QA);
        }
        int dd = d >> 1;
        FindPoly(polyP, cosLsf, 0, dd);
        FindPoly(polyQ, cosLsf, 1, dd);
        for (int k = 0; k < dd; k++)
        {
            int ptmp = polyP[k + 1] + polyP[k];
            int qtmp = polyQ[k + 1] - polyQ[k];
            a32[k] = -qtmp - ptmp;
            a32[d - k - 1] = qtmp - ptmp;
        }

        int i;
        for (i = 0; i < 10; i++)
        {
            int maxabs = 0, idx = 0;
            for (int k = 0; k < d; k++)
            {
                int absval = Abs(a32[k]);
                if (absval > maxabs) { maxabs = absval; idx = k; }
            }
            maxabs = RShiftRound(maxabs, QA + 1 - 12);
            if (maxabs > short.MaxValue)
            {
                maxabs = Math.Min(maxabs, 163838);
                int sc = 65470 - ((maxabs - short.MaxValue) << 14) / ((maxabs * (idx + 1)) >> 2);
                BwExpander32(a32, d, sc);
            }
            else break;
        }

        if (i == 10)
        {
            for (int k = 0; k < d; k++)
            {
                aQ12[k] = (short)Sat16(RShiftRound(a32[k], QA + 1 - 12));
                a32[k] = aQ12[k] << (QA + 1 - 12);
            }
        }
        else
        {
            for (int k = 0; k < d; k++) aQ12[k] = (short)RShiftRound(a32[k], QA + 1 - 12);
        }

        for (i = 0; i < 16; i++)
        {
            if (LpcInversePredGain(aQ12, d, scratch) < 107374)
            {
                BwExpander32(a32, d, 65536 - (2 << i));
                for (int k = 0; k < d; k++) aQ12[k] = (short)RShiftRound(a32[k], QA + 1 - 12);
            }
            else break;
        }
    }

    private const int MaxOrder = 16;

    private static void FindPoly(Span<int> output, ReadOnlySpan<int> cLsf, int cOff, int dd)
    {
        const int QA = 16;
        output[0] = 1 << QA;
        output[1] = -cLsf[cOff];
        for (int k = 1; k < dd; k++)
        {
            int ftmp = cLsf[cOff + 2 * k];
            output[k + 1] = (output[k - 1] << 1) - (int)RShiftRound64((long)ftmp * output[k], QA);
            for (int n = k; n > 1; n--)
                output[n] += output[n - 2] - (int)RShiftRound64((long)ftmp * output[n - 1], QA);
            output[1] -= ftmp;
        }
    }

    /// <summary>silk_NLSF_decode: reconstructs the quantised NLSFs from codebook indices.</summary>
    public static void Decode(Span<short> pNlsfQ15, ReadOnlySpan<sbyte> nlsfIndices, NlsfCodebook cb)
    {
        int order = cb.Order;
        Span<short> ecIx = stackalloc short[MaxOrder];
        Span<byte> predQ8 = stackalloc byte[MaxOrder];
        Span<short> resQ10 = stackalloc short[MaxOrder];
        Span<short> wTmp = stackalloc short[MaxOrder];
        int cb1 = nlsfIndices[0];
        for (int i = 0; i < order; i++) pNlsfQ15[i] = (short)(cb.CB1NlsfQ8[cb1 * order + i] << 7);
        SilkEncState.NlsfUnpack(ecIx, predQ8, cb, cb1);
        int outQ10 = 0;
        for (int i = order - 1; i >= 0; i--)
        {
            int predQ10 = SMulBB(outQ10, (short)predQ8[i]) >> 8;
            outQ10 = nlsfIndices[i + 1] << 10;
            if (outQ10 > 0) outQ10 -= 102;
            else if (outQ10 < 0) outQ10 += 102;
            outQ10 = SMlaWB(predQ10, outQ10, cb.QuantStepSizeQ16);
            resQ10[i] = (short)outQ10;
        }
        SilkChannel.NlsfVqWeightsLaroia(wTmp, pNlsfQ15, order);
        for (int i = 0; i < order; i++)
        {
            int wTmpQ9 = SqrtApprox(wTmp[i] << (18 - 2));
            int tmp = pNlsfQ15[i] + ((resQ10[i] << 14) / wTmpQ9);
            pNlsfQ15[i] = (short)Limit(tmp, 0, 32767);
        }
        SilkChannel.NlsfStabilize(pNlsfQ15, cb.DeltaMinQ15, order);
    }

    // ---- A2NLSF ----

    private static void TransPoly(Span<int> p, int dd)
    {
        for (int k = 2; k <= dd; k++)
        {
            for (int n = dd; n > k; n--) p[n - 2] -= p[n];
            p[k - 2] -= p[k] << 1;
        }
    }

    private static int EvalPoly(ReadOnlySpan<int> p, int x, int dd)
    {
        int y32 = p[dd];
        int xQ16 = x << 4;
        for (int n = dd - 1; n >= 0; n--) y32 = p[n] + SMulWW(y32, xQ16);
        return y32;
    }

    private static void A2NlsfInit(ReadOnlySpan<int> aQ16, Span<int> P, Span<int> Q, int dd)
    {
        P[dd] = 1 << 16;
        Q[dd] = 1 << 16;
        for (int k = 0; k < dd; k++)
        {
            P[k] = -aQ16[dd - k - 1] - aQ16[dd + k];
            Q[k] = -aQ16[dd - k - 1] + aQ16[dd + k];
        }
        for (int k = dd; k > 0; k--)
        {
            P[k - 1] -= P[k];
            Q[k - 1] += Q[k];
        }
        TransPoly(P, dd);
        TransPoly(Q, dd);
    }

    /// <summary>silk_A2NLSF: converts LPC coefficients (Q16, modified on failure by bandwidth expansion) to NLSFs.</summary>
    public static void A2Nlsf(Span<short> nlsf, Span<int> aQ16, int d)
    {
        const int binDivSteps = 3;
        const int maxIterations = 30;
        Span<int> P = stackalloc int[MaxOrder / 2 + 1];
        Span<int> Q = stackalloc int[MaxOrder / 2 + 1];
        int dd = d >> 1;
        A2NlsfInit(aQ16, P, Q, dd);
        bool useP = true;
        int xlo = SilkTables.LSFCosTabFIXQ12[0];
        int ylo = EvalPoly(P, xlo, dd);
        int rootIx;
        if (ylo < 0)
        {
            nlsf[0] = 0;
            useP = false;
            ylo = EvalPoly(Q, xlo, dd);
            rootIx = 1;
        }
        else rootIx = 0;
        int kk = 1, i = 0, thr = 0;
        while (true)
        {
            int xhi = SilkTables.LSFCosTabFIXQ12[kk];
            int yhi = EvalPoly(useP ? P : Q, xhi, dd);
            if ((ylo <= 0 && yhi >= thr) || (ylo >= 0 && yhi <= -thr))
            {
                thr = yhi == 0 ? 1 : 0;
                int ffrac = -256;
                for (int m = 0; m < binDivSteps; m++)
                {
                    int xmid = RShiftRound(xlo + xhi, 1);
                    int ymid = EvalPoly(useP ? P : Q, xmid, dd);
                    if ((ylo <= 0 && ymid >= 0) || (ylo >= 0 && ymid <= 0))
                    {
                        xhi = xmid;
                        yhi = ymid;
                    }
                    else
                    {
                        xlo = xmid;
                        ylo = ymid;
                        ffrac += 128 >> m;
                    }
                }
                if (Abs(ylo) < 65536)
                {
                    int den = ylo - yhi;
                    int nom = (ylo << (8 - binDivSteps)) + (den >> 1);
                    if (den != 0) ffrac += nom / den;
                }
                else
                {
                    ffrac += ylo / ((ylo - yhi) >> (8 - binDivSteps));
                }
                nlsf[rootIx] = (short)Math.Min((kk << 8) + ffrac, short.MaxValue);
                rootIx++;
                if (rootIx >= d) break;
                useP = (rootIx & 1) == 0;
                xlo = SilkTables.LSFCosTabFIXQ12[kk - 1];
                ylo = (1 - (rootIx & 2)) << 12;
            }
            else
            {
                kk++;
                xlo = xhi;
                ylo = yhi;
                thr = 0;
                if (kk > 128)
                {
                    i++;
                    if (i > maxIterations)
                    {
                        nlsf[0] = (short)((1 << 15) / (d + 1));
                        for (kk = 1; kk < d; kk++) nlsf[kk] = (short)SMulBB(kk + 1, nlsf[0]);
                        return;
                    }
                    BwExpander32(aQ16, d, 65536 - SMulBB(10 + i, i));
                    A2NlsfInit(aQ16, P, Q, dd);
                    useP = true;
                    xlo = SilkTables.LSFCosTabFIXQ12[0];
                    ylo = EvalPoly(P, xlo, dd);
                    if (ylo < 0)
                    {
                        nlsf[0] = 0;
                        useP = false;
                        ylo = EvalPoly(Q, xlo, dd);
                        rootIx = 1;
                    }
                    else rootIx = 0;
                    kk = 1;
                }
            }
        }
    }

    /// <summary>silk_interpolate.</summary>
    public static void Interpolate(Span<short> xi, ReadOnlySpan<short> x0, ReadOnlySpan<short> x1, int ifactQ2, int d)
    {
        for (int i = 0; i < d; i++)
            xi[i] = (short)(x0[i] + (SMulBB(x1[i] - x0[i], ifactQ2) >> 2));
    }

    // ---- NLSF quantisation ----

    private static void Vq(Span<int> errQ26, ReadOnlySpan<short> inQ15, byte[] pCbQ8, int K, int order)
    {
        int p = 0;
        for (int i = 0; i < K; i++)
        {
            int sumErrorQ26 = 0;
            for (int m = 0; m < order; m += 2)
            {
                int diff = inQ15[m] - (pCbQ8[p++] << 7);
                int sumErrorQ30 = SMulBB(diff, diff);
                diff = inQ15[m + 1] - (pCbQ8[p++] << 7);
                sumErrorQ30 = SMlaBB(sumErrorQ30, diff, diff);
                sumErrorQ26 += sumErrorQ30 >> 4;
            }
            errQ26[i] = sumErrorQ26;
        }
    }

    /// <summary>silk_insertion_sort_increasing: keeps the K smallest of L values, sorted, with their indices.</summary>
    public static void InsertionSortIncreasing(Span<int> a, Span<int> idx, int L, int K)
    {
        for (int i = 0; i < K; i++) idx[i] = i;
        for (int i = 1; i < K; i++)
        {
            int value = a[i];
            int j;
            for (j = i - 1; j >= 0 && value < a[j]; j--)
            {
                a[j + 1] = a[j];
                idx[j + 1] = idx[j];
            }
            a[j + 1] = value;
            idx[j + 1] = i;
        }
        for (int i = K; i < L; i++)
        {
            int value = a[i];
            if (value < a[K - 1])
            {
                int j;
                for (j = K - 2; j >= 0 && value < a[j]; j--)
                {
                    a[j + 1] = a[j];
                    idx[j + 1] = idx[j];
                }
                a[j + 1] = value;
                idx[j + 1] = i;
            }
        }
    }

    private static int DelDecQuant(Span<sbyte> indices, ReadOnlySpan<short> xQ10, ReadOnlySpan<short> wQ5, ReadOnlySpan<byte> predCoefQ8,
        ReadOnlySpan<short> ecIx, byte[] ecRatesQ5, int quantStepSizeQ16, int invQuantStepSizeQ6, int muQ20, int order)
    {
        const int States = 4;
        Span<int> indSort = stackalloc int[States];
        Span<sbyte> ind = stackalloc sbyte[States * MaxOrder];
        Span<short> prevOutQ10 = stackalloc short[2 * States];
        Span<int> rdQ25 = stackalloc int[2 * States];
        Span<int> rdMinQ25 = stackalloc int[States];
        Span<int> rdMaxQ25 = stackalloc int[States];
        int nStates = 1;
        rdQ25[0] = 0;
        prevOutQ10[0] = 0;
        for (int i = order - 1; ; i--)
        {
            var rates = ecRatesQ5.AsSpan(ecIx[i]);
            int predCoefQ16 = predCoefQ8[i] << 8;
            int inQ10 = xQ10[i];
            for (int j = 0; j < nStates; j++)
            {
                int predQ10 = SMulWB(predCoefQ16, prevOutQ10[j]);
                int resQ10 = inQ10 - predQ10;
                int indTmp = SMulWB(invQuantStepSizeQ6, resQ10);
                indTmp = Limit(indTmp, -10, 10 - 1);
                ind[j * MaxOrder + i] = (sbyte)indTmp;
                int out0Q10 = indTmp << 10;
                int out1Q10 = out0Q10 + 1024;
                if (indTmp > 0)
                {
                    out0Q10 -= 102; // 0.1 in Q10
                    out1Q10 -= 102;
                }
                else if (indTmp == 0) out1Q10 -= 102;
                else if (indTmp == -1) out0Q10 += 102;
                else
                {
                    out0Q10 += 102;
                    out1Q10 += 102;
                }
                out0Q10 = SMulWB(out0Q10, quantStepSizeQ16);
                out1Q10 = SMulWB(out1Q10, quantStepSizeQ16);
                out0Q10 += predQ10;
                out1Q10 += predQ10;
                prevOutQ10[j] = (short)out0Q10;
                prevOutQ10[j + nStates] = (short)out1Q10;

                int rate0Q5, rate1Q5;
                if (indTmp + 1 >= 4)
                {
                    if (indTmp + 1 == 4)
                    {
                        rate0Q5 = rates[indTmp + 4];
                        rate1Q5 = 280;
                    }
                    else
                    {
                        rate0Q5 = SMlaBB(280 - 43 * 4, 43, indTmp);
                        rate1Q5 = rate0Q5 + 43;
                    }
                }
                else if (indTmp <= -4)
                {
                    if (indTmp == -4)
                    {
                        rate0Q5 = 280;
                        rate1Q5 = rates[indTmp + 1 + 4];
                    }
                    else
                    {
                        rate0Q5 = SMlaBB(280 - 43 * 4, -43, indTmp);
                        rate1Q5 = rate0Q5 - 43;
                    }
                }
                else
                {
                    rate0Q5 = rates[indTmp + 4];
                    rate1Q5 = rates[indTmp + 1 + 4];
                }
                int rdTmpQ25 = rdQ25[j];
                int diffQ10 = inQ10 - out0Q10;
                rdQ25[j] = SMlaBB(rdTmpQ25 + SMulBB(diffQ10, diffQ10) * wQ5[i], muQ20, rate0Q5);
                diffQ10 = inQ10 - out1Q10;
                rdQ25[j + nStates] = SMlaBB(rdTmpQ25 + SMulBB(diffQ10, diffQ10) * wQ5[i], muQ20, rate1Q5);
            }

            if (nStates < States)
            {
                for (int j = 0; j < nStates; j++) ind[(j + nStates) * MaxOrder + i] = (sbyte)(ind[j * MaxOrder + i] + 1);
                nStates <<= 1;
                for (int j = nStates; j < States; j++) ind[j * MaxOrder + i] = ind[(j - nStates) * MaxOrder + i];
            }
            else if (i > 0)
            {
                for (int j = 0; j < States; j++)
                {
                    if (rdQ25[j] > rdQ25[j + States])
                    {
                        rdMaxQ25[j] = rdQ25[j];
                        rdMinQ25[j] = rdQ25[j + States];
                        rdQ25[j] = rdMinQ25[j];
                        rdQ25[j + States] = rdMaxQ25[j];
                        short t = prevOutQ10[j];
                        prevOutQ10[j] = prevOutQ10[j + States];
                        prevOutQ10[j + States] = t;
                        indSort[j] = j + States;
                    }
                    else
                    {
                        rdMinQ25[j] = rdQ25[j];
                        rdMaxQ25[j] = rdQ25[j + States];
                        indSort[j] = j;
                    }
                }
                while (true)
                {
                    int minMaxQ25 = int.MaxValue, maxMinQ25 = 0, indMinMax = 0, indMaxMin = 0;
                    for (int j = 0; j < States; j++)
                    {
                        if (minMaxQ25 > rdMaxQ25[j]) { minMaxQ25 = rdMaxQ25[j]; indMinMax = j; }
                        if (maxMinQ25 < rdMinQ25[j]) { maxMinQ25 = rdMinQ25[j]; indMaxMin = j; }
                    }
                    if (minMaxQ25 >= maxMinQ25) break;
                    indSort[indMaxMin] = indSort[indMinMax] ^ States;
                    rdQ25[indMaxMin] = rdQ25[indMinMax + States];
                    prevOutQ10[indMaxMin] = prevOutQ10[indMinMax + States];
                    rdMinQ25[indMaxMin] = 0;
                    rdMaxQ25[indMinMax] = int.MaxValue;
                    ind.Slice(indMinMax * MaxOrder, MaxOrder).CopyTo(ind.Slice(indMaxMin * MaxOrder, MaxOrder));
                }
                for (int j = 0; j < States; j++) ind[j * MaxOrder + i] += (sbyte)(indSort[j] >> 2);
            }
            else break;
        }

        int best = 0;
        int minQ25 = int.MaxValue;
        for (int j = 0; j < 2 * States; j++)
        {
            if (minQ25 > rdQ25[j]) { minQ25 = rdQ25[j]; best = j; }
        }
        for (int j = 0; j < order; j++) indices[j] = ind[(best & (States - 1)) * MaxOrder + j];
        indices[0] += (sbyte)(best >> 2);
        return minQ25;
    }

    /// <summary>silk_NLSF_encode: two-stage quantisation with a delayed-decision second stage; returns the rate-distortion cost.</summary>
    public static int Encode(Span<sbyte> nlsfIndices, Span<short> pNlsfQ15, NlsfCodebook cb, ReadOnlySpan<short> pWQw,
        int nlsfMuQ20, int nSurvivors, int signalType)
    {
        Span<int> errQ26 = stackalloc int[32];
        Span<int> rdQ25 = stackalloc int[32];
        Span<int> tempIndices1 = stackalloc int[32];
        Span<sbyte> tempIndices2 = stackalloc sbyte[32 * MaxOrder];
        Span<short> resQ15 = stackalloc short[MaxOrder];
        Span<short> resQ10 = stackalloc short[MaxOrder];
        Span<short> nlsfTmpQ15 = stackalloc short[MaxOrder];
        Span<short> wTmpQw = stackalloc short[MaxOrder];
        Span<short> wAdjQ5 = stackalloc short[MaxOrder];
        Span<byte> predQ8 = stackalloc byte[MaxOrder];
        Span<short> ecIx = stackalloc short[MaxOrder];
        int order = cb.Order;

        SilkChannel.NlsfStabilize(pNlsfQ15, cb.DeltaMinQ15, order);
        Vq(errQ26, pNlsfQ15, cb.CB1NlsfQ8, cb.NVectors, order);
        InsertionSortIncreasing(errQ26, tempIndices1, cb.NVectors, nSurvivors);

        for (int s = 0; s < nSurvivors; s++)
        {
            int ind1 = tempIndices1[s];
            for (int i = 0; i < order; i++)
            {
                nlsfTmpQ15[i] = (short)(cb.CB1NlsfQ8[ind1 * order + i] << 7);
                resQ15[i] = (short)(pNlsfQ15[i] - nlsfTmpQ15[i]);
            }
            SilkChannel.NlsfVqWeightsLaroia(wTmpQw, nlsfTmpQ15, order);
            for (int i = 0; i < order; i++)
            {
                int wTmpQ9 = SqrtApprox(wTmpQw[i] << (18 - 2));
                resQ10[i] = (short)(SMulBB(resQ15[i], wTmpQ9) >> 14);
            }
            for (int i = 0; i < order; i++)
                wAdjQ5[i] = (short)((pWQw[i] << 5) / wTmpQw[i]);
            SilkEncState.NlsfUnpack(ecIx, predQ8, cb, ind1);
            rdQ25[s] = DelDecQuant(tempIndices2.Slice(s * MaxOrder, MaxOrder), resQ10, wAdjQ5, predQ8, ecIx,
                cb.EcRatesQ5, cb.QuantStepSizeQ16, cb.InvQuantStepSizeQ6, nlsfMuQ20, order);

            int probQ8 = ind1 == 0
                ? 256 - cb.CB1ICdf[(signalType >> 1) * cb.NVectors + ind1]
                : cb.CB1ICdf[(signalType >> 1) * cb.NVectors + ind1 - 1] - cb.CB1ICdf[(signalType >> 1) * cb.NVectors + ind1];
            int bitsQ7 = (8 << 7) - Lin2Log(probQ8);
            rdQ25[s] = SMlaBB(rdQ25[s], bitsQ7, nlsfMuQ20 >> 2);
        }

        Span<int> bestIdx = stackalloc int[1];
        InsertionSortIncreasing(rdQ25, bestIdx, nSurvivors, 1);
        int bestIndex = bestIdx[0];
        nlsfIndices[0] = (sbyte)tempIndices1[bestIndex];
        tempIndices2.Slice(bestIndex * MaxOrder, order).CopyTo(nlsfIndices.Slice(1));
        Decode(pNlsfQ15, nlsfIndices, cb);
        return rdQ25[0];
    }
}
