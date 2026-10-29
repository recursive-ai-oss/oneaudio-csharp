namespace OneAudio.Amr;

/// <summary>AMR and AMR-WB storage-format files (<c>.amr</c>, <c>.awb</c>, RFC 4867 section 5); see <see cref="AmrPacketSource"/>.</summary>
public sealed class AmrFormat : IContainerFormat
{
    /// <summary>The shared instance.</summary>
    public static AmrFormat Instance { get; } = new();

    private static readonly string[] Exts = [".amr", ".awb"];

    private AmrFormat() { }

    /// <inheritdoc />
    public string Name => "AMR";

    /// <inheritdoc />
    public IReadOnlyList<string> Extensions => Exts;

    /// <inheritdoc />
    public bool Matches(ReadOnlySpan<byte> head) => AmrPacketSource.Identify(head, out _, out _) > 0;

    /// <inheritdoc />
    public IAudioDecoder Open(Stream stream, AudioCodecRegistry registry, AudioLimits limits, bool leaveOpen, CancellationToken cancellationToken)
    {
        var source = AmrPacketSource.Open(stream, limits, leaveOpen, cancellationToken);
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
