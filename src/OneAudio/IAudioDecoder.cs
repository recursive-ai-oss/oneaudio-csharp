namespace OneAudio;

/// <summary>
/// A pull-model audio decoder. The caller owns the destination buffers; a decoder allocates nothing per read.
/// Positions and lengths are in frames (one sample per channel). Instances are not thread-safe: use one thread at a
/// time. Dispose releases the decoder and, unless the caller asked otherwise at open, the underlying stream.
/// </summary>
public interface IAudioDecoder : IDisposable
{
    /// <summary>The decoded format. Fixed for the life of the decoder.</summary>
    AudioFormat Format { get; }

    /// <summary>Total length in frames, or -1 when the source does not say (for example a live or unsized stream).</summary>
    long LengthFrames { get; }

    /// <summary>The index of the next frame <see cref="Read"/> will return, counted from the start of the audio.</summary>
    long PositionFrames { get; }

    /// <summary>
    /// True when <see cref="Seek"/> works. False for sources read from a non-seekable stream; there, <see cref="Seek"/>
    /// throws <see cref="NotSupportedException"/> and the only way back to the start is to reopen the source.
    /// </summary>
    bool CanSeek { get; }

    /// <summary>
    /// True when <see cref="ReadInteger"/> works: the source is lossless integer PCM and exact native samples are
    /// available. False for lossy sources and for floating-point sources.
    /// </summary>
    bool HasNativeIntegers { get; }

    /// <summary>
    /// Decodes up to <c>destination.Length / Format.Channels</c> frames as interleaved <see cref="float"/>.
    /// Nominal range is [-1, 1); values are never clipped or dithered by the decoder (floating-point sources and
    /// lossy codecs may exceed the range). Trailing samples that do not fill a whole frame are left untouched.
    /// </summary>
    /// <param name="destination">Interleaved output. Its length should be a multiple of the channel count.</param>
    /// <returns>Frames written. May be fewer than requested (a short read is not the end); 0 means end of stream,
    /// or that <paramref name="destination"/> holds less than one frame.</returns>
    /// <exception cref="AudioFormatException">The encoded data is invalid.</exception>
    /// <exception cref="ObjectDisposedException">The decoder was disposed.</exception>
    int Read(Span<float> destination);

    /// <summary>
    /// Decodes up to <c>destination.Length / Format.Channels</c> frames as the source's exact native integers
    /// (see <see cref="NativeSampleType"/> for the value range of each type), interleaved. Mixing calls to this and
    /// <see cref="Read"/> is allowed; both advance the same position.
    /// </summary>
    /// <param name="destination">Interleaved output.</param>
    /// <returns>Frames written, with the same short-read and end-of-stream rules as <see cref="Read"/>.</returns>
    /// <exception cref="NotSupportedException"><see cref="HasNativeIntegers"/> is false.</exception>
    int ReadInteger(Span<int> destination);

    /// <summary>
    /// Moves so the next read starts at <paramref name="frame"/>. Seeking is sample-accurate: the following read
    /// returns exactly the samples a sequential decode would have returned at that position. Seeking to
    /// <see cref="LengthFrames"/> is allowed and leaves the decoder at the end.
    /// </summary>
    /// <param name="frame">Target frame, from 0 up to <see cref="LengthFrames"/> when the length is known.</param>
    /// <exception cref="NotSupportedException"><see cref="CanSeek"/> is false.</exception>
    /// <exception cref="ArgumentOutOfRangeException">The frame is negative or beyond a known length.</exception>
    void Seek(long frame);
}
