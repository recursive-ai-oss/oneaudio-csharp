using OneAudio.Mp4;

namespace OneAudio.Aac;

/// <summary>
/// Raw AAC streams in ADTS framing (ISO/IEC 14496-3 1.A.2, ISO/IEC 13818-7 6.2): files such as <c>.aac</c>, and HTTP radio
/// captures. Each frame is one packet, delivered without its ADTS header, and the container synthesises the two-byte
/// AudioSpecificConfig from the header fields so the codec sees the same inputs as for AAC in MP4.
/// </summary>
/// <remarks>
/// <para>A leading ID3v2 tag is skipped. The first frame fixes the profile, sampling rate and channel configuration; a later frame
/// that changes any of them ends the stream (as a format change ends a chained Ogg stream). Frames with more than one raw data
/// block (<c>number_of_raw_data_blocks_in_frame</c> above 0) and channel configuration 0 (a layout carried in the stream itself)
/// are rejected with <see cref="UnsupportedAudioFormatException"/>.</para>
/// <para>Bytes that are not an ADTS header end the stream when at most 8192 bytes remain (a trailing tag or a cut-off frame) and
/// are an <see cref="AudioFormatException"/> otherwise. On a seekable stream the file is scanned once at open, jumping from header to
/// header, which gives <see cref="LengthFrames"/> and a sparse index for seeking; a forward-only stream reports -1 and cannot seek.</para>
/// <para>Frames are 1024 samples at <see cref="SampleRate"/> (the core rate: implicit SBR is not visible to the container).
/// <see cref="PacketView.Timestamp"/> counts those samples.</para>
/// </remarks>
public sealed class AdtsPacketSource : IPacketSource
{
    private const int FrameSamples = 1024;
    private const int IndexStride = 64;
    private const int TailBytes = 8192;

    private readonly Stream _stream;
    private readonly AudioLimits _limits;
    private bool _leaveOpen;
    private bool _disposed;
    private readonly bool _seekable;
    private readonly long _length;
    private readonly long _firstFrame;
    private readonly List<long> _index = [];
    private readonly byte[] _config = new byte[2];
    private readonly int _profileRateChannels;
    private readonly byte[] _buffer = new byte[8192]; // aac_frame_length has 13 bits
    private bool _ended;
    private long _frames = -1;
    private long _next;
    private long _pos;

    private AdtsPacketSource(Stream stream, AudioLimits limits, bool leaveOpen, CancellationToken ct)
    {
        _stream = stream;
        _limits = limits;
        _leaveOpen = leaveOpen;
        _seekable = stream.CanSeek;
        _length = _seekable ? stream.Length : -1;
        _pos = _seekable ? stream.Position : 0;

        SkipId3();
        _firstFrame = _pos;
        var first = ReadHeader(allowEndAsMissing: false);
        if (first.Blocks != 0) throw new UnsupportedAudioFormatException("ADTS frames with several raw data blocks are not supported.");
        if (first.ChannelConfig == 0) throw new UnsupportedAudioFormatException("ADTS streams that carry their channel layout in-band (channel configuration 0) are not supported.");
        SampleRate = first.SampleRate;
        Channels = first.ChannelConfig == 7 ? 8 : first.ChannelConfig;
        limits.CheckSampleRate(SampleRate);
        limits.CheckChannels(Channels);
        // AudioSpecificConfig: audioObjectType(5) samplingFrequencyIndex(4) channelConfiguration(4) GASpecificConfig(3 zero bits).
        int aot = first.Profile + 1;
        int bits = (aot << 11) | (first.RateIndex << 7) | (first.ChannelConfig << 3);
        _config[0] = (byte)(bits >> 8);
        _config[1] = (byte)bits;
        AudioObjectType = aot;
        _profileRateChannels = first.Key;

        if (_seekable)
        {
            Scan(ct);
            Rewind();
        }
        else
        {
            // The header was consumed; the payload is read as part of the first packet.
            _pos = _firstFrame + 7;
            _pendingHeader = first;
        }
    }

    private AdtsHeader? _pendingHeader;

    /// <summary>
    /// Opens a raw ADTS stream.
    /// </summary>
    /// <param name="stream">The stream, positioned at the start (an ID3v2 tag may come first).</param>
    /// <param name="limits">Limits for untrusted input; <see cref="AudioLimits.Default"/> when null.</param>
    /// <param name="leaveOpen">Keep the stream open when the source is disposed.</param>
    /// <param name="cancellationToken">Observed while scanning a seekable file.</param>
    /// <returns>A source positioned at the first frame.</returns>
    /// <exception cref="AudioFormatException">No valid ADTS header where one is expected.</exception>
    /// <exception cref="UnsupportedAudioFormatException">A feature of ADTS this library does not read.</exception>
    public static AdtsPacketSource Open(Stream stream, AudioLimits? limits = null, bool leaveOpen = false, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(stream);
        if (!stream.CanRead) throw new ArgumentException("Stream must be readable.", nameof(stream));
        AdtsPacketSource? src = null;
        try
        {
            return src = new AdtsPacketSource(stream, limits ?? AudioLimits.Default, leaveOpen, cancellationToken);
        }
        catch
        {
            // The caller keeps the stream when opening fails.
            src?.Abandon();
            throw;
        }
    }

    /// <inheritdoc />
    public CodecId Codec => CodecId.Aac;

    /// <inheritdoc />
    public long LengthFrames => _frames < 0 ? -1 : _frames * FrameSamples;

    /// <inheritdoc />
    public bool CanSeek => _seekable;

    /// <summary>The two-byte AudioSpecificConfig built from the ADTS header.</summary>
    public ReadOnlyMemory<byte> CodecConfig => _config;

    /// <inheritdoc />
    public int SampleRate { get; }

    /// <inheritdoc />
    public int Channels { get; }

    /// <inheritdoc />
    public int TimeScale => SampleRate;

    /// <summary>The audio object type: the ADTS profile plus one (2 = AAC-LC).</summary>
    public int AudioObjectType { get; }

    /// <summary>Number of frames in the stream, or -1 on a forward-only stream.</summary>
    public long FrameCount => _frames;

    /// <inheritdoc />
    public bool TryReadPacket(out PacketView packet)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        packet = default;
        if (_ended) return false;
        var h = _pendingHeader ?? ReadHeader(allowEndAsMissing: true);
        _pendingHeader = null;
        if (!h.Valid || h.Key != _profileRateChannels || h.Blocks != 0)
        {
            _ended = true;
            return false;
        }
        int rest = h.FrameLength - 7;
        int got = ReadBytes(_buffer.AsSpan(0, rest));
        _pos += got;
        if (got < rest)
        {
            _ended = true; // cut-off last frame
            return false;
        }
        int skip = h.Crc ? 2 : 0;
        long index = _next++;
        packet = new PacketView(_buffer.AsSpan(skip, rest - skip), (index + 1) * FrameSamples, false, index * FrameSamples, FrameSamples);
        return true;
    }

    /// <summary>Positions on the frame containing <paramref name="frame"/> (1024 samples per frame).</summary>
    /// <param name="frame">Target frame, 0 or more; at or past the end positions at the end.</param>
    /// <param name="startFrame">The first sample of the frame that will be returned.</param>
    /// <exception cref="NotSupportedException">The stream is not seekable.</exception>
    /// <exception cref="ArgumentOutOfRangeException"><paramref name="frame"/> is negative.</exception>
    public void SeekToFrame(long frame, out long startFrame)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        if (!_seekable) throw new NotSupportedException("The stream is not seekable.");
        ArgumentOutOfRangeException.ThrowIfNegative(frame);
        long target = Math.Min(frame / FrameSamples, _frames);
        long block = Math.Min(target / IndexStride, _index.Count - 1);
        _ended = false;
        _stream.Position = _index[(int)block];
        _pos = _index[(int)block];
        _pendingHeader = null;
        for (long i = block * IndexStride; i < target; i++)
        {
            var h = ReadHeader(allowEndAsMissing: true);
            SkipPayload(h);
        }
        _next = target;
        startFrame = target * FrameSamples;
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

    // --- framing --------------------------------------------------------------------------------------------------

    private void Rewind()
    {
        _stream.Position = _firstFrame;
        _pos = _firstFrame;
        _next = 0;
        _pendingHeader = null;
        _ended = false;
    }

    private void Scan(CancellationToken ct)
    {
        long frames = 0;
        _pos = _firstFrame;
        _stream.Position = _firstFrame;
        while (true)
        {
            if ((frames & 0xFFF) == 0) ct.ThrowIfCancellationRequested();
            long at = _pos;
            var h = ReadHeader(allowEndAsMissing: true);
            if (!h.Valid || h.Key != _profileRateChannels || h.Blocks != 0) break;
            if (_length - (at + h.FrameLength) < 0) break; // cut-off last frame
            if (frames % IndexStride == 0) _index.Add(at);
            _limits.CheckSamples(++frames);
            SkipPayload(h);
        }
        _frames = frames;
        if (frames == 0) throw new AudioFormatException("The stream holds no complete ADTS frame.");
    }

    private void SkipPayload(AdtsHeader h)
    {
        if (!h.Valid) return;
        _pos += h.FrameLength - 7;
        _stream.Position = _pos;
    }

    private void SkipId3()
    {
        Span<byte> b = stackalloc byte[10];
        int n = _stream.ReadAtLeast(b, 10, throwOnEndOfStream: false);
        if (n == 10 && b[0] == 'I' && b[1] == 'D' && b[2] == '3' && b[3] != 0xFF && b[4] != 0xFF
            && ((b[6] | b[7] | b[8] | b[9]) & 0x80) == 0)
        {
            long size = (b[6] << 21) | (b[7] << 14) | (b[8] << 7) | b[9];
            if ((b[5] & 0x10) != 0) size += 10; // footer
            _pos += 10 + size;
            if (_seekable) _stream.Position = _pos;
            else
            {
                Span<byte> scratch = stackalloc byte[512];
                for (long left = size; left > 0;)
                {
                    int got = _stream.Read(scratch[..(int)Math.Min(left, scratch.Length)]);
                    if (got <= 0) throw new AudioFormatException("The file ends inside its ID3v2 tag.");
                    left -= got;
                }
            }
            return;
        }
        // No tag: the bytes read belong to the first frame.
        if (_seekable) _stream.Position = _pos;
        else _replay = b[..n].ToArray();
    }

    private byte[]? _replay;
    private int _replayPos;

    // Reads from the bytes taken for tag detection first, then from the stream.
    private int ReadBytes(Span<byte> destination)
    {
        int total = 0;
        if (_replay is { } replay)
        {
            int n = Math.Min(destination.Length, replay.Length - _replayPos);
            replay.AsSpan(_replayPos, n).CopyTo(destination);
            _replayPos += n;
            total = n;
            if (_replayPos >= replay.Length) _replay = null;
        }
        if (total < destination.Length)
            total += _stream.ReadAtLeast(destination[total..], destination.Length - total, throwOnEndOfStream: false);
        return total;
    }

    /// <summary>
    /// Reads the next header at the current position. Returns an invalid header (Valid false) at the end of the stream; throws when
    /// bytes that are not a header are followed by more than <see cref="TailBytes"/> bytes (so they are not just a trailer).
    /// </summary>
    private AdtsHeader ReadHeader(bool allowEndAsMissing)
    {
        Span<byte> b = stackalloc byte[7];
        int n = ReadBytes(b);
        _pos += n;
        if (n == 0 && allowEndAsMissing) return default;
        if (n < 7)
        {
            if (allowEndAsMissing) return default;
            throw new AudioFormatException("The stream is too short to hold an ADTS header.");
        }
        if (AdtsHeader.TryParse(b, out var h)) return h;
        if (allowEndAsMissing && IsTail()) return default;
        throw new AudioFormatException("Expected an ADTS frame header and found something else.");
    }

    // True when no more than TailBytes bytes follow the current position.
    private bool IsTail()
    {
        if (_seekable) return _length - _pos <= TailBytes;
        Span<byte> scratch = stackalloc byte[512];
        int total = 0;
        while (total <= TailBytes)
        {
            int got = _stream.Read(scratch);
            if (got <= 0) return true;
            total += got;
        }
        return false;
    }

    internal readonly struct AdtsHeader
    {
        public bool Valid { get; init; }
        public int Profile { get; init; }
        public int RateIndex { get; init; }
        public int SampleRate { get; init; }
        public int ChannelConfig { get; init; }
        public int FrameLength { get; init; }
        public int Blocks { get; init; }
        public bool Crc { get; init; }

        /// <summary>Profile, rate and channel configuration packed together; frames of one stream share it.</summary>
        public int Key => (Profile << 8) | (RateIndex << 4) | ChannelConfig;

        // adts_fixed_header: syncword(12) ID(1) layer(2) protection_absent(1) profile(2) sampling_frequency_index(4) private_bit(1)
        // channel_configuration(3) original/copy(1) home(1); adts_variable_header: copyright id bit(1) start(1)
        // aac_frame_length(13) adts_buffer_fullness(11) number_of_raw_data_blocks_in_frame(2).
        public static bool TryParse(ReadOnlySpan<byte> b, out AdtsHeader h)
        {
            h = default;
            if (b.Length < 7 || b[0] != 0xFF || (b[1] & 0xF6) != 0xF0) return false; // sync 0xFFF and layer 00
            int rateIndex = (b[2] >> 2) & 0xF;
            if (rateIndex >= 13) return false;
            bool crc = (b[1] & 1) == 0;
            int length = ((b[3] & 3) << 11) | (b[4] << 3) | (b[5] >> 5);
            if (length < (crc ? 9 : 7)) return false;
            h = new AdtsHeader
            {
                Valid = true,
                Profile = b[2] >> 6,
                RateIndex = rateIndex,
                SampleRate = AacConfigInfo.RateFromIndex(rateIndex),
                ChannelConfig = ((b[2] & 1) << 2) | (b[3] >> 6),
                FrameLength = length,
                Blocks = b[6] & 3,
                Crc = crc,
            };
            return true;
        }
    }
}
