namespace OneAudio;

/// <summary>
/// How one bus ducks (is turned down by) the level of another. The detector watches the source bus after its own gain
/// and mute (and its inserts), so muting or fading the source also stops the ducking.
/// <para>
/// <b>Gain curve.</b> The target's duck gain moves exponentially toward its goal (unity, or the depth set by
/// <see cref="ReductionDb"/>) with the time constants below, so it approaches the goal asymptotically and never quite reaches it.
/// After <see cref="ReleaseMs"/> * 4 milliseconds the gain has recovered all but 1.8% of the way (about 2% remains after 1 s with the default
/// 250 ms release); after one attack time constant 37% of the way remains. A bus ducked "fully" (a very large <see cref="ReductionDb"/>) therefore
/// decays toward silence but is only inaudible once the exponential has fallen far enough, and it takes about as long to come back.
/// </para>
/// </summary>
public sealed record DuckingSettings
{
    /// <summary>
    /// Settings that mean no ducking: pass them to <see cref="AudioMixer.SetDucking"/> to remove a link, which is the same as calling
    /// <see cref="AudioMixer.ClearDucking"/>.
    /// </summary>
    public static DuckingSettings None { get; } = new() { Enabled = false };

    /// <summary>False for <see cref="None"/>, true otherwise. When false the other values are ignored. Default true.</summary>
    public bool Enabled { get; init; } = true;

    /// <summary>Detector level, in dBFS of the loudest channel's sample magnitude, above which the target is turned down. Default -30.</summary>
    public float ThresholdDb { get; init; } = -30f;

    /// <summary>How far the target is turned down while the detector is over the threshold, in dB (positive; 12 means a gain of about 0.25). Default 12.</summary>
    public float ReductionDb { get; init; } = 12f;

    /// <summary>Time constant of the gain falling toward the ducked level, in milliseconds (about 63% of the way in this time). Default 10.</summary>
    public float AttackMs { get; init; } = 10f;

    /// <summary>Time constant of the gain returning to unity once the detector drops under the threshold, in milliseconds. Default 250.</summary>
    public float ReleaseMs { get; init; } = 250f;

    internal void Validate()
    {
        if (!float.IsFinite(ThresholdDb) || ThresholdDb > 24f || ThresholdDb < -150f) throw new ArgumentOutOfRangeException(nameof(ThresholdDb));
        if (!float.IsFinite(ReductionDb) || ReductionDb < 0f || ReductionDb > 120f) throw new ArgumentOutOfRangeException(nameof(ReductionDb));
        if (!float.IsFinite(AttackMs) || AttackMs < 0f || AttackMs > 60_000f) throw new ArgumentOutOfRangeException(nameof(AttackMs));
        if (!float.IsFinite(ReleaseMs) || ReleaseMs < 0f || ReleaseMs > 60_000f) throw new ArgumentOutOfRangeException(nameof(ReleaseMs));
    }
}
