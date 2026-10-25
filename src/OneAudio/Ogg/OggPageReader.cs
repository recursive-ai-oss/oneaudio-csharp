using System.Buffers.Binary;

namespace OneAudio.Ogg;

/// <summary>One Ogg page (RFC 3533 section 6). The spans point into the reader's buffer and are valid until its next call.</summary>
internal readonly ref struct OggPage
{
    public required long Offset { get; init; }
    public required byte Flags { get; init; }
    public required long Granule { get; init; }
    public required uint Serial { get; init; }
    public required uint Sequence { get; init; }
    public required ReadOnlySpan<byte> Lacing { get; init; }
    /// <summary>The page payload; empty when the page was read with the body skipped.</summary>
    public required ReadOnlySpan<byte> Body { get; init; }
    public required int BodyLength { get; init; }

    public bool IsContinued => (Flags & 1) != 0;
    public bool IsBos => (Flags & 2) != 0;
    public bool IsEos => (Flags & 4) != 0;
    /// <summary>Total page size in bytes.</summary>
    public int Size => 27 + Lacing.Length + BodyLength;
}

/// <summary>
/// Finds and validates Ogg pages in a byte stream: capture pattern, header sanity, then the CRC. Anything that fails
/// is skipped one byte at a time, which is how a reader resynchronises after damage. Memory is one fixed buffer.
/// </summary>
internal sealed class OggPageReader
{
    /// <summary>A page is at most 27 + 255 + 255 * 255 bytes.</summary>
    public const int MaxPageSize = 27 + 255 + 255 * 255;

    private readonly Stream _stream;
    private readonly byte[] _buf = new byte[2 * 65536];
    private int _start;
    private int _end;
    private long _pos;
    private bool _eof;
    private int _lastStart;
    private int _lastLen;

    public OggPageReader(Stream stream)
    {
        _stream = stream;
        _pos = stream.CanSeek ? stream.Position : 0;
    }

    /// <summary>Stream offset of the next unread byte (counted from the start of reading when the stream cannot seek).</summary>
    public long Position => _pos;

    /// <summary>The raw bytes (header, lacing, payload) of the page last returned by a full read.</summary>
    public ReadOnlySpan<byte> LastPageBytes => _buf.AsSpan(_lastStart, _lastLen);

    /// <summary>Repositions at an absolute stream offset and drops buffered data. Seekable streams only.</summary>
    public void Seek(long offset)
    {
        _stream.Position = offset;
        _pos = offset;
        _start = _end = 0;
        _eof = false;
    }

    private bool Ensure(int n)
    {
        if (_end - _start >= n) return true;
        if (_start > 0)
        {
            Buffer.BlockCopy(_buf, _start, _buf, 0, _end - _start);
            _end -= _start;
            _start = 0;
        }
        while (_end < n && !_eof)
        {
            int r = _stream.Read(_buf, _end, _buf.Length - _end);
            if (r <= 0) _eof = true; else _end += r;
        }
        return _end >= n;
    }

    private void Advance(int n)
    {
        _start += n;
        _pos += n;
    }

    /// <summary>
    /// Reads the next valid page. With <paramref name="skipBody"/> the checksum is not verified and the payload is
    /// skipped, which makes walking a whole file cheap; the header fields are still sanity-checked.
    /// </summary>
    /// <returns>False at the end of the stream (a truncated final page counts as the end).</returns>
    public bool TryReadPage(bool skipBody, out OggPage page)
    {
        while (true)
        {
            if (!Ensure(27)) { page = default; return false; }
            var avail = _buf.AsSpan(_start, _end - _start);
            if (!avail[..4].SequenceEqual("OggS"u8))
            {
                int i = avail[1..].IndexOf("OggS"u8);
                if (i >= 0) Advance(i + 1);
                else Advance(Math.Max(1, avail.Length - 3));
                continue;
            }
            if (avail[4] != 0 || (avail[5] & ~7) != 0) { Advance(1); continue; }
            int nseg = avail[26];
            if (!Ensure(27 + nseg)) { page = default; return false; }
            avail = _buf.AsSpan(_start, _end - _start);
            int body = 0;
            for (int i = 0; i < nseg; i++) body += avail[27 + i];
            int size = 27 + nseg + body;
            if (skipBody)
            {
                var hdr = avail[..(27 + nseg)];
                var lacing = hdr.Slice(27, nseg);
                long off = _pos;
                page = Make(off, hdr, lacing, default, body);
                // The header bytes stay readable in the buffer: skipping only moves the cursor.
                SkipAfterHeader(27 + nseg, body, out bool ok);
                if (!ok) { page = default; return false; }
                return true;
            }
            if (!Ensure(size)) { page = default; return false; }
            avail = _buf.AsSpan(_start, _end - _start);
            uint stored = BinaryPrimitives.ReadUInt32LittleEndian(avail[22..]);
            uint crc = OggCrc.Update(0, avail[..22]);
            crc = OggCrc.Update(crc, "\0\0\0\0"u8);
            crc = OggCrc.Update(crc, avail[26..size]);
            if (crc != stored) { Advance(1); continue; }
            page = Make(_pos, avail[..(27 + nseg)], avail.Slice(27, nseg), avail.Slice(27 + nseg, body), body);
            _lastStart = _start;
            _lastLen = size;
            Advance(size);
            return true;
        }
    }

    private void SkipAfterHeader(int headerLen, int body, out bool ok)
    {
        _start += headerLen;
        _pos += headerLen;
        int remaining = body;
        int inBuf = Math.Min(remaining, _end - _start);
        _start += inBuf;
        _pos += inBuf;
        remaining -= inBuf;
        ok = true;
        if (remaining > 0)
        {
            _start = _end = 0;
            if (_stream.CanSeek)
            {
                long target = _pos + remaining;
                if (target > _stream.Length) { ok = false; _pos = target; _eof = true; return; }
                _stream.Position = target;
                _pos = target;
            }
            else
            {
                Span<byte> trash = stackalloc byte[512];
                while (remaining > 0)
                {
                    int r = _stream.Read(trash[..Math.Min(512, remaining)]);
                    if (r <= 0) { ok = false; _eof = true; return; }
                    remaining -= r;
                    _pos += r;
                }
            }
        }
    }

    private static OggPage Make(long offset, ReadOnlySpan<byte> hdr, ReadOnlySpan<byte> lacing, ReadOnlySpan<byte> body, int bodyLen) => new()
    {
        Offset = offset,
        Flags = hdr[5],
        Granule = BinaryPrimitives.ReadInt64LittleEndian(hdr[6..]),
        Serial = BinaryPrimitives.ReadUInt32LittleEndian(hdr[14..]),
        Sequence = BinaryPrimitives.ReadUInt32LittleEndian(hdr[18..]),
        Lacing = lacing,
        Body = body,
        BodyLength = bodyLen,
    };
}
