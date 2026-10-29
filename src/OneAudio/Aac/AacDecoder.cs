namespace OneAudio.Aac;

/// <summary>
/// Decodes AAC-LC (audio object type 2), mono or stereo, at core rates from 8 to 48 kHz, from packets of an MP4/M4A/3GP
/// track or an ADTS stream. Output is interleaved float with 1.0 as 16-bit full scale (32768); no clipping or dithering.
/// <para>
/// <b>Tools.</b> Single and pair channel elements, section data, scalefactors, pulse data, TNS, perceptual noise
/// substitution, mid/side and intensity stereo, sine and KBD windows, all four window sequences; data stream and fill
/// elements are skipped. The behaviour of each tool follows ISO/IEC 14496-3 as reproduced by 3GPP TS 26.410.
/// </para>
/// <para>
/// <b>Not supported</b> (typed <see cref="UnsupportedAudioFormatException"/> at open or at the first frame that needs it):
/// more than two channels, program config elements, coupling channels and LFE, rates above 48 kHz and 7350 Hz, 960-sample
/// frames, object types other than 2, and explicit SBR / parametric stereo signalling. A plain AAC-LC stream that carries
/// SBR data in fill elements (implicit signalling) decodes as its core: output is at the core rate with no high band, and
/// <see cref="SbrDataIgnored"/> turns true when such data was skipped.
/// </para>
/// <para>
/// <b>Length and position.</b> The container's priming (encoder delay, from an edit list or iTunes gapless data) is dropped
/// from the start and <see cref="LengthFrames"/> is the container's trimmed length, so the decoded length is the true
/// length. When the container counts frames at twice the core rate (an implicit-SBR file), positions are converted to the
/// core rate. Seeking decodes one extra frame before the target to rebuild the MDCT overlap; the result then equals a
/// linear decode exactly (the noise of perceptual noise substitution is seeded per frame number so it matches too).
/// </para>
/// <para>
/// <b>Errors.</b> A malformed access unit throws <see cref="AudioFormatException"/>. With <see cref="ConcealErrors"/> set, a
/// malformed unit is concealed instead; an empty unit (a lost packet) is always concealed. Concealment repeats the previous
/// long-block spectrum at half level per consecutive lost frame (silent from the fifth) and fades out after short blocks;
/// each concealed frame counts in <see cref="ConcealedFrames"/>.
/// </para>
/// </summary>
public sealed class AacDecoder : IAudioDecoder
{
    private const int Frame = AacFrameDecoder.FrameSize;

    private readonly IPacketSource _src;
    private readonly AacFrameDecoder _core;
    private readonly AacAudioConfig _config;
    private readonly int _channels;
    private readonly float[] _buf;
    private readonly int _factor;     // container frames per core frame (2 for an implicit-SBR file counted at the output rate)
    private readonly int _prime;      // core frames dropped from the start of the decoded stream
    private readonly long _length;    // core frames, or -1
    private int _bufPos;
    private int _bufCount;
    private long _skip;               // decoded frames still to drop
    private long _pos;
    private long _frameIndex;         // index of the next packet's access unit, counted from the start of the media
    private bool _disposed;

    /// <summary>Opens a decoder over the packets of an AAC track. The decoder takes ownership of <paramref name="packets"/>.</summary>
    /// <param name="packets">A packet source whose <see cref="IPacketSource.CodecConfig"/> holds the AudioSpecificConfig.</param>
    /// <param name="limits">Limits to enforce; the channel count and rate are checked before any buffer is made.</param>
    /// <exception cref="AudioFormatException">The config is missing or invalid.</exception>
    /// <exception cref="UnsupportedAudioFormatException">The stream is not mono or stereo AAC-LC at 8-48 kHz without explicit SBR.</exception>
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
            limits.CheckChannels(_config.Channels);
            limits.CheckSampleRate(_config.SampleRate);
            _channels = _config.Channels;
            _core = new AacFrameDecoder(_config);
            _buf = new float[Frame * _channels];
            Format = new AudioFormat(_config.SampleRate, _channels, AudioFormat.DefaultMask(_channels));
            int rate = packets.SampleRate;
            _factor = rate > 0 && rate > _config.SampleRate && rate % _config.SampleRate == 0 ? rate / _config.SampleRate : 1;
            _prime = packets.PrimingFrames / _factor;
            long len = packets.LengthFrames;
            _length = len < 0 ? -1 : len / _factor;
            _skip = _prime;
        }
        catch
        {
            packets.Dispose();
            throw;
        }
    }

    /// <summary>The parsed AudioSpecificConfig.</summary>
    public AacAudioConfig Config => _config;

    /// <summary>True once an access unit carried SBR data in a fill element that this decoder skipped (implicit HE-AAC signalling).</summary>
    public bool SbrDataIgnored => _core.SbrDataSeen;

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
            if (_length >= 0 && _pos >= _length) break;
            if (_bufPos >= _bufCount)
            {
                if (!Fill()) break;
                continue;
            }
            int n = Math.Min(want - got, _bufCount - _bufPos);
            if (_length >= 0) n = (int)Math.Min(n, _length - _pos);
            _buf.AsSpan(_bufPos * _channels, n * _channels).CopyTo(destination.Slice(got * _channels));
            _bufPos += n;
            got += n;
            _pos += n;
        }
        return got;
    }

    private bool Fill()
    {
        if (!_src.TryReadPacket(out var packet)) return false;
        long index = _frameIndex++;
        if (packet.Data.Length == 0)
        {
            _core.Conceal(_buf);
            ConcealedFrames++;
        }
        else
        {
            try
            {
                _core.Decode(packet.Data, _buf, index);
            }
            catch (AudioFormatException ex) when (ConcealErrors && ex is not UnsupportedAudioFormatException)
            {
                _core.Conceal(_buf);
                ConcealedFrames++;
            }
        }
        long drop = Math.Min(_skip, Frame);
        _skip -= drop;
        _bufPos = (int)drop;
        _bufCount = Frame;
        return true;
    }

    /// <inheritdoc />
    public void Seek(long frame)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        if (!_src.CanSeek) throw new NotSupportedException("The source is not seekable.");
        ArgumentOutOfRangeException.ThrowIfNegative(frame);
        if (_length >= 0) ArgumentOutOfRangeException.ThrowIfGreaterThan(frame, _length);

        long want = Math.Max(frame - Frame, -(long)_prime);
        long start;
        while (true)
        {
            _src.SeekToFrame(want * _factor, out long startSrc);
            start = startSrc / _factor;
            // The landing frame must be a whole frame before the target so its overlap is rebuilt, unless it is the very start.
            if (start <= -(long)_prime || frame - start >= Frame) break;
            want = Math.Max(want - Frame, -(long)_prime);
        }
        _core.Reset();
        _frameIndex = Math.Max(0, (start + _prime) / Frame);
        _skip = Math.Max(0, frame - start);
        if (_length >= 0 && frame >= _length) _skip = 0;
        _bufPos = _bufCount = 0;
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
