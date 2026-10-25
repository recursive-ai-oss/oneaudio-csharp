namespace OneAudio.Loudness;

/// <summary>
/// A consistent set of loudness readings taken from one point in time of a running meter, such as a mixer bus meter
/// (<see cref="AudioMixer.TryGetBusLoudness"/>). All fields come from the same render pass; they are never mixed from two passes.
/// Values that have no data yet are <see cref="double.NegativeInfinity"/>, as in <see cref="LoudnessMeter"/>.
/// </summary>
/// <param name="MomentaryLufs">Momentary loudness (400 ms window, ungated) in LUFS.</param>
/// <param name="ShortTermLufs">Short-term loudness (3 s window, ungated) in LUFS.</param>
/// <param name="IntegratedLufs">Gated integrated loudness since the meter was enabled or reset, in LUFS. Updated every 100 ms of measured audio, like the gating blocks it is made of.</param>
/// <param name="TruePeakDbtp">Largest true peak on any channel since the meter was enabled or reset, in dBTP.</param>
/// <param name="MaxMomentaryLufs">Highest momentary loudness since the meter was enabled or reset, in LUFS.</param>
/// <param name="MaxShortTermLufs">Highest short-term loudness since the meter was enabled or reset, in LUFS.</param>
/// <param name="FramesMeasured">Frames fed to the meter since it was enabled or reset (0 right after).</param>
public readonly record struct LoudnessSnapshot(
    double MomentaryLufs,
    double ShortTermLufs,
    double IntegratedLufs,
    double TruePeakDbtp,
    double MaxMomentaryLufs,
    double MaxShortTermLufs,
    long FramesMeasured)
{
    /// <summary>The reading of a meter that has measured nothing yet: every level is negative infinity and no frames were measured.</summary>
    public static LoudnessSnapshot Empty { get; } = new(
        double.NegativeInfinity, double.NegativeInfinity, double.NegativeInfinity,
        double.NegativeInfinity, double.NegativeInfinity, double.NegativeInfinity, 0);
}
