using System.Buffers.Binary;
using System.Numerics;
using OneAudio.Kernels;

namespace OneAudio;

/// <summary>Options for <see cref="WavEncoder"/>.</summary>
public sealed record WavEncoderSettings
{
    /// <summary>
    /// The stored sample type: <see cref="NativeSampleType.PcmUnsigned8"/>, <see cref="NativeSampleType.PcmSigned16"/>,
    /// <see cref="NativeSampleType.PcmSigned24"/>, <see cref="NativeSampleType.PcmSigned32"/>, <see cref="NativeSampleType.IeeeFloat32"/>
    /// or <see cref="NativeSampleType.IeeeFloat64"/>. Null (default) uses the format's own native type, or 16-bit PCM when that is lossy.
    /// </summary>
    public NativeSampleType? SampleType { get; init; }

    /// <summary>
    /// Write an RF64 file (EBU Tech 3306) even when the data is small. Default false: a RIFF file that, on a seekable stream, is
    /// converted to RF64 at <see cref="WavEncoder.Finish"/> if the data turns out to exceed what RIFF can describe.
    /// </summary>
    public bool ForceRf64 { get; init; }

    /// <summary>
    /// Use WAVE_FORMAT_EXTENSIBLE: true always, false never, null (default) when Microsoft's rules call for it, that is when
    /// there are more than two channels or more than 16 bits per sample.
    /// </summary>
    public bool? Extensible { get; init; }

    /// <summary>Add triangular (TPDF) dither of one LSB when float input is rounded to an integer type. Default false.</summary>
    public bool Dither { get; init; }

    /// <summary>Seed of the dither generator; must not be 0. Default 1.</summary>
    public uint DitherSeed { get; init; } = 1;

    /// <summary>
    /// The number of frames the file will hold, or -1 if unknown (default). Only needed on a non-seekable output, where the header
    /// is written once at the start: with it the sizes are exact, without it they are the "unknown" value 0xFFFFFFFF, which readers
    /// take to mean "until the end of the stream". <see cref="WavEncoder.Finish"/> throws on a mismatch. Ignored on seekable output.
    /// </summary>
    public long TotalFrames { get; init; } = -1;
}

/// <summary>
/// Streaming RIFF/WAVE writer: integer PCM 8/16/24/32-bit and IEEE float 32/64-bit, with WAVE_FORMAT_EXTENSIBLE and a channel
/// mask when needed, and RF64 for data beyond 4 GiB. Float samples written to an integer type are scaled by 2^(bits-1), rounded to
/// nearest (ties to even), clipped to the type's range, optionally dithered, with NaN written as 0. Integer samples are written
/// exactly and must lie in the type's range (8-bit values are signed, -128 to 127, and stored offset by 128). Writing a float
/// type from integer samples is not supported; use <see cref="Write"/>.
/// <para>
/// On a seekable stream the sizes in the header are patched by <see cref="Finish"/>, and the stream's position afterwards is the end
/// of the file. The header then also holds a 28-byte JUNK chunk that becomes the RF64 <c>ds64</c> chunk if the data outgrows RIFF.
/// On a non-seekable stream nothing can be patched; see <see cref="WavEncoderSettings.TotalFrames"/>.
/// </para>
/// Instances are not thread-safe. Nothing is allocated per write after the first call.
/// </summary>
public sealed class WavEncoder : IDisposable
{
    private const int ChunkSamples = 4096;
    private static readonly byte[] ExtensibleGuidTail = [0x00, 0x00, 0x00, 0x00, 0x10, 0x00, 0x80, 0x00, 0x00, 0xAA, 0x00, 0x38, 0x9B, 0x71];

    private readonly Stream _out;
    private readonly bool _leaveOpen, _seekable, _rf64Header, _hasJunk;
    private readonly long _origin;
    private readonly NativeSampleType _type;
    private readonly int _channels, _bytesPerSample, _bits;
    private readonly bool _isFloat, _dither;
    private uint _ditherState;
    private readonly long _declaredFrames;
    private readonly int[] _ints;
    private readonly byte[] _packed;
    private readonly int _factOffset, _dataHeaderOffset, _headerLength;
    private long _dataBytes;
    private bool _finished, _failed, _disposed;

    /// <summary>Creates an encoder and writes the file header.</summary>
    /// <param name="output">A writable stream; the file starts at its current position.</param>
    /// <param name="format">Sample rate, channel count and channel layout. When the layout is <see cref="ChannelMask.None"/> the conventional one for the channel count is used (none for counts without one).</param>
    /// <param name="settings">Options; defaults when null.</param>
    /// <param name="leaveOpen">Keep <paramref name="output"/> open on dispose (default false).</param>
    /// <exception cref="ArgumentException">The format or settings cannot be written (sample type, rate, channel count, mask that does not match the channels, RF64 on a non-seekable stream without a known length).</exception>
    public WavEncoder(Stream output, AudioFormat format, WavEncoderSettings? settings = null, bool leaveOpen = false)
    {
        ArgumentNullException.ThrowIfNull(output);
        if (!output.CanWrite) throw new ArgumentException("Stream must be writable.", nameof(output));
        settings ??= new WavEncoderSettings();
        var type = settings.SampleType ?? (format.NativeType == NativeSampleType.Lossy ? NativeSampleType.PcmSigned16 : format.NativeType);
        if (type is < NativeSampleType.PcmUnsigned8 or > NativeSampleType.IeeeFloat64) throw new ArgumentException("Unsupported sample type.", nameof(settings));
        if (format.SampleRate < 1) throw new ArgumentException("Sample rate must be positive.", nameof(format));
        if (format.Channels < 1) throw new ArgumentException("Channel count must be positive.", nameof(format));
        if (settings.DitherSeed == 0) throw new ArgumentException("DitherSeed must not be 0.", nameof(settings));

        _type = type;
        _channels = format.Channels;
        _bytesPerSample = SampleConvert.BytesPerSample(type);
        _bits = _bytesPerSample * 8;
        _isFloat = type is NativeSampleType.IeeeFloat32 or NativeSampleType.IeeeFloat64;
        long blockAlign = (long)_channels * _bytesPerSample;
        if (blockAlign > ushort.MaxValue || _channels > ushort.MaxValue) throw new ArgumentException("Too many channels for a WAV block.", nameof(format));
        if ((ulong)format.SampleRate * (ulong)blockAlign > uint.MaxValue) throw new ArgumentException("Byte rate does not fit a WAV header.", nameof(format));

        uint mask = (uint)format.ChannelMask;
        if (mask == 0) mask = (uint)AudioFormat.DefaultMask(_channels);
        if (mask != 0 && BitOperations.PopCount(mask) != _channels) throw new ArgumentException("The channel mask must have one bit per channel.", nameof(format));
        bool extensible = settings.Extensible ?? (_channels > 2 || _bits > 16);

        _out = output;
        _leaveOpen = leaveOpen;
        _seekable = output.CanSeek;
        _origin = _seekable ? output.Position : 0;
        _dither = settings.Dither;
        _ditherState = settings.DitherSeed;
        _declaredFrames = _seekable ? -1 : settings.TotalFrames;
        _ints = new int[ChunkSamples / _channels * _channels + _channels];
        _packed = new byte[_ints.Length * _bytesPerSample];

        long knownBytes = _declaredFrames >= 0 ? _declaredFrames * blockAlign : -1;
        bool rf64 = settings.ForceRf64 || (knownBytes >= 0 && knownBytes > 0xFFFF_FFFFL - 4096);
        if (rf64 && !_seekable && _declaredFrames < 0)
            throw new ArgumentException("RF64 on a non-seekable stream needs WavEncoderSettings.TotalFrames.", nameof(settings));
        _rf64Header = rf64;
        _hasJunk = _seekable && !rf64;

        // Header layout: RIFF/RF64 header, [ds64 or JUNK], fmt, [fact], data header.
        int fmtSize = extensible ? 40 : (_isFloat ? 18 : 16);
        int len = 12 + (rf64 || _hasJunk ? 36 : 0) + 8 + fmtSize + (_isFloat ? 12 : 0) + 8;
        var h = new byte[len];
        int p;
        (rf64 ? "RF64"u8 : "RIFF"u8).CopyTo(h);
        BinaryPrimitives.WriteUInt32LittleEndian(h.AsSpan(4), 0xFFFF_FFFF);
        "WAVE"u8.CopyTo(h.AsSpan(8));
        p = 12;
        if (rf64) { "ds64"u8.CopyTo(h.AsSpan(p)); BinaryPrimitives.WriteUInt32LittleEndian(h.AsSpan(p + 4), 28); }
        else if (_hasJunk) { "JUNK"u8.CopyTo(h.AsSpan(p)); BinaryPrimitives.WriteUInt32LittleEndian(h.AsSpan(p + 4), 28); }
        if (rf64 || _hasJunk) p += 36;

        "fmt "u8.CopyTo(h.AsSpan(p));
        BinaryPrimitives.WriteUInt32LittleEndian(h.AsSpan(p + 4), (uint)fmtSize);
        var f = h.AsSpan(p + 8, fmtSize);
        ushort tag = _isFloat ? (ushort)3 : (ushort)1;
        BinaryPrimitives.WriteUInt16LittleEndian(f, extensible ? (ushort)0xFFFE : tag);
        BinaryPrimitives.WriteUInt16LittleEndian(f[2..], (ushort)_channels);
        BinaryPrimitives.WriteUInt32LittleEndian(f[4..], (uint)format.SampleRate);
        BinaryPrimitives.WriteUInt32LittleEndian(f[8..], (uint)(format.SampleRate * blockAlign));
        BinaryPrimitives.WriteUInt16LittleEndian(f[12..], (ushort)blockAlign);
        BinaryPrimitives.WriteUInt16LittleEndian(f[14..], (ushort)_bits);
        if (fmtSize >= 18) BinaryPrimitives.WriteUInt16LittleEndian(f[16..], (ushort)(extensible ? 22 : 0));
        if (extensible)
        {
            BinaryPrimitives.WriteUInt16LittleEndian(f[18..], (ushort)_bits); // valid bits per sample
            BinaryPrimitives.WriteUInt32LittleEndian(f[20..], mask);
            BinaryPrimitives.WriteUInt16LittleEndian(f[24..], tag); // SubFormat GUID: the format tag plus the standard base
            ExtensibleGuidTail.CopyTo(f[26..]);
        }
        p += 8 + fmtSize;

        _factOffset = -1;
        if (_isFloat)
        {
            // Non-PCM formats carry a fact chunk with the frame count.
            "fact"u8.CopyTo(h.AsSpan(p));
            BinaryPrimitives.WriteUInt32LittleEndian(h.AsSpan(p + 4), 4);
            _factOffset = p + 8;
            BinaryPrimitives.WriteUInt32LittleEndian(h.AsSpan(p + 8), knownBytes >= 0 ? (uint)Math.Min(_declaredFrames, uint.MaxValue) : 0);
            p += 12;
        }
        "data"u8.CopyTo(h.AsSpan(p));
        _dataHeaderOffset = p;
        _headerLength = len;

        // Sizes: exact when known up front, otherwise the 0xFFFFFFFF "unknown" value (patched later on seekable streams).
        if (knownBytes >= 0 && !rf64)
        {
            long total = knownBytes + (knownBytes & 1) + len - 8;
            BinaryPrimitives.WriteUInt32LittleEndian(h.AsSpan(4), (uint)total);
            BinaryPrimitives.WriteUInt32LittleEndian(h.AsSpan(p + 4), (uint)knownBytes);
        }
        else BinaryPrimitives.WriteUInt32LittleEndian(h.AsSpan(p + 4), 0xFFFF_FFFF);
        if (rf64)
        {
            var ds = h.AsSpan(20, 28);
            if (knownBytes >= 0)
            {
                BinaryPrimitives.WriteUInt64LittleEndian(ds, (ulong)(knownBytes + (knownBytes & 1) + len - 8));
                BinaryPrimitives.WriteUInt64LittleEndian(ds[8..], (ulong)knownBytes);
                BinaryPrimitives.WriteUInt64LittleEndian(ds[16..], (ulong)_declaredFrames);
            }
        }
        Format = new AudioFormat(format.SampleRate, _channels, (ChannelMask)mask, type, _bits);
        _out.Write(h);
    }

    /// <summary>The format being written: the given rate and channels, the mask in use, and the stored sample type.</summary>
    public AudioFormat Format { get; }

    /// <summary>Frames accepted so far.</summary>
    public long FramesWritten => _dataBytes / (_channels * _bytesPerSample);

    /// <summary>Writes interleaved <see cref="float"/> frames, converting to the stored type as described on the class.</summary>
    /// <param name="samples">Interleaved samples; the length must be a multiple of the channel count.</param>
    /// <exception cref="ArgumentException">The length is not a whole number of frames.</exception>
    /// <exception cref="InvalidOperationException">The encoder was finished, an earlier write failed, or the declared length was exceeded.</exception>
    public void Write(ReadOnlySpan<float> samples)
    {
        Check(samples.Length);
        for (int o = 0; o < samples.Length; o += _ints.Length)
        {
            var part = samples.Slice(o, Math.Min(_ints.Length, samples.Length - o));
            if (_isFloat)
            {
                SamplePack.Float(part, _packed, _type == NativeSampleType.IeeeFloat64);
            }
            else
            {
                var ints = _ints.AsSpan(0, part.Length);
                if (_dither) SampleQuantize.ToIntegerDithered(part, ints, _bits, ref _ditherState);
                else SampleQuantize.ToInteger(part, ints, _bits);
                SamplePack.Integer(_type, ints, _packed);
            }
            Emit(part.Length);
        }
    }

    /// <summary>Writes interleaved integer frames exactly (integer sample types only).</summary>
    /// <param name="samples">Interleaved samples; the length must be a multiple of the channel count.</param>
    /// <exception cref="NotSupportedException">The stored type is floating-point.</exception>
    /// <exception cref="ArgumentException">The length is not a whole number of frames.</exception>
    /// <exception cref="ArgumentOutOfRangeException">A sample is outside the stored type's range.</exception>
    /// <exception cref="InvalidOperationException">The encoder was finished, an earlier write failed, or the declared length was exceeded.</exception>
    public void WriteInteger(ReadOnlySpan<int> samples)
    {
        if (_isFloat) throw new NotSupportedException("Integer samples cannot be written to a floating-point WAV; use Write(float).");
        Check(samples.Length);
        int lo = _bits == 32 ? int.MinValue : -(1 << (_bits - 1));
        int hi = _bits == 32 ? int.MaxValue : (1 << (_bits - 1)) - 1;
        for (int o = 0; o < samples.Length; o += _ints.Length)
        {
            var part = samples.Slice(o, Math.Min(_ints.Length, samples.Length - o));
            foreach (int v in part)
                if (v < lo || v > hi) throw new ArgumentOutOfRangeException(nameof(samples), $"A sample does not fit {_bits} bits.");
            SamplePack.Integer(_type, part, _packed);
            Emit(part.Length);
        }
    }

    private void Check(int length)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        if (_finished) throw new InvalidOperationException("The encoder is finished.");
        if (_failed) throw new InvalidOperationException("An earlier write failed; the output is incomplete.");
        if (length % _channels != 0) throw new ArgumentException("The sample count must be a multiple of the channel count.", nameof(length));
    }

    private void Emit(int sampleCount)
    {
        int bytes = sampleCount * _bytesPerSample;
        if (_declaredFrames >= 0 && _dataBytes + bytes > _declaredFrames * _channels * _bytesPerSample)
            throw new InvalidOperationException("More frames were written than WavEncoderSettings.TotalFrames declared.");
        try { _out.Write(_packed, 0, bytes); }
        catch { _failed = true; throw; }
        _dataBytes += bytes;
    }

    /// <summary>
    /// Completes the file. On a seekable stream the chunk sizes are patched (converting to RF64 if the data needs it) and the position is left at
    /// the end; a pad byte follows odd-length data. Calling it again does nothing.
    /// </summary>
    /// <exception cref="InvalidOperationException">The output is not seekable and fewer frames were written than <see cref="WavEncoderSettings.TotalFrames"/> declared, or an earlier write failed.</exception>
    public void Finish()
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        if (_finished) return;
        if (_failed) throw new InvalidOperationException("An earlier write failed; the output is incomplete.");
        _finished = true;
        if ((_dataBytes & 1) != 0) _out.WriteByte(0); // RIFF chunks are word-aligned
        if (!_seekable)
        {
            if (_declaredFrames >= 0 && FramesWritten != _declaredFrames)
                throw new InvalidOperationException($"TotalFrames was {_declaredFrames} but {FramesWritten} frames were written.");
            _out.Flush();
            return;
        }

        long fileLength = _headerLength + _dataBytes + (_dataBytes & 1);
        long riffSize = fileLength - 8;
        long end = _out.Position;
        Span<byte> b8 = stackalloc byte[8];
        Span<byte> ds = stackalloc byte[28];
        long frames = FramesWritten;
        bool needRf64 = _rf64Header || riffSize >= 0xFFFF_FFFFL || _dataBytes >= 0xFFFF_FFFFL;
        if (needRf64)
        {
            // RF64 (EBU Tech 3306): the 32-bit size fields become 0xFFFFFFFF and the real sizes live in ds64.
            "RF64"u8.CopyTo(b8);
            BinaryPrimitives.WriteUInt32LittleEndian(b8[4..], 0xFFFF_FFFF);
            PatchAt(0, b8);
            "ds64"u8.CopyTo(b8);
            BinaryPrimitives.WriteUInt32LittleEndian(b8[4..], 28);
            PatchAt(12, b8);
            BinaryPrimitives.WriteUInt64LittleEndian(ds, (ulong)riffSize);
            BinaryPrimitives.WriteUInt64LittleEndian(ds[8..], (ulong)_dataBytes);
            BinaryPrimitives.WriteUInt64LittleEndian(ds[16..], (ulong)frames);
            BinaryPrimitives.WriteUInt32LittleEndian(ds[24..], 0); // no table entries
            PatchAt(20, ds);
            BinaryPrimitives.WriteUInt32LittleEndian(b8, 0xFFFF_FFFF);
            PatchAt(_dataHeaderOffset + 4, b8[..4]);
        }
        else
        {
            BinaryPrimitives.WriteUInt32LittleEndian(b8, (uint)riffSize);
            PatchAt(4, b8[..4]);
            BinaryPrimitives.WriteUInt32LittleEndian(b8, (uint)_dataBytes);
            PatchAt(_dataHeaderOffset + 4, b8[..4]);
        }
        if (_factOffset >= 0)
        {
            BinaryPrimitives.WriteUInt32LittleEndian(b8, (uint)Math.Min(frames, uint.MaxValue));
            PatchAt(_factOffset, b8[..4]);
        }
        _out.Position = end;
        _out.Flush();
    }

    private void PatchAt(long offset, ReadOnlySpan<byte> bytes)
    {
        _out.Position = _origin + offset;
        _out.Write(bytes);
    }

    /// <summary>Finishes the file if <see cref="Finish"/> has not been called (and no write failed), then closes the output unless it was left open.</summary>
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
            if (!_leaveOpen) _out.Dispose();
        }
    }

    /// <summary>
    /// Writes everything a decoder delivers. Rate, channels and layout come from the decoder; the stored type is
    /// <see cref="WavEncoderSettings.SampleType"/> or the decoder's own. A lossless integer source stored at its own type is copied exactly,
    /// anything else goes through float.
    /// </summary>
    /// <param name="source">The decoder, read from its current position to the end. It is not disposed.</param>
    /// <param name="output">Destination stream.</param>
    /// <param name="settings">Options; defaults when null.</param>
    /// <param name="leaveOpen">Keep <paramref name="output"/> open (default false).</param>
    /// <param name="cancellationToken">Checked between reads.</param>
    /// <returns>The number of frames written.</returns>
    public static long Encode(IAudioDecoder source, Stream output, WavEncoderSettings? settings = null, bool leaveOpen = false, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(source);
        settings ??= new WavEncoderSettings();
        var fmt = source.Format;
        var type = settings.SampleType ?? (fmt.NativeType == NativeSampleType.Lossy ? NativeSampleType.PcmSigned16 : fmt.NativeType);
        if (settings.TotalFrames < 0 && source.LengthFrames >= 0) settings = settings with { TotalFrames = source.LengthFrames - source.PositionFrames };
        bool exact = source.HasNativeIntegers && type == fmt.NativeType;
        using var enc = new WavEncoder(output, fmt, settings with { SampleType = type }, leaveOpen);
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
}
