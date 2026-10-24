using System.Numerics;

namespace OneAudio.Opus;

/// <summary>
/// The Opus range decoder (RFC 6716 section 4.1). Derived from the reference decoder in RFC 6716 (Simplified BSD
/// licence, see THIRD-PARTY-NOTICES.md). Reads past the end of the buffer return zero bits, as the format requires.
/// </summary>
internal ref struct RangeDecoder
{
    private const int SymBits = 8;
    private const int CodeBits = 32;
    private const uint SymMax = (1u << SymBits) - 1;
    private const uint CodeTop = 1u << (CodeBits - 1);
    private const uint CodeBot = CodeTop >> SymBits;
    private const int CodeExtra = (CodeBits - 2) % SymBits + 1;
    private const int UintBits = 8;
    private const int WindowSize = 32;

    private ReadOnlySpan<byte> _buf;
    private int _storage;
    private int _endOffs;
    private uint _endWindow;
    private int _nendBits;
    private int _nbitsTotal;
    private int _offs;
    private uint _rng;
    private uint _val;
    private uint _ext;
    private int _rem;

    /// <summary>Set when a value decoded with <see cref="DecodeUInt"/> was out of range.</summary>
    public bool Error;

    public RangeDecoder(ReadOnlySpan<byte> buf)
    {
        _buf = buf;
        _storage = buf.Length;
        _endOffs = 0;
        _endWindow = 0;
        _nendBits = 0;
        _nbitsTotal = CodeBits + 1 - (CodeBits - CodeExtra) / SymBits * SymBits;
        _offs = 0;
        _rng = 1u << CodeExtra;
        _ext = 0;
        Error = false;
        _rem = ReadByte();
        _val = _rng - 1 - (uint)(_rem >> (SymBits - CodeExtra));
        Normalize();
    }

    /// <summary>Final range, used by conformance checks.</summary>
    public readonly uint Range => _rng;

    /// <summary>Bytes of the buffer the decoder may still read.</summary>
    public readonly int Storage => _storage;

    /// <summary>Drops bytes from the end of the buffer (they belong to a redundancy frame).</summary>
    public void ShrinkStorage(int bytes) => _storage -= bytes;

    private int ReadByte() => _offs < _storage ? _buf[_offs++] : 0;

    private int ReadByteFromEnd() => _endOffs < _storage ? _buf[_storage - ++_endOffs] : 0;

    private void Normalize()
    {
        while (_rng <= CodeBot)
        {
            _nbitsTotal += SymBits;
            _rng <<= SymBits;
            int sym = _rem;
            _rem = ReadByte();
            sym = ((sym << SymBits) | _rem) >> (SymBits - CodeExtra);
            _val = ((_val << SymBits) + (SymMax & ~(uint)sym)) & (CodeTop - 1);
        }
    }

    public uint Decode(uint ft)
    {
        _ext = _rng / ft;
        uint s = _val / _ext;
        return ft - Math.Min(s + 1, ft);
    }

    public uint DecodeBin(int bits)
    {
        _ext = _rng >> bits;
        uint s = _val / _ext;
        return (1u << bits) - Math.Min(s + 1u, 1u << bits);
    }

    public void Update(uint fl, uint fh, uint ft)
    {
        uint s = unchecked(_ext * (ft - fh));
        _val -= s;
        _rng = fl > 0 ? unchecked(_ext * (fh - fl)) : _rng - s;
        Normalize();
    }

    public int BitLogP(int logp)
    {
        uint r = _rng, d = _val, s = r >> logp;
        int ret = d < s ? 1 : 0;
        if (ret == 0) _val = d - s;
        _rng = ret != 0 ? s : r - s;
        Normalize();
        return ret;
    }

    /// <summary>Decodes a symbol from an inverse-CDF table with 2^<paramref name="ftb"/> total.</summary>
    public int ICdf(scoped ReadOnlySpan<byte> icdf, int ftb)
    {
        uint s = _rng, d = _val, r = s >> ftb, t;
        int ret = -1;
        do
        {
            t = s;
            s = unchecked(r * icdf[++ret]);
        } while (d < s);
        _val = d - s;
        _rng = t - s;
        Normalize();
        return ret;
    }

    public uint DecodeUInt(uint ft)
    {
        ft--;
        int ftb = 32 - BitOperations.LeadingZeroCount(ft);
        if (ftb > UintBits)
        {
            ftb -= UintBits;
            uint f = (ft >> ftb) + 1;
            uint s = Decode(f);
            Update(s, s + 1, f);
            uint t = s << ftb | Bits(ftb);
            if (t <= ft) return t;
            Error = true;
            return ft;
        }
        else
        {
            ft++;
            uint s = Decode(ft);
            Update(s, s + 1, ft);
            return s;
        }
    }

    public uint Bits(int bits)
    {
        uint window = _endWindow;
        int available = _nendBits;
        if ((uint)available < (uint)bits)
        {
            do
            {
                window |= (uint)ReadByteFromEnd() << available;
                available += SymBits;
            } while (available <= WindowSize - SymBits);
        }
        uint ret = window & ((1u << bits) - 1u);
        window >>= bits;
        available -= bits;
        _endWindow = window;
        _nendBits = available;
        _nbitsTotal += bits;
        return ret;
    }

    /// <summary>Accounts for bits consumed without reading them (a silent CELT frame).</summary>
    public void AddTotalBits(int bits) => _nbitsTotal += bits;

    /// <summary>Whole bits used so far (RFC 6716 ec_tell).</summary>
    public readonly int Tell() => _nbitsTotal - (32 - BitOperations.LeadingZeroCount(_rng));

    /// <summary>Bits used so far in 1/8 bit units (RFC 6716 ec_tell_frac).</summary>
    public readonly uint TellFrac()
    {
        uint nbits = (uint)_nbitsTotal << 3;
        int l = 32 - BitOperations.LeadingZeroCount(_rng);
        uint r = _rng >> (l - 16);
        for (int i = 3; i-- > 0;)
        {
            r = r * r >> 15;
            int b = (int)(r >> 16);
            l = l << 1 | b;
            r >>= b;
        }
        return nbits - (uint)l;
    }

    /// <summary>Laplace-distributed value for CELT energy coding (RFC 6716 section 4.3.2.1).</summary>
    public int LaplaceDecode(uint fs, int decay)
    {
        int val = 0;
        uint fm = DecodeBin(15);
        uint fl = 0;
        if (fm >= fs)
        {
            val++;
            fl = fs;
            fs = (((32768u - 32u - fs) * (uint)(16384 - decay)) >> 15) + 1;
            while (fs > 1 && fm >= fl + 2 * fs)
            {
                fs *= 2;
                fl += fs;
                fs = (uint)(((int)(fs - 2) * decay) >> 15);
                fs += 1;
                val++;
            }
            if (fs <= 1)
            {
                int di = (int)(fm - fl) >> 1;
                val += di;
                fl += (uint)(2 * di);
            }
            if (fm < fl + fs) val = -val;
            else fl += fs;
        }
        Update(fl, Math.Min(fl + fs, 32768), 32768);
        return val;
    }
}
