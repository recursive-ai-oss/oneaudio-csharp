namespace OneAudio.Loudness;

/// <summary>
/// Fixed-memory store of loudness values for the gated measurements. Each bin is 0.01 LU wide and keeps how many
/// values fell in it and their summed mean-square power, so the gated mean is exact in power and only the gating
/// decision and the LRA percentiles are quantised to one bin (0.01 LU). Memory never grows with programme length.
/// </summary>
internal sealed class LoudnessHistogram
{
    public const double Floor = -70.0;      // absolute gate, LUFS
    public const double Offset = 0.691;     // BS.1770 constant in equation (2)
    private const int PerLu = 100;
    private const int Bins = 90 * PerLu;    // -70 .. +20 LUFS; anything louder lands in the top bin

    private readonly long[] _count = new long[Bins];
    private readonly double[] _power = new double[Bins];

    public static double ToLufs(double meanSquare) => -Offset + 10.0 * Math.Log10(meanSquare);

    public void Clear()
    {
        Array.Clear(_count);
        Array.Clear(_power);
    }

    /// <summary>Adds one block's weighted mean-square power. Blocks at or below the absolute gate are dropped.</summary>
    public void Add(double meanSquare)
    {
        if (!(meanSquare > 0)) return;
        double l = ToLufs(meanSquare);
        if (!(l > Floor)) return;
        int bin = (int)((l - Floor) * PerLu);
        if (bin >= Bins) bin = Bins - 1;
        _count[bin]++;
        _power[bin] += meanSquare;
    }

    private static int BinOf(double lufs)
    {
        double b = Math.Floor((lufs - Floor) * PerLu);
        return b < -1 ? -1 : b >= Bins ? Bins - 1 : (int)b;
    }

    /// <summary>
    /// Mean power of all stored values above the relative threshold set <paramref name="relativeLu"/> below the
    /// absolute-gated mean. Returns false when nothing is stored or nothing survives.
    /// </summary>
    public bool GatedMean(double relativeLu, out double meanSquare, out long count, out int firstBin)
    {
        meanSquare = 0; count = 0; firstBin = 0;
        long n = 0; double p = 0;
        for (int i = 0; i < Bins; i++) { n += _count[i]; p += _power[i]; }
        if (n == 0) return false;
        double threshold = ToLufs(p / n) + relativeLu;
        firstBin = BinOf(threshold) + 1;
        long n2 = 0; double p2 = 0;
        for (int i = firstBin; i < Bins; i++) { n2 += _count[i]; p2 += _power[i]; }
        if (n2 == 0) return false;
        meanSquare = p2 / n2;
        count = n2;
        return true;
    }

    /// <summary>Loudness of the k-th smallest stored value at or above <paramref name="firstBin"/> (0-based k).</summary>
    public double Select(int firstBin, long k)
    {
        long seen = 0;
        for (int i = firstBin; i < Bins; i++)
        {
            if (k < seen + _count[i]) return ToLufs(_power[i] / _count[i]);
            seen += _count[i];
        }
        return double.NegativeInfinity;
    }
}
