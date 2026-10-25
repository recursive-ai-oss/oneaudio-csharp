namespace OneAudio.Loudness;

/// <summary>
/// K-weighting pre-filter of ITU-R BS.1770-5 (Annex 1): a high-shelf "head" filter followed by a high-pass (RLB) filter.
/// The Recommendation prints coefficients for 48 kHz only and asks other rates to "provide the same frequency
/// response". The analog parameters below (centre frequencies, shelf gain, Q values) are the ones that the 48 kHz
/// table realises through a bilinear transform; designing the biquads from them at the actual rate, with the centre
/// frequencies pre-warped, gives the table's coefficients at 48 kHz and the same analog response elsewhere.
/// </summary>
internal readonly struct KWeighting
{
    // Analog parameters recovered from the 48 kHz table (the table values are reproduced to better than 1e-9).
    private const double ShelfFreq = 1681.974450955533;
    private const double ShelfGainDb = 3.999843853973347;
    private const double ShelfQ = 0.7071752369554196;
    private const double HighPassFreq = 38.13547087602444;
    private const double HighPassQ = 0.5003270373238773;

    // Table 1 and Table 2 of the Recommendation, for tests and for the high-pass gain constant.
    internal static ReadOnlySpan<double> Table48kShelf => [1.53512485958697, -2.69169618940638, 1.19839281085285, -1.69065929318241, 0.73248077421585];
    internal static ReadOnlySpan<double> Table48kHighPass => [1.0, -2.0, 1.0, -1.99004745483398, 0.99007225036621];

    /// <summary>Stage 1 numerator b0..b2 and denominator a1, a2 (a0 = 1).</summary>
    public readonly double S1b0, S1b1, S1b2, S1a1, S1a2;

    /// <summary>Stage 2 numerator b0..b2 and denominator a1, a2 (a0 = 1).</summary>
    public readonly double S2b0, S2b1, S2b2, S2a1, S2a2;

    public KWeighting(double sampleRate)
    {
        // High shelf: H(s) = (Vh s^2 + (Vb/Q) s + 1) / (s^2 + s/Q + 1), s normalised to the centre frequency.
        double k = Math.Tan(Math.PI * ShelfFreq / sampleRate);
        double vh = Math.Pow(10.0, ShelfGainDb / 20.0);
        double vb = Math.Pow(vh, 0.4996667741545416);
        double a0 = 1.0 + k / ShelfQ + k * k;
        S1b0 = (vh + vb * k / ShelfQ + k * k) / a0;
        S1b1 = 2.0 * (k * k - vh) / a0;
        S1b2 = (vh - vb * k / ShelfQ + k * k) / a0;
        S1a1 = 2.0 * (k * k - 1.0) / a0;
        S1a2 = (1.0 - k / ShelfQ + k * k) / a0;

        // High-pass. The Recommendation's table carries unit numerator coefficients at 48 kHz, i.e. a passband gain of
        // its a0 (about +0.043 dB) relative to a unity-gain analog high-pass; keep that gain at every rate so the
        // response matches the table everywhere.
        double kh = Math.Tan(Math.PI * HighPassFreq / sampleRate);
        double h0 = 1.0 + kh / HighPassQ + kh * kh;
        double kh48 = Math.Tan(Math.PI * HighPassFreq / 48000.0);
        double gain = 1.0 + kh48 / HighPassQ + kh48 * kh48;
        double g = gain / h0;
        S2b0 = g;
        S2b1 = -2.0 * g;
        S2b2 = g;
        S2a1 = 2.0 * (kh * kh - 1.0) / h0;
        S2a2 = (1.0 - kh / HighPassQ + kh * kh) / h0;
    }

    /// <summary>Combined magnitude response in dB at <paramref name="freq"/> Hz for a stream at <paramref name="sampleRate"/>.</summary>
    internal static double ResponseDb(ReadOnlySpan<double> shelf, ReadOnlySpan<double> highPass, double freq, double sampleRate)
    {
        return 20.0 * Math.Log10(Magnitude(shelf, freq, sampleRate) * Magnitude(highPass, freq, sampleRate));
    }

    private static double Magnitude(ReadOnlySpan<double> c, double freq, double sampleRate)
    {
        double w = 2.0 * Math.PI * freq / sampleRate;
        var z1 = new System.Numerics.Complex(Math.Cos(w), -Math.Sin(w));
        var z2 = z1 * z1;
        var num = c[0] + c[1] * z1 + c[2] * z2;
        var den = 1.0 + c[3] * z1 + c[4] * z2;
        return (num / den).Magnitude;
    }

    internal double[] ShelfArray() => [S1b0, S1b1, S1b2, S1a1, S1a2];

    internal double[] HighPassArray() => [S2b0, S2b1, S2b2, S2a1, S2a2];
}
