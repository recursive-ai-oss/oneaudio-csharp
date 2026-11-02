using System.Buffers.Binary;
using System.Numerics;

namespace OneAudio.Flac;

/// <summary>
/// Incremental MD5 (RFC 1321) in plain managed code. FLAC stores the MD5 of the unencoded samples in STREAMINFO
/// (RFC 9639 section 8.2). The BCL's MD5 is unavailable on some platforms (browser-wasm), so the library always uses
/// this one: the same code and the same bytes everywhere. It allocates only at construction.
/// </summary>
internal sealed class Md5
{
    private const int BlockSize = 64;

    // RFC 1321 3.4: T[i] = floor(2^32 * abs(sin(i + 1))), the table the appendix spells out in the round macros.
    private static readonly uint[] K =
    [
        0xd76aa478, 0xe8c7b756, 0x242070db, 0xc1bdceee, 0xf57c0faf, 0x4787c62a, 0xa8304613, 0xfd469501,
        0x698098d8, 0x8b44f7af, 0xffff5bb1, 0x895cd7be, 0x6b901122, 0xfd987193, 0xa679438e, 0x49b40821,
        0xf61e2562, 0xc040b340, 0x265e5a51, 0xe9b6c7aa, 0xd62f105d, 0x02441453, 0xd8a1e681, 0xe7d3fbc8,
        0x21e1cde6, 0xc33707d6, 0xf4d50d87, 0x455a14ed, 0xa9e3e905, 0xfcefa3f8, 0x676f02d9, 0x8d2a4c8a,
        0xfffa3942, 0x8771f681, 0x6d9d6122, 0xfde5380c, 0xa4beea44, 0x4bdecfa9, 0xf6bb4b60, 0xbebfbc70,
        0x289b7ec6, 0xeaa127fa, 0xd4ef3085, 0x04881d05, 0xd9d4d039, 0xe6db99e5, 0x1fa27cf8, 0xc4ac5665,
        0xf4292244, 0x432aff97, 0xab9423a7, 0xfc93a039, 0x655b59c3, 0x8f0ccc92, 0xffeff47d, 0x85845dd1,
        0x6fa87e4f, 0xfe2ce6e0, 0xa3014314, 0x4e0811a1, 0xf7537e82, 0xbd3af235, 0x2ad7d2bb, 0xeb86d391,
    ];

    // Per-step left rotations, four rounds of sixteen steps (RFC 1321 3.4).
    private static readonly byte[] S =
    [
        7, 12, 17, 22, 7, 12, 17, 22, 7, 12, 17, 22, 7, 12, 17, 22,
        5, 9, 14, 20, 5, 9, 14, 20, 5, 9, 14, 20, 5, 9, 14, 20,
        4, 11, 16, 23, 4, 11, 16, 23, 4, 11, 16, 23, 4, 11, 16, 23,
        6, 10, 15, 21, 6, 10, 15, 21, 6, 10, 15, 21, 6, 10, 15, 21,
    ];

    private readonly uint[] _state = new uint[4];
    private readonly uint[] _x = new uint[16];
    private readonly byte[] _buf = new byte[BlockSize];
    private readonly byte[] _pad = new byte[BlockSize * 2];
    private readonly uint[] _saved = new uint[4];
    private readonly byte[] _savedBuf = new byte[BlockSize];
    private int _buffered;
    private long _length;

    public Md5() => Reset();

    /// <summary>Starts a new hash.</summary>
    public void Reset()
    {
        // RFC 1321 3.3: initial chaining values.
        _state[0] = 0x67452301;
        _state[1] = 0xefcdab89;
        _state[2] = 0x98badcfe;
        _state[3] = 0x10325476;
        _buffered = 0;
        _length = 0;
    }

    /// <summary>Adds bytes to the hash.</summary>
    public void Append(ReadOnlySpan<byte> data)
    {
        _length += data.Length;
        if (_buffered > 0)
        {
            int take = Math.Min(BlockSize - _buffered, data.Length);
            data.Slice(0, take).CopyTo(_buf.AsSpan(_buffered));
            _buffered += take;
            data = data.Slice(take);
            if (_buffered < BlockSize) return;
            Transform(_buf);
            _buffered = 0;
        }
        while (data.Length >= BlockSize)
        {
            Transform(data);
            data = data.Slice(BlockSize);
        }
        data.CopyTo(_buf);
        _buffered = data.Length;
    }

    /// <summary>Writes the 16-byte digest of everything appended so far without disturbing the running hash.</summary>
    public void GetCurrentHash(Span<byte> destination)
    {
        if (destination.Length < 16) throw new ArgumentException("The digest needs 16 bytes.", nameof(destination));
        _state.CopyTo(_saved, 0);
        _buf.CopyTo(_savedBuf, 0);
        long savedLength = _length;
        int savedBuffered = _buffered;
        // RFC 1321 3.1/3.2: a 1 bit, zeros up to 56 mod 64, then the bit length as 64-bit little endian.
        int padLen = _buffered < 56 ? 56 - _buffered : 120 - _buffered;
        Array.Clear(_pad);
        _pad[0] = 0x80;
        BinaryPrimitives.WriteInt64LittleEndian(_pad.AsSpan(padLen), savedLength * 8);
        Append(_pad.AsSpan(0, padLen + 8));
        for (int i = 0; i < 4; i++) BinaryPrimitives.WriteUInt32LittleEndian(destination.Slice(i * 4), _state[i]);
        _saved.CopyTo(_state, 0);
        _savedBuf.CopyTo(_buf, 0);
        _length = savedLength;
        _buffered = savedBuffered;
    }

    /// <summary>Writes the digest and starts a new hash.</summary>
    public void GetHashAndReset(Span<byte> destination)
    {
        GetCurrentHash(destination);
        Reset();
    }

    private void Transform(ReadOnlySpan<byte> block)
    {
        var x = _x;
        for (int i = 0; i < 16; i++) x[i] = BinaryPrimitives.ReadUInt32LittleEndian(block.Slice(i * 4, 4));
        uint a = _state[0], b = _state[1], c = _state[2], d = _state[3];
        for (int i = 0; i < 64; i++)
        {
            uint f;
            int g;
            if (i < 16) { f = (b & c) | (~b & d); g = i; }                 // F
            else if (i < 32) { f = (d & b) | (~d & c); g = (5 * i + 1) & 15; } // G
            else if (i < 48) { f = b ^ c ^ d; g = (3 * i + 5) & 15; }          // H
            else { f = c ^ (b | ~d); g = (7 * i) & 15; }                       // I
            uint t = a + f + K[i] + x[g];
            a = d;
            d = c;
            c = b;
            b += BitOperations.RotateLeft(t, S[i]);
        }
        _state[0] += a;
        _state[1] += b;
        _state[2] += c;
        _state[3] += d;
    }
}
