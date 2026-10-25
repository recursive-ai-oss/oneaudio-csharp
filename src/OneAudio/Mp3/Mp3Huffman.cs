namespace OneAudio.Mp3;

/// <summary>
/// A Layer III Huffman code table: a prefix lookup on the first bits of the codeword, then a walk down a small
/// binary trie for the rare longer codes. Built once from the (length, code) pairs of the format's tables; the
/// build validates that the code is prefix-free, so a mistake in a table shows up as an exception in the tests
/// rather than as bad audio.
/// </summary>
internal sealed class Mp3HuffmanTable
{
    private const int LookupBits = 9;
    private readonly int _lookupBits;
    private readonly int[] _lookup;  // >0: (length << 16) | symbol; 0: invalid; <0: ~trie node to continue from
    private readonly int[] _trie;    // two children per node: >0 node, <0 ~symbol leaf, 0 none

    /// <summary>Extra bits that follow an escaped value 15 (zero for tables without an escape).</summary>
    public int LinBits { get; }

    /// <summary>Values per axis of a pair table (2, 3, 4, 6, 8 or 16); 0 for the four-value (count1) tables.</summary>
    public int Dimension { get; }

    /// <param name="codes">Entries <c>(length &lt;&lt; 20) | code</c>, indexed by symbol.</param>
    /// <param name="dimension">Axis size of a pair table, or 0 for a four-value table.</param>
    /// <param name="linBits">Escape bits.</param>
    public Mp3HuffmanTable(uint[] codes, int dimension, int linBits)
    {
        LinBits = linBits;
        Dimension = dimension;
        int maxLen = 0;
        foreach (uint c in codes) maxLen = Math.Max(maxLen, (int)(c >> 20));
        _lookupBits = Math.Min(maxLen, LookupBits);

        var trie = new List<int> { 0, 0 };
        for (int sym = 0; sym < codes.Length; sym++)
        {
            int len = (int)(codes[sym] >> 20);
            uint code = codes[sym] & 0xFFFFF;
            if (len == 0) continue;
            // Symbols are stored as (x << 4 | y) for pair tables; four-value tables use v w x y bits.
            int node = 0;
            for (int b = len - 1; b >= 0; b--)
            {
                int bit = (int)((code >> b) & 1);
                int slot = node * 2 + bit;
                if (b == 0)
                {
                    if (trie[slot] != 0) throw new InvalidOperationException("Huffman table is not prefix-free.");
                    trie[slot] = ~SymbolFor(sym);
                }
                else
                {
                    int child = trie[slot];
                    if (child < 0) throw new InvalidOperationException("Huffman table is not prefix-free.");
                    if (child == 0)
                    {
                        child = trie.Count / 2;
                        trie.Add(0); trie.Add(0);
                        trie[slot] = child;
                    }
                    node = child;
                }
            }
        }
        _trie = [.. trie];

        _lookup = new int[1 << _lookupBits];
        for (int prefix = 0; prefix < _lookup.Length; prefix++)
        {
            int node = 0;
            int entry = 0;
            for (int d = 0; d < _lookupBits; d++)
            {
                int bit = (prefix >> (_lookupBits - 1 - d)) & 1;
                int c = _trie[node * 2 + bit];
                if (c < 0) { entry = ((d + 1) << 16) | ~c; break; }
                if (c == 0) { entry = 0; break; }
                node = c;
                if (d == _lookupBits - 1) entry = ~node;
            }
            _lookup[prefix] = entry;
        }
    }

    // Pair tables index symbols x * dimension + y; the reader wants x and y separately in one small integer.
    private int SymbolFor(int index) => Dimension == 0 ? index : ((index / Dimension) << 4) | (index % Dimension);

    /// <summary>
    /// Decodes one symbol. Returns <c>(x &lt;&lt; 4) | y</c> for pair tables and <c>v&lt;&lt;3 | w&lt;&lt;2 | x&lt;&lt;1 | y</c> for
    /// the four-value tables, or -1 when the bits match no codeword.
    /// </summary>
    public int Decode(ref Mp3BitReader br)
    {
        int e = _lookup[br.Peek(_lookupBits)];
        if (e > 0)
        {
            br.Position += e >> 16;
            return e & 0xFFFF;
        }
        if (e == 0) return -1;
        // Codes longer than the lookup depth: continue bit by bit down the trie.
        int node = ~e;
        long pos = br.Position + _lookupBits;
        for (int guard = 0; guard < 24; guard++)
        {
            int c = _trie[node * 2 + (int)br.BitAt(pos)];
            pos++;
            if (c < 0) { br.Position = pos; return ~c; }
            if (c == 0) return -1;
            node = c;
        }
        return -1;
    }
}
