// Copyright (c) Recursive AI, LLC. MIT OR Apache-2.0.
namespace OneAudio.Amr;

// Receive-side discontinuous transmission and comfort noise generation of the AMR wideband decoder
// (TS 26.192 and TS 26.173 dtx.c): the state machine that classifies frames, the comfort noise parameter
// interpolation and the noise excitation.
internal sealed partial class AmrWbCore
{
    private const int DtxHistSize = 8;
    private const int DtxMaxEmptyThresh = 50;
    private const int DtxElapsedFramesThresh = 24 + 7 - 1;
    private const int DtxHangConst = 7;

    private sealed class DtxState
    {
        public short SinceLastSid, TrueSidPeriodInv, LogEn, OldLogEn;
        public readonly short[] Isf = new short[M];
        public readonly short[] IsfOld = new short[M];
        public short CngSeed;
        public readonly short[] IsfHist = new short[M * DtxHistSize];
        public readonly short[] LogEnHist = new short[DtxHistSize];
        public short HistPtr, DtxHangoverCount, DecAnaElapsedCount, SidFrame, ValidData, DtxHangoverAdded,
            DtxGlobalState, DataUpdated, DitherSeed, CnDith, DtxVadHist;
    }

    private readonly DtxState _dtxSt;

    /// <summary>dtx_dec_reset (TS 26.173 dtx.c).</summary>
    private void DtxDecReset()
    {
        DtxState st = _dtxSt;
        st.SinceLastSid = 0;
        st.TrueSidPeriodInv = 1 << 13;
        st.LogEn = 3500;
        st.OldLogEn = 3500;
        st.CngSeed = RandomInitSeed;
        st.HistPtr = 0;
        Array.Copy(AmrWbTables.IsfInit, st.Isf, M);
        Array.Copy(AmrWbTables.IsfInit, st.IsfOld, M);
        for (int i = 0; i < DtxHistSize; i++)
        {
            Array.Copy(AmrWbTables.IsfInit, 0, st.IsfHist, i * M, M);
            st.LogEnHist[i] = 3500 >> 3;
        }
        st.DtxHangoverCount = DtxHangConst;
        st.DecAnaElapsedCount = 32767;
        st.SidFrame = 0;
        st.ValidData = 0;
        st.DtxHangoverAdded = 0;
        st.DtxGlobalState = Speech;
        st.DataUpdated = 0;
        st.DitherSeed = RandomInitSeed;
        st.CnDith = 0;
        st.DtxVadHist = 0;
    }

    /// <summary>rx_dtx_handler: classifies the received frame and returns the new DTX state (TS 26.192 section 5).</summary>
    private int RxDtxHandler(int frameType)
    {
        DtxState st = _dtxSt;
        int newState;
        if (frameType == RxSidFirst || frameType == RxSidUpdate || frameType == RxSidBad
            || ((st.DtxGlobalState == Dtx || st.DtxGlobalState == DtxMute)
                && (frameType == RxNoData || frameType == RxSpeechBad || frameType == RxSpeechLost)))
        {
            newState = Dtx;
            if (st.DtxGlobalState == DtxMute
                && (frameType == RxSidBad || frameType == RxSidFirst || frameType == RxSpeechLost || frameType == RxNoData))
            {
                newState = DtxMute;
            }
            st.SinceLastSid = Add(st.SinceLastSid, 1);
            if (frameType != RxSidUpdate && st.SinceLastSid > DtxMaxEmptyThresh) newState = DtxMute;
        }
        else
        {
            newState = Speech;
            st.SinceLastSid = 0;
        }

        if (st.DataUpdated == 0 && frameType == RxSidUpdate) st.DecAnaElapsedCount = 0;
        st.DecAnaElapsedCount = Add(st.DecAnaElapsedCount, 1);
        st.DtxHangoverAdded = 0;

        int encState;
        if (frameType == RxSidFirst || frameType == RxSidUpdate || frameType == RxSidBad
            || (frameType == RxNoData && (st.DtxGlobalState != Speech || st.DtxVadHist >= DtxHangConst)))
        {
            encState = Dtx;
        }
        else
        {
            encState = Speech;
        }

        if (encState == Speech)
        {
            st.DtxHangoverCount = DtxHangConst;
        }
        else if (st.DecAnaElapsedCount > DtxElapsedFramesThresh)
        {
            st.DtxHangoverAdded = 1;
            st.DecAnaElapsedCount = 0;
            st.DtxHangoverCount = 0;
        }
        else if (st.DtxHangoverCount == 0)
        {
            st.DecAnaElapsedCount = 0;
        }
        else
        {
            st.DtxHangoverCount = Sub(st.DtxHangoverCount, 1);
        }

        if (newState != Speech)
        {
            st.SidFrame = 0;
            st.ValidData = 0;
            if (frameType == RxSidFirst)
            {
                st.SidFrame = 1;
            }
            else if (frameType == RxSidUpdate)
            {
                st.SidFrame = 1;
                st.ValidData = 1;
            }
            else if (frameType == RxSidBad)
            {
                st.SidFrame = 1;
                st.DtxHangoverAdded = 0;
            }
        }
        return newState;
    }

    /// <summary>dtx_dec_activity_update: records ISFs and frame energy of an active speech frame for later comfort noise.</summary>
    private void DtxDecActivityUpdate(short[] isf, short[] exc, int excOff)
    {
        DtxState st = _dtxSt;
        st.HistPtr = Add(st.HistPtr, 1);
        if (st.HistPtr == DtxHistSize) st.HistPtr = 0;
        Array.Copy(isf, 0, st.IsfHist, st.HistPtr * M, M);
        int lFrameEn = 0;
        for (int i = 0; i < LFrame; i++) lFrameEn = LMac(lFrameEn, exc[excOff + i], exc[excOff + i]);
        lFrameEn = LShr(lFrameEn, 1);
        Log2(lFrameEn, out short logEnE, out short logEnM);
        short logEn = Shl(logEnE, 7);
        logEn = Add(logEn, Shr(logEnM, 15 - 7));
        logEn = Sub(logEn, 1024);
        st.LogEnHist[st.HistPtr] = logEn;
    }

    /// <summary>dtx_dec: comfort noise parameters for this frame (interpolated ISFs) and the noise excitation (TS 26.192 section 6).</summary>
    private void DtxDec(short[] exc2, int newState, short[] isf)
    {
        DtxState st = _dtxSt;
        Span<int> lIsf = stackalloc int[M];

        if (st.DtxHangoverAdded != 0 && st.SidFrame != 0)
        {
            int ptr = st.HistPtr + 1;
            if (ptr == DtxHistSize) ptr = 0;
            Array.Copy(st.IsfHist, st.HistPtr * M, st.IsfHist, ptr * M, M);
            st.LogEnHist[ptr] = st.LogEnHist[st.HistPtr];
            st.LogEn = 0;
            lIsf.Clear();
            for (int i = 0; i < DtxHistSize; i++)
            {
                st.LogEn = Add(st.LogEn, st.LogEnHist[i]);
                for (int j = 0; j < M; j++) lIsf[j] = LAdd(lIsf[j], st.IsfHist[i * M + j]);
            }
            st.LogEn = Shr(st.LogEn, 1);
            st.LogEn = Add(st.LogEn, 1024);
            if (st.LogEn < 0) st.LogEn = 0;
            for (int j = 0; j < M; j++) st.Isf[j] = ExtractL(LShr(lIsf[j], 3));
        }

        if (st.SidFrame != 0)
        {
            Array.Copy(st.Isf, st.IsfOld, M);
            st.OldLogEn = st.LogEn;
            if (st.ValidData != 0)
            {
                short tmpIntLength = st.SinceLastSid;
                if (tmpIntLength > 32) tmpIntLength = 32;
                if (tmpIntLength >= 2) st.TrueSidPeriodInv = DivS(1 << 10, Shl(tmpIntLength, 10));
                else st.TrueSidPeriodInv = 1 << 14;

                short[] ind = _ind;
                ind[0] = SerialParm(6);
                ind[1] = SerialParm(6);
                ind[2] = SerialParm(6);
                ind[3] = SerialParm(5);
                ind[4] = SerialParm(5);
                DisfNs(ind, st.Isf);
                short logEnIndex = SerialParm(6);
                st.CnDith = SerialParm(1);
                st.LogEn = Shl(logEnIndex, 15 - 6);
                st.LogEn = Mult(st.LogEn, 12483);
                if (st.DataUpdated == 0 || st.DtxGlobalState == Speech)
                {
                    Array.Copy(st.Isf, st.IsfOld, M);
                    st.OldLogEn = st.LogEn;
                }
            }
        }

        if (st.SidFrame != 0 && st.ValidData != 0) st.SinceLastSid = 0;

        short intFac = Shl(st.SinceLastSid, 10);
        intFac = Mult(intFac, st.TrueSidPeriodInv);
        if (intFac > 1024) intFac = 1024;
        intFac = Shl(intFac, 4);
        int lLogEnInt = LMult(intFac, st.LogEn);
        for (int i = 0; i < M; i++) isf[i] = Mult(intFac, st.Isf[i]);
        intFac = Sub(16384, intFac);
        lLogEnInt = LMac(lLogEnInt, intFac, st.OldLogEn);
        for (int i = 0; i < M; i++)
        {
            isf[i] = Add(isf[i], Mult(intFac, st.IsfOld[i]));
            isf[i] = Shl(isf[i], 1);
        }

        if (st.CnDith != 0) CnDithering(isf, ref lLogEnInt, ref st.DitherSeed);

        lLogEnInt = LShr(lLogEnInt, 9);
        short logEnIntE = ExtractH(lLogEnInt);
        short logEnIntM = ExtractL(LShr(LSub(lLogEnInt, LDepositH(logEnIntE)), 1));
        logEnIntE = Add(logEnIntE, 16 - 1);
        int level32 = Pow2(logEnIntE, logEnIntM);
        short exp0 = NormL(level32);
        level32 = LShl(level32, exp0);
        exp0 = Sub(15, exp0);
        short level = ExtractH(level32);

        for (int i = 0; i < LFrame; i++) exc2[i] = Shr(Random(ref st.CngSeed), 4);
        int ener32 = DotProduct12(exc2, 0, exc2, 0, LFrame, out short exp);
        IsqrtN(ref ener32, ref exp);
        short gain = ExtractH(ener32);
        gain = Mult(level, gain);
        exp = Add(exp0, exp);
        exp = Add(exp, 4);
        for (int i = 0; i < LFrame; i++)
        {
            short tmp = Mult(exc2[i], gain);
            exc2[i] = Shl(tmp, exp);
        }

        if (newState == DtxMute)
        {
            short tmpIntLength = st.SinceLastSid;
            if (tmpIntLength > 32) tmpIntLength = 32;
            if (tmpIntLength <= 0) tmpIntLength = 8;
            st.TrueSidPeriodInv = DivS(1 << 10, Shl(tmpIntLength, 10));
            st.SinceLastSid = 0;
            st.OldLogEn = st.LogEn;
            st.LogEn = Sub(st.LogEn, 64);
        }

        if (st.SidFrame != 0 && (st.ValidData != 0 || st.DtxHangoverAdded != 0))
        {
            st.SinceLastSid = 0;
            st.DataUpdated = 1;
        }
    }

    /// <summary>CN_dithering (TS 26.192 section 6.2): random perturbation of the comfort noise ISFs and level.</summary>
    private void CnDithering(short[] isf, ref int lLogEnInt, ref short ditherSeed)
    {
        short randDith = Shr(Random(ref ditherSeed), 1);
        short randDith2 = Shr(Random(ref ditherSeed), 1);
        randDith = Add(randDith, randDith2);
        lLogEnInt = LAdd(lLogEnInt, LMult(randDith, 75));
        if (lLogEnInt < 0) lLogEnInt = 0;

        short ditherFac = 256;
        randDith = Shr(Random(ref ditherSeed), 1);
        randDith2 = Shr(Random(ref ditherSeed), 1);
        randDith = Add(randDith, randDith2);
        short temp = Add(isf[0], MultR(randDith, ditherFac));
        isf[0] = temp < 128 ? (short)128 : temp;
        for (int i = 1; i < M - 1; i++)
        {
            ditherFac = Add(ditherFac, 2);
            randDith = Shr(Random(ref ditherSeed), 1);
            randDith2 = Shr(Random(ref ditherSeed), 1);
            randDith = Add(randDith, randDith2);
            temp = Add(isf[i], MultR(randDith, ditherFac));
            short temp1 = Sub(temp, isf[i - 1]);
            isf[i] = temp1 < 448 ? Add(isf[i - 1], 448) : temp;
        }
        if (isf[M - 2] > 16384) isf[M - 2] = 16384;
    }
}
