using System.Numerics;

namespace OneAudio.Opus;

/// <summary>
/// The Opus range encoder (RFC 6716 section 5.1). Derived from the reference encoder in RFC 6716 (Simplified BSD
/// licence, see THIRD-PARTY-NOTICES.md). Raw bits are written backwards from the end of the buffer; a write that
/// would overlap the range-coded bytes sets <see cref="Error"/> instead of throwing, as the format requires.
/// </summary>
internal ref struct RangeEncoder
{
    private const int SymBits = 8;
    private const int CodeBits = 32;
    private const uint SymMax = (1u << SymBits) - 1;
    private const uint CodeTop = 1u << (CodeBits - 1);
    private const uint CodeBot = CodeTop >> SymBits;
    private const int CodeShift = CodeBits - SymBits - 1;
    private const int UintBits = 8;
    private const int WindowSize = 32;

    private Span<byte> _buf;
    private int _storage;
    private int _endOffs;
    private uint _endWindow;
    private int _nendBits;
    private int _nbitsTotal;
    private int _offs;
    private uint _rng;
    private uint _val;
    private int _rem;
    private int _ext;

    /// <summary>Set when the buffer was too small.</summary>
    public bool Error;

    public RangeEncoder(Span<byte> buf, int size)
    {
        _buf = buf;
        _storage = size;
        _endOffs = 0;
        _endWindow = 0;
        _nendBits = 0;
        _nbitsTotal = CodeBits + 1;
        _offs = 0;
        _rng = CodeTop;
        _rem = -1;
        _val = 0;
        _ext = 0;
        Error = false;
    }

    public readonly uint Range => _rng;
    public readonly int Storage => _storage;
    public readonly int Offs => _offs;
    public readonly Span<byte> Buffer => _buf;

    /// <summary>Forces the bit counter forward (a silent CELT frame pretends to have used all its bits).</summary>
    public void AddTotalBits(int bits) => _nbitsTotal += bits;

    private void WriteByte(uint value)
    {
        if (_offs + _endOffs >= _storage) { Error = true; return; }
        _buf[_offs++] = (byte)value;
    }

    private void WriteByteAtEnd(uint value)
    {
        if (_offs + _endOffs >= _storage) { Error = true; return; }
        _buf[_storage - ++_endOffs] = (byte)value;
    }

    private void CarryOut(int c)
    {
        if (c != SymMax)
        {
            int carry = c >> SymBits;
            if (_rem >= 0) WriteByte((uint)(_rem + carry));
            if (_ext > 0)
            {
                uint sym = (SymMax + (uint)carry) & SymMax;
                do WriteByte(sym); while (--_ext > 0);
            }
            _rem = c & (int)SymMax;
        }
        else _ext++;
    }

    private void Normalize()
    {
        while (_rng <= CodeBot)
        {
            CarryOut((int)(_val >> CodeShift));
            _val = (_val << SymBits) & (CodeTop - 1);
            _rng <<= SymBits;
            _nbitsTotal += SymBits;
        }
    }

    public void Encode(uint fl, uint fh, uint ft)
    {
        uint r = _rng / ft;
        if (fl > 0)
        {
            _val += _rng - unchecked(r * (ft - fl));
            _rng = unchecked(r * (fh - fl));
        }
        else _rng -= unchecked(r * (ft - fh));
        Normalize();
    }

    public void EncodeBin(uint fl, uint fh, int bits)
    {
        uint r = _rng >> bits;
        if (fl > 0)
        {
            _val += _rng - unchecked(r * ((1u << bits) - fl));
            _rng = unchecked(r * (fh - fl));
        }
        else _rng -= unchecked(r * ((1u << bits) - fh));
        Normalize();
    }

    public void BitLogP(int val, int logp)
    {
        uint r = _rng, l = _val, s = r >> logp;
        r -= s;
        if (val != 0) _val = l + r;
        _rng = val != 0 ? s : r;
        Normalize();
    }

    public void ICdf(int s, ReadOnlySpan<byte> icdf, int ftb)
    {
        uint r = _rng >> ftb;
        if (s > 0)
        {
            _val += _rng - unchecked(r * icdf[s - 1]);
            _rng = unchecked(r * (uint)(icdf[s - 1] - icdf[s]));
        }
        else _rng -= unchecked(r * icdf[s]);
        Normalize();
    }

    public void EncodeUInt(uint fl, uint ft)
    {
        ft--;
        int ftb = 32 - BitOperations.LeadingZeroCount(ft);
        if (ftb > UintBits)
        {
            ftb -= UintBits;
            uint f = (ft >> ftb) + 1;
            uint l = fl >> ftb;
            Encode(l, l + 1, f);
            Bits(fl & ((1u << ftb) - 1u), ftb);
        }
        else Encode(fl, fl + 1, ft + 1);
    }

    public void Bits(uint fl, int bits)
    {
        uint window = _endWindow;
        int used = _nendBits;
        if (used + bits > WindowSize)
        {
            do
            {
                WriteByteAtEnd(window & SymMax);
                window >>= SymBits;
                used -= SymBits;
            } while (used >= SymBits);
        }
        window |= fl << used;
        used += bits;
        _endWindow = window;
        _nendBits = used;
        _nbitsTotal += bits;
    }

    /// <summary>Overwrites the first bits of the stream (RFC 6716 section 5.1.1).</summary>
    public void PatchInitialBits(uint val, int nbits)
    {
        int shift = SymBits - nbits;
        uint mask = ((1u << nbits) - 1) << shift;
        if (_offs > 0) _buf[0] = (byte)((_buf[0] & ~mask) | (val << shift));
        else if (_rem >= 0) _rem = (int)(((uint)_rem & ~mask) | (val << shift));
        else if (_rng <= (CodeTop >> nbits))
            _val = (_val & ~(mask << CodeShift)) | (val << (CodeShift + shift));
        else Error = true;
    }

    public void Shrink(int size)
    {
        _buf.Slice(_storage - _endOffs, _endOffs).CopyTo(_buf.Slice(size - _endOffs, _endOffs));
        _storage = size;
    }

    public readonly int Tell() => _nbitsTotal - (32 - BitOperations.LeadingZeroCount(_rng));

    public readonly int TellFrac()
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
        return (int)(nbits - (uint)l);
    }

    public void Done()
    {
        int l = CodeBits - (32 - BitOperations.LeadingZeroCount(_rng));
        uint msk = (CodeTop - 1) >> l;
        uint end = (_val + msk) & ~msk;
        if ((end | msk) >= _val + _rng)
        {
            l++;
            msk >>= 1;
            end = (_val + msk) & ~msk;
        }
        while (l > 0)
        {
            CarryOut((int)(end >> CodeShift));
            end = (end << SymBits) & (CodeTop - 1);
            l -= SymBits;
        }
        if (_rem >= 0 || _ext > 0) CarryOut(0);
        uint window = _endWindow;
        int used = _nendBits;
        while (used >= SymBits)
        {
            WriteByteAtEnd(window & SymMax);
            window >>= SymBits;
            used -= SymBits;
        }
        if (!Error)
        {
            _buf.Slice(_offs, _storage - _offs - _endOffs).Clear();
            if (used > 0)
            {
                if (_endOffs >= _storage) Error = true;
                else
                {
                    l = -l;
                    if (_offs + _endOffs >= _storage && l < used)
                    {
                        window &= (1u << l) - 1;
                        Error = true;
                    }
                    _buf[_storage - _endOffs - 1] |= (byte)window;
                }
            }
        }
    }

    /// <summary>Encodes a Laplace-distributed energy delta (RFC 6716 section 4.3.2.1); returns the value actually coded.</summary>
    public int LaplaceEncode(int value, uint fs, int decay)
    {
        uint fl = 0;
        int val = value;
        if (val != 0)
        {
            int s = val < 0 ? -1 : 0;
            val = (val + s) ^ s;
            fl = fs;
            fs = ((32768u - 2 * 16 - fs) * (uint)(16384 - decay)) >> 15;
            int i;
            for (i = 1; fs > 0 && i < val; i++)
            {
                fs *= 2;
                fl += fs + 2;
                fs = (uint)(((int)fs * decay) >> 15);
            }
            if (fs == 0)
            {
                int ndiMax = (int)(32768 - fl + 1 - 1);
                ndiMax = (ndiMax - s) >> 1;
                int di = Math.Min(val - i, ndiMax - 1);
                fl += (uint)(2 * di + 1 + s);
                fs = Math.Min(1u, 32768 - fl);
                value = (i + di + s) ^ s;
            }
            else
            {
                fs += 1;
                fl += (uint)((int)fs & ~s);
            }
        }
        EncodeBin(fl, fl + fs, 15);
        return value;
    }
}
