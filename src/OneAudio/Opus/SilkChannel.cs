using static OneAudio.Opus.SilkMath;

namespace OneAudio.Opus;

/// <summary>An NLSF codebook (RFC 6716 section 4.2.7.5).</summary>
internal sealed class NlsfCodebook
{
    public required int NVectors { get; init; }
    public required int Order { get; init; }
    public required int QuantStepSizeQ16 { get; init; }
    public required byte[] CB1NlsfQ8 { get; init; }
    public required byte[] CB1ICdf { get; init; }
    public required byte[] PredQ8 { get; init; }
    public required byte[] EcSel { get; init; }
    public required byte[] EcICdf { get; init; }
    public required short[] DeltaMinQ15 { get; init; }

    public static readonly NlsfCodebook NbMb = new()
    {
        NVectors = 32,
        Order = 10,
        QuantStepSizeQ16 = 11796, // 0.18 in Q16
        CB1NlsfQ8 = SilkTables.NLSFCB1NBMBQ8,
        CB1ICdf = SilkTables.NLSFCB1ICDFNBMB,
        PredQ8 = SilkTables.NLSFPREDNBMBQ8,
        EcSel = SilkTables.NLSFCB2SELECTNBMB,
        EcICdf = SilkTables.NLSFCB2ICDFNBMB,
        DeltaMinQ15 = SilkTables.NLSFDELTAMINNBMBQ15,
    };

    public static readonly NlsfCodebook Wb = new()
    {
        NVectors = 32,
        Order = 16,
        QuantStepSizeQ16 = 9830, // 0.15 in Q16
        CB1NlsfQ8 = SilkTables.NLSFCB1WBQ8,
        CB1ICdf = SilkTables.NLSFCB1ICDFWB,
        PredQ8 = SilkTables.NLSFPREDWBQ8,
        EcSel = SilkTables.NLSFCB2SELECTWB,
        EcICdf = SilkTables.NLSFCB2ICDFWB,
        DeltaMinQ15 = SilkTables.NLSFDELTAMINWBQ15,
    };
}

/// <summary>Per-channel SILK decoder state and the frame decoding pipeline (RFC 6716 section 4.2).</summary>
internal sealed class SilkChannel
{
    public const int MaxFrameLength = 320;
    public const int MaxSubFrameLength = 80;
    public const int MaxLpcOrder = 16;
    public const int LtpOrder = 5;
    private const int MaxNbSubfr = 4;
    private const int TypeNoVoiceActivity = 0;
    private const int TypeVoiced = 2;
    private const int CodeIndependently = 0;
    private const int CodeIndependentlyNoLtpScaling = 1;
    private const int CodeConditionally = 2;

    // Decoder state
    public int PrevGainQ16;
    public readonly int[] ExcQ14 = new int[MaxFrameLength];
    public readonly int[] SLpcQ14Buf = new int[MaxLpcOrder];
    public readonly short[] OutBuf = new short[MaxFrameLength + 2 * MaxSubFrameLength];
    public int LagPrev;
    public int LastGainIndex;
    public int FsKHz;
    public int FsApiHz;
    public int NbSubfr;
    public int FrameLength;
    public int SubfrLength;
    public int LtpMemLength;
    public int LpcOrder;
    public readonly short[] PrevNlsfQ15 = new short[MaxLpcOrder];
    public bool FirstFrameAfterReset;
    public byte[] PitchLagLowBitsICdf = SilkTables.Uniform4ICDF;
    public byte[] PitchContourICdf = SilkTables.PitchContourICDF;
    public int NFramesDecoded;
    public int NFramesPerPacket;
    public int EcPrevSignalType;
    public int EcPrevLagIndex;
    public readonly int[] VadFlags = new int[3];
    public int LbrrFlag;
    public readonly int[] LbrrFlags = new int[3];
    public readonly SilkResampler Resampler = new();
    public NlsfCodebook? NlsfCb;
    public int LossCnt;
    public int PrevSignalType;

    // Side information indices of the frame being decoded
    private readonly int[] _gainsIndices = new int[MaxNbSubfr];
    private readonly int[] _ltpIndex = new int[MaxNbSubfr];
    private readonly int[] _nlsfIndices = new int[MaxLpcOrder + 1];
    private int _lagIndex, _contourIndex, _signalType, _quantOffsetType, _nlsfInterpCoefQ2, _perIndex, _ltpScaleIndex, _seed;

    // Decoder control of the frame being decoded
    private readonly int[] _pitchL = new int[MaxNbSubfr];
    private readonly int[] _gainsQ16 = new int[MaxNbSubfr];
    private readonly short[] _predCoefQ12 = new short[2 * MaxLpcOrder];
    private readonly short[] _ltpCoefQ14 = new short[LtpOrder * MaxNbSubfr];
    private int _ltpScaleQ14;

    // Concealment (PLC) state
    private int _plcPitchLQ8;
    private readonly short[] _plcLtpCoefQ14 = new short[LtpOrder];
    private readonly short[] _plcPrevLpcQ12 = new short[MaxLpcOrder];
    private bool _plcLastFrameLost;
    private int _plcRandSeed;
    private short _plcRandScaleQ14;
    private int _plcConcEnergy, _plcConcEnergyShift;
    private int _plcPrevLtpScaleQ14;
    private readonly int[] _plcPrevGainQ16 = new int[2];
    private int _plcFsKHz, _plcNbSubfr, _plcSubfrLength;

    // Comfort noise state
    private readonly int[] _cngExcBufQ14 = new int[MaxFrameLength];
    private readonly short[] _cngSmthNlsfQ15 = new short[MaxLpcOrder];
    private readonly int[] _cngSynthState = new int[MaxLpcOrder];
    private int _cngSmthGainQ16, _cngRandSeed, _cngFsKHz;

    // Scratch (preallocated so a frame decode allocates nothing)
    private readonly int[] _pulses = new int[MaxFrameLength];
    private readonly short[] _sLtp = new short[MaxFrameLength];
    private readonly int[] _sLtpQ15 = new int[2 * MaxFrameLength];
    private readonly int[] _resQ14 = new int[MaxSubFrameLength];
    private readonly int[] _sLpcQ14 = new int[MaxSubFrameLength + MaxLpcOrder];
    private readonly short[] _aQ12Tmp = new short[MaxLpcOrder];
    private readonly short[] _excBuf = new short[2 * MaxSubFrameLength];
    private readonly int[] _cngSigQ10 = new int[MaxFrameLength + MaxLpcOrder];
    private readonly int[] _scratch32 = new int[32];
    private readonly short[] _nlsf = new short[MaxLpcOrder];
    private readonly short[] _nlsf0 = new short[MaxLpcOrder];
    private readonly byte[] _predQ8 = new byte[MaxLpcOrder];
    private readonly short[] _ecIx = new short[MaxLpcOrder];
    private readonly short[] _resQ10 = new short[MaxLpcOrder];
    private readonly short[] _wTmp = new short[MaxLpcOrder];
    private readonly int[] _a32 = new int[MaxLpcOrder];
    private readonly int[] _polyP = new int[MaxLpcOrder / 2 + 1];
    private readonly int[] _polyQ = new int[MaxLpcOrder / 2 + 1];
    private readonly int[] _cosLsf = new int[MaxLpcOrder];
    private readonly int[] _sumPulses = new int[MaxFrameLength / 16];
    private readonly int[] _nLshifts = new int[MaxFrameLength / 16];
    private readonly int[] _shell = new int[8 + 4 + 2];

    public SilkChannel() => Init();

    /// <summary>Resets all state, as for a new stream (silk_init_decoder).</summary>
    public void Init()
    {
        PrevGainQ16 = 65536;
        Array.Clear(ExcQ14); Array.Clear(SLpcQ14Buf); Array.Clear(OutBuf); Array.Clear(PrevNlsfQ15);
        LagPrev = 0; LastGainIndex = 0; FsKHz = 0; FsApiHz = 0; NbSubfr = 0; FrameLength = 0; SubfrLength = 0;
        LtpMemLength = 0; LpcOrder = 0; FirstFrameAfterReset = true;
        PitchLagLowBitsICdf = SilkTables.Uniform4ICDF; PitchContourICdf = SilkTables.PitchContourICDF;
        NFramesDecoded = 0; NFramesPerPacket = 0; EcPrevSignalType = 0; EcPrevLagIndex = 0;
        Array.Clear(VadFlags); LbrrFlag = 0; Array.Clear(LbrrFlags);
        Resampler.Clear();
        NlsfCb = null; LossCnt = 0; PrevSignalType = 0;
        Array.Clear(_gainsIndices); Array.Clear(_ltpIndex); Array.Clear(_nlsfIndices);
        _lagIndex = _contourIndex = _signalType = _quantOffsetType = _nlsfInterpCoefQ2 = _perIndex = _ltpScaleIndex = _seed = 0;
        Array.Clear(_pitchL); Array.Clear(_gainsQ16); Array.Clear(_predCoefQ12); Array.Clear(_ltpCoefQ14); _ltpScaleQ14 = 0;
        _plcLtpCoefQ14.AsSpan().Clear(); _plcPrevLpcQ12.AsSpan().Clear(); _plcLastFrameLost = false; _plcRandSeed = 0;
        _plcRandScaleQ14 = 0; _plcConcEnergy = 0; _plcConcEnergyShift = 0; _plcPrevLtpScaleQ14 = 0;
        _plcFsKHz = 0;
        Array.Clear(_cngExcBufQ14); Array.Clear(_cngSynthState);
        _cngSmthGainQ16 = 0; _cngFsKHz = 0;
        CngReset();
        PlcReset();
    }

    private void CngReset()
    {
        int step = short.MaxValue / (LpcOrder + 1);
        int acc = 0;
        _cngSmthNlsfQ15.AsSpan().Clear();
        for (int i = 0; i < LpcOrder; i++)
        {
            acc += step;
            _cngSmthNlsfQ15[i] = (short)acc;
        }
        _cngSmthGainQ16 = 0;
        _cngRandSeed = 3176576;
    }

    private void PlcReset()
    {
        _plcPitchLQ8 = FrameLength << (8 - 1);
        _plcPrevGainQ16[0] = 65536;
        _plcPrevGainQ16[1] = 65536;
        _plcSubfrLength = 20;
        _plcNbSubfr = 2;
    }

    /// <summary>Selects the internal rate and frame structure (silk_decoder_set_fs).</summary>
    public void SetFs(int fsKHz, int fsApiHz)
    {
        SubfrLength = 5 * fsKHz;
        int frameLength = NbSubfr * SubfrLength;
        if (FsKHz != fsKHz || FsApiHz != fsApiHz)
        {
            Resampler.Init(fsKHz * 1000, fsApiHz);
            FsApiHz = fsApiHz;
        }
        if (FsKHz != fsKHz || frameLength != FrameLength)
        {
            if (fsKHz == 8)
                PitchContourICdf = NbSubfr == MaxNbSubfr ? SilkTables.PitchContourNBICDF : SilkTables.PitchContour10MsNBICDF;
            else
                PitchContourICdf = NbSubfr == MaxNbSubfr ? SilkTables.PitchContourICDF : SilkTables.PitchContour10MsICDF;
            if (FsKHz != fsKHz)
            {
                LtpMemLength = 20 * fsKHz;
                if (fsKHz == 8 || fsKHz == 12)
                {
                    LpcOrder = 10;
                    NlsfCb = NlsfCodebook.NbMb;
                }
                else
                {
                    LpcOrder = 16;
                    NlsfCb = NlsfCodebook.Wb;
                }
                PitchLagLowBitsICdf = fsKHz == 16 ? SilkTables.Uniform8ICDF : fsKHz == 12 ? SilkTables.Uniform6ICDF : SilkTables.Uniform4ICDF;
                FirstFrameAfterReset = true;
                LagPrev = 100;
                LastGainIndex = 10;
                PrevSignalType = TypeNoVoiceActivity;
                Array.Clear(OutBuf);
                Array.Clear(SLpcQ14Buf);
            }
            FsKHz = fsKHz;
            FrameLength = frameLength;
        }
    }

    /// <summary>Resets the state the side channel needs when it starts again after a mid-only stretch.</summary>
    public void RestartAfterMidOnly()
    {
        Array.Clear(OutBuf);
        Array.Clear(SLpcQ14Buf);
        LagPrev = 100;
        LastGainIndex = 10;
        PrevSignalType = TypeNoVoiceActivity;
        FirstFrameAfterReset = true;
    }

    // ---- Frame decoding (silk_decode_frame) ----

    /// <summary>Decodes (or conceals) one SILK frame of <see cref="FrameLength"/> samples at the internal rate.</summary>
    public void DecodeFrame(ref RangeDecoder rd, Span<short> pOut, bool lost, int condCoding)
    {
        int l = FrameLength;
        _ltpScaleQ14 = 0;
        if (!lost)
        {
            DecodeIndices(ref rd, NFramesDecoded, false, condCoding);
            DecodePulses(ref rd, _signalType, _quantOffsetType, FrameLength);
            DecodeParameters(condCoding);
            DecodeCore(pOut);
            Plc(pOut, false);
            LossCnt = 0;
            PrevSignalType = _signalType;
            FirstFrameAfterReset = false;
        }
        else
        {
            Plc(pOut, true);
        }

        int mvLen = LtpMemLength - FrameLength;
        Array.Copy(OutBuf, FrameLength, OutBuf, 0, mvLen);
        pOut.Slice(0, FrameLength).CopyTo(OutBuf.AsSpan(mvLen));
        PlcGlueFrames(pOut, l);
        Cng(pOut, l);
        LagPrev = _pitchL[NbSubfr - 1];
    }

    /// <summary>Reads and discards one low-bit-rate-redundancy frame so the range decoder stays aligned.</summary>
    public void SkipLbrrFrame(ref RangeDecoder rd, int frameIndex, int condCoding)
    {
        DecodeIndices(ref rd, frameIndex, true, condCoding);
        DecodePulses(ref rd, _signalType, _quantOffsetType, FrameLength);
    }

    // ---- Side information (silk_decode_indices) ----

    private void DecodeIndices(ref RangeDecoder rd, int frameIndex, bool decodeLbrr, int condCoding)
    {
        int ix;
        if (decodeLbrr || VadFlags[frameIndex] != 0)
            ix = rd.ICdf(SilkTables.TypeOffsetVADICDF, 8) + 2;
        else
            ix = rd.ICdf(SilkTables.TypeOffsetNoVADICDF, 8);
        _signalType = ix >> 1;
        _quantOffsetType = ix & 1;

        if (condCoding == CodeConditionally)
        {
            _gainsIndices[0] = rd.ICdf(SilkTables.DeltaGainICDF, 8);
        }
        else
        {
            _gainsIndices[0] = rd.ICdf(SilkTables.GainICDF.AsSpan(_signalType * 8), 8) << 3;
            _gainsIndices[0] += rd.ICdf(SilkTables.Uniform8ICDF, 8);
        }
        for (int i = 1; i < NbSubfr; i++)
            _gainsIndices[i] = rd.ICdf(SilkTables.DeltaGainICDF, 8);

        var cb = NlsfCb!;
        _nlsfIndices[0] = rd.ICdf(cb.CB1ICdf.AsSpan((_signalType >> 1) * cb.NVectors), 8);
        NlsfUnpack(_ecIx, _predQ8, cb, _nlsfIndices[0]);
        for (int i = 0; i < cb.Order; i++)
        {
            ix = rd.ICdf(cb.EcICdf.AsSpan(_ecIx[i]), 8);
            if (ix == 0) ix -= rd.ICdf(SilkTables.NLSFEXTICDF, 8);
            else if (ix == 8) ix += rd.ICdf(SilkTables.NLSFEXTICDF, 8);
            _nlsfIndices[i + 1] = ix - 4;
        }

        _nlsfInterpCoefQ2 = NbSubfr == MaxNbSubfr ? rd.ICdf(SilkTables.NLSFInterpolationFactorICDF, 8) : 4;

        if (_signalType == TypeVoiced)
        {
            bool decodeAbsolute = true;
            if (condCoding == CodeConditionally && EcPrevSignalType == TypeVoiced)
            {
                int delta = rd.ICdf(SilkTables.PitchDeltaICDF, 8);
                if (delta > 0)
                {
                    delta -= 9;
                    _lagIndex = (short)(EcPrevLagIndex + delta);
                    decodeAbsolute = false;
                }
            }
            if (decodeAbsolute)
            {
                _lagIndex = (short)(rd.ICdf(SilkTables.PitchLagICDF, 8) * (FsKHz >> 1));
                _lagIndex += (short)rd.ICdf(PitchLagLowBitsICdf, 8);
            }
            EcPrevLagIndex = _lagIndex;
            _contourIndex = rd.ICdf(PitchContourICdf, 8);
            _perIndex = rd.ICdf(SilkTables.LTPPerIndexICDF, 8);
            byte[] ltpIcdf = _perIndex == 0 ? SilkTables.LTPGainICDF0 : _perIndex == 1 ? SilkTables.LTPGainICDF1 : SilkTables.LTPGainICDF2;
            for (int k = 0; k < NbSubfr; k++)
                _ltpIndex[k] = rd.ICdf(ltpIcdf, 8);
            _ltpScaleIndex = condCoding == CodeIndependently ? rd.ICdf(SilkTables.LTPscaleICDF, 8) : 0;
        }
        EcPrevSignalType = _signalType;
        _seed = rd.ICdf(SilkTables.Uniform4ICDF, 8);
    }

    private static void NlsfUnpack(Span<short> ecIx, Span<byte> predQ8, NlsfCodebook cb, int cb1Index)
    {
        int selOff = cb1Index * cb.Order / 2;
        for (int i = 0; i < cb.Order; i += 2)
        {
            int entry = cb.EcSel[selOff++];
            ecIx[i] = (short)(((entry >> 1) & 7) * 9);
            predQ8[i] = cb.PredQ8[i + (entry & 1) * (cb.Order - 1)];
            ecIx[i + 1] = (short)(((entry >> 5) & 7) * 9);
            predQ8[i + 1] = cb.PredQ8[i + ((entry >> 4) & 1) * (cb.Order - 1) + 1];
        }
    }

    // ---- Excitation pulses (silk_decode_pulses, shell coder, sign decoding) ----

    private void DecodePulses(ref RangeDecoder rd, int signalType, int quantOffsetType, int frameLength)
    {
        int rateLevel = rd.ICdf(SilkTables.RateLevelsICDF.AsSpan((signalType >> 1) * 9), 8);
        int iter = frameLength >> 4;
        if (iter * 16 < frameLength) iter++;
        var cdf = SilkTables.PulsesPerBlockICDF.AsSpan(rateLevel * 18);
        var pulses = _pulses;
        for (int i = 0; i < iter; i++)
        {
            _nLshifts[i] = 0;
            _sumPulses[i] = rd.ICdf(cdf, 8);
            while (_sumPulses[i] == 17)
            {
                _nLshifts[i]++;
                _sumPulses[i] = rd.ICdf(SilkTables.PulsesPerBlockICDF.AsSpan(9 * 18 + (_nLshifts[i] == 10 ? 1 : 0)), 8);
            }
        }
        for (int i = 0; i < iter; i++)
        {
            if (_sumPulses[i] > 0) ShellDecode(pulses.AsSpan(i * 16, 16), ref rd, _sumPulses[i]);
            else pulses.AsSpan(i * 16, 16).Clear();
        }
        for (int i = 0; i < iter; i++)
        {
            int nls = _nLshifts[i];
            if (nls > 0)
            {
                for (int k = 0; k < 16; k++)
                {
                    int absQ = pulses[i * 16 + k];
                    for (int j = 0; j < nls; j++)
                    {
                        absQ <<= 1;
                        absQ += rd.ICdf(SilkTables.LsbICDF, 8);
                    }
                    pulses[i * 16 + k] = absQ;
                }
                _sumPulses[i] |= nls << 5;
            }
        }
        DecodeSigns(ref rd, pulses, frameLength, signalType, quantOffsetType);
    }

    private static void DecodeSplit(ref RangeDecoder rd, ref int child1, ref int child2, int p, byte[] table)
    {
        if (p > 0)
        {
            child1 = rd.ICdf(table.AsSpan(SilkTables.ShellCodeTableOffsets[p]), 8);
            child2 = p - child1;
        }
        else
        {
            child1 = 0;
            child2 = 0;
        }
    }

    private void ShellDecode(Span<int> p0, ref RangeDecoder rd, int p4)
    {
        // RFC 6716 section 4.2.7.8.1: binary-split pulse-count tree over 16 samples.
        Span<int> p3 = stackalloc int[2], p2 = stackalloc int[4], p1 = stackalloc int[8];
        DecodeSplit(ref rd, ref p3[0], ref p3[1], p4, SilkTables.ShellCodeTable3);
        DecodeSplit(ref rd, ref p2[0], ref p2[1], p3[0], SilkTables.ShellCodeTable2);
        DecodeSplit(ref rd, ref p1[0], ref p1[1], p2[0], SilkTables.ShellCodeTable1);
        DecodeSplit(ref rd, ref p0[0], ref p0[1], p1[0], SilkTables.ShellCodeTable0);
        DecodeSplit(ref rd, ref p0[2], ref p0[3], p1[1], SilkTables.ShellCodeTable0);
        DecodeSplit(ref rd, ref p1[2], ref p1[3], p2[1], SilkTables.ShellCodeTable1);
        DecodeSplit(ref rd, ref p0[4], ref p0[5], p1[2], SilkTables.ShellCodeTable0);
        DecodeSplit(ref rd, ref p0[6], ref p0[7], p1[3], SilkTables.ShellCodeTable0);
        DecodeSplit(ref rd, ref p2[2], ref p2[3], p3[1], SilkTables.ShellCodeTable2);
        DecodeSplit(ref rd, ref p1[4], ref p1[5], p2[2], SilkTables.ShellCodeTable1);
        DecodeSplit(ref rd, ref p0[8], ref p0[9], p1[4], SilkTables.ShellCodeTable0);
        DecodeSplit(ref rd, ref p0[10], ref p0[11], p1[5], SilkTables.ShellCodeTable0);
        DecodeSplit(ref rd, ref p1[6], ref p1[7], p2[3], SilkTables.ShellCodeTable1);
        DecodeSplit(ref rd, ref p0[12], ref p0[13], p1[6], SilkTables.ShellCodeTable0);
        DecodeSplit(ref rd, ref p0[14], ref p0[15], p1[7], SilkTables.ShellCodeTable0);
    }

    private void DecodeSigns(ref RangeDecoder rd, int[] pulses, int length, int signalType, int quantOffsetType)
    {
        Span<byte> icdf = stackalloc byte[2];
        int signBase = 7 * (quantOffsetType + (signalType << 1));
        int blocks = (length + 8) >> 4;
        for (int i = 0; i < blocks; i++)
        {
            int p = _sumPulses[i];
            if (p > 0)
            {
                icdf[0] = SilkTables.SignICDF[signBase + Math.Min(p & 0x1F, 6)];
                icdf[1] = 0;
                for (int j = 0; j < 16; j++)
                {
                    int q = pulses[i * 16 + j];
                    if (q > 0)
                        pulses[i * 16 + j] = q * ((rd.ICdf(icdf, 8) << 1) - 1);
                }
            }
        }
    }

    // ---- Parameters (silk_decode_parameters, gains, NLSF) ----

    private void DecodeParameters(int condCoding)
    {
        GainsDequant(condCoding == CodeConditionally);
        var cb = NlsfCb!;
        NlsfDecode(_nlsf, cb);
        var pred1 = _predCoefQ12.AsSpan(MaxLpcOrder, MaxLpcOrder);
        var pred0 = _predCoefQ12.AsSpan(0, MaxLpcOrder);
        Nlsf2A(pred1, _nlsf, LpcOrder);

        if (FirstFrameAfterReset) _nlsfInterpCoefQ2 = 4;

        if (_nlsfInterpCoefQ2 < 4)
        {
            for (int i = 0; i < LpcOrder; i++)
                _nlsf0[i] = (short)(PrevNlsfQ15[i] + ((_nlsfInterpCoefQ2 * (_nlsf[i] - PrevNlsfQ15[i])) >> 2));
            Nlsf2A(pred0, _nlsf0, LpcOrder);
        }
        else
        {
            pred1.Slice(0, LpcOrder).CopyTo(pred0);
        }
        _nlsf.AsSpan(0, LpcOrder).CopyTo(PrevNlsfQ15);

        if (LossCnt != 0)
        {
            BwExpander(pred0, LpcOrder, 63570);
            BwExpander(pred1, LpcOrder, 63570);
        }

        if (_signalType == TypeVoiced)
        {
            DecodePitch();
            sbyte[] cbk = _perIndex == 0 ? SilkTables.LTPGainVq0 : _perIndex == 1 ? SilkTables.LTPGainVq1 : SilkTables.LTPGainVq2;
            for (int k = 0; k < NbSubfr; k++)
            {
                int ix = _ltpIndex[k];
                for (int i = 0; i < LtpOrder; i++)
                    _ltpCoefQ14[k * LtpOrder + i] = (short)(cbk[ix * LtpOrder + i] << 7);
            }
            _ltpScaleQ14 = SilkTables.LTPScalesTableQ14[_ltpScaleIndex];
        }
        else
        {
            Array.Clear(_pitchL, 0, NbSubfr);
            Array.Clear(_ltpCoefQ14, 0, LtpOrder * NbSubfr);
            _perIndex = 0;
            _ltpScaleQ14 = 0;
        }
    }

    private void GainsDequant(bool conditional)
    {
        const int invScaleQ16 = 1907825; // 65536 * ((88 - 2) * 128 / 6) / 63
        const int offset = 2090;         // 2 * 128 / 6 + 16 * 128
        int prev = LastGainIndex;
        for (int k = 0; k < NbSubfr; k++)
        {
            if (k == 0 && !conditional)
            {
                prev = Math.Max(_gainsIndices[k], prev - 16);
            }
            else
            {
                int indTmp = _gainsIndices[k] - 4;
                int threshold = 2 * 36 - 64 + prev;
                if (indTmp > threshold) prev += (indTmp << 1) - threshold;
                else prev += indTmp;
            }
            prev = Limit(prev, 0, 63);
            _gainsQ16[k] = Log2Lin(Math.Min(SMulWB(invScaleQ16, prev) + offset, 3967));
        }
        LastGainIndex = prev;
    }

    private void NlsfDecode(Span<short> pNlsfQ15, NlsfCodebook cb)
    {
        int order = cb.Order;
        int cb1 = _nlsfIndices[0];
        for (int i = 0; i < order; i++)
            pNlsfQ15[i] = (short)(cb.CB1NlsfQ8[cb1 * order + i] << 7);
        NlsfUnpack(_ecIx, _predQ8, cb, cb1);

        // Residual dequantisation (RFC 6716 section 4.2.7.5.3), backward prediction across coefficients.
        int outQ10 = 0;
        for (int i = order - 1; i >= 0; i--)
        {
            int predQ10 = SMulBB(outQ10, (short)_predQ8[i]) >> 8;
            outQ10 = _nlsfIndices[i + 1] << 10;
            if (outQ10 > 0) outQ10 -= 102;       // 0.1 in Q10
            else if (outQ10 < 0) outQ10 += 102;
            outQ10 = SMlaWB(predQ10, outQ10, cb.QuantStepSizeQ16);
            _resQ10[i] = (short)outQ10;
        }

        NlsfVqWeightsLaroia(_wTmp, pNlsfQ15, order);
        for (int i = 0; i < order; i++)
        {
            int wTmpQ9 = SqrtApprox(_wTmp[i] << (18 - 2));
            int tmp = pNlsfQ15[i] + ((_resQ10[i] << 14) / wTmpQ9);
            pNlsfQ15[i] = (short)Limit(tmp, 0, 32767);
        }
        NlsfStabilize(pNlsfQ15, cb.DeltaMinQ15, order);
    }

    private static void NlsfVqWeightsLaroia(Span<short> w, ReadOnlySpan<short> nlsf, int d)
    {
        const int num = 1 << (15 + 2);
        int tmp1 = Math.Max((int)nlsf[0], 1);
        tmp1 = num / tmp1;
        int tmp2 = Math.Max(nlsf[1] - nlsf[0], 1);
        tmp2 = num / tmp2;
        w[0] = (short)Math.Min(tmp1 + tmp2, short.MaxValue);
        for (int k = 1; k < d - 1; k += 2)
        {
            tmp1 = Math.Max(nlsf[k + 1] - nlsf[k], 1);
            tmp1 = num / tmp1;
            w[k] = (short)Math.Min(tmp1 + tmp2, short.MaxValue);
            tmp2 = Math.Max(nlsf[k + 2] - nlsf[k + 1], 1);
            tmp2 = num / tmp2;
            w[k + 1] = (short)Math.Min(tmp1 + tmp2, short.MaxValue);
        }
        tmp1 = Math.Max((1 << 15) - nlsf[d - 1], 1);
        tmp1 = num / tmp1;
        w[d - 1] = (short)Math.Min(tmp1 + tmp2, short.MaxValue);
    }

    private static void NlsfStabilize(Span<short> nlsf, ReadOnlySpan<short> deltaMin, int l)
    {
        const int maxLoops = 20;
        int loops;
        for (loops = 0; loops < maxLoops; loops++)
        {
            int minDiff = nlsf[0] - deltaMin[0];
            int idx = 0;
            for (int i = 1; i <= l - 1; i++)
            {
                int diff = nlsf[i] - (nlsf[i - 1] + deltaMin[i]);
                if (diff < minDiff) { minDiff = diff; idx = i; }
            }
            int d = (1 << 15) - (nlsf[l - 1] + deltaMin[l]);
            if (d < minDiff) { minDiff = d; idx = l; }

            if (minDiff >= 0) return;

            if (idx == 0)
            {
                nlsf[0] = deltaMin[0];
            }
            else if (idx == l)
            {
                nlsf[l - 1] = (short)((1 << 15) - deltaMin[l]);
            }
            else
            {
                int minCenter = 0;
                for (int k = 0; k < idx; k++) minCenter += deltaMin[k];
                minCenter += deltaMin[idx] >> 1;
                int maxCenter = 1 << 15;
                for (int k = l; k > idx; k--) maxCenter -= deltaMin[k];
                maxCenter -= deltaMin[idx] >> 1;
                short center = (short)Limit(RShiftRound(nlsf[idx - 1] + nlsf[idx], 1), minCenter, maxCenter);
                nlsf[idx - 1] = (short)(center - (deltaMin[idx] >> 1));
                nlsf[idx] = (short)(nlsf[idx - 1] + deltaMin[idx]);
            }
        }

        // Fallback after too many iterations: sort and clamp (RFC 6716 section 4.2.7.5.4).
        for (int i = 1; i < l; i++)
        {
            int value = nlsf[i];
            int j;
            for (j = i - 1; j >= 0 && value < nlsf[j]; j--) nlsf[j + 1] = nlsf[j];
            nlsf[j + 1] = (short)value;
        }
        nlsf[0] = (short)Math.Max((int)nlsf[0], (int)deltaMin[0]);
        for (int i = 1; i < l; i++)
            nlsf[i] = (short)Math.Max((int)nlsf[i], Sat16(nlsf[i - 1] + deltaMin[i]));
        nlsf[l - 1] = (short)Math.Min((int)nlsf[l - 1], (1 << 15) - deltaMin[l]);
        for (int i = l - 2; i >= 0; i--)
            nlsf[i] = (short)Math.Min((int)nlsf[i], nlsf[i + 1] - deltaMin[i + 1]);
    }

    private static readonly byte[] Ordering16 = [0, 15, 8, 7, 4, 11, 12, 3, 2, 13, 10, 5, 6, 9, 14, 1];
    private static readonly byte[] Ordering10 = [0, 9, 6, 3, 4, 5, 8, 1, 2, 7];

    private void Nlsf2A(Span<short> aQ12, ReadOnlySpan<short> nlsf, int d)
    {
        const int QA = 16;
        var ordering = d == 16 ? Ordering16 : Ordering10;
        var cosLsf = _cosLsf;
        for (int k = 0; k < d; k++)
        {
            int fInt = nlsf[k] >> (15 - 7);
            int fFrac = nlsf[k] - (fInt << (15 - 7));
            int cosVal = SilkTables.LSFCosTabFIXQ12[fInt];
            int delta = SilkTables.LSFCosTabFIXQ12[fInt + 1] - cosVal;
            cosLsf[ordering[k]] = RShiftRound((cosVal << 8) + delta * fFrac, 20 - QA);
        }
        int dd = d >> 1;
        FindPoly(_polyP, cosLsf, 0, dd);
        FindPoly(_polyQ, cosLsf, 1, dd);
        var a32 = _a32;
        for (int k = 0; k < dd; k++)
        {
            int ptmp = _polyP[k + 1] + _polyP[k];
            int qtmp = _polyQ[k + 1] - _polyQ[k];
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
                int sc = 65470 - ((maxabs - short.MaxValue) << 14) / ((maxabs * (idx + 1)) >> 2); // 0.999 in Q16
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
            for (int k = 0; k < d; k++)
                aQ12[k] = (short)RShiftRound(a32[k], QA + 1 - 12);
        }

        for (i = 0; i < 16; i++)
        {
            if (LpcInversePredGain(aQ12, d, _scratch32) < 107374) // 1 / 1e4 in Q30
            {
                BwExpander32(a32, d, 65536 - (2 << i));
                for (int k = 0; k < d; k++)
                    aQ12[k] = (short)RShiftRound(a32[k], QA + 1 - 12);
            }
            else break;
        }
    }

    private static void FindPoly(int[] output, int[] cLsf, int cOff, int dd)
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

    private void DecodePitch()
    {
        // RFC 6716 section 4.2.7.6.1: lag from the base index plus a per-subframe contour offset.
        sbyte[] lagCb;
        int cbkSize;
        if (FsKHz == 8)
        {
            if (NbSubfr == MaxNbSubfr) { lagCb = SilkTables.CBLagsStage2; cbkSize = 11; }
            else { lagCb = SilkTables.CBLagsStage210Ms; cbkSize = 3; }
        }
        else
        {
            if (NbSubfr == MaxNbSubfr) { lagCb = SilkTables.CBLagsStage3; cbkSize = 34; }
            else { lagCb = SilkTables.CBLagsStage310Ms; cbkSize = 12; }
        }
        int minLag = 2 * FsKHz;
        int maxLag = 18 * FsKHz;
        int lag = minLag + _lagIndex;
        for (int k = 0; k < NbSubfr; k++)
            _pitchL[k] = Limit(lag + lagCb[k * cbkSize + _contourIndex], minLag, maxLag);
    }

    // ---- Synthesis (silk_decode_core) ----

    private void DecodeCore(Span<short> xq)
    {
        var pulses = _pulses;
        int offsetQ10 = SilkTables.QuantizationOffsetsQ10[(_signalType >> 1) * 2 + _quantOffsetType];
        bool nlsfInterpolation = _nlsfInterpCoefQ2 < 4;

        int randSeed = _seed;
        for (int i = 0; i < FrameLength; i++)
        {
            randSeed = Rand(randSeed);
            int e = pulses[i] << 14;
            if (e > 0) e -= 80 << 4;
            else if (e < 0) e += 80 << 4;
            e += offsetQ10 << 4;
            if (randSeed < 0) e = -e;
            ExcQ14[i] = e;
            randSeed = unchecked(randSeed + pulses[i]);
        }

        var sLpc = _sLpcQ14;
        Array.Copy(SLpcQ14Buf, sLpc, MaxLpcOrder);
        int excOff = 0, xqOff = 0;
        int sLtpBufIdx = LtpMemLength;
        int lag = 0;
        var sLtp = _sLtp;
        var sLtpQ15 = _sLtpQ15;

        for (int k = 0; k < NbSubfr; k++)
        {
            var aQ12 = _predCoefQ12.AsSpan((k >> 1) * MaxLpcOrder, MaxLpcOrder);
            aQ12.Slice(0, LpcOrder).CopyTo(_aQ12Tmp);
            var b = _ltpCoefQ14.AsSpan(k * LtpOrder, LtpOrder);
            int signalType = _signalType;
            int gainQ10 = _gainsQ16[k] >> 6;
            int invGainQ31 = Inverse32VarQ(_gainsQ16[k], 47);

            int gainAdjQ16;
            if (_gainsQ16[k] != PrevGainQ16)
            {
                gainAdjQ16 = Div32VarQ(PrevGainQ16, _gainsQ16[k], 16);
                for (int i = 0; i < MaxLpcOrder; i++)
                    sLpc[i] = SMulWW(gainAdjQ16, sLpc[i]);
            }
            else
            {
                gainAdjQ16 = 1 << 16;
            }
            PrevGainQ16 = _gainsQ16[k];

            // After a loss, force a voiced-like prediction so the start of an unvoiced frame stays smooth.
            if (LossCnt != 0 && PrevSignalType == TypeVoiced && _signalType != TypeVoiced && k < MaxNbSubfr / 2)
            {
                b.Clear();
                b[LtpOrder / 2] = 4096; // 0.25 in Q14
                signalType = TypeVoiced;
                _pitchL[k] = LagPrev;
            }

            if (signalType == TypeVoiced)
            {
                lag = _pitchL[k];
                if (k == 0 || (k == 2 && nlsfInterpolation))
                {
                    int startIdx = LtpMemLength - lag - LpcOrder - LtpOrder / 2;
                    if (k == 2)
                        xq.Slice(0, 2 * SubfrLength).CopyTo(OutBuf.AsSpan(LtpMemLength));
                    LpcAnalysisFilter(sLtp.AsSpan(startIdx), OutBuf.AsSpan(startIdx + k * SubfrLength), aQ12,
                        LtpMemLength - startIdx, LpcOrder);
                    if (k == 0)
                        invGainQ31 = SMulWB(invGainQ31, _ltpScaleQ14) << 2;
                    for (int i = 0; i < lag + LtpOrder / 2; i++)
                        sLtpQ15[sLtpBufIdx - i - 1] = SMulWB(invGainQ31, sLtp[LtpMemLength - i - 1]);
                }
                else if (gainAdjQ16 != 1 << 16)
                {
                    for (int i = 0; i < lag + LtpOrder / 2; i++)
                        sLtpQ15[sLtpBufIdx - i - 1] = SMulWW(gainAdjQ16, sLtpQ15[sLtpBufIdx - i - 1]);
                }
            }

            Span<int> pres;
            if (signalType == TypeVoiced)
            {
                // Long-term prediction (RFC 6716 section 4.2.7.9.1).
                int p = sLtpBufIdx - lag + LtpOrder / 2;
                pres = _resQ14;
                for (int i = 0; i < SubfrLength; i++)
                {
                    int ltpPredQ13 = 2;
                    ltpPredQ13 = SMlaWB(ltpPredQ13, sLtpQ15[p + i], b[0]);
                    ltpPredQ13 = SMlaWB(ltpPredQ13, sLtpQ15[p + i - 1], b[1]);
                    ltpPredQ13 = SMlaWB(ltpPredQ13, sLtpQ15[p + i - 2], b[2]);
                    ltpPredQ13 = SMlaWB(ltpPredQ13, sLtpQ15[p + i - 3], b[3]);
                    ltpPredQ13 = SMlaWB(ltpPredQ13, sLtpQ15[p + i - 4], b[4]);
                    pres[i] = ExcQ14[excOff + i] + (ltpPredQ13 << 1);
                    sLtpQ15[sLtpBufIdx] = pres[i] << 1;
                    sLtpBufIdx++;
                }
            }
            else
            {
                pres = ExcQ14.AsSpan(excOff, SubfrLength);
            }

            // Short-term (LPC) synthesis (RFC 6716 section 4.2.7.9.2).
            var a = _aQ12Tmp;
            int order = LpcOrder;
            for (int i = 0; i < SubfrLength; i++)
            {
                int pred = order >> 1;
                int basis = MaxLpcOrder + i;
                pred = SMlaWB(pred, sLpc[basis - 1], a[0]);
                pred = SMlaWB(pred, sLpc[basis - 2], a[1]);
                pred = SMlaWB(pred, sLpc[basis - 3], a[2]);
                pred = SMlaWB(pred, sLpc[basis - 4], a[3]);
                pred = SMlaWB(pred, sLpc[basis - 5], a[4]);
                pred = SMlaWB(pred, sLpc[basis - 6], a[5]);
                pred = SMlaWB(pred, sLpc[basis - 7], a[6]);
                pred = SMlaWB(pred, sLpc[basis - 8], a[7]);
                pred = SMlaWB(pred, sLpc[basis - 9], a[8]);
                pred = SMlaWB(pred, sLpc[basis - 10], a[9]);
                if (order == 16)
                {
                    pred = SMlaWB(pred, sLpc[basis - 11], a[10]);
                    pred = SMlaWB(pred, sLpc[basis - 12], a[11]);
                    pred = SMlaWB(pred, sLpc[basis - 13], a[12]);
                    pred = SMlaWB(pred, sLpc[basis - 14], a[13]);
                    pred = SMlaWB(pred, sLpc[basis - 15], a[14]);
                    pred = SMlaWB(pred, sLpc[basis - 16], a[15]);
                }
                sLpc[basis] = pres[i] + (pred << 4);
                xq[xqOff + i] = (short)Sat16(RShiftRound(SMulWW(sLpc[basis], gainQ10), 8));
            }

            Array.Copy(sLpc, SubfrLength, sLpc, 0, MaxLpcOrder);
            excOff += SubfrLength;
            xqOff += SubfrLength;
        }
        Array.Copy(sLpc, SLpcQ14Buf, MaxLpcOrder);
    }

    /// <summary>LPC analysis (whitening) filter (silk_LPC_analysis_filter). Wrap-around is deliberate and matches the reference.</summary>
    private static void LpcAnalysisFilter(Span<short> output, ReadOnlySpan<short> input, ReadOnlySpan<short> b, int len, int d)
    {
        for (int ix = d; ix < len; ix++)
        {
            int p = ix - 1;
            int acc = input[p] * b[0];
            acc = unchecked(acc + input[p - 1] * b[1]);
            acc = unchecked(acc + input[p - 2] * b[2]);
            acc = unchecked(acc + input[p - 3] * b[3]);
            acc = unchecked(acc + input[p - 4] * b[4]);
            acc = unchecked(acc + input[p - 5] * b[5]);
            for (int j = 6; j < d; j += 2)
            {
                acc = unchecked(acc + input[p - j] * b[j]);
                acc = unchecked(acc + input[p - j - 1] * b[j + 1]);
            }
            int o = unchecked((input[p + 1] << 12) - acc);
            output[ix] = (short)Sat16(RShiftRound(o, 12));
        }
        output.Slice(0, d).Clear();
    }

    // ---- Packet loss concealment (silk_PLC) ----

    private void Plc(Span<short> frame, bool lost)
    {
        if (FsKHz != _plcFsKHz)
        {
            PlcReset();
            _plcFsKHz = FsKHz;
        }
        if (lost)
        {
            PlcConceal(frame);
            LossCnt++;
        }
        else
        {
            PlcUpdate();
        }
    }

    private void PlcUpdate()
    {
        PrevSignalType = _signalType;
        int ltpGainQ14 = 0;
        if (_signalType == TypeVoiced)
        {
            for (int j = 0; j * SubfrLength < _pitchL[NbSubfr - 1]; j++)
            {
                if (j == NbSubfr) break;
                int temp = 0;
                for (int i = 0; i < LtpOrder; i++)
                    temp += _ltpCoefQ14[(NbSubfr - 1 - j) * LtpOrder + i];
                if (temp > ltpGainQ14)
                {
                    ltpGainQ14 = temp;
                    _ltpCoefQ14.AsSpan((NbSubfr - 1 - j) * LtpOrder, LtpOrder).CopyTo(_plcLtpCoefQ14);
                    _plcPitchLQ8 = _pitchL[NbSubfr - 1 - j] << 8;
                }
            }
            _plcLtpCoefQ14.AsSpan().Clear();
            _plcLtpCoefQ14[LtpOrder / 2] = (short)ltpGainQ14;
            if (ltpGainQ14 < 11469)
            {
                int scaleQ10 = (11469 << 10) / Math.Max(ltpGainQ14, 1);
                for (int i = 0; i < LtpOrder; i++)
                    _plcLtpCoefQ14[i] = (short)(SMulBB(_plcLtpCoefQ14[i], scaleQ10) >> 10);
            }
            else if (ltpGainQ14 > 15565)
            {
                int scaleQ14 = (15565 << 14) / Math.Max(ltpGainQ14, 1);
                for (int i = 0; i < LtpOrder; i++)
                    _plcLtpCoefQ14[i] = (short)(SMulBB(_plcLtpCoefQ14[i], scaleQ14) >> 14);
            }
        }
        else
        {
            _plcPitchLQ8 = SMulBB(FsKHz, 18) << 8;
            _plcLtpCoefQ14.AsSpan().Clear();
        }
        _predCoefQ12.AsSpan(MaxLpcOrder, LpcOrder).CopyTo(_plcPrevLpcQ12);
        _plcPrevLtpScaleQ14 = _ltpScaleQ14;
        _plcPrevGainQ16[0] = _gainsQ16[NbSubfr - 2];
        _plcPrevGainQ16[1] = _gainsQ16[NbSubfr - 1];
        _plcSubfrLength = SubfrLength;
        _plcNbSubfr = NbSubfr;
    }

    private static readonly short[] HarmAttQ15 = [32440, 31130];
    private static readonly short[] PlcRandAttenuateVQ15 = [31130, 26214];
    private static readonly short[] PlcRandAttenuateUvQ15 = [32440, 29491];

    private void PlcConceal(Span<short> frame)
    {
        const int randBufSize = 128, randBufMask = randBufSize - 1;
        Span<short> aQ12 = stackalloc short[MaxLpcOrder];
        var sLtpQ14 = new Span<int>(_sLtpQ15Plc, 0, 2 * MaxFrameLength);
        var sLtp = _plcSLtp;
        Span<int> prevGainQ10 = stackalloc int[2];
        prevGainQ10[0] = _plcPrevGainQ16[0] >> 6;
        prevGainQ10[1] = _plcPrevGainQ16[1] >> 6;

        if (FirstFrameAfterReset) _plcPrevLpcQ12.AsSpan().Clear();

        // Pick the louder of the last two subframes' excitation as the noise source.
        var excBuf = _excBuf;
        int eo = 0;
        for (int k = 0; k < 2; k++)
        {
            for (int i = 0; i < _plcSubfrLength; i++)
                excBuf[eo + i] = (short)Sat16(SMulWW(ExcQ14[i + (k + _plcNbSubfr - 2) * _plcSubfrLength], prevGainQ10[k]) >> 8);
            eo += _plcSubfrLength;
        }
        SumSqrShift(out int energy1, out int shift1, excBuf, _plcSubfrLength);
        SumSqrShift(out int energy2, out int shift2, excBuf.AsSpan(_plcSubfrLength), _plcSubfrLength);
        int randOff;
        if ((energy1 >> shift2) < (energy2 >> shift1))
            randOff = Math.Max(0, (_plcNbSubfr - 1) * _plcSubfrLength - randBufSize);
        else
            randOff = Math.Max(0, _plcNbSubfr * _plcSubfrLength - randBufSize);

        var b = _plcLtpCoefQ14;
        short randScaleQ14 = _plcRandScaleQ14;
        int harmGainQ15 = HarmAttQ15[Math.Min(1, LossCnt)];
        int randGainQ15 = PrevSignalType == TypeVoiced
            ? PlcRandAttenuateVQ15[Math.Min(1, LossCnt)]
            : PlcRandAttenuateUvQ15[Math.Min(1, LossCnt)];

        BwExpander(_plcPrevLpcQ12, LpcOrder, 64881); // 0.99 in Q16
        _plcPrevLpcQ12.AsSpan(0, LpcOrder).CopyTo(aQ12);

        if (LossCnt == 0)
        {
            randScaleQ14 = 1 << 14;
            if (PrevSignalType == TypeVoiced)
            {
                for (int i = 0; i < LtpOrder; i++) randScaleQ14 -= b[i];
                randScaleQ14 = (short)Math.Max(3277, (int)randScaleQ14);
                randScaleQ14 = (short)(SMulBB(randScaleQ14, _plcPrevLtpScaleQ14) >> 14);
            }
            else
            {
                int invGainQ30 = LpcInversePredGain(_plcPrevLpcQ12, LpcOrder, _scratch32);
                int downScaleQ30 = Math.Min((1 << 30) >> 3, invGainQ30);
                downScaleQ30 = Math.Max((1 << 30) >> 8, downScaleQ30);
                downScaleQ30 <<= 3;
                randGainQ15 = SMulWB(downScaleQ30, randGainQ15) >> 14;
            }
        }

        int randSeed = _plcRandSeed;
        int lag = RShiftRound(_plcPitchLQ8, 8);
        int sLtpBufIdx = LtpMemLength;

        int idx = LtpMemLength - lag - LpcOrder - LtpOrder / 2;
        LpcAnalysisFilter(sLtp.AsSpan(idx), OutBuf.AsSpan(idx), aQ12, LtpMemLength - idx, LpcOrder);

        int invGain = Math.Min(Inverse32VarQ(_plcPrevGainQ16[1], 46), int.MaxValue >> 1);
        for (int i = idx + LpcOrder; i < LtpMemLength; i++)
            sLtpQ14[i] = SMulWB(invGain, sLtp[i]);

        for (int k = 0; k < NbSubfr; k++)
        {
            int p = sLtpBufIdx - lag + LtpOrder / 2;
            for (int i = 0; i < SubfrLength; i++)
            {
                int pred = 2;
                pred = SMlaWB(pred, sLtpQ14[p + i], b[0]);
                pred = SMlaWB(pred, sLtpQ14[p + i - 1], b[1]);
                pred = SMlaWB(pred, sLtpQ14[p + i - 2], b[2]);
                pred = SMlaWB(pred, sLtpQ14[p + i - 3], b[3]);
                pred = SMlaWB(pred, sLtpQ14[p + i - 4], b[4]);
                randSeed = Rand(randSeed);
                idx = (randSeed >> 25) & randBufMask;
                sLtpQ14[sLtpBufIdx] = SMlaWB(pred, ExcQ14[randOff + idx], randScaleQ14) << 2;
                sLtpBufIdx++;
            }
            for (int j = 0; j < LtpOrder; j++)
                b[j] = (short)(SMulBB(harmGainQ15, b[j]) >> 15);
            randScaleQ14 = (short)(SMulBB(randScaleQ14, randGainQ15) >> 15);
            _plcPitchLQ8 = SMlaWB(_plcPitchLQ8, _plcPitchLQ8, 655);
            _plcPitchLQ8 = Math.Min(_plcPitchLQ8, SMulBB(18, FsKHz) << 8);
            lag = RShiftRound(_plcPitchLQ8, 8);
        }

        int baseIdx = LtpMemLength - MaxLpcOrder;
        for (int i = 0; i < MaxLpcOrder; i++) sLtpQ14[baseIdx + i] = SLpcQ14Buf[i];
        for (int i = 0; i < FrameLength; i++)
        {
            int pred = LpcOrder >> 1;
            int basis = baseIdx + MaxLpcOrder + i;
            for (int j = 0; j < LpcOrder; j++)
                pred = SMlaWB(pred, sLtpQ14[basis - j - 1], aQ12[j]);
            sLtpQ14[basis] += pred << 4;
            frame[i] = (short)Sat16(Sat16(RShiftRound(SMulWW(sLtpQ14[basis], prevGainQ10[1]), 8)));
        }
        for (int i = 0; i < MaxLpcOrder; i++) SLpcQ14Buf[i] = sLtpQ14[baseIdx + FrameLength + i];

        _plcRandSeed = randSeed;
        _plcRandScaleQ14 = randScaleQ14;
        for (int i = 0; i < MaxNbSubfr; i++) _pitchL[i] = lag;
    }

    private readonly int[] _sLtpQ15Plc = new int[2 * MaxFrameLength];
    private readonly short[] _plcSLtp = new short[MaxFrameLength];

    /// <summary>Smooths the energy step at the first good frame after a loss (silk_PLC_glue_frames).</summary>
    private void PlcGlueFrames(Span<short> frame, int length)
    {
        if (LossCnt != 0)
        {
            SumSqrShift(out _plcConcEnergy, out _plcConcEnergyShift, frame, length);
            _plcLastFrameLost = true;
        }
        else
        {
            if (_plcLastFrameLost)
            {
                SumSqrShift(out int energy, out int energyShift, frame, length);
                if (energyShift > _plcConcEnergyShift)
                    _plcConcEnergy >>= energyShift - _plcConcEnergyShift;
                else if (energyShift < _plcConcEnergyShift)
                    energy >>= _plcConcEnergyShift - energyShift;

                if (energy > _plcConcEnergy)
                {
                    int lz = Clz32(_plcConcEnergy) - 1;
                    _plcConcEnergy <<= lz;
                    energy >>= Math.Max(24 - lz, 0);
                    int fracQ24 = _plcConcEnergy / Math.Max(energy, 1);
                    int gainQ16 = SqrtApprox(fracQ24) << 4;
                    int slopeQ16 = ((1 << 16) - gainQ16) / length;
                    slopeQ16 <<= 2;
                    for (int i = 0; i < length; i++)
                    {
                        frame[i] = (short)SMulWB(gainQ16, frame[i]);
                        gainQ16 += slopeQ16;
                        if (gainQ16 > 1 << 16) break;
                    }
                }
            }
            _plcLastFrameLost = false;
        }
    }

    // ---- Comfort noise (silk_CNG) ----

    private void Cng(Span<short> frame, int length)
    {
        if (FsKHz != _cngFsKHz)
        {
            CngReset();
            _cngFsKHz = FsKHz;
        }
        if (LossCnt == 0 && PrevSignalType == TypeNoVoiceActivity)
        {
            for (int i = 0; i < LpcOrder; i++)
                _cngSmthNlsfQ15[i] += (short)SMulWB(PrevNlsfQ15[i] - _cngSmthNlsfQ15[i], 16348);
            int maxGain = 0, subfr = 0;
            for (int i = 0; i < NbSubfr; i++)
            {
                if (_gainsQ16[i] > maxGain) { maxGain = _gainsQ16[i]; subfr = i; }
            }
            Array.Copy(_cngExcBufQ14, 0, _cngExcBufQ14, SubfrLength, (NbSubfr - 1) * SubfrLength);
            Array.Copy(ExcQ14, subfr * SubfrLength, _cngExcBufQ14, 0, SubfrLength);
            for (int i = 0; i < NbSubfr; i++)
                _cngSmthGainQ16 += SMulWB(_gainsQ16[i] - _cngSmthGainQ16, 4634);
        }

        if (LossCnt != 0)
        {
            var sig = _cngSigQ10;
            int excMask = 255;
            while (excMask > length) excMask >>= 1;
            int seed = _cngRandSeed;
            for (int i = 0; i < length; i++)
            {
                seed = Rand(seed);
                int idx = (seed >> 24) & excMask;
                sig[MaxLpcOrder + i] = (short)Sat16(SMulWW(_cngExcBufQ14[idx], _cngSmthGainQ16 >> 4));
            }
            _cngRandSeed = seed;

            Span<short> aQ12 = stackalloc short[MaxLpcOrder];
            Nlsf2A(aQ12, _cngSmthNlsfQ15, LpcOrder);
            Array.Copy(_cngSynthState, sig, MaxLpcOrder);
            for (int i = 0; i < length; i++)
            {
                int sum = LpcOrder >> 1;
                int basis = MaxLpcOrder + i;
                for (int j = 0; j < LpcOrder; j++)
                    sum = SMlaWB(sum, sig[basis - j - 1], aQ12[j]);
                sig[basis] += sum << 4;
                frame[i] = (short)Sat16(frame[i] + RShiftRound(sum, 6));
            }
            Array.Copy(sig, length, _cngSynthState, 0, MaxLpcOrder);
        }
        else
        {
            Array.Clear(_cngSynthState, 0, LpcOrder);
        }
    }
}
