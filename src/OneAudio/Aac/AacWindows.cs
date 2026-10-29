namespace OneAudio.Aac;

/// <summary>
/// The rising halves of the AAC windows (ISO/IEC 14496-3 4.6.11.3.2): the sine window and the Kaiser-Bessel-derived window
/// (alpha 4 for 2048-sample windows, 6 for 256). A falling half is the rising half read backwards. Computed in double
/// precision at first use rather than carried as rounded tables.
/// </summary>
internal static class AacWindows
{
    /// <summary>Long rising slope (1024 samples) for shape 0 (sine) and 1 (KBD).</summary>
    internal static readonly float[][] Long = [Sine(2048), Kbd(2048, 4.0)];

    /// <summary>Short rising slope (128 samples) for shape 0 and 1.</summary>
    internal static readonly float[][] Short = [Sine(256), Kbd(256, 6.0)];

    private static float[] Sine(int n)
    {
        var w = new float[n / 2];
        for (int i = 0; i < w.Length; i++) w[i] = (float)Math.Sin(Math.PI * (i + 0.5) / n);
        return w;
    }

    private static float[] Kbd(int n, double alpha)
    {
        int half = n / 2;
        var kaiser = new double[half + 1];
        double total = 0;
        for (int j = 0; j <= half; j++)
        {
            double r = 2.0 * j / half - 1.0;
            kaiser[j] = BesselI0(Math.PI * alpha * Math.Sqrt(Math.Max(0, 1 - r * r)));
            total += kaiser[j];
        }
        var w = new float[half];
        double acc = 0;
        for (int i = 0; i < half; i++)
        {
            acc += kaiser[i];
            w[i] = (float)Math.Sqrt(acc / total);
        }
        return w;
    }

    private static double BesselI0(double x)
    {
        double sum = 1, term = 1, q = x * x / 4;
        for (int k = 1; k < 64; k++)
        {
            term *= q / ((double)k * k);
            sum += term;
            if (term < sum * 1e-17) break;
        }
        return sum;
    }
}
