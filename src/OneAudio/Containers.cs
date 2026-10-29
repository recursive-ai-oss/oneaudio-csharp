namespace OneAudio;

/// <summary>Identifies a compressed codec, as carried by a container. Compare by <see cref="Name"/>.</summary>
/// <param name="Name">Short lowercase name, for example <c>vorbis</c>.</param>
public readonly record struct CodecId(string Name)
{
    /// <summary>Xiph Vorbis I.</summary>
    public static CodecId Vorbis => new("vorbis");
    /// <summary>Opus (RFC 6716).</summary>
    public static CodecId Opus => new("opus");
    /// <summary>FLAC (RFC 9639).</summary>
    public static CodecId Flac => new("flac");
    /// <summary>MPEG audio Layer III.</summary>
    public static CodecId Mp3 => new("mp3");
    /// <summary>Adaptive Multi-Rate narrowband speech, 8 kHz (3GPP TS 26.071). Packets are a table-of-contents byte followed by the speech bits.</summary>
    public static CodecId AmrNb => new("amr-nb");
    /// <summary>Adaptive Multi-Rate wideband speech, 16 kHz (3GPP TS 26.171). Packets are a table-of-contents byte followed by the speech bits.</summary>
    public static CodecId AmrWb => new("amr-wb");

    /// <inheritdoc />
    public override string ToString() => Name;
}

/// <summary>One compressed packet delivered by an <see cref="IPacketSource"/>.</summary>
/// <param name="Data">The packet bytes. Valid only until the next call on the source that produced it.</param>
/// <param name="EndFrame">
/// The position, in output frames at the codec's rate, at the end of this packet when the container knows it
/// (for example an Ogg granule position), otherwise -1. Codecs use it to trim end padding and to validate seeks.
/// </param>
/// <param name="IsHeader">True for codec header packets (identification, comments, setup) that precede audio.</param>
public readonly ref struct PacketView(ReadOnlySpan<byte> Data, long EndFrame, bool IsHeader)
{
    /// <summary>The packet bytes.</summary>
    public ReadOnlySpan<byte> Data { get; } = Data;
    /// <summary>End position in output frames, or -1 when unknown.</summary>
    public long EndFrame { get; } = EndFrame;
    /// <summary>True for header packets.</summary>
    public bool IsHeader { get; } = IsHeader;
}

/// <summary>
/// What a container (Ogg, and later others) hands to a codec: an ordered sequence of packets for one audio stream,
/// header packets first. The container does framing, checksums and seeking by position; the codec does everything
/// inside the packets. Implementations allocate their buffers when opened (bounded by <see cref="AudioLimits"/>),
/// not per packet. Not thread-safe.
/// </summary>
public interface IPacketSource : IDisposable
{
    /// <summary>Which codec the packets belong to.</summary>
    CodecId Codec { get; }

    /// <summary>Total length in output frames when the container records it, else -1.</summary>
    long LengthFrames { get; }

    /// <summary>True when <see cref="SeekToFrame"/> works (the underlying stream is seekable).</summary>
    bool CanSeek { get; }

    /// <summary>
    /// Reads the next packet. The returned data is valid until the next call on this source.
    /// </summary>
    /// <param name="packet">The packet.</param>
    /// <returns>False at the end of the stream.</returns>
    /// <exception cref="AudioFormatException">The container framing is invalid.</exception>
    bool TryReadPacket(out PacketView packet);

    /// <summary>
    /// Repositions so the next packet read is the first one the codec needs to decode output at or after
    /// <paramref name="frame"/>, which may be earlier in the stream (codecs with overlap or prediction need history).
    /// </summary>
    /// <param name="frame">Target output frame.</param>
    /// <param name="startFrame">Output frame at which the first returned packet's audio begins; the codec decodes
    /// from there and discards <c>frame - startFrame</c> frames to land exactly on target.</param>
    /// <exception cref="NotSupportedException"><see cref="CanSeek"/> is false.</exception>
    void SeekToFrame(long frame, out long startFrame);
}

/// <summary>Turns a packet stream of one codec into an <see cref="IAudioDecoder"/>. Registered explicitly so unused codecs trim away.</summary>
public interface IAudioCodec
{
    /// <summary>The codec this factory decodes.</summary>
    CodecId Id { get; }

    /// <summary>
    /// Reads the codec's header packets from <paramref name="packets"/> and returns a decoder over the rest.
    /// The decoder takes ownership of <paramref name="packets"/> and disposes it.
    /// </summary>
    /// <param name="packets">Packets of this codec, positioned at the first header.</param>
    /// <param name="limits">Limits to enforce before allocating.</param>
    /// <exception cref="AudioFormatException">The headers are invalid.</exception>
    /// <exception cref="AudioLimitExceededException">A header asks for more than <paramref name="limits"/> allow.</exception>
    IAudioDecoder CreateDecoder(IPacketSource packets, AudioLimits limits);
}

/// <summary>Recognises one container format and opens it. Registered explicitly so unused formats trim away.</summary>
public interface IContainerFormat
{
    /// <summary>Human-readable format name, for diagnostics.</summary>
    string Name { get; }

    /// <summary>
    /// Cheap check on the first bytes of a stream. May only look at <paramref name="head"/> (at most
    /// <see cref="AudioCodecRegistry.SniffBytes"/> bytes, fewer for a short file).
    /// </summary>
    /// <param name="head">The start of the stream.</param>
    bool Matches(ReadOnlySpan<byte> head);

    /// <summary>
    /// Opens the stream, positioned at its start, as a decoder. Containers that carry compressed packets build an
    /// <see cref="IPacketSource"/> and pick the codec from <paramref name="registry"/>; containers that carry raw
    /// PCM (WAV) decode directly.
    /// </summary>
    /// <param name="stream">Source stream. The returned decoder owns it when <paramref name="leaveOpen"/> is false.</param>
    /// <param name="registry">Registered codecs.</param>
    /// <param name="limits">Limits to enforce before allocating.</param>
    /// <param name="leaveOpen">Keep the stream open when the decoder is disposed.</param>
    /// <param name="cancellationToken">Observed while scanning headers.</param>
    /// <exception cref="AudioFormatException">The data is invalid.</exception>
    /// <exception cref="UnsupportedAudioFormatException">The data needs a codec that is not registered.</exception>
    IAudioDecoder Open(Stream stream, AudioCodecRegistry registry, AudioLimits limits, bool leaveOpen, CancellationToken cancellationToken);
}
