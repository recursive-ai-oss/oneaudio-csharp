using System.Buffers.Binary;
using System.Numerics;
using System.Runtime.CompilerServices;

namespace OneAudio.Flac;

/// <summary>
/// Reads a FLAC frame bit by bit, most significant bit first (RFC 9639 section 5). A 64-bit buffer holds the next bits
/// left-aligned and is refilled eight bytes at a time. Reading past the end of the data sets <see cref="Overrun"/>
/// and yields zero bits, so a truncated frame is detected once at the end of the frame rather than on every read.
/// </summary>
internal ref struct FlacBitReader
{
    private readonly ReadOnlySpan<byte> _data;
    private int _pos;
    private ulong _buf;
    private int _cnt;

    public FlacBitReader(ReadOnlySpan<byte> data)
    {
        _data = data;
        _pos = 0;
        _buf = 0;
        _cnt = 0;
        Overrun = false;
    }

    /// <summary>True once a read asked for more bits than the data had.</summary>
    public bool Overrun { get; private set; }

    /// <summary>Byte offset of the next unread bit; meaningful after <see cref="AlignToByte"/>.</summary>
    public readonly int BytePosition => _pos - (_cnt >> 3);

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private void Refill()
    {
        if (_pos + 8 <= _data.Length)
        {
            // Bits past _cnt are copies of upcoming data, so OR-ing the same bytes again later is harmless.
            ulong v = BinaryPrimitives.ReadUInt64BigEndian(_data.Slice(_pos));
            _buf |= v >> _cnt;
            int take = (63 - _cnt) >> 3;
            _pos += take;
            _cnt += take << 3;
        }
        else
        {
            while (_cnt <= 56 && _pos < _data.Length)
            {
                _buf |= (ulong)_data[_pos++] << (56 - _cnt);
                _cnt += 8;
            }
        }
    }

    private void Fail()
    {
        Overrun = true;
        _buf = 0;
        _cnt = 0;
        _pos = _data.Length;
    }

    /// <summary>Reads <paramref name="n"/> (0 to 32) bits as an unsigned integer.</summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public uint ReadBits(int n)
    {
        if (n == 0) return 0;
        if (_cnt < n)
        {
            Refill();
            if (_cnt < n) { Fail(); return 0; }
        }
        uint v = (uint)(_buf >> (64 - n));
        _buf <<= n;
        _cnt -= n;
        return v;
    }

    /// <summary>Reads <paramref name="n"/> (0 to 32) bits as a two's complement integer.</summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public int ReadSigned(int n)
    {
        if (n == 0) return 0;
        return (int)(ReadBits(n) << (32 - n)) >> (32 - n);
    }

    /// <summary>Reads <paramref name="n"/> (0 to 33) bits as a two's complement integer.</summary>
    public long ReadSignedLong(int n)
    {
        if (n <= 32) return ReadSigned(n);
        long hi = ReadSigned(n - 32);
        long lo = ReadBits(32);
        return (hi << 32) | lo;
    }

    /// <summary>Counts zero bits up to and including the next one bit and returns the number of zeros.</summary>
    public int ReadUnary()
    {
        int zeros = 0;
        while (true)
        {
            if (_cnt < 1)
            {
                Refill();
                if (_cnt < 1) { Fail(); return zeros; }
            }
            // Bits past _cnt may be non-zero copies of upcoming data; only trust the first _cnt.
            int lz = BitOperations.LeadingZeroCount(_buf);
            if (lz < _cnt)
            {
                _buf <<= lz + 1;
                _cnt -= lz + 1;
                return zeros + lz;
            }
            zeros += _cnt;
            _buf = 0;
            _cnt = 0;
        }
    }

    /// <summary>
    /// Reads one Rice-coded residual with parameter <paramref name="k"/> (RFC 9639 section 9.2.7.1): a unary quotient,
    /// <paramref name="k"/> remainder bits, and the zig-zag mapping back to a signed value. Returns false when the
    /// quotient is so large that the value cannot fit 32 bits.
    /// </summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public bool TryReadRice(int k, out int value)
    {
        if (_cnt < 40) Refill();
        int lz = BitOperations.LeadingZeroCount(_buf);
        ulong u;
        if (lz + 1 + k <= _cnt)
        {
            _buf <<= lz + 1;
            ulong r = k == 0 ? 0 : _buf >> (64 - k);
            _buf <<= k;
            _cnt -= lz + 1 + k;
            u = ((ulong)lz << k) | r;
        }
        else
        {
            long q = ReadUnary();
            ulong r = ReadBits(k);
            if (q > 0xFFFF_FFFFL >> k) { value = 0; return false; }
            u = ((ulong)q << k) | r;
        }
        if (u > 0xFFFF_FFFFUL) { value = 0; return false; }
        uint w = (uint)u;
        value = (int)(w >> 1) ^ -(int)(w & 1);
        return true;
    }

    /// <summary>Discards the bits left in the current byte.</summary>
    public void AlignToByte()
    {
        int drop = _cnt & 7;
        _buf <<= drop;
        _cnt -= drop;
    }
}
