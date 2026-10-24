namespace OneAudio.Ogg;

/// <summary>
/// The Ogg container as an <see cref="IContainerFormat"/>: opens the stream as an <see cref="OggPacketSource"/> and hands
/// it to the codec registered for what it carries. Register with <see cref="OneAudio.OggRegistryExtensions.AddOgg"/>.
/// </summary>
public sealed class OggFormat : IContainerFormat
{
    /// <summary>The shared instance.</summary>
    public static OggFormat Instance { get; } = new();

    private OggFormat() { }

    /// <inheritdoc />
    public string Name => "Ogg";

    /// <inheritdoc />
    public bool Matches(ReadOnlySpan<byte> head) => head.Length >= 4 && head[..4].SequenceEqual("OggS"u8);

    /// <inheritdoc />
    public IAudioDecoder Open(Stream stream, AudioCodecRegistry registry, AudioLimits limits, bool leaveOpen, CancellationToken cancellationToken)
    {
        var source = OggPacketSource.Open(stream, limits, leaveOpen, cancellationToken);
        try
        {
            return registry.GetCodec(source.Codec).CreateDecoder(source, limits);
        }
        catch
        {
            // The caller keeps the stream when opening fails.
            source.Abandon();
            throw;
        }
    }
}
