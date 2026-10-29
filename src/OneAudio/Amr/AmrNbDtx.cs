// Copyright (c) Recursive AI, LLC. MIT OR Apache-2.0.
namespace OneAudio.Amr;

// Comfort noise generation and discontinuous transmission handling of the AMR narrowband decoder:
// TS 26.092 (comfort noise), TS 26.093 (source controlled rate operation); TS 26.073 dtx_dec.c, b_cn_cod.c, a_refl.c.
internal sealed partial class AmrNbCore
{
    private const int Speech = 0, Dtx = 1, DtxMute = 2;
    private const int DtxHistSize = 8;
    private const int DtxHangConst = 7;
    private const int DtxElapsedFramesThresh = 24 + 7 - 1;
    private const int DtxMaxEmptyThresh = 50;
    private const int PnInitialSeed = 0x70816958;

    private short _sinceLastSid, _trueSidPeriodInv, _logEn, _oldLogEn;
    private int _lPnSeedRx;
    private readonly short[] _dtxLsp = new short[M];
    private readonly short[] _dtxLspOld = new short[M];
    private readonly short[] _lsfHist = new short[M * DtxHistSize];
    private short _lsfHistPtr;
    private readonly short[] _lsfHistMean = new short[M * DtxHistSize];
    private short _logPgMean;
    private readonly short[] _logEnHist = new short[DtxHistSize];
    private short _logEnHistPtr, _logEnAdjust, _dtxHangoverCount, _decAnaElapsedCount, _sidFrame, _validData,
        _dtxHangoverAdded, _dataUpdated;
    private int _dtxGlobalState;

    private readonly int[] _lLsf = new int[M];
    private readonly short[] _dLsf = new short[M];
    private readonly short[] _lspInt = new short[M];
    private readonly short[] _lsfInt = new short[M];
    private readonly short[] _lsfIntVariab = new short[M];
    private readonly short[] _lspIntVariab = new short[M];
    private readonly short[] _acoeff = new short[Mp1];
    private readonly short[] _acoeffVariab = new short[Mp1];
    private readonly short[] _refl = new short[M];
    private readonly short[] _aState = new short[M];
    private readonly short[] _bState = new short[M];
    private readonly short[] _ex = new short[LSubfr];

    private void DtxReset()
    {
        _sinceLastSid = 0;
        _trueSidPeriodInv = 1 << 13;
        _logEn = 3500;
        _oldLogEn = 3500;
        _lPnSeedRx = PnInitialSeed;
        Array.Copy(AmrNbTables.LspInit, _dtxLsp, M);
        Array.Copy(AmrNbTables.LspInit, _dtxLspOld, M);
        _lsfHistPtr = 0;
        _logPgMean = 0;
        _logEnHistPtr = 0;
        Array.Copy(AmrNbTables.MeanLsf5, _lsfHist, M);
        for (int i = 1; i < DtxHistSize; i++) Array.Copy(_lsfHist, 0, _lsfHist, M * i, M);
        Array.Clear(_lsfHistMean);
        for (int i = 0; i < DtxHistSize; i++) _logEnHist[i] = _logEn;
        _logEnAdjust = 0;
        _dtxHangoverCount = DtxHangConst;
        _decAnaElapsedCount = 32767;
        _sidFrame = 0;
        _validData = 0;
        _dtxHangoverAdded = 0;
        _dtxGlobalState = Dtx;
        _dataUpdated = 0;
    }

    private short Pseudonoise(ref int shiftReg, int noBits)
    {
        short noiseBits = 0;
        for (int i = 0; i < noBits; i++)
        {
            int sn = (shiftReg & 1) != 0 ? 1 : 0;
            if ((shiftReg & 0x10000000) != 0) sn ^= 1;
            noiseBits = Shl(noiseBits, 1);
            noiseBits = (short)(noiseBits + (shiftReg & 1)); // low bit is clear after the shift, so + is the C |
            shiftReg = LShr(shiftReg, 1);
            if ((sn & 1) != 0) shiftReg |= 0x40000000;
        }
        return noiseBits;
    }

    private void BuildCnCode(ref int seed, short[] cod)
    {
        for (int i = 0; i < LSubfr; i++) cod[i] = 0;
        for (int k = 0; k < 10; k++)
        {
            short i = Pseudonoise(ref seed, 2);
            i = Shr(ExtractL(LMult(i, 10)), 1);
            i = Add(i, k);
            short j = Pseudonoise(ref seed, 1);
            cod[i] = j > 0 ? (short)4096 : (short)-4096;
        }
    }

    /// <summary>build_CN_param: pseudo-random parameters substituted for lost or onset frames (TS 26.092 section 6).</summary>
    private void BuildCnParam(ref short seed, int nParam, short[] paramSizeTable, short[] parm)
    {
        seed = ExtractL(LAdd(LShr(LMult(seed, 31821), 1), 13849));
        int p = seed & 0x7F;
        short[] win = AmrNbTables.Window200x40;
        for (int i = 0; i < nParam; i++) parm[i] = (short)(win[p++] & ((1 << paramSizeTable[i]) - 1));
    }

    /// <summary>A_Refl: reflection coefficients from direct-form predictor coefficients (TS 26.073 a_refl.c).</summary>
    private void ARefl(short[] a, int aOff, short[] refl)
    {
        for (int i = 0; i < M; i++) _aState[i] = a[aOff + i];
        bool bad = false;
        for (int i = M - 1; i >= 0 && !bad; i--)
        {
            if (AbsS(_aState[i]) >= 4096)
            {
                bad = true;
                break;
            }
            refl[i] = Shl(_aState[i], 3);
            int lTemp = LMult(refl[i], refl[i]);
            int lAcc = LSub(Max32, lTemp);
            short normShift = NormL(lAcc);
            short scale = Sub(15, normShift);
            lAcc = LShl(lAcc, normShift);
            short normProd = Round(lAcc);
            short mult = DivS(16384, normProd);
            for (int j = 0; j < i; j++)
            {
                lAcc = LDepositH(_aState[j]);
                lAcc = LMsu(lAcc, refl[i], _aState[i - j - 1]);
                short temp = Round(lAcc);
                lTemp = LMult(mult, temp);
                lTemp = LShrR(lTemp, scale);
                if (LSub(LAbs(lTemp), 32767) > 0)
                {
                    bad = true;
                    break;
                }
                _bState[j] = ExtractL(lTemp);
            }
            if (bad) break;
            for (int j = 0; j < i; j++) _aState[j] = _bState[j];
        }
        if (bad)
        {
            for (int i = 0; i < M; i++) refl[i] = 0;
        }
    }

    private static readonly short[] LsfHistMeanScale = [20000, 20000, 20000, 20000, 20000, 18000, 16384, 8192, 0, 0];
    private static readonly short[] DtxLogEnAdjust = [-1023, -878, -732, -586, -440, -294, -148, 0, 0];

    /// <summary>dtx_dec: synthesizes comfort noise for the current 20 ms frame (TS 26.092 section 6).</summary>
    private void DtxDec(int newState, int mode, short[] parm, short[] synth, short[] aT)
    {
        if (_dtxHangoverAdded != 0 && _sidFrame != 0)
        {
            _logEnAdjust = DtxLogEnAdjust[mode];
            short ptr = Add(_lsfHistPtr, M);
            if (ptr == 80) ptr = 0;
            Array.Copy(_lsfHist, _lsfHistPtr, _lsfHist, ptr, M);
            ptr = Add(_logEnHistPtr, 1);
            if (ptr == DtxHistSize) ptr = 0;
            _logEnHist[ptr] = _logEnHist[_logEnHistPtr];

            _logEn = 0;
            for (int i = 0; i < M; i++) _lLsf[i] = 0;
            for (int i = 0; i < DtxHistSize; i++)
            {
                _logEn = Add(_logEn, Shr(_logEnHist[i], 3));
                for (int j = 0; j < M; j++) _lLsf[j] = LAdd(_lLsf[j], LDepositL(_lsfHist[i * M + j]));
            }
            for (int j = 0; j < M; j++) _dLsf[j] = ExtractL(LShr(_lLsf[j], 3));
            LsfToLsp(_dLsf, _dtxLsp, M);
            _logEn = Sub(_logEn, _logEnAdjust);

            Array.Copy(_lsfHist, _lsfHistMean, 80);
            for (int i = 0; i < M; i++)
            {
                int lLsfMean = 0;
                for (int j = 0; j < 8; j++) lLsfMean = LAdd(lLsfMean, LDepositL(_lsfHistMean[i + j * M]));
                short lsfMean = ExtractL(LShr(lLsfMean, 3));
                for (int j = 0; j < 8; j++)
                {
                    int k = i + j * M;
                    _lsfHistMean[k] = Sub(_lsfHistMean[k], lsfMean);
                    _lsfHistMean[k] = Mult(_lsfHistMean[k], LsfHistMeanScale[i]);
                    bool negative = _lsfHistMean[k] < 0;
                    _lsfHistMean[k] = AbsS(_lsfHistMean[k]);
                    if (_lsfHistMean[k] > 655) _lsfHistMean[k] = Add(655, Shr(Sub(_lsfHistMean[k], 655), 2));
                    if (_lsfHistMean[k] > 1310) _lsfHistMean[k] = 1310;
                    if (negative) _lsfHistMean[k] = (short)-_lsfHistMean[k];
                }
            }
        }

        if (_sidFrame != 0)
        {
            Array.Copy(_dtxLsp, _dtxLspOld, M);
            _oldLogEn = _logEn;
            if (_validData != 0)
            {
                short tmpIntLength = _sinceLastSid;
                _sinceLastSid = 0;
                if (tmpIntLength > 32) tmpIntLength = 32;
                if (tmpIntLength >= 2) _trueSidPeriodInv = DivS(1 << 10, Shl(tmpIntLength, 10));
                else _trueSidPeriodInv = 1 << 14;

                // Init_D_plsf_3
                Array.Copy(AmrNbTables.PastRqInit, parm[0] * M, _pastRQ, 0, M);
                DPlsf3(MrDtx, 0, parm, 1, _dtxLsp);
                Array.Clear(_pastRQ);

                short logEnIndex = parm[4];
                _logEn = Shl(logEnIndex, 11 - 2);
                _logEn = Sub(_logEn, 2560 * 2);
                if (logEnIndex == 0) _logEn = Min16;

                if (_dataUpdated == 0 || _dtxGlobalState == Speech)
                {
                    Array.Copy(_dtxLsp, _dtxLspOld, M);
                    _oldLogEn = _logEn;
                }
            }

            short maPredInit = Sub(Shr(_logEn, 1), 9000);
            if (maPredInit > 0) maPredInit = 0;
            if (maPredInit < -14436) maPredInit = -14436;
            for (int i = 0; i < 4; i++) _pastQuaEn[i] = maPredInit;
            maPredInit = Mult(5443, maPredInit);
            for (int i = 0; i < 4; i++) _pastQuaEnMr122[i] = maPredInit;
        }

        _logEnAdjust = Add(Mult(_logEnAdjust, 29491), Shr(Mult(Shl(DtxLogEnAdjust[mode], 5), 3277), 5));

        short intFac = Shl(Add(1, _sinceLastSid), 10);
        intFac = Mult(intFac, _trueSidPeriodInv);
        if (intFac > 1024) intFac = 1024;
        intFac = Shl(intFac, 4);

        int lLogEnInt = LMult(intFac, _logEn);
        for (int i = 0; i < M; i++) _lspInt[i] = Mult(intFac, _dtxLsp[i]);
        intFac = Sub(16384, intFac);
        lLogEnInt = LMac(lLogEnInt, intFac, _oldLogEn);
        for (int i = 0; i < M; i++)
        {
            _lspInt[i] = Add(_lspInt[i], Mult(intFac, _dtxLspOld[i]));
            _lspInt[i] = Shl(_lspInt[i], 1);
        }

        short lsfVariabFactor = Sub(_logPgMean, 2457);
        lsfVariabFactor = Sub(4096, Mult(lsfVariabFactor, 9830));
        if (lsfVariabFactor > 4096) lsfVariabFactor = 4096;
        if (lsfVariabFactor < 0) lsfVariabFactor = 0;
        lsfVariabFactor = Shl(lsfVariabFactor, 3);

        short lsfVariabIndex = Pseudonoise(ref _lPnSeedRx, 3);

        LspToLsf(_lspInt, _lsfInt, M);
        Array.Copy(_lsfInt, _lsfIntVariab, M);
        for (int i = 0; i < M; i++)
            _lsfIntVariab[i] = Add(_lsfIntVariab[i], Mult(lsfVariabFactor, _lsfHistMean[i + lsfVariabIndex * M]));

        ReorderLsf(_lsfInt, LsfGap, M);
        ReorderLsf(_lsfIntVariab, LsfGap, M);
        Array.Copy(_lsfInt, _pastLsfQ, M);

        LsfToLsp(_lsfInt, _lspInt, M);
        LsfToLsp(_lsfIntVariab, _lspIntVariab, M);
        LspAz(_lspInt, 0, _acoeff, 0);
        LspAz(_lspIntVariab, 0, _acoeffVariab, 0);

        for (int k = 0; k < 4; k++) Array.Copy(_acoeff, 0, aT, k * Mp1, Mp1);

        ARefl(_acoeff, 1, _refl);
        short predErr = Max16;
        for (int i = 0; i < M; i++) predErr = Mult(predErr, Sub(Max16, Mult(_refl[i], _refl[i])));
        Log2(LDepositL(predErr), out short logPgE, out short logPgM);
        short logPg = Shl(Sub(logPgE, 15), 12);
        logPg = Shr(Sub(0, Add(logPg, Shr(logPgM, 15 - 12))), 1);
        _logPgMean = Add(Mult(29491, _logPgMean), Mult(3277, logPg));

        lLogEnInt = LShr(lLogEnInt, 10);
        lLogEnInt = LAdd(lLogEnInt, 4 * 65536);
        lLogEnInt = LSub(lLogEnInt, LShl(LDepositL(logPg), 4));
        lLogEnInt = LAdd(lLogEnInt, LShl(LDepositL(_logEnAdjust), 5));
        short logEnIntE = ExtractH(lLogEnInt);
        short logEnIntM = ExtractL(LShr(LSub(lLogEnInt, LDepositH(logEnIntE)), 1));
        short level = ExtractL(Pow2(logEnIntE, logEnIntM));

        for (int i = 0; i < 4; i++)
        {
            BuildCnCode(ref _lPnSeedRx, _ex);
            for (int j = 0; j < LSubfr; j++) _ex[j] = Mult(level, _ex[j]);
            SynFilt(_acoeffVariab, 0, _ex, 0, synth, i * LSubfr, LSubfr, _memSyn, 0, true);
        }

        _hangVar = 20;
        _hangCount = 0;

        if (newState == DtxMute)
        {
            short tmpIntLength = _sinceLastSid;
            if (tmpIntLength > 32) tmpIntLength = 32;
            if (tmpIntLength <= 0) tmpIntLength = 8;
            _trueSidPeriodInv = DivS(1 << 10, Shl(tmpIntLength, 10));
            _sinceLastSid = 0;
            Array.Copy(_dtxLsp, _dtxLspOld, M);
            _oldLogEn = _logEn;
            _logEn = Sub(_logEn, 256);
        }

        if (_sidFrame != 0 && (_validData != 0 || (_validData == 0 && _dtxHangoverAdded != 0)))
        {
            _sinceLastSid = 0;
            _dataUpdated = 1;
        }
    }

    private void DtxDecActivityUpdate(short[] lsf, short[] frame)
    {
        _lsfHistPtr = Add(_lsfHistPtr, M);
        if (_lsfHistPtr == 80) _lsfHistPtr = 0;
        Array.Copy(lsf, 0, _lsfHist, _lsfHistPtr, M);

        int lFrameEn = 0;
        for (int i = 0; i < LFrame; i++) lFrameEn = LMac(lFrameEn, frame[i], frame[i]);
        Log2(lFrameEn, out short logEnE, out short logEnM);
        short logEn = Shl(logEnE, 10);
        logEn = Add(logEn, Shr(logEnM, 15 - 10));
        logEn = Sub(logEn, 7497 + 1024);

        _logEnHistPtr = Add(_logEnHistPtr, 1);
        if (_logEnHistPtr == DtxHistSize) _logEnHistPtr = 0;
        _logEnHist[_logEnHistPtr] = logEn;
    }

    /// <summary>rx_dtx_handler: the receive-side DTX state machine of TS 26.093 section 6.</summary>
    private int RxDtxHandler(int frameType)
    {
        int newState;
        int encState;

        if (frameType == RxSidFirst || frameType == RxSidUpdate || frameType == RxSidBad ||
            ((_dtxGlobalState == Dtx || _dtxGlobalState == DtxMute) &&
             (frameType == RxNoData || frameType == RxSpeechBad || frameType == RxOnset)))
        {
            newState = Dtx;
            if (_dtxGlobalState == DtxMute &&
                (frameType == RxSidBad || frameType == RxSidFirst || frameType == RxOnset || frameType == RxNoData))
            {
                newState = DtxMute;
            }
            _sinceLastSid = Add(_sinceLastSid, 1);
            if (frameType != RxSidUpdate && _sinceLastSid > DtxMaxEmptyThresh) newState = DtxMute;
        }
        else
        {
            newState = Speech;
            _sinceLastSid = 0;
        }

        if (_dataUpdated == 0 && frameType == RxSidUpdate) _decAnaElapsedCount = 0;
        _decAnaElapsedCount = Add(_decAnaElapsedCount, 1);
        _dtxHangoverAdded = 0;

        if (frameType == RxSidFirst || frameType == RxSidUpdate || frameType == RxSidBad ||
            frameType == RxOnset || frameType == RxNoData)
        {
            encState = Dtx;
            if (frameType == RxNoData && newState == Speech) encState = Speech;
        }
        else
        {
            encState = Speech;
        }

        if (encState == Speech)
        {
            _dtxHangoverCount = DtxHangConst;
        }
        else
        {
            if (_decAnaElapsedCount > DtxElapsedFramesThresh)
            {
                _dtxHangoverAdded = 1;
                _decAnaElapsedCount = 0;
                _dtxHangoverCount = 0;
            }
            else if (_dtxHangoverCount == 0)
            {
                _decAnaElapsedCount = 0;
            }
            else
            {
                _dtxHangoverCount = Sub(_dtxHangoverCount, 1);
            }
        }

        if (newState != Speech)
        {
            _sidFrame = 0;
            _validData = 0;
            if (frameType == RxSidFirst)
            {
                _sidFrame = 1;
            }
            else if (frameType == RxSidUpdate)
            {
                _sidFrame = 1;
                _validData = 1;
            }
            else if (frameType == RxSidBad)
            {
                _sidFrame = 1;
                _dtxHangoverAdded = 0;
            }
        }
        return newState;
    }
}
