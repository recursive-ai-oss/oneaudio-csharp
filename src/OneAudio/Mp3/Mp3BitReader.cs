using System.Buffers.Binary;

namespace OneAudio.Mp3;

/// <summary>
/// A forward-only reader over a byte span that returns zero bits past the end instead of throwing, so Huffman
/// decoding can run on short or damaged data and the caller checks the final position against the granule's
/// declared length. All offsets are bit positions.
/// </summary>
internal ref struct Mp3BitReader
{
    private readonly ReadOnlySpan<byte> _data;

    /// <summary>Current bit position from the start of the span.</summary>
    public long Position;

    public Mp3BitReader(ReadOnlySpan<byte> data, long position = 0)
    {
        _data = data;
        Position = position;
    }

    public readonly long LengthBits => (long)_data.Length * 8;

    /// <summary>Returns the next <paramref name="n"/> bits (1 to 25) without consuming them.</summary>
    public readonly uint Peek(int n)
    {
        long bytePos = Position >> 3;
        int shift = (int)(Position & 7);
        uint w;
        if (bytePos + 4 <= _data.Length)
        {
            w = BinaryPrimitives.ReadUInt32BigEndian(_data.Slice((int)bytePos, 4));
        }
        else
        {
            w = 0;
            for (int k = 0; k < 4; k++)
            {
                long p = bytePos + k;
                w = (w << 8) | (p >= 0 && p < _data.Length ? _data[(int)p] : (uint)0);
            }
        }
        return (w << shift) >> (32 - n);
    }

    /// <summary>Consumes and returns <paramref name="n"/> bits (0 to 25).</summary>
    public uint Read(int n)
    {
        if (n == 0) return 0;
        uint v = Peek(n);
        Position += n;
        return v;
    }

    /// <summary>The bit at an absolute position (zero past the end), without moving the reader.</summary>
    public readonly uint BitAt(long position)
    {
        long bytePos = position >> 3;
        return bytePos >= 0 && bytePos < _data.Length ? (uint)(_data[(int)bytePos] >> (7 - (int)(position & 7))) & 1u : 0;
    }

    public uint ReadBit()
    {
        long bytePos = Position >> 3;
        uint v = bytePos < _data.Length ? (uint)(_data[(int)bytePos] >> (7 - (int)(Position & 7))) & 1u : 0;
        Position++;
        return v;
    }
}
