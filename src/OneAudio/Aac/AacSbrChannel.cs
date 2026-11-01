namespace OneAudio.Aac;

/// <summary>
/// The QMF-domain state of one SBR channel (ISO/IEC 14496-3 4.6.18): the complex slot buffers, the analysis and synthesis
/// filterbanks, and the two stages that run between them, the transposer and the envelope adjuster. The slot buffers
/// hold six slots carried over from the previous frame followed by the 32 slots of the current one; both stages work
/// on the whole buffer, as the standard's delay model requires.
/// </summary>
internal sealed class SbrChannel
{
    internal const int Cols = 32, Overlap = 6, Slots = Cols + Overlap, Bands = 64;

    public readonly float[][] Re = NewBuffer();
    public readonly float[][] Im = NewBuffer();
    public readonly SbrQmfAnalysis Analysis = new();
    public readonly SbrQmfSynthesis Synthesis;
    private readonly SbrTransposer _transposer = new();
    private readonly SbrAdjuster _adjuster = new();

    public SbrChannel(int synthBands) => Synthesis = new SbrQmfSynthesis(synthBands);

    private static float[][] NewBuffer()
    {
        var b = new float[Slots][];
        for (int i = 0; i < Slots; i++) b[i] = new float[Bands];
        return b;
    }

    public void Reset()
    {
        Analysis.Reset();
        Synthesis.Reset();
        foreach (var r in Re) Array.Clear(r);
        foreach (var r in Im) Array.Clear(r);
        _transposer.Reset();
        _adjuster.Reset();
    }

    /// <summary>Restarts the envelope adjuster after a header with a new band layout.</summary>
    public void ResetEnvelope() => _adjuster.Restart();

    /// <summary>
    /// Clears carried-over slots and transposer history between the old and new crossover when the band layout changes
    /// (4.6.18.2.1 header reset): the old lower edge is <paramref name="oldLsb"/>, the new one <paramref name="newLsb"/>.
    /// </summary>
    public void ResetQmfRange(int oldLsb, int newLsb, int prevStopPos, bool first)
    {
        int startSlot = 2 * (prevStopPos - 16);
        for (int l = Math.Max(0, startSlot); l < Overlap; l++)
            for (int k = oldLsb; k < newLsb; k++) { Re[l][k] = 0; Im[l][k] = 0; }
        if (first) _transposer.ClearHistory(Math.Min(oldLsb, newLsb), Math.Max(oldLsb, newLsb));
    }

    /// <summary>Keeps the transposer's history current in a frame where no high band is generated.</summary>
    public void SaveLpcState() => _transposer.SaveHistory(Re, Im, 32);

    /// <summary>Carries the last six slots of the buffer to its start (the QMF overlap).</summary>
    public void ShiftOverlap()
    {
        for (int i = 0; i < Overlap; i++)
        {
            Array.Copy(Re[i + Cols], Re[i], Bands);
            Array.Copy(Im[i + Cols], Im[i], Bands);
        }
    }

    /// <summary>4.6.18.6: generates the high band from the low band.</summary>
    public void Transpose(SbrBands b, SbrFrame f, SbrPrev prev) => _transposer.Run(Re, Im, b, f, prev);

    /// <summary>4.6.18.7: scales the generated high band to the transmitted envelope and adds noise and sinusoids.</summary>
    public void Adjust(SbrBands b, SbrHeader h, SbrFrame f) => _adjuster.Run(Re, Im, b, h, f);
}
