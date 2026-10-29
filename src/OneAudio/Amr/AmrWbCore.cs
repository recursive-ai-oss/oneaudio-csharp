// Copyright (c) Recursive AI, LLC. MIT OR Apache-2.0.
namespace OneAudio.Amr;

/// <summary>
/// The AMR wideband speech decoder core (3GPP TS 26.190 with the error concealment of TS 26.191 and the comfort
/// noise of TS 26.192), written in the fixed-point arithmetic of TS 26.173 so its output is bit-exact against the
/// TS 26.174 test sequences. This part holds the state and the frame-level control flow (TS 26.190 section 6); the
/// LPC, excitation, gain, concealment and DTX parts are in the sibling partial files.
/// </summary>
internal sealed partial class AmrWbCore : AmrFixed
{
    internal const int LFrame16k = 320;
    private const int LFrame = 256;
    private const int LSubfr16k = 80;
    private const int LSubfr = 64;
    private const int NbSubfr = 4;
    private const int M = 16;
    private const int M16k = 20;
    private const int PitMin = 34;
    private const int PitFr2 = 128;
    private const int PitFr1_9b = 160;
    private const int PitFr1_8b = 92;
    private const int PitMax = 231;
    private const int LInterpol = 17;
    private const int PitSharp = 27853;
    private const int PreemphFac = 22282;
    private const int QMax = 8;
    private const int RandomInitSeed = 21845;
    private const int LMeanBuf = 3;
    private const int ExcOff = PitMax + LInterpol;

    private const int NbBits7k = 132, NbBits9k = 177, NbBits12k = 253, NbBits14k = 285, NbBits16k = 317, NbBits18k = 365,
        NbBits20k = 397, NbBits23k = 461, NbBits24k = 477, NbBitsSid = 35;

    /// <summary>Mode numbers (TS 26.173 cnst.h): 0..8 are 6.60..23.85 kbit/s, 9 is the SID (comfort noise) mode.</summary>
    internal const int Mode7k = 0, Mode9k = 1, Mode12k = 2, Mode14k = 3, Mode16k = 4, Mode18k = 5, Mode20k = 6,
        Mode23k = 7, Mode24k = 8, MrDtx = 9;

    internal const int RxSpeechGood = 0, RxSpeechProbablyDegraded = 1, RxSpeechLost = 2, RxSpeechBad = 3,
        RxSidFirst = 4, RxSidUpdate = 5, RxSidBad = 6, RxNoData = 7;

    private const int Speech = 0, Dtx = 1, DtxMute = 2;

    /// <summary>Number of speech bits per mode (nb_of_bits in TS 26.173 bits.h); index 9 is the SID frame.</summary>
    internal static readonly short[] NbOfBits = [132, 177, 253, 285, 317, 365, 397, 461, 477, 35];

    private static readonly short[] InterpolFrac = [14746, 26214, 31457, 32767];

    // ---- Decoder_State ----
    private readonly short[] _oldExc = new short[PitMax + LInterpol];
    private readonly short[] _ispold = new short[M];
    private readonly short[] _isfold = new short[M];
    private readonly short[] _isfBuf = new short[LMeanBuf * M];
    private readonly short[] _pastIsfq = new short[M];
    private short _tiltCode, _qOld;
    private readonly short[] _qsubfr = new short[4];
    private int _lGcThres;
    private readonly short[] _memSynHi = new short[M];
    private readonly short[] _memSynLo = new short[M];
    private short _memDeemph;
    private readonly short[] _memSigOut = new short[6];
    private readonly short[] _memOversamp = new short[24];
    private readonly short[] _memSynHf = new short[M16k];
    private readonly short[] _memHf = new short[30];
    private readonly short[] _memHf3 = new short[30];
    private short _seed, _seed2, _seed3, _oldT0;
    private readonly short[] _lagHist = new short[5];
    private readonly short[] _decGain = new short[23];
    private readonly short[] _dispMem = new short[8];
    private readonly short[] _memHp400 = new short[6];
    private short _prevBfi, _state, _firstFrame, _vadHist;

    // ---- Scratch ----
    private readonly short[] _workExc = new short[LFrame + 1 + PitMax + LInterpol];
    private readonly short[] _aq = new short[NbSubfr * (M + 1)];
    private readonly short[] _ispnew = new short[M];
    private readonly short[] _isf = new short[M];
    private readonly short[] _code = new short[LSubfr];
    private readonly short[] _code2 = new short[LSubfr];
    private readonly short[] _exc2 = new short[LFrame];
    private readonly short[] _excp = new short[LSubfr];
    private readonly short[] _isfTmp = new short[M];
    private readonly short[] _hfIsf = new short[M16k];
    private readonly short[] _ind = new short[8];
    private readonly short[] _pos = new short[6];
    private readonly short[] _synthHi = new short[M + LSubfr];
    private readonly short[] _synthLo = new short[M + LSubfr];
    private readonly short[] _synth = new short[LSubfr];
    private readonly short[] _hf = new short[LSubfr16k];
    private readonly short[] _ap = new short[M16k + 1];
    private readonly short[] _hfA = new short[M16k + 1];
    private readonly short[] _synBuf = new short[LSubfr16k + M16k];
    private readonly short[] _intIsp = new short[M];
    private readonly int[] _f1 = new int[11];
    private readonly int[] _f2 = new int[10];
    private readonly short[] _sig = new short[Math.Max(LSubfr + 24, LSubfr16k + 30)];
    private readonly short[] _synthOut = new short[LFrame16k];
    private readonly short[] _bits = new short[NbBits24k + 8];
    private int _bitPos;

    private short _prevMode;

    /// <summary>Mode of the last frame that carried one (the mode reused for SID, lost and no-data frames).</summary>
    internal int PrevMode => _prevMode;
    private bool _homed = true;

    public AmrWbCore()
    {
        _dtxSt = new DtxState();
        ResetDecoder(true);
    }

    /// <summary>Reset_decoder (TS 26.173 dec_main.c); <paramref name="resetAll"/> also clears filter memories and DTX state.</summary>
    private void ResetDecoder(bool resetAll)
    {
        Array.Clear(_oldExc);
        Array.Clear(_pastIsfq);
        _oldT0 = 64;
        _firstFrame = 1;
        _lGcThres = 0;
        _tiltCode = 0;
        Array.Clear(_dispMem); // Init_Phase_dispersion
        _qOld = QMax;
        _qsubfr[3] = QMax;
        _qsubfr[2] = QMax;
        _qsubfr[1] = QMax;
        _qsubfr[0] = QMax;
        if (resetAll)
        {
            InitDGain2();
            Array.Clear(_memOversamp);
            Array.Clear(_memSigOut);
            Array.Clear(_memHf);
            Array.Clear(_memHf3);
            Array.Clear(_memHp400);
            for (int i = 0; i < 5; i++) _lagHist[i] = 64; // Init_Lagconc
            Array.Copy(AmrWbTables.IspInit, _ispold, M);
            Array.Copy(AmrWbTables.IsfInit, _isfold, M);
            for (int i = 0; i < LMeanBuf; i++) Array.Copy(AmrWbTables.IsfInit, 0, _isfBuf, i * M, M);
            _memDeemph = 0;
            _seed = RandomInitSeed;
            _seed2 = RandomInitSeed;
            _seed3 = RandomInitSeed;
            _state = 0;
            _prevBfi = 0;
            Array.Clear(_memSynHf);
            Array.Clear(_memSynHi);
            Array.Clear(_memSynLo);
            DtxDecReset();
            _vadHist = 0;
        }
    }

    /// <summary>Resets every state to its initial value (a new stream, or after a decoder homing frame).</summary>
    public void ResetAll()
    {
        ResetDecoder(true);
        _prevMode = 0;
        _homed = true;
    }

    // ---------------------------------------------------------------------------------------------------------
    // Frame entry point

    private static readonly short[][] DhfTable =
    [
        AmrWbTables.Dhf7k, AmrWbTables.Dhf9k, AmrWbTables.Dhf12k, AmrWbTables.Dhf14k, AmrWbTables.Dhf16k,
        AmrWbTables.Dhf18k, AmrWbTables.Dhf20k, AmrWbTables.Dhf23k, AmrWbTables.Dhf24k,
    ];

    /// <summary>
    /// Decodes one frame the way the reference decoder program does (TS 26.173 decoder.c), including decoder homing
    /// frames (TS 26.190 section 7 / TS 26.174). The bits of the frame are copied over the start of an internal
    /// buffer that, as in the reference program, keeps stale bits past the end of short frames.
    /// </summary>
    /// <param name="frameType">Received frame type, one of the Rx constants.</param>
    /// <param name="mode">Mode of the frame (ignored for lost and no-data frames, which reuse the previous mode).</param>
    /// <param name="bits">Frame bits, one per element: 1 is a set bit, anything else clear.</param>
    /// <param name="bitCount">Number of valid bits in <paramref name="bits"/>.</param>
    /// <param name="pcm">320 output samples, 14-bit PCM in 16-bit words.</param>
    public void Decode(int frameType, int mode, ReadOnlySpan<short> bits, int bitCount, Span<short> pcm)
    {
        bits.Slice(0, Math.Min(bitCount, _bits.Length)).CopyTo(_bits);

        bool resetFlag = false;
        if (frameType == RxNoData || frameType == RxSpeechLost)
        {
            mode = _prevMode;
            resetFlag = false;
        }
        else
        {
            _prevMode = (short)mode;
            if (_homed) resetFlag = HomingTest(mode, first: true);
        }

        bool wasHomed = _homed;
        if (resetFlag && wasHomed)
        {
            for (int i = 0; i < LFrame16k; i++) pcm[i] = 0x0008;
        }
        else
        {
            DecodeFrame(mode, frameType, _synthOut);
            for (int i = 0; i < LFrame16k; i++) pcm[i] = (short)(_synthOut[i] & unchecked((short)0xfffc));
        }
        if (resetFlag && wasHomed)
        {
            for (int i = 0; i < LFrame16k; i++) pcm[i] = (short)(pcm[i] & unchecked((short)0xfffc));
        }

        if (!wasHomed) resetFlag = HomingTest(mode, first: false);
        if (resetFlag) ResetDecoder(true);
        _homed = resetFlag;
    }

    private bool HomingTest(int mode, bool first)
    {
        if (mode == MrDtx) return false;
        int nparms = first ? AmrWbTables.Prmnofsf[mode] : NbOfBits[mode];
        int pos = 0;
        Span<short> param = stackalloc short[32];
        int i = 0, j = 0;
        short shift;
        if (mode != Mode24k)
        {
            int tmp = nparms - 15;
            while (tmp - j > 0)
            {
                param[i] = SerialParmAt(ref pos, 15);
                j += 15;
                i++;
            }
            tmp = nparms - j;
            param[i] = SerialParmAt(ref pos, tmp);
            shift = (short)(15 - tmp);
            param[i] = Shl(param[i], shift);
        }
        else
        {
            for (i = 0; i < 10; i++) param[i] = SerialParmAt(ref pos, 15);
            param[10] = (short)(SerialParmAt(ref pos, 15) & 0x61FF);
            for (i = 11; i < 17; i++) param[i] = SerialParmAt(ref pos, 15);
            param[17] = (short)(SerialParmAt(ref pos, 15) & unchecked((short)0xE0FF));
            for (i = 18; i < 24; i++) param[i] = SerialParmAt(ref pos, 15);
            param[24] = (short)(SerialParmAt(ref pos, 15) & 0x7F0F);
            for (i = 25; i < 31; i++) param[i] = SerialParmAt(ref pos, 15);
            short t = SerialParmAt(ref pos, 8);
            param[31] = Shl(t, 7);
            shift = 0;
        }
        int count = i;
        short[] dhf = DhfTable[mode];
        short jj = 0;
        for (i = 0; i < count; i++)
        {
            jj = (short)(param[i] ^ dhf[i]);
            if (jj != 0) break;
        }
        short mask = 0x7fff;
        mask = Shr(mask, shift);
        mask = Shl(mask, shift);
        // The partial last parameter is compared under its mask; when the loop broke early the indexes are those of
        // the first mismatch, which cannot make the frame a homing frame anyway.
        int last = Math.Min(i, dhf.Length - 1);
        short tmp2 = (short)(dhf[last] & mask);
        tmp2 = (short)(param[Math.Min(i, 31)] ^ tmp2);
        jj = (short)(jj | tmp2);
        return jj == 0;
    }

    private short SerialParmAt(ref int pos, int noOfBits)
    {
        short value = 0;
        for (int i = 0; i < noOfBits; i++)
        {
            value = Shl(value, 1);
            short bit = pos < _bits.Length ? _bits[pos] : (short)0;
            pos++;
            if (bit == 1) value = Add(value, 1);
        }
        return value;
    }

    /// <summary>Serial_parm: reads the next <paramref name="noOfBits"/> bits of the frame, most significant first.</summary>
    private short SerialParm(int noOfBits)
    {
        short v = SerialParmAt(ref _bitPos, noOfBits);
        return v;
    }

    // ---------------------------------------------------------------------------------------------------------
    // decoder (TS 26.173 dec_main.c; TS 26.190 section 6)

    private void DecodeFrame(int mode, int frameType, short[] synth16k)
    {
        _bitPos = 0;
        int nbBits = NbOfBits[mode];
        short[] oldExc = _workExc;
        short[] isf = _isf;
        short[] code = _code;
        short[] exc2 = _exc2;
        short t0 = 0, t0Frac = 0, t0Min = 0, t0Max = 0;
        short gainPit, gainCode, gainCodeLo;
        short bfi, unusableFrame;
        short voiceFac, stabFac;
        short qNew = 0;
        short tmp;
        int lTmp, lGainCode;
        short vadFlag;
        short pitSharp;

        int newDtxState = RxDtxHandler(frameType);
        if (newDtxState != Speech) DtxDec(exc2, newDtxState, isf);

        if (frameType == RxSpeechBad || frameType == RxSpeechProbablyDegraded)
        {
            bfi = 1;
            unusableFrame = 0;
        }
        else if (frameType == RxNoData || frameType == RxSpeechLost)
        {
            bfi = 1;
            unusableFrame = 1;
        }
        else
        {
            bfi = 0;
            unusableFrame = 0;
        }

        if (bfi != 0)
        {
            _state = Add(_state, 1);
            if (_state > 6) _state = 6;
        }
        else
        {
            _state = Shr(_state, 1);
        }

        if (_dtxSt.DtxGlobalState == Dtx)
        {
            _state = 5;
            _prevBfi = 0;
        }
        else if (_dtxSt.DtxGlobalState == DtxMute)
        {
            _state = 5;
            _prevBfi = 1;
        }

        if (newDtxState == Speech)
        {
            vadFlag = SerialParm(1);
            if (bfi == 0)
            {
                if (vadFlag == 0)
                {
                    _vadHist = Add(_vadHist, 1);
                    _dtxSt.DtxVadHist = Add(_dtxSt.DtxVadHist, 1);
                }
                else
                {
                    _vadHist = 0;
                    _dtxSt.DtxVadHist = 0;
                }
            }
            else if (_dtxSt.DtxVadHist > 0)
            {
                _dtxSt.DtxVadHist = Add(_dtxSt.DtxVadHist, 1);
            }
        }

        if (newDtxState != Speech)
        {
            // Comfort noise frame: synthesize the 20 ms from the interpolated ISFs (TS 26.192).
            IsfToIsp(isf, _ispnew, M);
            IspAz(_ispnew, _aq, 0, M, 1);
            Array.Copy(_isfold, _isfTmp, M);
            for (int iSubfr = 0; iSubfr < LFrame; iSubfr += LSubfr)
            {
                int jj = iSubfr >> 6;
                for (int i = 0; i < M; i++)
                {
                    int l = LMult(_isfTmp[i], Sub(32767, InterpolFrac[jj]));
                    l = LMac(l, isf[i], InterpolFrac[jj]);
                    _hfIsf[i] = Round(l);
                }
                Synthesis(_aq, 0, exc2, iSubfr, 0, synth16k, iSubfr * 5 / 4, 1, nbBits, newDtxState, bfi);
            }
            ResetDecoder(false);
            Array.Copy(isf, _isfold, M);
            _prevBfi = bfi;
            _dtxSt.DtxGlobalState = (short)newDtxState;
            return;
        }

        Array.Copy(_oldExc, oldExc, PitMax + LInterpol);
        short[] exc = oldExc; // exc[i] is oldExc[ExcOff + i]

        short[] ind = _ind;
        if (nbBits <= NbBits7k)
        {
            ind[0] = SerialParm(8);
            ind[1] = SerialParm(8);
            ind[2] = SerialParm(7);
            ind[3] = SerialParm(7);
            ind[4] = SerialParm(6);
            Dpisf2s36b(ind, isf, _pastIsfq, _isfold, _isfBuf, bfi, true);
        }
        else
        {
            ind[0] = SerialParm(8);
            ind[1] = SerialParm(8);
            ind[2] = SerialParm(6);
            ind[3] = SerialParm(7);
            ind[4] = SerialParm(7);
            ind[5] = SerialParm(5);
            ind[6] = SerialParm(5);
            Dpisf2s46b(ind, isf, _pastIsfq, _isfold, _isfBuf, bfi, true);
        }

        IsfToIsp(isf, _ispnew, M);
        if (_firstFrame != 0)
        {
            _firstFrame = 0;
            Array.Copy(_ispnew, _ispold, M);
        }
        IntIsp(_ispold, _ispnew, InterpolFrac, _aq);
        Array.Copy(_ispnew, _ispold, M);

        lTmp = 0;
        for (int i = 0; i < M - 1; i++)
        {
            tmp = Sub(isf[i], _isfold[i]);
            lTmp = LMac(lTmp, tmp, tmp);
        }
        tmp = ExtractH(LShl(lTmp, 8));
        tmp = Mult(tmp, 26214);
        tmp = Sub(20480, tmp);
        stabFac = Shl(tmp, 1);
        if (stabFac < 0) stabFac = 0;

        Array.Copy(_isfold, _isfTmp, M);
        Array.Copy(isf, _isfold, M);

        int pAq = 0;
        for (int iSubfr = 0; iSubfr < LFrame; iSubfr += LSubfr)
        {
            int eb = ExcOff + iSubfr; // exc[iSubfr]
            short pitFlag = (short)iSubfr;
            if (iSubfr == 2 * LSubfr && nbBits > NbBits7k) pitFlag = 0;

            short index;
            if (pitFlag == 0)
            {
                if (nbBits <= NbBits9k)
                {
                    index = SerialParm(8);
                    if (index < (PitFr1_8b - PitMin) * 2)
                    {
                        t0 = Add(PitMin, Shr(index, 1));
                        t0Frac = Sub(index, Shl(Sub(t0, PitMin), 1));
                        t0Frac = Shl(t0Frac, 1);
                    }
                    else
                    {
                        t0 = Add(index, PitFr1_8b - ((PitFr1_8b - PitMin) * 2));
                        t0Frac = 0;
                    }
                }
                else
                {
                    index = SerialParm(9);
                    if (index < (PitFr2 - PitMin) * 4)
                    {
                        t0 = Add(PitMin, Shr(index, 2));
                        t0Frac = Sub(index, Shl(Sub(t0, PitMin), 2));
                    }
                    else if (index < ((PitFr2 - PitMin) * 4) + ((PitFr1_9b - PitFr2) * 2))
                    {
                        index = Sub(index, (PitFr2 - PitMin) * 4);
                        t0 = Add(PitFr2, Shr(index, 1));
                        t0Frac = Sub(index, Shl(Sub(t0, PitFr2), 1));
                        t0Frac = Shl(t0Frac, 1);
                    }
                    else
                    {
                        t0 = Add(index, PitFr1_9b - ((PitFr2 - PitMin) * 4) - ((PitFr1_9b - PitFr2) * 2));
                        t0Frac = 0;
                    }
                }

                t0Min = Sub(t0, 8);
                if (t0Min < PitMin) t0Min = PitMin;
                t0Max = Add(t0Min, 15);
                if (t0Max > PitMax)
                {
                    t0Max = PitMax;
                    t0Min = Sub(t0Max, 15);
                }
            }
            else
            {
                if (nbBits <= NbBits9k)
                {
                    index = SerialParm(5);
                    t0 = Add(t0Min, Shr(index, 1));
                    t0Frac = Sub(index, Shl(Sub(t0, t0Min), 1));
                    t0Frac = Shl(t0Frac, 1);
                }
                else
                {
                    index = SerialParm(6);
                    t0 = Add(t0Min, Shr(index, 2));
                    t0Frac = Sub(index, Shl(Sub(t0, t0Min), 2));
                }
            }

            if (bfi != 0)
            {
                Lagconc(ref t0, unusableFrame);
                t0Frac = 0;
            }

            PredLt4(oldExc, eb, t0, t0Frac, LSubfr + 1);

            short select;
            if (unusableFrame != 0) select = 1;
            else if (nbBits <= NbBits9k) select = 0;
            else select = SerialParm(1);

            if (select == 0)
            {
                for (int i = 0; i < LSubfr; i++)
                {
                    lTmp = LMult(5898, oldExc[eb + i - 1]);
                    lTmp = LMac(lTmp, 20972, oldExc[eb + i]);
                    lTmp = LMac(lTmp, 5898, oldExc[eb + i + 1]);
                    code[i] = Round(lTmp);
                }
                Array.Copy(code, 0, oldExc, eb, LSubfr);
            }

            if (unusableFrame != 0)
            {
                for (int i = 0; i < LSubfr; i++) code[i] = Shr(Random(ref _seed), 3);
            }
            else if (nbBits <= NbBits7k)
            {
                ind[0] = SerialParm(12);
                DecAcelp2t64(ind[0], code);
            }
            else if (nbBits <= NbBits9k)
            {
                for (int i = 0; i < 4; i++) ind[i] = SerialParm(5);
                DecAcelp4t64(ind, 20, code);
            }
            else if (nbBits <= NbBits12k)
            {
                for (int i = 0; i < 4; i++) ind[i] = SerialParm(9);
                DecAcelp4t64(ind, 36, code);
            }
            else if (nbBits <= NbBits14k)
            {
                ind[0] = SerialParm(13);
                ind[1] = SerialParm(13);
                ind[2] = SerialParm(9);
                ind[3] = SerialParm(9);
                DecAcelp4t64(ind, 44, code);
            }
            else if (nbBits <= NbBits16k)
            {
                for (int i = 0; i < 4; i++) ind[i] = SerialParm(13);
                DecAcelp4t64(ind, 52, code);
            }
            else if (nbBits <= NbBits18k)
            {
                for (int i = 0; i < 4; i++) ind[i] = SerialParm(2);
                for (int i = 4; i < 8; i++) ind[i] = SerialParm(14);
                DecAcelp4t64(ind, 64, code);
            }
            else if (nbBits <= NbBits20k)
            {
                ind[0] = SerialParm(10);
                ind[1] = SerialParm(10);
                ind[2] = SerialParm(2);
                ind[3] = SerialParm(2);
                ind[4] = SerialParm(10);
                ind[5] = SerialParm(10);
                ind[6] = SerialParm(14);
                ind[7] = SerialParm(14);
                DecAcelp4t64(ind, 72, code);
            }
            else
            {
                for (int i = 0; i < 4; i++) ind[i] = SerialParm(11);
                for (int i = 4; i < 8; i++) ind[i] = SerialParm(11);
                DecAcelp4t64(ind, 88, code);
            }

            // Tilt and pitch sharpening of the innovation (TS 26.190 section 6.1).
            tmp = 0;
            PreemphMem(code, _tiltCode, LSubfr, ref tmp);
            tmp = t0;
            if (t0Frac > 2) tmp = Add(tmp, 1);
            PitShrp(code, tmp, PitSharp, LSubfr);

            if (nbBits <= NbBits9k)
            {
                index = SerialParm(6);
                DGain2(index, 6, code, LSubfr, out gainPit, out lGainCode, bfi, _prevBfi, _state, unusableFrame, _vadHist);
            }
            else
            {
                index = SerialParm(7);
                DGain2(index, 7, code, LSubfr, out gainPit, out lGainCode, bfi, _prevBfi, _state, unusableFrame, _vadHist);
            }

            // Scale the excitation memory so the gain code fits (TS 26.173 dec_main.c Q_new handling).
            tmp = _qsubfr[0];
            for (int i = 1; i < 4; i++)
                if (_qsubfr[i] < tmp) tmp = _qsubfr[i];
            if (tmp > QMax) tmp = QMax;
            qNew = 0;
            lTmp = lGainCode;
            while (LSub(lTmp, 0x08000000) < 0 && qNew < tmp)
            {
                lTmp = LShl(lTmp, 1);
                qNew = Add(qNew, 1);
            }
            gainCode = Round(lTmp);

            ScaleSig(oldExc, eb - (PitMax + LInterpol), PitMax + LInterpol + LSubfr, Sub(qNew, _qOld));
            _qOld = qNew;

            if (bfi == 0)
            {
                for (int i = 4; i > 0; i--) _lagHist[i] = _lagHist[i - 1];
                _lagHist[0] = t0;
                _oldT0 = t0;
            }

            Array.Copy(oldExc, eb, exc2, 0, LSubfr);
            ScaleSig(exc2, 0, LSubfr, -3);

            if (nbBits <= NbBits9k)
            {
                pitSharp = Shl(gainPit, 1);
                if (pitSharp > 16384)
                {
                    for (int i = 0; i < LSubfr; i++)
                    {
                        tmp = Mult(exc2[i], pitSharp);
                        lTmp = LMult(tmp, gainPit);
                        lTmp = LShr(lTmp, 1);
                        _excp[i] = Round(lTmp);
                    }
                }
            }
            else
            {
                pitSharp = 0;
            }

            voiceFac = VoiceFactor(exc2, -3, gainPit, code, gainCode, LSubfr);
            _tiltCode = Add(Shr(voiceFac, 2), 8192);

            Array.Copy(oldExc, eb, exc2, 0, LSubfr);

            for (int i = 0; i < LSubfr; i++)
            {
                lTmp = LMult(code[i], gainCode);
                lTmp = LShl(lTmp, 5);
                lTmp = LMac(lTmp, oldExc[eb + i], gainPit);
                lTmp = LShl(lTmp, 1);
                oldExc[eb + i] = Round(lTmp);
            }

            short max = 1;
            for (int i = 0; i < LSubfr; i++)
            {
                tmp = AbsS(oldExc[eb + i]);
                if (tmp > max) max = tmp;
            }
            tmp = Sub(Add(NormS(max), qNew), 1);
            _qsubfr[3] = _qsubfr[2];
            _qsubfr[2] = _qsubfr[1];
            _qsubfr[1] = _qsubfr[0];
            _qsubfr[0] = tmp;

            LExtract(lGainCode, out gainCode, out gainCodeLo);

            int jm;
            if (nbBits <= NbBits7k) jm = 0;
            else if (nbBits <= NbBits9k) jm = 1;
            else jm = 2;
            PhaseDispersion(gainCode, gainPit, code, jm);

            // Gain code smoothing and innovation emphasis (TS 26.190 section 6.1.x).
            tmp = Sub(16384, Shr(voiceFac, 1));
            short fac = Mult(stabFac, tmp);
            lTmp = lGainCode;
            if (LSub(lTmp, _lGcThres) < 0)
            {
                lTmp = LAdd(lTmp, Mpy3216(gainCode, gainCodeLo, 6226));
                if (LSub(lTmp, _lGcThres) > 0) lTmp = _lGcThres;
            }
            else
            {
                lTmp = Mpy3216(gainCode, gainCodeLo, 27536);
                if (LSub(lTmp, _lGcThres) < 0) lTmp = _lGcThres;
            }
            _lGcThres = lTmp;

            lGainCode = Mpy3216(gainCode, gainCodeLo, Sub(32767, fac));
            LExtract(lTmp, out gainCode, out gainCodeLo);
            lGainCode = LAdd(lGainCode, Mpy3216(gainCode, gainCodeLo, fac));

            tmp = Add(Shr(voiceFac, 3), 4096);
            lTmp = LDepositH(code[0]);
            lTmp = LMsu(lTmp, code[1], tmp);
            _code2[0] = Round(lTmp);
            for (int i = 1; i < LSubfr - 1; i++)
            {
                lTmp = LDepositH(code[i]);
                lTmp = LMsu(lTmp, code[i + 1], tmp);
                lTmp = LMsu(lTmp, code[i - 1], tmp);
                _code2[i] = Round(lTmp);
            }
            lTmp = LDepositH(code[LSubfr - 1]);
            lTmp = LMsu(lTmp, code[LSubfr - 2], tmp);
            _code2[LSubfr - 1] = Round(lTmp);

            gainCode = Round(LShl(lGainCode, qNew));

            for (int i = 0; i < LSubfr; i++)
            {
                lTmp = LMult(_code2[i], gainCode);
                lTmp = LShl(lTmp, 5);
                lTmp = LMac(lTmp, exc2[i], gainPit);
                lTmp = LShl(lTmp, 1);
                exc2[i] = Round(lTmp);
            }

            if (nbBits <= NbBits9k)
            {
                if (pitSharp > 16384)
                {
                    for (int i = 0; i < LSubfr; i++) _excp[i] = Add(_excp[i], exc2[i]);
                    Agc2(exc2, _excp, LSubfr);
                    Array.Copy(_excp, exc2, LSubfr);
                }
            }

            if (nbBits <= NbBits7k)
            {
                int jj = iSubfr >> 6;
                for (int i = 0; i < M; i++)
                {
                    int l = LMult(_isfTmp[i], Sub(32767, InterpolFrac[jj]));
                    l = LMac(l, isf[i], InterpolFrac[jj]);
                    _hfIsf[i] = Round(l);
                }
            }
            else
            {
                Array.Clear(_memSynHf, 0, M16k - M);
            }

            if (nbBits >= NbBits24k)
            {
                short corrGain = SerialParm(4);
                Synthesis(_aq, pAq, exc2, 0, qNew, synth16k, iSubfr * 5 / 4, corrGain, nbBits, newDtxState, bfi);
            }
            else
            {
                Synthesis(_aq, pAq, exc2, 0, qNew, synth16k, iSubfr * 5 / 4, 0, nbBits, newDtxState, bfi);
            }
            pAq += M + 1;
        }

        Array.Copy(oldExc, LFrame, _oldExc, 0, PitMax + LInterpol);
        ScaleSig(oldExc, ExcOff, LFrame, Sub(0, qNew));
        DtxDecActivityUpdate(isf, oldExc, ExcOff);
        _dtxSt.DtxGlobalState = (short)newDtxState;
        _prevBfi = bfi;
    }

    // ---------------------------------------------------------------------------------------------------------
    // Synthesis of one 5 ms subframe (TS 26.173 dec_main.c synthesis; TS 26.190 sections 6.2-6.3)

    private void Synthesis(short[] aq, int aqOff, short[] exc, int excOff, short qNew, short[] synth16k, int s16Off,
        short prms, int nbBits, int newDtxState, short bfi)
    {
        short[] synthHi = _synthHi;
        short[] synthLo = _synthLo;
        short[] synth = _synth;
        short[] hf = _hf;

        Array.Copy(_memSynHi, 0, synthHi, 0, M);
        Array.Copy(_memSynLo, 0, synthLo, 0, M);

        SynFilt32(aq, aqOff, M, exc, excOff, qNew, synthHi, M, synthLo, M, LSubfr);
        Array.Copy(synthHi, LSubfr, _memSynHi, 0, M);
        Array.Copy(synthLo, LSubfr, _memSynLo, 0, M);

        Deemph32(synthHi, M, synthLo, M, synth, PreemphFac, LSubfr);
        Hp50(synth, LSubfr);
        Oversamp16k(synth, LSubfr, synth16k, s16Off);

        for (int i = 0; i < LSubfr16k; i++) hf[i] = Shr(Random(ref _seed2), 3);

        // High band: noise shaped to the level of the low band (TS 26.190 section 6.3).
        ScaleSig(exc, excOff, LSubfr, -3);
        qNew = Sub(qNew, 3);
        short ener = ExtractH(DotProduct12(exc, excOff, exc, excOff, LSubfr, out short expEner));
        expEner = Sub(expEner, Add(qNew, qNew));

        short tmp = ExtractH(DotProduct12(hf, 0, hf, 0, LSubfr16k, out short exp));
        if (tmp > ener)
        {
            tmp = Shr(tmp, 1);
            exp = Add(exp, 1);
        }
        int lTmp = LDepositH(DivS(tmp, ener));
        exp = Sub(exp, expEner);
        IsqrtN(ref lTmp, ref exp);
        lTmp = LShl(lTmp, Add(exp, 1));
        tmp = ExtractH(lTmp);
        for (int i = 0; i < LSubfr16k; i++) hf[i] = Mult(hf[i], tmp);

        Hp400(synth, LSubfr);

        lTmp = 1;
        for (int i = 0; i < LSubfr; i++) lTmp = LMac(lTmp, synth[i], synth[i]);
        exp = NormL(lTmp);
        ener = ExtractH(LShl(lTmp, exp));

        lTmp = 1;
        for (int i = 1; i < LSubfr; i++) lTmp = LMac(lTmp, synth[i], synth[i - 1]);
        tmp = ExtractH(LShl(lTmp, exp));

        short fac;
        if (tmp > 0) fac = DivS(tmp, ener);
        else fac = 0;

        short gain1 = Sub(32767, fac);
        short gain2 = Mult(Sub(32767, fac), 20480);
        gain2 = Shl(gain2, 1);

        short weight1, weight2;
        if (_vadHist > 0)
        {
            weight1 = 0;
            weight2 = 32767;
        }
        else
        {
            weight1 = 32767;
            weight2 = 0;
        }
        tmp = Mult(weight1, gain1);
        tmp = Add(tmp, Mult(weight2, gain2));
        if (tmp != 0) tmp = Add(tmp, 1);
        if (tmp < 3277) tmp = 3277;

        if (nbBits >= NbBits24k && bfi == 0)
        {
            short hfGainInd = prms;
            short hfCorrGain = AmrWbTables.HpGain[hfGainInd];
            for (int i = 0; i < LSubfr16k; i++) hf[i] = Shl(Mult(hf[i], hfCorrGain), 1);
        }
        else
        {
            for (int i = 0; i < LSubfr16k; i++) hf[i] = Mult(hf[i], tmp);
        }

        if (nbBits <= NbBits7k && newDtxState == Speech)
        {
            IsfExtrapolation(_hfIsf);
            IspAz(_hfIsf, _hfA, 0, M16k, 0);
            WeightA(_hfA, _ap, 29491, M16k);
            SynFilt(_ap, M16k, hf, hf, LSubfr16k, _memSynHf, 0, true);
        }
        else
        {
            WeightA(aq, aqOff, _ap, 19661, M);
            SynFilt(_ap, M, hf, hf, LSubfr16k, _memSynHf, M16k - M, true);
        }

        Filt6k7k(hf, LSubfr16k);
        if (nbBits >= NbBits24k) Filt7k(hf, LSubfr16k);

        for (int i = 0; i < LSubfr16k; i++) synth16k[s16Off + i] = Add(synth16k[s16Off + i], hf[i]);
    }
}
