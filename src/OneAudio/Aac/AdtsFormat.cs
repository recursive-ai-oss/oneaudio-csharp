namespace OneAudio.Aac;

/// <summary>Raw AAC in ADTS framing (<c>.aac</c>); see <see cref="AdtsPacketSource"/>.</summary>
public sealed class AdtsFormat : IContainerFormat
{
    /// <summary>The shared instance.</summary>
    public static AdtsFormat Instance { get; } = new();

    private static readonly string[] Exts = [".aac", ".adts"];

    private AdtsFormat() { }

    /// <inheritdoc />
    public string Name => "ADTS";

    /// <inheritdoc />
    public IReadOnlyList<string> Extensions => Exts;

    /// <summary>
    /// True when the data starts (after an optional ID3v2 tag that ends within the first bytes) with a valid ADTS header, and, if the
    /// bytes are there, the next frame's header follows right after. Layer bits 00 keep MP3 files from matching.
    /// </summary>
    /// <param name="head">The start of the stream.</param>
    public bool Matches(ReadOnlySpan<byte> head)
    {
        int pos = 0;
        if (head.Length >= 10 && head[0] == 'I' && head[1] == 'D' && head[2] == '3')
        {
            long size = (head[6] << 21) | (head[7] << 14) | (head[8] << 7) | head[9];
            if ((head[5] & 0x10) != 0) size += 10;
            if (10 + size >= head.Length) return false;
            pos = 10 + (int)size;
        }
        if (!AdtsPacketSource.AdtsHeader.TryParse(head[pos..], out var h)) return false;
        int next = pos + h.FrameLength;
        if (next + 7 > head.Length) return true;
        return AdtsPacketSource.AdtsHeader.TryParse(head[next..], out _);
    }

    /// <inheritdoc />
    public IAudioDecoder Open(Stream stream, AudioCodecRegistry registry, AudioLimits limits, bool leaveOpen, CancellationToken cancellationToken)
    {
        var source = AdtsPacketSource.Open(stream, limits, leaveOpen, cancellationToken);
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
