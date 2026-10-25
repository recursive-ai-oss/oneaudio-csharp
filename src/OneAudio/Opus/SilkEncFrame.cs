using static OneAudio.Opus.SilkMath;
using static OneAudio.Opus.SilkEncMath;

namespace OneAudio.Opus;

internal sealed partial class SilkEncState
{
    private const int LaPitchMax = 32;

    public readonly SilkPitchAnalysis PitchAnalysis = new();
    private readonly SilkNsqState _nsqCopy = new();
    private readonly SilkNsqState _nsqCopy2 = new();
    private readonly SilkNsqState _nsqLbrr = new();
    private readonly float[] _xfw = new float[MaxFrameLength];
    private readonly float[] _resPitch = new float[2 * MaxFrameLength + LaPitchMax];
    private readonly float[] _wsig = new float[20 * 16 + 64];
    private readonly float[] _lpcInPre = new float[MaxNbSubfr * MaxLpcOrder + MaxFrameLength];
    private readonly float[] _wLtp = new float[MaxNbSubfr * LtpOrder * LtpOrder];
    private readonly int[] _xQ3 = new int[MaxFrameLength];
    private readonly byte[] _ecBufCopy = new byte[1275];
    private readonly SilkEncCtrl _ctrl = new();

    // ---- VAD wrapper (silk_encode_do_VAD_FLP) ----

    public void DoVad()
    {
        VadGetSa(InputBuf.AsSpan(1));
        if (SpeechActivityQ8 < 13) // 0.05 in Q8
        {
            Indices.SignalType = TypeNoVoiceActivity;
            NoSpeechCounter++;
            if (NoSpeechCounter < 10)
            {
                InDtx = false;
            }
            else if (NoSpeechCounter > 20 + 10)
            {
                NoSpeechCounter = 10;
                InDtx = false;
            }
            VadFlags[NFramesEncoded] = 0;
        }
        else
        {
            NoSpeechCounter = 0;
            InDtx = false;
            Indices.SignalType = TypeUnvoiced;
            VadFlags[NFramesEncoded] = 1;
        }
    }

    // ---- Prediction analysis ----

    private void FindPitchLags(SilkEncCtrl c, float[] res, int xFrame)
    {
        Span<float> autoCorr = stackalloc float[17];
        Span<float> a = stackalloc float[16];
        Span<float> reflCoef = stackalloc float[16];
        int bufLen = LaPitch + FrameLength + LtpMemLength;
        int xBuf = xFrame - LtpMemLength;
        int xBufPtr = xBuf + bufLen - PitchLpcWinLength;
        var wsig = _wsig;
        var xb = XBuf;
        int wp = 0;
        SilkFlp.ApplySineWindow(wsig.AsSpan(wp), xb.AsSpan(xBufPtr), 1, LaPitch);
        wp += LaPitch;
        xBufPtr += LaPitch;
        xb.AsSpan(xBufPtr, PitchLpcWinLength - (LaPitch << 1)).CopyTo(wsig.AsSpan(wp));
        wp += PitchLpcWinLength - (LaPitch << 1);
        xBufPtr += PitchLpcWinLength - (LaPitch << 1);
        SilkFlp.ApplySineWindow(wsig.AsSpan(wp), xb.AsSpan(xBufPtr), 2, LaPitch);

        SilkFlp.Autocorrelation(autoCorr, wsig, PitchLpcWinLength, PitchEstimationLpcOrder + 1);
        autoCorr[0] += autoCorr[0] * 1e-3f + 1;
        float resNrg = SilkFlp.Schur(reflCoef, autoCorr, PitchEstimationLpcOrder);
        c.PredGain = autoCorr[0] / (resNrg > 1.0f ? resNrg : 1.0f);
        SilkFlp.K2A(a, reflCoef, PitchEstimationLpcOrder);
        SilkFlp.BwExpander(a, PitchEstimationLpcOrder, 0.99f);
        SilkFlp.LpcAnalysisFilter(res, a, xb.AsSpan(xBuf), bufLen, PitchEstimationLpcOrder);

        if (Indices.SignalType != TypeNoVoiceActivity && !FirstFrameAfterReset)
        {
            float thrhld = 0.6f;
            thrhld -= 0.004f * PitchEstimationLpcOrder;
            thrhld -= 0.1f * SpeechActivityQ8 * (1.0f / 256.0f);
            thrhld -= 0.15f * (PrevSignalType >> 1);
            thrhld -= 0.1f * InputTiltQ15 * (1.0f / 32768.0f);

            bool unvoiced = PitchAnalysis.Analyze(res, c.PitchL, out short lagIndex, out sbyte contourIndex, ref LtpCorr, PrevLag,
                PitchEstimationThresholdQ16 / 65536.0f, thrhld, FsKHz, PitchEstimationComplexity, NbSubfr);
            Indices.LagIndex = lagIndex;
            Indices.ContourIndex = contourIndex;
            Indices.SignalType = unvoiced ? (sbyte)TypeUnvoiced : (sbyte)TypeVoiced;
        }
        else
        {
            Array.Clear(c.PitchL);
            Indices.LagIndex = 0;
            Indices.ContourIndex = 0;
            LtpCorr = 0;
        }
    }

    private static void A2NlsfFlp(Span<short> nlsfQ15, ReadOnlySpan<float> ar, int order)
    {
        Span<int> aFixQ16 = stackalloc int[16];
        for (int i = 0; i < order; i++) aFixQ16[i] = SilkFlp.Float2Int(ar[i] * 65536.0f);
        SilkNlsf.A2Nlsf(nlsfQ15, aFixQ16, order);
    }

    private static void Nlsf2AFlp(Span<float> ar, ReadOnlySpan<short> nlsfQ15, int order)
    {
        Span<short> aFixQ12 = stackalloc short[16];
        SilkNlsf.Nlsf2A(aFixQ12, nlsfQ15, order);
        for (int i = 0; i < order; i++) ar[i] = aFixQ12[i] * (1.0f / 4096.0f);
    }

    private void FindLpc(Span<short> nlsfQ15, ReadOnlySpan<float> x, float minInvGain)
    {
        Span<float> a = stackalloc float[16];
        Span<float> aTmp = stackalloc float[16];
        Span<short> nlsf0Q15 = stackalloc short[16];
        Span<float> lpcRes = stackalloc float[MaxFrameLength + MaxNbSubfr * MaxLpcOrder];
        int subfrLength = SubfrLength + PredictLpcOrder;
        Indices.NlsfInterpCoefQ2 = 4;

        float resNrg = SilkFlp.BurgModified(a, x, minInvGain, subfrLength, NbSubfr, PredictLpcOrder);

        if (UseInterpolatedNlsfs != 0 && !FirstFrameAfterReset && NbSubfr == MaxNbSubfr)
        {
            resNrg -= SilkFlp.BurgModified(aTmp, x.Slice((MaxNbSubfr / 2) * subfrLength), minInvGain, subfrLength, MaxNbSubfr / 2, PredictLpcOrder);
            A2NlsfFlp(nlsfQ15, aTmp, PredictLpcOrder);
            float resNrg2nd = float.MaxValue;
            for (int k = 3; k >= 0; k--)
            {
                SilkNlsf.Interpolate(nlsf0Q15, PrevNlsfqQ15, nlsfQ15, k, PredictLpcOrder);
                Nlsf2AFlp(aTmp, nlsf0Q15, PredictLpcOrder);
                SilkFlp.LpcAnalysisFilter(lpcRes, aTmp, x, 2 * subfrLength, PredictLpcOrder);
                float resNrgInterp = (float)(
                    SilkFlp.Energy(lpcRes.Slice(PredictLpcOrder), subfrLength - PredictLpcOrder) +
                    SilkFlp.Energy(lpcRes.Slice(PredictLpcOrder + subfrLength), subfrLength - PredictLpcOrder));
                if (resNrgInterp < resNrg)
                {
                    resNrg = resNrgInterp;
                    Indices.NlsfInterpCoefQ2 = (sbyte)k;
                }
                else if (resNrgInterp > resNrg2nd)
                {
                    break;
                }
                resNrg2nd = resNrgInterp;
            }
        }

        if (Indices.NlsfInterpCoefQ2 == 4) A2NlsfFlp(nlsfQ15, a, PredictLpcOrder);
    }

    private void ProcessNlsfs(Span<short> predCoefQ12, Span<short> nlsfQ15, ReadOnlySpan<short> prevNlsfQ15)
    {
        Span<short> nlsf0Temp = stackalloc short[16];
        Span<short> nlsfwQw = stackalloc short[16];
        Span<short> nlsfw0Temp = stackalloc short[16];
        int muQ20 = SMlaWB(3146, -268434, SpeechActivityQ8);
        if (NbSubfr == 2) muQ20 += muQ20 >> 1;

        SilkChannel.NlsfVqWeightsLaroia(nlsfwQw, nlsfQ15, PredictLpcOrder);
        bool doInterpolate = UseInterpolatedNlsfs == 1 && Indices.NlsfInterpCoefQ2 < 4;
        if (doInterpolate)
        {
            SilkNlsf.Interpolate(nlsf0Temp, prevNlsfQ15, nlsfQ15, Indices.NlsfInterpCoefQ2, PredictLpcOrder);
            SilkChannel.NlsfVqWeightsLaroia(nlsfw0Temp, nlsf0Temp, PredictLpcOrder);
            int iSqrQ15 = SMulBB(Indices.NlsfInterpCoefQ2, Indices.NlsfInterpCoefQ2) << 11;
            for (int i = 0; i < PredictLpcOrder; i++)
                nlsfwQw[i] = (short)SMlaWB(nlsfwQw[i] >> 1, nlsfw0Temp[i], iSqrQ15);
        }

        SilkNlsf.Encode(Indices.NlsfIndices, nlsfQ15, NlsfCb, nlsfwQw, muQ20, NlsfMsvqSurvivors, Indices.SignalType);

        SilkNlsf.Nlsf2A(predCoefQ12.Slice(16, 16), nlsfQ15, PredictLpcOrder);
        if (doInterpolate)
        {
            SilkNlsf.Interpolate(nlsf0Temp, prevNlsfQ15, nlsfQ15, Indices.NlsfInterpCoefQ2, PredictLpcOrder);
            SilkNlsf.Nlsf2A(predCoefQ12.Slice(0, 16), nlsf0Temp, PredictLpcOrder);
        }
        else
        {
            predCoefQ12.Slice(16, PredictLpcOrder).CopyTo(predCoefQ12.Slice(0, 16));
        }
    }

    private void LtpScaleCtrl(SilkEncCtrl c, int condCoding)
    {
        if (condCoding == CodeIndependently)
        {
            int roundLoss = PacketLossPerc + NFramesPerPacket;
            float v = roundLoss * c.LtpredCodGain * 0.1f;
            v = v > 2.0f ? 2.0f : v < 0.0f ? 0.0f : v;
            Indices.LtpScaleIndex = (sbyte)v;
        }
        else Indices.LtpScaleIndex = 0;
        c.LtpScale = (float)SilkTables.LTPScalesTableQ14[Indices.LtpScaleIndex] / 16384.0f;
    }

    private void FindPredCoefs(SilkEncCtrl c, float[] resPitch, int xFrame, int condCoding)
    {
        Span<float> invGains = stackalloc float[MaxNbSubfr];
        Span<float> wght = stackalloc float[MaxNbSubfr];
        Span<short> nlsfQ15 = stackalloc short[MaxLpcOrder];
        Span<short> predCoefQ12 = stackalloc short[32];
        var lpcInPre = _lpcInPre;
        for (int i = 0; i < NbSubfr; i++)
        {
            invGains[i] = 1.0f / c.Gains[i];
            wght[i] = invGains[i] * invGains[i];
        }

        if (Indices.SignalType == TypeVoiced)
        {
            SilkLtpEnc.FindLtp(c.LtpCoef, _wLtp, out c.LtpredCodGain, resPitch, c.PitchL, wght, SubfrLength, NbSubfr, LtpMemLength);
            SilkLtpEnc.QuantLtpGains(c.LtpCoef, Indices.LtpIndex, out sbyte per, _wLtp, MuLtpQ9, LtpQuantLowComplexity != 0, NbSubfr);
            Indices.PerIndex = per;
            LtpScaleCtrl(c, condCoding);
            SilkLtpEnc.LtpAnalysisFilter(lpcInPre, XBuf, xFrame - PredictLpcOrder, c.LtpCoef, c.PitchL, invGains, SubfrLength, NbSubfr, PredictLpcOrder);
        }
        else
        {
            int xPtr = xFrame - PredictLpcOrder;
            int pre = 0;
            for (int i = 0; i < NbSubfr; i++)
            {
                SilkFlp.ScaleCopyVector(lpcInPre.AsSpan(pre), XBuf.AsSpan(xPtr), invGains[i], SubfrLength + PredictLpcOrder);
                pre += SubfrLength + PredictLpcOrder;
                xPtr += SubfrLength;
            }
            Array.Clear(c.LtpCoef, 0, NbSubfr * LtpOrder);
            c.LtpredCodGain = 0.0f;
        }

        float minInvGain;
        if (FirstFrameAfterReset)
        {
            minInvGain = 1.0f / 1e2f;
        }
        else
        {
            minInvGain = (float)Math.Pow(2, c.LtpredCodGain / 3) / 1e4f;
            minInvGain /= 0.25f + 0.75f * c.CodingQuality;
        }

        FindLpc(nlsfQ15, lpcInPre, minInvGain);
        ProcessNlsfs(predCoefQ12, nlsfQ15, PrevNlsfqQ15);
        for (int i = 0; i < PredictLpcOrder; i++)
        {
            c.PredCoef0[i] = predCoefQ12[i] * (1.0f / 4096.0f);
            c.PredCoef1[i] = predCoefQ12[16 + i] * (1.0f / 4096.0f);
        }
        SilkLtpEnc.ResidualEnergy(c.ResNrg, lpcInPre, c.PredCoef0, c.PredCoef1, c.Gains, SubfrLength, NbSubfr, PredictLpcOrder);
        nlsfQ15.CopyTo(PrevNlsfqQ15);
    }

    // ---- Noise shaping quantisation wrapper ----

    private readonly short[] _ar2Q13 = new short[MaxNbSubfr * 16];
    private readonly int[] _lfShpQ14 = new int[MaxNbSubfr];
    private readonly int[] _tiltQ14 = new int[MaxNbSubfr];
    private readonly int[] _harmShapeGainQ14 = new int[MaxNbSubfr];
    private readonly int[] _gainsQ16 = new int[MaxNbSubfr];
    private readonly short[] _ltpCoefQ14 = new short[LtpOrder * MaxNbSubfr];
    private readonly short[] _predCoefQ12 = new short[32];

    private void NsqWrapper(SilkEncCtrl c, SilkIndices ind, SilkNsqState nsq, sbyte[] pulses, ReadOnlySpan<float> x)
    {
        for (int i = 0; i < NbSubfr; i++)
            for (int j = 0; j < ShapingLpcOrder; j++)
                _ar2Q13[i * 16 + j] = (short)SilkFlp.Float2Int(c.AR2[i * 16 + j] * 8192.0f);
        for (int i = 0; i < NbSubfr; i++)
        {
            _lfShpQ14[i] = (SilkFlp.Float2Int(c.LfArShp[i] * 16384.0f) << 16) | (ushort)SilkFlp.Float2Int(c.LfMaShp[i] * 16384.0f);
            _tiltQ14[i] = SilkFlp.Float2Int(c.Tilt[i] * 16384.0f);
            _harmShapeGainQ14[i] = SilkFlp.Float2Int(c.HarmShapeGain[i] * 16384.0f);
        }
        int lambdaQ10 = SilkFlp.Float2Int(c.Lambda * 1024.0f);
        for (int i = 0; i < NbSubfr * LtpOrder; i++) _ltpCoefQ14[i] = (short)SilkFlp.Float2Int(c.LtpCoef[i] * 16384.0f);
        for (int i = 0; i < PredictLpcOrder; i++)
        {
            _predCoefQ12[i] = (short)SilkFlp.Float2Int(c.PredCoef0[i] * 4096.0f);
            _predCoefQ12[16 + i] = (short)SilkFlp.Float2Int(c.PredCoef1[i] * 4096.0f);
        }
        for (int i = 0; i < NbSubfr; i++) _gainsQ16[i] = SilkFlp.Float2Int(c.Gains[i] * 65536.0f);
        int ltpScaleQ14 = ind.SignalType == TypeVoiced ? SilkTables.LTPScalesTableQ14[ind.LtpScaleIndex] : 0;
        for (int i = 0; i < FrameLength; i++) _xQ3[i] = SilkFlp.Float2Int(8.0 * x[i]);

        if (NStatesDelayedDecision > 1 || WarpingQ16 > 0)
            nsq.QuantizeDelDec(this, ind, _xQ3, pulses, _predCoefQ12, _ltpCoefQ14, _ar2Q13, _harmShapeGainQ14, _tiltQ14, _lfShpQ14,
                _gainsQ16, c.PitchL, lambdaQ10, ltpScaleQ14);
        else
            nsq.Quantize(this, ind, _xQ3, pulses, _predCoefQ12, _ltpCoefQ14, _ar2Q13, _harmShapeGainQ14, _tiltQ14, _lfShpQ14,
                _gainsQ16, c.PitchL, lambdaQ10, ltpScaleQ14);
    }

    // ---- In-band redundancy (LBRR) ----

    private void LbrrEncode(SilkEncCtrl c, ReadOnlySpan<float> xfw, int condCoding)
    {
        Span<int> gainsQ16 = stackalloc int[MaxNbSubfr];
        Span<float> tempGains = stackalloc float[MaxNbSubfr];
        var indLbrr = IndicesLbrr[NFramesEncoded];
        if (LbrrEnabled && SpeechActivityQ8 > 77) // 0.3 in Q8
        {
            LbrrFlags[NFramesEncoded] = 1;
            _nsqLbrr.CopyFrom(Nsq);
            indLbrr.CopyFrom(Indices);
            c.Gains.AsSpan(0, NbSubfr).CopyTo(tempGains);
            if (NFramesEncoded == 0 || LbrrFlags[NFramesEncoded - 1] == 0)
            {
                LbrrPrevLastGainIndex = ShapeLastGainIndex;
                indLbrr.GainsIndices[0] = (sbyte)Math.Min(indLbrr.GainsIndices[0] + LbrrGainIncreases, 63);
            }
            GainsDequant(gainsQ16, indLbrr.GainsIndices, ref LbrrPrevLastGainIndex, condCoding == CodeConditionally, NbSubfr);
            for (int k = 0; k < NbSubfr; k++) c.Gains[k] = gainsQ16[k] * (1.0f / 65536.0f);
            NsqWrapper(c, indLbrr, _nsqLbrr, PulsesLbrr[NFramesEncoded], xfw);
            tempGains.CopyTo(c.Gains);
        }
    }

    // ---- Frame encoding (silk_encode_frame_FLP) ----

    /// <summary>Encodes one frame; returns the number of bytes written so far in the packet (0 while prefilling).</summary>
    public int EncodeFrame(ref RangeEncoder rc, int condCoding, int maxBits, bool useCbr)
    {
        var c = _ctrl;
        var xfw = _xfw;
        var resPitch = _resPitch;
        Span<int> pGainsQ16 = stackalloc int[MaxNbSubfr];
        int nBits = 0, nBitsLower = 0, nBitsUpper = 0, gainMultLower = 0, gainMultUpper = 0;
        sbyte lastGainIndexCopy2 = 0;

        Indices.Seed = (sbyte)(FrameCounter++ & 3);
        int xFrame = LtpMemLength;

        LpVariableCutoff();
        for (int i = 0; i < FrameLength; i++) XBuf[xFrame + LaShapeMs * FsKHz + i] = InputBuf[1 + i];
        for (int i = 0; i < 8; i++)
            XBuf[xFrame + LaShapeMs * FsKHz + i * (FrameLength >> 3)] += (1 - (i & 2)) * 1e-6f;

        if (!PrefillFlag)
        {
            FindPitchLags(c, resPitch, xFrame);
            NoiseShapeAnalysis(c, resPitch, LtpMemLength, xFrame);
            FindPredCoefs(c, resPitch, xFrame, condCoding);
            ProcessGains(c, condCoding);
            Prefilter(c, xfw, xFrame);
            LbrrEncode(c, xfw, condCoding);

            const int maxIter = 6;
            int gainMultQ8 = 256;
            bool foundLower = false, foundUpper = false;
            int gainsId = GainsId(Indices.GainsIndices, NbSubfr);
            int gainsIdLower = -1, gainsIdUpper = -1;

            var rcCopy = rc;
            _nsqCopy.CopyFrom(Nsq);
            int seedCopy = Indices.Seed;
            short ecPrevLagIndexCopy = EcPrevLagIndex;
            int ecPrevSignalTypeCopy = EcPrevSignalType;
            RangeEncoder rcCopy2 = rc;

            for (int iter = 0; ; iter++)
            {
                if (gainsId == gainsIdLower)
                {
                    nBits = nBitsLower;
                }
                else if (gainsId == gainsIdUpper)
                {
                    nBits = nBitsUpper;
                }
                else
                {
                    if (iter > 0)
                    {
                        rc = rcCopy;
                        Nsq.CopyFrom(_nsqCopy);
                        Indices.Seed = (sbyte)seedCopy;
                        EcPrevLagIndex = ecPrevLagIndexCopy;
                        EcPrevSignalType = ecPrevSignalTypeCopy;
                    }
                    NsqWrapper(c, Indices, Nsq, Pulses, xfw);
                    EncodeIndices(ref rc, NFramesEncoded, false, condCoding);
                    EncodePulses(ref rc, Indices.SignalType, Indices.QuantOffsetType, Pulses, FrameLength);
                    nBits = rc.Tell();
                    if (!useCbr && iter == 0 && nBits <= maxBits) break;
                }

                if (iter == maxIter)
                {
                    if (foundLower && (gainsId == gainsIdLower || nBits > maxBits))
                    {
                        rc = rcCopy2;
                        _ecBufCopy.AsSpan(0, rcCopy2.Offs).CopyTo(rc.Buffer);
                        Nsq.CopyFrom(_nsqCopy2);
                        ShapeLastGainIndex = lastGainIndexCopy2;
                    }
                    break;
                }

                if (nBits > maxBits)
                {
                    if (!foundLower && iter >= 2)
                    {
                        c.Lambda *= 1.5f;
                        foundUpper = false;
                        gainsIdUpper = -1;
                    }
                    else
                    {
                        foundUpper = true;
                        nBitsUpper = nBits;
                        gainMultUpper = gainMultQ8;
                        gainsIdUpper = gainsId;
                    }
                }
                else if (nBits < maxBits - 5)
                {
                    foundLower = true;
                    nBitsLower = nBits;
                    gainMultLower = gainMultQ8;
                    if (gainsId != gainsIdLower)
                    {
                        gainsIdLower = gainsId;
                        rcCopy2 = rc;
                        rc.Buffer.Slice(0, rc.Offs).CopyTo(_ecBufCopy);
                        _nsqCopy2.CopyFrom(Nsq);
                        lastGainIndexCopy2 = ShapeLastGainIndex;
                    }
                }
                else break;

                if (!(foundLower & foundUpper))
                {
                    int gainFactorQ16 = Log2Lin(((nBits - maxBits) << 7) / FrameLength + (16 << 7));
                    gainFactorQ16 = Math.Min(gainFactorQ16, 2 << 16);
                    if (nBits > maxBits) gainFactorQ16 = Math.Max(gainFactorQ16, 85197); // 1.3 in Q16
                    gainMultQ8 = SMulWB(gainFactorQ16, gainMultQ8);
                }
                else
                {
                    gainMultQ8 = gainMultLower + ((gainMultUpper - gainMultLower) * (maxBits - nBitsLower)) / (nBitsUpper - nBitsLower);
                    if (gainMultQ8 > gainMultLower + ((gainMultUpper - gainMultLower) >> 2))
                        gainMultQ8 = gainMultLower + ((gainMultUpper - gainMultLower) >> 2);
                    else if (gainMultQ8 < gainMultUpper - ((gainMultUpper - gainMultLower) >> 2))
                        gainMultQ8 = gainMultUpper - ((gainMultUpper - gainMultLower) >> 2);
                }

                for (int i = 0; i < NbSubfr; i++)
                    pGainsQ16[i] = LShiftSat32(SMulWB(c.GainsUnqQ16[i], (short)gainMultQ8), 8);
                ShapeLastGainIndex = c.LastGainIndexPrev;
                GainsQuant(Indices.GainsIndices, pGainsQ16, ref ShapeLastGainIndex, condCoding == CodeConditionally, NbSubfr);
                gainsId = GainsId(Indices.GainsIndices, NbSubfr);
                for (int i = 0; i < NbSubfr; i++) c.Gains[i] = pGainsQ16[i] / 65536.0f;
            }
        }

        Array.Copy(XBuf, FrameLength, XBuf, 0, LtpMemLength + LaShapeMs * FsKHz);
        PrevLag = c.PitchL[NbSubfr - 1];
        PrevSignalType = Indices.SignalType;

        if (PrefillFlag) return 0;
        FirstFrameAfterReset = false;
        return (rc.Tell() + 7) >> 3;
    }
}
