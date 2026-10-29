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
    private int _bufPos;
    private int _bufCount;
    private long _position;
    private bool _disposed;

    public AmrStreamDecoder(IPacketSource packets, IAmrFrameDecoder decoder, int sampleRate)
    {
        _packets = packets;
        _decoder = decoder;
        _frameSize = decoder.SamplesPerFrame;
        _pcm = new short[_frameSize];
        Format = new AudioFormat(sampleRate, 1, ChannelMask.FrontCenter);
    }

    public AudioFormat Format { get; }

    public long LengthFrames => _packets.LengthFrames;

    public long PositionFrames => _position;

    public bool CanSeek => _packets.CanSeek;

    public bool HasNativeIntegers => false;

    private bool DecodeNext()
    {
        while (true)
        {
            if (!_packets.TryReadPacket(out PacketView packet)) return false;
            if (packet.IsHeader) continue;
            _decoder.Decode(packet.Data, _pcm);
            _bufPos = 0;
            _bufCount = _frameSize;
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
