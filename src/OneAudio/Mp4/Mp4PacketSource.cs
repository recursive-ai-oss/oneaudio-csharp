using System.Buffers.Binary;
using System.Globalization;
using System.Text;

namespace OneAudio.Mp4;

/// <summary>
/// Reads the audio packets of an ISO base media file (ISO/IEC 14496-12): MP4, M4A, 3GP and 3G2, and QuickTime files with the same
/// layout. Only the movie box is parsed; the media data is read one sample at a time and never loaded whole.
/// </summary>
/// <remarks>
/// <para>The movie box may come before or after the media data. A forward-only stream works when it comes first; otherwise the
/// stream must be seekable. Fragmented files (<c>mvex</c>/<c>moof</c>) are rejected with <see cref="UnsupportedAudioFormatException"/>.</para>
/// <para>Packets are the file's samples, byte for byte: for AAC one raw_data_block, for AMR one table-of-contents byte followed by
/// the speech bits (<c>frames per sample</c> in the <c>damr</c> box tells how many frames a sample holds; the codec reads it from
/// <see cref="CodecConfig"/>). Sample-table entries, boxes and metadata are bounded by <see cref="AudioLimits"/> before anything is allocated.</para>
/// <para><b>Priming and trimming.</b> <see cref="PrimingFrames"/> and <see cref="LengthFrames"/> come from the iTunes
/// <c>iTunSMPB</c> tag when it is well formed and its delay plus length fits in the decoded media, otherwise from the first
/// non-empty edit of the track's edit list (media_time is the priming, segment_duration the length; later edits are ignored),
/// otherwise there is none: the whole media plays. The tag wins because that is what gapless players follow when both are
/// present and they disagree (encoders that write both put the full decoder delay in the tag and only the frame
/// overlap in the edit list). Leading empty edits (a delayed start) are not represented.</para>
/// </remarks>
public sealed class Mp4PacketSource : IPacketSource
{
    private const int MaxAudioTracks = 64;
    private const int MaxComments = 64;

    private readonly Mp4Reader _r;
    private readonly Stream _stream;
    private readonly AudioLimits _limits;
    private readonly CancellationToken _ct;
    private readonly List<Mp4TrackInfo> _tracks = [];
    private readonly List<string> _comments = [];
    private bool _leaveOpen;
    private bool _disposed;
    private long _boxCount;
    private long _entryBudget;
    private uint _movieTimeScale;
    private bool _fragmented;
    private string? _smpb;

    private Mp4TrackInfo _track = null!;
    private Mp4SampleTable _table = null!;
    private byte[] _buffer = [];
    private long _next;

    private Mp4PacketSource(Stream stream, AudioLimits limits, bool leaveOpen, CancellationToken ct)
    {
        _stream = stream;
        _r = new Mp4Reader(stream);
        _limits = limits;
        _leaveOpen = leaveOpen;
        _ct = ct;
        _entryBudget = 4L * limits.MaxSamples;
    }

    /// <summary>
    /// Parses the movie box of the file and selects its first audio track whose codec this library recognises (or the first audio
    /// track when none is recognised, which then throws). Use <see cref="SelectTrack"/> for another one.
    /// </summary>
    /// <param name="stream">The file. Must be seekable unless the movie box precedes the media data.</param>
    /// <param name="limits">Limits for untrusted input; <see cref="AudioLimits.Default"/> when null.</param>
    /// <param name="leaveOpen">Keep the stream open when the source is disposed.</param>
    /// <param name="cancellationToken">Observed while walking boxes.</param>
    /// <returns>A source positioned at the first packet.</returns>
    /// <exception cref="AudioFormatException">The file is not valid or is truncated.</exception>
    /// <exception cref="UnsupportedAudioFormatException">Fragmented file, no audio track, or no audio track with a supported codec.</exception>
    /// <exception cref="AudioLimitExceededException">A box asks for more than <paramref name="limits"/> allow.</exception>
    public static Mp4PacketSource Open(Stream stream, AudioLimits? limits = null, bool leaveOpen = false, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(stream);
        if (!stream.CanRead) throw new ArgumentException("Stream must be readable.", nameof(stream));
        var src = new Mp4PacketSource(stream, limits ?? AudioLimits.Default, leaveOpen, cancellationToken);
        try
        {
            src.Parse();
            return src;
        }
        catch
        {
            // The caller keeps the stream when opening fails.
            src._leaveOpen = true;
            src.Dispose();
            throw;
        }
    }

    // --- public surface -------------------------------------------------------------------------------------------

    /// <summary>The audio tracks in file order.</summary>
    public IReadOnlyList<Mp4TrackInfo> Tracks => _tracks;

    /// <summary>The track packets are currently read from.</summary>
    public Mp4TrackInfo SelectedTrack => _track;

    /// <summary>The major brand of the <c>ftyp</c> box (for example <c>M4A </c>, <c>isom</c>, <c>3gp4</c>), or empty when there is none.</summary>
    public string MajorBrand { get; private set; } = "";

    /// <summary>
    /// Tags as <c>KEY=value</c> strings in the same form as FLAC comments: TITLE, ARTIST, ALBUM, ALBUMARTIST, DATE, GENRE,
    /// COMPOSER and COMMENT from iTunes-style <c>ilst</c> items, and TITLE, ARTIST (3GPP author) and ALBUM from 3GPP
    /// <c>udta</c> boxes. At most 64 entries; cover art and other binary items are not read.
    /// </summary>
    public IReadOnlyList<string> Comments => _comments;

    /// <summary>The first TITLE tag, or null.</summary>
    public string? Title => Find("TITLE=");

    /// <summary>The first ARTIST tag, or null.</summary>
    public string? Artist => Find("ARTIST=");

    /// <summary>Playable duration of the selected track (<see cref="LengthFrames"/> at <see cref="SampleRate"/>).</summary>
    public TimeSpan Duration => SampleRate > 0 ? TimeSpan.FromSeconds((double)LengthFrames / SampleRate) : TimeSpan.Zero;

    /// <inheritdoc />
    public CodecId Codec => _track.Codec!.Value;

    /// <inheritdoc />
    public long LengthFrames => _track.LengthFrames;

    /// <inheritdoc />
    public bool CanSeek => _r.CanSeek;

    /// <inheritdoc />
    public ReadOnlyMemory<byte> CodecConfig => _track.CodecConfig;

    /// <inheritdoc />
    public int SampleRate => _track.SampleRate;

    /// <inheritdoc />
    public int Channels => _track.Channels;

    /// <inheritdoc />
    public int TimeScale => _track.TimeScale;

    /// <inheritdoc />
    public int PrimingFrames => _track.PrimingFrames;

    /// <summary>Switches to another audio track and rewinds to its first packet.</summary>
    /// <param name="index">Index into <see cref="Tracks"/>.</param>
    /// <exception cref="ArgumentOutOfRangeException">No such track.</exception>
    /// <exception cref="UnsupportedAudioFormatException">The track's sample entry is not one this library reads.</exception>
    public void SelectTrack(int index)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        if ((uint)index >= (uint)_tracks.Count) throw new ArgumentOutOfRangeException(nameof(index));
        var t = _tracks[index];
        if (t.Codec is null) throw new UnsupportedAudioFormatException($"Audio track {index} uses {t.UnsupportedReason}.");
        _track = t;
        _table = t.Table!;
        _limits.CheckPacketBytes(_table.MaxSampleSize);
        _buffer = new byte[_table.MaxSampleSize];
        _next = 0;
        _table.ResetCursor();
    }

    /// <inheritdoc />
    public bool TryReadPacket(out PacketView packet)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        if (_next >= _table.SampleCount)
        {
            packet = default;
            return false;
        }
        long sample = _next;
        long offset = _table.OffsetOf(sample);
        uint size = _table.SizeOf(sample);
        if (_r.Length >= 0 && (offset > _r.Length || size > _r.Length - offset))
            throw new AudioFormatException("A sample lies beyond the end of the file (truncated).");
        _r.Seek(offset);
        _r.ReadExactly(_buffer.AsSpan(0, (int)size));
        _table.Advance(sample, offset);
        long ts = _table.TimeOf(sample, out uint dur);
        long end = FramesFromTicks(ts + dur, _track.SampleRate, _track.TimeScale) - _track.PrimingFrames;
        end = Math.Clamp(end, 0, _track.LengthFrames);
        _next = sample + 1;
        packet = new PacketView(_buffer.AsSpan(0, (int)size), end, false, ts, dur);
        return true;
    }

    /// <summary>
    /// Positions on the packet whose time span contains <paramref name="frame"/>, counted after the priming
    /// (<see cref="PrimingFrames"/>). That is the packet to decode first; codecs with overlap ask for an earlier frame.
    /// A frame at or past <see cref="LengthFrames"/> positions at the end.
    /// </summary>
    /// <param name="frame">Target frame at <see cref="SampleRate"/>, from <c>-PrimingFrames</c> (the very start of the media) upwards.</param>
    /// <param name="startFrame">Frame at which the first returned packet begins; negative for packets inside the priming.</param>
    /// <exception cref="NotSupportedException">The stream is not seekable.</exception>
    /// <exception cref="ArgumentOutOfRangeException"><paramref name="frame"/> is before the start of the media (below <c>-PrimingFrames</c>).</exception>
    public void SeekToFrame(long frame, out long startFrame)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        if (!_r.CanSeek) throw new NotSupportedException("The stream is not seekable.");
        ArgumentOutOfRangeException.ThrowIfLessThan(frame, -(long)_track.PrimingFrames);
        if (frame >= _track.LengthFrames)
        {
            _next = _table.SampleCount;
            startFrame = _track.LengthFrames;
            return;
        }
        long ticks = TicksFromFrames(frame + _track.PrimingFrames, _track.SampleRate, _track.TimeScale);
        long sample = _table.SampleAt(ticks);
        _next = sample;
        startFrame = sample >= _table.SampleCount
            ? _track.LengthFrames
            : FramesFromTicks(_table.TimeOf(sample, out _), _track.SampleRate, _track.TimeScale) - _track.PrimingFrames;
    }

    /// <inheritdoc />
    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        if (!_leaveOpen) _stream.Dispose();
    }

    internal void Abandon()
    {
        _leaveOpen = true;
        Dispose();
    }

    private string? Find(string prefix)
    {
        foreach (var c in _comments)
            if (c.StartsWith(prefix, StringComparison.Ordinal)) return c[prefix.Length..];
        return null;
    }

    // --- time conversion ------------------------------------------------------------------------------------------

    internal static long FramesFromTicks(long ticks, int rate, int scale)
    {
        if (scale <= 0 || rate <= 0) return 0;
        return Saturate((Int128)ticks * rate / scale);
    }

    internal static long TicksFromFrames(long frames, int rate, int scale)
    {
        if (rate <= 0) return 0;
        return Saturate((Int128)frames * scale / rate);
    }

    private static long Saturate(Int128 v) => v > long.MaxValue ? long.MaxValue : v < long.MinValue ? long.MinValue : (long)v;

    // --- box walk -------------------------------------------------------------------------------------------------

    private void Parse()
    {
        long fileEnd = _r.Length >= 0 ? _r.Length : long.MaxValue;
        bool movieSeen = false;
        while (!movieSeen && NextBox(fileEnd, true, out var h))
        {
            switch (h.Type)
            {
                case BoxHeader.Ftyp:
                    {
                        var b = ReadPrefix(h, 4);
                        if (b.Length == 4) MajorBrand = Encoding.Latin1.GetString(b);
                        break;
                    }
                case BoxHeader.Moov:
                    ParseMoov(h.End);
                    movieSeen = true;
                    break;
                case BoxHeader.Moof:
                    throw new UnsupportedAudioFormatException("Fragmented MP4 files (moof boxes) are not supported.");
                case BoxHeader.Mdat:
                    if (!_r.CanSeek) throw new UnsupportedAudioFormatException("The movie box comes after the media data, which needs a seekable stream.");
                    break;
            }
            if (!movieSeen) _r.Seek(h.End);
        }
        if (!movieSeen) throw new AudioFormatException("The file has no movie (moov) box.");
        if (_fragmented) throw new UnsupportedAudioFormatException("Fragmented MP4 files (mvex boxes) are not supported.");

        foreach (var t in _tracks) Finish(t);
        if (_tracks.Count == 0) throw new UnsupportedAudioFormatException("The file has no audio track.");
        int pick = _tracks.FindIndex(t => t.Codec is not null);
        if (pick < 0) pick = 0;
        SelectTrack(pick);
    }

    private bool NextBox(long parentEnd, bool topLevel, out BoxHeader header)
    {
        header = default;
        _ct.ThrowIfCancellationRequested();
        long start = _r.Position;
        if (parentEnd - start < 8) return false; // trailing padding inside a container is ignored
        Span<byte> b = stackalloc byte[16];
        int got = _r.ReadUpTo(b[..8]);
        if (got < 8)
        {
            if (topLevel) return false;
            throw new AudioFormatException("The file ends inside a box header (truncated).");
        }
        _limits.CheckContainerElements(++_boxCount);
        ulong size = BinaryPrimitives.ReadUInt32BigEndian(b);
        uint type = BinaryPrimitives.ReadUInt32BigEndian(b[4..]);
        long payload = start + 8;
        if (size == 1)
        {
            _r.ReadExactly(b[8..16]);
            size = BinaryPrimitives.ReadUInt64BigEndian(b[8..]);
            payload = start + 16;
            if (size < 16) throw new AudioFormatException("A box has an invalid 64-bit size.");
        }
        else if (size == 0) size = (ulong)Math.Min(parentEnd - start, long.MaxValue - start);
        else if (size < 8) throw new AudioFormatException("A box has an invalid size.");
        if (type == BoxHeader.Uuid) payload += 16;
        long room = parentEnd - start;
        long end;
        if (size > (ulong)room)
        {
            // A media data box cut short by an interrupted write is tolerated; every other box must fit.
            if (!(topLevel && type == BoxHeader.Mdat)) throw new AudioFormatException("A box extends past the end of its container (truncated).");
            end = parentEnd;
        }
        else end = start + (long)size;
        if (payload > end) throw new AudioFormatException("A box is smaller than its header.");
        header = new BoxHeader(type, start, payload, end);
        return true;
    }

    /// <summary>Reads up to <paramref name="max"/> payload bytes (fewer if the box is smaller).</summary>
    private byte[] ReadPrefix(BoxHeader h, int max)
    {
        _r.Seek(h.PayloadStart);
        return _r.ReadBytes((int)Math.Min(max, h.PayloadSize));
    }

    private void ParseMoov(long end)
    {
        while (NextBox(end, false, out var h))
        {
            switch (h.Type)
            {
                case BoxHeader.Mvhd:
                    {
                        var b = ReadPrefix(h, 32);
                        int ts = b.Length >= 1 && b[0] == 1 ? 20 : 12;
                        if (b.Length >= ts + 4) _movieTimeScale = BinaryPrimitives.ReadUInt32BigEndian(b.AsSpan(ts));
                        break;
                    }
                case BoxHeader.Mvex:
                    _fragmented = true;
                    break;
                case BoxHeader.Trak:
                    ParseTrak(h);
                    break;
                case BoxHeader.Udta:
                    ParseUdta(h);
                    break;
                case BoxHeader.Meta:
                    ParseMeta(h);
                    break;
            }
            _r.Seek(h.End);
        }
    }

    private void ParseTrak(BoxHeader trak)
    {
        var t = new Mp4TrackInfo();
        uint handler = 0;
        bool audio = false;
        while (NextBox(trak.End, false, out var h))
        {
            switch (h.Type)
            {
                case BoxHeader.Tkhd:
                    {
                        var b = ReadPrefix(h, 24);
                        int idOffset = b.Length >= 1 && b[0] == 1 ? 20 : 12;
                        if (b.Length >= idOffset + 4) t.TrackId = BinaryPrimitives.ReadUInt32BigEndian(b.AsSpan(idOffset));
                        break;
                    }
                case BoxHeader.Edts:
                    ParseEdts(h, t);
                    break;
                case BoxHeader.Mdia:
                    audio = ParseMdia(h, t, ref handler);
                    break;
            }
            _r.Seek(h.End);
        }
        if (!audio) return;
        if (_tracks.Count >= MaxAudioTracks) return;
        if (t.Table is null && t.Codec is not null) throw new AudioFormatException("An audio track has no sample tables.");
        if (t.Codec is null) t.UnsupportedReason ??= "an unknown sample entry";
        t.Index = _tracks.Count;
        _tracks.Add(t);
    }

    private void ParseEdts(BoxHeader edts, Mp4TrackInfo t)
    {
        while (NextBox(edts.End, false, out var h))
        {
            if (h.Type == BoxHeader.Elst)
            {
                var head = ReadPrefix(h, 8);
                if (head.Length < 8) throw new AudioFormatException("An edit list is too short.");
                int entrySize = head[0] == 1 ? 20 : 12;
                uint count = BinaryPrimitives.ReadUInt32BigEndian(head.AsSpan(4));
                _limits.CheckContainerElements(count);
                if ((long)count * entrySize > h.PayloadSize - 8) throw new AudioFormatException("An edit list is shorter than its entry count says.");
                var body = _r.ReadBytes((int)count * entrySize);
                var edits = new Mp4TrackInfo.EditEntry[count];
                for (int i = 0; i < edits.Length; i++)
                {
                    var e = body.AsSpan(i * entrySize);
                    edits[i] = head[0] == 1
                        ? new Mp4TrackInfo.EditEntry(BinaryPrimitives.ReadInt64BigEndian(e), BinaryPrimitives.ReadInt64BigEndian(e[8..]))
                        : new Mp4TrackInfo.EditEntry(BinaryPrimitives.ReadUInt32BigEndian(e), BinaryPrimitives.ReadInt32BigEndian(e[4..]));
                }
                t.Edits = edits;
                t.EditCount = edits.Length;
            }
            _r.Seek(h.End);
        }
    }

    private bool ParseMdia(BoxHeader mdia, Mp4TrackInfo t, ref uint handler)
    {
        bool audio = false;
        BoxHeader? pending = null;
        while (NextBox(mdia.End, false, out var h))
        {
            switch (h.Type)
            {
                case BoxHeader.Mdhd:
                    {
                        var b = ReadPrefix(h, 36);
                        bool v1 = b.Length >= 1 && b[0] == 1;
                        int need = v1 ? 24 : 16;
                        if (b.Length < need) throw new AudioFormatException("A media header is too short.");
                        t.MediaTimeScale = BinaryPrimitives.ReadUInt32BigEndian(b.AsSpan(v1 ? 20 : 12));
                        break;
                    }
                case BoxHeader.Hdlr:
                    {
                        var b = ReadPrefix(h, 12);
                        if (b.Length >= 12) handler = BinaryPrimitives.ReadUInt32BigEndian(b.AsSpan(8));
                        break;
                    }
                case BoxHeader.Minf:
                    // The handler and media header normally come first; some writers put them after the media information box.
                    // Then it is read afterwards, which needs a seekable stream.
                    if (handler == 0 || t.MediaTimeScale == 0) pending = h;
                    else if (handler == BoxHeader.Soun) audio = ParseAudioMinf(h, t);
                    break;
            }
            _r.Seek(h.End);
        }
        if (pending is { } p && handler == BoxHeader.Soun)
        {
            _r.Seek(p.PayloadStart);
            audio = ParseAudioMinf(p, t);
        }
        return audio;
    }

    private bool ParseAudioMinf(BoxHeader minf, Mp4TrackInfo t)
    {
        if (t.MediaTimeScale == 0 || t.MediaTimeScale > int.MaxValue) throw new AudioFormatException("An audio track has no usable media timescale.");
        ParseMinf(minf, t);
        return true;
    }

    private void ParseMinf(BoxHeader minf, Mp4TrackInfo t)
    {
        while (NextBox(minf.End, false, out var h))
        {
            if (h.Type == BoxHeader.Stbl) ParseStbl(h, t);
            _r.Seek(h.End);
        }
    }

    private void ParseStbl(BoxHeader stbl, Mp4TrackInfo t)
    {
        uint[]? stts = null, stsc = null, sizes = null;
        long[]? offsets = null;
        long sampleCount = -1;
        uint uniform = 0;
        bool stsdSeen = false;
        while (NextBox(stbl.End, false, out var h))
        {
            // Once the description says the codec is not one we read, its tables are skipped.
            if (stsdSeen && t.Codec is null && h.Type is BoxHeader.Stts or BoxHeader.Stsc or BoxHeader.Stsz or BoxHeader.Stco or BoxHeader.Co64)
            {
                _r.Seek(h.End);
                continue;
            }
            switch (h.Type)
            {
                case BoxHeader.Stsd:
                    {
                        _limits.CheckSetupBytes(h.PayloadSize);
                        ParseStsd(_r.ReadBytes((int)h.PayloadSize), t);
                        stsdSeen = true;
                        break;
                    }
                case BoxHeader.Stts:
                    stts = ReadWordTable(h, 2);
                    break;
                case BoxHeader.Stsc:
                    stsc = ReadWordTable(h, 3);
                    break;
                case BoxHeader.Stsz:
                    {
                        var head = ReadPrefix(h, 12);
                        if (head.Length < 12) throw new AudioFormatException("A sample size table is too short.");
                        uniform = BinaryPrimitives.ReadUInt32BigEndian(head.AsSpan(4));
                        sampleCount = BinaryPrimitives.ReadUInt32BigEndian(head.AsSpan(8));
                        _limits.CheckSamples(sampleCount);
                        if (uniform == 0)
                        {
                            if (sampleCount * 4 > h.PayloadSize - 12) throw new AudioFormatException("A sample size table is shorter than its count says.");
                            Spend(sampleCount);
                            sizes = _r.ReadUInt32s((int)sampleCount);
                        }
                        break;
                    }
                case BoxHeader.Stz2:
                    throw new UnsupportedAudioFormatException("Compact sample size tables (stz2) are not supported.");
                case BoxHeader.Stco:
                    {
                        var words = ReadWordTable(h, 1);
                        offsets = new long[words.Length];
                        for (int i = 0; i < words.Length; i++) offsets[i] = words[i];
                        break;
                    }
                case BoxHeader.Co64:
                    {
                        var head = ReadPrefix(h, 8);
                        if (head.Length < 8) throw new AudioFormatException("A chunk offset table is too short.");
                        uint count = BinaryPrimitives.ReadUInt32BigEndian(head.AsSpan(4));
                        _limits.CheckSamples(count);
                        if ((long)count * 8 > h.PayloadSize - 8) throw new AudioFormatException("A chunk offset table is shorter than its count says.");
                        Spend(count);
                        offsets = _r.ReadUInt64s((int)count);
                        break;
                    }
            }
            _r.Seek(h.End);
        }
        if (!stsdSeen) throw new AudioFormatException("An audio track has no sample description.");
        if (t.Codec is null) return; // unsupported track: the tables were skipped
        if (stts is null || stsc is null || offsets is null || sampleCount < 0)
            throw new AudioFormatException("An audio track is missing one of its sample tables.");
        // A time-to-sample table whose durations are all zero (seen in MPEG-4 systems test streams) carries no timing: fall back to the
        // codec's frame length, 1024 for AAC, 20 ms for AMR.
        if (AllDurationsZero(stts) && sampleCount > 0)
        {
            long frameSamples = t.Codec == CodecId.Aac ? 1024 : t.Codec == CodecId.AmrWb ? 320 : t.Codec == CodecId.AmrNb ? 160 : 0;
            long delta = frameSamples == 0 ? 0 : Math.Max(1, frameSamples * t.MediaTimeScale / t.SampleRate);
            stts = delta == 0 ? stts : [(uint)sampleCount, (uint)delta];
        }
        t.Table = new Mp4SampleTable(sampleCount, uniform, sizes, stts, stsc, offsets);
    }

    private static bool AllDurationsZero(uint[] sttsPairs)
    {
        for (int i = 1; i < sttsPairs.Length; i += 2)
            if (sttsPairs[i] != 0) return false;
        return true;
    }

    private uint[] ReadWordTable(BoxHeader h, int wordsPerEntry)
    {
        var head = ReadPrefix(h, 8);
        if (head.Length < 8) throw new AudioFormatException("A sample table box is too short.");
        uint count = BinaryPrimitives.ReadUInt32BigEndian(head.AsSpan(4));
        _limits.CheckSamples(count);
        if ((long)count * wordsPerEntry * 4 > h.PayloadSize - 8) throw new AudioFormatException("A sample table box is shorter than its entry count says.");
        Spend(count);
        return _r.ReadUInt32s((int)count * wordsPerEntry);
    }

    private void Spend(long entries)
    {
        _entryBudget -= entries;
        if (_entryBudget < 0) throw new AudioLimitExceededException(nameof(AudioLimits.MaxSamples), 4L * _limits.MaxSamples, 4L * _limits.MaxSamples - _entryBudget);
    }

    // --- sample entry (ISO/IEC 14496-12 12.2.3 AudioSampleEntry; ISO/IEC 14496-14 5.6 esds; 3GPP TS 26.244 8.3 / 3GPP TS 26.101 amr) ---

    private void ParseStsd(byte[] d, Mp4TrackInfo t)
    {
        if (d.Length < 16 || BinaryPrimitives.ReadUInt32BigEndian(d.AsSpan(4)) == 0) { Unsupported(t, "", "no sample description"); return; }
        long size = BinaryPrimitives.ReadUInt32BigEndian(d.AsSpan(8));
        uint type = BinaryPrimitives.ReadUInt32BigEndian(d.AsSpan(12));
        t.SampleEntryType = Encoding.Latin1.GetString(d, 12, 4);
        if (size < 36 || 8 + size > d.Length) throw new AudioFormatException("A sample entry does not fit in its description box.");
        int entryEnd = (int)(8 + size);
        if (type is not (BoxHeader.Mp4a or BoxHeader.Samr or BoxHeader.Sawb))
        {
            Unsupported(t, t.SampleEntryType, $"the '{t.SampleEntryType.Trim()}' sample entry");
            return;
        }
        // Audio sample entry: 6 reserved, data_reference_index, then the version-0 fields (or QuickTime's version 1 extension).
        int version = BinaryPrimitives.ReadUInt16BigEndian(d.AsSpan(24));
        int channels = BinaryPrimitives.ReadUInt16BigEndian(d.AsSpan(32));
        int rate = (int)(BinaryPrimitives.ReadUInt32BigEndian(d.AsSpan(40)) >> 16);
        int childStart = 44;
        if (version == 1) childStart += 16;
        else if (version != 0) { Unsupported(t, t.SampleEntryType, "a QuickTime sound description version this library does not read"); return; }
        if (childStart > entryEnd) throw new AudioFormatException("A sample entry is too short.");

        t.TimeScale = (int)t.MediaTimeScale;
        if (type == BoxHeader.Mp4a)
        {
            if (!FindChild(d, childStart, entryEnd, BoxHeader.Esds, out int p, out int e)
                && !(FindChild(d, childStart, entryEnd, BoxHeader.Wave, out int wp, out int we) && FindChild(d, wp, we, BoxHeader.Esds, out p, out e)))
                throw new AudioFormatException("An AAC sample entry has no esds box.");
            ParseEsds(d.AsSpan(p, e - p), out int oti, out ReadOnlyMemory<byte> config);
            t.ObjectTypeIndication = oti;
            t.CodecConfig = config;
            t.SampleRate = rate;
            t.Channels = channels;
            switch (oti)
            {
                case 0x40 or 0x66 or 0x67 or 0x68: // MPEG-4 AAC, MPEG-2 AAC Main/LC/SSR
                    t.Codec = CodecId.Aac;
                    if (config.IsEmpty) throw new AudioFormatException("An AAC track has no decoder-specific configuration.");
                    if (AacConfigInfo.TryParse(config.Span, out var info))
                    {
                        t.AacObjectType = info.ObjectType;
                        t.SampleRate = info.ExtensionSampleRate > 0 ? info.ExtensionSampleRate : info.SampleRate;
                        if (info.Channels > 0) t.Channels = info.Channels;
                        // Other MPEG-4 audio (ALS, CELP, structured audio, ...) shares the sample entry but is not AAC.
                        if (!IsAacFamily(info.ObjectType)) Unsupported(t, t.SampleEntryType, $"MPEG-4 audio object type {info.ObjectType}");
                    }
                    break;
                case 0x69 or 0x6B: // MPEG-2 / MPEG-1 audio (MP3 in MP4)
                    t.Codec = CodecId.Mp3;
                    break;
                default:
                    Unsupported(t, t.SampleEntryType, $"MPEG-4 object type 0x{oti:X2}");
                    break;
            }
        }
        else
        {
            bool wide = type == BoxHeader.Sawb;
            t.Codec = wide ? CodecId.AmrWb : CodecId.AmrNb;
            t.SampleRate = wide ? 16000 : 8000;
            t.Channels = 1;
            if (FindChild(d, childStart, entryEnd, BoxHeader.Damr, out int p, out int e)) t.CodecConfig = d.AsMemory(p, e - p);
        }
        if (t.Codec is not null)
        {
            _limits.CheckSampleRate(t.SampleRate);
            _limits.CheckChannels(t.Channels);
        }
    }

    // AAC Main, LC, SSR, LTP, SBR, scalable, the error-resilient AAC types, PS, ER AAC ELD and USAC.
    private static bool IsAacFamily(int aot) => aot is 1 or 2 or 3 or 4 or 5 or 6 or 17 or 19 or 20 or 21 or 22 or 23 or 29 or 39 or 42;

    private static void Unsupported(Mp4TrackInfo t, string type, string reason)
    {
        t.SampleEntryType = type;
        t.Codec = null;
        t.UnsupportedReason = reason;
    }

    private static bool FindChild(byte[] d, int pos, int end, uint wanted, out int payloadStart, out int payloadEnd)
    {
        payloadStart = payloadEnd = 0;
        while (end - pos >= 8)
        {
            long size = BinaryPrimitives.ReadUInt32BigEndian(d.AsSpan(pos));
            uint type = BinaryPrimitives.ReadUInt32BigEndian(d.AsSpan(pos + 4));
            if (size == 0) size = end - pos;
            if (size < 8 || size > end - pos) throw new AudioFormatException("A box inside a sample entry has an invalid size.");
            if (type == wanted)
            {
                payloadStart = pos + 8;
                payloadEnd = pos + (int)size;
                return true;
            }
            pos += (int)size;
        }
        return false;
    }

    // esds is a full box holding an ES_Descriptor (ISO/IEC 14496-1 7.2.6.5): tag 0x03, ES_ID, flags, then a
    // DecoderConfigDescriptor (tag 0x04: objectTypeIndication, streamType, bufferSizeDB, maxBitrate, avgBitrate) that holds
    // the DecoderSpecificInfo (tag 0x05), which is the AudioSpecificConfig.
    private static void ParseEsds(ReadOnlySpan<byte> box, out int objectTypeIndication, out ReadOnlyMemory<byte> config)
    {
        objectTypeIndication = 0;
        config = default;
        if (box.Length < 4) throw new AudioFormatException("An esds box is too short.");
        var d = box[4..];
        int pos = 0;
        if (!ReadDescriptor(d, ref pos, out int tag, out int len) || tag != 0x03 || len > d.Length - pos)
            throw new AudioFormatException("An esds box has no ES descriptor.");
        int esEnd = pos + len;
        if (esEnd - pos < 3) throw new AudioFormatException("An ES descriptor is too short.");
        int flags = d[pos + 2];
        pos += 3;
        if ((flags & 0x80) != 0) pos += 2;
        if ((flags & 0x40) != 0)
        {
            if (pos >= esEnd) throw new AudioFormatException("An ES descriptor is too short.");
            pos += 1 + d[pos];
        }
        if ((flags & 0x20) != 0) pos += 2;
        if (pos > esEnd) throw new AudioFormatException("An ES descriptor is too short.");
        while (pos < esEnd && ReadDescriptor(d, ref pos, out tag, out len))
        {
            if (len > esEnd - pos) throw new AudioFormatException("A descriptor extends past its container.");
            if (tag == 0x04)
            {
                if (len < 13) throw new AudioFormatException("A decoder config descriptor is too short.");
                objectTypeIndication = d[pos];
                int sub = pos + 13;
                int subEnd = pos + len;
                while (sub < subEnd && ReadDescriptor(d, ref sub, out int stag, out int slen))
                {
                    if (slen > subEnd - sub) throw new AudioFormatException("A descriptor extends past its container.");
                    if (stag == 0x05)
                    {
                        config = d.Slice(sub, slen).ToArray();
                        return;
                    }
                    sub += slen;
                }
                return;
            }
            pos += len;
        }
    }

    // Descriptor header: one tag byte, then a length of up to four bytes of 7 bits each, high bit set on all but the last.
    private static bool ReadDescriptor(ReadOnlySpan<byte> d, ref int pos, out int tag, out int length)
    {
        tag = length = 0;
        if (pos >= d.Length) return false;
        tag = d[pos++];
        for (int i = 0; i < 4; i++)
        {
            if (pos >= d.Length) return false;
            byte b = d[pos++];
            length = (length << 7) | (b & 0x7F);
            if ((b & 0x80) == 0) return true;
        }
        return false;
    }

    // --- priming and trimming -------------------------------------------------------------------------------------

    private void Finish(Mp4TrackInfo t)
    {
        if (t.Table is null) return;
        t.SampleCount = t.Table.SampleCount;
        t.MediaTicks = t.Table.TotalTicks;
        long media = FramesFromTicks(t.MediaTicks, t.SampleRate, t.TimeScale);
        long priming = 0, length = media;
        var source = GaplessSource.None;

        foreach (var e in t.Edits.AsSpan(0, t.EditCount))
        {
            if (e.MediaTime < 0) continue; // an empty edit delays the start, which is not represented
            t.EditListMediaTime = e.MediaTime;
            priming = Math.Clamp(FramesFromTicks(e.MediaTime, t.SampleRate, t.TimeScale), 0, media);
            long room = media - priming;
            // segment_duration is in movie time units; 0 means "to the end of the media".
            long seg = e.SegmentDuration > 0
                ? FramesFromTicks(e.SegmentDuration, t.SampleRate, (int)Math.Min(_movieTimeScale == 0 ? t.MediaTimeScale : _movieTimeScale, int.MaxValue))
                : room;
            length = Math.Clamp(seg, 0, room);
            source = GaplessSource.EditList;
            break;
        }

        if (_smpb is not null && TryParseSmpb(_smpb, out long delay, out long valid) && delay <= media && valid <= media - delay)
        {
            priming = delay;
            length = valid;
            source = GaplessSource.ITunSmpb;
        }

        t.PrimingFrames = (int)Math.Min(priming, int.MaxValue);
        t.LengthFrames = length;
        t.Gapless = source;
    }

    // iTunSMPB text: " 00000000 <delay> <end padding> <valid frames, 16 hex digits> ..." — fields are hexadecimal.
    internal static bool TryParseSmpb(string text, out long delay, out long valid)
    {
        delay = valid = 0;
        var parts = text.Split(' ', StringSplitOptions.RemoveEmptyEntries);
        if (parts.Length < 4) return false;
        if (!long.TryParse(parts[1], NumberStyles.AllowHexSpecifier, CultureInfo.InvariantCulture, out delay)
            || !long.TryParse(parts[3], NumberStyles.AllowHexSpecifier, CultureInfo.InvariantCulture, out valid)) return false;
        return delay >= 0 && valid >= 0;
    }

    // --- metadata (iTunes ilst, 3GPP udta) ------------------------------------------------------------------------

    private void ParseUdta(BoxHeader udta)
    {
        while (NextBox(udta.End, false, out var h))
        {
            switch (h.Type)
            {
                case BoxHeader.Meta:
                    ParseMeta(h);
                    break;
                case BoxHeader.Titl:
                    Add3gppString("TITLE", h);
                    break;
                case BoxHeader.Auth:
                    Add3gppString("ARTIST", h);
                    break;
                case BoxHeader.Albm:
                    Add3gppString("ALBUM", h);
                    break;
            }
            _r.Seek(h.End);
        }
    }

    // 3GPP TS 26.244 8.3: full box, 16-bit packed language, then a null-terminated UTF-8 or (with byte order mark) UTF-16 string.
    private void Add3gppString(string key, BoxHeader h)
    {
        if (_comments.Count >= MaxComments || h.PayloadSize < 6) return;
        _limits.CheckMetadataBytes(h.PayloadSize);
        var b = ReadPrefix(h, (int)h.PayloadSize);
        var s = b.AsSpan(6);
        string text = s.Length >= 2 && s[0] == 0xFE && s[1] == 0xFF ? Encoding.BigEndianUnicode.GetString(s[2..])
            : s.Length >= 2 && s[0] == 0xFF && s[1] == 0xFE ? Encoding.Unicode.GetString(s[2..])
            : Encoding.UTF8.GetString(s);
        text = text.TrimEnd('\0');
        if (text.Length > 0) _comments.Add(key + "=" + text);
    }

    private void ParseMeta(BoxHeader meta)
    {
        if (meta.PayloadSize < 4) return;
        // ISO meta is a full box: four bytes of version and flags (zero), then the children. QuickTime's has no such prefix, so the
        // first four bytes are the size of its first child (hdlr), which is skipped here without reading back.
        uint first = _r.ReadUInt32();
        if (first != 0)
        {
            if (first < 8 || first > meta.PayloadSize) throw new AudioFormatException("A metadata box has an invalid first child.");
            _r.Seek(meta.PayloadStart + first);
        }
        while (NextBox(meta.End, false, out var h))
        {
            if (h.Type == BoxHeader.Ilst) ParseIlst(h);
            _r.Seek(h.End);
        }
    }

    private void ParseIlst(BoxHeader ilst)
    {
        while (NextBox(ilst.End, false, out var item))
        {
            string? key = item.Type switch
            {
                0xA96E616D => "TITLE",         // (c)nam
                0xA9415254 => "ARTIST",        // (c)ART
                0x61415254 => "ALBUMARTIST",   // aART
                0xA9616C62 => "ALBUM",         // (c)alb
                0xA9646179 => "DATE",          // (c)day
                0xA967656E => "GENRE",         // (c)gen
                0xA9777274 => "COMPOSER",      // (c)wrt
                0xA9636D74 => "COMMENT",       // (c)cmt
                _ => null,
            };
            if (key is not null && _comments.Count < MaxComments)
            {
                _limits.CheckMetadataBytes(item.PayloadSize);
                var body = ReadPrefix(item, (int)item.PayloadSize);
                if (FindChild(body, 0, body.Length, BoxHeader.Data, out int p, out int e) && e - p >= 8 && BinaryPrimitives.ReadUInt32BigEndian(body.AsSpan(p)) == 1)
                {
                    string text = Encoding.UTF8.GetString(body, p + 8, e - p - 8);
                    if (text.Length > 0) _comments.Add(key + "=" + text);
                }
            }
            else if (item.Type == BoxHeader.Freeform && item.PayloadSize <= 4096 && _smpb is null)
            {
                var body = ReadPrefix(item, (int)item.PayloadSize);
                if (FindChild(body, 0, body.Length, BoxHeader.Name, out int np, out int ne) && ne - np >= 4
                    && body.AsSpan(np + 4, ne - np - 4).SequenceEqual("iTunSMPB"u8)
                    && FindChild(body, 0, body.Length, BoxHeader.Data, out int p, out int e) && e - p >= 8)
                    _smpb = Encoding.UTF8.GetString(body, p + 8, e - p - 8);
            }
            _r.Seek(item.End);
        }
    }
}
