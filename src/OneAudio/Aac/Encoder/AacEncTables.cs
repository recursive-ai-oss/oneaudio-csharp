namespace OneAudio.Aac;

/// <summary>Receives the bits of a syntax element: either counts them or writes them (generic struct specialization keeps the encoder to one emission path).</summary>
internal interface IAacBitSink
{
    /// <summary>Appends the low <paramref name="bits"/> bits of <paramref name="value"/>, most significant first. <paramref name="bits"/> is 0 to 32.</summary>
    void Put(uint value, int bits);
}

/// <summary>Counts bits without storing them.</summary>
internal struct AacBitCounter : IAacBitSink
{
    public long Bits;
    public void Put(uint value, int bits) => Bits += bits;
}

/// <summary>MSB-first bit writer over a fixed buffer.</summary>
internal struct AacBitWriter : IAacBitSink
{
    private readonly byte[] _buf;
    private ulong _acc;
    private int _accBits;
    private int _bytes;

    public AacBitWriter(byte[] buffer)
    {
        _buf = buffer;
        _acc = 0;
        _accBits = 0;
        _bytes = 0;
    }

    public readonly long Bits => (long)_bytes * 8 + _accBits;
    public readonly int ByteLength => _bytes + (_accBits > 0 ? 1 : 0);

    public void Put(uint value, int bits)
    {
        if (bits == 0) return;
        _acc = (_acc << bits) | (value & (uint)((1UL << bits) - 1));
        _accBits += bits;
        while (_accBits >= 8)
        {
            _accBits -= 8;
            _buf[_bytes++] = (byte)(_acc >> _accBits);
        }
    }

    /// <summary>Pads with zero bits to a byte boundary.</summary>
    public void AlignZero()
    {
        if (_accBits > 0) Put(0, 8 - _accBits);
    }
}

/// <summary>
/// Encoder-side tables: the Huffman codes of ISO/IEC 14496-3 4.A.1 indexed by symbol (built from the same standard data the decoder
/// reads), the |q|^(4/3) table, scalefactor-band layouts per sampling rate, and the sampling-rate index list (4.5.1.2).
/// </summary>
internal static class AacEncTables
{
    /// <summary>Largest quantized magnitude (14496-3 4.6.3: escape sequences reach 8191).</summary>
    internal const int MaxQuant = 8191;

    /// <summary>Largest magnitude each spectral codebook 1..11 can carry without an escape (index 0 unused).</summary>
    internal static readonly int[] BookMax = [0, 1, 1, 2, 2, 4, 4, 7, 7, 12, 12, 16];

    /// <summary>Per codebook 1..12: code length by packed symbol (0 for symbols that do not exist).</summary>
    internal static readonly byte[][] Len = new byte[13][];

    /// <summary>Per codebook 1..12: code by packed symbol.</summary>
    internal static readonly uint[][] Code = new uint[13][];

    /// <summary>|q|^(4/3) for q = 0..8191 + 1 (the +1 absorbs a rounding overshoot before clamping).</summary>
    internal static readonly float[] Pow43 = BuildPow43();

    /// <summary>Sampling frequencies by index (14496-3 table 1.18).</summary>
    internal static readonly int[] Rates = [96000, 88200, 64000, 48000, 44100, 32000, 24000, 22050, 16000, 12000, 11025, 8000, 7350];

    static AacEncTables()
    {
        Build(1, AacTables.Lengths1, AacTables.Codes1, AacTables.Values1);
        Build(2, AacTables.Lengths2, AacTables.Codes2, AacTables.Values2);
        Build(3, AacTables.Lengths3, AacTables.Codes3, AacTables.Values3);
        Build(4, AacTables.Lengths4, AacTables.Codes4, AacTables.Values4);
        Build(5, AacTables.Lengths5, AacTables.Codes5, AacTables.Values5);
        Build(6, AacTables.Lengths6, AacTables.Codes6, AacTables.Values6);
        Build(7, AacTables.Lengths7, AacTables.Codes7, AacTables.Values7);
        Build(8, AacTables.Lengths8, AacTables.Codes8, AacTables.Values8);
        Build(9, AacTables.Lengths9, AacTables.Codes9, AacTables.Values9);
        Build(10, AacTables.Lengths10, AacTables.Codes10, AacTables.Values10);
        Build(11, AacTables.Lengths11, AacTables.Codes11, AacTables.Values11);
        Build(12, AacTables.Lengths12, AacTables.Codes12, AacTables.Values12);
    }

    private static void Build(int book, ReadOnlySpan<byte> lengths, ReadOnlySpan<uint> codes, ReadOnlySpan<ushort> values)
    {
        int max = 0;
        foreach (ushort v in values) max = Math.Max(max, v);
        var len = new byte[max + 1];
        var code = new uint[max + 1];
        for (int i = 0; i < values.Length; i++)
        {
            len[values[i]] = lengths[i];
            code[values[i]] = codes[i];
        }
        Len[book] = len;
        Code[book] = code;
    }

    private static float[] BuildPow43()
    {
        var t = new float[MaxQuant + 2];
        for (int i = 0; i < t.Length; i++) t[i] = (float)Math.Pow(i, 4.0 / 3.0);
        return t;
    }

    /// <summary>Index into <see cref="Rates"/> for a rate the encoder supports (8000 to 48000 Hz), else -1.</summary>
    internal static int RateIndex(int rate)
    {
        for (int i = 3; i <= 11; i++)
            if (Rates[i] == rate) return i;
        return -1;
    }

    /// <summary>Long and short scalefactor-band offsets for a sampling-rate index, matching 14496-3 tables 4.5 to 4.10.</summary>
    internal static void Bands(int rateIndex, out short[] longBands, out short[] shortBands)
    {
        switch (rateIndex)
        {
            case 3: case 4: longBands = AacTables.SfbLong48.ToArray(); shortBands = AacTables.SfbShort48.ToArray(); break;
            case 5: longBands = AacTables.SfbLong32.ToArray(); shortBands = AacTables.SfbShort48.ToArray(); break;
            case 6: case 7: longBands = AacTables.SfbLong24.ToArray(); shortBands = AacTables.SfbShort24.ToArray(); break;
            case 8: case 9: case 10: longBands = AacTables.SfbLong16.ToArray(); shortBands = AacTables.SfbShort16.ToArray(); break;
            case 11: longBands = AacTables.SfbLong8.ToArray(); shortBands = AacTables.SfbShort8.ToArray(); break;
            default: throw new ArgumentOutOfRangeException(nameof(rateIndex));
        }
    }
}
