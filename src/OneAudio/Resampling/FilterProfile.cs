namespace OneAudio.Resampling;

/// <summary>
/// Windowed-sinc low-pass prototypes for the polyphase resampler. The design is the classic Kaiser-window method
/// (Kaiser, "Nonrecursive digital filter design using the I0-sinh window function", 1974; Oppenheim and Schafer,
/// "Discrete-Time Signal Processing", section on Kaiser window filter design): the stopband attenuation A in dB sets
/// beta = 0.1102 (A - 8.7) and the transition width sets the length, N about (A - 8) / (2.285 * dw).
/// </summary>
internal sealed class FilterProfile
{
    /// <summary>Table resolution: prototype samples per input-sample spacing.</summary>
    public const int TableResolution = 1024;

    /// <summary>Half-width of the prototype in input samples (zero crossings per side at unit scale).</summary>
    public int HalfWidth { get; }

    /// <summary>Kaiser window shape parameter.</summary>
    public double Beta { get; }

    /// <summary>Cutoff as a fraction of the lower Nyquist frequency; the transition band is centred on it.</summary>
    public double Cutoff { get; }

    /// <summary>Prototype impulse response for u >= 0 (it is even), at <see cref="TableResolution"/> points per unit, plus guard entries.</summary>
    public float[] Table { get; }

    private FilterProfile(int halfWidth, double beta, double cutoff)
    {
        HalfWidth = halfWidth;
        Beta = beta;
        Cutoff = cutoff;
        var t = new float[halfWidth * TableResolution + 2];
        double i0Beta = BesselI0(beta);
        for (int k = 0; k < t.Length; k++)
        {
            double u = (double)k / TableResolution;
            if (u >= halfWidth) continue;
            double r = u / halfWidth;
            double w = BesselI0(beta * Math.Sqrt(1 - r * r)) / i0Beta;
            double x = cutoff * u;
            double sinc = x < 1e-12 ? 1.0 : Math.Sin(Math.PI * x) / (Math.PI * x);
            t[k] = (float)(cutoff * sinc * w);
        }
        Table = t;
    }

    /// <summary>
    /// Stopband about 110 dB from 1.0 of the lower Nyquist frequency, passband flat past 0.9 (transition 0.9 to 1.0,
    /// so N is about 142; 144 taps at unit scale).
    /// </summary>
    public static FilterProfile High { get; } = new(72, 11.16, 0.95);

    /// <summary>Stopband about 75 dB from 1.0 of the lower Nyquist frequency, passband flat to 0.8 (44 taps at unit scale).</summary>
    public static FilterProfile Fast { get; } = new(22, 7.2, 0.90);

    public static FilterProfile For(ResamplerQuality quality) => quality switch
    {
        ResamplerQuality.Polyphase => High,
        ResamplerQuality.PolyphaseFast => Fast,
        _ => throw new ArgumentOutOfRangeException(nameof(quality)),
    };

    /// <summary>Modified Bessel function of the first kind, order 0, by its power series.</summary>
    private static double BesselI0(double x)
    {
        double sum = 1, term = 1, q = x * x / 4;
        for (int k = 1; k < 200; k++)
        {
            term *= q / ((double)k * k);
            sum += term;
            if (term < sum * 1e-17) break;
        }
        return sum;
    }
}
