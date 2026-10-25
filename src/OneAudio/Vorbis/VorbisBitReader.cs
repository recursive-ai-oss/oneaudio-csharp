namespace OneAudio.Vorbis;

/// <summary>
/// Reads a Vorbis packet bit by bit, least significant bit first (Vorbis I specification section 2). Reading past the
/// end of the packet sets <see cref="EndOfPacket"/> and yields zero bits; the specification treats that as an ordinary
/// condition, because encoders may truncate packets. A 64-bit buffer is refilled a byte at a time.
/// </summary>
internal ref struct VorbisBitReader
{
    private readonly ReadOnlySpan<byte> _data;
    private int _pos;
    private ulong _buf;
    private int _cnt;

    public VorbisBitReader(ReadOnlySpan<byte> data)
    {
        _data = data;
        _pos = 0;
        _buf = 0;
        _cnt = 0;
        EndOfPacket = false;
    }

    /// <summary>True once a read asked for more bits than the packet had.</summary>
    public bool EndOfPacket { get; private set; }

    /// <summary>Bits left in the packet.</summary>
    public readonly long BitsRemaining => _cnt + 8L * (_data.Length - _pos);

    private void Refill()
    {
        while (_cnt <= 56 && _pos < _data.Length)
        {
            _buf |= (ulong)_data[_pos++] << _cnt;
            _cnt += 8;
        }
    }

    /// <summary>Reads <paramref name="n"/> (0 to 32) bits as an unsigned integer.</summary>
    public uint Read(int n)
    {
        if (_cnt < n)
        {
            Refill();
            if (_cnt < n)
            {
                EndOfPacket = true;
                uint partial = (uint)(_buf & ((1UL << _cnt) - 1));
                _buf = 0;
                _cnt = 0;
                return partial;
            }
        }
        uint v = (uint)(_buf & ((1UL << n) - 1));
        _buf >>= n;
        _cnt -= n;
        return v;
    }

    /// <summary>Reads one bit.</summary>
    public bool ReadBit() => Read(1) != 0;

    /// <summary>Returns the next <paramref name="n"/> (at most 32) bits without consuming them; missing bits read as zero.</summary>
    public uint Peek(int n)
    {
        if (_cnt < n) Refill();
        return (uint)(_buf & ((1UL << n) - 1));
    }

    /// <summary>Consumes <paramref name="n"/> bits that were peeked. Consuming more than remain sets end-of-packet.</summary>
    public void Skip(int n)
    {
        if (_cnt < n)
        {
            EndOfPacket = true;
            _buf = 0;
            _cnt = 0;
            return;
        }
        _buf >>= n;
        _cnt -= n;
    }
}
