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
/// <b>Comparing with other decoders.</b> In bands coded with perceptual noise substitution the standard fixes only the band's
/// energy, and each decoder fills it with noise of its own (ISO/IEC 14496-3 4.6.13). Output there matches another decoder's in
/// energy, not sample by sample, so waveform differences of the order of the band's level are expected wherever an encoder
/// used it (many encoders do on noise-like content above a few kHz). Compare band energies there; elsewhere output agrees
/// with other conforming decoders to float rounding.
/// </para>
/// <para>
/// <b>HE-AAC.</b> SBR (ISO/IEC 14496-3 4.6.18, in the complex-valued high-quality form) turns a core at rate f into output
/// at 2f, up to 96 kHz; parametric stereo (4.6.19) makes a stereo output from a mono core. Mono and stereo cores, including
/// coupled channel pairs, work at core rates from 8 to 48 kHz. All three signalling forms work: explicit hierarchical
/// (object types 5 and 29), backward-compatible (sync extension 0x2b7 / 0x548 after the core config), and implicit (SBR
/// data in the fill elements of a plain AAC-LC config). An extension rate in the config equal to the core rate selects the
/// downsampled SBR mode, which keeps the core rate; any other extension rate means output at twice the core rate.
/// <see cref="SbrActive"/> and <see cref="PsActive"/> report what the decoder runs, and <see cref="Format"/> is final from
/// the moment the decoder is created. For a stream whose config leaves SBR or parametric stereo open (it announces
/// nothing, or SBR on a mono core without parametric stereo) the decoder reads up to 256 access units at open to see whether
/// SBR and parametric stereo data are present, unless it is created with <c>probeAtOpen</c> false (or through an
/// <see cref="AacCodec"/> whose <see cref="AacCodec.ProbeAtOpen"/> is false), which trusts the config. Parametric stereo is
/// decoded in the baseline profile: the inter-channel and overall phase parameters (IPD/OPD) are parsed and ignored, so a
/// stream that uses them still decodes, to the output of a baseline decoder. The SBR CRC, when present, is skipped, not
/// checked. An implicit HE-AAC stream decodes at its full rate (twice the rate in its config); versions before 1.6.0
/// returned only the core there.
/// </para>
/// <para>
/// <b>Not supported</b> (typed <see cref="UnsupportedAudioFormatException"/>): more than two channels, program config elements
/// with anything but front channels, coupling channels and LFE, core rates above 48 kHz and 7350 Hz, 960-sample frames,
/// object types other than 2 (AAC-LC, alone or under SBR / PS), and two HE-AAC forms that are refused when the decoder knows
/// of them. <i>Dual mono SBR</i>, two single channel elements that each carry SBR data, is refused at open when the config
/// announces SBR on such a layout or the access units read at open show it, else at the first access unit that carries it.
/// An <i>SBR header whose bs_start_freq is not 9 at an SBR output rate of 64, 88.2 or 96 kHz</i> (core 32, 44.1 or 48 kHz) is
/// refused the same way, at open when the header is in the units read at open and otherwise when the header arrives,
/// because the band offsets for those rates are not in the specification the decoder is built from. The other
/// unsupported cases are decided from the config at open, or at the first access unit that uses the element. Errors that
/// are not unsupported features throw <see cref="AudioFormatException"/>.
/// </para>
/// <para>
/// /// <b>Length and position.</b> The container's priming (encoder delay, from an edit list or iTunes gapless data) is dropped
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
    /// <exception cref="UnsupportedAudioFormatException">The stream is not mono or stereo AAC-LC (with or without SBR / PS) at a core rate of 8-48 kHz, or uses an HE-AAC form listed under Not supported.</exception>
    public AacDecoder(IPacketSource packets, AudioLimits? limits = null) : this(packets, limits, true)
    {
    }

    /// <summary>Opens a decoder, choosing whether to look into the stream for SBR and parametric stereo that the config does not announce.</summary>
    /// <param name="packets">A packet source whose <see cref="IPacketSource.CodecConfig"/> holds the AudioSpecificConfig.</param>
    /// <param name="limits">Limits to enforce; the channel count and rate are checked before any buffer is made.</param>
    /// <param name="probeAtOpen">
    /// True (the default of the other constructor) reads up to 256 access units at open when the config leaves SBR or
    /// parametric stereo open, which is right for any stream but delays the first sample on a source that delivers in real
    /// time. The cost: an HE-AAC stream shows its SBR in the first frames, so the probe stops there (about 0.1 to 0.6 ms); AAC-LC
    /// without SBR reads all 256 units (about 8 ms per 100 kB of stream, roughly 6 s of audio at 128 kbit/s). False trusts the config and reads no access units to find out (a config that announces parametric stereo over a stereo channel configuration still reads the first unit to tell a mono core from a stereo one): a config that announces nothing is plain AAC-LC (SBR data
    /// that shows up is skipped and reported by <see cref="SbrDataIgnored"/>), and a config that announces SBR on a mono core
    /// is SBR without parametric stereo unless it announces parametric stereo itself.
    /// </param>
    /// <exception cref="AudioFormatException">The config is missing or invalid.</exception>
    /// <exception cref="UnsupportedAudioFormatException">The stream is not mono or stereo AAC-LC (with or without SBR / PS) at a core rate of 8-48 kHz, or uses an HE-AAC form listed under Not supported.</exception>
    public AacDecoder(IPacketSource packets, AudioLimits? limits, bool probeAtOpen)
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
            if (sbr && coreChannels == 2 && _config.IsDualMonoLayout) throw new UnsupportedAudioFormatException(AacFrameDecoder.DualMonoMessage);
            bool ps = _config.PsSignalled && coreChannels == 1;
            bool downsampled = sbr && _config.ExtensionSampleRate == _config.SampleRate;
            if (probeAtOpen && !ps && (!sbr || coreChannels == 1)) Probe(coreChannels, ref sbr, ref ps, ref downsampled);
            int rate = sbr && !downsampled ? 2 * _config.SampleRate : _config.SampleRate;
            PsActive = ps;
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
            if (parser.SbrDualMono) throw new UnsupportedAudioFormatException(AacFrameDecoder.DualMonoMessage);
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

    /// <summary>The parsed AudioSpecificConfig. Its <see cref="AacAudioConfig.SbrSignalled"/> and <see cref="AacAudioConfig.PsSignalled"/> say what the config announced; <see cref="SbrActive"/> and <see cref="PsActive"/> say what this decoder is doing.</summary>
    public AacAudioConfig Config => _config;

    /// <summary>
    /// True when the decoder runs spectral band replication: the config announced it, or (for a stream that announced nothing)
    /// SBR data showed in the access units read at open. Fixed when the decoder is created and valid at once. In the
    /// downsampled SBR mode this is true while <see cref="Format"/> keeps the core rate. False for plain AAC-LC, including
    /// a stream whose SBR data first appears after the access units read at open (see <see cref="SbrDataIgnored"/>).
    /// </summary>
    public bool SbrActive => _sbr is not null;

    /// <summary>
    /// True when parametric stereo expands a mono core to two output channels: announced by the config, or found in the access
    /// units read at open. Fixed when the decoder is created and valid at once. Implies <see cref="SbrActive"/>.
    /// </summary>
    public bool PsActive { get; }

    /// <summary>
    /// True once an access unit carried SBR data in a fill element that this decoder skipped. That happens only when the
    /// stream announced no SBR and none showed in the access units read at open, so the decoder runs as plain AAC-LC
    /// (<see cref="SbrActive"/> is false): the output is the core only, at the core rate. Never true while <see cref="SbrActive"/> is.
    /// It turns true while reading, at the first such unit, so check it after decoding.
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
            if (decoded && _core.SbrDualMono) throw new UnsupportedAudioFormatException(AacFrameDecoder.DualMonoMessage);
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
