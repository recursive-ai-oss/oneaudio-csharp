using OneAudio.Ogg;

namespace OneAudio.Vorbis;

/// <summary>Turns Vorbis packet streams into decoders. Register with <see cref="OneAudio.VorbisRegistryExtensions.AddVorbis"/>.</summary>
public sealed class VorbisCodec : IAudioCodec
{
    /// <summary>The shared instance.</summary>
    public static VorbisCodec Instance { get; } = new();

    private VorbisCodec() { }

    /// <inheritdoc />
    public CodecId Id => CodecId.Vorbis;

    /// <inheritdoc />
    public IAudioDecoder CreateDecoder(IPacketSource packets, AudioLimits limits) => VorbisDecoder.Create(packets, limits);
}


/// <summary>
/// Streaming Vorbis I decoder (floor types 0 and 1, residue types 0, 1 and 2, any channel count up to the limit),
/// written from the Vorbis I specification. It reads packets from an <see cref="IPacketSource"/> (normally Ogg) and
/// writes interleaved <see cref="float"/> frames; nothing is allocated per read once the headers are parsed.
/// <para>
/// <b>Output.</b> Samples are the decoder's float values with no clipping or dither, nominally in [-1, 1].
/// Channels are reordered from Vorbis order to the library's ascending speaker-bit order (for example 5.1 comes out
/// as front left, front right, centre, LFE, back left, back right) and <see cref="AudioFormat.ChannelMask"/> says so;
/// layouts without a standard mask (5 and 7 channels get one; more than 8 do not) keep stream order.
/// </para>
/// <para>
/// <b>Length and trimming.</b> The end of a link is trimmed to the granule position of its last page, as the Ogg
/// Vorbis mapping requires. <see cref="LengthFrames"/> is the sum of those granule positions, the same figure
/// libvorbis reports as the total; a damaged or truncated final packet can make the audio actually delivered shorter
/// than that figure, and reading simply ends early. A stream that starts at a non-zero granule position (a cut from
/// a longer programme) is treated as starting at zero.
/// </para>
/// <para>
/// <b>Chained files.</b> A chain is a series of independent Vorbis streams ("links"). Consecutive links with the
/// same sample rate and channel count are played as one continuous stream. A link with a different format ends
/// the stream at that point, because <see cref="Format"/> cannot change while reading; use <see cref="LinkCount"/>,
/// <see cref="GetLinkFormat"/> and <see cref="SelectRun"/> to reach the others.
/// </para>
/// <para>
/// <b>Seeking</b> is sample-accurate: the decoder finds a page at or before the target through the container, decodes
/// from there to prime the overlap, and discards up to the target, so the result equals a linear decode.
/// </para>
/// </summary>
public sealed class VorbisDecoder : IAudioDecoder
{
    private static readonly int[][] ChannelOrders =
    [
        [], [0], [0, 1], [0, 2, 1], [0, 1, 2, 3], [0, 2, 1, 3, 4], [0, 2, 1, 5, 3, 4], [0, 2, 1, 6, 5, 3, 4], [0, 2, 1, 7, 5, 6, 3, 4],
    ];

    private const ChannelMask FL = ChannelMask.FrontLeft, FR = ChannelMask.FrontRight, FC = ChannelMask.FrontCenter,
        LFE = ChannelMask.LowFrequency, BL = ChannelMask.BackLeft, BR = ChannelMask.BackRight, BC = ChannelMask.BackCenter,
        SL = ChannelMask.SideLeft, SR = ChannelMask.SideRight;

    private static readonly ChannelMask[] Masks =
    [
        ChannelMask.None, FC, FL | FR, FL | FR | FC, FL | FR | BL | BR, FL | FR | FC | BL | BR,
        FL | FR | FC | LFE | BL | BR, FL | FR | FC | LFE | BC | SL | SR, FL | FR | FC | LFE | BL | BR | SL | SR,
    ];

    private readonly IPacketSource _packets;
    private readonly OggPacketSource? _ogg;
    private readonly AudioLimits _limits;
    private readonly bool _canSeek;
    private bool _disposed;

    // Active link.
    private VorbisSetup _setup;
    private VorbisSynthesizer _synth;
    private int[] _channelMap;
    private int _activeLink;

    // Format of every link (null when a link's identification header is unusable) and the run being played.
    private readonly AudioFormat?[] _linkFormats;
    private readonly long[] _linkOffset;      // granule position of the first decoded frame of each link (see ScanOffset); usually 0
    private long[] _runStarts = [0];         // run-relative start frame of each link in the run
    private int _runStart;
    private int _runEnd;
    private AudioFormat _format;
    private long _length;

    // Output state.
    private int _outPos;
    private int _outLen;
    private long _skipFrames;
    private long _gpos;
    private long _pos;
    private bool _ended;
    private bool _anchored;

    private VorbisDecoder(IPacketSource packets, AudioLimits limits, VorbisSetup setup)
    {
        _packets = packets;
        _ogg = packets as OggPacketSource;
        _limits = limits;
        _canSeek = packets.CanSeek && _ogg is not null;
        _setup = setup;
        _synth = new VorbisSynthesizer(setup);
        _channelMap = MapFor(setup.Channels);
        _linkFormats = new AudioFormat?[_ogg?.LinkCount ?? 1];
        _linkOffset = new long[_linkFormats.Length];
    }

    internal static VorbisDecoder Create(IPacketSource packets, AudioLimits limits)
    {
        ArgumentNullException.ThrowIfNull(packets);
        ArgumentNullException.ThrowIfNull(limits);
        if (!packets.TryReadPacket(out var first) || !first.IsHeader)
            throw new AudioFormatException("The Vorbis stream does not start with a header packet.");
        var setup = VorbisSetup.ParseIdentification(first.Data, limits);
        ReadRemainingHeaders(packets, setup, limits);

        var dec = new VorbisDecoder(packets, limits, setup);
        dec._format = FormatOf(setup);
        dec._linkFormats[0] = dec._format;
        if (dec._ogg is { CanSeek: true } ogg)
        {
            // Learn each link's format and start trim from its headers, so runs of identical links can be played through.
            dec._linkOffset[0] = ScanOffset(ogg, setup);
            for (int i = 1; i < ogg.LinkCount; i++)
            {
                ogg.SeekToLink(i);
                if (!ogg.TryReadPacket(out var p) || !p.IsHeader) continue;
                try
                {
                    var other = VorbisSetup.ParseIdentification(p.Data, limits);
                    ReadRemainingHeaders(packets, other, limits);
                    dec._linkFormats[i] = FormatOf(other);
                    dec._linkOffset[i] = ScanOffset(ogg, other);
                }
                catch (AudioFormatException) { dec._linkFormats[i] = null; }
            }
            // Back to the first link, past its three headers (already parsed above).
            ogg.SeekToLink(0);
            for (int i = 0; i < 3; i++)
                if (!ogg.TryReadPacket(out _)) throw new AudioFormatException("The Vorbis stream is truncated.");
            dec.SelectRunCore(0);
            dec.StartLinkAt(0);
        }
        else
        {
            dec._runEnd = 0;
            dec._length = -1;
        }
        dec._activeLink = 0;
        dec._synth.Reset();
        return dec;
    }

    /// <summary>
    /// The granule position of the first frame the decoder produces for a link. Normally 0. An encoder may give the
    /// first audio page a granule position different from the audio decoded up to it (Ogg Vorbis mapping, appendix
    /// A.2): smaller means "the first samples lie before time zero, drop them" (sample-accurate cutting), larger
    /// means the stream starts at a later time. The block sizes of the packets up to that page give the decoded count
    /// without decoding anything. The scan gives up after 10000 packets without a position and returns 0.
    /// </summary>
    private static long ScanOffset(OggPacketSource ogg, VorbisSetup setup)
    {
        long decoded = 0;
        int prev = 0;
        for (int count = 0; count < 10_000 && ogg.TryReadPacket(out var p); count++)
        {
            if (p.IsHeader) break;
            int n = setup.PeekBlockSize(p.Data);
            if (n == 0) continue;
            if (prev != 0) decoded += prev / 4 + n / 4;
            prev = n;
            if (p.EndFrame >= 0) return p.EndFrame - decoded;
        }
        return 0;
    }

    /// <summary>Sets the position bookkeeping for decoding a link from its first audio packet.</summary>
    private void StartLinkAt(int link)
    {
        long off = _canSeek && link < _linkOffset.Length ? _linkOffset[link] : 0;
        _gpos = off;
        _anchored = _canSeek;
        _skipFrames = Math.Max(0, -off);
    }

    private static void ReadRemainingHeaders(IPacketSource packets, VorbisSetup setup, AudioLimits limits)
    {
        if (!packets.TryReadPacket(out var comment) || !comment.IsHeader)
            throw new AudioFormatException("The Vorbis comment header is missing.");
        setup.ParseComments(comment.Data, limits);
        if (!packets.TryReadPacket(out var setupPacket) || !setupPacket.IsHeader)
            throw new AudioFormatException("The Vorbis setup header is missing.");
        setup.ParseSetup(setupPacket.Data, limits);
    }

    private static AudioFormat FormatOf(VorbisSetup s) =>
        new(s.SampleRate, s.Channels, s.Channels < Masks.Length ? Masks[s.Channels] : ChannelMask.None);

    private static int[] MapFor(int channels)
    {
        if (channels < ChannelOrders.Length) return ChannelOrders[channels];
        var m = new int[channels];
        for (int i = 0; i < m.Length; i++) m[i] = i;
        return m;
    }

    /// <inheritdoc />
    public AudioFormat Format => _format;

    /// <inheritdoc />
    public long LengthFrames => _length;

    /// <inheritdoc />
    public long PositionFrames => _pos;

    /// <inheritdoc />
    public bool CanSeek => _canSeek;

    /// <inheritdoc />
    public bool HasNativeIntegers => false;

    /// <summary>The encoder's vendor string from the comment header of the link being decoded.</summary>
    public string Vendor => _setup.Vendor;

    /// <summary>The user comments (<c>NAME=value</c> strings) from the comment header of the link being decoded.</summary>
    public IReadOnlyList<string> Comments => _setup.Comments;

    /// <summary>The number of links (independent streams) in the file; 1 unless the file is chained and seekable.</summary>
    public int LinkCount => _linkFormats.Length;

    /// <summary>Format of a link, or null when its identification header is unusable.</summary>
    /// <param name="link">0 to <see cref="LinkCount"/> - 1.</param>
    public AudioFormat? GetLinkFormat(int link) => _linkFormats[link];

    /// <summary>
    /// Switches to the run of consecutive same-format links that contains <paramref name="link"/> and repositions at
    /// its start. <see cref="Format"/>, <see cref="LengthFrames"/> and <see cref="PositionFrames"/> then describe that run.
    /// </summary>
    /// <param name="link">0 to <see cref="LinkCount"/> - 1.</param>
    /// <exception cref="NotSupportedException">The decoder cannot seek.</exception>
    /// <exception cref="ArgumentOutOfRangeException">The link does not exist or its headers are unusable.</exception>
    public void SelectRun(int link)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        if (!_canSeek) throw new NotSupportedException("The stream is not seekable.");
        if ((uint)link >= (uint)_linkFormats.Length || _linkFormats[link] is null) throw new ArgumentOutOfRangeException(nameof(link));
        SelectRunCore(link);
        SeekCore(0);
    }

    private void SelectRunCore(int link)
    {
        var fmt = _linkFormats[link]!.Value;
        int s = link, e = link;
        while (s > 0 && _linkFormats[s - 1] == fmt) s--;
        while (e + 1 < _linkFormats.Length && _linkFormats[e + 1] == fmt) e++;
        _runStart = s;
        _runEnd = e;
        _format = fmt;
        var ogg = _ogg!;
        _runStarts = new long[e - s + 1];
        long cum = 0;
        for (int i = s; i <= e; i++)
        {
            _runStarts[i - s] = cum;
            cum += Math.Max(0, ogg.GetLink(i).LengthFrames - Math.Max(0, _linkOffset[i]));
        }
        _length = cum;
    }

    /// <inheritdoc />
    public int Read(Span<float> destination)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        int ch = _format.Channels;
        int frames = destination.Length / ch;
        int written = 0;
        while (written < frames)
        {
            if (_outPos >= _outLen)
            {
                if (!DecodeNext()) break;
                continue;
            }
            int n = Math.Min(frames - written, _outLen - _outPos);
            Interleave(destination.Slice(written * ch, n * ch), n, ch);
            _outPos += n;
            written += n;
        }
        _pos += written;
        return written;
    }

    private void Interleave(Span<float> dest, int n, int ch)
    {
        var pcm = _synth.Pcm;
        int pos = _outPos;
        if (ch == 1)
        {
            pcm[0].AsSpan(pos, n).CopyTo(dest);
            return;
        }
        for (int o = 0; o < ch; o++)
        {
            var src = pcm[_channelMap[o]].AsSpan(pos, n);
            int d = o;
            for (int i = 0; i < src.Length; i++, d += ch) dest[d] = src[i];
        }
    }

    /// <inheritdoc />
    public int ReadInteger(Span<int> destination) =>
        throw new NotSupportedException("Vorbis is a lossy codec and has no native integer samples.");

    /// <summary>Decodes packets until one yields audio. False at the end of the stream (or run).</summary>
    private bool DecodeNext()
    {
        _outPos = _outLen = 0;
        if (_ended) return false;
        while (true)
        {
            if (!_packets.TryReadPacket(out var p)) { _ended = true; return false; }
            if (p.IsHeader)
            {
                if (!BeginLinkFromHeaders(p)) { _ended = true; return false; }
                continue;
            }
            if (!_synth.Decode(p.Data)) continue;
            int outLen = _synth.OutLength;
            int front = 0;
            _gpos += outLen;
            long end = p.EndFrame;
            if (!_anchored && end >= 0)
            {
                // Unseekable stream: adopt the first position the container reports as the granule clock.
                _gpos = end;
                _anchored = true;
            }
            if (end >= 0)
            {
                // Follow the container's clock. Ahead of us (packets were lost): just adopt it. Behind us at the last
                // page of the stream: the stream ends inside this packet's output (Ogg Vorbis mapping, appendix A.2).
                if (end < _gpos && (_ogg?.PacketEndsStream ?? true))
                {
                    long trim = Math.Min(_gpos - end, outLen);
                    outLen -= (int)trim;
                }
                _gpos = end;
            }
            if (_skipFrames > 0)
            {
                int avail = outLen - front;
                int skip = (int)Math.Min(_skipFrames, avail);
                front += skip;
                _skipFrames -= skip;
            }
            if (outLen - front <= 0) continue;
            _outPos = front;
            _outLen = outLen;
            return true;
        }
    }

    /// <summary>Handles an in-band header packet: the start of the next link of a chain. False ends the run.</summary>
    private bool BeginLinkFromHeaders(PacketView first)
    {
        var d = first.Data;
        if (d.Length < 7 || d[0] != 1) return true; // a stray comment or setup header: nothing to do
        VorbisSetup setup;
        try { setup = VorbisSetup.ParseIdentification(d, _limits); }
        catch (UnsupportedAudioFormatException) { return false; }
        if (FormatOf(setup) != _format) return false;
        // The header must also agree with what the scan of this link found. A damaged file can carry a second
        // identification header with another channel count inside a link; adopting it would leave the active link
        // marked as loaded while the synthesizer holds a setup that does not match the link's format.
        if (_ogg is not null && FormatOf(setup) != _linkFormats[Math.Min(_ogg.CurrentLink, _linkFormats.Length - 1)]) return false;
        ReadRemainingHeaders(_packets, setup, _limits);
        _setup = setup;
        _synth = new VorbisSynthesizer(setup);
        _channelMap = MapFor(setup.Channels);
        if (_ogg is not null) _activeLink = _ogg.CurrentLink;
        StartLinkAt(_activeLink);
        return true;
    }

    /// <inheritdoc />
    public void Seek(long frame)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        if (!_canSeek) throw new NotSupportedException("The stream is not seekable.");
        if (frame < 0 || frame > _length) throw new ArgumentOutOfRangeException(nameof(frame));
        SeekCore(frame);
    }

    private void SeekCore(long frame)
    {
        var ogg = _ogg!;
        _outPos = _outLen = 0;
        _pos = frame;
        _skipFrames = 0;
        if (frame >= _length)
        {
            _ended = true;
            return;
        }
        _ended = false;

        // Which link of the run holds the frame? Positions inside a link count from the first frame delivered; the
        // container works in granule positions, so a link that starts at a positive granule is shifted by that much.
        int link = _runStart;
        while (link < _runEnd && _runStarts[link + 1 - _runStart] <= frame) link++;
        var info = ogg.GetLink(link);
        long t = frame - _runStarts[link - _runStart] + Math.Max(0, _linkOffset[link]);
        if (t > info.LengthFrames) t = info.LengthFrames;

        if (link != _activeLink)
        {
            ogg.SeekToLink(link);
            if (!ogg.TryReadPacket(out var first) || !first.IsHeader) throw new AudioFormatException("Vorbis headers missing in link.");
            var setup = VorbisSetup.ParseIdentification(first.Data, _limits);
            ReadRemainingHeaders(_packets, setup, _limits);
            _setup = setup;
            _synth = new VorbisSynthesizer(setup);
            _channelMap = MapFor(setup.Channels);
            _activeLink = link;
        }
        ogg.SeekToFrame(info.StartFrame + t, out long start);
        long startInLink = start - info.StartFrame;
        _synth.Reset();
        // No usable page (start of the link): the first packet ends at the link's first granule position.
        _gpos = startInLink == 0 ? _linkOffset[link] : startInLink;
        _anchored = true;
        _skipFrames = Math.Max(0, t - _gpos);
    }

    /// <inheritdoc />
    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        _packets.Dispose();
    }
}
