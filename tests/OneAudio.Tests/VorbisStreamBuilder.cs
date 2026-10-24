namespace OneAudio.Tests;

/// <summary>Writes bits least significant first, as the Vorbis bit packing convention requires.</summary>
internal sealed class VorbisBitWriter
{
    private readonly List<byte> _bytes = [];
    private int _bit;

    public void Write(long value, int bits)
    {
        for (int i = 0; i < bits; i++)
        {
            if (_bit == 0) _bytes.Add(0);
            if (((value >> i) & 1) != 0) _bytes[^1] |= (byte)(1 << _bit);
            _bit = (_bit + 1) & 7;
        }
    }

    /// <summary>Writes a Huffman codeword: its first bit is the first bit of the stream.</summary>
    public void WriteCodeword(int code, int length)
    {
        for (int i = length - 1; i >= 0; i--) Write((code >> i) & 1, 1);
    }

    public byte[] ToArray() => [.. _bytes];
}

/// <summary>
/// A small Vorbis stream generator for tests: valid headers (one floor 1 curve per channel, one VQ residue book, optional
/// channel coupling, a short and a long mode) and audio packets of pseudo-random residue under a sloped floor. The audio is
/// noise, but it exercises window shapes, floors, residue types 0/1/2, coupling and channel layouts, and it decodes in
/// libvorbis, so the output can be cross-checked.
/// </summary>
internal sealed class VorbisStreamBuilder
{
    public int Channels { get; init; } = 2;
    public int SampleRate { get; init; } = 44100;
    public int ShortExp { get; init; } = 8;
    public int LongExp { get; init; } = 11;
    public int ResidueType { get; init; } = 1;
    public bool Coupling { get; init; } = true;
    public int Seed { get; init; } = 1;

    /// <summary>
    /// Residue partition size. For residue type 2 it must be a multiple of the channel count: libvorbis restarts the
    /// channel interleave at every partition, which only agrees with the specification's single flat vector when the
    /// partition size is a multiple of the channel count (encoders never write anything else).
    /// </summary>
    private int PartitionSize
    {
        get
        {
            int odd = Channels;
            while ((odd & 1) == 0) odd >>= 1;
            return 8 * odd;
        }
    }

    private int Short => 1 << ShortExp;
    private int Long => 1 << LongExp;

    public byte[] Identification()
    {
        var w = new VorbisBitWriter();
        w.Write(1, 8);
        foreach (char c in "vorbis") w.Write(c, 8);
        w.Write(0, 32); w.Write(Channels, 8); w.Write(SampleRate, 32);
        w.Write(0, 32); w.Write(0, 32); w.Write(0, 32);
        w.Write(ShortExp, 4); w.Write(LongExp, 4); w.Write(1, 1);
        return w.ToArray();
    }

    public static byte[] Comment()
    {
        var w = new VorbisBitWriter();
        w.Write(3, 8);
        foreach (char c in "vorbis") w.Write(c, 8);
        const string vendor = "oneaudio tests";
        w.Write(vendor.Length, 32);
        foreach (char c in vendor) w.Write(c, 8);
        w.Write(1, 32);
        const string tag = "TITLE=generated";
        w.Write(tag.Length, 32);
        foreach (char c in tag) w.Write(c, 8);
        w.Write(1, 1);
        return w.ToArray();
    }

    public byte[] Setup()
    {
        var w = new VorbisBitWriter();
        w.Write(5, 8);
        foreach (char c in "vorbis") w.Write(c, 8);

        w.Write(1, 8); // two codebooks
        // Book 0: two entries of one bit, the classification book.
        WriteBookHeader(w, dims: 1, entries: 2);
        w.Write(0, 1); w.Write(0, 1); // not ordered, not sparse
        w.Write(0, 5); w.Write(0, 5); // lengths 1, 1
        w.Write(0, 4);                // no lookup
        // Book 1: sixteen four-bit entries, two dimensions, a 4x4 lattice of values -1.5 .. 1.5.
        WriteBookHeader(w, dims: 2, entries: 16);
        w.Write(0, 1); w.Write(0, 1);
        for (int i = 0; i < 16; i++) w.Write(3, 5);
        w.Write(1, 4);                                  // lookup type 1
        w.Write(0x80000000u | (787u << 21) | 3u, 32);   // minimum -1.5
        w.Write((788u << 21) | 1u, 32);                 // delta 1.0
        w.Write(1, 4); w.Write(0, 1);                   // 2-bit values, no sequence
        for (int i = 0; i < 4; i++) w.Write(i, 2);

        w.Write(0, 6); w.Write(0, 16);                  // one time placeholder

        w.Write(0, 6); w.Write(1, 16);                  // one floor, type 1
        w.Write(0, 5);                                  // no partitions
        w.Write(1, 2);                                  // multiplier 2 (range 128)
        w.Write(8, 4);                                  // x list is 0 and 256

        w.Write(0, 6); w.Write(ResidueType, 16);        // one residue
        w.Write(0, 24); w.Write(1 << 20, 24); w.Write(PartitionSize - 1, 24); // begin 0, end past any block
        w.Write(1, 6);                                  // two classifications
        w.Write(0, 8);                                  // class book 0
        w.Write(0, 3); w.Write(0, 1);                   // class 0: no passes
        w.Write(1, 3); w.Write(0, 1);                   // class 1: pass 0
        w.Write(1, 8);                                  // pass 0 uses book 1

        w.Write(0, 6); w.Write(0, 16);                  // one mapping
        w.Write(0, 1);                                  // one submap
        if (Coupling && Channels >= 2)
        {
            w.Write(1, 1); w.Write(0, 8);               // one coupling step
            int bits = ILog(Channels - 1);
            w.Write(0, bits); w.Write(1, bits);         // magnitude channel 0, angle channel 1
        }
        else w.Write(0, 1);
        w.Write(0, 2);
        w.Write(0, 8); w.Write(0, 8); w.Write(0, 8);    // unused, floor 0, residue 0

        w.Write(1, 6);                                  // two modes
        w.Write(0, 1); w.Write(0, 16); w.Write(0, 16); w.Write(0, 8);
        w.Write(1, 1); w.Write(0, 16); w.Write(0, 16); w.Write(0, 8);
        w.Write(1, 1);
        return w.ToArray();
    }

    private static void WriteBookHeader(VorbisBitWriter w, int dims, int entries)
    {
        w.Write(0x564342, 24); w.Write(dims, 16); w.Write(entries, 24);
    }

    private static int ILog(int x) { int n = 0; while (x > 0) { n++; x >>= 1; } return n; }

    /// <summary>Block flag (true = long) for each audio packet: pseudo-random with a bias towards long blocks.</summary>
    public bool[] BlockPattern(int count)
    {
        var rng = new Random(Seed);
        var p = new bool[count];
        for (int i = 0; i < count; i++) p[i] = rng.Next(4) != 0;
        return p;
    }

    public List<byte[]> AudioPackets(bool[] longBlocks)
    {
        var rng = new Random(Seed * 7919 + 1);
        var list = new List<byte[]>();
        for (int i = 0; i < longBlocks.Length; i++)
        {
            bool isLong = longBlocks[i];
            var w = new VorbisBitWriter();
            w.Write(0, 1);
            w.Write(isLong ? 1 : 0, 1);
            if (isLong)
            {
                w.Write(i > 0 && longBlocks[i - 1] ? 1 : 0, 1);
                w.Write(i + 1 < longBlocks.Length && longBlocks[i + 1] ? 1 : 0, 1);
            }
            for (int c = 0; c < Channels; c++)
            {
                w.Write(1, 1);                       // floor nonzero
                w.Write(rng.Next(60, 110), 7);
                w.Write(rng.Next(60, 110), 7);
            }
            int n2 = (isLong ? Long : Short) / 2;
            int vectors = ResidueType == 2 ? 1 : Channels;
            int length = ResidueType == 2 ? n2 * Channels : n2;
            int partitions = length / PartitionSize;
            for (int pc = 0; pc < partitions; pc++)
            {
                var cls = new int[vectors];
                for (int j = 0; j < vectors; j++)
                {
                    cls[j] = rng.Next(2);
                    w.WriteCodeword(cls[j], 1);
                }
                for (int j = 0; j < vectors; j++)
                {
                    if (cls[j] == 0) continue;
                    for (int k = 0; k < PartitionSize / 2; k++) w.WriteCodeword(rng.Next(16), 4); // 2-dimensional vectors
                }
            }
            list.Add(w.ToArray());
        }
        return list;
    }

    /// <summary>The granule position after each audio packet when every packet is decoded (the first packet yields none).</summary>
    public long[] Granules(bool[] longBlocks)
    {
        var g = new long[longBlocks.Length];
        long total = 0;
        for (int i = 0; i < g.Length; i++)
        {
            if (i > 0) total += (longBlocks[i - 1] ? Long : Short) / 4 + (longBlocks[i] ? Long : Short) / 4;
            g[i] = total;
        }
        return g;
    }

    /// <summary>A complete Ogg Vorbis file of <paramref name="count"/> audio packets, with <paramref name="trimEnd"/> frames cut from the end.</summary>
    public byte[] BuildFile(int count, uint serial = 1, int trimEnd = 0, int packetsPerPage = 8)
    {
        var pattern = BlockPattern(count);
        var audio = AudioPackets(pattern);
        var granules = Granules(pattern);
        var packets = new List<byte[]> { Identification(), Comment(), Setup() };
        var gr = new List<long> { 0, 0, 0 };
        packets.AddRange(audio);
        for (int i = 0; i < audio.Count; i++)
        {
            bool lastOnPage = (i + 1) % packetsPerPage == 0 || i == audio.Count - 1;
            long g = granules[i];
            if (i == audio.Count - 1) g -= trimEnd;
            gr.Add(lastOnPage ? g : -1);
        }
        // Page breaks: after each group of packetsPerPage audio packets, and the setup header ends its page.
        var breaks = new HashSet<int> { 2 };
        for (int i = 0; i < audio.Count; i += packetsPerPage) breaks.Add(3 + Math.Min(i + packetsPerPage, audio.Count) - 1);
        var b = new OggBuilder();
        b.AddStream(serial, packets, gr, maxBody: 1 << 30, pageBreakAfter: breaks);
        return b.ToArray();
    }
}
