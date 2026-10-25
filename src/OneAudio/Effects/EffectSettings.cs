namespace OneAudio;

/// <summary>Response of a mixer filter.</summary>
public enum FilterType
{
    /// <summary>Passes frequencies below the cutoff (second order, 12 dB per octave).</summary>
    LowPass = 0,
    /// <summary>Passes frequencies above the cutoff (second order, 12 dB per octave).</summary>
    HighPass = 1,
}

/// <summary>
/// Settings of the reverb insert on a bus (<see cref="AudioMixer.SetBusReverb"/>): an eight-line feedback delay network
/// (Jot and Chaigne, 1991) behind four Schroeder allpass diffusers and a pre-delay. Decay time is set directly, so the
/// tail length does not change with <see cref="RoomSize"/>.
/// </summary>
public sealed record ReverbSettings
{
    /// <summary>Lowest accepted <see cref="DecaySeconds"/>.</summary>
    public const float MinDecaySeconds = 0.05f;

    /// <summary>Highest accepted <see cref="DecaySeconds"/>.</summary>
    public const float MaxDecaySeconds = 30f;

    /// <summary>Highest accepted <see cref="PreDelayMilliseconds"/>.</summary>
    public const float MaxPreDelayMilliseconds = 200f;

    /// <summary>
    /// Apparent size of the room, 0 to 1: scales the delay-line lengths between 25% and 100% of their maximum (about 6 to 79 ms).
    /// Small rooms sound denser and more metallic, large ones sparser. Changes glide over the ramp and bend the pitch of the
    /// ringing tail while they move, so set it before use where you can. Default 0.5.
    /// </summary>
    public float RoomSize { get; init; } = 0.5f;

    /// <summary>
    /// Time for the tail to fall by 60 dB (RT60) at low frequencies, in seconds, from <see cref="MinDecaySeconds"/> to
    /// <see cref="MaxDecaySeconds"/>. Default 1.5.
    /// </summary>
    public float DecaySeconds { get; init; } = 1.5f;

    /// <summary>
    /// Absorption of high frequencies, 0 to 1. At 0 every frequency decays in <see cref="DecaySeconds"/>; at 1 the decay time at
    /// the Nyquist frequency is a tenth of it. Default 0.4.
    /// </summary>
    public float Damping { get; init; } = 0.4f;

    /// <summary>Delay before the tail starts, in milliseconds, 0 to <see cref="MaxPreDelayMilliseconds"/>. Default 0.</summary>
    public float PreDelayMilliseconds { get; init; }

    /// <summary>
    /// Linear gain of the reverberated signal added to the output, 0 to 4. The wet path is scaled so that its level is roughly
    /// the level of the input whatever the decay time, so 1 is a very wet mix. Default 0.25.
    /// </summary>
    public float Wet { get; init; } = 0.25f;

    /// <summary>Linear gain of the unprocessed signal in the output, 0 to 4. Default 1.</summary>
    public float Dry { get; init; } = 1f;

    internal void Validate()
    {
        Check(RoomSize, 0f, 1f, nameof(RoomSize));
        Check(DecaySeconds, MinDecaySeconds, MaxDecaySeconds, nameof(DecaySeconds));
        Check(Damping, 0f, 1f, nameof(Damping));
        Check(PreDelayMilliseconds, 0f, MaxPreDelayMilliseconds, nameof(PreDelayMilliseconds));
        Check(Wet, 0f, 4f, nameof(Wet));
        Check(Dry, 0f, 4f, nameof(Dry));
    }

    private static void Check(float v, float lo, float hi, string name)
    {
        if (!(v >= lo && v <= hi)) throw new ArgumentOutOfRangeException(name, $"Must be within {lo} to {hi}.");
    }
}

/// <summary>
/// Settings of the master limiter (<see cref="AudioMixer.SetLimiter"/>). The limiter looks ahead by
/// <see cref="MixerOptions.LimiterLookaheadMilliseconds"/>, which fixes its latency (<see cref="AudioMixer.LatencyFrames"/>).
/// </summary>
public sealed record LimiterSettings
{
    /// <summary>Highest sample magnitude allowed at the output, in dBFS, -60 to 0. Default -1.</summary>
    public float CeilingDb { get; init; } = -1f;

    /// <summary>
    /// Time constant with which the gain recovers toward unity after a peak, in milliseconds, 1 to 10000. The reduction itself
    /// is applied gradually over the lookahead, before the peak arrives. Default 100.
    /// </summary>
    public float ReleaseMilliseconds { get; init; } = 100f;

    internal void Validate()
    {
        if (!(CeilingDb >= -60f && CeilingDb <= 0f)) throw new ArgumentOutOfRangeException(nameof(CeilingDb), "Must be within -60 to 0 dBFS.");
        if (!(ReleaseMilliseconds >= 1f && ReleaseMilliseconds <= 10_000f)) throw new ArgumentOutOfRangeException(nameof(ReleaseMilliseconds), "Must be within 1 to 10000 ms.");
    }
}
