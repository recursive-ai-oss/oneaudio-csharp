namespace OneAudio;

/// <summary>The format-detecting entry point for decoding.</summary>
public static class AudioDecoder
{
    /// <summary>
    /// Detects the container from the first bytes of <paramref name="stream"/> and opens it as a decoder.
    /// Seekable streams are rewound after detection; non-seekable streams are read through a small replay buffer, and
    /// the resulting decoder reports <see cref="IAudioDecoder.CanSeek"/> false.
    /// </summary>
    /// <param name="stream">Readable stream positioned at the start of the audio file. Seekable streams are used from their current position.</param>
    /// <param name="registry">The formats and codecs to try.</param>
    /// <param name="limits">Limits for untrusted input; <see cref="AudioLimits.Default"/> when null.</param>
    /// <param name="leaveOpen">When false (default) disposing the decoder disposes the stream. If this method throws, the stream is left open for the caller.</param>
    /// <param name="cancellationToken">Observed while scanning headers.</param>
    /// <returns>A decoder positioned at frame 0.</returns>
    /// <exception cref="UnsupportedAudioFormatException">No registered container recognises the data.</exception>
    /// <exception cref="AudioFormatException">The data is invalid.</exception>
    /// <exception cref="AudioLimitExceededException">The data exceeds <paramref name="limits"/>.</exception>
    /// <exception cref="ArgumentException">The stream is not readable.</exception>
    public static IAudioDecoder Open(
        Stream stream,
        AudioCodecRegistry registry,
        AudioLimits? limits = null,
        bool leaveOpen = false,
        CancellationToken cancellationToken = default)
        => OpenCore(stream, registry, limits, leaveOpen, null, cancellationToken);

    private static IAudioDecoder OpenCore(
        Stream stream,
        AudioCodecRegistry registry,
        AudioLimits? limits,
        bool leaveOpen,
        string? extension,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(stream);
        ArgumentNullException.ThrowIfNull(registry);
        if (!stream.CanRead) throw new ArgumentException("Stream must be readable.", nameof(stream));
        limits ??= AudioLimits.Default;

        Span<byte> head = stackalloc byte[AudioCodecRegistry.SniffBytes];
        Stream source = stream;
        int n;
        if (stream.CanSeek)
        {
            long start = stream.Position;
            n = stream.ReadAtLeast(head, head.Length, throwOnEndOfStream: false);
            stream.Position = start;
        }
        else
        {
            n = stream.ReadAtLeast(head, head.Length, throwOnEndOfStream: false);
            source = new ReplayStream(stream, head[..n].ToArray());
        }

        var format = registry.Detect(head[..n])
            ?? registry.DetectByExtension(extension)
            ?? throw new UnsupportedAudioFormatException("No registered container format recognises this data.");
        // Replay wrapper must not close the caller's stream if the caller said leaveOpen, or if opening fails.
        return format.Open(source, registry, limits, leaveOpen, cancellationToken);
    }

    /// <summary>
    /// Opens a file by path with <see cref="Open"/>; when the start of the file is not recognised, the file name extension picks the
    /// format (see <see cref="AudioCodecRegistry.DetectByExtension"/>). The decoder owns the file stream.
    /// </summary>
    /// <param name="path">File path.</param>
    /// <param name="registry">The formats and codecs to try.</param>
    /// <param name="limits">Limits for untrusted input; <see cref="AudioLimits.Default"/> when null.</param>
    /// <param name="cancellationToken">Observed while scanning headers.</param>
    /// <returns>A decoder positioned at frame 0.</returns>
    public static IAudioDecoder OpenFile(string path, AudioCodecRegistry registry, AudioLimits? limits = null, CancellationToken cancellationToken = default)
    {
        var fs = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read, 4096, FileOptions.SequentialScan);
        try { return OpenCore(fs, registry, limits, false, Path.GetExtension(path), cancellationToken); }
        catch { fs.Dispose(); throw; }
    }
}

/// <summary>Replays bytes already consumed for format detection, then continues with the wrapped stream.</summary>
internal sealed class ReplayStream(Stream inner, byte[] prefix) : Stream
{
    private int _pos;
    public override bool CanRead => true;
    public override bool CanSeek => false;
    public override bool CanWrite => false;
    public override long Length => throw new NotSupportedException();
    public override long Position { get => throw new NotSupportedException(); set => throw new NotSupportedException(); }
    public override void Flush() { }
    public override int Read(byte[] buffer, int offset, int count) => Read(buffer.AsSpan(offset, count));
    public override int Read(Span<byte> buffer)
    {
        if (_pos < prefix.Length)
        {
            int n = Math.Min(buffer.Length, prefix.Length - _pos);
            prefix.AsSpan(_pos, n).CopyTo(buffer);
            _pos += n;
            return n;
        }
        return inner.Read(buffer);
    }
    public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
    public override void SetLength(long value) => throw new NotSupportedException();
    public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();
    protected override void Dispose(bool disposing)
    {
        if (disposing) inner.Dispose();
        base.Dispose(disposing);
    }
}
