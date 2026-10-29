namespace OneAudio;

/// <summary>
/// Upper bounds on what untrusted input may make the library allocate or loop over. Every size read from a file is
/// checked against these before the allocation it guards; a violation throws <see cref="AudioLimitExceededException"/>.
/// Instances are immutable; use <c>with</c> to derive a variant. The defaults suit games and tools reading ordinary
/// files; raise them deliberately for unusual material.
/// </summary>
public sealed record AudioLimits
{
    /// <summary>The default limits.</summary>
    public static AudioLimits Default { get; } = new();

    /// <summary>Most channels per frame. Default 32.</summary>
    public int MaxChannels { get; init; } = 32;

    /// <summary>Highest sample rate in Hz. Default 768000.</summary>
    public int MaxSampleRate { get; init; } = 768_000;

    /// <summary>Largest single compressed packet or container page payload, in bytes. Default 4 MiB.</summary>
    public int MaxPacketBytes { get; init; } = 4 * 1024 * 1024;

    /// <summary>Largest decoded block a codec may produce per call, in frames. Default 1,048,576.</summary>
    public int MaxFrameSize { get; init; } = 1 << 20;

    /// <summary>Most bytes of metadata (tags, comments, header chunks) read into memory for one item. Default 1 MiB.</summary>
    public int MaxMetadataBytes { get; init; } = 1 << 20;

    /// <summary>Largest codec setup structure (for example codebook tables), in bytes after expansion. Default 8 MiB.</summary>
    public int MaxSetupBytes { get; init; } = 8 * 1024 * 1024;

    /// <summary>Most chunks, pages or similar container elements walked while looking for what is needed. Default 100,000.</summary>
    public int MaxContainerElements { get; init; } = 100_000;

    /// <summary>
    /// Most samples (packets) one MP4 track may have, and the most entries any one sample-table box may declare. Default 8,388,608
    /// (about 46 hours of 20 ms AMR frames, or 53 hours of 1024-sample AAC frames at 44.1 kHz). The tables of all audio tracks
    /// in one file together may hold four times this many entries.
    /// </summary>
    public int MaxSamples { get; init; } = 8 * 1024 * 1024;

    /// <summary>Throws if <paramref name="channels"/> is outside 1..<see cref="MaxChannels"/>.</summary>
    /// <param name="channels">Channel count read from input.</param>
    /// <exception cref="AudioFormatException">The count is not positive.</exception>
    /// <exception cref="AudioLimitExceededException">The count exceeds the limit.</exception>
    public void CheckChannels(long channels)
    {
        if (channels < 1) throw new AudioFormatException("Channel count must be at least 1.");
        if (channels > MaxChannels) throw new AudioLimitExceededException(nameof(MaxChannels), MaxChannels, channels);
    }

    /// <summary>Throws if <paramref name="sampleRate"/> is outside 1..<see cref="MaxSampleRate"/>.</summary>
    /// <param name="sampleRate">Sample rate read from input, in Hz.</param>
    /// <exception cref="AudioFormatException">The rate is not positive.</exception>
    /// <exception cref="AudioLimitExceededException">The rate exceeds the limit.</exception>
    public void CheckSampleRate(long sampleRate)
    {
        if (sampleRate < 1) throw new AudioFormatException("Sample rate must be at least 1 Hz.");
        if (sampleRate > MaxSampleRate) throw new AudioLimitExceededException(nameof(MaxSampleRate), MaxSampleRate, sampleRate);
    }

    /// <summary>Throws if a packet of <paramref name="bytes"/> exceeds <see cref="MaxPacketBytes"/>.</summary>
    /// <param name="bytes">Packet size read from input.</param>
    /// <exception cref="AudioLimitExceededException">The size exceeds the limit.</exception>
    public void CheckPacketBytes(long bytes)
    {
        if (bytes > MaxPacketBytes) throw new AudioLimitExceededException(nameof(MaxPacketBytes), MaxPacketBytes, bytes);
    }

    /// <summary>Throws if a block of <paramref name="frames"/> exceeds <see cref="MaxFrameSize"/>.</summary>
    /// <param name="frames">Block size in frames read from input.</param>
    /// <exception cref="AudioLimitExceededException">The size exceeds the limit.</exception>
    public void CheckFrameSize(long frames)
    {
        if (frames > MaxFrameSize) throw new AudioLimitExceededException(nameof(MaxFrameSize), MaxFrameSize, frames);
    }

    /// <summary>Throws if <paramref name="bytes"/> of metadata exceed <see cref="MaxMetadataBytes"/>.</summary>
    /// <param name="bytes">Metadata size read from input.</param>
    /// <exception cref="AudioLimitExceededException">The size exceeds the limit.</exception>
    public void CheckMetadataBytes(long bytes)
    {
        if (bytes > MaxMetadataBytes) throw new AudioLimitExceededException(nameof(MaxMetadataBytes), MaxMetadataBytes, bytes);
    }

    /// <summary>Throws if <paramref name="bytes"/> of codec setup exceed <see cref="MaxSetupBytes"/>.</summary>
    /// <param name="bytes">Setup size computed from input.</param>
    /// <exception cref="AudioLimitExceededException">The size exceeds the limit.</exception>
    public void CheckSetupBytes(long bytes)
    {
        if (bytes > MaxSetupBytes) throw new AudioLimitExceededException(nameof(MaxSetupBytes), MaxSetupBytes, bytes);
    }

    /// <summary>Throws if <paramref name="count"/> samples or table entries exceed <see cref="MaxSamples"/>.</summary>
    /// <param name="count">Count read from input.</param>
    /// <exception cref="AudioLimitExceededException">The count exceeds the limit.</exception>
    public void CheckSamples(long count)
    {
        if (count > MaxSamples) throw new AudioLimitExceededException(nameof(MaxSamples), MaxSamples, count);
    }

    /// <summary>Throws if <paramref name="count"/> container elements exceed <see cref="MaxContainerElements"/>.</summary>
    /// <param name="count">Elements walked so far.</param>
    /// <exception cref="AudioLimitExceededException">The count exceeds the limit.</exception>
    public void CheckContainerElements(long count)
    {
        if (count > MaxContainerElements) throw new AudioLimitExceededException(nameof(MaxContainerElements), MaxContainerElements, count);
    }
}
