namespace OneAudio.Aac;

/// <summary>Spectral band replication header fields (ISO/IEC 14496-3 4.5.2.8.1 sbr_header), with the defaults of 4.6.18.</summary>
internal sealed class SbrHeader
{
    public int AmpResolution;       // bs_amp_res: 0 = 1.5 dB steps, 1 = 3 dB steps
    public int StartFreq;           // bs_start_freq
    public int StopFreq;            // bs_stop_freq
    public int XoverBand;           // bs_xover_band
    public int FreqScale = 2;       // bs_freq_scale
    public int AlterScale = 1;      // bs_alter_scale
    public int NoiseBands = 2;      // bs_noise_bands
    public int LimiterBands = 2;    // bs_limiter_bands
    public int LimiterGains = 2;    // bs_limiter_gains
    public int InterpolFreq = 1;    // bs_interpol_freq
    public int SmoothingLength = 1; // bs_smoothing_mode

    public void CopyFrom(SbrHeader o)
    {
        AmpResolution = o.AmpResolution; StartFreq = o.StartFreq; StopFreq = o.StopFreq; XoverBand = o.XoverBand;
        FreqScale = o.FreqScale; AlterScale = o.AlterScale; NoiseBands = o.NoiseBands; LimiterBands = o.LimiterBands;
        LimiterGains = o.LimiterGains; InterpolFreq = o.InterpolFreq; SmoothingLength = o.SmoothingLength;
    }

    /// <summary>True when the fields that decide the band layout differ (4.6.18.2.1: a new master table is built).</summary>
    public bool LayoutDiffers(SbrHeader o) =>
        StartFreq != o.StartFreq || StopFreq != o.StopFreq || XoverBand != o.XoverBand ||
        FreqScale != o.FreqScale || AlterScale != o.AlterScale || NoiseBands != o.NoiseBands;
}

/// <summary>One high-frequency patch of the transposer (4.6.18.6.3).</summary>
internal struct SbrPatch
{
    public int GuardStartBand, TargetStartBand, TargetBandOffs, NumBandsInPatch, SourceStartBand, SourceStopBand;
}

/// <summary>
/// The frequency band tables derived from a header (ISO/IEC 14496-3 4.6.18.3: master table, high and low resolution
/// tables, noise bands, limiter bands) and the patch layout of the transposer (4.6.18.6.3). Built by <see cref="Reset"/>
/// without allocating.
/// </summary>
internal sealed class SbrBands
{
    internal const int MaxFreqCoeffs = 48, MaxNoiseCoeffs = 5, MaxLimiters = 12, MaxPatches = 6;

    public readonly byte[] Master = new byte[MaxFreqCoeffs + 1];
    public int NumMaster;
    public readonly byte[][] Table = [new byte[MaxFreqCoeffs / 2 + 1], new byte[MaxFreqCoeffs + 1]]; // [0] low, [1] high resolution
    public readonly int[] NSfb = new int[2];
    public readonly byte[] Noise = new byte[MaxNoiseCoeffs + 1];
    public int NNoise;
    public int LowSubband, HighSubband;
    public readonly byte[] Limiter = new byte[MaxLimiters + 1];
    public int NLimiter;
    public readonly SbrPatch[] Patches = new SbrPatch[MaxPatches];
    public int NPatches;
    public int LbStart, LbStop;
    public readonly int[] BwBorders = new int[MaxPatches];

    private readonly byte[] _diffTot = new byte[29 + 50];
    private readonly byte[] _work = new byte[MaxFreqCoeffs / 2 + MaxPatches + 2];
    private static readonly float[] Ld = BuildLd();
    private static float[] BuildLd()
    {
        var t = new float[256];
        t[0] = -1f;
        for (int i = 1; i < t.Length; i++) t[i] = (float)(Math.Log(i) / Math.Log(2.0f));
        return t;
    }

    private static float Octaves(int a, int b) => Ld[b] - Ld[a];

    // 4.6.18.3.2 Table 4.84: offsets added to startMin; one row per class of the SBR sampling rate.
    private static ReadOnlySpan<sbyte> StartOffset(int fsSbr) => fsSbr switch
    {
        < 22050 => [-8, -7, -6, -5, -4, -3, -2, -1, 0, 1, 2, 3, 4, 5, 6, 7],
        < 24000 => [-5, -4, -3, -2, -1, 0, 1, 2, 3, 4, 5, 6, 7, 9, 11, 13],
        < 32000 => [-5, -3, -2, -1, 0, 1, 2, 3, 4, 5, 6, 7, 9, 11, 13, 16],
        < 44100 => [-6, -4, -2, -1, 0, 1, 2, 3, 4, 5, 6, 7, 9, 11, 13, 16],
        < 64000 => [-4, -2, -1, 0, 1, 2, 3, 4, 5, 6, 7, 9, 11, 13, 16, 20],
        < 88200 => [-2, -1, 0, 1, 2, 3, 4, 5, 6, 7, 9, 11, 13, 16, 20, 24],
        _ => [0, 1, 2, 3, 4, 5, 6, 7, 9, 11, 13, 16, 20, 24, 28, 33],
    };

    private static int StartBand(int fsSbr, int startFreq)
    {
        // Rates up to 48 kHz use the constant tables of the reference decoder; higher rates use the formula of 4.6.18.3.2
        // (startMin from 5000 Hz scaled to the 128-band grid, plus the offset row).
        switch (fsSbr)
        {
            case 16000: return AacSbrTables.StartFreq16[startFreq];
            case 22050: return AacSbrTables.StartFreq22[startFreq];
            case 24000: return AacSbrTables.StartFreq24[startFreq];
            case 32000: return AacSbrTables.StartFreq32[startFreq];
            case 44100: return AacSbrTables.StartFreq44[startFreq];
            case 48000: return AacSbrTables.StartFreq48[startFreq];
        }
        if (fsSbr < 64000) return -1;
        int startMin = (int)(5000.0 * 128.0 / fsSbr + 0.5);
        return startMin + StartOffset(fsSbr)[startFreq];
    }

    private static int StopMin(int fsSbr) => fsSbr switch
    {
        48000 => 21, 44100 => 23, 32000 or 24000 => 32, 22050 => 35, 16000 => 48,
        >= 64000 => (int)(10000.0 * 128.0 / fsSbr + 0.5),
        _ => -1,
    };

    /// <summary>Builds every table for <paramref name="h"/>; false when the header asks for an impossible layout.</summary>
    public bool Reset(SbrHeader h, int fsSbr)
    {
        if (!UpdateFreqScale(h, fsSbr)) return false;
        if (h.XoverBand > NumMaster) return false;
        // high resolution table: master table from the crossover band
        int nHi = NumMaster - h.XoverBand;
        for (int i = h.XoverBand; i <= NumMaster; i++) Table[1][i - h.XoverBand] = Master[i];
        // low resolution table: every second high resolution border
        int nLo;
        var hi = Table[1];
        var lo = Table[0];
        if ((nHi & 1) == 0)
        {
            nLo = nHi >> 1;
            for (int i = 0; i <= nLo; i++) lo[i] = hi[i * 2];
        }
        else
        {
            nLo = (nHi + 1) >> 1;
            lo[0] = hi[0];
            for (int i = 1; i <= nLo; i++) lo[i] = hi[i * 2 - 1];
        }
        NSfb[0] = nLo;
        NSfb[1] = nHi;
        if (nLo <= 0 || nLo > MaxFreqCoeffs / 2) return false;
        int lsb = lo[0], usb = lo[nLo];
        if (lsb > 32 || lsb >= usb) return false;
        int k2 = hi[nHi], kx = hi[0];
        if (h.NoiseBands == 0) NNoise = 1;
        else
        {
            float t = Octaves(kx, k2) * h.NoiseBands;
            int n = (int)(t + 0.5f);
            NNoise = n == 0 ? 1 : n;
        }
        if (NNoise > MaxNoiseCoeffs) return false;
        DownSampleLoRes(Noise, NNoise, lo, nLo);
        LowSubband = lsb;
        HighSubband = usb;
        return true;
    }

    private bool UpdateFreqScale(SbrHeader h, int fsSbr)
    {
        int k0 = StartBand(fsSbr, h.StartFreq);
        if (k0 < 0) return false;
        int k1, k2;
        if (h.StopFreq < 14)
        {
            int stopMin = StopMin(fsSbr);
            if (stopMin < 0) return false;
            var d0 = _diffTot.AsSpan(0, 13);
            CalcBands(d0, stopMin, 64, 13);
            Shellsort(d0, 13);
            // the stop frequency is the h.StopFreq-th entry of the cumulative sum
            int acc = stopMin;
            k2 = stopMin;
            for (int i = 0; i < h.StopFreq; i++) { acc += d0[i]; k2 = acc; }
            if (h.StopFreq == 0) k2 = stopMin;
        }
        else k2 = h.StopFreq == 14 ? 2 * k0 : 3 * k0;
        if (k2 > 64) k2 = 64;
        if (k2 - k0 > MaxFreqCoeffs || k2 <= k0) return false;
        if (fsSbr == 44100 && k2 - k0 > 35) return false;
        if (fsSbr >= 48000 && k2 - k0 > 32) return false;

        if (h.FreqScale > 0)
        {
            float bpo = h.FreqScale == 1 ? 12f : h.FreqScale == 2 ? 10f : 8f;
            var diff0 = _diffTot.AsSpan(0, 29);
            var diff1 = _diffTot.AsSpan(29, 50);
            if (1000 * k2 > 2245 * k0)
            {
                k1 = 2 * k0;
                int nb0 = NumberOfBands(bpo, k0, k1, 0);
                int nb1 = NumberOfBands(bpo, k1, k2, h.AlterScale);
                if (nb0 < 1 || nb1 < 1 || nb0 > 29 || nb1 > 50) return false;
                CalcBands(diff0, k0, k1, nb0);
                Shellsort(diff0, nb0);
                if (diff0[0] == 0) return false;
                CumSum(k0, diff0, nb0, Master, 0);
                CalcBands(diff1, k1, k2, nb1);
                Shellsort(diff1, nb1);
                if (diff0[nb0 - 1] > diff1[0])
                {
                    int change = diff0[nb0 - 1] - diff1[0];
                    if (change > (diff1[nb1 - 1] - diff1[0]) / 2) change = (diff1[nb1 - 1] - diff1[0]) / 2;
                    diff1[0] = (byte)(diff1[0] + change);
                    diff1[nb1 - 1] = (byte)(diff1[nb1 - 1] - change);
                    Shellsort(diff1, nb1);
                }
                if (nb0 + nb1 > MaxFreqCoeffs) return false;
                CumSum(k1, diff1, nb1, Master, nb0);
                NumMaster = nb0 + nb1;
            }
            else
            {
                k1 = k2;
                int nb0 = NumberOfBands(bpo, k0, k1, 0);
                if (nb0 < 1 || nb0 > 29) return false;
                CalcBands(diff0, k0, k1, nb0);
                Shellsort(diff0, nb0);
                if (diff0[0] == 0) return false;
                CumSum(k0, diff0, nb0, Master, 0);
                NumMaster = nb0;
            }
        }
        else
        {
            int dk, nb0;
            if (h.AlterScale == 0) { dk = 1; nb0 = (k2 - k0) & 254; }
            else { dk = 2; nb0 = (((k2 - k0) >> 1) + 1) & 254; }
            if (nb0 < 1 || nb0 > MaxFreqCoeffs) return false;
            int k2Achieved = k0 + nb0 * dk;
            int k2Diff = k2 - k2Achieved;
            var d = _diffTot.AsSpan(0, nb0);
            for (int i = 0; i < nb0; i++) d[i] = (byte)dk;
            int incr = 0, at = 0;
            if (k2Diff < 0) { incr = 1; at = 0; }
            if (k2Diff > 0) { incr = -1; at = nb0 - 1; }
            while (k2Diff != 0)
            {
                d[at] = (byte)(d[at] - incr);
                at += incr;
                k2Diff += incr;
            }
            CumSum(k0, d, nb0, Master, 0);
            NumMaster = nb0;
        }
        return NumMaster >= 1;
    }

    private static int NumberOfBands(float bpo, int start, int stop, int warp)
    {
        float v = 0.5f * Octaves(start, stop) * bpo;
        if (warp != 0) v *= 25200.0f / 32768.0f;
        v += 0.5f;
        return 2 * (int)v;
    }

    private static void CalcBands(Span<byte> diff, int start, int stop, int numBands)
    {
        float exact = start;
        float bandFactor = (float)Math.Pow((float)stop * (1.0f / start), 1.0f / numBands);
        int previous = start;
        for (int i = 1; i <= numBands; i++)
        {
            exact *= bandFactor;
            int current = (int)(exact + 0.5f);
            diff[i - 1] = (byte)(current - previous);
            previous = current;
        }
    }

    private static void CumSum(int start, ReadOnlySpan<byte> diff, int length, byte[] dest, int at)
    {
        dest[at] = (byte)start;
        for (int i = 1; i <= length; i++) dest[at + i] = (byte)(dest[at + i - 1] + diff[i - 1]);
    }

    internal static void Shellsort(Span<byte> a, int n)
    {
        // plain insertion sort: the tables are at most 50 entries
        for (int i = 1; i < n; i++)
        {
            byte v = a[i];
            int j = i - 1;
            while (j >= 0 && a[j] > v) { a[j + 1] = a[j]; j--; }
            a[j + 1] = v;
        }
    }

    private static void DownSampleLoRes(byte[] result, int numResult, byte[] reference, int numRef)
    {
        Span<int> index = stackalloc int[MaxNoiseCoeffs + 2];
        int orgLength = numRef, resultLength = numResult;
        index[0] = 0;
        int i = 0;
        while (orgLength > 0)
        {
            i++;
            int step = orgLength / resultLength;
            orgLength -= step;
            resultLength--;
            index[i] = index[i - 1] + step;
        }
        for (int j = 0; j <= i; j++) result[j] = reference[index[j]];
    }

    // --- patches (4.6.18.6.3) ----------------------------------------------------------------------------------------

    private static int FindClosest(int goal, byte[] master, int numMaster, bool up)
    {
        if (goal <= master[0]) return master[0];
        if (goal >= master[numMaster]) return master[numMaster];
        int index;
        if (up)
        {
            index = 0;
            while (master[index] < goal) index++;
        }
        else
        {
            index = numMaster;
            while (master[index] > goal) index--;
        }
        return master[index];
    }

    /// <summary>Lays out the patches for the current band tables; false when the layout is impossible.</summary>
    public bool ResetPatches(int fsSbr)
    {
        int lsb = Master[0];
        int xoverOffset = LowSubband - lsb;
        if (lsb - 1 < 4) return false;
        int goalSb = (int)(2048000.0 / fsSbr + 0.5);
        if (fsSbr is >= 16000 and <= 32000) goalSb = 64;
        goalSb = FindClosest(goalSb, Master, NumMaster, true);
        if (Math.Abs(goalSb - HighSubband) < 4) goalSb = HighSubband;
        int sourceStart = 1 + xoverOffset;
        int targetStop = lsb + xoverOffset;
        int patch = 0;
        while (targetStop < HighSubband)
        {
            if (patch > MaxPatches) return false;
            if (patch >= MaxPatches) return false;
            Patches[patch].GuardStartBand = targetStop;
            Patches[patch].TargetStartBand = targetStop;
            int numBands = goalSb - targetStop;
            int patchDistance;
            if (numBands >= lsb - sourceStart)
            {
                patchDistance = (targetStop - sourceStart) & ~1;
                numBands = lsb - (targetStop - patchDistance);
                numBands = FindClosest(targetStop + numBands, Master, NumMaster, false) - targetStop;
            }
            patchDistance = (numBands + targetStop - lsb + 1) & ~1;
            if (numBands > 0)
            {
                Patches[patch].SourceStartBand = targetStop - patchDistance;
                Patches[patch].TargetBandOffs = patchDistance;
                Patches[patch].NumBandsInPatch = numBands;
                Patches[patch].SourceStopBand = Patches[patch].SourceStartBand + numBands;
                targetStop += numBands;
                patch++;
            }
            sourceStart = 1;
            if (Math.Abs(targetStop - goalSb) < 3) goalSb = HighSubband;
        }
        patch--;
        if (patch > 0 && Patches[patch].NumBandsInPatch < 3)
        {
            patch--;
            targetStop = Patches[patch].TargetStartBand + Patches[patch].NumBandsInPatch;
        }
        if (patch < 0 || patch >= MaxPatches) return false;
        NPatches = patch + 1;
        LbStart = targetStop;
        LbStop = 0;
        for (int p = 0; p < NPatches; p++)
        {
            LbStart = Math.Min(LbStart, Patches[p].SourceStartBand);
            LbStop = Math.Max(LbStop, Patches[p].SourceStopBand);
        }
        for (int i = 0; i < NNoise; i++) BwBorders[i] = Noise[i + 1];
        return true;
    }

    // 4.6.18.7.2: limiter band table
    public bool ResetLimiter(int limiterBands)
    {
        int lowSubband = Table[0][0];
        int highSubband = Table[0][NSfb[0]];
        int nBands;
        if (limiterBands == 0)
        {
            Limiter[0] = 0;
            Limiter[1] = (byte)(highSubband - lowSubband);
            nBands = 1;
        }
        else
        {
            Span<int> patchBorders = stackalloc int[MaxPatches + 1];
            for (int i = 0; i < NPatches; i++) patchBorders[i] = Patches[i].GuardStartBand - lowSubband;
            patchBorders[NPatches] = highSubband - lowSubband;
            var work = _work;
            int nLo = NSfb[0];
            for (int k = 0; k <= nLo; k++) work[k] = (byte)(Table[0][k] - lowSubband);
            for (int k = 1; k < NPatches; k++) work[nLo + k] = (byte)patchBorders[k];
            int tempNoLim = nBands = nLo + NPatches - 1;
            Shellsort(work, tempNoLim + 1);
            int lo = 0, hi = 1;
            while (hi <= tempNoLim)
            {
                int k2 = work[hi] + lowSubband;
                int kx = work[lo] + lowSubband;
                float t = Octaves(kx, k2) * LimiterBandsPerOctave[limiterBands];
                if (t < 0.49f)
                {
                    if (work[hi] == work[lo])
                    {
                        work[hi] = (byte)highSubband;
                        nBands--;
                        hi++;
                        continue;
                    }
                    bool hiIsBorder = false;
                    for (int k = 0; k <= NPatches; k++) if (work[hi] == patchBorders[k]) { hiIsBorder = true; break; }
                    if (!hiIsBorder)
                    {
                        work[hi] = (byte)highSubband;
                        nBands--;
                        hi++;
                        continue;
                    }
                    bool loIsBorder = false;
                    for (int k = 0; k <= NPatches; k++) if (work[lo] == patchBorders[k]) { loIsBorder = true; break; }
                    if (!loIsBorder)
                    {
                        work[lo] = (byte)highSubband;
                        nBands--;
                    }
                }
                lo = hi;
                hi++;
            }
            Shellsort(work, tempNoLim + 1);
            if (nBands > MaxLimiters || nBands <= 0) return false;
            for (int k = 0; k <= nBands; k++) Limiter[k] = work[k];
        }
        NLimiter = nBands;
        return true;
    }

    private static readonly float[] LimiterBandsPerOctave = [1.0f, 1.2f, 2.0f, 3.0f];
}
