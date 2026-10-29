namespace OneAudio.Mp4;

/// <summary>Where an MP4 track's start offset and length come from.</summary>
public enum GaplessSource
{
    /// <summary>Nothing says anything about priming or trimming: the whole media is playable.</summary>
    None = 0,
    /// <summary>The track's edit list (ISO/IEC 14496-12 8.6.6).</summary>
    EditList = 1,
    /// <summary>The iTunes <c>iTunSMPB</c> gapless tag, which wins over an edit list when well formed.</summary>
    ITunSmpb = 2,
}

/// <summary>What the container says about one audio track of an MP4, M4A or 3GP file.</summary>
public sealed class Mp4TrackInfo
{
    internal Mp4TrackInfo() { }

    /// <summary>Position among the file's audio tracks, in file order; the argument of <see cref="Mp4PacketSource.SelectTrack"/>.</summary>
    public int Index { get; internal set; }

    /// <summary>The track_ID from the track header.</summary>
    public uint TrackId { get; internal set; }

    /// <summary>The four characters of the sample entry, for example <c>mp4a</c>, <c>samr</c> or <c>sawb</c>.</summary>
    public string SampleEntryType { get; internal set; } = "";

    /// <summary>The codec of the packets, or null when the sample entry is one this library does not read.</summary>
    public CodecId? Codec { get; internal set; }

    /// <summary>Why <see cref="Codec"/> is null, else null.</summary>
    public string? UnsupportedReason { get; internal set; }

    /// <summary>
    /// The decoder configuration exactly as stored: the AudioSpecificConfig for AAC (the descriptor payload of the <c>esds</c>
    /// box, untouched) and the <c>damr</c> payload for AMR.
    /// </summary>
    public ReadOnlyMemory<byte> CodecConfig { get; internal set; }

    /// <summary>For AAC, the audio object type from the config (2 = AAC-LC); 0 otherwise or when the config cannot be read.</summary>
    public int AacObjectType { get; internal set; }

    /// <summary>The MPEG-4 object type indication from the <c>esds</c> box (0x40 = MPEG-4 AAC), or 0.</summary>
    public int ObjectTypeIndication { get; internal set; }

    /// <summary>Sample rate in Hz: from the AudioSpecificConfig for AAC (the extension rate when SBR is signalled explicitly), else the sample entry; 8000 and 16000 for AMR.</summary>
    public int SampleRate { get; internal set; }

    /// <summary>Channel count: from the AudioSpecificConfig for AAC when it names a layout, else the sample entry; 1 for AMR.</summary>
    public int Channels { get; internal set; }

    /// <summary>Media time units per second (the <c>mdhd</c> timescale).</summary>
    public int TimeScale { get; internal set; }

    /// <summary>Number of samples (packets) in the track.</summary>
    public long SampleCount { get; internal set; }

    /// <summary>Sum of the sample durations, in <see cref="TimeScale"/> units.</summary>
    public long MediaTicks { get; internal set; }

    /// <summary>Decoder output frames, at <see cref="SampleRate"/>, to drop from the start (see <see cref="GaplessSource"/>).</summary>
    public int PrimingFrames { get; internal set; }

    /// <summary>Playable frames at <see cref="SampleRate"/> after priming and end trimming.</summary>
    public long LengthFrames { get; internal set; }

    /// <summary>Which rule produced <see cref="PrimingFrames"/> and <see cref="LengthFrames"/>.</summary>
    public GaplessSource Gapless { get; internal set; }

    /// <summary>The first non-empty edit's media_time in <see cref="TimeScale"/> units, or -1 when the track has no edit list.</summary>
    public long EditListMediaTime { get; internal set; } = -1;

    // Parse state, internal.
    internal Mp4SampleTable? Table;
    internal uint MediaTimeScale;
    internal EditEntry[] Edits = [];
    internal int EditCount;

    internal readonly record struct EditEntry(long SegmentDuration, long MediaTime);
}
