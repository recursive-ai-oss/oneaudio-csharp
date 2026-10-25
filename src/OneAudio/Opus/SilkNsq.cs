using static OneAudio.Opus.SilkMath;
using static OneAudio.Opus.SilkEncMath;

namespace OneAudio.Opus;

internal sealed partial class SilkNsqState
{
    public const int NsqLpcBufLength = 32;
    private const int LtpOrder = 5;
    private const int MaxShapeLpcOrder = 16;
    private const int MaxLpcOrder = 16;

    private readonly int[] _sLtpQ15 = new int[2 * SilkEncState.MaxFrameLength];
    private readonly short[] _sLtp = new short[2 * SilkEncState.MaxFrameLength];
    private readonly int[] _xScQ10 = new int[SilkEncState.MaxSubFrameLength];

    /// <summary>silk_NSQ: noise shaping quantisation of a frame with a single survivor path.</summary>
    public void Quantize(SilkEncState e, SilkIndices ind, ReadOnlySpan<int> xQ3, sbyte[] pulses,
        ReadOnlySpan<short> predCoefQ12, ReadOnlySpan<short> ltpCoefQ14, ReadOnlySpan<short> ar2Q13,
        ReadOnlySpan<int> harmShapeGainQ14, ReadOnlySpan<int> tiltQ14, ReadOnlySpan<int> lfShpQ14,
        ReadOnlySpan<int> gainsQ16, ReadOnlySpan<int> pitchL, int lambdaQ10, int ltpScaleQ14)
    {
        RandSeed = ind.Seed;
        int lag = LagPrev;
        int offsetQ10 = SilkTables.QuantizationOffsetsQ10[(ind.SignalType >> 1) * 2 + ind.QuantOffsetType];
        int lsfInterpolationFlag = ind.NlsfInterpCoefQ2 == 4 ? 0 : 1;
        SLtpShpBufIdx = e.LtpMemLength;
        SLtpBufIdx = e.LtpMemLength;
        int pxq = e.LtpMemLength;
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
                    int startIdx = e.LtpMemLength - lag - e.PredictLpcOrder - LtpOrder / 2;
                    SilkChannel.LpcAnalysisFilter(_sLtp.AsSpan(startIdx), Xq.AsSpan(startIdx + k * e.SubfrLength), aQ12,
                        e.LtpMemLength - startIdx, e.PredictLpcOrder);
                    RewhiteFlag = 1;
                    SLtpBufIdx = e.LtpMemLength;
                }
            }
            ScaleStates(e, xQ3.Slice(xOff), k, ltpScaleQ14, gainsQ16, pitchL, ind.SignalType);
            NoiseShapeQuantizer(ind.SignalType, pulses, pOff, pxq, aQ12, bQ14, arShpQ13, lag, harmShapeFirPackedQ14,
                tiltQ14[k], lfShpQ14[k], gainsQ16[k], lambdaQ10, offsetQ10, e.SubfrLength, e.ShapingLpcOrder, e.PredictLpcOrder);
            xOff += e.SubfrLength;
            pOff += e.SubfrLength;
            pxq += e.SubfrLength;
        }
        LagPrev = pitchL[e.NbSubfr - 1];
        Array.Copy(Xq, e.FrameLength, Xq, 0, e.LtpMemLength);
        Array.Copy(SLtpShpQ14, e.FrameLength, SLtpShpQ14, 0, e.LtpMemLength);
    }

    private void NoiseShapeQuantizer(int signalType, sbyte[] pulses, int pOff, int xqOff, ReadOnlySpan<short> aQ12,
        ReadOnlySpan<short> bQ14, ReadOnlySpan<short> arShpQ13, int lag, int harmShapeFirPackedQ14, int tiltQ14, int lfShpQ14,
        int gainQ16, int lambdaQ10, int offsetQ10, int length, int shapingLpcOrder, int predictLpcOrder)
    {
        var sLtpQ15 = _sLtpQ15;
        var xScQ10 = _xScQ10;
        int shpLag = SLtpShpBufIdx - lag + 3 / 2;
        int predLag = SLtpBufIdx - lag + LtpOrder / 2;
        int gainQ10 = gainQ16 >> 6;
        int psLpc = NsqLpcBufLength - 1;
        var sLpc = SLpcQ14;
        for (int i = 0; i < length; i++)
        {
            RandSeed = Rand(RandSeed);
            int lpcPredQ10 = predictLpcOrder >> 1;
            for (int j = 0; j < 10; j++) lpcPredQ10 = SMlaWB(lpcPredQ10, sLpc[psLpc - j], aQ12[j]);
            if (predictLpcOrder == 16)
                for (int j = 10; j < 16; j++) lpcPredQ10 = SMlaWB(lpcPredQ10, sLpc[psLpc - j], aQ12[j]);

            int ltpPredQ13;
            if (signalType == SilkEncState.TypeVoiced)
            {
                ltpPredQ13 = 2;
                for (int j = 0; j < 5; j++) ltpPredQ13 = SMlaWB(ltpPredQ13, sLtpQ15[predLag - j], bQ14[j]);
                predLag++;
            }
            else ltpPredQ13 = 0;

            int tmp2 = sLpc[psLpc];
            int tmp1 = SAr2Q14[0];
            SAr2Q14[0] = tmp2;
            int nArQ12 = shapingLpcOrder >> 1;
            nArQ12 = SMlaWB(nArQ12, tmp2, arShpQ13[0]);
            for (int j = 2; j < shapingLpcOrder; j += 2)
            {
                tmp2 = SAr2Q14[j - 1];
                SAr2Q14[j - 1] = tmp1;
                nArQ12 = SMlaWB(nArQ12, tmp1, arShpQ13[j - 1]);
                tmp1 = SAr2Q14[j];
                SAr2Q14[j] = tmp2;
                nArQ12 = SMlaWB(nArQ12, tmp2, arShpQ13[j]);
            }
            SAr2Q14[shapingLpcOrder - 1] = tmp1;
            nArQ12 = SMlaWB(nArQ12, tmp1, arShpQ13[shapingLpcOrder - 1]);
            nArQ12 <<= 1;
            nArQ12 = SMlaWB(nArQ12, SLfArShpQ14, tiltQ14);

            int nLfQ12 = SMulWB(SLtpShpQ14[SLtpShpBufIdx - 1], lfShpQ14);
            nLfQ12 = SMlaWT(nLfQ12, SLfArShpQ14, lfShpQ14);

            tmp1 = unchecked((lpcPredQ10 << 2) - nArQ12);
            tmp1 -= nLfQ12;
            if (lag > 0)
            {
                int nLtpQ13 = SMulWB(SLtpShpQ14[shpLag] + SLtpShpQ14[shpLag - 2], harmShapeFirPackedQ14);
                nLtpQ13 = SMlaWT(nLtpQ13, SLtpShpQ14[shpLag - 1], harmShapeFirPackedQ14);
                nLtpQ13 <<= 1;
                shpLag++;
                tmp2 = ltpPredQ13 - nLtpQ13;
                tmp1 = tmp2 + (tmp1 << 1);
                tmp1 = RShiftRound(tmp1, 3);
            }
            else tmp1 = RShiftRound(tmp1, 2);

            int rQ10 = xScQ10[i] - tmp1;
            if (RandSeed < 0) rQ10 = -rQ10;
            rQ10 = Limit(rQ10, -(31 << 10), 30 << 10);

            int q1Q10 = rQ10 - offsetQ10;
            int q1Q0 = q1Q10 >> 10;
            int q2Q10, rd1Q20, rd2Q20;
            if (q1Q0 > 0)
            {
                q1Q10 = (q1Q0 << 10) - 80;
                q1Q10 += offsetQ10;
                q2Q10 = q1Q10 + 1024;
                rd1Q20 = SMulBB(q1Q10, lambdaQ10);
                rd2Q20 = SMulBB(q2Q10, lambdaQ10);
            }
            else if (q1Q0 == 0)
            {
                q1Q10 = offsetQ10;
                q2Q10 = q1Q10 + (1024 - 80);
                rd1Q20 = SMulBB(q1Q10, lambdaQ10);
                rd2Q20 = SMulBB(q2Q10, lambdaQ10);
            }
            else if (q1Q0 == -1)
            {
                q2Q10 = offsetQ10;
                q1Q10 = q2Q10 - (1024 - 80);
                rd1Q20 = SMulBB(-q1Q10, lambdaQ10);
                rd2Q20 = SMulBB(q2Q10, lambdaQ10);
            }
            else
            {
                q1Q10 = (q1Q0 << 10) + 80;
                q1Q10 += offsetQ10;
                q2Q10 = q1Q10 + 1024;
                rd1Q20 = SMulBB(-q1Q10, lambdaQ10);
                rd2Q20 = SMulBB(-q2Q10, lambdaQ10);
            }
            int rrQ10 = rQ10 - q1Q10;
            rd1Q20 = SMlaBB(rd1Q20, rrQ10, rrQ10);
            rrQ10 = rQ10 - q2Q10;
            rd2Q20 = SMlaBB(rd2Q20, rrQ10, rrQ10);
            if (rd2Q20 < rd1Q20) q1Q10 = q2Q10;

            pulses[pOff + i] = (sbyte)RShiftRound(q1Q10, 10);

            int excQ14 = q1Q10 << 4;
            if (RandSeed < 0) excQ14 = -excQ14;
            int lpcExcQ14 = excQ14 + (ltpPredQ13 << 1);
            int xqQ14 = lpcExcQ14 + (lpcPredQ10 << 4);
            Xq[xqOff + i] = (short)Sat16(RShiftRound(SMulWW(xqQ14, gainQ10), 8));

            psLpc++;
            sLpc[psLpc] = xqQ14;
            int sLfArShpQ14 = xqQ14 - (nArQ12 << 2);
            SLfArShpQ14 = sLfArShpQ14;
            SLtpShpQ14[SLtpShpBufIdx] = sLfArShpQ14 - (nLfQ12 << 2);
            sLtpQ15[SLtpBufIdx] = lpcExcQ14 << 1;
            SLtpShpBufIdx++;
            SLtpBufIdx++;

            RandSeed = unchecked(RandSeed + pulses[pOff + i]);
        }
        Array.Copy(sLpc, length, sLpc, 0, NsqLpcBufLength);
    }

    private void ScaleStates(SilkEncState e, ReadOnlySpan<int> xQ3, int subfr, int ltpScaleQ14, ReadOnlySpan<int> gainsQ16,
        ReadOnlySpan<int> pitchL, int signalType)
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
                for (int i = SLtpBufIdx - lag - LtpOrder / 2; i < SLtpBufIdx; i++)
                    sLtpQ15[i] = SMulWW(gainAdjQ16, sLtpQ15[i]);
            SLfArShpQ14 = SMulWW(gainAdjQ16, SLfArShpQ14);
            for (int i = 0; i < NsqLpcBufLength; i++) SLpcQ14[i] = SMulWW(gainAdjQ16, SLpcQ14[i]);
            for (int i = 0; i < MaxShapeLpcOrder; i++) SAr2Q14[i] = SMulWW(gainAdjQ16, SAr2Q14[i]);
        }
    }

    /// <summary>Copies the complete state of another quantizer (used to try several gain choices).</summary>
    public void CopyFrom(SilkNsqState o)
    {
        Array.Copy(o.Xq, Xq, Xq.Length);
        Array.Copy(o.SLtpShpQ14, SLtpShpQ14, SLtpShpQ14.Length);
        Array.Copy(o.SLpcQ14, SLpcQ14, SLpcQ14.Length);
        Array.Copy(o.SAr2Q14, SAr2Q14, SAr2Q14.Length);
        SLfArShpQ14 = o.SLfArShpQ14; LagPrev = o.LagPrev; SLtpBufIdx = o.SLtpBufIdx; SLtpShpBufIdx = o.SLtpShpBufIdx;
        RandSeed = o.RandSeed; PrevGainQ16 = o.PrevGainQ16; RewhiteFlag = o.RewhiteFlag;
    }
}
