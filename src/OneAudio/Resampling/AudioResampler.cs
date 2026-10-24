namespace OneAudio;

/// <summary>Bounds that size a resampler's preallocated state.</summary>
public sealed record ResamplerOptions
{
    /// <summary>
    /// Lowest ratio (output rate / input rate) the resampler will accept. Default 0.25. The polyphase filter has to
    /// widen as the ratio falls, so memory scales with 1 / MinRatio: the coefficient bank of
    /// <see cref="ResamplerQuality.Polyphase"/> takes about 513 x 144 / min(1, MinRatio) floats (about 300 KB at 1.0,
    /// 1.2 MB at 0.25). Raise it (for example to 1.0 for voices that are only ever slowed down or kept at the source rate)
    /// to save memory. Range 1/64 to <see cref="MaxRatio"/>.
    /// </summary>
    public double MinRatio { get; init; } = 0.25;

    /// <summary>Highest ratio accepted. Default 16. Costs no extra memory for polyphase; the output just gets longer. Range <see cref="MinRatio"/> to 64.</summary>
    public double MaxRatio { get; init; } = 16;
}

/// <summary>Creates streaming sample-rate converters.</summary>
public static class AudioResampler
{
    /// <summary>
    /// Creates a resampler. Everything it will ever need is allocated here (including, for the polyphase modes, the
    /// shared filter prototype on first use), so <see cref="IResampler.Process"/>, <see cref="IResampler.SetRatio"/>,
    /// <see cref="IResampler.Flush"/> and <see cref="IResampler.Reset"/> allocate nothing.
    /// </summary>
    /// <param name="channels">Interleaved channel count, at least 1 and at most <see cref="AudioLimits.MaxChannels"/> of <see cref="AudioLimits.Default"/>.</param>
    /// <param name="quality">The mode.</param>
    /// <param name="ratio">Initial output rate divided by input rate; within the options' range.</param>
    /// <param name="options">Bounds that size the preallocated state; <see langword="null"/> for the defaults.</param>
    /// <exception cref="ArgumentOutOfRangeException">An argument is out of range.</exception>
    public static IResampler Create(int channels, ResamplerQuality quality, double ratio, ResamplerOptions? options = null)
        => new Resampling.StreamResampler(channels, quality, ratio, options ?? new ResamplerOptions());

    /// <summary>Creates a resampler converting from <paramref name="inputRate"/> to <paramref name="outputRate"/> Hz (ratio = output / input).</summary>
    /// <param name="inputRate">Source sample rate in Hz.</param>
    /// <param name="outputRate">Target sample rate in Hz.</param>
    /// <param name="channels">Interleaved channel count.</param>
    /// <param name="quality">The mode.</param>
    /// <param name="options">Bounds that size the preallocated state; <see langword="null"/> for the defaults.</param>
    /// <exception cref="ArgumentOutOfRangeException">An argument is out of range.</exception>
    public static IResampler Create(int inputRate, int outputRate, int channels, ResamplerQuality quality, ResamplerOptions? options = null)
    {
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(inputRate);
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(outputRate);
        return Create(channels, quality, (double)outputRate / inputRate, options);
    }
}
