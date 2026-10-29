namespace OneAudio.Amr;

/// <summary>
/// AMR and AMR-WB storage-format files (RFC 4867 section 5: <c>.amr</c>, <c>.awb</c>): a magic number, then speech frames, each a
/// one-byte table of contents followed by the speech bits. Every frame is delivered whole (TOC byte included), the same bytes
/// a 3GP sample holds. NO_DATA and SPEECH_LOST frames are one-byte packets.
/// </summary>
/// <remarks>
/// <para>Single-channel files only: the multi-channel magic numbers are accepted when the channel field says one channel and
/// rejected with <see cref="UnsupportedAudioFormatException"/> otherwise. A frame type without a defined size (reserved types)
/// ends the stream when at most 8192 bytes remain and is an <see cref="AudioFormatException"/> otherwise; a cut-off last frame is
/// dropped.</para>
/// <para>Every frame is 20 ms (160 samples at 8000 Hz or 320 at 16000 Hz), so seeking by time is seeking by frame number. On a
/// seekable stream the file is scanned once at open (jumping by frame size) for <see cref="LengthFrames"/> and a sparse seek index;
/// a forward-only stream reports -1 and cannot seek.</para>
/// </remarks>
public sealed class AmrPacketSource : IPacketSource
{
    private const int IndexStride = 64;
    private const int TailBytes = 8192;

    private readonly Stream _stream;
    private readonly AudioLimits _limits;
    private readonly bool _seekable;
    private readonly long _length;
    private readonly long _firstFrame;
    private readonly List<long> _index = [];
    private readonly byte[] _buffer = new byte[1 + 60];
    private bool _leaveOpen;
    private bool _disposed;
    private bool _ended;
    private long _frames = -1;
    private long _next;
    private long _pos;

    private AmrPacketSource(Stream stream, AudioLimits limits, bool leaveOpen, CancellationToken ct)
    {
        _stream = stream;
        _limits = limits;
        _leaveOpen = leaveOpen;
        _seekable = stream.CanSeek;
        _length = _seekable ? stream.Length : -1;
        _pos = _seekable ? stream.Position : 0;

        Span<byte> magic = stackalloc byte[24];
        int n = stream.ReadAtLeast(magic, magic.Length, throwOnEndOfStream: false);
        int header = Identify(magic[..n], out bool wide, out bool multi);
        if (header == 0) throw new AudioFormatException("The data does not start with an AMR file magic number.");
        if (multi)
        {
            if (n < header + 4) throw new AudioFormatException("The file ends inside the multi-channel AMR header.");
            // RFC 4867 5.2: the low four bits of the 32-bit channel description field are the channel count.
            int channels = magic[header + 3] & 0xF;
            header += 4;
            if (channels != 1) throw new UnsupportedAudioFormatException($"Multi-channel AMR files ({channels} channels) are not supported.");
        }
        IsWideband = wide;
        Codec = wide ? CodecId.AmrWb : CodecId.AmrNb;
        SampleRate = wide ? 16000 : 8000;
        SamplesPerFrame = AmrFrameTable.SamplesPerFrame(wide);
        limits.CheckSampleRate(SampleRate);

        _firstFrame = _pos + header;
        if (_seekable)
        {
            _stream.Position = _firstFrame;
            _pos = _firstFrame;
            Scan(ct);
            Rewind();
        }
        else
        {
            // Bytes read past the header belong to the first frame.
            _replay = magic[header..n].ToArray();
            _pos = _firstFrame + _replay.Length;
        }
    }

    private byte[]? _replay;
    private int _replayPos;

    /// <summary>Opens an AMR or AMR-WB storage-format stream.</summary>
    /// <param name="stream">The stream, positioned at the magic number.</param>
    /// <param name="limits">Limits for untrusted input; <see cref="AudioLimits.Default"/> when null.</param>
    /// <param name="leaveOpen">Keep the stream open when the source is disposed.</param>
    /// <param name="cancellationToken">Observed while scanning a seekable file.</param>
    /// <returns>A source positioned at the first frame.</returns>
    /// <exception cref="AudioFormatException">Missing magic number, or a bad frame where a frame is expected.</exception>
    /// <exception cref="UnsupportedAudioFormatException">A multi-channel file.</exception>
    public static AmrPacketSource Open(Stream stream, AudioLimits? limits = null, bool leaveOpen = false, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(stream);
        if (!stream.CanRead) throw new ArgumentException("Stream must be readable.", nameof(stream));
        AmrPacketSource? src = null;
        try
        {
            return src = new AmrPacketSource(stream, limits ?? AudioLimits.Default, leaveOpen, cancellationToken);
        }
        catch
        {
            // The caller keeps the stream when opening fails.
            src?.Abandon();
            throw;
        }
    }

    /// <summary>Length of the magic number (and channel field) at the start of <paramref name="head"/>, or 0 when it is not an AMR file.</summary>
    internal static int Identify(ReadOnlySpan<byte> head, out bool wideband, out bool multiChannel)
    {
        wideband = multiChannel = false;
        if (head.StartsWith("#!AMR\n"u8)) return 6;
        if (head.StartsWith("#!AMR-WB\n"u8)) { wideband = true; return 9; }
        if (head.StartsWith("#!AMR_MC1.0\n"u8)) { multiChannel = true; return 12; }
        if (head.StartsWith("#!AMR-WB_MC1.0\n"u8)) { wideband = multiChannel = true; return 15; }
        return 0;
    }

    /// <summary>True for AMR-WB (16 kHz), false for AMR narrowband (8 kHz).</summary>
    public bool IsWideband { get; }

    /// <inheritdoc />
    public CodecId Codec { get; }

    /// <inheritdoc />
    public int SampleRate { get; }

    /// <inheritdoc />
    public int Channels => 1;

    /// <inheritdoc />
    public int TimeScale => SampleRate;

    /// <summary>Empty: a storage-format file carries no codec configuration.</summary>
    public ReadOnlyMemory<byte> CodecConfig => default;

    /// <summary>Samples in each frame: 160 (narrowband) or 320 (wideband).</summary>
    public int SamplesPerFrame { get; }

    /// <inheritdoc />
    public long LengthFrames => _frames < 0 ? -1 : _frames * SamplesPerFrame;

    /// <summary>Number of speech frames, or -1 on a forward-only stream.</summary>
    public long FrameCount => _frames;

    /// <inheritdoc />
    public bool CanSeek => _seekable;

    /// <inheritdoc />
    public bool TryReadPacket(out PacketView packet)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        packet = default;
        if (_ended) return false;
        int size = ReadToc(out byte toc);
        if (size < 0)
        {
            _ended = true;
            return false;
        }
        _buffer[0] = toc;
        int got = ReadBytes(_buffer.AsSpan(1, size));
        if (got < size)
        {
            _ended = true; // cut-off last frame
            return false;
        }
        long index = _next++;
        packet = new PacketView(_buffer.AsSpan(0, 1 + size), (index + 1) * SamplesPerFrame, false, index * SamplesPerFrame, SamplesPerFrame);
        return true;
    }

    /// <summary>Positions on the frame containing <paramref name="frame"/> (20 ms per frame).</summary>
    /// <param name="frame">Target sample, 0 or more; at or past the end positions at the end.</param>
    /// <param name="startFrame">The first sample of the frame that will be returned.</param>
    /// <exception cref="NotSupportedException">The stream is not seekable.</exception>
    /// <exception cref="ArgumentOutOfRangeException"><paramref name="frame"/> is negative.</exception>
    public void SeekToFrame(long frame, out long startFrame)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        if (!_seekable) throw new NotSupportedException("The stream is not seekable.");
        ArgumentOutOfRangeException.ThrowIfNegative(frame);
        long target = Math.Min(frame / SamplesPerFrame, _frames);
        long block = Math.Min(target / IndexStride, _index.Count - 1);
        _ended = false;
        _pos = _index[(int)block];
        _stream.Position = _pos;
        for (long i = block * IndexStride; i < target; i++)
        {
            int size = ReadToc(out _);
            if (size < 0) break;
            Skip(size);
        }
        _next = target;
        startFrame = target * SamplesPerFrame;
    }

    /// <inheritdoc />
    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        if (!_leaveOpen) _stream.Dispose();
    }

    internal void Abandon()
    {
        _leaveOpen = true;
        Dispose();
    }

    private void Rewind()
    {
        _stream.Position = _firstFrame;
        _pos = _firstFrame;
        _next = 0;
        _ended = false;
    }

    private void Scan(CancellationToken ct)
    {
        long frames = 0;
        while (true)
        {
            if ((frames & 0xFFF) == 0) ct.ThrowIfCancellationRequested();
            long at = _pos;
            int size = ReadToc(out _);
            if (size < 0 || _length - (at + 1 + size) < 0) break;
            if (frames % IndexStride == 0) _index.Add(at);
            _limits.CheckSamples(++frames);
            Skip(size);
        }
        _frames = frames;
        if (_index.Count == 0) _index.Add(_firstFrame);
    }

    // Reads one TOC byte. Returns the payload size, or -1 at the end of the stream (clean end, trailing bytes, or a cut-off frame).
    private int ReadToc(out byte toc)
    {
        toc = 0;
        Span<byte> b = stackalloc byte[1];
        if (ReadBytes(b) < 1) return -1;
        toc = b[0];
        int size = AmrFrameTable.PayloadBytes(IsWideband, AmrFrameTable.FrameType(toc));
        if (size >= 0) return size;
        if (IsTail()) return -1;
        throw new AudioFormatException("A frame has a reserved frame type.");
    }

    private int ReadBytes(Span<byte> destination)
    {
        int total = 0;
        if (_replay is { } replay)
        {
            int n = Math.Min(destination.Length, replay.Length - _replayPos);
            replay.AsSpan(_replayPos, n).CopyTo(destination);
            _replayPos += n;
            total = n;
            if (_replayPos >= replay.Length) _replay = null;
        }
        if (total < destination.Length)
        {
            int got = _stream.ReadAtLeast(destination[total..], destination.Length - total, throwOnEndOfStream: false);
            _pos += got;
            total += got;
        }
        return total;
    }

    private void Skip(int count)
    {
        _pos += count;
        _stream.Position = _pos;
    }

    private bool IsTail()
    {
        if (_seekable) return _length - _pos <= TailBytes;
        Span<byte> scratch = stackalloc byte[512];
        int total = 0;
        while (total <= TailBytes)
        {
            int got = _stream.Read(scratch);
            if (got <= 0) return true;
            total += got;
        }
        return false;
    }
}
