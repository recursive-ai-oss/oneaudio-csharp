namespace OneAudio.Flac;

/// <summary>
/// A refillable byte window over a stream for the native FLAC reader. The window starts at 64 KiB and only grows
/// (within <see cref="AudioLimits.MaxPacketBytes"/>) when a single frame is larger than it.
/// </summary>
internal sealed class StreamReaderState
{
    private readonly AudioLimits _limits;
    private byte[] _buf = new byte[64 * 1024];
    private int _pos, _end;
    private long _bufFileOff;

    public StreamReaderState(Stream stream, AudioLimits limits)
    {
        Stream = stream;
        _limits = limits;
        _bufFileOff = stream.CanSeek ? stream.Position : 0;
        Hint = 4096;
    }

    public Stream Stream { get; }
    public bool Eof { get; private set; }

    /// <summary>Bytes that should be buffered before a frame is decoded, so most frames decode without a retry.</summary>
    public int Hint { get; private set; }

    public int Available => _end - _pos;

    /// <summary>Stream offset of the next unread byte.</summary>
    public long Offset => _bufFileOff + _pos;

    public ReadOnlySpan<byte> Peek(int n) => _buf.AsSpan(_pos, n);

    public void Consume(int n) => _pos += n;

    /// <summary>Sizes the window from the STREAMINFO maximum frame size (0 when unknown).</summary>
    public void SetHint(int maxFrameSize)
    {
        if (maxFrameSize > 0)
        {
            int want = (int)Math.Min((long)maxFrameSize * 2, _limits.MaxPacketBytes);
            if (want > _buf.Length) Array.Resize(ref _buf, want);
        }
        Hint = maxFrameSize > 0 ? Math.Min(maxFrameSize, _buf.Length / 2) : 4096;
    }

    public void NoteFrame(int used)
    {
        if (used > Hint && used <= _buf.Length / 2) Hint = used;
    }

    /// <summary>Reads more bytes into the window, compacting it first (or growing it when it is full of unread data).</summary>
    public void Fill()
    {
        if (_pos > 0)
        {
            Buffer.BlockCopy(_buf, _pos, _buf, 0, _end - _pos);
            _bufFileOff += _pos;
            _end -= _pos;
            _pos = 0;
        }
        else if (_end == _buf.Length)
        {
            long newLen = (long)_buf.Length * 2;
            _limits.CheckPacketBytes(newLen);
            Array.Resize(ref _buf, (int)newLen);
        }
        int n = Stream.Read(_buf, _end, _buf.Length - _end);
        if (n <= 0) Eof = true;
        else _end += n;
    }

    public bool EnsureBytes(int n)
    {
        while (Available < n)
        {
            if (Eof) return false;
            Fill();
        }
        return true;
    }

    public bool ReadExact(Span<byte> dst)
    {
        while (dst.Length > 0)
        {
            if (Available == 0)
            {
                if (Eof) return false;
                Fill();
                continue;
            }
            int n = Math.Min(Available, dst.Length);
            Peek(n).CopyTo(dst);
            Consume(n);
            dst = dst[n..];
        }
        return true;
    }

    /// <summary>Discards <paramref name="n"/> bytes without buffering them; false if the stream ends first.</summary>
    public bool Skip(long n)
    {
        int fromBuf = (int)Math.Min(Available, n);
        Consume(fromBuf);
        n -= fromBuf;
        if (n == 0) return true;
        if (Stream.CanSeek)
        {
            long target = Offset + n;
            if (target > Stream.Length) return false;
            SetOffset(target);
            return true;
        }
        while (n > 0)
        {
            if (Available == 0)
            {
                if (Eof) return false;
                Fill();
                continue;
            }
            int k = (int)Math.Min(Available, n);
            Consume(k);
            n -= k;
        }
        return true;
    }

    /// <summary>Moves the window to a stream offset (seekable streams only, unless the offset is already buffered).</summary>
    public void SetOffset(long offset)
    {
        if (offset >= _bufFileOff && offset <= _bufFileOff + _end)
        {
            _pos = (int)(offset - _bufFileOff);
            return;
        }
        Stream.Position = offset;
        _bufFileOff = offset;
        _pos = 0;
        _end = 0;
        Eof = false;
    }
}
