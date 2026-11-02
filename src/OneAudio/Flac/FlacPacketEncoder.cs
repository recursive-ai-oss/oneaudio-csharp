using System.Buffers.Binary;

namespace OneAudio.Flac;

/// <summary>One encoded FLAC frame (RFC 9639 section 9) and where it sits in the stream.</summary>
/// <param name="Data">The complete frame, header through CRC-16, ready to be a sample of an MP4 <c>fLaC</c> track or an Ogg FLAC packet. Valid until the next call on the encoder that produced it.</param>
/// <param name="FirstSample">Index of the frame's first sample (per channel) in the stream, starting at 0.</param>
/// <param name="Samples">Samples per channel in the frame: the encoder's block size, less for the last frame.</param>
/// <param name="Index">Zero-based frame number.</param>
public readonly record struct FlacPacket(ReadOnlyMemory<byte> Data, long FirstSample, int Samples, long Index);

/// <summary>
/// Packet-level FLAC encoder: PCM in, FLAC frames out, with the STREAMINFO block a container needs to describe them. Use it
/// to put FLAC into a container other than native FLAC (the <c>dfLa</c> box of MP4, an Ogg FLAC mapping) or to move frames
/// yourself. It runs the same analysis as <see cref="FlacEncoder"/>, so frames are identical to those of a native file made
/// with the same settings, and a stream of these frames decodes bit-exactly.
/// <para>
/// Usage mirrors the Opus packet encoder: call <see cref="Write(ReadOnlySpan{float})"/> or <see cref="WriteInteger"/> with
/// PCM; each call takes input only until one block is complete and returns how much it took; collect the frame with
/// <see cref="TryReadPacket"/>, then offer the rest of the input. Call <see cref="Finish"/> at the end to turn a partial
/// block into the last frame and to complete the MD5 signature.
/// </para>
/// <code>
/// while (input.Length > 0)
/// {
///     int taken = enc.Write(input);
///     input = input.Slice(taken * channels);
///     while (enc.TryReadPacket(out var p)) container.AddSample(p.Data.Span, p.FirstSample, p.Samples);
/// }
/// enc.Finish();
/// while (enc.TryReadPacket(out var p)) container.AddSample(p.Data.Span, p.FirstSample, p.Samples);
/// byte[] streamInfo = enc.GetStreamInfo();   // now holds the length, frame sizes and MD5
/// </code>
/// After construction nothing is allocated per frame. Output is deterministic for identical input and settings. The
/// settings that shape a file (<see cref="FlacEncoderSettings.SeekPoints"/>, <see cref="FlacEncoderSettings.Comments"/>,
/// <see cref="FlacEncoderSettings.TotalFrames"/> checking) have no effect here. Instances are not thread-safe.
/// </summary>
public sealed class FlacPacketEncoder : IDisposable
{
    private const int ChunkSamples = 4096;

    private readonly FlacFrameEncoder _frames;
    private readonly int[][] _block;
    private readonly int[] _chunk;
    private readonly byte[] _md5Buf;
    private readonly Md5 _md5 = new();
    private readonly int _bps, _channels, _rate, _blockSize, _bytesPerSample;
    private readonly bool _dither;
    private readonly long _declaredTotal;
    private uint _ditherState;

    private int _fill;
    private bool _pending, _finished, _disposed;
    private long _frameNumber, _totalFrames;
    private int _minFrameBytes = int.MaxValue, _maxFrameBytes;
    private long _pendingFirst;
    private int _pendingSamples;
    private readonly byte[] _md5Final = new byte[16];

    /// <summary>Creates an encoder.</summary>
    /// <param name="format">Sample rate (1 to 1048575 Hz), channel count (1 to 8) and layout of the input. Its native type only supplies the default bit depth.</param>
    /// <param name="settings">Encoder options; defaults when null. The block size, compression level, bit depth, dither and subset rules apply as for <see cref="FlacEncoder"/>.</param>
    /// <exception cref="ArgumentException">A setting or format value is out of range.</exception>
    public FlacPacketEncoder(AudioFormat format, FlacEncoderSettings? settings = null)
    {
        settings ??= new FlacEncoderSettings();
        var plan = FlacEncoder.BuildPlan(format, settings);
        _bps = plan.BitsPerSample;
        _channels = plan.Channels;
        _rate = plan.SampleRate;
        _blockSize = plan.BlockSize;
        _bytesPerSample = (_bps + 7) / 8;
        _dither = settings.Dither;
        _ditherState = settings.DitherSeed;
        _declaredTotal = settings.TotalFrames;
        Format = new AudioFormat(_rate, _channels, format.ChannelMask, _bytesPerSample switch { 1 => NativeSampleType.PcmUnsigned8, 2 => NativeSampleType.PcmSigned16, 3 => NativeSampleType.PcmSigned24, _ => NativeSampleType.PcmSigned32 }, _bytesPerSample * 8);
        _frames = new FlacFrameEncoder(plan);
        _block = new int[_channels][];
        for (int c = 0; c < _channels; c++) _block[c] = new int[_blockSize];
        int chunk = ChunkSamples / _channels * _channels;
        _chunk = new int[chunk];
        _md5Buf = new byte[chunk * _bytesPerSample];
    }

    /// <summary>The input format: the given rate, channels and layout, with the native type set to the container width of <see cref="BitsPerSample"/>.</summary>
    public AudioFormat Format { get; }

    /// <summary>Bits per sample of the stream (the setting, else the format's native depth, else 16).</summary>
    public int BitsPerSample => _bps;

    /// <summary>Samples per channel in every frame but the last.</summary>
    public int BlockSize => _blockSize;

    /// <summary>Samples per channel accepted so far.</summary>
    public long FramesWritten => _totalFrames + _fill;

    /// <summary>
    /// Encodes interleaved <see cref="float"/> frames: each sample is multiplied by 2^(bits-1), rounded to nearest (ties to
    /// even), clipped to the signed range of <see cref="BitsPerSample"/> and, if dither is on, dithered first; NaN becomes 0.
    /// </summary>
    /// <param name="samples">Interleaved samples; the length must be a multiple of the channel count.</param>
    /// <returns>Frames (samples per channel) taken. It is less than the input holds when a block completed; collect it with <see cref="TryReadPacket"/> and offer the rest. 0 while an earlier packet is unread.</returns>
    /// <exception cref="ArgumentException">The length is not a whole number of frames.</exception>
    /// <exception cref="InvalidOperationException">The encoder is finished.</exception>
    public int Write(ReadOnlySpan<float> samples)
    {
        Check(samples.Length, nameof(samples));
        if (_pending || samples.Length == 0) return 0;
        int frames = Math.Min(samples.Length / _channels, _blockSize - _fill);
        int count = frames * _channels;
        for (int o = 0; o < count; o += _chunk.Length)
        {
            int take = Math.Min(_chunk.Length, count - o);
            var dst = _chunk.AsSpan(0, take);
            if (_dither) Kernels.SampleQuantize.ToIntegerDithered(samples.Slice(o, take), dst, _bps, ref _ditherState);
            else Kernels.SampleQuantize.ToInteger(samples.Slice(o, take), dst, _bps);
            Push(dst);
        }
        return frames;
    }

    /// <summary>Encodes interleaved integer frames exactly. Values must lie in the signed range of <see cref="BitsPerSample"/>.</summary>
    /// <param name="samples">Interleaved samples; the length must be a multiple of the channel count.</param>
    /// <returns>Frames taken; see <see cref="Write(ReadOnlySpan{float})"/>.</returns>
    /// <exception cref="ArgumentException">The length is not a whole number of frames.</exception>
    /// <exception cref="ArgumentOutOfRangeException">A sample does not fit <see cref="BitsPerSample"/> bits.</exception>
    /// <exception cref="InvalidOperationException">The encoder is finished.</exception>
    public int WriteInteger(ReadOnlySpan<int> samples)
    {
        Check(samples.Length, nameof(samples));
        if (_pending || samples.Length == 0) return 0;
        int frames = Math.Min(samples.Length / _channels, _blockSize - _fill);
        int count = frames * _channels;
        int lo = _bps == 32 ? int.MinValue : -(1 << (_bps - 1));
        int hi = _bps == 32 ? int.MaxValue : (1 << (_bps - 1)) - 1;
        for (int i = 0; i < count; i++)
            if (samples[i] < lo || samples[i] > hi) throw new ArgumentOutOfRangeException(nameof(samples), $"A sample does not fit {_bps} bits.");
        for (int o = 0; o < count; o += _chunk.Length)
            Push(samples.Slice(o, Math.Min(_chunk.Length, count - o)));
        return frames;
    }

    private void Check(int length, string paramName)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        if (_finished) throw new InvalidOperationException("The encoder is finished.");
        if (length % _channels != 0) throw new ArgumentException("The sample count must be a multiple of the channel count.", paramName);
    }

    private void Push(ReadOnlySpan<int> ints)
    {
        // MD5 covers the samples as signed little-endian integers of the smallest whole byte width (RFC 9639 section 8.2).
        var buf = _md5Buf;
        int k = 0;
        switch (_bytesPerSample)
        {
            case 1:
                foreach (int v in ints) buf[k++] = (byte)v;
                break;
            case 2:
                foreach (int v in ints) { buf[k] = (byte)v; buf[k + 1] = (byte)(v >> 8); k += 2; }
                break;
            case 3:
                foreach (int v in ints) { buf[k] = (byte)v; buf[k + 1] = (byte)(v >> 8); buf[k + 2] = (byte)(v >> 16); k += 3; }
                break;
            default:
                foreach (int v in ints) { buf[k] = (byte)v; buf[k + 1] = (byte)(v >> 8); buf[k + 2] = (byte)(v >> 16); buf[k + 3] = (byte)(v >> 24); k += 4; }
                break;
        }
        _md5.Append(buf.AsSpan(0, k));

        int frames = ints.Length / _channels;
        for (int c = 0; c < _channels; c++)
        {
            var dst = _block[c];
            int src = c;
            for (int f = 0; f < frames; f++, src += _channels) dst[_fill + f] = ints[src];
        }
        _fill += frames;
        if (_fill == _blockSize) EmitBlock();
    }

    private void EmitBlock()
    {
        int n = _fill;
        _frames.Encode(_block, n, _frameNumber);
        int size = _frames.Frame.Length;
        _minFrameBytes = Math.Min(_minFrameBytes, size);
        _maxFrameBytes = Math.Max(_maxFrameBytes, size);
        _pendingFirst = _totalFrames;
        _pendingSamples = n;
        _totalFrames += n;
        _frameNumber++;
        _fill = 0;
        _pending = true;
    }

    /// <summary>Takes the frame completed by the last <see cref="Write(ReadOnlySpan{float})"/> or <see cref="Finish"/>.</summary>
    /// <param name="packet">The frame; its data stays valid until the next call that encodes.</param>
    /// <returns>False when no frame is waiting.</returns>
    public bool TryReadPacket(out FlacPacket packet)
    {
        if (!_pending)
        {
            packet = default;
            return false;
        }
        packet = new FlacPacket(_frames.FrameMemory, _pendingFirst, _pendingSamples, _frameNumber - 1);
        _pending = false;
        return true;
    }

    /// <summary>
    /// Ends the stream: a partial block becomes the last frame (collect it with <see cref="TryReadPacket"/>) and the MD5
    /// signature is completed. Does nothing when called again. An unread packet must be taken before calling this.
    /// </summary>
    /// <exception cref="InvalidOperationException">A packet is still waiting to be read.</exception>
    public void Finish()
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        if (_finished) return;
        if (_pending) throw new InvalidOperationException("Read the pending packet before finishing.");
        if (_fill > 0) EmitBlock();
        _finished = true;
        _md5.GetHashAndReset(_md5Final);
    }

    /// <summary>
    /// The 34-byte STREAMINFO block body (RFC 9639 section 8.2), without the 4-byte metadata block header. Before
    /// <see cref="Finish"/> it carries the declared length (<see cref="FlacEncoderSettings.TotalFrames"/>, else 0 for unknown), no
    /// MD5 (zeros mean "not computed") and no frame-size bounds; after it, the real length, frame sizes and MD5 of the PCM.
    /// A new array is returned each call.
    /// </summary>
    public byte[] GetStreamInfo()
    {
        var d = new byte[34];
        BinaryPrimitives.WriteUInt16BigEndian(d, (ushort)_blockSize);
        BinaryPrimitives.WriteUInt16BigEndian(d.AsSpan(2), (ushort)_blockSize);
        long total = _finished ? _totalFrames : Math.Max(0, _declaredTotal);
        if (_finished && _frameNumber > 0)
        {
            WriteUInt24(d.AsSpan(4), _minFrameBytes);
            WriteUInt24(d.AsSpan(7), _maxFrameBytes);
        }
        ulong packed = ((ulong)_rate << 44) | ((ulong)(_channels - 1) << 41) | ((ulong)(_bps - 1) << 36) | ((ulong)total & 0xF_FFFF_FFFFUL);
        BinaryPrimitives.WriteUInt64BigEndian(d.AsSpan(10), packed);
        if (_finished) _md5Final.CopyTo(d, 18);
        return d;
    }

    /// <summary>
    /// The metadata block a container carries to describe the stream: a 4-byte block header (type 0, last-block flag set, length 34)
    /// followed by <see cref="GetStreamInfo"/>. This is the payload of an MP4 <c>dfLa</c> box after its 4-byte version and flags,
    /// and what follows the <c>fLaC</c> marker in a native file.
    /// </summary>
    public byte[] GetStreamInfoBlock()
    {
        var d = new byte[38];
        d[0] = 0x80;
        d[3] = 34;
        GetStreamInfo().CopyTo(d, 4);
        return d;
    }

    /// <summary>
    /// The first packet of an Ogg FLAC stream (the mapping's identification header: <c>0x7F "FLAC"</c>, version 1.0, a header packet
    /// count of 0 for the metadata packets that follow, <c>fLaC</c> and the STREAMINFO block). Ogg FLAC streams are normally written with
    /// the length and MD5 still unknown; call this before <see cref="Finish"/> for that, or after it to carry the final values.
    /// </summary>
    public byte[] GetOggIdentificationPacket()
    {
        var d = new byte[13 + 38];
        d[0] = 0x7F;
        "FLAC"u8.CopyTo(d.AsSpan(1));
        d[5] = 1; // major version
        // d[6] minor version 0; d[7..9] number of header packets after this one: 0
        "fLaC"u8.CopyTo(d.AsSpan(9));
        GetStreamInfoBlock().CopyTo(d, 13);
        return d;
    }

    private static void WriteUInt24(Span<byte> d, int v)
    {
        d[0] = (byte)(v >> 16);
        d[1] = (byte)(v >> 8);
        d[2] = (byte)v;
    }

    /// <summary>Releases the encoder. It is unusable afterwards.</summary>
    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
    }
}
