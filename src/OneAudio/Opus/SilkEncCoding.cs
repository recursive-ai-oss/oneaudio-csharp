using static OneAudio.Opus.SilkMath;
using static OneAudio.Opus.SilkEncMath;

namespace OneAudio.Opus;

internal sealed partial class SilkEncState
{
    private const int MaxPulses = 16;
    private static readonly byte[] MaxPulsesTable = [8, 10, 12, 14];

    private static readonly byte[] PulsesPerBlockBitsQ5 =
    [
        31, 57, 107, 160, 205, 205, 255, 255, 255, 255, 255, 255, 255, 255, 255, 255, 255, 255,
        69, 47, 67, 111, 166, 205, 255, 255, 255, 255, 255, 255, 255, 255, 255, 255, 255, 255,
        82, 74, 79, 95, 109, 128, 145, 160, 173, 205, 205, 205, 224, 255, 255, 224, 255, 224,
        125, 74, 59, 69, 97, 141, 182, 255, 255, 255, 255, 255, 255, 255, 255, 255, 255, 255,
        173, 115, 85, 73, 76, 92, 115, 145, 173, 205, 224, 224, 255, 255, 255, 255, 255, 255,
        166, 134, 113, 102, 101, 102, 107, 118, 125, 138, 145, 155, 166, 182, 192, 192, 205, 150,
        224, 182, 134, 101, 83, 79, 85, 97, 120, 145, 173, 205, 224, 255, 255, 255, 255, 255,
        255, 224, 192, 150, 120, 101, 92, 89, 93, 102, 118, 134, 160, 182, 192, 224, 224, 224,
        255, 224, 224, 182, 155, 134, 118, 109, 104, 102, 106, 111, 118, 131, 145, 160, 173, 131,
    ];

    private static readonly byte[] RateLevelsBitsQ5 =
    [
        131, 74, 141, 79, 80, 138, 95, 104, 134,
        95, 99, 91, 125, 93, 76, 123, 115, 123,
    ];

    private readonly int[] _absPulses = new int[MaxFrameLength + 16];
    private readonly int[] _sumPulses = new int[MaxFrameLength / 16 + 1];
    private readonly int[] _nRshifts = new int[MaxFrameLength / 16 + 1];
    private readonly int[] _pulsesComb = new int[8];
    private readonly byte[] _signIcdf = new byte[2];

    private static bool CombineAndCheck(Span<int> comb, ReadOnlySpan<int> inp, int maxPulses, int len)
    {
        for (int k = 0; k < len; k++)
        {
            int sum = inp[2 * k] + inp[2 * k + 1];
            if (sum > maxPulses) return true;
            comb[k] = sum;
        }
        return false;
    }

    private static void EncodeSplit(ref RangeEncoder rc, int pChild1, int p, byte[] table)
    {
        if (p > 0) rc.ICdf(pChild1, table.AsSpan(SilkTables.ShellCodeTableOffsets[p]), 8);
    }

    private static void ShellEncoder(ref RangeEncoder rc, ReadOnlySpan<int> p0)
    {
        Span<int> p1 = stackalloc int[8], p2 = stackalloc int[4], p3 = stackalloc int[2];
        for (int k = 0; k < 8; k++) p1[k] = p0[2 * k] + p0[2 * k + 1];
        for (int k = 0; k < 4; k++) p2[k] = p1[2 * k] + p1[2 * k + 1];
        for (int k = 0; k < 2; k++) p3[k] = p2[2 * k] + p2[2 * k + 1];
        int p4 = p3[0] + p3[1];
        EncodeSplit(ref rc, p3[0], p4, SilkTables.ShellCodeTable3);
        EncodeSplit(ref rc, p2[0], p3[0], SilkTables.ShellCodeTable2);
        EncodeSplit(ref rc, p1[0], p2[0], SilkTables.ShellCodeTable1);
        EncodeSplit(ref rc, p0[0], p1[0], SilkTables.ShellCodeTable0);
        EncodeSplit(ref rc, p0[2], p1[1], SilkTables.ShellCodeTable0);
        EncodeSplit(ref rc, p1[2], p2[1], SilkTables.ShellCodeTable1);
        EncodeSplit(ref rc, p0[4], p1[2], SilkTables.ShellCodeTable0);
        EncodeSplit(ref rc, p0[6], p1[3], SilkTables.ShellCodeTable0);
        EncodeSplit(ref rc, p2[2], p3[1], SilkTables.ShellCodeTable2);
        EncodeSplit(ref rc, p1[4], p2[2], SilkTables.ShellCodeTable1);
        EncodeSplit(ref rc, p0[8], p1[4], SilkTables.ShellCodeTable0);
        EncodeSplit(ref rc, p0[10], p1[5], SilkTables.ShellCodeTable0);
        EncodeSplit(ref rc, p1[6], p2[3], SilkTables.ShellCodeTable1);
        EncodeSplit(ref rc, p0[12], p1[6], SilkTables.ShellCodeTable0);
        EncodeSplit(ref rc, p0[14], p1[7], SilkTables.ShellCodeTable0);
    }

    /// <summary>silk_encode_pulses: rate-level choice, shell-coded magnitudes, extra LSBs and signs (RFC 6716 section 4.2.7.8).</summary>
    public void EncodePulses(ref RangeEncoder rc, int signalType, int quantOffsetType, sbyte[] pulses, int frameLength)
    {
        var absPulses = _absPulses;
        var sumPulses = _sumPulses;
        var nRshifts = _nRshifts;
        var comb = _pulsesComb;
        Array.Clear(comb);

        int iter = frameLength >> 4;
        if (iter * 16 < frameLength)
        {
            iter++;
            Array.Clear(pulses, frameLength, 16);
        }
        for (int i = 0; i < iter * 16; i++) absPulses[i] = Math.Abs((int)pulses[i]);

        for (int i = 0; i < iter; i++)
        {
            nRshifts[i] = 0;
            var abs = absPulses.AsSpan(i * 16, 16);
            while (true)
            {
                bool scaleDown = CombineAndCheck(comb, abs, MaxPulsesTable[0], 8);
                scaleDown |= CombineAndCheck(comb, comb, MaxPulsesTable[1], 4);
                scaleDown |= CombineAndCheck(comb, comb, MaxPulsesTable[2], 2);
                scaleDown |= CombineAndCheck(sumPulses.AsSpan(i), comb, MaxPulsesTable[3], 1);
                if (scaleDown)
                {
                    nRshifts[i]++;
                    for (int k = 0; k < 16; k++) abs[k] >>= 1;
                }
                else break;
            }
        }

        int minSumBitsQ5 = int.MaxValue;
        int rateLevelIndex = 0;
        for (int k = 0; k < 9; k++)
        {
            int sumBitsQ5 = RateLevelsBitsQ5[(signalType >> 1) * 9 + k];
            for (int i = 0; i < iter; i++)
                sumBitsQ5 += nRshifts[i] > 0 ? PulsesPerBlockBitsQ5[k * 18 + MaxPulses + 1] : PulsesPerBlockBitsQ5[k * 18 + sumPulses[i]];
            if (sumBitsQ5 < minSumBitsQ5)
            {
                minSumBitsQ5 = sumBitsQ5;
                rateLevelIndex = k;
            }
        }
        rc.ICdf(rateLevelIndex, SilkTables.RateLevelsICDF.AsSpan((signalType >> 1) * 9), 8);

        var cdf = SilkTables.PulsesPerBlockICDF.AsSpan(rateLevelIndex * 18);
        var lastCdf = SilkTables.PulsesPerBlockICDF.AsSpan(9 * 18);
        for (int i = 0; i < iter; i++)
        {
            if (nRshifts[i] == 0)
            {
                rc.ICdf(sumPulses[i], cdf, 8);
            }
            else
            {
                rc.ICdf(MaxPulses + 1, cdf, 8);
                for (int k = 0; k < nRshifts[i] - 1; k++) rc.ICdf(MaxPulses + 1, lastCdf, 8);
                rc.ICdf(sumPulses[i], lastCdf, 8);
            }
        }

        for (int i = 0; i < iter; i++)
            if (sumPulses[i] > 0) ShellEncoder(ref rc, absPulses.AsSpan(i * 16, 16));

        for (int i = 0; i < iter; i++)
        {
            if (nRshifts[i] > 0)
            {
                int nLs = nRshifts[i] - 1;
                for (int k = 0; k < 16; k++)
                {
                    int absQ = (sbyte)Math.Abs((int)pulses[i * 16 + k]);
                    for (int j = nLs; j > 0; j--)
                        rc.ICdf((absQ >> j) & 1, SilkTables.LsbICDF, 8);
                    rc.ICdf(absQ & 1, SilkTables.LsbICDF, 8);
                }
            }
        }

        // Signs (silk_encode_signs)
        var icdf = _signIcdf;
        icdf[1] = 0;
        int signBase = 7 * (quantOffsetType + (signalType << 1));
        int blocks = (frameLength + 8) >> 4;
        for (int i = 0; i < blocks; i++)
        {
            int p = sumPulses[i];
            if (p > 0)
            {
                icdf[0] = SilkTables.SignICDF[signBase + Math.Min(p & 0x1F, 6)];
                for (int j = 0; j < 16; j++)
                {
                    int q = pulses[i * 16 + j];
                    if (q != 0) rc.ICdf((q >> 15) + 1, icdf, 8);
                }
            }
        }
    }

    /// <summary>silk_encode_indices: all side information of one frame (RFC 6716 section 4.2.7).</summary>
    public void EncodeIndices(ref RangeEncoder rc, int frameIndex, bool encodeLbrr, int condCoding)
    {
        var ind = encodeLbrr ? IndicesLbrr[frameIndex] : Indices;
        int typeOffset = 2 * ind.SignalType + ind.QuantOffsetType;
        if (encodeLbrr || typeOffset >= 2) rc.ICdf(typeOffset - 2, SilkTables.TypeOffsetVADICDF, 8);
        else rc.ICdf(typeOffset, SilkTables.TypeOffsetNoVADICDF, 8);

        if (condCoding == CodeConditionally)
        {
            rc.ICdf(ind.GainsIndices[0], SilkTables.DeltaGainICDF, 8);
        }
        else
        {
            rc.ICdf(ind.GainsIndices[0] >> 3, SilkTables.GainICDF.AsSpan(ind.SignalType * 8), 8);
            rc.ICdf(ind.GainsIndices[0] & 7, SilkTables.Uniform8ICDF, 8);
        }
        for (int i = 1; i < NbSubfr; i++) rc.ICdf(ind.GainsIndices[i], SilkTables.DeltaGainICDF, 8);

        var cb = NlsfCb;
        rc.ICdf(ind.NlsfIndices[0], cb.CB1ICdf.AsSpan((ind.SignalType >> 1) * cb.NVectors), 8);
        Span<short> ecIx = stackalloc short[MaxLpcOrder];
        Span<byte> predQ8 = stackalloc byte[MaxLpcOrder];
        NlsfUnpack(ecIx, predQ8, cb, ind.NlsfIndices[0]);
        for (int i = 0; i < cb.Order; i++)
        {
            int v = ind.NlsfIndices[i + 1];
            if (v >= 4)
            {
                rc.ICdf(2 * 4, cb.EcICdf.AsSpan(ecIx[i]), 8);
                rc.ICdf(v - 4, SilkTables.NLSFEXTICDF, 8);
            }
            else if (v <= -4)
            {
                rc.ICdf(0, cb.EcICdf.AsSpan(ecIx[i]), 8);
                rc.ICdf(-v - 4, SilkTables.NLSFEXTICDF, 8);
            }
            else
            {
                rc.ICdf(v + 4, cb.EcICdf.AsSpan(ecIx[i]), 8);
            }
        }

        if (NbSubfr == MaxNbSubfr) rc.ICdf(ind.NlsfInterpCoefQ2, SilkTables.NLSFInterpolationFactorICDF, 8);

        if (ind.SignalType == TypeVoiced)
        {
            bool encodeAbsoluteLagIndex = true;
            if (condCoding == CodeConditionally && EcPrevSignalType == TypeVoiced)
            {
                int deltaLagIndex = ind.LagIndex - EcPrevLagIndex;
                if (deltaLagIndex < -8 || deltaLagIndex > 11)
                {
                    deltaLagIndex = 0;
                }
                else
                {
                    deltaLagIndex += 9;
                    encodeAbsoluteLagIndex = false;
                }
                rc.ICdf(deltaLagIndex, SilkTables.PitchDeltaICDF, 8);
            }
            if (encodeAbsoluteLagIndex)
            {
                int pitchHighBits = ind.LagIndex / (FsKHz >> 1);
                int pitchLowBits = ind.LagIndex - pitchHighBits * (FsKHz >> 1);
                rc.ICdf(pitchHighBits, SilkTables.PitchLagICDF, 8);
                rc.ICdf(pitchLowBits, PitchLagLowBitsICdf, 8);
            }
            EcPrevLagIndex = ind.LagIndex;
            rc.ICdf(ind.ContourIndex, PitchContourICdf, 8);
            rc.ICdf(ind.PerIndex, SilkTables.LTPPerIndexICDF, 8);
            byte[] ltpIcdf = ind.PerIndex == 0 ? SilkTables.LTPGainICDF0 : ind.PerIndex == 1 ? SilkTables.LTPGainICDF1 : SilkTables.LTPGainICDF2;
            for (int k = 0; k < NbSubfr; k++) rc.ICdf(ind.LtpIndex[k], ltpIcdf, 8);
            if (condCoding == CodeIndependently) rc.ICdf(ind.LtpScaleIndex, SilkTables.LTPscaleICDF, 8);
        }
        EcPrevSignalType = ind.SignalType;
        rc.ICdf(ind.Seed, SilkTables.Uniform4ICDF, 8);
    }

    /// <summary>silk_NLSF_unpack: entropy-table indices and predictor weights for a first-stage vector.</summary>
    internal static void NlsfUnpack(Span<short> ecIx, Span<byte> predQ8, NlsfCodebook cb, int cb1Index)
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

    /// <summary>silk_gains_quant: quantises subframe gains (Q16 in, quantised Q16 out) to indices with delta coding.</summary>
    public static void GainsQuant(Span<sbyte> ind, Span<int> gainQ16, ref sbyte prevInd, bool conditional, int nbSubfr)
    {
        const int offset = 2090;           // (2 * 128) / 6 + 16 * 128
        const int scaleQ16 = 2251;         // 65536 * 63 / ((88 - 2) * 128 / 6)
        const int invScaleQ16 = 1907825;
        for (int k = 0; k < nbSubfr; k++)
        {
            int v = SMulWB(scaleQ16, Lin2Log(gainQ16[k]) - offset);
            if (v < prevInd) v++;
            v = Limit(v, 0, 63);
            if (k == 0 && !conditional)
            {
                v = Limit(v, prevInd + -4, 63);
                prevInd = (sbyte)v;
            }
            else
            {
                v -= prevInd;
                int doubleStepSizeThreshold = 2 * 36 - 64 + prevInd;
                if (v > doubleStepSizeThreshold) v = doubleStepSizeThreshold + ((v - doubleStepSizeThreshold + 1) >> 1);
                v = Limit(v, -4, 36);
                if (v > doubleStepSizeThreshold) prevInd = (sbyte)(prevInd + (v << 1) - doubleStepSizeThreshold);
                else prevInd = (sbyte)(prevInd + v);
                v -= -4;
            }
            ind[k] = (sbyte)v;
            gainQ16[k] = Log2Lin(Math.Min(SMulWB(invScaleQ16, prevInd) + offset, 3967));
        }
    }

    /// <summary>silk_gains_ID: a hash of the gain indices used to compare quantisations.</summary>
    public static int GainsId(ReadOnlySpan<sbyte> ind, int nbSubfr)
    {
        int id = 0;
        for (int k = 0; k < nbSubfr; k++) id = ind[k] + (id << 8);
        return id;
    }

    /// <summary>silk_gains_dequant.</summary>
    public static void GainsDequant(Span<int> gainQ16, ReadOnlySpan<sbyte> ind, ref sbyte prevInd, bool conditional, int nbSubfr)
    {
        const int offset = 2090;
        const int invScaleQ16 = 1907825;
        for (int k = 0; k < nbSubfr; k++)
        {
            int p = prevInd;
            if (k == 0 && !conditional)
            {
                p = Math.Max(ind[k], p - 16);
            }
            else
            {
                int indTmp = ind[k] + -4;
                int doubleStepSizeThreshold = 2 * 36 - 64 + p;
                if (indTmp > doubleStepSizeThreshold) p += (indTmp << 1) - doubleStepSizeThreshold;
                else p += indTmp;
            }
            p = Limit(p, 0, 63);
            prevInd = (sbyte)p;
            gainQ16[k] = Log2Lin(Math.Min(SMulWB(invScaleQ16, p) + offset, 3967));
        }
    }
}
