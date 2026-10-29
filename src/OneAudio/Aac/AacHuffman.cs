namespace OneAudio.Aac;

/// <summary>
/// Table-driven decoder for one AAC Huffman codebook (spectral books 1-11 and the scalefactor book 12). A direct lookup
/// resolves codes up to <see cref="FastBits"/> bits; longer codes fall back to a binary search over the codes left-aligned
/// to the longest length. The books are complete prefix codes (the Kraft sum of each is exactly 1), so the search always
/// lands on the one matching entry.
/// </summary>
internal sealed class AacHuffman
{
    private const int FastBits = 9;

    private readonly int[] _fast = new int[1 << FastBits]; // (length << 16) | value, 0 when the code is longer
    private readonly uint[] _starts;                       // codes left-aligned to _maxLen bits
    private readonly byte[] _lengths;
    private readonly ushort[] _values;
    private readonly int _maxLen;

    private AacHuffman(ReadOnlySpan<byte> lengths, ReadOnlySpan<uint> codes, ReadOnlySpan<ushort> values)
    {
        int n = lengths.Length;
        int max = 0;
        for (int i = 0; i < n; i++) max = Math.Max(max, lengths[i]);
        _maxLen = max;
        var order = new int[n];
        for (int i = 0; i < n; i++) order[i] = i;
        var aligned = new uint[n];
        for (int i = 0; i < n; i++) aligned[i] = codes[i] << (max - lengths[i]);
        Array.Sort(aligned, order);
        _starts = aligned;
        _lengths = new byte[n];
        _values = new ushort[n];
        for (int i = 0; i < n; i++)
        {
            _lengths[i] = lengths[order[i]];
            _values[i] = values[order[i]];
        }
        for (int i = 0; i < n; i++)
        {
            int len = _lengths[i];
            if (len > FastBits) continue;
            int span = 1 << (FastBits - len);
            int start = (int)(_starts[i] >> (max - len)) << (FastBits - len);
            for (int k = 0; k < span; k++) _fast[start + k] = (len << 16) | _values[i];
        }
    }

    /// <summary>Decodes one symbol; the value is the packed form the generated tables use.</summary>
    public int Decode(ref AacBitReader br)
    {
        uint peek = br.Peek(FastBits);
        int f = _fast[peek];
        if (f != 0)
        {
            br.Skip(f >> 16);
            return f & 0xFFFF;
        }
        uint key = br.Peek(_maxLen);
        // Last entry whose left-aligned code is <= key.
        int lo = 0, hi = _starts.Length - 1;
        while (lo < hi)
        {
            int mid = (lo + hi + 1) >> 1;
            if (_starts[mid] <= key) lo = mid; else hi = mid - 1;
        }
        br.Skip(_lengths[lo]);
        return _values[lo];
    }

    /// <summary>Codebook descriptions indexed 0..12: dimension, bits per packed field, offset, and the decoder.</summary>
    internal static readonly AacHuffman?[] Books = Build();

    internal static ReadOnlySpan<byte> Dimension => [0, 4, 4, 4, 4, 2, 2, 2, 2, 2, 2, 2, 1];
    internal static ReadOnlySpan<byte> FieldBits => [0, 2, 2, 2, 2, 4, 4, 4, 4, 4, 4, 5, 8];
    internal static ReadOnlySpan<byte> Offset => [0, 1, 1, 0, 0, 4, 4, 0, 0, 0, 0, 0, 60];

    private static AacHuffman?[] Build()
    {
        var b = new AacHuffman?[13];
        b[1] = new AacHuffman(AacTables.Lengths1, AacTables.Codes1, AacTables.Values1);
        b[2] = new AacHuffman(AacTables.Lengths2, AacTables.Codes2, AacTables.Values2);
        b[3] = new AacHuffman(AacTables.Lengths3, AacTables.Codes3, AacTables.Values3);
        b[4] = new AacHuffman(AacTables.Lengths4, AacTables.Codes4, AacTables.Values4);
        b[5] = new AacHuffman(AacTables.Lengths5, AacTables.Codes5, AacTables.Values5);
        b[6] = new AacHuffman(AacTables.Lengths6, AacTables.Codes6, AacTables.Values6);
        b[7] = new AacHuffman(AacTables.Lengths7, AacTables.Codes7, AacTables.Values7);
        b[8] = new AacHuffman(AacTables.Lengths8, AacTables.Codes8, AacTables.Values8);
        b[9] = new AacHuffman(AacTables.Lengths9, AacTables.Codes9, AacTables.Values9);
        b[10] = new AacHuffman(AacTables.Lengths10, AacTables.Codes10, AacTables.Values10);
        b[11] = new AacHuffman(AacTables.Lengths11, AacTables.Codes11, AacTables.Values11);
        b[12] = new AacHuffman(AacTables.Lengths12, AacTables.Codes12, AacTables.Values12);
        return b;
    }
}
