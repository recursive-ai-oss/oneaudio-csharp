using System.Buffers.Binary;
using System.Text;

namespace OneAudio.Vorbis;

internal sealed class VorbisMapping
{
    public int Submaps;
    public int[] Magnitude = [];
    public int[] Angle = [];
    public int[] Mux = [];
    public int[] SubmapFloor = [];
    public int[] SubmapResidue = [];
}

internal readonly record struct VorbisMode(bool LongBlock, int Mapping);

/// <summary>The three Vorbis headers decoded into a ready-to-use configuration (Vorbis I specification section 4.2).</summary>
internal sealed class VorbisSetup
{
    public int Channels { get; private set; }
    public int SampleRate { get; private set; }
    public int BlockSize0 { get; private set; }
    public int BlockSize1 { get; private set; }
    public VorbisCodebook[] Books { get; private set; } = [];
    public VorbisFloor[] Floors { get; private set; } = [];
    public VorbisResidue[] Residues { get; private set; } = [];
    public VorbisMapping[] Mappings { get; private set; } = [];
    public VorbisMode[] Modes { get; private set; } = [];
    public string Vendor { get; private set; } = "";
    public IReadOnlyList<string> Comments { get; private set; } = [];

    /// <summary>Block size in samples for a mode's flag.</summary>
    public int BlockSizeOf(VorbisMode m) => m.LongBlock ? BlockSize1 : BlockSize0;

    /// <summary>
    /// The block size of an audio packet, read from its mode number without decoding it; 0 when the packet is not
    /// audio or names a mode that does not exist.
    /// </summary>
    public int PeekBlockSize(ReadOnlySpan<byte> packet)
    {
        var r = new VorbisBitReader(packet);
        if (r.ReadBit()) return 0;
        int mode = (int)r.Read(VorbisCodebook.ILog(Modes.Length - 1));
        if (r.EndOfPacket || mode >= Modes.Length) return 0;
        return BlockSizeOf(Modes[mode]);
    }

    private static void CheckHeader(ReadOnlySpan<byte> p, byte type)
    {
        if (p.Length < 7 || p[0] != type || !p[1..7].SequenceEqual("vorbis"u8))
            throw new AudioFormatException($"Expected Vorbis header packet type {type}.");
    }

    /// <summary>Parses the identification header (section 4.2.2) and checks it against the limits.</summary>
    public static VorbisSetup ParseIdentification(ReadOnlySpan<byte> packet, AudioLimits limits)
    {
        CheckHeader(packet, 1);
        var r = new VorbisBitReader(packet[7..]);
        uint version = r.Read(32);
        int channels = (int)r.Read(8);
        uint rate = r.Read(32);
        r.Read(32); r.Read(32); r.Read(32); // bitrate hints
        int b0 = (int)r.Read(4);
        int b1 = (int)r.Read(4);
        bool framing = r.ReadBit();
        if (r.EndOfPacket) throw new AudioFormatException("Vorbis identification header truncated.");
        if (version != 0) throw new UnsupportedAudioFormatException($"Vorbis version {version} is not supported.");
        limits.CheckChannels(channels);
        if (rate == 0) throw new AudioFormatException("Vorbis sample rate is zero.");
        limits.CheckSampleRate(rate);
        if (b0 < 6 || b0 > 13 || b1 < 6 || b1 > 13 || b0 > b1) throw new AudioFormatException("Vorbis block sizes are out of range.");
        if (!framing) throw new AudioFormatException("Vorbis identification header framing bit is not set.");
        limits.CheckFrameSize(1 << b1);
        return new VorbisSetup { Channels = channels, SampleRate = (int)rate, BlockSize0 = 1 << b0, BlockSize1 = 1 << b1 };
    }

    /// <summary>Parses the comment header (section 5). A damaged comment header is not fatal: the tags are just left empty.</summary>
    public void ParseComments(ReadOnlySpan<byte> packet, AudioLimits limits)
    {
        CheckHeader(packet, 3);
        limits.CheckMetadataBytes(packet.Length);
        var d = packet[7..];
        int pos = 0;
        try
        {
            string vendor = ReadString(d, ref pos);
            uint count = ReadU32(d, ref pos);
            var list = new List<string>();
            for (uint i = 0; i < count; i++)
            {
                if (pos + 4 > d.Length) break;
                list.Add(ReadString(d, ref pos));
            }
            Vendor = vendor;
            Comments = list;
        }
        catch (FormatException)
        {
            // Non-fatal by specification.
        }
    }

    private static uint ReadU32(ReadOnlySpan<byte> d, ref int pos)
    {
        if (pos + 4 > d.Length) throw new FormatException();
        uint v = BinaryPrimitives.ReadUInt32LittleEndian(d[pos..]);
        pos += 4;
        return v;
    }

    private static string ReadString(ReadOnlySpan<byte> d, ref int pos)
    {
        uint len = ReadU32(d, ref pos);
        if (len > d.Length - pos) throw new FormatException();
        string s = Encoding.UTF8.GetString(d.Slice(pos, (int)len));
        pos += (int)len;
        return s;
    }

    /// <summary>Parses the setup header (section 4.2.4).</summary>
    public void ParseSetup(ReadOnlySpan<byte> packet, AudioLimits limits)
    {
        CheckHeader(packet, 5);
        var r = new VorbisBitReader(packet[7..]);
        long budget = limits.MaxSetupBytes;

        int bookCount = (int)r.Read(8) + 1;
        var books = new VorbisCodebook[bookCount];
        for (int i = 0; i < bookCount; i++) books[i] = VorbisCodebook.Parse(ref r, limits, ref budget);

        int timeCount = (int)r.Read(6) + 1;
        for (int i = 0; i < timeCount; i++)
            if (r.Read(16) != 0) throw new AudioFormatException("Vorbis time-domain transform placeholder is not zero.");

        int floorCount = (int)r.Read(6) + 1;
        var floors = new VorbisFloor[floorCount];
        for (int i = 0; i < floorCount; i++)
        {
            int type = (int)r.Read(16);
            floors[i] = type switch
            {
                0 => VorbisFloor0.Parse(ref r, books),
                1 => VorbisFloor1.Parse(ref r, bookCount),
                _ => throw new AudioFormatException($"Vorbis floor type {type} is not valid."),
            };
        }

        int residueCount = (int)r.Read(6) + 1;
        var residues = new VorbisResidue[residueCount];
        for (int i = 0; i < residueCount; i++)
        {
            int type = (int)r.Read(16);
            if (type > 2) throw new AudioFormatException($"Vorbis residue type {type} is not valid.");
            residues[i] = VorbisResidue.Parse(ref r, type, books);
            residues[i].Prepare(books, limits, ref budget, Channels, BlockSize1);
        }

        int mappingCount = (int)r.Read(6) + 1;
        var mappings = new VorbisMapping[mappingCount];
        int chBits = VorbisCodebook.ILog(Channels - 1);
        for (int i = 0; i < mappingCount; i++)
        {
            if (r.Read(16) != 0) throw new AudioFormatException("Vorbis mapping type is not valid.");
            var m = new VorbisMapping { Submaps = r.ReadBit() ? (int)r.Read(4) + 1 : 1 };
            int steps = 0;
            if (r.ReadBit())
            {
                steps = (int)r.Read(8) + 1;
                m.Magnitude = new int[steps];
                m.Angle = new int[steps];
                for (int j = 0; j < steps; j++)
                {
                    m.Magnitude[j] = (int)r.Read(chBits);
                    m.Angle[j] = (int)r.Read(chBits);
                    if (m.Angle[j] == m.Magnitude[j] || m.Magnitude[j] >= Channels || m.Angle[j] >= Channels)
                        throw new AudioFormatException("Vorbis channel coupling refers to invalid channels.");
                }
            }
            if (r.Read(2) != 0) throw new AudioFormatException("Vorbis mapping reserved bits are not zero.");
            m.Mux = new int[Channels];
            if (m.Submaps > 1)
            {
                for (int j = 0; j < Channels; j++)
                {
                    m.Mux[j] = (int)r.Read(4);
                    if (m.Mux[j] >= m.Submaps) throw new AudioFormatException("Vorbis mapping multiplex refers to a missing submap.");
                }
            }
            m.SubmapFloor = new int[m.Submaps];
            m.SubmapResidue = new int[m.Submaps];
            for (int j = 0; j < m.Submaps; j++)
            {
                r.Read(8);
                m.SubmapFloor[j] = (int)r.Read(8);
                if (m.SubmapFloor[j] >= floorCount) throw new AudioFormatException("Vorbis mapping refers to a missing floor.");
                m.SubmapResidue[j] = (int)r.Read(8);
                if (m.SubmapResidue[j] >= residueCount) throw new AudioFormatException("Vorbis mapping refers to a missing residue.");
            }
            mappings[i] = m;
        }

        int modeCount = (int)r.Read(6) + 1;
        var modes = new VorbisMode[modeCount];
        for (int i = 0; i < modeCount; i++)
        {
            bool longBlock = r.ReadBit();
            if (r.Read(16) != 0 || r.Read(16) != 0) throw new AudioFormatException("Vorbis mode window or transform type is not valid.");
            int mapping = (int)r.Read(8);
            if (mapping >= mappingCount) throw new AudioFormatException("Vorbis mode refers to a missing mapping.");
            modes[i] = new VorbisMode(longBlock, mapping);
        }
        bool framing = r.ReadBit();
        if (r.EndOfPacket) throw new AudioFormatException("Vorbis setup header truncated.");
        if (!framing) throw new AudioFormatException("Vorbis setup header framing bit is not set.");

        foreach (var f in floors)
        {
            f.Prepare(0, BlockSize0 / 2);
            f.Prepare(1, BlockSize1 / 2);
        }
        Books = books;
        Floors = floors;
        Residues = residues;
        Mappings = mappings;
        Modes = modes;
    }
}
