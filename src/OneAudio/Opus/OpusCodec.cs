using System.Buffers.Binary;

namespace OneAudio.Opus;

/// <summary>The identification header of an Ogg Opus stream (RFC 7845 section 5.1).</summary>
internal sealed class OpusHead
{
    public int Channels;
    public int PreSkip;
    public int InputSampleRate;
    public int OutputGainQ8;
    public int Family;
    public int StreamCount;
    public int CoupledCount;
    public byte[] Mapping = [];

    public static bool IsHead(ReadOnlySpan<byte> data) => data.Length >= 8 && data[..8].SequenceEqual("OpusHead"u8);

    public static OpusHead Parse(ReadOnlySpan<byte> d, AudioLimits limits)
    {
        if (d.Length < 19 || !IsHead(d)) throw new AudioFormatException("Missing OpusHead header.");
        if ((d[8] >> 4) != 0) throw new UnsupportedAudioFormatException("Unsupported Ogg Opus version.");
        var h = new OpusHead
        {
            Channels = d[9],
            PreSkip = BinaryPrimitives.ReadUInt16LittleEndian(d.Slice(10)),
            InputSampleRate = (int)Math.Min(BinaryPrimitives.ReadUInt32LittleEndian(d.Slice(12)), int.MaxValue),
            OutputGainQ8 = BinaryPrimitives.ReadInt16LittleEndian(d.Slice(16)),
            Family = d[18],
        };
        if (h.Channels < 1) throw new AudioFormatException("Opus stream has no channels.");
        limits.CheckChannels(h.Channels);
        switch (h.Family)
        {
            case 0:
                if (h.Channels > 2) throw new AudioFormatException("Channel mapping family 0 allows one or two channels.");
                h.StreamCount = 1;
                h.CoupledCount = h.Channels - 1;
                h.Mapping = h.Channels == 1 ? [0] : [0, 1];
                break;
            case 1:
            case 255:
                if (h.Family == 1 && h.Channels > 8) throw new AudioFormatException("Channel mapping family 1 allows up to eight channels.");
                if (d.Length < 21 + h.Channels) throw new AudioFormatException("OpusHead channel mapping table is truncated.");
                h.StreamCount = d[19];
                h.CoupledCount = d[20];
                if (h.StreamCount < 1) throw new AudioFormatException("Opus stream count must be at least 1.");
                if (h.CoupledCount > h.StreamCount) throw new AudioFormatException("Coupled stream count exceeds the stream count.");
                limits.CheckChannels(h.StreamCount);
                h.Mapping = d.Slice(21, h.Channels).ToArray();
                foreach (byte m in h.Mapping)
                    if (m != 255 && m >= h.StreamCount + h.CoupledCount)
                        throw new AudioFormatException("Channel mapping entry refers to a missing stream.");
                break;
            default:
                throw new UnsupportedAudioFormatException($"Opus channel mapping family {h.Family} is not supported.");
        }
        return h;
    }

    public bool SameLayout(OpusHead o) =>
        Channels == o.Channels && Family == o.Family && StreamCount == o.StreamCount && CoupledCount == o.CoupledCount &&
        Mapping.AsSpan().SequenceEqual(o.Mapping);
}

internal sealed class OpusCodec : IAudioCodec
{
    public static readonly OpusCodec Instance = new();

    public CodecId Id => CodecId.Opus;

    public IAudioDecoder CreateDecoder(IPacketSource packets, AudioLimits limits)
    {
        ArgumentNullException.ThrowIfNull(packets);
        ArgumentNullException.ThrowIfNull(limits);
        try
        {
            return new OpusStreamDecoder(packets, limits);
        }
        catch
        {
            // The decoder owns the packet source only once it exists.
            packets.Dispose();
            throw;
        }
    }
}

/// <summary>Registration helper for Opus.</summary>
public static class OpusRegistryExtensions
{
    /// <summary>Registers the Opus decoder, which reads Ogg Opus streams (RFC 7845).</summary>
    /// <param name="registry">The registry.</param>
    /// <returns><paramref name="registry"/>, for chaining.</returns>
    public static AudioCodecRegistry AddOpus(this AudioCodecRegistry registry)
    {
        ArgumentNullException.ThrowIfNull(registry);
        return registry.Add(OpusCodec.Instance);
    }
}

/// <summary>
/// Decodes an Ogg Opus stream (RFC 7845) from an <see cref="IPacketSource"/>. Output is 48 kHz interleaved float, in
/// the standard WAV channel order for channel mapping family 1. The pre-skip is removed, the header's output gain
/// (Q7.8 dB) is applied, and the last packet is trimmed to the container's final position. A seek decodes and
/// discards 80 ms of audio before the target so the decoder state has converged (RFC 7845 section 4.6).
/// </summary>
internal sealed class OpusStreamDecoder : IAudioDecoder
{
    private const int MaxFrames = 5760; // 120 ms at 48 kHz
    private const int SeekPreRoll = 3840; // 80 ms at 48 kHz

    private readonly IPacketSource _src;
    private readonly AudioLimits _limits;
    private OpusHead _head;
    private OpusDecoder[] _streams;
    private readonly int _channels;
    private readonly int[] _chStream;   // output channel -> stream index, -1 for silent
    private readonly int[] _chInStream; // channel within the stream
    private readonly int[] _chSlot;     // output channel -> slot in the output frame (WAV order)
    private readonly float[] _streamBuf = new float[MaxFrames * 2];
    private readonly float[] _decoded;
    private float _gain;

    private int _bufPos;
    private int _bufCount;
    private long _granule;       // 48 kHz position at the end of the last decoded packet, counted from the link start
    private long _discardUntil;  // frames before this granule position are not delivered
    private bool _seeked;
    private long _seekStart;
    private long _position;
    private bool _eof;
    private bool _disposed;

    public OpusStreamDecoder(IPacketSource packets, AudioLimits limits)
    {
        _src = packets;
        _limits = limits;

        if (!packets.TryReadPacket(out var first) || !first.IsHeader) throw new AudioFormatException("Missing OpusHead header.");
        limits.CheckPacketBytes(first.Data.Length);
        _head = OpusHead.Parse(first.Data, limits);
        if (!packets.TryReadPacket(out var tags) || !tags.IsHeader || tags.Data.Length < 8 || !tags.Data[..8].SequenceEqual("OpusTags"u8))
            throw new AudioFormatException("Missing OpusTags header.");
        limits.CheckMetadataBytes(tags.Data.Length);

        _channels = _head.Channels;
        _chStream = new int[_channels];
        _chInStream = new int[_channels];
        _chSlot = new int[_channels];
        _decoded = new float[MaxFrames * _channels];

        var mask = _head.Family == 1 ? VorbisOrderMask(_channels) : null;
        for (int c = 0; c < _channels; c++)
        {
            int m = _head.Mapping[c];
            if (m == 255) { _chStream[c] = -1; }
            else if (m < 2 * _head.CoupledCount) { _chStream[c] = m / 2; _chInStream[c] = m & 1; }
            else { _chStream[c] = m - _head.CoupledCount; _chInStream[c] = 0; }

            _chSlot[c] = c;
            if (mask is not null)
            {
                // Vorbis channel order -> ascending speaker bit order, the order ChannelMask implies.
                int slot = 0;
                for (int o = 0; o < _channels; o++)
                    if (mask[o] < mask[c]) slot++;
                _chSlot[c] = slot;
            }
        }

        _streams = CreateStreams();
        SetupLink();

        ChannelMask cm = _head.Family == 1 ? AssembleMask(mask!) : (_channels <= 2 ? AudioFormat.DefaultMask(_channels) : ChannelMask.None);
        Format = new AudioFormat(48000, _channels, cm, NativeSampleType.Lossy, 0);
        long len = packets.LengthFrames;
        LengthFrames = len >= 0 ? Math.Max(0, len - _head.PreSkip) : -1;
    }

    public AudioFormat Format { get; }

    public long LengthFrames { get; }

    public long PositionFrames => _position;

    public bool CanSeek => _src.CanSeek;

    public bool HasNativeIntegers => false;

    private OpusDecoder[] CreateStreams()
    {
        var s = new OpusDecoder[_head.StreamCount];
        for (int i = 0; i < s.Length; i++) s[i] = new OpusDecoder(48000, i < _head.CoupledCount ? 2 : 1);
        return s;
    }

    private void SetupLink()
    {
        _granule = 0;
        _discardUntil = _head.PreSkip;
        // RFC 7845 section 5.2: gain is a Q7.8 dB value applied to the output.
        _gain = _head.OutputGainQ8 == 0 ? 1f : (float)Math.Pow(10.0, _head.OutputGainQ8 / (20.0 * 256.0));
        _bufPos = _bufCount = 0;
    }

    // Speaker bits in the Vorbis channel order of RFC 7845 section 5.1.1.2 / Vorbis I section 4.3.9.
    private static uint[] VorbisOrderMask(int n)
    {
        const uint fl = 0x1, fr = 0x2, fc = 0x4, lfe = 0x8, bl = 0x10, br = 0x20, bc = 0x100, sl = 0x200, sr = 0x400;
        return n switch
        {
            1 => [fc],
            2 => [fl, fr],
            3 => [fl, fc, fr],
            4 => [fl, fr, bl, br],
            5 => [fl, fc, fr, bl, br],
            6 => [fl, fc, fr, bl, br, lfe],
            7 => [fl, fc, fr, sl, sr, bc, lfe],
            _ => [fl, fc, fr, sl, sr, bl, br, lfe],
        };
    }

    private static ChannelMask AssembleMask(uint[] bits)
    {
        uint m = 0;
        foreach (uint b in bits) m |= b;
        return (ChannelMask)m;
    }

    public int Read(Span<float> destination)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        int want = destination.Length / _channels;
        int done = 0;
        while (done < want)
        {
            if (_bufPos >= _bufCount && !FillBuffer()) break;
            int n = Math.Min(want - done, _bufCount - _bufPos);
            _decoded.AsSpan(_bufPos * _channels, n * _channels).CopyTo(destination.Slice(done * _channels));
            _bufPos += n;
            done += n;
        }
        _position += done;
        return done;
    }

    public int ReadInteger(Span<int> destination) => throw new NotSupportedException("Opus is lossy; there are no native integer samples.");

    public void Seek(long frame)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        if (!_src.CanSeek) throw new NotSupportedException("The packet source is not seekable.");
        if (frame < 0 || (LengthFrames >= 0 && frame > LengthFrames)) throw new ArgumentOutOfRangeException(nameof(frame));

        long target = frame + _head.PreSkip;
        _src.SeekToFrame(Math.Max(0, target - SeekPreRoll), out long start);
        foreach (var s in _streams) s.Reset();
        _bufPos = _bufCount = 0;
        _granule = 0;
        _discardUntil = target;
        // A start of 0 means the first packet is decoded from the stream start; otherwise the container reports the
        // position at the end of the first packet it returns, which we decode only to prime the decoder.
        _seeked = start > 0;
        _seekStart = start;
        _position = frame;
        _eof = false;
    }

    // Decodes packets until one yields audio to deliver. False at the end of the stream.
    private bool FillBuffer()
    {
        while (!_eof)
        {
            if (!_src.TryReadPacket(out var p)) { _eof = true; return false; }
            _limits.CheckPacketBytes(p.Data.Length);
            if (p.IsHeader)
            {
                if (OpusHead.IsHead(p.Data))
                {
                    // A chained stream starts a new link: restart pre-skip and decoder state.
                    var h = OpusHead.Parse(p.Data, _limits);
                    if (!h.SameLayout(_head)) throw new UnsupportedAudioFormatException("The channel layout changes within a chained Opus stream.");
                    _head = h;
                    foreach (var s in _streams) s.Reset();
                    SetupLink();
                    _seeked = false;
                }
                continue;
            }

            int n = DecodePacket(p.Data);
            long g1 = _granule + n;
            if (_seeked)
            {
                g1 = _seekStart;
                _seeked = false;
            }
            long g0 = g1 - n;
            int end = n;
            if (p.EndFrame >= 0 && p.EndFrame < g1)
            {
                // RFC 7845 section 4.4: the final page's position says how much of the last packet is real.
                end = (int)Math.Max(0, n - (g1 - p.EndFrame));
                g1 = p.EndFrame;
            }
            _granule = g1;
            long keepFrom = Math.Clamp(Math.Max(_head.PreSkip, _discardUntil) - g0, 0, n);
            if (keepFrom >= end) continue;
            _bufPos = (int)keepFrom;
            _bufCount = end;
            return true;
        }
        return false;
    }

    // Decodes one Ogg packet (all streams of a multistream packet) into _decoded, returns frames.
    private int DecodePacket(ReadOnlySpan<byte> data)
    {
        int n = -1;
        int pos = 0;
        int ch = _channels;
        int last = _streams.Length - 1;
        for (int s = 0; s <= last; s++)
        {
            var dec = _streams[s];
            int r = dec.Decode(data.Slice(pos), s != last, _streamBuf, out int used);
            pos += used;
            if (n < 0)
            {
                n = r;
                Array.Clear(_decoded, 0, n * ch);
            }
            else if (r != n)
            {
                throw new AudioFormatException("Streams of a multistream packet differ in duration.");
            }
            int sc = dec.Channels;
            for (int c = 0; c < ch; c++)
            {
                if (_chStream[c] != s) continue;
                int src = _chInStream[c], slot = _chSlot[c];
                for (int i = 0; i < n; i++) _decoded[i * ch + slot] = _streamBuf[i * sc + src];
            }
        }
        if (_gain != 1f)
        {
            var span = _decoded.AsSpan(0, n * ch);
            for (int i = 0; i < span.Length; i++) span[i] *= _gain;
        }
        return n;
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        _src.Dispose();
    }
}
