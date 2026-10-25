namespace OneAudio.Opus;

/// <summary>
/// An Opus encoder for one to eight channels using channel mapping family 1 of RFC 7845 section 5.1.1.2: the channels are
/// coded as coupled (stereo) and uncoupled (mono) streams in one multistream packet (RFC 6716 appendix B). One or two channels give
/// the ordinary single-stream packets.
/// </summary>
/// <remarks>
/// Input channels are in the Vorbis order of the Opus specification (for 6 channels: front left, center, front right, back
/// left, back right, LFE). The bitrate is shared among the streams in proportion to their channel count. Not thread safe;
/// <see cref="Encode(ReadOnlySpan{float}, int, Span{byte})"/> allocates nothing.
/// </remarks>
public sealed class OpusMultistreamEncoder
{
    private static readonly byte[][] Mappings =
    [
        [0],
        [0, 1],
        [0, 2, 1],
        [0, 1, 2, 3],
        [0, 4, 1, 2, 3],
        [0, 4, 1, 2, 3, 5],
        [0, 4, 1, 2, 3, 5, 6],
        [0, 6, 1, 2, 3, 4, 5, 7],
    ];

    private static readonly int[] StreamCounts = [1, 1, 2, 2, 3, 4, 5, 5];
    private static readonly int[] CoupledCounts = [0, 1, 1, 2, 2, 2, 2, 3];

    private readonly OpusEncoder[] _streams;
    private readonly float[][] _streamPcm;
    private readonly byte[] _scratch = new byte[1280 + 8];
    private readonly int _channels;
    private readonly int _coupled;
    private readonly byte[] _mapping;
    private uint _finalRange;

    /// <summary>Creates an encoder for <paramref name="settings"/>.Channels (1 to 8) channels.</summary>
    /// <exception cref="ArgumentOutOfRangeException">A setting is invalid or the channel count is not 1 to 8.</exception>
    public OpusMultistreamEncoder(OpusEncoderSettings settings)
    {
        ArgumentNullException.ThrowIfNull(settings);
        if (settings.Channels is < 1 or > 8)
            throw new ArgumentOutOfRangeException(nameof(settings), settings.Channels, "Channels must be 1 to 8.");
        _channels = settings.Channels;
        _coupled = CoupledCounts[_channels - 1];
        int streams = StreamCounts[_channels - 1];
        _mapping = Mappings[_channels - 1];
        _streams = new OpusEncoder[streams];
        _streamPcm = new float[streams][];
        int weights = _channels; // a coupled stream carries two channels, any other one
        for (int s = 0; s < streams; s++)
        {
            int ch = s < _coupled ? 2 : 1;
            int rate = settings.BitrateBps > 0 ? (int)((long)settings.BitrateBps * ch / weights) : settings.BitrateBps;
            if (rate > 0 && rate < 500) rate = 500;
            _streams[s] = new OpusEncoder(settings with { Channels = ch, BitrateBps = rate, ForceChannels = Math.Min(settings.ForceChannels, ch) });
            _streamPcm[s] = new float[2880 * ch];
        }
    }

    /// <summary>Input channel count.</summary>
    public int Channels => _channels;

    /// <summary>Number of Opus streams in each packet.</summary>
    public int Streams => _streams.Length;

    /// <summary>Number of streams that carry two (coupled) channels; they come first.</summary>
    public int CoupledStreams => _coupled;

    /// <summary>For each input channel, the decoded channel it maps to (RFC 7845 channel mapping table).</summary>
    public ReadOnlySpan<byte> ChannelMapping => _mapping;

    /// <summary>XOR of the streams' final range coder states after the last packet.</summary>
    public uint FinalRange => _finalRange;

    /// <summary>Samples of look-ahead (see <see cref="OpusEncoder.LookaheadSamples"/>).</summary>
    public int LookaheadSamples => _streams[0].LookaheadSamples;

    /// <summary>Clears all signal history.</summary>
    public void Reset()
    {
        foreach (var s in _streams) s.Reset();
        _finalRange = 0;
    }

    /// <summary>Encodes one multistream packet; arguments as for <see cref="OpusEncoder.Encode(ReadOnlySpan{float}, int, Span{byte})"/>.</summary>
    /// <returns>Bytes written.</returns>
    /// <exception cref="ArgumentException">The frame size is invalid or a buffer is too small.</exception>
    public int Encode(ReadOnlySpan<float> pcm, int frameSize, Span<byte> packet)
    {
        if (_streams.Length == 1) return EncodeSingle(pcm, frameSize, packet);
        if (pcm.Length < frameSize * _channels)
            throw new ArgumentException("pcm holds fewer than frameSize samples per channel.", nameof(pcm));
        if (packet.Length < 2 * _streams.Length)
            throw new ArgumentException("The packet buffer is too small.", nameof(packet));
        // De-interleave the input into the streams' own buffers following the mapping table.
        for (int c = 0; c < _channels; c++)
        {
            int m = _mapping[c];
            int stream, inStream, sch;
            if (m < 2 * _coupled) { stream = m / 2; inStream = m & 1; sch = 2; }
            else { stream = m - _coupled; inStream = 0; sch = 1; }
            var dst = _streamPcm[stream];
            for (int i = 0; i < frameSize; i++) dst[i * sch + inStream] = pcm[i * _channels + c];
        }
        int pos = 0;
        uint range = 0;
        // Share the packet budget evenly: a stream may use at most what remains divided by the streams still to code.
        for (int s = 0; s < _streams.Length; s++)
        {
            bool last = s == _streams.Length - 1;
            int budget = (packet.Length - pos) / (_streams.Length - s);
            if (budget < 3) throw new ArgumentException("The packet buffer is too small.", nameof(packet));
            int n;
            if (last)
                n = _streams[s].Encode(_streamPcm[s], frameSize, packet.Slice(pos, Math.Min(packet.Length - pos, 1276)));
            else
            {
                n = _streams[s].EncodeSelfDelimited(_streamPcm[s], frameSize, _scratch, Math.Min(budget - 2, 1276));
                if (n < 0 || pos + n > packet.Length) throw new ArgumentException("The packet buffer is too small.", nameof(packet));
                _scratch.AsSpan(0, n).CopyTo(packet.Slice(pos));
            }
            pos += n;
            range ^= _streams[s].FinalRange;
        }
        _finalRange = range;
        return pos;
    }

    private int EncodeSingle(ReadOnlySpan<float> pcm, int frameSize, Span<byte> packet)
    {
        int n = _streams[0].Encode(pcm, frameSize, packet);
        _finalRange = _streams[0].FinalRange;
        return n;
    }
}
