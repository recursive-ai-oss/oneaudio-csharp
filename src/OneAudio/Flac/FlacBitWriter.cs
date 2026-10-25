namespace OneAudio.Flac;

/// <summary>
/// Big-endian bit writer over a caller-sized byte buffer, used to assemble one FLAC frame. A 64-bit accumulator is
/// flushed four bytes at a time. The buffer is sized from the worst case (every subframe verbatim) when the encoder is
/// created, so writes never grow it.
/// </summary>
internal struct FlacBitWriter
{
    private readonly byte[] _buf;
    private int _pos;
    private ulong _acc;
    private int _bits; // bits waiting in _acc, always below 32 between calls

    public FlacBitWriter(byte[] buffer)
    {
        _buf = buffer;
        _pos = 0;
        _acc = 0;
        _bits = 0;
    }

    /// <summary>Whole bytes written so far (excluding the bits still in the accumulator).</summary>
    public readonly int BytePosition => _pos;

    public void Reset()
    {
        _pos = 0;
        _acc = 0;
        _bits = 0;
    }

    /// <summary>Writes the low <paramref name="n"/> bits (0 to 32) of <paramref name="value"/>; higher bits must be zero.</summary>
    public void Write(uint value, int n)
    {
        _acc = (_acc << n) | value;
        _bits += n;
        if (_bits >= 32)
        {
            _bits -= 32;
            uint w = (uint)(_acc >> _bits);
            _buf[_pos] = (byte)(w >> 24);
            _buf[_pos + 1] = (byte)(w >> 16);
            _buf[_pos + 2] = (byte)(w >> 8);
            _buf[_pos + 3] = (byte)w;
            _pos += 4;
        }
    }

    /// <summary>Writes a signed value as <paramref name="n"/> bits (1 to 32) of two's complement.</summary>
    public void WriteSigned(int value, int n) => Write(n == 32 ? (uint)value : (uint)value & ((1u << n) - 1), n);

    /// <summary>Writes <paramref name="zeros"/> zero bits followed by a one bit.</summary>
    public void WriteUnary(uint zeros)
    {
        while (zeros >= 32)
        {
            Write(0, 32);
            zeros -= 32;
        }
        Write(1, (int)zeros + 1 > 32 ? 32 : (int)zeros + 1); // zeros <= 31 here, so at most 32 bits
    }

    /// <summary>Pads with zero bits to the next byte boundary and flushes everything to the buffer.</summary>
    public void AlignToByte()
    {
        int pad = (8 - (_bits & 7)) & 7;
        if (pad != 0)
        {
            _acc <<= pad;
            _bits += pad;
        }
        while (_bits > 0)
        {
            _bits -= 8;
            _buf[_pos++] = (byte)(_acc >> _bits);
        }
        _acc = 0;
    }

    /// <summary>The bytes written so far; only meaningful when byte-aligned.</summary>
    public readonly ReadOnlySpan<byte> WrittenBytes => _buf.AsSpan(0, _pos);
}
