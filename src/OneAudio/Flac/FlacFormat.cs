namespace OneAudio.Flac;

/// <summary>
/// The native FLAC container (a <c>fLaC</c> marker, metadata blocks, then frames) as an <see cref="IContainerFormat"/>.
/// Register with <see cref="FlacRegistryExtensions.AddFlac"/>.
/// </summary>
public sealed class FlacFormat : IContainerFormat
{
    /// <summary>The shared instance.</summary>
    public static FlacFormat Instance { get; } = new();

    private FlacFormat() { }

    /// <inheritdoc />
    public string Name => "FLAC";

    /// <inheritdoc />
    public bool Matches(ReadOnlySpan<byte> head) => head.Length >= 4 && head[..4].SequenceEqual("fLaC"u8);

    /// <inheritdoc />
    public IAudioDecoder Open(Stream stream, AudioCodecRegistry registry, AudioLimits limits, bool leaveOpen, CancellationToken cancellationToken)
        => FlacDecoder.Open(stream, limits, leaveOpen, cancellationToken);
}

/// <summary>Turns FLAC packets carried by a container (Ogg FLAC) into a decoder.</summary>
public sealed class FlacCodec : IAudioCodec
{
    /// <summary>The shared instance.</summary>
    public static FlacCodec Instance { get; } = new();

    private FlacCodec() { }

    /// <inheritdoc />
    public CodecId Id => CodecId.Flac;

    /// <inheritdoc />
    public IAudioDecoder CreateDecoder(IPacketSource packets, AudioLimits limits) => FlacDecoder.Create(packets, limits);
}
