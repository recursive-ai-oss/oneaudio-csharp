using System.Buffers.Binary;

namespace OneAudio.Mp4;

/// <summary>
/// Position-tracking reader over the file for the box walk. Works on seekable streams (random access) and on forward-only
/// ones (skips by reading and discarding), so a movie box that precedes the media data streams without seeking.
/// </summary>
internal sealed class Mp4Reader
{
    private readonly Stream _stream;
    private readonly bool _seekable;
    private long _pos;

    public Mp4Reader(Stream stream)
    {
        _stream = stream;
        _seekable = stream.CanSeek;
        _pos = _seekable ? stream.Position : 0;
        Length = _seekable ? stream.Length : -1;
    }

    /// <summary>File length in bytes, or -1 when the stream cannot say.</summary>
    public long Length { get; }

    public bool CanSeek => _seekable;

    public long Position => _pos;

    /// <summary>Moves to an absolute position. A forward-only stream can only move forward.</summary>
    public void Seek(long position)
    {
        if (position == _pos) return;
        if (_seekable)
        {
            _stream.Position = position;
            _pos = position;
            return;
        }
        if (position < _pos) throw new UnsupportedAudioFormatException("This file needs a seekable stream (its data lies before the part being read).");
        Span<byte> scratch = stackalloc byte[512];
        while (_pos < position)
        {
            int n = (int)Math.Min(scratch.Length, position - _pos);
            int got = _stream.Read(scratch[..n]);
            if (got <= 0) throw new AudioFormatException("The file ends before the data it describes (truncated).");
            _pos += got;
        }
    }

    public void ReadExactly(Span<byte> destination)
    {
        int total = 0;
        while (total < destination.Length)
        {
            int n = _stream.Read(destination[total..]);
            if (n <= 0) throw new AudioFormatException("The file ends before the data it describes (truncated).");
            total += n;
            _pos += n;
        }
    }

    /// <summary>Reads as many bytes as the stream still has, up to the span length.</summary>
    public int ReadUpTo(Span<byte> destination)
    {
        int total = 0;
        while (total < destination.Length)
        {
            int n = _stream.Read(destination[total..]);
            if (n <= 0) break;
            total += n;
            _pos += n;
        }
        return total;
    }

    public uint ReadUInt32()
    {
        Span<byte> b = stackalloc byte[4];
        ReadExactly(b);
        return BinaryPrimitives.ReadUInt32BigEndian(b);
    }

    public byte[] ReadBytes(int count)
    {
        var data = new byte[count];
        ReadExactly(data);
        return data;
    }

    /// <summary>
    /// Reads <paramref name="count"/> big-endian 32-bit values. The caller has already checked that the box holds them, so
    /// a hostile count cannot allocate more than the box's real size.
    /// </summary>
    public uint[] ReadUInt32s(int count)
    {
        var result = new uint[count];
        Span<byte> chunk = stackalloc byte[1024];
        int done = 0;
        while (done < count)
        {
            int n = Math.Min(count - done, chunk.Length / 4);
            ReadExactly(chunk[..(n * 4)]);
            for (int i = 0; i < n; i++) result[done + i] = BinaryPrimitives.ReadUInt32BigEndian(chunk[(i * 4)..]);
            done += n;
        }
        return result;
    }

    public long[] ReadUInt64s(int count)
    {
        var result = new long[count];
        Span<byte> chunk = stackalloc byte[1024];
        int done = 0;
        while (done < count)
        {
            int n = Math.Min(count - done, chunk.Length / 8);
            ReadExactly(chunk[..(n * 8)]);
            for (int i = 0; i < n; i++)
            {
                ulong v = BinaryPrimitives.ReadUInt64BigEndian(chunk[(i * 8)..]);
                if (v > 1UL << 62) throw new AudioFormatException("A 64-bit offset is out of range.");
                result[done + i] = (long)v;
            }
            done += n;
        }
        return result;
    }
}

/// <summary>One box header (ISO/IEC 14496-12 4.2).</summary>
internal readonly record struct BoxHeader(uint Type, long Start, long PayloadStart, long End)
{
    public long PayloadSize => End - PayloadStart;

    public static uint Fourcc(ReadOnlySpan<byte> s) => BinaryPrimitives.ReadUInt32BigEndian(s);

    public const uint Ftyp = 0x66747970, Moov = 0x6D6F6F76, Mdat = 0x6D646174, Moof = 0x6D6F6F66, Mvhd = 0x6D766864,
        Trak = 0x7472616B, Tkhd = 0x746B6864, Edts = 0x65647473, Elst = 0x656C7374, Mdia = 0x6D646961, Mdhd = 0x6D646864,
        Hdlr = 0x68646C72, Minf = 0x6D696E66, Stbl = 0x7374626C, Stsd = 0x73747364, Stts = 0x73747473, Stsc = 0x73747363,
        Stsz = 0x7374737A, Stz2 = 0x73747A32, Stco = 0x7374636F, Co64 = 0x636F3634, Mvex = 0x6D766578, Udta = 0x75647461,
        Meta = 0x6D657461, Ilst = 0x696C7374, Data = 0x64617461, Mean = 0x6D65616E, Name = 0x6E616D65, Freeform = 0x2D2D2D2D,
        Soun = 0x736F756E, Mp4a = 0x6D703461, Samr = 0x73616D72, Sawb = 0x73617762, Esds = 0x65736473, Damr = 0x64616D72,
        Wave = 0x77617665, Uuid = 0x75756964, Titl = 0x7469746C, Auth = 0x61757468, Albm = 0x616C626D;
}
