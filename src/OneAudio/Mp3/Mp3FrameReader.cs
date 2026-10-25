namespace OneAudio.Mp3;

/// <summary>
/// Finds MPEG audio frames in a byte stream through a fixed buffer. A header is only believed when the bytes that
/// follow the frame it announces are themselves a compatible header (or the end of the data, or a trailing tag), so
/// stray 0xFF bytes in ID3 payloads, cover art or damaged data do not pass as frames. Free-format streams, where the
/// header does not give the length, are measured from the distance to the next header. All memory is the one buffer
/// allocated at construction.
/// </summary>
internal sealed class Mp3FrameReader
{
    /// <summary>Longest frame accepted: far above the 1441 bytes of the largest fixed-rate frame, which bounds free-format streams.</summary>
    public const int MaxFrameBytes = 8192;
    private const int BufferSize = 64 * 1024;

    private readonly Stream _stream;
    private readonly byte[] _buf = new byte[BufferSize];
    private int _start, _end;
    private long _bufOffset;
    private bool _eof;
    private Mp3Header _lock;
    private bool _locked;
    private int _freeSlots;   // free-format frame length without padding, once measured

    public Mp3FrameReader(Stream stream)
    {
        _stream = stream;
        _bufOffset = stream.CanSeek ? stream.Position : 0;
    }

    /// <summary>Stream offset of the next unread byte.</summary>
    public long Position => _bufOffset + _start;

    /// <summary>The stream parameters every later frame must match, once the first frame has been found.</summary>
    public void Lock(in Mp3Header h) { _lock = h; _locked = true; }

    /// <summary>Makes at least <paramref name="n"/> bytes available at the read position if the stream has them; returns how many are.</summary>
    public int Ensure(int n)
    {
        if (_end - _start >= n || _eof) return _end - _start;
        if (_start > 0)
        {
            Buffer.BlockCopy(_buf, _start, _buf, 0, _end - _start);
            _bufOffset += _start;
            _end -= _start;
            _start = 0;
        }
        while (_end < n && !_eof)
        {
            int r = _stream.Read(_buf, _end, _buf.Length - _end);
            if (r <= 0) _eof = true; else _end += r;
        }
        return _end - _start;
    }

    public ReadOnlySpan<byte> Peek(int n) => _buf.AsSpan(_start, Math.Min(n, _end - _start));

    public void Advance(int n) => _start += n;

    /// <summary>Skips <paramref name="count"/> bytes, seeking when the stream can.</summary>
    public void Skip(long count)
    {
        long buffered = _end - _start;
        if (count <= buffered) { _start += (int)count; return; }
        count -= buffered;
        _start = _end = 0;
        _bufOffset += buffered;
        if (_stream.CanSeek)
        {
            _stream.Position = _bufOffset + count;
            _bufOffset += count;
            return;
        }
        Span<byte> sink = stackalloc byte[512];
        while (count > 0)
        {
            int r = _stream.Read(sink[..(int)Math.Min(count, sink.Length)]);
            if (r <= 0) { _eof = true; break; }
            count -= r;
            _bufOffset += r;
        }
    }

    /// <summary>Repositions at an absolute stream offset (seekable streams only).</summary>
    public void SeekTo(long offset)
    {
        _stream.Position = offset;
        _bufOffset = offset;
        _start = _end = 0;
        _eof = false;
    }

    /// <summary>
    /// Advances to the next frame and returns its header and length in bytes. The frame's bytes are then available
    /// through <see cref="Peek"/>; call <see cref="Advance"/> with the length to move past them.
    /// </summary>
    /// <param name="header">The frame header.</param>
    /// <param name="length">The frame length in bytes, header included.</param>
    /// <param name="maxSkip">How many bytes may be discarded before a frame is found; exceeding it ends the search.</param>
    public bool Next(out Mp3Header header, out int length, long maxSkip)
    {
        long skipped = 0;
        while (true)
        {
            int have = Ensure(4);
            if (have < 4) { header = default; length = 0; return false; }
            var b = _buf.AsSpan(_start, 4);
            if (b[0] == 0xFF && Mp3Header.TryParse(b, out header) && Accept(header, out length)) return true;
            _start++;
            if (++skipped > maxSkip) { header = default; length = 0; return false; }
        }
    }

    private bool Accept(in Mp3Header h, out int length)
    {
        length = 0;
        if (_locked && !h.IsCompatibleWith(_lock)) return false;
        if (h.Layer != 3) return false; // layers I and II are refused by the caller before frames are followed
        int len;
        if (h.IsFreeFormat)
        {
            len = FreeFrameLength(h);
            if (len <= 0) return false;
        }
        else len = h.FrameBytes;
        if (len < h.HeaderBytes + h.SideInfoBytes || len > MaxFrameBytes) return false;
        int have = Ensure(len + 4);
        if (have < len) return false; // truncated last frame
        if (have >= len + 4)
        {
            var rest = _buf.AsSpan(_start + len, have - len);
            bool follows = Mp3Header.TryParse(rest, out var nh) && (_locked ? nh.IsCompatibleWith(_lock) : nh.IsCompatibleWith(h));
            if (follows)
            {
                if (nh.IsFreeFormat != h.IsFreeFormat || nh.Layer != 3) return false;
            }
            else if (!IsTrailer(rest)) return false;
        }
        // One to three bytes after the last frame are padding at the end of the file.
        length = len;
        return true;
    }

    private static bool IsTrailer(ReadOnlySpan<byte> rest)
    {
        // ID3v1 ("TAG"), ID3v2 ("ID3"), APE ("APETAGEX"), Lyrics3 and trailing zero padding are legitimate ends of audio.
        if (rest.Length >= 3 && ((rest[0] == 'T' && rest[1] == 'A' && rest[2] == 'G') || (rest[0] == 'I' && rest[1] == 'D' && rest[2] == '3'))) return true;
        if (rest.Length >= 8 && rest[..8].SequenceEqual("APETAGEX"u8)) return true;
        if (rest.Length >= 11 && rest[..11].SequenceEqual("LYRICSBEGIN"u8)) return true;
        foreach (byte x in rest) if (x != 0) return false;
        return true;
    }

    private int FreeFrameLength(in Mp3Header h)
    {
        if (_freeSlots > 0) return _freeSlots + (h.Padding ? 1 : 0);
        // Measure: the next compatible free-format header whose own successor lines up.
        int min = h.HeaderBytes + h.SideInfoBytes;
        int have = Ensure(2 * MaxFrameBytes + 8);
        for (int q = min; q + 4 <= have && q <= MaxFrameBytes; q++)
        {
            if (_buf[_start + q] != 0xFF) continue;
            if (!Mp3Header.TryParse(_buf.AsSpan(_start + q, 4), out var nh) || !nh.IsFreeFormat || !nh.IsCompatibleWith(h) || nh.Layer != 3) continue;
            int slots = q - (h.Padding ? 1 : 0);
            int nextLen = slots + (nh.Padding ? 1 : 0);
            int q2 = q + nextLen;
            if (q2 + 4 <= have)
            {
                if (!Mp3Header.TryParse(_buf.AsSpan(_start + q2, 4), out var n2) || !n2.IsFreeFormat || !n2.IsCompatibleWith(h)) continue;
            }
            else if (q2 < have && !IsTrailer(_buf.AsSpan(_start + q2, have - q2))) continue;
            _freeSlots = slots;
            return q;
        }
        return 0;
    }
}
