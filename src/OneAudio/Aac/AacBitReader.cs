using System.Buffers.Binary;

namespace OneAudio.Aac;

/// <summary>
/// MSB-first bit reader over one access unit. Reading past the end returns zero bits and is reported by
/// <see cref="Overrun"/>; the caller checks it at element boundaries, so a truncated unit cannot read outside the span
/// and loops stay bounded by the syntax (every loop count comes from a fixed-width field).
/// </summary>
internal ref struct AacBitReader
{
    private readonly ReadOnlySpan<byte> _data;
    private int _pos;
    private readonly int _bitLength;

    public AacBitReader(ReadOnlySpan<byte> data)
    {
        _data = data;
        _pos = 0;
        _bitLength = data.Length * 8;
    }

    /// <summary>Bit position from the start of the unit.</summary>
    public readonly int Position => _pos;

    /// <summary>True when more bits were requested than the unit holds.</summary>
    public readonly bool Overrun => _pos > _bitLength;

    public readonly int BitsLeft => _bitLength - _pos;

    /// <summary>Returns the next <paramref name="n"/> bits (0..32) without consuming them.</summary>
    public readonly uint Peek(int n)
    {
        if (n == 0) return 0;
        int i = _pos >> 3;
        ulong w;
        if (i >= 0 && i + 8 <= _data.Length)
        {
            w = BinaryPrimitives.ReadUInt64BigEndian(_data.Slice(i, 8));
        }
        else
        {
            w = 0;
            for (int k = 0; k < 8; k++)
            {
                w <<= 8;
                if (i + k < _data.Length) w |= _data[i + k];
            }
        }
        return (uint)((w << (_pos & 7)) >> (64 - n));
    }

    public uint Read(int n)
    {
        uint v = Peek(n);
        _pos += n;
        return v;
    }

    public bool ReadBit()
    {
        uint v = Peek(1);
        _pos++;
        return v != 0;
    }

    public void Skip(int n) => _pos += n;

    public void ByteAlign() => _pos = (_pos + 7) & ~7;
}
