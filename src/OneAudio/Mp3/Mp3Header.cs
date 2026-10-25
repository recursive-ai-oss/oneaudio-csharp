namespace OneAudio.Mp3;

/// <summary>MPEG audio version of a frame header.</summary>
internal enum MpegVersion
{
    /// <summary>MPEG-1 (32, 44.1, 48 kHz; 1152 samples per frame).</summary>
    Mpeg1 = 0,
    /// <summary>MPEG-2 low sampling frequencies (16, 22.05, 24 kHz; 576 samples per frame).</summary>
    Mpeg2 = 1,
    /// <summary>The MPEG-2.5 extension (8, 11.025, 12 kHz; 576 samples per frame).</summary>
    Mpeg25 = 2,
}

/// <summary>
/// A parsed 32-bit MPEG audio frame header. Field meanings follow the public Layer III bitstream description
/// (header syntax and Tables 7 to 13 of the Fraunhofer "MPEG-Layer3 Bitstream Syntax and Decoding" document).
/// </summary>
internal readonly struct Mp3Header
{
    private static readonly int[] Mpeg1Rates = [44100, 48000, 32000];
    private static readonly int[] Mpeg1Bitrates = [0, 32, 40, 48, 56, 64, 80, 96, 112, 128, 160, 192, 224, 256, 320];
    private static readonly int[] Mpeg2Bitrates = [0, 8, 16, 24, 32, 40, 48, 56, 64, 80, 96, 112, 128, 144, 160];

    public MpegVersion Version { get; }
    public int Layer { get; }
    public bool HasCrc { get; }
    public int BitrateIndex { get; }
    /// <summary>Bitrate in bits per second, or 0 for free format.</summary>
    public int Bitrate { get; }
    public int SampleRate { get; }
    public bool Padding { get; }
    public int ModeBits { get; }
    public int ModeExtension { get; }
    public uint Raw { get; }

    private Mp3Header(uint raw, MpegVersion version, int layer, int bitrateIndex, int bitrate, int rate)
    {
        Raw = raw; Version = version; Layer = layer; BitrateIndex = bitrateIndex; Bitrate = bitrate; SampleRate = rate;
        HasCrc = (raw & 0x10000) == 0;
        Padding = (raw & 0x200) != 0;
        ModeBits = (int)((raw >> 6) & 3);
        ModeExtension = (int)((raw >> 4) & 3);
    }

    public int Channels => ModeBits == 3 ? 1 : 2;
    public bool IsFreeFormat => BitrateIndex == 0;
    public int GranulesPerFrame => Version == MpegVersion.Mpeg1 ? 2 : 1;
    public int SamplesPerFrame => Layer == 3 ? 576 * GranulesPerFrame : Layer == 1 ? 384 : 1152;

    /// <summary>Side information size in bytes (Table 13 of the source document).</summary>
    public int SideInfoBytes => Version == MpegVersion.Mpeg1 ? (Channels == 1 ? 17 : 32) : (Channels == 1 ? 9 : 17);

    /// <summary>Bytes taken by the header and the optional CRC word.</summary>
    public int HeaderBytes => HasCrc ? 6 : 4;

    /// <summary>
    /// Frame length in bytes for a fixed bitrate, from the slot arithmetic of the source document:
    /// <c>144 * bitrate / sampling_frequency</c> (MPEG-1) or <c>72 * ...</c> (MPEG-2, 2.5), plus one slot of padding.
    /// Returns 0 in free format, where the length has to be measured from the stream.
    /// </summary>
    public int FrameBytes
    {
        get
        {
            if (Bitrate == 0) return 0;
            if (Layer != 3) return 0; // other layers are not decoded; their lengths are not needed
            int coef = Version == MpegVersion.Mpeg1 ? 144 : 72;
            return coef * Bitrate / SampleRate + (Padding ? 1 : 0);
        }
    }

    /// <summary>Parses the first four bytes as a header; false when they are not a valid MPEG audio frame header.</summary>
    public static bool TryParse(ReadOnlySpan<byte> b, out Mp3Header header)
    {
        header = default;
        if (b.Length < 4) return false;
        uint raw = (uint)(b[0] << 24 | b[1] << 16 | b[2] << 8 | b[3]);
        return TryParse(raw, out header);
    }

    public static bool TryParse(uint raw, out Mp3Header header)
    {
        header = default;
        if ((raw & 0xFFE00000) != 0xFFE00000) return false; // 11-bit syncword
        int idex = (int)((raw >> 20) & 1), id = (int)((raw >> 19) & 1);
        MpegVersion version;
        if (idex == 1) version = id == 1 ? MpegVersion.Mpeg1 : MpegVersion.Mpeg2;
        else if (id == 0) version = MpegVersion.Mpeg25;
        else return false; // reserved
        int layerBits = (int)((raw >> 17) & 3);
        if (layerBits == 0) return false;
        int layer = 4 - layerBits;
        int brIdx = (int)((raw >> 12) & 15);
        if (brIdx == 15) return false;
        int srIdx = (int)((raw >> 10) & 3);
        if (srIdx == 3) return false;
        int rate = Mpeg1Rates[srIdx];
        if (version == MpegVersion.Mpeg2) rate /= 2;
        else if (version == MpegVersion.Mpeg25) rate /= 4;
        int bitrate;
        if (layer == 3) bitrate = (version == MpegVersion.Mpeg1 ? Mpeg1Bitrates[brIdx] : Mpeg2Bitrates[brIdx]) * 1000;
        else bitrate = brIdx == 0 ? 0 : 1; // layers I and II are only recognised, never decoded
        header = new Mp3Header(raw, version, layer, brIdx, bitrate, rate);
        return true;
    }

    /// <summary>True when <paramref name="other"/> can follow this frame in one stream: same version, layer and sampling rate. The channel mode may change between frames.</summary>
    public bool IsCompatibleWith(in Mp3Header other) =>
        other.Version == Version && other.Layer == Layer && other.SampleRate == SampleRate;

    /// <summary>Index into the 9-entry band tables: 8, 11.025, 12, 16, 22.05, 24, 32, 44.1, 48 kHz.</summary>
    public int BandTableIndex => SampleRate switch
    {
        8000 => 0, 11025 => 1, 12000 => 2, 16000 => 3, 22050 => 4, 24000 => 5, 32000 => 6, 44100 => 7, _ => 8,
    };
}
