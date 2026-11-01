// Copyright (c) Recursive AI, LLC. MIT OR Apache-2.0.
namespace OneAudio.Amr;

/// <summary>
/// Decodes an AMR or AMR-WB packet stream (one TOC-prefixed speech frame per packet, as AMR files and 3GP samples hold
/// them) into 8 kHz or 16 kHz mono float. <b>Seeking:</b> the codec state cannot be reconstructed from a position, so a
/// seek resets the decoder, restarts a few frames earlier (<see cref="PrerollFrames"/>, 200 ms) and discards that audio
/// so the filters and gain predictors have converged. The position is exact to the sample; the samples after the seek
/// match a sequential decode closely but not bit for bit (comfort-noise state and long-term predictors are not
/// restored). Decode from the start and read forward when bit-exact output is needed.
/// </summary>
internal sealed class AmrStreamDecoder : IAudioDecoder
{
    /// <summary>Frames decoded and thrown away after a seek so the decoder state converges: 10 (200 ms).</summary>
    internal const int PrerollFrames = 10;

    private readonly IPacketSource _packets;
    private readonly IAmrFrameDecoder _decoder;
    private readonly int _frameSize;
    private readonly short[] _pcm;
    private readonly bool _wide;
    // The packet being decoded, kept so a 3GP sample holding several frames is decoded one frame per refill.
    private byte[] _packet = [];
    private int _packetLen;
    private int _packetPos;
    private int _packetFrames;    // frames in the packet, set when it is loaded
    private int _frameInPacket;
    private int _packetDiscard;   // PacketView.DiscardEndFrames
    private int _bufPos;
    private int _bufCount;
    private long _position;
    private bool _disposed;

    public AmrStreamDecoder(IPacketSource packets, IAmrFrameDecoder decoder, int sampleRate)
    {
        _packets = packets;
        _decoder = decoder;
        _frameSize = decoder.SamplesPerFrame;
        _wide = sampleRate == 16000;
        _pcm = new short[_frameSize];
        Format = new AudioFormat(sampleRate, 1, ChannelMask.FrontCenter);
    }

    public AudioFormat Format { get; }

    public long LengthFrames => _packets.LengthFrames;

    public long PositionFrames => _position;

    public bool CanSeek => _packets.CanSeek;

    public bool HasNativeIntegers => false;

    // 3GPP TS 26.244 8.3: a 3GP sample holds one or more storage-format frames (TOC byte + speech bytes), the count being
    // damr's frames-per-sample. Walk the TOC bytes instead of trusting that field, and require them to fill the sample exactly.
    // A packet whose first frame is unknown goes to the frame decoder whole, which reports the damaged frame.
    private int NextFrameLength()
    {
        int rest = _packetLen - _packetPos;
        if (_packetPos == 0)
        {
            int total = 0, frames = 0;
            while (total < _packetLen)
            {
                int payload = AmrFrameTable.PayloadBytes(_wide, AmrFrameTable.FrameType(_packet[total]));
                if (payload < 0 || total + 1 + payload > _packetLen)
                {
                    if (total == 0) { _packetFrames = 1; return rest; }
                    throw new AudioFormatException("The frames of an AMR sample do not fill the sample exactly.");
                }
                total += 1 + payload;
                frames++;
            }
            _packetFrames = frames;
        }
        return Math.Min(rest, 1 + Math.Max(0, AmrFrameTable.PayloadBytes(_wide, AmrFrameTable.FrameType(_packet[_packetPos]))));
    }

    private bool DecodeNext()
    {
        while (true)
        {
            if (_packetPos >= _packetLen)
            {
                if (!_packets.TryReadPacket(out PacketView packet)) return false;
                if (packet.IsHeader) continue;
                if (packet.Data.IsEmpty) throw new AudioFormatException("Empty AMR frame.");
                if (_packet.Length < packet.Data.Length) _packet = new byte[packet.Data.Length]; // bounded by the source's packet limit
                packet.Data.CopyTo(_packet);
                _packetLen = packet.Data.Length;
                _packetPos = 0;
                _frameInPacket = 0;
                _packetDiscard = packet.DiscardEndFrames;
            }
            int len = NextFrameLength();
            _decoder.Decode(_packet.AsSpan(_packetPos, len), _pcm);
            _packetPos += len;
            // PacketView.DiscardEndFrames trims the end of the packet's audio, which may cover several whole frames; they are still
            // decoded so the codec state stays in step.
            long tail = (long)(_packetFrames - 1 - _frameInPacket) * _frameSize;
            _frameInPacket++;
            long keep = Math.Clamp(_frameSize - Math.Max(0, _packetDiscard - tail), 0, _frameSize);
            if (keep == 0) continue;
            _bufPos = 0;
            _bufCount = (int)keep;
            return true;
        }
    }

    public int Read(Span<float> destination)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        int done = 0;
        int want = destination.Length;
        while (done < want)
        {
            if (_bufPos >= _bufCount && !DecodeNext()) break;
            int n = Math.Min(want - done, _bufCount - _bufPos);
            for (int i = 0; i < n; i++) destination[done + i] = _pcm[_bufPos + i] * (1f / 32768f);
            _bufPos += n;
            done += n;
        }
        _position += done;
        return done;
    }

    public int ReadInteger(Span<int> destination) =>
        throw new NotSupportedException("AMR is lossy; there are no native integer samples.");

    public void Seek(long frame)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        if (!CanSeek) throw new NotSupportedException("The packet source is not seekable.");
        long length = LengthFrames;
        if (frame < 0 || (length >= 0 && frame > length)) throw new ArgumentOutOfRangeException(nameof(frame));

        long frameIndex = frame / _frameSize;
        long pre = Math.Min(frameIndex, PrerollFrames);
        _packets.SeekToFrame((frameIndex - pre) * _frameSize, out long start);
        _decoder.Reset();
        _bufPos = _bufCount = 0;
        _packetPos = _packetLen = 0;
        _frameInPacket = 0;
        long discard = frame - start;
        if (discard < 0) throw new AudioFormatException("The container positioned after the requested frame.");
        while (discard > 0)
        {
            if (!DecodeNext()) break; // past the end: the next read reports the end of the stream
            long take = Math.Min(discard, _bufCount);
            _bufPos = (int)take;
            discard -= take;
        }
        _position = frame;
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        _packets.Dispose();
    }
}

/// <summary>The AMR narrowband codec factory.</summary>
internal sealed class AmrNbCodec : IAudioCodec
{
    public static readonly AmrNbCodec Instance = new();

    public CodecId Id => CodecId.AmrNb;

    public IAudioDecoder CreateDecoder(IPacketSource packets, AudioLimits limits)
    {
        ArgumentNullException.ThrowIfNull(packets);
        ArgumentNullException.ThrowIfNull(limits);
        try
        {
            limits.CheckSampleRate(AmrNbDecoder.SampleRate);
            return new AmrStreamDecoder(packets, new AmrNbDecoder(), AmrNbDecoder.SampleRate);
        }
        catch
        {
            packets.Dispose();
            throw;
        }
    }
}

/// <summary>The AMR-WB codec factory.</summary>
internal sealed class AmrWbCodec : IAudioCodec
{
    public static readonly AmrWbCodec Instance = new();

    public CodecId Id => CodecId.AmrWb;

    public IAudioDecoder CreateDecoder(IPacketSource packets, AudioLimits limits)
    {
        ArgumentNullException.ThrowIfNull(packets);
        ArgumentNullException.ThrowIfNull(limits);
        try
        {
            limits.CheckSampleRate(AmrWbDecoder.SampleRate);
            return new AmrStreamDecoder(packets, new AmrWbDecoder(), AmrWbDecoder.SampleRate);
        }
        catch
        {
            packets.Dispose();
            throw;
        }
    }
}
