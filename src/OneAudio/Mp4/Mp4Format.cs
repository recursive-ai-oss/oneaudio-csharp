namespace OneAudio.Mp4;

/// <summary>
/// ISO base media files: MP4, M4A, 3GP and 3G2 (ISO/IEC 14496-12, 3GPP TS 26.244). Opens the first audio track whose codec is
/// registered; <see cref="Mp4PacketSource"/> gives access to the others.
/// </summary>
public sealed class Mp4Format : IContainerFormat
{
    /// <summary>The shared instance.</summary>
    public static Mp4Format Instance { get; } = new();

    private static readonly string[] Exts = [".m4a", ".m4b", ".mp4", ".3gp", ".3g2", ".3gpp"];

    private Mp4Format() { }

    /// <inheritdoc />
    public string Name => "MP4";

    /// <inheritdoc />
    public IReadOnlyList<string> Extensions => Exts;

    /// <inheritdoc />
    public bool Matches(ReadOnlySpan<byte> head)
    {
        // The first box is ftyp in practice; a few writers start with the movie or media data box instead.
        if (head.Length < 12) return false;
        uint type = BoxHeader.Fourcc(head[4..]);
        return type is BoxHeader.Ftyp or BoxHeader.Moov or BoxHeader.Mdat;
    }

    /// <inheritdoc />
    public IAudioDecoder Open(Stream stream, AudioCodecRegistry registry, AudioLimits limits, bool leaveOpen, CancellationToken cancellationToken)
    {
        var source = Mp4PacketSource.Open(stream, limits, leaveOpen, cancellationToken);
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
