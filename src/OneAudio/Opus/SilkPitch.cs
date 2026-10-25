using static OneAudio.Opus.SilkMath;
using static OneAudio.Opus.SilkEncMath;

namespace OneAudio.Opus;

/// <summary>Three-stage pitch analysis of the SILK encoder (floating-point reference path, RFC 6716 reference encoder).</summary>
internal sealed class SilkPitchAnalysis
{
    private const int MaxNbSubfr = 4;
    private const int PeSubfrLengthMs = 5;
    private const int PeLtpMemLengthMs = 4 * PeSubfrLengthMs;
    private const int PeMaxLagMs = 18;
    private const int PeMinLagMs = 2;
    private const int PeMaxLag = PeMaxLagMs * 16;
    private const int PeNbStage3Lags = 5;
    private const int PeNbCbksStage2 = 3;
    private const int PeNbCbksStage2Ext = 11;
    private const int PeNbCbksStage3Max = 34;
    private const int PeNbCbksStage3Mid = 24;
    private const int PeNbCbksStage3Min = 16;
    private const int PeNbCbksStage3_10Ms = 12;
    private const int PeNbCbksStage2_10Ms = 3;
    private const float PeShortlagBias = 0.2f;
    private const float PePrevlagBias = 0.2f;
    private const float PeFlatcontourBias = 0.05f;
    private const float Eps = 1.192092896e-07f;
    private const int CStride = (PeMaxLag >> 1) + 5;

    private static readonly sbyte[] LagRangeStage3 =
    [
        -5, 8, -1, 6, -1, 6, -4, 10,
        -6, 10, -2, 6, -1, 6, -5, 10,
        -9, 12, -3, 7, -2, 7, -7, 13,
    ];

    private static readonly sbyte[] LagRangeStage3_10Ms = [-3, 7, -2, 7];
    private static readonly int[] NbCbkSearchsStage3 = [PeNbCbksStage3Min, PeNbCbksStage3Mid, PeNbCbksStage3Max];

    private readonly float[] _frame8kHz = new float[40 * 8];
    private readonly float[] _frame4kHz = new float[40 * 4];
    private readonly short[] _frame8Fix = new short[40 * 8];
    private readonly short[] _frame4Fix = new short[40 * 4];
    private readonly short[] _frameFix = new short[40 * 16];
    private readonly float[] _c = new float[MaxNbSubfr * CStride];
    private readonly float[] _cc = new float[PeNbCbksStage2Ext];
    private readonly int[] _dSrch = new int[24];
    private readonly short[] _dComp = new short[CStride];
    private readonly float[] _energiesSt3 = new float[MaxNbSubfr * PeNbCbksStage3Max * PeNbStage3Lags];
    private readonly float[] _crossCorrSt3 = new float[MaxNbSubfr * PeNbCbksStage3Max * PeNbStage3Lags];
    private readonly int[] _filtState = new int[6];
    private readonly int[] _buf = new int[480 + 4];

    private static void Down2(Span<int> s, Span<short> output, ReadOnlySpan<short> input, int inLen)
    {
        const int down20 = 9872;
        const int down21 = 39809 - 65536;
        int len2 = inLen >> 1;
        for (int k = 0; k < len2; k++)
        {
            int in32 = input[2 * k] << 10;
            int y = in32 - s[0];
            int x = SMlaWB(y, y, down21);
            int out32 = s[0] + x;
            s[0] = in32 + x;
            in32 = input[2 * k + 1] << 10;
            y = in32 - s[1];
            x = SMulWB(y, down20);
            out32 += s[1];
            out32 += x;
            s[1] = in32 + x;
            output[k] = (short)Sat16(RShiftRound(out32, 11));
        }
    }

    private static readonly short[] Coefs2To3Lq = [-2797, -6507, 4697, 10739, 1567, 8276];

    private void Down2To3(Span<int> s, Span<short> output, ReadOnlySpan<short> input, int inLen)
    {
        const int orderFir = 4;
        var buf = _buf;
        for (int i = 0; i < orderFir; i++) buf[i] = s[i];
        int outPos = 0, inPos = 0;
        int nSamplesIn;
        var c = Coefs2To3Lq;
        while (true)
        {
            nSamplesIn = Math.Min(inLen, 480);
            // silk_resampler_private_AR2 on the state words after the FIR history
            for (int k = 0; k < nSamplesIn; k++)
            {
                int out32 = s[orderFir] + (input[inPos + k] << 8);
                buf[orderFir + k] = out32;
                out32 <<= 2;
                s[orderFir] = SMlaWB(s[orderFir + 1], out32, c[0]);
                s[orderFir + 1] = SMulWB(out32, c[1]);
            }
            int bp = 0;
            int counter = nSamplesIn;
            while (counter > 2)
            {
                int res = SMulWB(buf[bp], c[2]);
                res = SMlaWB(res, buf[bp + 1], c[3]);
                res = SMlaWB(res, buf[bp + 2], c[5]);
                res = SMlaWB(res, buf[bp + 3], c[4]);
                output[outPos++] = (short)Sat16(RShiftRound(res, 6));
                res = SMulWB(buf[bp + 1], c[4]);
                res = SMlaWB(res, buf[bp + 2], c[5]);
                res = SMlaWB(res, buf[bp + 3], c[3]);
                res = SMlaWB(res, buf[bp + 4], c[2]);
                output[outPos++] = (short)Sat16(RShiftRound(res, 6));
                bp += 3;
                counter -= 3;
            }
            inPos += nSamplesIn;
            inLen -= nSamplesIn;
            if (inLen > 0) Array.Copy(buf, nSamplesIn, buf, 0, orderFir);
            else break;
        }
        for (int i = 0; i < orderFir; i++) s[i] = buf[nSamplesIn + i];
    }

    private static void Float2Short(Span<short> output, ReadOnlySpan<float> input, int n)
    {
        for (int i = 0; i < n; i++) output[i] = (short)Sat16(SilkFlp.Float2Int(input[i]));
    }

    private static void Short2Float(Span<float> output, ReadOnlySpan<short> input, int n)
    {
        for (int i = 0; i < n; i++) output[i] = input[i];
    }

    /// <summary>
    /// silk_pitch_analysis_core_FLP. Returns true when the frame is unvoiced (no pitch found). On success fills
    /// <paramref name="pitchOut"/>, the lag index and the contour index, and updates <paramref name="ltpCorr"/>.
    /// </summary>
    public bool Analyze(ReadOnlySpan<float> frame, Span<int> pitchOut, out short lagIndex, out sbyte contourIndex, ref float ltpCorr,
        int prevLag, float searchThres1, float searchThres2, int fsKHz, int complexity, int nbSubfr)
    {
        var frame8kHz = _frame8kHz;
        var frame4kHz = _frame4kHz;
        var frame8Fix = _frame8Fix;
        var frame4Fix = _frame4Fix;
        var c = _c;
        var cc = _cc;
        var dSrch = _dSrch;
        var dComp = _dComp;
        var filtState = _filtState;

        int frameLength = (PeLtpMemLengthMs + nbSubfr * PeSubfrLengthMs) * fsKHz;
        int frameLength4kHz = (PeLtpMemLengthMs + nbSubfr * PeSubfrLengthMs) * 4;
        int frameLength8kHz = (PeLtpMemLengthMs + nbSubfr * PeSubfrLengthMs) * 8;
        int sfLength = PeSubfrLengthMs * fsKHz;
        int sfLength4kHz = PeSubfrLengthMs * 4;
        int sfLength8kHz = PeSubfrLengthMs * 8;
        int minLag = PeMinLagMs * fsKHz;
        int minLag4kHz = PeMinLagMs * 4;
        int minLag8kHz = PeMinLagMs * 8;
        int maxLag = PeMaxLagMs * fsKHz - 1;
        int maxLag4kHz = PeMaxLagMs * 4;
        int maxLag8kHz = PeMaxLagMs * 8 - 1;

        lagIndex = 0;
        contourIndex = 0;
        Array.Clear(c, 0, nbSubfr * CStride);

        if (fsKHz == 16)
        {
            Float2Short(_frameFix, frame, frameLength);
            Array.Clear(filtState, 0, 2);
            Down2(filtState, frame8Fix, _frameFix, frameLength);
            Short2Float(frame8kHz, frame8Fix, frameLength8kHz);
        }
        else if (fsKHz == 12)
        {
            Float2Short(_frameFix, frame, frameLength);
            Array.Clear(filtState, 0, 6);
            Down2To3(filtState, frame8Fix, _frameFix, frameLength);
            Short2Float(frame8kHz, frame8Fix, frameLength8kHz);
        }
        else
        {
            Float2Short(frame8Fix, frame, frameLength8kHz);
        }

        Array.Clear(filtState, 0, 2);
        Down2(filtState, frame4Fix, frame8Fix, frameLength8kHz);
        Short2Float(frame4kHz, frame4Fix, frameLength4kHz);

        for (int i = frameLength4kHz - 1; i > 0; i--) frame4kHz[i] += frame4kHz[i - 1];

        // Stage 1: coarse search at 4 kHz
        int target = sfLength4kHz << 2;
        for (int k = 0; k < nbSubfr >> 1; k++)
        {
            int basis = target - minLag4kHz;
            double crossCorr = SilkFlp.InnerProduct(frame4kHz.AsSpan(target), frame4kHz.AsSpan(basis), sfLength8kHz);
            double normalizer = SilkFlp.Energy(frame4kHz.AsSpan(basis), sfLength8kHz) + sfLength8kHz * 4000.0f;
            c[minLag4kHz] += (float)(crossCorr / Math.Sqrt(normalizer));
            for (int d = minLag4kHz + 1; d <= maxLag4kHz; d++)
            {
                basis--;
                crossCorr = SilkFlp.InnerProduct(frame4kHz.AsSpan(target), frame4kHz.AsSpan(basis), sfLength8kHz);
                normalizer += frame4kHz[basis] * (double)frame4kHz[basis] -
                    frame4kHz[basis + sfLength8kHz] * (double)frame4kHz[basis + sfLength8kHz];
                c[d] += (float)(crossCorr / Math.Sqrt(normalizer));
            }
            target += sfLength8kHz;
        }

        for (int i = maxLag4kHz; i >= minLag4kHz; i--) c[i] -= c[i] * i / 4096.0f;

        int lengthDSrch = 4 + 2 * complexity;
        SilkFlp.InsertionSortDecreasing(c.AsSpan(minLag4kHz), dSrch, maxLag4kHz - minLag4kHz + 1, lengthDSrch);

        float cmax = c[minLag4kHz];
        target = sfLength4kHz * nbSubfr;
        double energy = 1000.0f;
        for (int i = 0; i < sfLength4kHz << 2; i++) energy += frame4kHz[target + i] * (double)frame4kHz[target + i];
        float threshold = cmax * cmax;

        if (energy / 16.0f > threshold)
        {
            pitchOut.Slice(0, nbSubfr).Clear();
            ltpCorr = 0.0f;
            lagIndex = 0;
            contourIndex = 0;
            return true;
        }

        threshold = searchThres1 * cmax;
        for (int i = 0; i < lengthDSrch; i++)
        {
            if (c[minLag4kHz + i] > threshold)
            {
                dSrch[i] = (dSrch[i] + minLag4kHz) << 1;
            }
            else
            {
                lengthDSrch = i;
                break;
            }
        }

        for (int i = minLag8kHz - 5; i < maxLag8kHz + 5; i++) dComp[i] = 0;
        for (int i = 0; i < lengthDSrch; i++) dComp[dSrch[i]] = 1;
        for (int i = maxLag8kHz + 3; i >= minLag8kHz; i--) dComp[i] += (short)(dComp[i - 1] + dComp[i - 2]);

        lengthDSrch = 0;
        for (int i = minLag8kHz; i < maxLag8kHz + 1; i++)
        {
            if (dComp[i + 1] > 0)
            {
                dSrch[lengthDSrch] = i;
                lengthDSrch++;
            }
        }

        for (int i = maxLag8kHz + 3; i >= minLag8kHz; i--) dComp[i] += (short)(dComp[i - 1] + dComp[i - 2] + dComp[i - 3]);

        int lengthDComp = 0;
        for (int i = minLag8kHz; i < maxLag8kHz + 4; i++)
        {
            if (dComp[i] > 0)
            {
                dComp[lengthDComp] = (short)(i - 2);
                lengthDComp++;
            }
        }

        // Stage 2: refine at 8 kHz
        Array.Clear(c);
        ReadOnlySpan<float> tgtBase = fsKHz == 8 ? frame : frame8kHz;
        int tp = PeLtpMemLengthMs * 8;
        for (int k = 0; k < nbSubfr; k++)
        {
            double energyTmp = SilkFlp.Energy(tgtBase.Slice(tp), sfLength8kHz);
            for (int j = 0; j < lengthDComp; j++)
            {
                int d = dComp[j];
                int bp = tp - d;
                double crossCorr = SilkFlp.InnerProduct(tgtBase.Slice(bp), tgtBase.Slice(tp), sfLength8kHz);
                energy = SilkFlp.Energy(tgtBase.Slice(bp), sfLength8kHz);
                if (crossCorr > 0.0f)
                    c[k * CStride + d] = (float)(crossCorr * crossCorr / (energy * energyTmp + Eps));
                else
                    c[k * CStride + d] = 0.0f;
            }
            tp += sfLength8kHz;
        }

        float ccmax = 0.0f;
        float ccmaxB = -1000.0f;
        int cbimax = 0;
        int lag = -1;
        float prevLagLog2;
        if (prevLag > 0)
        {
            if (fsKHz == 12) prevLag = (prevLag << 1) / 3;
            else if (fsKHz == 16) prevLag >>= 1;
            prevLagLog2 = SilkFlp.Log2((float)prevLag);
        }
        else prevLagLog2 = 0;

        int cbkSize, nbCbkSearch;
        sbyte[] lagCb;
        if (nbSubfr == MaxNbSubfr)
        {
            cbkSize = PeNbCbksStage2Ext;
            lagCb = SilkTables.CBLagsStage2;
            nbCbkSearch = fsKHz == 8 && complexity > 0 ? PeNbCbksStage2Ext : PeNbCbksStage2;
        }
        else
        {
            cbkSize = PeNbCbksStage2_10Ms;
            lagCb = SilkTables.CBLagsStage210Ms;
            nbCbkSearch = PeNbCbksStage2_10Ms;
        }

        for (int k = 0; k < lengthDSrch; k++)
        {
            int d = dSrch[k];
            for (int j = 0; j < nbCbkSearch; j++)
            {
                cc[j] = 0.0f;
                for (int i = 0; i < nbSubfr; i++) cc[j] += c[i * CStride + d + lagCb[i * cbkSize + j]];
            }
            float ccmaxNew = -1000.0f;
            int cbimaxNew = 0;
            for (int i = 0; i < nbCbkSearch; i++)
            {
                if (cc[i] > ccmaxNew)
                {
                    ccmaxNew = cc[i];
                    cbimaxNew = i;
                }
            }
            ccmaxNew = ccmaxNew > 0.0f ? ccmaxNew : 0.0f;
            float ccmaxNewB = ccmaxNew;

            float lagLog2 = SilkFlp.Log2((float)d);
            ccmaxNewB -= PeShortlagBias * nbSubfr * lagLog2;

            if (prevLag > 0)
            {
                float deltaLagLog2Sqr = lagLog2 - prevLagLog2;
                deltaLagLog2Sqr *= deltaLagLog2Sqr;
                ccmaxNewB -= PePrevlagBias * nbSubfr * ltpCorr * deltaLagLog2Sqr / (deltaLagLog2Sqr + 0.5f);
            }

            if (ccmaxNewB > ccmaxB && ccmaxNew > nbSubfr * searchThres2 * searchThres2 && SilkTables.CBLagsStage2[cbimaxNew] <= minLag8kHz)
            {
                ccmaxB = ccmaxNewB;
                ccmax = ccmaxNew;
                lag = d;
                cbimax = cbimaxNew;
            }
        }

        if (lag == -1)
        {
            pitchOut.Slice(0, MaxNbSubfr).Clear();
            ltpCorr = 0.0f;
            lagIndex = 0;
            contourIndex = 0;
            return true;
        }

        if (fsKHz > 8)
        {
            // Stage 3: fine search at the full rate
            if (fsKHz == 12) lag = RShiftRound(SMulBB(lag, 3), 1);
            else lag <<= 1;

            lag = Limit(lag, minLag, maxLag);
            int startLag = Math.Max(lag - 2, minLag);
            int endLag = Math.Min(lag + 2, maxLag);
            int lagNew = lag;
            cbimax = 0;

            ltpCorr = (float)Math.Sqrt(ccmax / nbSubfr);
            ccmax = -1000.0f;

            CalcCorrSt3(frame, startLag, sfLength, nbSubfr, complexity);
            CalcEnergySt3(frame, startLag, sfLength, nbSubfr, complexity);

            int lagCounter = 0;
            float contourBias = PeFlatcontourBias / lag;
            sbyte[] lagCb3;
            if (nbSubfr == MaxNbSubfr)
            {
                nbCbkSearch = NbCbkSearchsStage3[complexity];
                cbkSize = PeNbCbksStage3Max;
                lagCb3 = SilkTables.CBLagsStage3;
            }
            else
            {
                nbCbkSearch = PeNbCbksStage3_10Ms;
                cbkSize = PeNbCbksStage3_10Ms;
                lagCb3 = SilkTables.CBLagsStage310Ms;
            }

            for (int d = startLag; d <= endLag; d++)
            {
                for (int j = 0; j < nbCbkSearch; j++)
                {
                    double crossCorr = 0.0;
                    energy = Eps;
                    for (int k = 0; k < nbSubfr; k++)
                    {
                        int ix = (k * PeNbCbksStage3Max + j) * PeNbStage3Lags + lagCounter;
                        energy += _energiesSt3[ix];
                        crossCorr += _crossCorrSt3[ix];
                    }
                    float ccmaxNew;
                    if (crossCorr > 0.0)
                    {
                        ccmaxNew = (float)(crossCorr * crossCorr / energy);
                        ccmaxNew *= 1.0f - contourBias * j;
                    }
                    else ccmaxNew = 0.0f;

                    if (ccmaxNew > ccmax && (d + SilkTables.CBLagsStage3[j]) <= maxLag)
                    {
                        ccmax = ccmaxNew;
                        lagNew = d;
                        cbimax = j;
                    }
                }
                lagCounter++;
            }

            for (int k = 0; k < nbSubfr; k++)
            {
                pitchOut[k] = lagNew + lagCb3[k * cbkSize + cbimax];
                pitchOut[k] = Limit(pitchOut[k], minLag, PeMaxLagMs * fsKHz);
            }
            lagIndex = (short)(lagNew - minLag);
            contourIndex = (sbyte)cbimax;
        }
        else
        {
            ltpCorr = (float)Math.Sqrt(ccmax / nbSubfr);
            for (int k = 0; k < nbSubfr; k++)
            {
                pitchOut[k] = lag + lagCb[k * cbkSize + cbimax];
                pitchOut[k] = Limit(pitchOut[k], minLag8kHz, PeMaxLagMs * fsKHz);
            }
            lagIndex = (short)(lag - minLag8kHz);
            contourIndex = (sbyte)cbimax;
        }
        return false;
    }

    private void CalcCorrSt3(ReadOnlySpan<float> frame, int startLag, int sfLength, int nbSubfr, int complexity)
    {
        Span<float> scratch = stackalloc float[22];
        ReadOnlySpan<sbyte> lagRange;
        sbyte[] lagCb;
        int nbCbkSearch, cbkSize;
        if (nbSubfr == MaxNbSubfr)
        {
            lagRange = LagRangeStage3.AsSpan(complexity * 8);
            lagCb = SilkTables.CBLagsStage3;
            nbCbkSearch = NbCbkSearchsStage3[complexity];
            cbkSize = PeNbCbksStage3Max;
        }
        else
        {
            lagRange = LagRangeStage3_10Ms;
            lagCb = SilkTables.CBLagsStage310Ms;
            nbCbkSearch = PeNbCbksStage3_10Ms;
            cbkSize = PeNbCbksStage3_10Ms;
        }
        int target = sfLength << 2;
        for (int k = 0; k < nbSubfr; k++)
        {
            int lagCounter = 0;
            int lagLow = lagRange[k * 2];
            int lagHigh = lagRange[k * 2 + 1];
            for (int j = lagLow; j <= lagHigh; j++)
            {
                int basis = target - (startLag + j);
                scratch[lagCounter] = (float)SilkFlp.InnerProduct(frame.Slice(target), frame.Slice(basis), sfLength);
                lagCounter++;
            }
            int delta = lagRange[k * 2];
            for (int i = 0; i < nbCbkSearch; i++)
            {
                int idx = lagCb[k * cbkSize + i] - delta;
                for (int j = 0; j < PeNbStage3Lags; j++)
                    _crossCorrSt3[(k * PeNbCbksStage3Max + i) * PeNbStage3Lags + j] = scratch[idx + j];
            }
            target += sfLength;
        }
    }

    private void CalcEnergySt3(ReadOnlySpan<float> frame, int startLag, int sfLength, int nbSubfr, int complexity)
    {
        Span<float> scratch = stackalloc float[22];
        ReadOnlySpan<sbyte> lagRange;
        sbyte[] lagCb;
        int nbCbkSearch, cbkSize;
        if (nbSubfr == MaxNbSubfr)
        {
            lagRange = LagRangeStage3.AsSpan(complexity * 8);
            lagCb = SilkTables.CBLagsStage3;
            nbCbkSearch = NbCbkSearchsStage3[complexity];
            cbkSize = PeNbCbksStage3Max;
        }
        else
        {
            lagRange = LagRangeStage3_10Ms;
            lagCb = SilkTables.CBLagsStage310Ms;
            nbCbkSearch = PeNbCbksStage3_10Ms;
            cbkSize = PeNbCbksStage3_10Ms;
        }
        int target = sfLength << 2;
        for (int k = 0; k < nbSubfr; k++)
        {
            int lagCounter = 0;
            int basis = target - (startLag + lagRange[k * 2]);
            double energy = SilkFlp.Energy(frame.Slice(basis), sfLength) + 1e-3;
            scratch[lagCounter] = (float)energy;
            lagCounter++;
            int lagDiff = lagRange[k * 2 + 1] - lagRange[k * 2] + 1;
            for (int i = 1; i < lagDiff; i++)
            {
                energy -= frame[basis + sfLength - i] * (double)frame[basis + sfLength - i];
                energy += frame[basis - i] * (double)frame[basis - i];
                scratch[lagCounter] = (float)energy;
                lagCounter++;
            }
            int delta = lagRange[k * 2];
            for (int i = 0; i < nbCbkSearch; i++)
            {
                int idx = lagCb[k * cbkSize + i] - delta;
                for (int j = 0; j < PeNbStage3Lags; j++)
                    _energiesSt3[(k * PeNbCbksStage3Max + i) * PeNbStage3Lags + j] = scratch[idx + j];
            }
            target += sfLength;
        }
    }
}
