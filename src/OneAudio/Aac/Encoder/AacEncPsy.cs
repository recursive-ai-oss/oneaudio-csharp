namespace OneAudio.Aac;

/// <summary>
/// Per-block-type psychoacoustic constants: band layout, absolute thresholds, spreading factors and the smallest signal-to-noise
/// ratios that keep a band from being dropped. The model follows the structure of the 3GPP enhanced aacPlus encoder description
/// (TS 26.403 / the TS 26.410 reference): band energy, a fixed signal-to-mask ratio, spreading across the Bark scale, an absolute
/// threshold, and pre-echo limiting.
/// </summary>
internal sealed class AacEncPsyConfig
{
    // Threshold in quiet by Bark band, in dB relative to the most sensitive region (TS 26.410 psy_configuration).
    private static readonly float[] QuietDb =
    [
        15f, 10f, 7f, 2f, 0f, 0f, 0f, 0f, 0f, 0f, 0f, 0f, 0f, 0f, 0f, 0f, 0f, 0f, 0f, 0f, 3f, 5f, 10f, 20f, 30f,
    ];

    /// <summary>Signal-to-mask ratio before bit-rate reduction: -29 dB.</summary>
    public const float Ratio = 0.001258925f;

    public readonly int Lines;
    public readonly short[] Offsets;
    public readonly int BandCount;
    /// <summary>Bands that start below the low-pass line.</summary>
    public readonly int Active;
    public readonly int LowpassLine;
    public readonly float[] Quiet, LoFactor, HiFactor, MinSnr;

    /// <param name="rate">Sampling rate in Hz.</param>
    /// <param name="offsets">Band offsets, ending at <paramref name="lines"/>.</param>
    /// <param name="lines">1024 (long) or 128 (short).</param>
    /// <param name="bitratePerChannel">Bits per second per channel (drives how long minimum SNRs stay high).</param>
    /// <param name="bandwidth">Low-pass frequency in Hz.</param>
    public AacEncPsyConfig(int rate, short[] offsets, int lines, int bitratePerChannel, int bandwidth)
    {
        Lines = lines;
        Offsets = offsets;
        BandCount = offsets.Length - 1;
        LowpassLine = (int)Math.Min(lines, 2L * bandwidth * lines / rate);
        int active = 0;
        while (active < BandCount && offsets[active] < LowpassLine) active++;
        Active = Math.Max(1, active);

        var bark = new float[BandCount];
        double prev = 0;
        for (int b = 0; b < BandCount; b++)
        {
            double hi = BarkOfLine(lines, offsets[b + 1], rate);
            bark[b] = (float)((prev + hi) * 0.5);
            prev = hi;
        }

        Quiet = new float[BandCount];
        // Variance of 2.0 (16-bit LSB squared) per time sample at the most sensitive region; a coefficient of an M-line block
        // carries 2M times that variance (the transform's scale).
        double perLine = 2.0 * 2 * lines;
        for (int b = 0; b < BandCount; b++)
        {
            int bv1 = b > 0 ? (int)(bark[b] + bark[b - 1]) >> 1 : (int)bark[b] >> 1;
            int bv2 = b < BandCount - 1 ? (int)(bark[b] + bark[b + 1]) >> 1 : (int)bark[b];
            bv1 = Math.Min(bv1, 24);
            bv2 = Math.Min(bv2, 24);
            double q = Math.Min(QuietDb[bv1], QuietDb[bv2]);
            Quiet[b] = (float)(perLine * Math.Pow(10, q / 10) * (offsets[b + 1] - offsets[b]));
        }

        // Spreading: masker in band i-1 masks band i by -15 dB per Bark upward and -30 dB per Bark downward.
        LoFactor = new float[BandCount];
        HiFactor = new float[BandCount];
        for (int b = 1; b < BandCount; b++)
        {
            double d = bark[b] - bark[b - 1];
            HiFactor[b] = (float)Math.Pow(10, -1.5 * d);
            LoFactor[b - 1] = (float)Math.Pow(10, -3.0 * d);
        }

        // Smallest SNR per band that avoids holes (the reference's "min SNR": more bits per window keep more of each band).
        MinSnr = new float[BandCount];
        double pePerWindow = bitratePerChannel / (double)rate * lines * 1.18;
        double barcFactor = 1.0 / Math.Min(bark[Active - 1] / 24.0, 1.0);
        double pbVal0 = 0;
        for (int b = 0; b < Active; b++)
        {
            double pbVal1 = 2.0 * bark[b] - pbVal0;
            double width = pbVal1 - pbVal0;
            pbVal0 = pbVal1;
            double pePart = pePerWindow * 0.024 * barcFactor * width / (offsets[b + 1] - offsets[b]);
            double snr = Math.Pow(2.0, pePart) - 1.5;
            snr = 1.0 / Math.Max(snr, 1.0);
            MinSnr[b] = (float)Math.Clamp(snr, 0.003, 0.8);
        }
    }

    // Bark value of a frequency line (Zwicker's formula).
    private static double BarkOfLine(int lines, int line, int rate)
    {
        double f = line * (rate * 0.5) / lines;
        double t = Math.Atan(f / 7500.0);
        return 13.3 * Math.Atan(0.00076 * f) + 3.5 * t * t;
    }

    /// <summary>
    /// Masking threshold per band from band energies: ratio, spreading, absolute threshold. <paramref name="previous"/> holds the last
    /// block's thresholds for pre-echo limiting and is updated.
    /// </summary>
    public void Thresholds(ReadOnlySpan<float> energy, Span<float> thr, Span<float> previous, bool ignorePrevious)
    {
        int n = BandCount;
        for (int b = 0; b < n; b++) thr[b] = energy[b] * Ratio;
        for (int b = 1; b < n; b++) thr[b] = Math.Max(thr[b], HiFactor[b] * thr[b - 1]);
        for (int b = n - 2; b >= 0; b--) thr[b] = Math.Max(thr[b], LoFactor[b] * thr[b + 1]);
        for (int b = 0; b < n; b++) thr[b] = Math.Max(thr[b], Quiet[b]);
        // Pre-echo control: a threshold may rise at most 2x over the previous block's, but never falls below 1% of itself.
        for (int b = 0; b < n; b++)
        {
            float cur = thr[b];
            float limit = ignorePrevious ? float.MaxValue : 2f * previous[b];
            float floor = 0.01f * cur;
            previous[b] = cur;
            if (cur > limit) cur = limit;
            if (floor > cur) cur = floor;
            thr[b] = cur;
        }
    }
}
