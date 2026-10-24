namespace OneAudio;

/// <summary>
/// How one bus ducks (is turned down by) the level of another. The detector watches the source bus after its own gain
/// and mute, so muting or fading the source also stops the ducking.
/// </summary>
public sealed record DuckingSettings
{
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
