using static OneAudio.Opus.SilkMath;
using static OneAudio.Opus.SilkEncMath;

namespace OneAudio.Opus;

/// <summary>Stereo (mid/side) analysis state and coding of the SILK encoder (RFC 6716 section 4.2.8, reference encoder).</summary>
internal sealed class SilkStereoEnc
{
    public const int Frames = 3;
    private const int InterpLenMs = 8;

    public readonly short[] PredPrevQ13 = new short[2];
    public readonly short[] SMid = new short[2];
    public readonly short[] SSide = new short[2];
    public readonly int[] MidSideAmpQ0 = new int[4];
    public short SmthWidthQ14;
    public short WidthPrevQ14;
    public short SilentSideLen;
    /// <summary>Quantisation indices per frame: [frame * 6 + channel * 3 + k].</summary>
    public readonly sbyte[] PredIx = new sbyte[Frames * 6];
    public readonly sbyte[] MidOnlyFlags = new sbyte[Frames];

    private readonly short[] _side = new short[SilkEncState.MaxFrameLength + 2];
    private readonly short[] _lpMid = new short[SilkEncState.MaxFrameLength];
    private readonly short[] _hpMid = new short[SilkEncState.MaxFrameLength];
    private readonly short[] _lpSide = new short[SilkEncState.MaxFrameLength];
    private readonly short[] _hpSide = new short[SilkEncState.MaxFrameLength];

    public void Reset()
    {
        Array.Clear(PredPrevQ13); Array.Clear(SMid); Array.Clear(SSide); Array.Clear(MidSideAmpQ0);
        SmthWidthQ14 = 0; WidthPrevQ14 = 0; SilentSideLen = 0;
        Array.Clear(PredIx); Array.Clear(MidOnlyFlags);
    }

    /// <summary>Called when the stream turns from mono to stereo.</summary>
    public void StartStereo()
    {
        Array.Clear(PredPrevQ13);
        Array.Clear(SSide);
        MidSideAmpQ0[0] = 0; MidSideAmpQ0[1] = 1; MidSideAmpQ0[2] = 0; MidSideAmpQ0[3] = 1;
        WidthPrevQ14 = 0;
        SmthWidthQ14 = 1 << 14;
    }

    private static int FindPredictor(out int ratioQ14, ReadOnlySpan<short> x, ReadOnlySpan<short> y, Span<int> midResAmpQ0, int length, int smoothCoefQ16)
    {
        SumSqrShift(out int nrgx, out int scale1, x, length);
        SumSqrShift(out int nrgy, out int scale2, y, length);
        int scale = Math.Max(scale1, scale2);
        scale += scale & 1;
        nrgy >>= scale - scale2;
        nrgx >>= scale - scale1;
        nrgx = Math.Max(nrgx, 1);
        int corr = 0;
        for (int i = 0; i < length; i++) corr += SMulBB(x[i], y[i]) >> scale;
        int predQ13 = Div32VarQ(corr, nrgx, 13);
        predQ13 = Limit(predQ13, -(1 << 14), 1 << 14);
        int pred2Q10 = SMulWB(predQ13, predQ13);
        smoothCoefQ16 = Math.Max(smoothCoefQ16, Abs(pred2Q10));
        scale >>= 1;
        midResAmpQ0[0] = SMlaWB(midResAmpQ0[0], (SqrtApprox(nrgx) << scale) - midResAmpQ0[0], smoothCoefQ16);
        nrgy -= SMulWB(corr, predQ13) << (3 + 1);
        nrgy += SMulWB(nrgx, pred2Q10) << 6;
        midResAmpQ0[1] = SMlaWB(midResAmpQ0[1], (SqrtApprox(nrgy) << scale) - midResAmpQ0[1], smoothCoefQ16);
        ratioQ14 = Div32VarQ(midResAmpQ0[1], Math.Max(midResAmpQ0[0], 1), 14);
        ratioQ14 = Limit(ratioQ14, 0, 32767);
        return predQ13;
    }

    private static void QuantPred(Span<int> predQ13, Span<sbyte> ix)
    {
        int quantPredQ13 = 0;
        for (int n = 0; n < 2; n++)
        {
            int errMin = int.MaxValue;
            bool done = false;
            for (int i = 0; i < 15 && !done; i++)
            {
                int low = SilkTables.StereoPredQuantQ13[i];
                int step = SMulWB(SilkTables.StereoPredQuantQ13[i + 1] - low, 6554); // 0.5 / 5 in Q16
                for (int j = 0; j < 5; j++)
                {
                    int lvl = SMlaBB(low, step, 2 * j + 1);
                    int err = Abs(predQ13[n] - lvl);
                    if (err < errMin)
                    {
                        errMin = err;
                        quantPredQ13 = lvl;
                        ix[n * 3] = (sbyte)i;
                        ix[n * 3 + 1] = (sbyte)j;
                    }
                    else
                    {
                        done = true;
                        break;
                    }
                }
            }
            ix[n * 3 + 2] = (sbyte)(ix[n * 3] / 3);
            ix[n * 3] -= (sbyte)(ix[n * 3 + 2] * 3);
            predQ13[n] = quantPredQ13;
        }
        predQ13[0] -= predQ13[1];
    }

    /// <summary>silk_stereo_LR_to_MS. The buffers are the channels' input buffers whose first two samples are history.</summary>
    public void LrToMs(Span<short> b1, Span<short> b2, int frameIdx, Span<int> midSideRatesBps, int totalRateBps,
        int prevSpeechActQ8, bool toMono, int fsKHz, int frameLength)
    {
        Span<int> predQ13 = stackalloc int[2];
        var ix = PredIx.AsSpan(frameIdx * 6, 6);
        var side = _side;
        var mid = b1;
        for (int n = 0; n < frameLength + 2; n++)
        {
            int sum = b1[n] + (int)b2[n];
            int diff = b1[n] - (int)b2[n];
            mid[n] = (short)RShiftRound(sum, 1);
            side[n] = (short)Sat16(RShiftRound(diff, 1));
        }
        SMid.AsSpan().CopyTo(mid);
        SSide.AsSpan().CopyTo(side);
        mid.Slice(frameLength, 2).CopyTo(SMid);
        side.AsSpan(frameLength, 2).CopyTo(SSide);

        for (int n = 0; n < frameLength; n++)
        {
            int sum = RShiftRound(mid[n] + mid[n + 2] + (mid[n + 1] << 1), 2);
            _lpMid[n] = (short)sum;
            _hpMid[n] = (short)(mid[n + 1] - sum);
        }
        for (int n = 0; n < frameLength; n++)
        {
            int sum = RShiftRound(side[n] + side[n + 2] + (side[n + 1] << 1), 2);
            _lpSide[n] = (short)sum;
            _hpSide[n] = (short)(side[n + 1] - sum);
        }

        bool is10ms = frameLength == 10 * fsKHz;
        int smoothCoefQ16 = is10ms ? 328 : 655; // 0.01 (/2) in Q16
        smoothCoefQ16 = SMulWB(SMulBB(prevSpeechActQ8, prevSpeechActQ8), smoothCoefQ16);
        predQ13[0] = FindPredictor(out int lpRatioQ14, _lpMid, _lpSide, MidSideAmpQ0.AsSpan(0, 2), frameLength, smoothCoefQ16);
        predQ13[1] = FindPredictor(out int hpRatioQ14, _hpMid, _hpSide, MidSideAmpQ0.AsSpan(2, 2), frameLength, smoothCoefQ16);
        int fracQ16 = SMlaBB(hpRatioQ14, lpRatioQ14, 3);
        fracQ16 = Math.Min(fracQ16, 1 << 16);

        totalRateBps -= is10ms ? 1200 : 600;
        if (totalRateBps < 1) totalRateBps = 1;
        int minMidRateBps = SMlaBB(2000, fsKHz, 900);
        int frac3Q16 = 3 * fracQ16;
        midSideRatesBps[0] = Div32VarQ(totalRateBps, ((8 + 5) << 16) + frac3Q16, 16 + 3);
        int widthQ14;
        if (midSideRatesBps[0] < minMidRateBps)
        {
            midSideRatesBps[0] = minMidRateBps;
            midSideRatesBps[1] = totalRateBps - midSideRatesBps[0];
            widthQ14 = Div32VarQ((midSideRatesBps[1] << 1) - minMidRateBps,
                SMulWB((1 << 16) + frac3Q16, minMidRateBps), 14 + 2);
            widthQ14 = Limit(widthQ14, 0, 1 << 14);
        }
        else
        {
            midSideRatesBps[1] = totalRateBps - midSideRatesBps[0];
            widthQ14 = 1 << 14;
        }

        SmthWidthQ14 = (short)SMlaWB(SmthWidthQ14, widthQ14 - SmthWidthQ14, smoothCoefQ16);

        MidOnlyFlags[frameIdx] = 0;
        if (toMono)
        {
            widthQ14 = 0;
            predQ13[0] = 0;
            predQ13[1] = 0;
            QuantPred(predQ13, ix);
        }
        else if (WidthPrevQ14 == 0 &&
            (8 * totalRateBps < 13 * minMidRateBps || SMulWB(fracQ16, SmthWidthQ14) < 819)) // 0.05 in Q14
        {
            predQ13[0] = SMulBB(SmthWidthQ14, predQ13[0]) >> 14;
            predQ13[1] = SMulBB(SmthWidthQ14, predQ13[1]) >> 14;
            QuantPred(predQ13, ix);
            widthQ14 = 0;
            predQ13[0] = 0;
            predQ13[1] = 0;
            midSideRatesBps[0] = totalRateBps;
            midSideRatesBps[1] = 0;
            MidOnlyFlags[frameIdx] = 1;
        }
        else if (WidthPrevQ14 != 0 &&
            (8 * totalRateBps < 11 * minMidRateBps || SMulWB(fracQ16, SmthWidthQ14) < 328)) // 0.02 in Q14
        {
            predQ13[0] = SMulBB(SmthWidthQ14, predQ13[0]) >> 14;
            predQ13[1] = SMulBB(SmthWidthQ14, predQ13[1]) >> 14;
            QuantPred(predQ13, ix);
            widthQ14 = 0;
            predQ13[0] = 0;
            predQ13[1] = 0;
        }
        else if (SmthWidthQ14 > 15565) // 0.95 in Q14
        {
            QuantPred(predQ13, ix);
            widthQ14 = 1 << 14;
        }
        else
        {
            predQ13[0] = SMulBB(SmthWidthQ14, predQ13[0]) >> 14;
            predQ13[1] = SMulBB(SmthWidthQ14, predQ13[1]) >> 14;
            QuantPred(predQ13, ix);
            widthQ14 = SmthWidthQ14;
        }

        if (MidOnlyFlags[frameIdx] == 1)
        {
            SilentSideLen += (short)(frameLength - InterpLenMs * fsKHz);
            if (SilentSideLen < 5 * fsKHz) MidOnlyFlags[frameIdx] = 0;
            else SilentSideLen = 10000;
        }
        else
        {
            SilentSideLen = 0;
        }

        if (MidOnlyFlags[frameIdx] == 0 && midSideRatesBps[1] < 1)
        {
            midSideRatesBps[1] = 1;
            midSideRatesBps[0] = Math.Max(1, totalRateBps - midSideRatesBps[1]);
        }

        int pred0Q13 = -PredPrevQ13[0];
        int pred1Q13 = -PredPrevQ13[1];
        int wQ24 = WidthPrevQ14 << 10;
        int denomQ16 = (1 << 16) / (InterpLenMs * fsKHz);
        int delta0Q13 = -RShiftRound(SMulBB(predQ13[0] - PredPrevQ13[0], denomQ16), 16);
        int delta1Q13 = -RShiftRound(SMulBB(predQ13[1] - PredPrevQ13[1], denomQ16), 16);
        int deltawQ24 = SMulWB(widthQ14 - WidthPrevQ14, denomQ16) << 10;
        for (int n = 0; n < InterpLenMs * fsKHz; n++)
        {
            pred0Q13 += delta0Q13;
            pred1Q13 += delta1Q13;
            wQ24 += deltawQ24;
            int sum = (mid[n] + mid[n + 2] + (mid[n + 1] << 1)) << 9;
            sum = SMlaWB(SMulWB(wQ24, side[n + 1]), sum, pred0Q13);
            sum = SMlaWB(sum, mid[n + 1] << 11, pred1Q13);
            b2[n + 1] = (short)Sat16(RShiftRound(sum, 8));
        }
        pred0Q13 = -predQ13[0];
        pred1Q13 = -predQ13[1];
        wQ24 = widthQ14 << 10;
        for (int n = InterpLenMs * fsKHz; n < frameLength; n++)
        {
            int sum = (mid[n] + mid[n + 2] + (mid[n + 1] << 1)) << 9;
            sum = SMlaWB(SMulWB(wQ24, side[n + 1]), sum, pred0Q13);
            sum = SMlaWB(sum, mid[n + 1] << 11, pred1Q13);
            b2[n + 1] = (short)Sat16(RShiftRound(sum, 8));
        }
        PredPrevQ13[0] = (short)predQ13[0];
        PredPrevQ13[1] = (short)predQ13[1];
        WidthPrevQ14 = (short)widthQ14;
    }

    public void EncodePred(ref RangeEncoder rc, int frameIdx)
    {
        var ix = PredIx.AsSpan(frameIdx * 6, 6);
        int n = 5 * ix[2] + ix[5];
        rc.ICdf(n, SilkTables.StereoPredJointICDF, 8);
        for (int c = 0; c < 2; c++)
        {
            rc.ICdf(ix[c * 3], SilkTables.Uniform3ICDF, 8);
            rc.ICdf(ix[c * 3 + 1], SilkTables.Uniform5ICDF, 8);
        }
    }

    public void EncodeMidOnly(ref RangeEncoder rc, int frameIdx) =>
        rc.ICdf(MidOnlyFlags[frameIdx], SilkTables.StereoOnlyCodeMidICDF, 8);
}
