using System.Buffers.Binary;
using System.Security.Cryptography;
using System.Text;
using OneAudio.Kernels;

namespace OneAudio.Flac;

/// <summary>
/// Streaming FLAC encoder (RFC 9639). Feed interleaved frames with <see cref="Write"/> or <see cref="WriteInteger"/>,
/// then call <see cref="Finish"/>. Frames are encoded and written as soon as a block is full; after the first call
/// nothing is allocated per write. The output is deterministic: the same input and settings give the same bytes on a
/// given platform (the encoder's floating-point analysis uses no fused operations, so the hardware path does not matter).
/// <para>
/// On a seekable stream the encoder reserves STREAMINFO, an optional SEEKTABLE and a VORBIS_COMMENT block first and
/// rewrites them in <see cref="Finish"/> with the true length, MD5 signature, frame sizes and seek points; the stream's
/// position afterwards is the end of the file. On a non-seekable stream STREAMINFO is written once, up front: it has the
/// length from <see cref="FlacEncoderSettings.TotalFrames"/> (0 when unknown), no MD5 signature (all zeros, which means
/// "not computed") and no frame-size bounds, and no seek table is written.
/// </para>
/// <para>
/// With more than one channel, two-channel input may be stored as left/side, right/side or mid/side per frame. For 32-bit
/// samples no decorrelation is used (the side channel would need 33 bits). Ogg encapsulation is not provided.
/// </para>
/// Instances are not thread-safe.
/// </summary>
public sealed class FlacEncoder : IDisposable
{
    private const int ChunkSamples = 4096;

    private readonly Stream _out;
    private readonly bool _leaveOpen, _seekable;
    private readonly long _origin;
    private readonly int _bps, _channels, _rate, _bytesPerSample;
    private readonly long _declaredTotal;
    private readonly bool _dither;
    private uint _ditherState;
    private readonly FlacFrameEncoder _frames;
    private readonly int[][] _block;
    private readonly int _blockSize;
    private readonly int[] _chunk;
    private readonly byte[] _md5Buf;
    private readonly IncrementalHash _md5 = IncrementalHash.CreateHash(HashAlgorithmName.MD5);
    private readonly long _seekTableOffset; // from _origin; -1 when there is none
    private readonly int _seekCapacity;
    private readonly long[] _seekSample, _seekByte;
    private readonly int[] _seekCount;
    private int _seekEntries;
    private long _seekInterval, _seekNext;

    private int _fill;
    private long _frameNumber, _totalFrames, _bytesOfFrames;
    private int _minFrameBytes = int.MaxValue, _maxFrameBytes;
    
    private bool _finished, _failed, _disposed;

    /// <summary>Creates an encoder writing to <paramref name="output"/> and writes the stream header.</summary>
    /// <param name="output">A writable stream. A seekable stream gets the header patched at <see cref="Finish"/>; encoding starts at its current position.</param>
    /// <param name="format">Sample rate (1 to 1048575 Hz), channel count (1 to 8) and optional channel layout of the input. Its native type only supplies the default bit depth.</param>
    /// <param name="settings">Encoder options; defaults when null.</param>
    /// <param name="leaveOpen">Keep <paramref name="output"/> open on dispose (default false).</param>
    /// <exception cref="ArgumentException">A setting or format value is out of range, or the settings break the streamable subset while <see cref="FlacEncoderSettings.Subset"/> is true.</exception>
    public FlacEncoder(Stream output, AudioFormat format, FlacEncoderSettings? settings = null, bool leaveOpen = false)
    {
        ArgumentNullException.ThrowIfNull(output);
        if (!output.CanWrite) throw new ArgumentException("Stream must be writable.", nameof(output));
        settings ??= new FlacEncoderSettings();
        var plan = BuildPlan(format, settings);
        _out = output;
        _leaveOpen = leaveOpen;
        _seekable = output.CanSeek;
        _origin = _seekable ? output.Position : 0;
        _bps = plan.BitsPerSample;
        _channels = plan.Channels;
        _rate = plan.SampleRate;
        _blockSize = plan.BlockSize;
        _bytesPerSample = (_bps + 7) / 8;
        _declaredTotal = settings.TotalFrames;
        _dither = settings.Dither;
        _ditherState = settings.DitherSeed;
        Format = new AudioFormat(_rate, _channels, format.ChannelMask, _bytesPerSample switch { 1 => NativeSampleType.PcmUnsigned8, 2 => NativeSampleType.PcmSigned16, 3 => NativeSampleType.PcmSigned24, _ => NativeSampleType.PcmSigned32 }, _bytesPerSample * 8);

        _frames = new FlacFrameEncoder(plan);
        _block = new int[_channels][];
        for (int c = 0; c < _channels; c++) _block[c] = new int[_blockSize];
        int chunk = ChunkSamples / _channels * _channels;
        _chunk = new int[chunk];
        _md5Buf = new byte[chunk * _bytesPerSample];

        _seekCapacity = _seekable ? settings.SeekPoints : 0;
        _seekSample = new long[_seekCapacity];
        _seekByte = new long[_seekCapacity];
        _seekCount = new int[_seekCapacity];
        _seekInterval = Math.Max(_blockSize, _rate);
        _seekNext = 0;

        // Header: marker, STREAMINFO, then VORBIS_COMMENT and (when reserved) SEEKTABLE as the last block.
        byte[] comment = BuildComment(settings, format, plan);
        int seekBytes = _seekCapacity * 18;
        var head = new byte[4 + 4 + 34 + 4 + comment.Length + (_seekCapacity > 0 ? 4 + seekBytes : 0)];
        "fLaC"u8.CopyTo(head);
        WriteBlockHeader(head.AsSpan(4), 0, 34, false);
        WriteStreamInfo(head.AsSpan(8, 34), declared: true);
        int pos = 8 + 34;
        WriteBlockHeader(head.AsSpan(pos), 4, comment.Length, _seekCapacity == 0);
        comment.CopyTo(head.AsSpan(pos + 4));
        pos += 4 + comment.Length;
        if (_seekCapacity > 0)
        {
            WriteBlockHeader(head.AsSpan(pos), 3, seekBytes, true);
            _seekTableOffset = pos + 4;
            for (int i = 0; i < _seekCapacity; i++) WritePlaceholder(head.AsSpan(pos + 4 + 18 * i, 18));
        }
        else _seekTableOffset = -1;
        _out.Write(head);
    }

    /// <summary>
    /// The format being encoded: the given sample rate, channels and mask, with the native type set to the container width
    /// of <see cref="BitsPerSample"/>. <see cref="WriteInteger"/> takes values in the range of <see cref="BitsPerSample"/>, not of the container.
    /// </summary>
    public AudioFormat Format { get; }

    /// <summary>The bit depth of the stream, 4 to 32.</summary>
    public int BitsPerSample => _bps;

    /// <summary>Frames accepted so far.</summary>
    public long FramesWritten => _totalFrames + _fill;

    /// <summary>
    /// Encodes interleaved <see cref="float"/> frames. Each sample is multiplied by 2^(bits-1), rounded to nearest (ties to even),
    /// clipped to the signed range of <see cref="BitsPerSample"/> and, if dither is on, dithered first; NaN becomes 0.
    /// </summary>
    /// <param name="samples">Interleaved samples; the length must be a multiple of the channel count.</param>
    /// <exception cref="ArgumentException">The length is not a whole number of frames.</exception>
    /// <exception cref="InvalidOperationException">The encoder was finished or an earlier write failed.</exception>
    public void Write(ReadOnlySpan<float> samples)
    {
        CheckWritable(samples.Length);
        for (int o = 0; o < samples.Length; o += _chunk.Length)
        {
            int take = Math.Min(_chunk.Length, samples.Length - o);
            var dst = _chunk.AsSpan(0, take);
            if (_dither) SampleQuantize.ToIntegerDithered(samples.Slice(o, take), dst, _bps, ref _ditherState);
            else SampleQuantize.ToInteger(samples.Slice(o, take), dst, _bps);
            Push(dst);
        }
    }

    /// <summary>Encodes interleaved integer frames exactly. Values must lie in the signed range of <see cref="BitsPerSample"/>.</summary>
    /// <param name="samples">Interleaved samples; the length must be a multiple of the channel count.</param>
    /// <exception cref="ArgumentException">The length is not a whole number of frames.</exception>
    /// <exception cref="ArgumentOutOfRangeException">A sample does not fit <see cref="BitsPerSample"/> bits.</exception>
    /// <exception cref="InvalidOperationException">The encoder was finished or an earlier write failed.</exception>
    public void WriteInteger(ReadOnlySpan<int> samples)
    {
        CheckWritable(samples.Length);
        int lo = _bps == 32 ? int.MinValue : -(1 << (_bps - 1));
        int hi = _bps == 32 ? int.MaxValue : (1 << (_bps - 1)) - 1;
        for (int o = 0; o < samples.Length; o += _chunk.Length)
        {
            var part = samples.Slice(o, Math.Min(_chunk.Length, samples.Length - o));
            foreach (int v in part)
                if (v < lo || v > hi) throw new ArgumentOutOfRangeException(nameof(samples), $"A sample does not fit {_bps} bits.");
            Push(part);
        }
    }

    private void CheckWritable(int length)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        if (_finished) throw new InvalidOperationException("The encoder is finished.");
        if (_failed) throw new InvalidOperationException("An earlier write failed; the output is incomplete.");
        if (length % _channels != 0) throw new ArgumentException("The sample count must be a multiple of the channel count.", nameof(length));
    }

    private void Push(ReadOnlySpan<int> ints)
    {
        // MD5 covers the samples as signed little-endian integers of the smallest whole byte width (RFC 9639 8.2).
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
        _md5.AppendData(buf.AsSpan(0, k));

        int frames = ints.Length / _channels;
        int pos = 0;
        while (frames > 0)
        {
            int take = Math.Min(_blockSize - _fill, frames);
            for (int c = 0; c < _channels; c++)
            {
                var dst = _block[c];
                int src = pos * _channels + c;
                for (int f = 0; f < take; f++, src += _channels) dst[_fill + f] = ints[src];
            }
            _fill += take;
            pos += take;
            frames -= take;
            if (_fill == _blockSize) EmitBlock();
        }
    }

    private void EmitBlock()
    {
        int n = _fill;
        _frames.Encode(_block, n, _frameNumber);
        var frame = _frames.Frame;
        RecordSeekPoint(_totalFrames, _bytesOfFrames, n);
        try { _out.Write(frame); }
        catch { _failed = true; throw; }
        _bytesOfFrames += frame.Length;
        _minFrameBytes = Math.Min(_minFrameBytes, frame.Length);
        _maxFrameBytes = Math.Max(_maxFrameBytes, frame.Length);
        _totalFrames += n;
        _frameNumber++;
        _fill = 0;
    }

    private void RecordSeekPoint(long sample, long byteOffset, int frameSamples)
    {
        if (_seekCapacity == 0 || sample < _seekNext) return;
        if (_seekEntries == _seekCapacity)
        {
            // Full: keep every second point and double the spacing, so the table always spans the whole stream.
            int kept = 0;
            for (int i = 0; i < _seekEntries; i += 2, kept++)
            {
                _seekSample[kept] = _seekSample[i];
                _seekByte[kept] = _seekByte[i];
                _seekCount[kept] = _seekCount[i];
            }
            _seekEntries = kept;
            _seekInterval *= 2;
            _seekNext = _seekSample[kept - 1] + _seekInterval;
            if (sample < _seekNext) return;
        }
        _seekSample[_seekEntries] = sample;
        _seekByte[_seekEntries] = byteOffset;
        _seekCount[_seekEntries] = frameSamples;
        _seekEntries++;
        _seekNext = sample + _seekInterval;
    }

    /// <summary>
    /// Encodes the last partial block and completes the stream. On a seekable output it then rewrites STREAMINFO (length, MD5,
    /// frame sizes) and the seek table and leaves the position at the end of the data. Calling it again does nothing.
    /// </summary>
    /// <exception cref="InvalidOperationException">The output is not seekable and the frame count differs from <see cref="FlacEncoderSettings.TotalFrames"/>, or an earlier write failed.</exception>
    public void Finish()
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        if (_finished) return;
        if (_failed) throw new InvalidOperationException("An earlier write failed; the output is incomplete.");
        if (_fill > 0) EmitBlock();
        _finished = true;
        if (_seekable)
        {
            long end = _out.Position;
            Span<byte> si = stackalloc byte[34];
            WriteStreamInfo(si, declared: false);
            _out.Position = _origin + 8;
            _out.Write(si);
            if (_seekTableOffset >= 0)
            {
                var table = new byte[_seekCapacity * 18];
                for (int i = 0; i < _seekCapacity; i++)
                {
                    var e = table.AsSpan(18 * i, 18);
                    if (i < _seekEntries)
                    {
                        BinaryPrimitives.WriteUInt64BigEndian(e, (ulong)_seekSample[i]);
                        BinaryPrimitives.WriteUInt64BigEndian(e[8..], (ulong)_seekByte[i]);
                        BinaryPrimitives.WriteUInt16BigEndian(e[16..], (ushort)_seekCount[i]);
                    }
                    else WritePlaceholder(e);
                }
                _out.Position = _origin + _seekTableOffset;
                _out.Write(table);
            }
            _out.Position = end;
        }
        else if (_declaredTotal >= 0 && _declaredTotal != _totalFrames)
            throw new InvalidOperationException($"TotalFrames was {_declaredTotal} but {_totalFrames} frames were written.");
        _out.Flush();
    }

    /// <summary>Finishes the stream if <see cref="Finish"/> has not been called (and no write failed), then closes the output unless it was left open.</summary>
    public void Dispose()
    {
        if (_disposed) return;
        try
        {
            if (!_finished && !_failed) Finish();
        }
        finally
        {
            _disposed = true;
            _md5.Dispose();
            if (!_leaveOpen) _out.Dispose();
        }
    }

    /// <summary>
    /// Encodes everything a decoder delivers. The sample rate, channel layout and (unless set in <paramref name="settings"/>) the bit
    /// depth come from the decoder; a lossless integer source at its own depth is copied exactly, anything else goes through float.
    /// </summary>
    /// <param name="source">The decoder, read from its current position to the end. It is not disposed.</param>
    /// <param name="output">Destination stream.</param>
    /// <param name="settings">Encoder options; defaults when null.</param>
    /// <param name="leaveOpen">Keep <paramref name="output"/> open (default false).</param>
    /// <param name="cancellationToken">Checked between reads.</param>
    /// <returns>The number of frames encoded.</returns>
    public static long Encode(IAudioDecoder source, Stream output, FlacEncoderSettings? settings = null, bool leaveOpen = false, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(source);
        settings ??= new FlacEncoderSettings();
        var fmt = source.Format;
        int nativeBits = source.HasNativeIntegers ? fmt.BitsPerSample : 0;
        bool exact = nativeBits != 0 && (settings.BitsPerSample == 0 || settings.BitsPerSample == nativeBits);
        if (settings.BitsPerSample == 0) settings = settings with { BitsPerSample = exact ? nativeBits : 16 };
        if (settings.TotalFrames < 0 && source.LengthFrames >= 0) settings = settings with { TotalFrames = source.LengthFrames - source.PositionFrames };
        using var enc = new FlacEncoder(output, fmt, settings, leaveOpen);
        int ch = fmt.Channels;
        if (exact)
        {
            var buf = new int[4096 * ch];
            int n;
            while ((n = source.ReadInteger(buf)) > 0)
            {
                cancellationToken.ThrowIfCancellationRequested();
                enc.WriteInteger(buf.AsSpan(0, n * ch));
            }
        }
        else
        {
            var buf = new float[4096 * ch];
            int n;
            while ((n = source.Read(buf)) > 0)
            {
                cancellationToken.ThrowIfCancellationRequested();
                enc.Write(buf.AsSpan(0, n * ch));
            }
        }
        enc.Finish();
        return enc.FramesWritten;
    }

    // ---- header assembly ------------------------------------------------------------------------------------------

    private static void WriteBlockHeader(Span<byte> d, int type, int length, bool last)
    {
        d[0] = (byte)(type | (last ? 0x80 : 0));
        d[1] = (byte)(length >> 16);
        d[2] = (byte)(length >> 8);
        d[3] = (byte)length;
    }

    private static void WritePlaceholder(Span<byte> e)
    {
        // RFC 9639 8.5: a placeholder point has sample number 0xFFFFFFFFFFFFFFFF.
        e.Clear();
        e[..8].Fill(0xFF);
    }

    // RFC 9639 8.2. When `declared`, the fields known only at the end are zero apart from the optional total.
    private void WriteStreamInfo(Span<byte> d, bool declared)
    {
        d.Clear();
        long total = declared ? Math.Max(0, _declaredTotal) : _totalFrames;
        // Every block but the last has the configured size. Streams shorter than one block still state that size (a block
        // size below 16 is not valid in STREAMINFO, RFC 9639 8.2), as the reference encoder does.
        int minBlock = _blockSize, maxBlock = _blockSize;
        BinaryPrimitives.WriteUInt16BigEndian(d, (ushort)minBlock);
        BinaryPrimitives.WriteUInt16BigEndian(d[2..], (ushort)maxBlock);
        if (!declared && _frameNumber > 0)
        {
            WriteUInt24(d[4..], _minFrameBytes);
            WriteUInt24(d[7..], _maxFrameBytes);
        }
        ulong packed = ((ulong)_rate << 44) | ((ulong)(_channels - 1) << 41) | ((ulong)(_bps - 1) << 36) | ((ulong)total & 0xF_FFFF_FFFFUL);
        BinaryPrimitives.WriteUInt64BigEndian(d[10..], packed);
        if (!declared) _md5.GetCurrentHash(d.Slice(18, 16));
    }

    private static void WriteUInt24(Span<byte> d, int v)
    {
        d[0] = (byte)(v >> 16);
        d[1] = (byte)(v >> 8);
        d[2] = (byte)v;
    }

    private static byte[] BuildComment(FlacEncoderSettings s, AudioFormat format, FlacEncoderPlan plan)
    {
        var entries = new List<byte[]>();
        if (s.Comments is not null)
            foreach (string c in s.Comments)
            {
                int eq = c.IndexOf('=');
                if (eq < 1) throw new ArgumentException("Each comment must look like NAME=value.", nameof(s));
                for (int i = 0; i < eq; i++)
                    if (c[i] < 0x20 || c[i] > 0x7D) throw new ArgumentException("Comment names use printable ASCII other than '='.", nameof(s));
                entries.Add(Encoding.UTF8.GetBytes(c));
            }
        if (format.ChannelMask != ChannelMask.None && format.ChannelMask != DefaultMaskFor(plan.Channels))
            entries.Add(Encoding.ASCII.GetBytes($"WAVEFORMATEXTENSIBLE_CHANNEL_MASK=0x{(uint)format.ChannelMask:X}"));
        byte[] vendor = Encoding.UTF8.GetBytes(s.Vendor ?? "RecursiveAI.OneAudio");
        long size = 8 + vendor.Length;
        foreach (var e in entries) size += 4 + e.Length;
        if (size > 0xFF_FFFF) throw new ArgumentException("The comments are too large for one metadata block.", nameof(s));
        var d = new byte[size];
        BinaryPrimitives.WriteInt32LittleEndian(d, vendor.Length);
        vendor.CopyTo(d, 4);
        int p = 4 + vendor.Length;
        BinaryPrimitives.WriteInt32LittleEndian(d.AsSpan(p), entries.Count);
        p += 4;
        foreach (var e in entries)
        {
            BinaryPrimitives.WriteInt32LittleEndian(d.AsSpan(p), e.Length);
            e.CopyTo(d, p + 4);
            p += 4 + e.Length;
        }
        return d;
    }

    // The layouts RFC 9639 9.1.3 gives each channel count.
    private static ChannelMask DefaultMaskFor(int channels) => channels switch
    {
        5 => ChannelMask.FrontLeft | ChannelMask.FrontRight | ChannelMask.FrontCenter | ChannelMask.BackLeft | ChannelMask.BackRight,
        7 => ChannelMask.FrontLeft | ChannelMask.FrontRight | ChannelMask.FrontCenter | ChannelMask.LowFrequency
             | ChannelMask.BackCenter | ChannelMask.SideLeft | ChannelMask.SideRight,
        _ => AudioFormat.DefaultMask(channels),
    };

    // ---- settings -> plan -------------------------------------------------------------------------------------------

    private static FlacEncoderPlan BuildPlan(AudioFormat format, FlacEncoderSettings s)
    {
        if (format.SampleRate < 1 || format.SampleRate > 1_048_575) throw new ArgumentException("Sample rate must be 1 to 1048575 Hz.", nameof(format));
        if (format.Channels < 1 || format.Channels > 8) throw new ArgumentException("FLAC holds 1 to 8 channels.", nameof(format));
        int bps = s.BitsPerSample;
        if (bps == 0)
            bps = format.NativeType is >= NativeSampleType.PcmUnsigned8 and <= NativeSampleType.PcmSigned32 ? format.BitsPerSample : 16;
        if (bps < 4 || bps > 32) throw new ArgumentException("BitsPerSample must be 4 to 32.", nameof(s));
        int level = s.CompressionLevel;
        if (level < 0 || level > 8) throw new ArgumentException("CompressionLevel must be 0 to 8.", nameof(s));
        if (s.DitherSeed == 0) throw new ArgumentException("DitherSeed must not be 0.", nameof(s));
        if (s.SeekPoints < 0 || s.SeekPoints > 1024) throw new ArgumentException("SeekPoints must be 0 to 1024.", nameof(s));
        if (s.TotalFrames > 0xF_FFFF_FFFFL) throw new ArgumentException("TotalFrames exceeds what STREAMINFO can hold.", nameof(s));

        var (block, lpc, stereo, orders, part) = level switch
        {
            0 => (1152, 0, 0, 1, 3),
            1 => (1152, 0, 1, 1, 3),
            2 => (1152, 0, 2, 1, 3),
            3 => (4096, 6, 0, 1, 4),
            4 => (4096, 8, 1, 1, 4),
            5 => (4096, 8, 2, 1, 5),
            6 => (4096, 8, 2, 1, 6),
            7 => (4096, 12, 2, 2, 6),
            _ => (4096, 12, 2, 3, 6),
        };
        var tukey = new WindowSpec(WindowSpec.Tukey, 0.5, 1, 0);
        WindowSpec[] windows = level switch
        {
            < 6 => [tukey],
            < 8 => [tukey, new WindowSpec(WindowSpec.PartialTukey, 0.2, 2, 0.1)],
            _ => [tukey, new WindowSpec(WindowSpec.PartialTukey, 0.2, 2, 0.1), new WindowSpec(WindowSpec.PunchoutTukey, 0.2, 3, 0.1)],
        };
        if (s.BlockSize != 0) block = s.BlockSize;
        if (s.MaxLpcOrder >= 0) lpc = s.MaxLpcOrder;
        if (s.MaxPartitionOrder >= 0) part = s.MaxPartitionOrder;
        if (block < 16 || block > 65535) throw new ArgumentException("BlockSize must be 16 to 65535.", nameof(s));
        if (lpc > 32) throw new ArgumentException("MaxLpcOrder must be 0 to 32.", nameof(s));
        if (part > 15) throw new ArgumentException("MaxPartitionOrder must be 0 to 15.", nameof(s));
        if (s.QlpPrecision != 0 && (s.QlpPrecision < 5 || s.QlpPrecision > 15)) throw new ArgumentException("QlpPrecision must be 5 to 15.", nameof(s));
        if (s.Subset)
        {
            bool low = format.SampleRate <= 48000;
            if (block > 16384 || (low && block > 4608)) throw new ArgumentException("BlockSize breaks the streamable subset (at most 16384, or 4608 up to 48 kHz).", nameof(s));
            if (low && lpc > 12) throw new ArgumentException("MaxLpcOrder breaks the streamable subset (at most 12 up to 48 kHz).", nameof(s));
            if (part > 8) throw new ArgumentException("MaxPartitionOrder breaks the streamable subset (at most 8).", nameof(s));
        }
        if (bps == 32 || format.Channels != 2) stereo = 0; // 32-bit side channels need 33 bits; only two channels are decorrelated
        return new FlacEncoderPlan
        {
            SampleRate = format.SampleRate,
            Channels = format.Channels,
            BitsPerSample = bps,
            BlockSize = block,
            MaxLpcOrder = lpc,
            MaxPartitionOrder = part,
            StereoMode = stereo,
            ExhaustiveOrders = level >= 7,
            OrdersPerWindow = orders,
            QlpPrecision = s.QlpPrecision,
            Windows = lpc > 0 ? windows : [],
        };
    }
}
