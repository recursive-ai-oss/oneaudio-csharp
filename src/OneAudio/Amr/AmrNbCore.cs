// Copyright (c) Recursive AI, LLC. MIT OR Apache-2.0.
namespace OneAudio.Amr;

/// <summary>
/// The AMR narrowband speech decoder core (3GPP TS 26.090 with the error concealment of TS 26.091 and the comfort
/// noise of TS 26.092), written in the fixed-point arithmetic of TS 26.073 so its output is bit-exact against the
/// TS 26.074 test sequences. This part holds the state, the frame-level control flow (TS 26.090 section 6) and the
/// post-processing; the LPC, excitation and DTX parts are in the sibling partial files.
/// </summary>
internal sealed partial class AmrNbCore : AmrFixed
{
    // Frame constants (TS 26.073 cnst.h).
    internal const int LFrame = 160;
    private const int LFrameBy2 = 80;
    private const int LSubfr = 40;
    private const int M = 10;
    private const int Mp1 = M + 1;
    private const int PitMin = 20;
    private const int PitMax = 143;
    private const int PitMinMr122 = 18;
    private const int LInterpol = 11;
    private const int SharpMax = 13017;
    private const int SharpMin = 0;
    private const int AzSize = 4 * M + 4;
    private const int LsfGap = 205;
    private const int ExcOff = PitMax + LInterpol;

    // Modes (TS 26.073 mode.h) and received frame types (frame.h).
    internal const int Mr475 = 0, Mr515 = 1, Mr59 = 2, Mr67 = 3, Mr74 = 4, Mr795 = 5, Mr102 = 6, Mr122 = 7, MrDtx = 8;
    internal const int RxSpeechGood = 0, RxSpeechDegraded = 1, RxOnset = 2, RxSpeechBad = 3, RxSidFirst = 4,
        RxSidUpdate = 5, RxSidBad = 6, RxNoData = 7;

    private static readonly short[][] BitnoTable =
    [
        AmrNbTables.Bitno475, AmrNbTables.Bitno515, AmrNbTables.Bitno59, AmrNbTables.Bitno67, AmrNbTables.Bitno74,
        AmrNbTables.Bitno795, AmrNbTables.Bitno102, AmrNbTables.Bitno122, AmrNbTables.BitnoDtx,
    ];

    // ---- Decoder state (Decoder_amrState and its sub-states) ----
    private readonly short[] _oldExc = new short[PitMax + LInterpol + LSubfr];
    private readonly short[] _memSyn = new short[M];
    private short _sharp, _oldT0, _prevBf, _prevPdf, _state, _t0LagBuff, _inBackgroundNoise, _voicedHangover;
    private readonly short[] _excEnergyHist = new short[9];
    private readonly short[] _ltpGainHistory = new short[9];
    private short _nodataSeed;
    private readonly short[] _lspOld = new short[M];

    private readonly short[] _pastRQ = new short[M];
    private readonly short[] _pastLsfQ = new short[M];

    private readonly short[] _pbuf = new short[5];
    private short _pastGainPit, _prevGp;
    private readonly short[] _gbuf = new short[5];
    private short _pastGainCode, _prevGc;

    private readonly short[] _pastQuaEn = new short[4];
    private readonly short[] _pastQuaEnMr122 = new short[4];

    private readonly short[] _cbGainHistory = new short[7];
    private short _hangVar, _hangCount;

    private readonly short[] _lspMeanSave = new short[M];

    private readonly short[] _frameEnergyHist = new short[60];
    private short _bgHangover;

    private readonly short[] _phGainMem = new short[5];
    private short _phPrevState, _phPrevCbGain, _phLockFull, _phOnset;

    // Post filter and high-pass state.
    private readonly short[] _memSynPst = new short[M];
    private readonly short[] _res2 = new short[LSubfr];
    private readonly short[] _synthBuf = new short[M + LFrame];
    private short _memPre;
    private short _agcPastGain;
    private short _y2Hi, _y2Lo, _y1Hi, _y1Lo, _x0, _x1;

    // Scratch (allocated once so decoding never allocates).
    private readonly short[] _synTmp = new short[80];
    private readonly short[] _parm = new short[58];
    private readonly short[] _az = new short[AzSize];
    private readonly short[] _lspNew = new short[M];
    private readonly short[] _lspMid = new short[M];
    private readonly short[] _prevLsf = new short[M];
    private readonly short[] _lsfI = new short[M];
    private readonly short[] _code = new short[LSubfr];
    private readonly short[] _excp = new short[LSubfr];
    private readonly short[] _excEnhanced = new short[LSubfr];
    private readonly short[] _lsfTmp1 = new short[M];
    private readonly short[] _lsfTmp2 = new short[M];
    private readonly short[] _lspTmp = new short[M];
    private readonly int[] _f1 = new int[6];
    private readonly int[] _f2 = new int[6];
    private readonly short[] _gmedTmp = new short[9];
    private readonly short[] _gmedTmp2 = new short[9];
    private readonly short[] _lagOut = new short[2];
    private readonly short[] _ap3 = new short[Mp1];
    private readonly short[] _ap4 = new short[Mp1];
    private readonly short[] _h = new short[22 + 10 + 1 + 10];
    private readonly short[] _posIndx = new short[8];
    private readonly short[] _signIndx = new short[4];
    private readonly short[] _phInnoSav = new short[LSubfr];
    private readonly short[] _phPsPoss = new short[LSubfr];
    private readonly short[] _serial = new short[244];
    private readonly short[] _synthOut = new short[LFrame];

    private short _prevMode;
    private bool _homed = true;

    public AmrNbCore()
    {
        ResetAll();
    }

    /// <summary>Decoder_amr_reset(state, MR475) plus the post filters (Speech_Decode_Frame_reset).</summary>
    public void ResetAll()
    {
        DecoderReset(Mr475);
        PostFilterReset();
        PostProcessReset();
        _prevMode = 0;
        _homed = true;
    }

    private void PostFilterReset()
    {
        Array.Clear(_memSynPst);
        Array.Clear(_res2);
        Array.Clear(_synthBuf);
        _agcPastGain = 4096;
        _memPre = 0;
    }

    private void PostProcessReset()
    {
        _y2Hi = _y2Lo = _y1Hi = _y1Lo = _x0 = _x1 = 0;
    }

    private void DecoderReset(int mode)
    {
        Array.Clear(_oldExc, 0, PitMax + LInterpol);
        if (mode != MrDtx) Array.Clear(_memSyn);
        _sharp = SharpMin;
        _oldT0 = 40;
        if (mode != MrDtx) Array.Copy(AmrNbTables.LspInit, _lspOld, M);
        _prevBf = 0;
        _prevPdf = 0;
        _state = 0;
        _t0LagBuff = 40;
        _inBackgroundNoise = 0;
        _voicedHangover = 0;
        if (mode != MrDtx) Array.Clear(_excEnergyHist);
        Array.Clear(_ltpGainHistory);
        // Cb_gain_average_reset
        Array.Clear(_cbGainHistory);
        _hangVar = 0;
        _hangCount = 0;
        if (mode != MrDtx) Array.Copy(AmrNbTables.MeanLsf5, _lspMeanSave, M);
        // D_plsf_reset
        Array.Clear(_pastRQ);
        Array.Copy(AmrNbTables.MeanLsf5, _pastLsfQ, M);
        // ec_gain_pitch_reset / ec_gain_code_reset
        for (int i = 0; i < 5; i++) _pbuf[i] = 1640;
        _pastGainPit = 0;
        _prevGp = 16384;
        for (int i = 0; i < 5; i++) _gbuf[i] = 1;
        _pastGainCode = 0;
        _prevGc = 1;
        if (mode != MrDtx)
        {
            for (int i = 0; i < 4; i++)
            {
                _pastQuaEn[i] = -14336;
                _pastQuaEnMr122[i] = -2381;
            }
        }
        // Bgn_scd_reset
        Array.Clear(_frameEnergyHist);
        _bgHangover = 0;
        _nodataSeed = 21845;
        // ph_disp_reset
        Array.Clear(_phGainMem);
        _phPrevState = 0;
        _phPrevCbGain = 0;
        _phLockFull = 0;
        _phOnset = 0;
        if (mode != MrDtx) DtxReset();
    }

    // ---------------------------------------------------------------------------------------------------------
    // Frame entry points

    /// <summary>
    /// Decodes one frame the way the reference decoder program does (TS 26.073 decoder.c): handles decoder homing
    /// frames (TS 26.101 / 26.093) around Speech_Decode_Frame. <paramref name="serial"/> holds the 244 speech bits
    /// of the frame in the codec's serial order, one bit per element (0 or 1).
    /// </summary>
    /// <param name="rxType">Received frame type, one of the Rx constants.</param>
    /// <param name="mode">Mode of the frame (for SID frames, the mode indication it carries).</param>
    /// <param name="serial">Speech bits, at least as many as the mode needs.</param>
    /// <param name="pcm">160 output samples, 13-bit PCM in 16-bit words.</param>
    public void Decode(int rxType, int mode, short[] serial, Span<short> pcm)
    {
        if (rxType == RxNoData) mode = _prevMode;
        else _prevMode = (short)mode;

        bool resetFlag = false;
        if (_homed)
        {
            // Only the first subframe is checked while homed (decoder_homing_frame_test_first).
            resetFlag = HomingTest(serial, mode, first: true);
        }
        bool wasHomed = _homed;
        if (resetFlag && wasHomed)
        {
            for (int i = 0; i < LFrame; i++) pcm[i] = 0x0008;
        }
        else
        {
            SpeechDecodeFrame(mode, serial, rxType, _synthOut);
            _synthOut.AsSpan(0, LFrame).CopyTo(pcm);
        }
        if (!wasHomed) resetFlag = HomingTest(serial, mode, first: false);
        if (resetFlag) ResetAll();
        _homed = resetFlag;
    }

    private static readonly short[][] DhfTable =
    [
        AmrNbTables.Dhf475, AmrNbTables.Dhf515, AmrNbTables.Dhf59, AmrNbTables.Dhf67, AmrNbTables.Dhf74,
        AmrNbTables.Dhf795, AmrNbTables.Dhf102, AmrNbTables.Dhf122,
    ];

    private bool HomingTest(short[] serial, int mode, bool first)
    {
        // The homing test applies to the eight speech modes only; the reference program reads past the table for
        // SID frames, where the result is never a match in practice, so they are never homing frames.
        if (mode < 0 || mode > Mr122) return false;
        Bits2Prm(mode, serial, _parm);
        int n = first ? AmrNbTables.Prmnofsf[mode] : AmrNbTables.Prmno[mode];
        short[] dhf = DhfTable[mode];
        for (int i = 0; i < n; i++)
            if ((_parm[i] ^ dhf[i]) != 0) return false;
        return true;
    }

    private void Bits2Prm(int mode, short[] bits, short[] prm)
    {
        short[] bitno = BitnoTable[mode];
        int n = AmrNbTables.Prmno[mode];
        int pos = 0;
        for (int i = 0; i < n; i++)
        {
            short value = 0;
            for (int k = 0; k < bitno[i]; k++)
            {
                value = Shl(value, 1);
                if (bits[pos++] == 1) value = Add(value, 1);
            }
            prm[i] = value;
        }
    }

    /// <summary>Speech_Decode_Frame (TS 26.073 sp_dec.c).</summary>
    private void SpeechDecodeFrame(int mode, short[] serial, int frameType, short[] synth)
    {
        if (frameType == RxSidBad || frameType == RxSidUpdate) Bits2Prm(MrDtx, serial, _parm);
        else Bits2Prm(mode, serial, _parm);

        DecoderAmr(mode, _parm, frameType, synth, _az);
        PostFilter(mode, synth, _az);
        PostProcess(synth, LFrame);
        for (int i = 0; i < LFrame; i++) synth[i] = (short)(synth[i] & unchecked((short)0xfff8));
    }

    // ---------------------------------------------------------------------------------------------------------
    // Decoder_amr (TS 26.073 dec_amr.c; TS 26.090 section 6)

    private void DecoderAmr(int mode, short[] parm, int frameType, short[] synth, short[] aT)
    {
        int pi = 0;
        short t0 = 0, t0Frac = 0, index, indexMr475 = 0;
        short gainPit = 0, gainCode = 0, gainCodeMix, pitSharp, pitFlag, pitchFac;
        short bfi = 0, pdfi = 0;
        int newDtxState = RxDtxHandler(frameType);

        if (newDtxState != Speech)
        {
            DecoderReset(MrDtx);
            DtxDec(newDtxState, mode, parm, synth, aT);
            LsfToLsp(_pastLsfQ, _lspOld, M);
            LspAvg(_pastLsfQ);
            _dtxGlobalState = newDtxState;
            return;
        }

        if (frameType == RxSpeechBad || frameType == RxNoData || frameType == RxOnset)
        {
            bfi = 1;
            if (frameType == RxNoData || frameType == RxOnset)
                BuildCnParam(ref _nodataSeed, AmrNbTables.Prmno[mode], BitnoTable[mode], parm);
        }
        else if (frameType == RxSpeechDegraded)
        {
            pdfi = 1;
        }

        // Error concealment state machine (TS 26.091 section 5).
        if (bfi != 0) _state = Add(_state, 1);
        else if (_state == 6) _state = 5;
        else _state = 0;
        if (_state > 6) _state = 6;

        if (_dtxGlobalState == Dtx)
        {
            _state = 5;
            _prevBf = 0;
        }
        else if (_dtxGlobalState == DtxMute)
        {
            _state = 5;
            _prevBf = 1;
        }

        Array.Copy(_pastLsfQ, _prevLsf, M);

        if (mode != Mr122)
        {
            DPlsf3(mode, bfi, parm, pi, _lspNew);
            pi += 3;
            IntLpc1To3(_lspOld, _lspNew, aT);
        }
        else
        {
            DPlsf5(bfi, parm, pi, _lspMid, _lspNew);
            pi += 5;
            IntLpc1And3(_lspOld, _lspMid, _lspNew, aT);
        }
        for (int i = 0; i < M; i++) _lspOld[i] = _lspNew[i];

        int azOff = 0;
        bool evenSubfr = false;
        short subfrNr = -1;
        for (int iSubfr = 0; iSubfr < LFrame; iSubfr += LSubfr)
        {
            subfrNr = Add(subfrNr, 1);
            evenSubfr = !evenSubfr;
            pitFlag = (short)iSubfr;
            if (iSubfr == LFrameBy2)
            {
                if (mode != Mr475 && mode != Mr515) pitFlag = 0;
            }

            index = parm[pi++];

            if (mode != Mr122)
            {
                bool flag4 = mode == Mr475 || mode == Mr515 || mode == Mr59 || mode == Mr67;
                short deltaFrcLow = 5, deltaFrcRange = 9;
                if (mode == Mr795)
                {
                    deltaFrcLow = 10;
                    deltaFrcRange = 19;
                }
                short t0Min = Sub(_oldT0, deltaFrcLow);
                if (t0Min < PitMin) t0Min = PitMin;
                short t0Max = Add(t0Min, deltaFrcRange);
                if (t0Max > PitMax)
                {
                    t0Max = PitMax;
                    t0Min = Sub(t0Max, deltaFrcRange);
                }

                DecLag3(index, t0Min, t0Max, pitFlag, _oldT0, out t0, out t0Frac, flag4);
                _t0LagBuff = t0;

                if (bfi != 0)
                {
                    if (_oldT0 < PitMax) _oldT0 = Add(_oldT0, 1);
                    t0 = _oldT0;
                    t0Frac = 0;
                    if (_inBackgroundNoise != 0 && _voicedHangover > 4 &&
                        (mode == Mr475 || mode == Mr515 || mode == Mr59))
                    {
                        t0 = _t0LagBuff;
                    }
                }
                PredLt3Or6(t0, t0Frac, LSubfr, true);
            }
            else
            {
                DecLag6(index, PitMinMr122, PitMax, pitFlag, ref t0, out t0Frac);
                if (bfi == 0 && (pitFlag == 0 || index < 61))
                {
                    // good frame: keep the decoded lag
                }
                else
                {
                    _t0LagBuff = t0;
                    t0 = _oldT0;
                    t0Frac = 0;
                }
                PredLt3Or6(t0, t0Frac, LSubfr, false);
            }

            // Algebraic codebook (TS 26.090 section 6.1 / 5.x per mode).
            if (mode == Mr475 || mode == Mr515)
            {
                index = parm[pi++];
                short sign = parm[pi++];
                DecodeI40Bits9(subfrNr, sign, index, _code);
                pitSharp = Shl(_sharp, 1);
            }
            else if (mode == Mr59)
            {
                index = parm[pi++];
                short sign = parm[pi++];
                Decode2i40Bits11(sign, index, _code);
                pitSharp = Shl(_sharp, 1);
            }
            else if (mode == Mr67)
            {
                index = parm[pi++];
                short sign = parm[pi++];
                Decode3i40Bits14(sign, index, _code);
                pitSharp = Shl(_sharp, 1);
            }
            else if (mode <= Mr795)
            {
                index = parm[pi++];
                short sign = parm[pi++];
                Decode4i40Bits17(sign, index, _code);
                pitSharp = Shl(_sharp, 1);
            }
            else if (mode == Mr102)
            {
                Dec8i40Bits31(parm, pi, _code);
                pi += 7;
                pitSharp = Shl(_sharp, 1);
            }
            else
            {
                index = parm[pi++];
                if (bfi != 0) gainPit = EcGainPitch(_state);
                else gainPit = DGainPitch(mode, index);
                gainPit = EcGainPitchUpdate(bfi, _prevBf, gainPit);
                Dec10i40Bits35(parm, pi, _code);
                pi += 10;
                pitSharp = Shl(gainPit, 1);
            }

            // Pitch sharpening of the innovation (TS 26.090 section 5.x / 6.1).
            for (int i = t0; i < LSubfr; i++)
            {
                short temp = Mult(_code[i - t0], pitSharp);
                _code[i] = Add(_code[i], temp);
            }

            // Gains.
            if (mode == Mr475)
            {
                if (evenSubfr) indexMr475 = parm[pi++];
                if (bfi == 0)
                {
                    DecGain(mode, indexMr475, _code, evenSubfr, out gainPit, out gainCode);
                }
                else
                {
                    gainPit = EcGainPitch(_state);
                    gainCode = EcGainCode(_state);
                }
                gainPit = EcGainPitchUpdate(bfi, _prevBf, gainPit);
                gainCode = EcGainCodeUpdate(bfi, _prevBf, gainCode);
                pitSharp = gainPit;
                if (pitSharp > SharpMax) pitSharp = SharpMax;
            }
            else if (mode <= Mr74 || mode == Mr102)
            {
                index = parm[pi++];
                if (bfi == 0)
                {
                    DecGain(mode, index, _code, evenSubfr, out gainPit, out gainCode);
                }
                else
                {
                    gainPit = EcGainPitch(_state);
                    gainCode = EcGainCode(_state);
                }
                gainPit = EcGainPitchUpdate(bfi, _prevBf, gainPit);
                gainCode = EcGainCodeUpdate(bfi, _prevBf, gainCode);
                pitSharp = gainPit;
                if (pitSharp > SharpMax) pitSharp = SharpMax;
                if (mode == Mr102)
                {
                    if (_oldT0 > LSubfr + 5) pitSharp = Shr(pitSharp, 2);
                }
            }
            else
            {
                index = parm[pi++];
                if (mode == Mr795)
                {
                    if (bfi != 0) gainPit = EcGainPitch(_state);
                    else gainPit = DGainPitch(mode, index);
                    gainPit = EcGainPitchUpdate(bfi, _prevBf, gainPit);

                    index = parm[pi++];
                    if (bfi == 0) gainCode = DGainCode(mode, index, _code);
                    else gainCode = EcGainCode(_state);
                    gainCode = EcGainCodeUpdate(bfi, _prevBf, gainCode);
                    pitSharp = gainPit;
                    if (pitSharp > SharpMax) pitSharp = SharpMax;
                }
                else
                {
                    // MR122: the pitch gain was decoded together with the codebook above.
                    if (bfi == 0) gainCode = DGainCode(mode, index, _code);
                    else gainCode = EcGainCode(_state);
                    gainCode = EcGainCodeUpdate(bfi, _prevBf, gainCode);
                    pitSharp = gainPit;
                }
            }

            if (mode != Mr475 || !evenSubfr)
            {
                _sharp = gainPit;
                if (_sharp > SharpMax) _sharp = SharpMax;
            }

            pitSharp = Shl(pitSharp, 1);
            if (pitSharp > 16384)
            {
                for (int i = 0; i < LSubfr; i++)
                {
                    short temp = Mult(_oldExc[ExcOff + i], pitSharp);
                    int lTemp = LMult(temp, gainPit);
                    if (mode == Mr122) lTemp = LShr(lTemp, 1);
                    _excp[i] = Round(lTemp);
                }
            }

            if (bfi == 0)
            {
                for (int i = 0; i < 8; i++) _ltpGainHistory[i] = _ltpGainHistory[i + 1];
                _ltpGainHistory[8] = gainPit;
            }

            // Background-noise adaptations of the gains (TS 26.091 section 6).
            if ((_prevBf != 0 || bfi != 0) && _inBackgroundNoise != 0 &&
                (mode == Mr475 || mode == Mr515 || mode == Mr59))
            {
                if (gainPit > 12288) gainPit = Add(Shr(Sub(gainPit, 12288), 1), 12288);
                if (gainPit > 14745) gainPit = 14745;
            }

            IntLsf(_prevLsf, _pastLsfQ, iSubfr, _lsfI);
            gainCodeMix = CbGainAverage(mode, gainCode, _lsfI, _lspMeanSave, bfi, _prevBf, pdfi, _prevPdf,
                _inBackgroundNoise, _voicedHangover);
            if (mode > Mr67 && mode != Mr102) gainCodeMix = gainCode;

            int tmpShift;
            if (mode <= Mr102)
            {
                pitchFac = gainPit;
                tmpShift = 1;
            }
            else
            {
                pitchFac = Shr(gainPit, 1);
                tmpShift = 2;
            }

            for (int i = 0; i < LSubfr; i++)
            {
                _excEnhanced[i] = _oldExc[ExcOff + i];
                int lTemp = LMult(_oldExc[ExcOff + i], pitchFac);
                lTemp = LMac(lTemp, _code[i], gainCode);
                lTemp = LShl(lTemp, tmpShift);
                _oldExc[ExcOff + i] = Round(lTemp);
            }

            _phLockFull = 0; // ph_disp_release
            if ((mode == Mr475 || mode == Mr515 || mode == Mr59) && _voicedHangover > 3 &&
                _inBackgroundNoise != 0 && bfi != 0)
            {
                _phLockFull = 1; // ph_disp_lock
            }
            PhDisp(mode, _excEnhanced, gainCodeMix, gainPit, _code, pitchFac, tmpShift);

            // Excitation energy (for the energy control of TS 26.091).
            int lEnergy = 0;
            for (int i = 0; i < LSubfr; i++) lEnergy = LMac(lEnergy, _excEnhanced[i], _excEnhanced[i]);
            lEnergy = LShr(lEnergy, 1);
            lEnergy = SqrtLExp(lEnergy, out short expTemp);
            lEnergy = LShr(lEnergy, Add(Shr(expTemp, 1), 15));
            lEnergy = LShr(lEnergy, 2);
            short excEnergy = ExtractL(lEnergy);

            if ((mode == Mr475 || mode == Mr515 || mode == Mr59) && _voicedHangover > 5 &&
                _inBackgroundNoise != 0 && _state < 4 &&
                ((pdfi != 0 && _prevPdf != 0) || bfi != 0 || _prevBf != 0))
            {
                short carefulFlag = 0;
                if (pdfi != 0 && bfi == 0) carefulFlag = 1;
                ExCtrl(_excEnhanced, excEnergy, _excEnergyHist, _voicedHangover, _prevBf, carefulFlag);
            }

            if (_inBackgroundNoise != 0 && (bfi != 0 || _prevBf != 0) && _state < 4)
            {
                // keep the history unchanged
            }
            else
            {
                for (int i = 0; i < 8; i++) _excEnergyHist[i] = _excEnergyHist[i + 1];
                _excEnergyHist[8] = excEnergy;
            }

            // Synthesis (TS 26.090 section 6.1).
            if (pitSharp > 16384)
            {
                for (int i = 0; i < LSubfr; i++) _excp[i] = Add(_excp[i], _excEnhanced[i]);
                Agc2(_excEnhanced, _excp, LSubfr);
                Overflow = false;
                SynFilt(aT, azOff, _excp, 0, synth, iSubfr, LSubfr, _memSyn, 0, false);
            }
            else
            {
                Overflow = false;
                SynFilt(aT, azOff, _excEnhanced, 0, synth, iSubfr, LSubfr, _memSyn, 0, false);
            }

            if (Overflow)
            {
                // Scale the excitation memory down and redo the synthesis, updating the filter memory.
                for (int i = 0; i < PitMax + LInterpol + LSubfr; i++) _oldExc[i] = Shr(_oldExc[i], 2);
                for (int i = 0; i < LSubfr; i++) _excEnhanced[i] = Shr(_excEnhanced[i], 2);
                SynFilt(aT, azOff, _excEnhanced, 0, synth, iSubfr, LSubfr, _memSyn, 0, true);
            }
            else
            {
                Array.Copy(synth, iSubfr + LSubfr - M, _memSyn, 0, M);
            }

            Array.Copy(_oldExc, LSubfr, _oldExc, 0, PitMax + LInterpol);

            azOff += Mp1;
            _oldT0 = t0;
        }

        _inBackgroundNoise = BgnScd(_ltpGainHistory, synth);
        DtxDecActivityUpdate(_pastLsfQ, synth);
        _prevBf = bfi;
        _prevPdf = pdfi;
        LspAvg(_pastLsfQ);
        _dtxGlobalState = newDtxState;
    }

    // ---------------------------------------------------------------------------------------------------------
    // Synthesis filter and helpers (TS 26.073 syn_filt.c, residu.c, weight_a.c)

    /// <summary>
    /// 1/A(z) synthesis over <paramref name="lg"/> samples: y[i] = x[i] - sum a[j] y[i-j] in Q12 coefficients
    /// (TS 26.090 section 5.x). <paramref name="x"/> and <paramref name="y"/> may be the same array.
    /// </summary>
    private void SynFilt(short[] a, int aOff, short[] x, int xOff, short[] y, int yOff, int lg, short[] mem, int memOff, bool update)
    {
        short[] tmp = _synTmp;
        for (int i = 0; i < M; i++) tmp[i] = mem[memOff + i];
        for (int i = 0; i < lg; i++)
        {
            int s = LMult(x[xOff + i], a[aOff]);
            for (int j = 1; j <= M; j++) s = LMsu(s, a[aOff + j], tmp[M + i - j]);
            s = LShl(s, 3);
            tmp[M + i] = Round(s);
        }
        for (int i = 0; i < lg; i++) y[yOff + i] = tmp[i + M];
        if (update)
        {
            for (int i = 0; i < M; i++) mem[memOff + i] = y[yOff + lg - M + i];
        }
    }

    private void Residu(short[] a, short[] x, int xOff, short[] y, int lg)
    {
        for (int i = 0; i < lg; i++)
        {
            int s = LMult(x[xOff + i], a[0]);
            for (int j = 1; j <= M; j++) s = LMac(s, a[j], x[xOff + i - j]);
            s = LShl(s, 3);
            y[i] = Round(s);
        }
    }

    private void WeightAi(short[] a, int aOff, short[] fac, short[] aExp)
    {
        aExp[0] = a[aOff];
        for (int i = 1; i <= M; i++) aExp[i] = Round(LMult(a[aOff + i], fac[i - 1]));
    }

    /// <summary>Pred_lt_3or6: adaptive codebook vector by interpolating the past excitation (TS 26.090 section 5.x).</summary>
    private void PredLt3Or6(short t0, short frac, int lSubfr, bool flag3)
    {
        short[] exc = _oldExc;
        int x0 = ExcOff - t0;
        frac = Negate(frac);
        if (flag3) frac = Shl(frac, 1);
        if (frac < 0)
        {
            frac = Add(frac, 6);
            x0--;
        }
        short[] inter6 = AmrNbTables.Inter6;
        for (int j = 0; j < lSubfr; j++)
        {
            int x1 = x0++;
            int x2 = x0;
            int c1 = frac;
            int c2 = 6 - frac;
            int s = 0;
            for (int i = 0, k = 0; i < 10; i++, k += 6)
            {
                s = LMac(s, exc[x1 - i], inter6[c1 + k]);
                s = LMac(s, exc[x2 + i], inter6[c2 + k]);
            }
            exc[ExcOff + j] = Round(s);
        }
    }

    // ---------------------------------------------------------------------------------------------------------
    // Post filter (TS 26.090 section 6.2 / TS 26.073 pstfilt.c, agc.c, preemph.c)

    private void PostFilter(int mode, short[] syn, short[] az4)
    {
        const int LH = 22;
        short[] synWork = _synthBuf; // syn_work = &synth_buf[M]
        Array.Copy(syn, 0, synWork, M, LFrame);
        int azOff = 0;
        short[] h = _h;
        for (int iSubfr = 0; iSubfr < LFrame; iSubfr += LSubfr)
        {
            if (mode == Mr122 || mode == Mr102)
            {
                WeightAi(az4, azOff, AmrNbTables.Gamma3Mr122, _ap3);
                WeightAi(az4, azOff, AmrNbTables.Gamma4Mr122, _ap4);
            }
            else
            {
                WeightAi(az4, azOff, AmrNbTables.Gamma3, _ap3);
                WeightAi(az4, azOff, AmrNbTables.Gamma4, _ap4);
            }

            Residu(_ap3, synWork, M + iSubfr, _res2, LSubfr);

            // Impulse response of A(z/g3)/A(z/g4) to get the tilt compensation gain.
            Array.Copy(_ap3, h, Mp1);
            Array.Clear(h, M + 1, LH - M - 1);
            SynFilt(_ap4, 0, h, 0, h, 0, LH, h, M + 1, false);

            int lTmp = LMult(h[0], h[0]);
            for (int i = 1; i < LH; i++) lTmp = LMac(lTmp, h[i], h[i]);
            short temp1 = ExtractH(lTmp);

            lTmp = LMult(h[0], h[1]);
            for (int i = 1; i < LH - 1; i++) lTmp = LMac(lTmp, h[i], h[i + 1]);
            short temp2 = ExtractH(lTmp);

            if (temp2 <= 0)
            {
                temp2 = 0;
            }
            else
            {
                temp2 = Mult(temp2, 26214); // MU
                temp2 = DivS(temp2, temp1);
            }

            Preemphasis(_res2, temp2, LSubfr);
            SynFilt(_ap4, 0, _res2, 0, syn, iSubfr, LSubfr, _memSynPst, 0, true);
            Agc(synWork, M + iSubfr, syn, iSubfr, 29491, LSubfr);
            azOff += Mp1;
        }
        Array.Copy(synWork, LFrame, synWork, 0, M); // syn_work[-M..] = syn_work[L_FRAME-M..]
    }

    private void Preemphasis(short[] signal, short g, int len)
    {
        int p1 = len - 1;
        int p2 = p1 - 1;
        short temp = signal[p1];
        for (int i = 0; i <= len - 2; i++)
        {
            signal[p1] = Sub(signal[p1], Mult(g, signal[p2--]));
            p1--;
        }
        signal[p1] = Sub(signal[p1], Mult(g, _memPre));
        _memPre = temp;
    }

    private int EnergyOld(short[] x, int off, int len)
    {
        short temp = Shr(x[off], 2);
        int s = LMult(temp, temp);
        for (int i = 1; i < len; i++)
        {
            temp = Shr(x[off + i], 2);
            s = LMac(s, temp, temp);
        }
        return s;
    }

    private int EnergyNew(short[] x, int off, int len)
    {
        bool ovSave = Overflow;
        int s = LMult(x[off], x[off]);
        for (int i = 1; i < len; i++) s = LMac(s, x[off + i], x[off + i]);
        if (LSub(s, Max32) == 0)
        {
            Overflow = ovSave;
            s = EnergyOld(x, off, len);
        }
        else
        {
            s = LShr(s, 4);
        }
        return s;
    }

    /// <summary>agc: scales sig_out so its energy follows sig_in (TS 26.090 section 6.2.x).</summary>
    private void Agc(short[] sigIn, int inOff, short[] sigOut, int outOff, short agcFac, int len)
    {
        int s = EnergyNew(sigOut, outOff, len);
        if (s == 0)
        {
            _agcPastGain = 0;
            return;
        }
        short exp = Sub(NormL(s), 1);
        short gainOut = Round(LShl(s, exp));
        s = EnergyNew(sigIn, inOff, len);
        short g0;
        if (s == 0)
        {
            g0 = 0;
        }
        else
        {
            short i = NormL(s);
            short gainIn = Round(LShl(s, i));
            exp = Sub(exp, i);
            s = LDepositL(DivS(gainOut, gainIn));
            s = LShl(s, 7);
            s = LShr(s, exp);
            s = InvSqrt(s);
            i = Round(LShl(s, 9));
            g0 = Mult(i, Sub(32767, agcFac));
        }
        short gain = _agcPastGain;
        for (int k = 0; k < len; k++)
        {
            gain = Mult(gain, agcFac);
            gain = Add(gain, g0);
            sigOut[outOff + k] = ExtractH(LShl(LMult(sigOut[outOff + k], gain), 3));
        }
        _agcPastGain = gain;
    }

    /// <summary>agc2: one-shot energy matching used on the pitch-sharpened excitation.</summary>
    private void Agc2(short[] sigIn, short[] sigOut, int len)
    {
        int s = EnergyNew(sigOut, 0, len);
        if (s == 0) return;
        short exp = Sub(NormL(s), 1);
        short gainOut = Round(LShl(s, exp));
        s = EnergyNew(sigIn, 0, len);
        short g0;
        if (s == 0)
        {
            g0 = 0;
        }
        else
        {
            short i = NormL(s);
            short gainIn = Round(LShl(s, i));
            exp = Sub(exp, i);
            s = LDepositL(DivS(gainOut, gainIn));
            s = LShl(s, 7);
            s = LShr(s, exp);
            s = InvSqrt(s);
            g0 = Round(LShl(s, 9));
        }
        for (int k = 0; k < len; k++) sigOut[k] = ExtractH(LShl(LMult(sigOut[k], g0), 3));
    }

    /// <summary>Post_Process: 2nd order high-pass at 60 Hz (TS 26.090 section 6.3), with gain 2 (TS 26.073 post_pro.c).</summary>
    private void PostProcess(short[] signal, int lg)
    {
        for (int i = 0; i < lg; i++)
        {
            short x2 = _x1;
            _x1 = _x0;
            _x0 = signal[i];
            int lTmp = Mpy3216(_y1Hi, _y1Lo, 15836);
            lTmp = LAdd(lTmp, Mpy3216(_y2Hi, _y2Lo, -7667));
            lTmp = LMac(lTmp, _x0, 7699);
            lTmp = LMac(lTmp, _x1, -15398);
            lTmp = LMac(lTmp, x2, 7699);
            lTmp = LShl(lTmp, 2);
            signal[i] = Round(LShl(lTmp, 1));
            _y2Hi = _y1Hi;
            _y2Lo = _y1Lo;
            LExtract(lTmp, out _y1Hi, out _y1Lo);
        }
    }

    // ---------------------------------------------------------------------------------------------------------
    // Math library (TS 26.073 log2.c, pow2.c, inv_sqrt.c, sqrt_l.c)

    private void Log2Norm(int lX, short exp, out short exponent, out short fraction)
    {
        if (lX <= 0)
        {
            exponent = 0;
            fraction = 0;
            return;
        }
        exponent = Sub(30, exp);
        lX = LShr(lX, 9);
        short i = ExtractH(lX);
        lX = LShr(lX, 1);
        short a = ExtractL(lX);
        a = (short)(a & 0x7fff);
        i = Sub(i, 32);
        short[] table = AmrNbTables.Log2;
        int lY = LDepositH(table[i]);
        short tmp = Sub(table[i], table[i + 1]);
        lY = LMsu(lY, tmp, a);
        fraction = ExtractH(lY);
    }

    private void Log2(int lX, out short exponent, out short fraction)
    {
        short exp = NormL(lX);
        Log2Norm(LShl(lX, exp), exp, out exponent, out fraction);
    }

    private int Pow2(short exponent, short fraction)
    {
        int lX = LMult(fraction, 32);
        short i = ExtractH(lX);
        lX = LShr(lX, 1);
        short a = ExtractL(lX);
        a = (short)(a & 0x7fff);
        short[] table = AmrNbTables.Pow2;
        lX = LDepositH(table[i]);
        short tmp = Sub(table[i], table[i + 1]);
        lX = LMsu(lX, tmp, a);
        short exp = Sub(30, exponent);
        return LShrR(lX, exp);
    }

    private int InvSqrt(int lX)
    {
        if (lX <= 0) return 0x3fffffff;
        short exp = NormL(lX);
        lX = LShl(lX, exp);
        exp = Sub(30, exp);
        if ((exp & 1) == 0) lX = LShr(lX, 1);
        exp = Shr(exp, 1);
        exp = Add(exp, 1);
        lX = LShr(lX, 9);
        short i = ExtractH(lX);
        lX = LShr(lX, 1);
        short a = ExtractL(lX);
        a = (short)(a & 0x7fff);
        i = Sub(i, 16);
        short[] table = AmrNbTables.InvSqrt;
        int lY = LDepositH(table[i]);
        short tmp = Sub(table[i], table[i + 1]);
        lY = LMsu(lY, tmp, a);
        return LShr(lY, exp);
    }

    private int SqrtLExp(int lX, out short exp)
    {
        if (lX <= 0)
        {
            exp = 0;
            return 0;
        }
        short e = (short)(NormL(lX) & 0xFFFE);
        lX = LShl(lX, e);
        exp = e;
        lX = LShr(lX, 9);
        short i = ExtractH(lX);
        lX = LShr(lX, 1);
        short a = ExtractL(lX);
        a = (short)(a & 0x7fff);
        i = Sub(i, 16);
        short[] table = AmrNbTables.SqrtL;
        int lY = LDepositH(table[i]);
        short tmp = Sub(table[i], table[i + 1]);
        lY = LMsu(lY, tmp, a);
        return lY;
    }

    /// <summary>gmed_n: median of n values (n at most 9), TS 26.073 gmed_n.c.</summary>
    private short GmedN(short[] ind, int off, int n)
    {
        short[] tmp = _gmedTmp;
        short[] tmp2 = _gmedTmp2;
        int ix = 0;
        for (int i = 0; i < n; i++) tmp2[i] = ind[off + i];
        for (int i = 0; i < n; i++)
        {
            short max = -32767;
            for (int j = 0; j < n; j++)
            {
                if (tmp2[j] >= max)
                {
                    max = tmp2[j];
                    ix = j;
                }
            }
            tmp2[ix] = -32768;
            tmp[i] = (short)ix;
        }
        return ind[off + tmp[n >> 1]];
    }
}
