using System.Buffers.Binary;
using System.Numerics;

namespace OneAudio.Vorbis;

/// <summary>
/// A Vorbis codebook (specification section 3): a Huffman code for entry numbers plus an optional vector lookup table.
/// Decoding is table-driven: codes up to <see cref="PrimaryBits"/> bits resolve with one lookup, longer ones by a
/// binary search over the sorted left-aligned codewords.
/// </summary>
internal sealed class VorbisCodebook
{
    private const int PrimaryBits = 10;

    private int[] _table = [];
    private int _primaryBits;
    private uint[] _longCodes = [];
    private int[] _longEntries = [];
    private byte[] _longLengths = [];

    public int Dimensions { get; private set; }
    public int Entries { get; private set; }
    public int LookupType { get; private set; }

    /// <summary>Entry vectors, <see cref="Dimensions"/> floats each, or empty when there is no lookup table.</summary>
    public float[] Vectors { get; private set; } = [];

    /// <summary>Decodes a scalar entry number; returns -1 when the packet ends first.</summary>
    public int DecodeScalar(ref VorbisBitReader r)
    {
        int bits = _primaryBits;
        int e = _table[(int)r.Peek(bits)];
        if (e != 0)
        {
            int len = e & 63;
            if (len > r.BitsRemaining) { r.Skip(len); return -1; }
            r.Skip(len);
            return e >> 6;
        }
        return DecodeLong(ref r);
    }

    private int DecodeLong(ref VorbisBitReader r)
    {
        if (_longCodes.Length == 0) return -1;
        uint peek = r.Peek(32);
        uint msb = ReverseBits(peek);
        // Last code that is <= the peeked bits.
        int lo = 0, hi = _longCodes.Length - 1, found = -1;
        while (lo <= hi)
        {
            int mid = (lo + hi) >> 1;
            if (_longCodes[mid] <= msb) { found = mid; lo = mid + 1; }
            else hi = mid - 1;
        }
        if (found < 0) { r.Skip(1); return -1; }
        int len = _longLengths[found];
        if ((msb >> (32 - len)) != (_longCodes[found] >> (32 - len))) { r.Skip(1); return -1; }
        if (len > r.BitsRemaining) { r.Skip(len); return -1; }
        r.Skip(len);
        return _longEntries[found];
    }

    /// <summary>
    /// Reads a codebook from the setup header. Sizes are validated against <paramref name="limits"/> before anything
    /// is allocated, and <paramref name="budget"/> (bytes of setup still allowed) is reduced by what this book keeps.
    /// </summary>
    public static VorbisCodebook Parse(ref VorbisBitReader r, AudioLimits limits, ref long budget)
    {
        if (r.Read(24) != 0x564342) throw new AudioFormatException("Vorbis codebook sync pattern missing.");
        int dims = (int)r.Read(16);
        int entries = (int)r.Read(24);
        if (dims == 0) throw new AudioFormatException("Vorbis codebook has zero dimensions.");
        if (entries == 0) throw new AudioFormatException("Vorbis codebook has no entries.");
        // Entry lengths, codewords and the decode table together cost about 12 bytes per entry.
        Charge(limits, ref budget, 12L * entries);

        var lengths = new byte[entries];
        int used = 0;
        bool ordered = r.ReadBit();
        if (!ordered)
        {
            bool sparse = r.ReadBit();
            for (int i = 0; i < entries; i++)
            {
                if (sparse && !r.ReadBit()) continue;
                lengths[i] = (byte)(r.Read(5) + 1);
                used++;
                if (r.EndOfPacket) throw new AudioFormatException("Vorbis codebook truncated.");
            }
        }
        else
        {
            int cur = 0;
            int len = (int)r.Read(5) + 1;
            while (cur < entries)
            {
                int count = (int)r.Read(ILog(entries - cur));
                if (r.EndOfPacket) throw new AudioFormatException("Vorbis codebook truncated.");
                if (len > 32 || count > entries - cur) throw new AudioFormatException("Vorbis codebook length run is out of range.");
                for (int i = 0; i < count; i++) lengths[cur + i] = (byte)len;
                cur += count;
                used += count;
                len++;
            }
        }
        if (r.EndOfPacket) throw new AudioFormatException("Vorbis codebook truncated.");

        var book = new VorbisCodebook { Dimensions = dims, Entries = entries };
        book.BuildHuffman(lengths, used);

        int lookup = (int)r.Read(4);
        book.LookupType = lookup;
        if (lookup > 2) throw new AudioFormatException("Vorbis codebook lookup type is reserved.");
        if (lookup != 0)
        {
            double min = Float32Unpack(r.Read(32));
            double delta = Float32Unpack(r.Read(32));
            int valueBits = (int)r.Read(4) + 1;
            bool sequence = r.ReadBit();
            long lookupValues = lookup == 1 ? Lookup1Values(entries, dims) : (long)entries * dims;
            Charge(limits, ref budget, 4L * lookupValues + 4L * entries * dims);
            if (lookupValues > r.BitsRemaining / valueBits) throw new AudioFormatException("Vorbis codebook truncated.");
            var mult = new uint[lookupValues];
            for (long i = 0; i < lookupValues; i++) mult[i] = r.Read(valueBits);
            if (r.EndOfPacket) throw new AudioFormatException("Vorbis codebook truncated.");

            var vectors = new float[(long)entries * dims];
            for (int e = 0; e < entries; e++)
            {
                double last = 0;
                long indexDivisor = 1;
                for (int d = 0; d < dims; d++)
                {
                    long off = lookup == 1 ? (e / indexDivisor) % lookupValues : (long)e * dims + d;
                    double v = mult[off] * delta + min + last;
                    vectors[(long)e * dims + d] = (float)v;
                    if (sequence) last = v;
                    if (lookup == 1) indexDivisor *= lookupValues;
                    if (indexDivisor > int.MaxValue) indexDivisor = int.MaxValue; // later digits are then all zero
                }
            }
            book.Vectors = vectors;
        }
        return book;
    }

    private static void Charge(AudioLimits limits, ref long budget, long bytes)
    {
        limits.CheckSetupBytes(limits.MaxSetupBytes - budget + bytes);
        budget -= bytes;
    }

    private void BuildHuffman(byte[] lengths, int used)
    {
        if (used == 0)
        {
            // Legal in practice (a book that is never used for decoding); any attempt to read from it fails.
            _primaryBits = 1;
            _table = new int[2];
            return;
        }
        int maxLen = 0;
        foreach (byte l in lengths) if (l > maxLen) maxLen = l;
        _primaryBits = Math.Min(maxLen, PrimaryBits);
        _table = new int[1 << _primaryBits];

        if (used == 1)
        {
            // Errata 20150226: a lone codeword has length 1; either bit value decodes to it.
            int only = Array.FindIndex(lengths, l => l != 0);
            if (lengths[only] != 1) throw new AudioFormatException("Vorbis single-entry codebook must use a 1-bit codeword.");
            _primaryBits = 1;
            _table = [(only << 6) | 1, (only << 6) | 1];
            return;
        }

        // Assign codewords: each entry in order gets the lowest-valued free codeword of its length (section 3.2.1).
        // The free codeword space is kept as a list of aligned blocks over a 32-bit space.
        Span<ulong> fstart = stackalloc ulong[80];
        Span<int> flen = stackalloc int[80];
        int fcount = 1;
        fstart[0] = 0;
        flen[0] = 0;
        var codes = new uint[lengths.Length];
        int longCount = 0;
        for (int e = 0; e < lengths.Length; e++)
        {
            int L = lengths[e];
            if (L == 0) continue;
            int best = -1;
            for (int k = 0; k < fcount; k++)
                if (flen[k] <= L && (best < 0 || fstart[k] < fstart[best])) best = k;
            if (best < 0) throw new AudioFormatException("Vorbis codebook is overspecified.");
            ulong start = fstart[best];
            int blen = flen[best];
            fstart[best] = fstart[--fcount];
            flen[best] = flen[fcount];
            if (fcount + (L - blen) > 80) throw new AudioFormatException("Vorbis codebook is malformed.");
            for (int k = L; k > blen; k--)
            {
                fstart[fcount] = start + (1UL << (32 - k));
                flen[fcount] = k;
                fcount++;
            }
            codes[e] = (uint)(start >> (32 - L)) ;
            if (L > PrimaryBits) longCount++;
        }
        if (fcount != 0) throw new AudioFormatException("Vorbis codebook is underspecified.");

        for (int e = 0; e < lengths.Length; e++)
        {
            int L = lengths[e];
            if (L == 0 || L > PrimaryBits) continue;
            // The stream delivers the codeword's first bit in the least significant position.
            uint rev = ReverseBits(codes[e]) >> (32 - L);
            int step = 1 << L;
            int val = (e << 6) | L;
            for (int idx = (int)rev; idx < _table.Length; idx += step) _table[idx] = val;
        }

        if (longCount > 0)
        {
            var keys = new uint[longCount];
            var entriesArr = new int[longCount];
            int n = 0;
            for (int e = 0; e < lengths.Length; e++)
            {
                if (lengths[e] <= PrimaryBits) continue;
                keys[n] = codes[e] << (32 - lengths[e]);
                entriesArr[n++] = e;
            }
            Array.Sort(keys, entriesArr);
            _longCodes = keys;
            _longEntries = entriesArr;
            _longLengths = new byte[longCount];
            for (int i = 0; i < longCount; i++) _longLengths[i] = lengths[entriesArr[i]];
        }
    }

    internal static uint ReverseBits(uint v)
    {
        v = ((v >> 1) & 0x55555555u) | ((v & 0x55555555u) << 1);
        v = ((v >> 2) & 0x33333333u) | ((v & 0x33333333u) << 2);
        v = ((v >> 4) & 0x0F0F0F0Fu) | ((v & 0x0F0F0F0Fu) << 4);
        return BinaryPrimitives.ReverseEndianness(v);
    }

    /// <summary>Position of the highest set bit, 0 for values of zero or less (section 9.2.1).</summary>
    public static int ILog(int x) => x <= 0 ? 0 : 32 - BitOperations.LeadingZeroCount((uint)x);

    /// <summary>Section 9.2.2.</summary>
    public static double Float32Unpack(uint x)
    {
        double mantissa = x & 0x1FFFFF;
        if ((x & 0x80000000) != 0) mantissa = -mantissa;
        int exponent = (int)((x & 0x7FE00000) >> 21);
        return Math.ScaleB(mantissa, exponent - 788);
    }

    /// <summary>The greatest integer whose <paramref name="dims"/>-th power is at most <paramref name="entries"/> (section 9.2.3).</summary>
    public static long Lookup1Values(int entries, int dims)
    {
        long r = (long)Math.Floor(Math.Pow(entries, 1.0 / dims));
        if (r < 1) r = 1;
        while (PowExceeds(r + 1, dims, entries) == false) r++;
        while (r > 1 && PowExceeds(r, dims, entries)) r--;
        return r;
    }

    private static bool PowExceeds(long b, int dims, long limit)
    {
        long p = 1;
        for (int i = 0; i < dims; i++)
        {
            p *= b;
            if (p > limit) return true;
            if (b == 1) return p > limit;
        }
        return p > limit;
    }
}
