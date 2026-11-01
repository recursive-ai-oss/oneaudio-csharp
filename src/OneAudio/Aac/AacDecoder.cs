namespace OneAudio.Aac;

/// <summary>
/// Decodes AAC-LC (audio object type 2), mono or stereo, at core rates from 8 to 48 kHz, with spectral band replication
/// and parametric stereo (HE-AAC v1 and v2), from packets of an MP4/M4A/3GP track or an ADTS stream. Output is interleaved
/// float with 1.0 as 16-bit full scale (32768); no clipping or dithering.
/// <para>
/// <b>Tools.</b> Single and pair channel elements, section data, scalefactors, pulse data, TNS, perceptual noise
/// substitution, mid/side and intensity stereo, sine and KBD windows, all four window sequences; data stream elements are
/// skipped. The behaviour of each tool follows ISO/IEC 14496-3 as reproduced by 3GPP TS 26.410.
/// </para>
/// <para>
/// <b>HE-AAC.</b> SBR (ISO/IEC 14496-3 4.6.18, in the complex-valued high-quality form) turns a core at rate f into output
/// at 2f; parametric stereo (4.6.19) makes a stereo output from a mono core. All three signalling forms work: explicit
/// hierarchical (object types 5 and 29), backward-compatible (sync extension 0x2b7 / 0x548 after the core config), and
/// implicit (SBR data in the fill elements of a plain AAC-LC config). For the first two the config says what to expect; for a
/// stream that announces nothing, or announces SBR without parametric stereo on a mono core, the decoder reads up to
/// 256 access units when it is opened to see whether SBR and parametric stereo data are present, so <see cref="Format"/> is
/// right from the first read. An output rate equal to the core rate in the config selects the downsampled SBR mode, which
/// keeps the core rate. Parametric stereo with inter-channel phase and overall phase parameters (IPD/OPD) is decoded with
/// those parameters ignored, as a baseline-profile decoder does. The SBR CRC, when present, is skipped, not checked.
/// An implicit HE-AAC stream decodes at its full rate (twice the rate in its config); versions before 1.6.0 returned only
/// the core there.
/// </para>
/// <para>
/// <b>Not supported</b> (typed <see cref="UnsupportedAudioFormatException"/> at open or at the first frame that needs it):
/// more than two channels, program config elements with anything but front channels, coupling channels and LFE, rates above
/// 48 kHz and 7350 Hz for the core, 960-sample frames, object types other than 2, dual-mono SBR (two single channel
/// elements, each with its own SBR data) and an SBR output rate that is neither the core rate nor twice it.
/// </para>
/// <para>
/// <b>Length and position.</b> The container's priming (encoder delay, from an edit list or iTunes gapless data) is dropped
/// from the start and <see cref="LengthFrames"/> is the container's trimmed length, so the decoded length is the true
/// length. Container positions are counted at the container's sample rate and converted to the output rate. Seeking decodes
/// whole frames before the target to rebuild the MDCT overlap; for plain AAC-LC the result then equals a linear decode
/// exactly (the noise of perceptual noise substitution is seeded per frame number so it matches too). With SBR, seeking first
/// decodes three further frames to rebuild the filter bank and envelope history; the result is close to, but not bit-identical
/// with, a linear decode, because the SBR noise generator and parametric stereo decorrelators run on from where they were.
/// </para>
/// <para>
/// <b>Errors.</b> A malformed access unit throws <see cref="AudioFormatException"/>. With <see cref="ConcealErrors"/> set, a
/// malformed unit is concealed instead; an empty unit (a lost packet) is always concealed. Concealment repeats the previous
/// long-block spectrum at half level per consecutive lost frame (silent from the fifth) and fades out after short blocks;
/// each concealed frame counts in <see cref="ConcealedFrames"/>. A damaged SBR payload never throws: that frame's high
/// band is concealed (held near the previous energies, without noise).
/// </para>
/// </summary>
public sealed class AacDecoder : IAudioDecoder
{
    private const int CoreFrame = AacFrameDecoder.FrameSize;
    private const int ProbeUnits = 256;
    private const int SbrPreroll = 3;   // extra frames decoded after a seek to settle the SBR filter banks and envelopes

    private readonly IPacketSource _src;
    private readonly AacFrameDecoder _core;
    private readonly SbrDecoder? _sbr;
    private readonly AacAudioConfig _config;
    private readonly int _channels;      // output channels
    private readonly int _frame;         // output frames per access unit
    private readonly float[] _buf;
    private readonly float[] _coreBuf;   // core output when SBR is on; the same array as _buf otherwise
    private readonly long _num, _den;    // output frames per container frame = _num / _den
    private readonly int _prime;         // output frames dropped from the start of the decoded stream
    private readonly long _length;       // output frames, or -1
    private int _bufPos;
    private int _bufCount;
    private long _skip;                  // decoded frames still to drop
    private long _pos;
    private long _dropped;               // frames removed so far by PacketView.DiscardEndFrames; the length cap counts them as played
    private int _tail;                   // frames the current packet is dropping from its end
    private long _frameIndex;            // index of the next packet's access unit, counted from the start of the media
    private bool _disposed;

    // packets read while probing for SBR, replayed before the source is read again
    private byte[][]? _replay;
    private int[]? _replayDiscard;
    private int _replayCount, _replayPos;

    /// <summary>Opens a decoder over the packets of an AAC track. The decoder takes ownership of <paramref name="packets"/>.</summary>
    /// <param name="packets">A packet source whose <see cref="IPacketSource.CodecConfig"/> holds the AudioSpecificConfig.</param>
    /// <param name="limits">Limits to enforce; the channel count and rate are checked before any buffer is made.</param>
    /// <exception cref="AudioFormatException">The config is missing or invalid.</exception>
    /// <exception cref="UnsupportedAudioFormatException">The stream is not mono or stereo AAC-LC (with or without SBR / PS) at a core rate of 8-48 kHz.</exception>
    public AacDecoder(IPacketSource packets, AudioLimits? limits = null)
    {
        ArgumentNullException.ThrowIfNull(packets);
        limits ??= AudioLimits.Default;
        _src = packets;
        try
        {
            if (packets.Codec != CodecId.Aac) throw new ArgumentException("The packet source does not carry AAC.", nameof(packets));
            _config = AacAudioConfig.Parse(packets.CodecConfig.Span);
            _config.CheckSupported();
            int coreChannels = _config.CoreChannels;
            // Parametric stereo is signalled with a stereo channelConfiguration, but real files carry either a mono core
            // (an SCE: the PS case) or a stereo core (a CPE: plain SBR stereo, to which no PS applies). The first access
            // unit says which.
            if (_config.PsSignalled && _config.Channels == 2) coreChannels = DetectCoreChannels();
            limits.CheckChannels(coreChannels);
            limits.CheckSampleRate(_config.SampleRate);
            bool sbr = _config.SbrSignalled || _config.PsSignalled;
            bool ps = _config.PsSignalled && coreChannels == 1;
            bool downsampled = sbr && _config.ExtensionSampleRate == _config.SampleRate;
            if (!ps && (!sbr || coreChannels == 1)) Probe(coreChannels, ref sbr, ref ps, ref downsampled);
            int rate = sbr && !downsampled ? 2 * _config.SampleRate : _config.SampleRate;
            _channels = ps ? 2 : coreChannels;
            limits.CheckChannels(_channels);
            limits.CheckSampleRate(rate);
            _core = new AacFrameDecoder(_config, coreChannels);
            _frame = sbr ? (downsampled ? CoreFrame : 2 * CoreFrame) : CoreFrame;
            _buf = new float[_frame * _channels];
            if (sbr)
            {
                _sbr = new SbrDecoder(_config.SampleRate, coreChannels, downsampled, ps);
                _coreBuf = new float[CoreFrame * coreChannels];
            }
            else
            {
                _coreBuf = _buf;
            }
            Format = new AudioFormat(rate, _channels, AudioFormat.DefaultMask(_channels));
            long container = packets.SampleRate;
            _num = rate;
            _den = container > 0 ? container : rate;
            _prime = (int)Math.Min(int.MaxValue, ToOutput(packets.PrimingFrames));
            long len = packets.LengthFrames;
            _length = len < 0 ? -1 : ToOutput(len);
            _skip = _prime;
        }
        catch
        {
            packets.Dispose();
            throw;
        }
    }

    private long ToOutput(long containerFrames) => containerFrames * _num / _den;

    private long ToContainer(long outputFrames) => outputFrames * _den / _num;

    // Decides between a mono and a stereo core from the first non-empty access unit that parses as one or the other.
    private int DetectCoreChannels()
    {
        _replay = new byte[ProbeUnits][];
        _replayDiscard = new int[ProbeUnits];
        var mono = new AacFrameDecoder(_config, 1);
        var stereo = new AacFrameDecoder(_config, 2);
        while (_replayCount < ProbeUnits && _src.TryReadPacket(out var packet))
        {
            _replay[_replayCount] = packet.Data.ToArray();
            _replayDiscard[_replayCount] = packet.DiscardEndFrames;
            _replayCount++;
            if (packet.Data.Length == 0) continue;
            if (mono.Probe(packet.Data, out _) == AacProbeResult.Frame) return 1;
            if (stereo.Probe(packet.Data, out _) == AacProbeResult.Frame) return 2;
        }
        return 1;
    }

    // Looks at the first access units for SBR and parametric stereo payloads, for streams whose config does not say.
    private void Probe(int coreChannels, ref bool sbr, ref bool ps, ref bool downsampled)
    {
        _replay = new byte[ProbeUnits][];
        _replayDiscard = new int[ProbeUnits];
        var parser = new AacFrameDecoder(_config);
        var scan = new SbrDecoder(_config.SampleRate, coreChannels, false, coreChannels == 1);
        bool found = false;
        while (_replayCount < ProbeUnits && _src.TryReadPacket(out var packet))
        {
            _replay[_replayCount] = packet.Data.ToArray();
            _replayDiscard[_replayCount] = packet.DiscardEndFrames;
            _replayCount++;
            if (packet.Data.Length == 0) continue;
            if (parser.Probe(packet.Data, out _) != AacProbeResult.Frame || !parser.SbrPresent) continue;
            found = true;
            scan.Scan(parser.SbrPayload, parser.SbrBits, parser.SbrCrc, parser.SbrPair);
            if (coreChannels == 2 || scan.PsSeen) break;
        }
        if (found)
        {
            sbr = true;
            ps |= scan.PsSeen;
        }
    }

    /// <summary>The parsed AudioSpecificConfig.</summary>
    public AacAudioConfig Config => _config;

    /// <summary>
    /// True once an access unit carried SBR data in a fill element that this decoder skipped. That happens only when the
    /// stream announced no SBR and none showed in its first 256 access units, so the decoder was opened as plain AAC-LC.
    /// </summary>
    public bool SbrDataIgnored => _sbr is null && _core.SbrDataSeen;

    /// <summary>The coding tools the frames decoded so far have used; a diagnostic for conformance reports.</summary>
    public AacTools ToolsSeen => _core.Tools;

    /// <summary>
    /// When true, a malformed access unit is concealed (and counted) instead of throwing <see cref="AudioFormatException"/>.
    /// Unsupported features always throw. Default false.
    /// </summary>
    public bool ConcealErrors { get; set; }

    /// <summary>How many access units were replaced by concealment since the decoder was opened.</summary>
    public long ConcealedFrames { get; private set; }

    /// <inheritdoc />
    public AudioFormat Format { get; }

    /// <inheritdoc />
    public long LengthFrames => _length;

    /// <inheritdoc />
    public long PositionFrames => _pos;

    /// <inheritdoc />
    public bool CanSeek => _src.CanSeek;

    /// <inheritdoc />
    public bool HasNativeIntegers => false;

    /// <inheritdoc />
    public int ReadInteger(Span<int> destination) => throw new NotSupportedException("AAC is a lossy format; use Read.");

    /// <inheritdoc />
    public int Read(Span<float> destination)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        int want = destination.Length / _channels;
        int got = 0;
        while (got < want)
        {
            if (_length >= 0 && _pos + _dropped >= _length) break;
            if (_bufPos >= _bufCount)
            {
                _dropped += _tail;
                _tail = 0;
                if (!Fill()) break;
                continue;
            }
            int n = Math.Min(want - got, _bufCount - _bufPos);
            if (_length >= 0) n = (int)Math.Min(n, _length - _pos - _dropped);
            _buf.AsSpan(_bufPos * _channels, n * _channels).CopyTo(destination.Slice(got * _channels));
            _bufPos += n;
            got += n;
            _pos += n;
        }
        return got;
    }

    private bool NextPacket(out ReadOnlySpan<byte> data, out int discardEnd)
    {
        if (_replay is not null && _replayPos < _replayCount)
        {
            data = _replay[_replayPos];
            discardEnd = _replayDiscard![_replayPos];
            _replayPos++;
            return true;
        }
        if (!_src.TryReadPacket(out var packet))
        {
            data = default;
            discardEnd = 0;
            return false;
        }
        data = packet.Data;
        discardEnd = packet.DiscardEndFrames;
        return true;
    }

    private bool Fill()
    {
        if (!NextPacket(out var data, out int discardEnd)) return false;
        long index = _frameIndex++;
        bool decoded = false;
        if (data.Length == 0)
        {
            _core.Conceal(_coreBuf);
            ConcealedFrames++;
        }
        else
        {
            try
            {
                _core.Decode(data, _coreBuf, index);
                decoded = true;
            }
            catch (AudioFormatException ex) when (ConcealErrors && ex is not UnsupportedAudioFormatException)
            {
                _core.Conceal(_coreBuf);
                ConcealedFrames++;
            }
        }
        if (_sbr is not null)
        {
            if (decoded && _core.SbrPresent) _sbr.SetPayload(_core.SbrPayload, _core.SbrBits, _core.SbrCrc, _core.SbrPair);
            else _sbr.NoPayload();
            _sbr.Process(_coreBuf, _buf);
        }
        long drop = Math.Min(_skip, _frame);
        _skip -= drop;
        _bufPos = (int)drop;
        // PacketView.DiscardEndFrames: the tail of this packet's output is not delivered (never below the priming already dropped).
        long discard = discardEnd == 0 ? 0 : ToOutput(discardEnd);
        _bufCount = (int)Math.Max(drop, _frame - discard);
        _tail = _frame - _bufCount;
        return true;
    }

    /// <inheritdoc />
    public void Seek(long frame)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        if (!_src.CanSeek) throw new NotSupportedException("The source is not seekable.");
        ArgumentOutOfRangeException.ThrowIfNegative(frame);
        if (_length >= 0) ArgumentOutOfRangeException.ThrowIfGreaterThan(frame, _length);

        long lead = (_sbr is null ? 1 : 1 + SbrPreroll) * (long)_frame;
        long want = Math.Max(frame - lead, -(long)_prime);
        long start;
        while (true)
        {
            _src.SeekToFrame(ToContainer(want), out long startSrc);
            start = ToOutput(startSrc);
            // The landing frame must be whole frames before the target so the overlap is rebuilt, unless it is the very start.
            if (start <= -(long)_prime || frame - start >= lead) break;
            want = Math.Max(want - _frame, -(long)_prime);
        }
        _replayCount = _replayPos = 0;
        _core.Reset();
        _sbr?.Reset(start <= -(long)_prime);
        _frameIndex = Math.Max(0, (start + _prime) / _frame);
        _skip = Math.Max(0, frame - start);
        if (_length >= 0 && frame >= _length) _skip = 0;
        _bufPos = _bufCount = 0;
        _dropped = 0;
        _tail = 0;
        _pos = frame;
    }

    /// <inheritdoc />
    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        _src.Dispose();
    }
}
