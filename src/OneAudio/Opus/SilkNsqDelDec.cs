using static OneAudio.Opus.SilkMath;
using static OneAudio.Opus.SilkEncMath;

namespace OneAudio.Opus;

internal sealed partial class SilkNsqState
{
    private const int DecisionDelay = 32;
    private const int DecisionDelayMask = DecisionDelay - 1;
    private const int MaxDelDecStates = 4;

    // Flat layout of one delayed-decision state; the tail-copy used when states are replaced relies on this order.
    private const int DdSLpc = 0;
    private const int DdRand = SilkEncState.MaxSubFrameLength + NsqLpcBufLength;
    private const int DdQ = DdRand + DecisionDelay;
    private const int DdXq = DdQ + DecisionDelay;
    private const int DdPred = DdXq + DecisionDelay;
    private const int DdShape = DdPred + DecisionDelay;
    private const int DdSAr2 = DdShape + DecisionDelay;
    private const int DdLfAr = DdSAr2 + MaxShapeLpcOrder;
    private const int DdSeed = DdLfAr + 1;
    private const int DdSeedInit = DdSeed + 1;
    private const int DdRd = DdSeedInit + 1;
    private const int DdSize = DdRd + 1;

    // Sample state fields
    private const int SsQ = 0, SsRd = 1, SsXq = 2, SsLfAr = 3, SsShape = 4, SsLpcExc = 5, SsSize = 6;

    private readonly int[][] _dd = [new int[DdSize], new int[DdSize], new int[DdSize], new int[DdSize]];
    private readonly int[] _ss = new int[MaxDelDecStates * 2 * SsSize];
    private readonly int[] _delayedGainQ10 = new int[DecisionDelay];

    /// <summary>silk_NSQ_del_dec: noise shaping quantisation with several survivor paths and delayed decision.</summary>
    public void QuantizeDelDec(SilkEncState e, SilkIndices ind, ReadOnlySpan<int> xQ3, sbyte[] pulses,
        ReadOnlySpan<short> predCoefQ12, ReadOnlySpan<short> ltpCoefQ14, ReadOnlySpan<short> ar2Q13,
        ReadOnlySpan<int> harmShapeGainQ14, ReadOnlySpan<int> tiltQ14, ReadOnlySpan<int> lfShpQ14,
        ReadOnlySpan<int> gainsQ16, ReadOnlySpan<int> pitchL, int lambdaQ10, int ltpScaleQ14)
    {
        int lag = LagPrev;
        int nStates = e.NStatesDelayedDecision;
        for (int k = 0; k < nStates; k++)
        {
            var d = _dd[k];
            Array.Clear(d);
            d[DdSeed] = (k + ind.Seed) & 3;
            d[DdSeedInit] = d[DdSeed];
            d[DdRd] = 0;
            d[DdLfAr] = SLfArShpQ14;
            d[DdShape] = SLtpShpQ14[e.LtpMemLength - 1];
            Array.Copy(SLpcQ14, 0, d, DdSLpc, NsqLpcBufLength);
            Array.Copy(SAr2Q14, 0, d, DdSAr2, MaxShapeLpcOrder);
        }

        int offsetQ10 = SilkTables.QuantizationOffsetsQ10[(ind.SignalType >> 1) * 2 + ind.QuantOffsetType];
        int smplBufIdx = 0;
        int decisionDelay = Math.Min(DecisionDelay, e.SubfrLength);
        if (ind.SignalType == SilkEncState.TypeVoiced)
        {
            for (int k = 0; k < e.NbSubfr; k++) decisionDelay = Math.Min(decisionDelay, pitchL[k] - LtpOrder / 2 - 1);
        }
        else if (lag > 0)
        {
            decisionDelay = Math.Min(decisionDelay, lag - LtpOrder / 2 - 1);
        }

        int lsfInterpolationFlag = ind.NlsfInterpCoefQ2 == 4 ? 0 : 1;
        int pxq = e.LtpMemLength;
        SLtpShpBufIdx = e.LtpMemLength;
        SLtpBufIdx = e.LtpMemLength;
        int subfr = 0;
        int pOff = 0, xOff = 0;
        for (int k = 0; k < e.NbSubfr; k++)
        {
            var aQ12 = predCoefQ12.Slice(((k >> 1) | (1 - lsfInterpolationFlag)) * MaxLpcOrder, MaxLpcOrder);
            var bQ14 = ltpCoefQ14.Slice(k * LtpOrder, LtpOrder);
            var arShpQ13 = ar2Q13.Slice(k * MaxShapeLpcOrder, MaxShapeLpcOrder);
            int harmShapeFirPackedQ14 = harmShapeGainQ14[k] >> 2;
            harmShapeFirPackedQ14 |= (harmShapeGainQ14[k] >> 1) << 16;
            RewhiteFlag = 0;
            if (ind.SignalType == SilkEncState.TypeVoiced)
            {
                lag = pitchL[k];
                if ((k & (3 - (lsfInterpolationFlag << 1))) == 0)
                {
                    if (k == 2)
                    {
                        int rdMin = _dd[0][DdRd];
                        int winner = 0;
                        for (int i = 1; i < nStates; i++)
                        {
                            if (_dd[i][DdRd] < rdMin) { rdMin = _dd[i][DdRd]; winner = i; }
                        }
                        for (int i = 0; i < nStates; i++)
                            if (i != winner) _dd[i][DdRd] += int.MaxValue >> 4;
                        var psDd = _dd[winner];
                        int lastSmpleIdx = smplBufIdx + decisionDelay;
                        for (int i = 0; i < decisionDelay; i++)
                        {
                            lastSmpleIdx = (lastSmpleIdx - 1) & DecisionDelayMask;
                            pulses[pOff + i - decisionDelay] = (sbyte)RShiftRound(psDd[DdQ + lastSmpleIdx], 10);
                            Xq[pxq + i - decisionDelay] = (short)Sat16(RShiftRound(SMulWW(psDd[DdXq + lastSmpleIdx], gainsQ16[1]), 14));
                            SLtpShpQ14[SLtpShpBufIdx - decisionDelay + i] = psDd[DdShape + lastSmpleIdx];
                        }
                        subfr = 0;
                    }
                    int startIdx = e.LtpMemLength - lag - e.PredictLpcOrder - LtpOrder / 2;
                    SilkChannel.LpcAnalysisFilter(_sLtp.AsSpan(startIdx), Xq.AsSpan(startIdx + k * e.SubfrLength), aQ12,
                        e.LtpMemLength - startIdx, e.PredictLpcOrder);
                    SLtpBufIdx = e.LtpMemLength;
                    RewhiteFlag = 1;
                }
            }
            ScaleStatesDelDec(e, xQ3.Slice(xOff), k, nStates, ltpScaleQ14, gainsQ16, pitchL, ind.SignalType, decisionDelay);
            NoiseShapeQuantizerDelDec(ind.SignalType, pulses, pOff, pxq, aQ12, bQ14, arShpQ13, lag, harmShapeFirPackedQ14,
                tiltQ14[k], lfShpQ14[k], gainsQ16[k], lambdaQ10, offsetQ10, e.SubfrLength, subfr++, e.ShapingLpcOrder,
                e.PredictLpcOrder, e.WarpingQ16, nStates, ref smplBufIdx, decisionDelay);
            xOff += e.SubfrLength;
            pOff += e.SubfrLength;
            pxq += e.SubfrLength;
        }

        int rdMinQ10 = _dd[0][DdRd];
        int winnerInd = 0;
        for (int k = 1; k < nStates; k++)
            if (_dd[k][DdRd] < rdMinQ10) { rdMinQ10 = _dd[k][DdRd]; winnerInd = k; }
        var win = _dd[winnerInd];
        ind.Seed = (sbyte)win[DdSeedInit];
        int last = smplBufIdx + decisionDelay;
        int gainQ10 = gainsQ16[e.NbSubfr - 1] >> 6;
        for (int i = 0; i < decisionDelay; i++)
        {
            last = (last - 1) & DecisionDelayMask;
            pulses[pOff + i - decisionDelay] = (sbyte)RShiftRound(win[DdQ + last], 10);
            Xq[pxq + i - decisionDelay] = (short)Sat16(RShiftRound(SMulWW(win[DdXq + last], gainQ10), 8));
            SLtpShpQ14[SLtpShpBufIdx - decisionDelay + i] = win[DdShape + last];
        }
        Array.Copy(win, DdSLpc + e.SubfrLength, SLpcQ14, 0, NsqLpcBufLength);
        Array.Copy(win, DdSAr2, SAr2Q14, 0, MaxShapeLpcOrder);
        SLfArShpQ14 = win[DdLfAr];
        LagPrev = pitchL[e.NbSubfr - 1];
        Array.Copy(Xq, e.FrameLength, Xq, 0, e.LtpMemLength);
        Array.Copy(SLtpShpQ14, e.FrameLength, SLtpShpQ14, 0, e.LtpMemLength);
    }

    private void NoiseShapeQuantizerDelDec(int signalType, sbyte[] pulses, int pOff, int xqOff, ReadOnlySpan<short> aQ12,
        ReadOnlySpan<short> bQ14, ReadOnlySpan<short> arShpQ13, int lag, int harmShapeFirPackedQ14, int tiltQ14, int lfShpQ14,
        int gainQ16, int lambdaQ10, int offsetQ10, int length, int subfr, int shapingLpcOrder, int predictLpcOrder,
        int warpingQ16, int nStates, ref int smplBufIdx, int decisionDelay)
    {
        var sLtpQ15 = _sLtpQ15;
        var xQ10 = _xScQ10;
        var ss = _ss;
        var delayedGainQ10 = _delayedGainQ10;
        int shpLag = SLtpShpBufIdx - lag + 3 / 2;
        int predLag = SLtpBufIdx - lag + LtpOrder / 2;
        int gainQ10 = gainQ16 >> 6;

        for (int i = 0; i < length; i++)
        {
            int ltpPredQ14;
            if (signalType == SilkEncState.TypeVoiced)
            {
                ltpPredQ14 = 2;
                for (int j = 0; j < 5; j++) ltpPredQ14 = SMlaWB(ltpPredQ14, sLtpQ15[predLag - j], bQ14[j]);
                ltpPredQ14 <<= 1;
                predLag++;
            }
            else ltpPredQ14 = 0;

            int nLtpQ14;
            if (lag > 0)
            {
                nLtpQ14 = SMulWB(SLtpShpQ14[shpLag] + SLtpShpQ14[shpLag - 2], harmShapeFirPackedQ14);
                nLtpQ14 = SMlaWT(nLtpQ14, SLtpShpQ14[shpLag - 1], harmShapeFirPackedQ14);
                nLtpQ14 = ltpPredQ14 - (nLtpQ14 << 2);
                shpLag++;
            }
            else nLtpQ14 = 0;

            for (int k = 0; k < nStates; k++)
            {
                var d = _dd[k];
                int sBase = k * 2 * SsSize;
                d[DdSeed] = Rand(d[DdSeed]);
                int psLpc = DdSLpc + NsqLpcBufLength - 1 + i;
                int lpcPredQ14 = predictLpcOrder >> 1;
                for (int j = 0; j < 10; j++) lpcPredQ14 = SMlaWB(lpcPredQ14, d[psLpc - j], aQ12[j]);
                if (predictLpcOrder == 16)
                    for (int j = 10; j < 16; j++) lpcPredQ14 = SMlaWB(lpcPredQ14, d[psLpc - j], aQ12[j]);
                lpcPredQ14 <<= 4;

                int tmp2 = SMlaWB(d[psLpc], d[DdSAr2], warpingQ16);
                int tmp1 = SMlaWB(d[DdSAr2], d[DdSAr2 + 1] - tmp2, warpingQ16);
                d[DdSAr2] = tmp2;
                int nArQ14 = shapingLpcOrder >> 1;
                nArQ14 = SMlaWB(nArQ14, tmp2, arShpQ13[0]);
                for (int j = 2; j < shapingLpcOrder; j += 2)
                {
                    tmp2 = SMlaWB(d[DdSAr2 + j - 1], d[DdSAr2 + j] - tmp1, warpingQ16);
                    d[DdSAr2 + j - 1] = tmp1;
                    nArQ14 = SMlaWB(nArQ14, tmp1, arShpQ13[j - 1]);
                    tmp1 = SMlaWB(d[DdSAr2 + j], d[DdSAr2 + j + 1] - tmp2, warpingQ16);
                    d[DdSAr2 + j] = tmp2;
                    nArQ14 = SMlaWB(nArQ14, tmp2, arShpQ13[j]);
                }
                d[DdSAr2 + shapingLpcOrder - 1] = tmp1;
                nArQ14 = SMlaWB(nArQ14, tmp1, arShpQ13[shapingLpcOrder - 1]);
                nArQ14 <<= 1;
                nArQ14 = SMlaWB(nArQ14, d[DdLfAr], tiltQ14);
                nArQ14 <<= 2;

                int nLfQ14 = SMulWB(d[DdShape + smplBufIdx], lfShpQ14);
                nLfQ14 = SMlaWT(nLfQ14, d[DdLfAr], lfShpQ14);
                nLfQ14 <<= 2;

                tmp1 = nArQ14 + nLfQ14;
                tmp2 = nLtpQ14 + lpcPredQ14;
                tmp1 = tmp2 - tmp1;
                tmp1 = RShiftRound(tmp1, 4);

                int rQ10 = xQ10[i] - tmp1;
                if (d[DdSeed] < 0) rQ10 = -rQ10;
                rQ10 = Limit(rQ10, -(31 << 10), 30 << 10);

                int q1Q10 = rQ10 - offsetQ10;
                int q1Q0 = q1Q10 >> 10;
                int q2Q10, rd1Q10, rd2Q10;
                if (q1Q0 > 0)
                {
                    q1Q10 = (q1Q0 << 10) - 80;
                    q1Q10 += offsetQ10;
                    q2Q10 = q1Q10 + 1024;
                    rd1Q10 = SMulBB(q1Q10, lambdaQ10);
                    rd2Q10 = SMulBB(q2Q10, lambdaQ10);
                }
                else if (q1Q0 == 0)
                {
                    q1Q10 = offsetQ10;
                    q2Q10 = q1Q10 + (1024 - 80);
                    rd1Q10 = SMulBB(q1Q10, lambdaQ10);
                    rd2Q10 = SMulBB(q2Q10, lambdaQ10);
                }
                else if (q1Q0 == -1)
                {
                    q2Q10 = offsetQ10;
                    q1Q10 = q2Q10 - (1024 - 80);
                    rd1Q10 = SMulBB(-q1Q10, lambdaQ10);
                    rd2Q10 = SMulBB(q2Q10, lambdaQ10);
                }
                else
                {
                    q1Q10 = (q1Q0 << 10) + 80;
                    q1Q10 += offsetQ10;
                    q2Q10 = q1Q10 + 1024;
                    rd1Q10 = SMulBB(-q1Q10, lambdaQ10);
                    rd2Q10 = SMulBB(-q2Q10, lambdaQ10);
                }
                int rrQ10 = rQ10 - q1Q10;
                rd1Q10 = SMlaBB(rd1Q10, rrQ10, rrQ10) >> 10;
                rrQ10 = rQ10 - q2Q10;
                rd2Q10 = SMlaBB(rd2Q10, rrQ10, rrQ10) >> 10;

                if (rd1Q10 < rd2Q10)
                {
                    ss[sBase + SsRd] = d[DdRd] + rd1Q10;
                    ss[sBase + SsSize + SsRd] = d[DdRd] + rd2Q10;
                    ss[sBase + SsQ] = q1Q10;
                    ss[sBase + SsSize + SsQ] = q2Q10;
                }
                else
                {
                    ss[sBase + SsRd] = d[DdRd] + rd2Q10;
                    ss[sBase + SsSize + SsRd] = d[DdRd] + rd1Q10;
                    ss[sBase + SsQ] = q2Q10;
                    ss[sBase + SsSize + SsQ] = q1Q10;
                }

                for (int s = 0; s < 2; s++)
                {
                    int b = sBase + s * SsSize;
                    int excQ14 = ss[b + SsQ] << 4;
                    if (d[DdSeed] < 0) excQ14 = -excQ14;
                    int lpcExcQ14 = excQ14 + ltpPredQ14;
                    int xqQ14 = lpcExcQ14 + lpcPredQ14;
                    int sLfArShpQ14 = xqQ14 - nArQ14;
                    ss[b + SsShape] = sLfArShpQ14 - nLfQ14;
                    ss[b + SsLfAr] = sLfArShpQ14;
                    ss[b + SsLpcExc] = lpcExcQ14;
                    ss[b + SsXq] = xqQ14;
                }
            }

            smplBufIdx = (smplBufIdx - 1) & DecisionDelayMask;
            int lastSmpleIdx = (smplBufIdx + decisionDelay) & DecisionDelayMask;

            int rdMinQ10 = ss[SsRd];
            int winnerInd = 0;
            for (int k = 1; k < nStates; k++)
            {
                if (ss[k * 2 * SsSize + SsRd] < rdMinQ10) { rdMinQ10 = ss[k * 2 * SsSize + SsRd]; winnerInd = k; }
            }
            int winnerRandState = _dd[winnerInd][DdRand + lastSmpleIdx];
            for (int k = 0; k < nStates; k++)
            {
                if (_dd[k][DdRand + lastSmpleIdx] != winnerRandState)
                {
                    ss[k * 2 * SsSize + SsRd] += int.MaxValue >> 4;
                    ss[k * 2 * SsSize + SsSize + SsRd] += int.MaxValue >> 4;
                }
            }

            int rdMaxQ10 = ss[SsRd];
            rdMinQ10 = ss[SsSize + SsRd];
            int rdMaxInd = 0, rdMinInd = 0;
            for (int k = 1; k < nStates; k++)
            {
                if (ss[k * 2 * SsSize + SsRd] > rdMaxQ10) { rdMaxQ10 = ss[k * 2 * SsSize + SsRd]; rdMaxInd = k; }
                if (ss[k * 2 * SsSize + SsSize + SsRd] < rdMinQ10) { rdMinQ10 = ss[k * 2 * SsSize + SsSize + SsRd]; rdMinInd = k; }
            }
            if (rdMinQ10 < rdMaxQ10)
            {
                // Replace the worst survivor by the best alternative; the first i history words are not needed again.
                Array.Copy(_dd[rdMinInd], i, _dd[rdMaxInd], i, DdSize - i);
                Array.Copy(ss, (rdMinInd * 2 + 1) * SsSize, ss, rdMaxInd * 2 * SsSize, SsSize);
            }

            var psDd = _dd[winnerInd];
            if (subfr > 0 || i >= decisionDelay)
            {
                pulses[pOff + i - decisionDelay] = (sbyte)RShiftRound(psDd[DdQ + lastSmpleIdx], 10);
                Xq[xqOff + i - decisionDelay] = (short)Sat16(RShiftRound(SMulWW(psDd[DdXq + lastSmpleIdx], delayedGainQ10[lastSmpleIdx]), 8));
                SLtpShpQ14[SLtpShpBufIdx - decisionDelay] = psDd[DdShape + lastSmpleIdx];
                sLtpQ15[SLtpBufIdx - decisionDelay] = psDd[DdPred + lastSmpleIdx];
            }
            SLtpShpBufIdx++;
            SLtpBufIdx++;

            for (int k = 0; k < nStates; k++)
            {
                var d = _dd[k];
                int b = k * 2 * SsSize;
                d[DdLfAr] = ss[b + SsLfAr];
                d[DdSLpc + NsqLpcBufLength + i] = ss[b + SsXq];
                d[DdXq + smplBufIdx] = ss[b + SsXq];
                d[DdQ + smplBufIdx] = ss[b + SsQ];
                d[DdPred + smplBufIdx] = ss[b + SsLpcExc] << 1;
                d[DdShape + smplBufIdx] = ss[b + SsShape];
                d[DdSeed] = unchecked(d[DdSeed] + RShiftRound(ss[b + SsQ], 10));
                d[DdRand + smplBufIdx] = d[DdSeed];
                d[DdRd] = ss[b + SsRd];
            }
            delayedGainQ10[smplBufIdx] = gainQ10;
        }
        for (int k = 0; k < nStates; k++)
            Array.Copy(_dd[k], DdSLpc + length, _dd[k], DdSLpc, NsqLpcBufLength);
    }

    private void ScaleStatesDelDec(SilkEncState e, ReadOnlySpan<int> xQ3, int subfr, int nStates, int ltpScaleQ14,
        ReadOnlySpan<int> gainsQ16, ReadOnlySpan<int> pitchL, int signalType, int decisionDelay)
    {
        var sLtpQ15 = _sLtpQ15;
        int lag = pitchL[subfr];
        int invGainQ31 = Inverse32VarQ(Math.Max(gainsQ16[subfr], 1), 47);
        int gainAdjQ16 = gainsQ16[subfr] != PrevGainQ16 ? Div32VarQ(PrevGainQ16, gainsQ16[subfr], 16) : 1 << 16;
        int invGainQ23 = RShiftRound(invGainQ31, 8);
        for (int i = 0; i < e.SubfrLength; i++) _xScQ10[i] = SMulWW(xQ3[i], invGainQ23);
        PrevGainQ16 = gainsQ16[subfr];

        if (RewhiteFlag != 0)
        {
            if (subfr == 0) invGainQ31 = SMulWB(invGainQ31, ltpScaleQ14) << 2;
            for (int i = SLtpBufIdx - lag - LtpOrder / 2; i < SLtpBufIdx; i++)
                sLtpQ15[i] = SMulWB(invGainQ31, _sLtp[i]);
        }

        if (gainAdjQ16 != 1 << 16)
        {
            for (int i = SLtpShpBufIdx - e.LtpMemLength; i < SLtpShpBufIdx; i++)
                SLtpShpQ14[i] = SMulWW(gainAdjQ16, SLtpShpQ14[i]);
            if (signalType == SilkEncState.TypeVoiced && RewhiteFlag == 0)
                for (int i = SLtpBufIdx - lag - LtpOrder / 2; i < SLtpBufIdx - decisionDelay; i++)
                    sLtpQ15[i] = SMulWW(gainAdjQ16, sLtpQ15[i]);
            for (int k = 0; k < nStates; k++)
            {
                var d = _dd[k];
                d[DdLfAr] = SMulWW(gainAdjQ16, d[DdLfAr]);
                for (int i = 0; i < NsqLpcBufLength; i++) d[DdSLpc + i] = SMulWW(gainAdjQ16, d[DdSLpc + i]);
                for (int i = 0; i < MaxShapeLpcOrder; i++) d[DdSAr2 + i] = SMulWW(gainAdjQ16, d[DdSAr2 + i]);
                for (int i = 0; i < DecisionDelay; i++)
                {
                    d[DdPred + i] = SMulWW(gainAdjQ16, d[DdPred + i]);
                    d[DdShape + i] = SMulWW(gainAdjQ16, d[DdShape + i]);
                }
            }
        }
    }
}
