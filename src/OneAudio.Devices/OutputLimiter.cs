namespace OneAudio.Devices;

/// <summary>Keeps float samples inside the range a device accepts. Not-a-number becomes 0 in both modes.</summary>
public static class OutputLimiter
{
    private const float Knee = 0.9f;

    /// <summary>Limits one sample.</summary>
    /// <param name="x">The sample.</param>
    /// <param name="mode">The limiter.</param>
    /// <returns>The sample inside -1..1.</returns>
    public static float Apply(float x, OutputLimiterMode mode)
    {
        if (float.IsNaN(x))
        {
            return 0f;
        }

        if (mode == OutputLimiterMode.Clamp)
        {
            return x > 1f ? 1f : x < -1f ? -1f : x;
        }

        float a = MathF.Abs(x);
        if (a <= Knee)
        {
            return x;
        }

        // tanh maps the overshoot onto the last tenth of headroom, so the output approaches 1 smoothly and never reaches it.
        float limited = Knee + ((1f - Knee) * MathF.Tanh((a - Knee) / (1f - Knee)));
        return x < 0f ? -limited : limited;
    }

    /// <summary>Limits a buffer in place and returns the largest absolute value left in it.</summary>
    /// <param name="samples">The samples.</param>
    /// <param name="mode">The limiter.</param>
    /// <returns>The peak after limiting.</returns>
    public static float Apply(Span<float> samples, OutputLimiterMode mode)
    {
        float peak = 0f;
        for (int i = 0; i < samples.Length; i++)
        {
            float x = samples[i];
            float a = MathF.Abs(x);

            // The common case (inside the knee) takes this one comparison; NaN fails it and falls through to the slow path.
            if (!(a <= Knee))
            {
                x = Apply(x, mode);
                samples[i] = x;
                a = MathF.Abs(x);
            }

            if (a > peak)
            {
                peak = a;
            }
        }

        return peak;
    }
}
