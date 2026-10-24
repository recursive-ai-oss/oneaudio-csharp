namespace OneAudio.Ogg;

/// <summary>Describes one link (one logical bitstream) of a possibly chained Ogg file.</summary>
/// <param name="SerialNumber">The bitstream serial number from the page headers.</param>
/// <param name="Codec">The codec found in the link's first packet.</param>
/// <param name="StartFrame">Where the link begins on the file-wide frame axis (the sum of the lengths of the links before it).</param>
/// <param name="LengthFrames">Frames in the link: the granule position of its last page, 0 when no page carries one.</param>
public readonly record struct OggLinkInfo(uint SerialNumber, CodecId Codec, long StartFrame, long LengthFrames);

/// <summary>
/// Reads the packets of one audio stream out of an Ogg file (RFC 3533, RFC 5334). It does framing, checksums,
/// chaining and seeking by granule position, and knows nothing about what is inside the packets.
/// <para>
/// <b>Codec detection.</b> The codec is taken from the first packet of the first stream whose magic is recognised:
/// <c>\x01vorbis</c> (Vorbis), <c>OpusHead</c> (Opus), <c>\x7fFLAC</c> or <c>fLaC</c> (FLAC). Streams of other codecs in
/// the same file (video, text) are skipped. Header packets are returned with <see cref="PacketView.IsHeader"/> set:
/// three for Vorbis, <c>OpusHead</c> and <c>OpusTags</c> for Opus, and the first packet plus the number announced in it for FLAC.
/// </para>
/// <para>
/// <b>EndFrame.</b> An Ogg page carries one granule position, which belongs to the last packet that finishes on that
/// page. Only that packet gets a non-negative <see cref="PacketView.EndFrame"/>; every other packet gets -1, because
/// working out where they end needs the codec's block sizes. What the number means is up to the codec:
/// for Vorbis it is the sample position after the packet's finished audio (the end of the overlapped region);
/// for Opus it is a 48 kHz position that includes the pre-skip; for FLAC it is the sample number after the frame.
/// Positions are counted per link, from the link's own start.
/// </para>
/// <para>
/// <b>Chains.</b> A chained file is a series of links. Between links the source returns the next link's header packets
/// in-band (<c>IsHeader</c> true), so a codec that sees a new header sequence knows a new stream begins. On a seekable
/// stream the links are found when the source is opened (see <see cref="GetLink"/>); on a non-seekable stream they are
/// discovered as they are read. A link that uses a different codec from the first one ends the stream.
/// </para>
/// <para>
/// <b>Seeking.</b> <see cref="SeekToFrame"/> works on the file-wide frame axis. It finds the last page whose granule
/// position is at or before the target and returns, as the first packet, the last packet that finishes on that page;
/// <c>startFrame</c> is that page's granule position, i.e. the position at the <i>end</i> of the first returned
/// packet. A codec decodes that packet to prime its overlap or prediction state, discards its output, and continues
/// counting from <c>startFrame</c>. Near the start of a link, where no usable page exists, the first audio packet is
/// returned and <c>startFrame</c> is the link's start. Seeking never delivers header packets.
/// </para>
/// <para>
/// Memory is two fixed 64 KiB buffers plus one packet-assembly buffer that grows only as far as the largest packet
/// (bounded by <see cref="AudioLimits.MaxPacketBytes"/>). Packets that fit inside a page are returned without copying.
/// </para>
/// </summary>
public sealed class OggPacketSource : IPacketSource
{
    private sealed class Link
    {
        public uint Serial;
        public CodecId Codec;
        public long StartOffset;
        public long EndOffset = -1;
        public long AudioStartOffset = -1;
        public long EndGranule;
        public long StartFrame;
        public long Length;
    }

    private readonly OggPageReader _reader;
    private readonly Stream _stream;
    private readonly bool _leaveOpen;
    private readonly AudioLimits _limits;
    private readonly bool _seekable;
    private readonly List<Link> _links = [];

    private CodecId _codec;
    private bool _haveSerial;
    private uint _serial;
    private int _curLink = -1;
    private bool _dataSeen;
    private bool _disposed;
    private bool _ended;

    // Current page.
    private int _nseg;
    private int _seg;
    private int _bodyOff;
    private int _lastCompleteSeg;
    private long _pgGranule;
    private bool _pgEos;
    private bool _lastPacketEndsStream;
    private long _pgEnd;
    private uint _expectSeq;
    private bool _haveSeq;
    private bool _dropLeading;
    private int _skipPackets;

    // Packet assembly across pages.
    private byte[] _asm = new byte[4096];
    private int _asmLen;
    private bool _asmPending;
    private bool _asmReturned;

    // Header classification for FLAC-in-Ogg.
    private int _flacHeadersLeft;
    private bool _dropRestOfPage;
    private int _packetIndexInLink;

    private OggPacketSource(Stream stream, AudioLimits limits, bool leaveOpen)
    {
        _stream = stream;
        _limits = limits;
        _leaveOpen = leaveOpen;
        _seekable = stream.CanSeek;
        _reader = new OggPageReader(stream);
    }

    /// <summary>Opens an Ogg stream positioned at its first page and finds the first audio stream.</summary>
    /// <param name="stream">The Ogg data. Seekable streams get chain discovery and seeking.</param>
    /// <param name="limits">Limits for untrusted input; <see cref="AudioLimits.Default"/> when null.</param>
    /// <param name="leaveOpen">Keep <paramref name="stream"/> open when the source is disposed.</param>
    /// <param name="cancellationToken">Observed while scanning a chained file.</param>
    /// <returns>The source, positioned at the first header packet.</returns>
    /// <exception cref="AudioFormatException">There is no Ogg page at the start.</exception>
    /// <exception cref="UnsupportedAudioFormatException">No stream with a recognised audio codec was found.</exception>
    /// <exception cref="AudioLimitExceededException">The file has more links than <see cref="AudioLimits.MaxContainerElements"/>.</exception>
    public static OggPacketSource Open(Stream stream, AudioLimits? limits = null, bool leaveOpen = false, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(stream);
        if (!stream.CanRead) throw new ArgumentException("Stream must be readable.", nameof(stream));
        var src = new OggPacketSource(stream, limits ?? AudioLimits.Default, leaveOpen);
        try
        {
            src.InitFirstLink();
            if (src._seekable)
            {
                src.DiscoverLinks(cancellationToken);
                src.SeekToLink(0);
            }
            return src;
        }
        catch
        {
            src.Abandon();
            throw;
        }
    }

    /// <inheritdoc />
    public CodecId Codec => _codec;

    /// <inheritdoc />
    public bool CanSeek => _seekable;

    /// <inheritdoc />
    public long LengthFrames
    {
        get
        {
            if (!_seekable) return -1;
            var last = _links[^1];
            return last.StartFrame + last.Length;
        }
    }

    /// <summary>The number of links known so far (all of them for a seekable stream).</summary>
    public int LinkCount => _links.Count;

    /// <summary>The index of the link the most recently returned packet belongs to.</summary>
    public int CurrentLink => Math.Max(_curLink, 0);

    /// <summary>
    /// True when the packet most recently returned by <see cref="TryReadPacket"/> was the last one finished on a page
    /// marked end-of-stream, that is, the last packet of its link. Codecs use it to apply end-of-stream trimming only there.
    /// </summary>
    public bool PacketEndsStream => _lastPacketEndsStream;

    /// <summary>Describes a link.</summary>
    /// <param name="index">0 to <see cref="LinkCount"/> - 1.</param>
    public OggLinkInfo GetLink(int index)
    {
        var l = _links[index];
        return new OggLinkInfo(l.Serial, l.Codec, l.StartFrame, l.Length);
    }

    /// <summary>
    /// Repositions at the first header packet of a link, so the codec can read that link's headers before
    /// <see cref="SeekToFrame"/> lands inside it. Seekable streams only.
    /// </summary>
    /// <param name="link">0 to <see cref="LinkCount"/> - 1.</param>
    /// <exception cref="NotSupportedException">The stream is not seekable.</exception>
    public void SeekToLink(int link)
    {
        if (!_seekable) throw new NotSupportedException("The Ogg stream is not seekable.");
        ObjectDisposedException.ThrowIf(_disposed, this);
        var l = _links[link];
        _reader.Seek(l.StartOffset);
        BeginLink(l.Serial, link);
        _haveSeq = false;
        _dataSeen = false;
    }

    /// <inheritdoc />
    public bool TryReadPacket(out PacketView packet)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        if (_asmReturned) { _asmLen = 0; _asmReturned = false; }
        if (_dropRestOfPage)
        {
            // Audio starts on a fresh page after the last header page (Vorbis I appendix A.2, RFC 7845 section 3);
            // anything sharing that page is not delivered, so linear reading and seeking agree.
            _dropRestOfPage = false;
            _seg = _nseg;
        }
        while (true)
        {
            if (_ended) { packet = default; return false; }
            if (_seg >= _nseg)
            {
                if (!LoadNextPage()) { _ended = true; packet = default; return false; }
                continue;
            }
            var pageBytes = _reader.LastPageBytes;
            var lacing = pageBytes.Slice(27, _nseg);
            int bodyBase = 27 + _nseg;

            int i = _seg;
            int segBytes = 0;
            bool complete = false;
            while (i < _nseg)
            {
                int l = lacing[i++];
                segBytes += l;
                if (l < 255) { complete = true; break; }
            }
            var piece = pageBytes.Slice(bodyBase + _bodyOff, segBytes);
            _seg = i;
            _bodyOff += segBytes;

            if (_dropLeading)
            {
                // The tail of a packet whose start we never saw: nothing to decode.
                if (complete) _dropLeading = false;
                continue;
            }
            if (!complete)
            {
                Append(piece);
                _asmPending = true;
                continue;
            }

            ReadOnlySpan<byte> data;
            if (_asmPending)
            {
                Append(piece);
                data = _asm.AsSpan(0, _asmLen);
                _asmPending = false;
                _asmReturned = true;
            }
            else
            {
                data = piece;
            }
            if (_skipPackets > 0) { _skipPackets--; if (_asmReturned) { _asmLen = 0; _asmReturned = false; } continue; }

            bool lastOnPage = i - 1 == _lastCompleteSeg;
            long end = lastOnPage ? _pgGranule : -1;
            _lastPacketEndsStream = lastOnPage && _pgEos;
            bool header = ClassifyHeader(data, out bool lastHeader);
            if (lastHeader) _dropRestOfPage = true;
            _packetIndexInLink++;
            packet = new PacketView(data, end, header);
            return true;
        }
    }

    /// <inheritdoc />
    public void SeekToFrame(long frame, out long startFrame)
    {
        if (!_seekable) throw new NotSupportedException("The Ogg stream is not seekable.");
        ObjectDisposedException.ThrowIf(_disposed, this);
        if (frame < 0) frame = 0;
        int li = _links.Count - 1;
        while (li > 0 && _links[li].StartFrame > frame) li--;
        var link = _links[li];
        long t = Math.Min(frame - link.StartFrame, link.Length);
        EnsureAudioStart(li);

        bool found = FindSeekPage(link, t, out long pageOffset, out long granule, out int drop);
        _reader.Seek(found ? pageOffset : link.AudioStartOffset);
        BeginLink(link.Serial, li);
        _haveSeq = false;
        _dataSeen = true;
        _flacHeadersLeft = 0;
        _dropRestOfPage = false;
        _packetIndexInLink = 1; // audio only
        _skipPackets = found ? drop : 0;
        startFrame = link.StartFrame + (found ? granule : 0);
    }

    /// <inheritdoc />
    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        if (!_leaveOpen) _stream.Dispose();
    }

    /// <summary>Disposes without closing the stream (used when opening fails and the caller keeps the stream).</summary>
    internal void Abandon()
    {
        _disposed = true;
    }

    // ---------------------------------------------------------------------------------------------------

    private static CodecId? DetectCodec(ReadOnlySpan<byte> first)
    {
        if (first.Length >= 7 && first[0] == 1 && first[1..7].SequenceEqual("vorbis"u8)) return CodecId.Vorbis;
        if (first.Length >= 8 && first[..8].SequenceEqual("OpusHead"u8)) return CodecId.Opus;
        if (first.Length >= 5 && first[0] == 0x7F && first[1..5].SequenceEqual("FLAC"u8)) return CodecId.Flac;
        if (first.Length >= 4 && first[..4].SequenceEqual("fLaC"u8)) return CodecId.Flac;
        return null;
    }

    /// <summary>The first packet of a BOS page: the BOS page holds exactly that packet.</summary>
    private static ReadOnlySpan<byte> FirstPacketOf(ReadOnlySpan<byte> pageBytes)
    {
        int nseg = pageBytes[26];
        int len = 0;
        for (int i = 0; i < nseg; i++)
        {
            len += pageBytes[27 + i];
            if (pageBytes[27 + i] < 255) break;
        }
        return pageBytes.Slice(27 + nseg, len);
    }

    private void BeginLink(uint serial, int linkIndex)
    {
        _serial = serial;
        _haveSerial = true;
        _curLink = linkIndex;
        _nseg = _seg = _bodyOff = 0;
        _lastCompleteSeg = -1;
        _asmLen = 0;
        _asmPending = false;
        _asmReturned = false;
        _dropLeading = false;
        _skipPackets = 0;
        _packetIndexInLink = 0;
        _flacHeadersLeft = 0;
        _dropRestOfPage = false;
        _ended = false;
    }

    private void InitFirstLink()
    {
        // The first stream with a recognised codec fixes the codec of the whole file.
        long count = 0;
        bool sawStream = false;
        while (_reader.TryReadPage(false, out var pg))
        {
            _limits.CheckContainerElements(++count);
            if (!pg.IsBos) break;
            sawStream = true;
            var codec = DetectCodec(FirstPacketOf(_reader.LastPageBytes));
            if (codec is null) continue;
            _codec = codec.Value;
            _links.Add(new Link { Serial = pg.Serial, Codec = _codec, StartOffset = pg.Offset });
            BeginLink(pg.Serial, 0);
            LoadPageState(pg);
            _dataSeen = false;
            return;
        }
        if (sawStream) throw new UnsupportedAudioFormatException("No supported audio stream found in the Ogg file.");
        throw new AudioFormatException("The stream does not start with an Ogg page.");
    }

    private void LoadPageState(in OggPage pg)
    {
        var lacing = pg.Lacing;
        _nseg = lacing.Length;
        _seg = 0;
        _bodyOff = 0;
        _lastCompleteSeg = -1;
        for (int i = 0; i < lacing.Length; i++)
            if (lacing[i] < 255) _lastCompleteSeg = i;
        _pgGranule = pg.Granule;
        _pgEos = pg.IsEos;
        _pgEnd = pg.Offset + pg.Size;
        if (_haveSeq && pg.Sequence != _expectSeq)
        {
            // A page went missing: whatever packet was being assembled is lost.
            _asmLen = 0;
            _asmPending = false;
        }
        _haveSeq = true;
        _expectSeq = pg.Sequence + 1;
        if (pg.IsContinued)
        {
            if (!_asmPending) _dropLeading = true;
        }
        else
        {
            _dropLeading = false;
            if (_asmPending)
            {
                _asmLen = 0;
                _asmPending = false;
            }
        }
        if (_nseg == 0) _seg = 0;
    }

    private bool LoadNextPage()
    {
        while (true)
        {
            if (!_reader.TryReadPage(false, out var pg)) return false;
            if (pg.Serial == _serial && _haveSerial)
            {
                if (!pg.IsBos) _dataSeen = true;
                else if (_dataSeen)
                {
                    // Same serial number reused for a new link: treat it as one.
                    if (!NewLinkFromBos(pg)) return false;
                    return true;
                }
                LoadPageState(pg);
                return true;
            }
            if (pg.IsBos && _dataSeen)
            {
                if (NewLinkFromBos(pg)) return true;
                if (_ended) return false;
            }
            // Pages of other streams are skipped.
        }
    }

    private bool NewLinkFromBos(in OggPage pg)
    {
        var codec = DetectCodec(FirstPacketOf(_reader.LastPageBytes));
        if (codec is null) return false;
        if (codec.Value != _codec) { _ended = true; return false; }
        int idx = _curLink + 1;
        if (!_seekable)
        {
            _limits.CheckContainerElements(idx + 1);
            if (idx >= _links.Count) _links.Add(new Link { Serial = pg.Serial, Codec = codec.Value, StartOffset = pg.Offset });
        }
        BeginLink(pg.Serial, Math.Min(idx, _links.Count - 1));
        _haveSeq = false;
        LoadPageState(pg);
        _dataSeen = false;
        return true;
    }

    private void Append(ReadOnlySpan<byte> piece)
    {
        long needed = (long)_asmLen + piece.Length;
        _limits.CheckPacketBytes(needed);
        if (needed > _asm.Length)
        {
            int size = _asm.Length;
            while (size < needed) size = (int)Math.Min((long)size * 2, int.MaxValue / 2);
            Array.Resize(ref _asm, size);
        }
        piece.CopyTo(_asm.AsSpan(_asmLen));
        _asmLen += piece.Length;
    }

    private bool ClassifyHeader(ReadOnlySpan<byte> d, out bool last)
    {
        last = false;
        switch (_codec.Name)
        {
            case "vorbis":
                last = d.Length >= 7 && d[0] == 5 && d[1..7].SequenceEqual("vorbis"u8);
                return d.Length >= 7 && (d[0] & 1) == 1 && d[1..7].SequenceEqual("vorbis"u8);
            case "opus":
                last = d.Length >= 8 && d[..8].SequenceEqual("OpusTags"u8);
                return d.Length >= 8 && (d[..8].SequenceEqual("OpusHead"u8) || last);
            case "flac":
                if (_packetIndexInLink == 0)
                {
                    if (d.Length >= 9 && d[0] == 0x7F) _flacHeadersLeft = (d[7] << 8) | d[8];
                    last = _flacHeadersLeft == 0;
                    return true;
                }
                if (_flacHeadersLeft > 0)
                {
                    _flacHeadersLeft--;
                    last = _flacHeadersLeft == 0;
                    return true;
                }
                return false;
            default:
                return false;
        }
    }

    // ---- Chain discovery ----------------------------------------------------------------------------

    private void DiscoverLinks(CancellationToken ct)
    {
        long length = _stream.Length;
        var first = _links[0];

        // Fast path: if the last page belongs to the first stream, the file is one link (the Vorbis and Opus
        // specifications forbid interleaving unrelated streams inside a link, so no other serial can be hiding).
        long tailStart = Math.Max(first.StartOffset, length - 2 * OggPageReader.MaxPageSize);
        _reader.Seek(tailStart);
        bool single = true;
        long lastGranule = -1;
        bool anyPage = false;
        while (_reader.TryReadPage(false, out var pg))
        {
            anyPage = true;
            if (pg.Serial != first.Serial) { single = false; break; }
            if (pg.Granule >= 0) lastGranule = pg.Granule;
        }
        if (single && anyPage)
        {
            first.EndOffset = length;
            first.EndGranule = lastGranule;
            first.Length = Math.Max(lastGranule, 0);
            return;
        }

        WalkLinks(length, ct);
    }

    private void WalkLinks(long length, CancellationToken ct)
    {
        var first = _links[0];
        _links.Clear();
        _reader.Seek(first.StartOffset);
        Link? cur = null;
        bool dataSeen = false;
        long lastG = -1;
        long pages = 0;
        var codec0 = first.Codec;

        while (_reader.TryReadPage(true, out var pg))
        {
            if ((++pages & 1023) == 0) ct.ThrowIfCancellationRequested();
            long off = pg.Offset;
            uint ser = pg.Serial;
            long g = pg.Granule;
            bool bos = pg.IsBos;
            if (bos && (cur is null || dataSeen))
            {
                // Read the page again in full to see which codec it starts.
                long resume = _reader.Position;
                _reader.Seek(off);
                CodecId? codec = null;
                if (_reader.TryReadPage(false, out _)) codec = DetectCodec(FirstPacketOf(_reader.LastPageBytes));
                _reader.Seek(resume);
                if (codec is null) continue;
                if (codec.Value != codec0) break;
                if (cur is not null) Finish(cur, off, lastG);
                _limits.CheckContainerElements(_links.Count + 1);
                cur = new Link { Serial = ser, Codec = codec.Value, StartOffset = off };
                _links.Add(cur);
                dataSeen = false;
                lastG = -1;
                continue;
            }
            if (cur is not null && ser == cur.Serial && !bos)
            {
                dataSeen = true;
                if (g >= 0) lastG = g;
            }
        }
        if (cur is null) throw new AudioFormatException("No Ogg stream found.");
        Finish(cur, length, lastG);

        long start = 0;
        foreach (var l in _links)
        {
            l.StartFrame = start;
            start += l.Length;
        }
    }

    private static void Finish(Link l, long endOffset, long lastGranule)
    {
        l.EndOffset = endOffset;
        l.EndGranule = lastGranule;
        l.Length = Math.Max(lastGranule, 0);
    }

    // ---- Seeking ------------------------------------------------------------------------------------

    private void EnsureAudioStart(int li)
    {
        var link = _links[li];
        if (link.AudioStartOffset >= 0) return;
        SeekToLink(li);
        long lastHeaderEnd = link.StartOffset;
        while (TryReadPacket(out var p))
        {
            if (!p.IsHeader) break;
            lastHeaderEnd = _pgEnd;
        }
        link.AudioStartOffset = lastHeaderEnd;
    }

    private static int CompletedPackets(ReadOnlySpan<byte> lacing)
    {
        int n = 0;
        foreach (byte b in lacing) if (b < 255) n++;
        return n;
    }

    /// <summary>
    /// Finds the last page in the link whose granule position is at or before <paramref name="target"/> and whose last
    /// finished packet lies wholly inside the page (so it can be returned without earlier pages).
    /// </summary>
    private bool FindSeekPage(Link link, long target, out long pageOffset, out long granule, out int drop)
    {
        pageOffset = 0;
        granule = 0;
        drop = 0;
        long lo = link.AudioStartOffset;
        long hi = link.EndOffset;
        const long Window = 4L * OggPageReader.MaxPageSize;

        // Bisect on byte offsets. Invariant: every valid page at or after hi has a granule beyond the target,
        // and scanning forward from lo reaches every candidate that is not.
        while (hi - lo > Window)
        {
            long mid = lo + (hi - lo) / 2;
            _reader.Seek(mid);
            bool got = false;
            long off = 0, g = 0;
            while (_reader.Position < hi && _reader.TryReadPage(true, out var pg))
            {
                if (pg.Serial != link.Serial || pg.Granule < 0) continue;
                off = pg.Offset;
                g = pg.Granule;
                got = true;
                break;
            }
            if (!got || off >= hi) { hi = mid; continue; }
            if (g <= target) lo = off;
            else hi = off;
        }

        _reader.Seek(lo);
        bool best = false;
        while (_reader.TryReadPage(false, out var pg))
        {
            if (pg.Offset >= link.EndOffset) break;
            if (pg.Serial != link.Serial) continue;
            if (pg.Granule < 0) continue;
            if (pg.Granule > target) break;
            int m = CompletedPackets(pg.Lacing);
            bool continued = pg.IsContinued;
            if (m < 1 || (m == 1 && continued)) continue;
            pageOffset = pg.Offset;
            granule = pg.Granule;
            drop = m - 1 - (continued ? 1 : 0);
            best = true;
        }
        return best;
    }
}
