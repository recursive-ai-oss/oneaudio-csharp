using System.Buffers.Binary;
using System.Numerics;
using OneAudio.Kernels;

namespace OneAudio;

/// <summary>The RIFF/WAVE container as an <see cref="IContainerFormat"/>. Register with <see cref="AudioCodecRegistryExtensions.AddWav"/>.</summary>
public sealed class WavFormat : IContainerFormat
{
    /// <summary>The shared instance.</summary>
    public static WavFormat Instance { get; } = new();

    private WavFormat() { }

    /// <inheritdoc />
    public string Name => "RIFF/WAVE";

    /// <inheritdoc />
    public bool Matches(ReadOnlySpan<byte> head) =>
        head.Length >= 12 && (head[..4].SequenceEqual("RIFF"u8) || head[..4].SequenceEqual("RF64"u8)) && head[8..12].SequenceEqual("WAVE"u8);

    /// <inheritdoc />
    public IAudioDecoder Open(Stream stream, AudioCodecRegistry registry, AudioLimits limits, bool leaveOpen, CancellationToken cancellationToken) =>
        WavDecoder.Open(stream, limits, leaveOpen, cancellationToken);
}

/// <summary>Registration helpers.</summary>
public static class AudioCodecRegistryExtensions
{
    /// <summary>Registers RIFF/WAVE (and RF64) reading.</summary>
    /// <param name="registry">The registry.</param>
    /// <returns><paramref name="registry"/>, for chaining.</returns>
    public static AudioCodecRegistry AddWav(this AudioCodecRegistry registry)
    {
        ArgumentNullException.ThrowIfNull(registry);
        return registry.Add(WavFormat.Instance);
    }
}

/// <summary>
/// Streaming decoder for RIFF/WAVE and RF64 files: integer PCM 8/16/24/32-bit, IEEE float 32/64, and
/// <c>WAVE_FORMAT_EXTENSIBLE</c> wrappers of those. Other format tags (ADPCM, MP3-in-WAV, ...) throw
/// <see cref="UnsupportedAudioFormatException"/>.
/// <para>
/// Native integers are the container-width samples (a 20-bit-valid sample in a 24-bit container is returned as
/// stored). Float reads of integer PCM divide by 2^(bits-1). A <c>data</c> chunk whose declared size is 0 or
/// 0xFFFFFFFF (streaming writers) extends to the end of a seekable stream; on a non-seekable stream it is read until
/// the stream ends and <see cref="LengthFrames"/> is -1. If a file is shorter than its header claims, decoding stops at
/// the real end without error. Trailing bytes that do not make a whole frame are ignored. Chunks after <c>data</c> are
/// not read, and a <c>data</c> chunk before <c>fmt </c> is accepted only on seekable streams.
/// </para>
/// </summary>
public sealed class WavDecoder : IAudioDecoder
{
    private const uint DeclaredUnsized = 0xFFFFFFFF;

    private readonly Stream _stream;
    private readonly bool _leaveOpen;
    private readonly long _origin;
    private readonly long _dataStart;
    private readonly int _blockAlign;
    private readonly byte[] _scratch;
    private readonly int _scratchFrames;
    private long _position;
    private bool _disposed;

    private WavDecoder(Stream stream, bool leaveOpen, long origin, long dataStart, long lengthFrames, AudioFormat format)
    {
        _stream = stream;
        _leaveOpen = leaveOpen;
        _origin = origin;
        _dataStart = dataStart;
        LengthFrames = lengthFrames;
        Format = format;
        _blockAlign = format.NativeBytesPerFrame;
        _scratchFrames = Math.Max(1, 8192 / _blockAlign);
        _scratch = new byte[_scratchFrames * _blockAlign];
    }

    /// <inheritdoc />
    public AudioFormat Format { get; }

    /// <inheritdoc />
    public long LengthFrames { get; }

    /// <inheritdoc />
    public long PositionFrames => _position;

    /// <inheritdoc />
    public bool CanSeek => _stream.CanSeek;

    /// <inheritdoc />
    public bool HasNativeIntegers => Format.NativeType is >= NativeSampleType.PcmUnsigned8 and <= NativeSampleType.PcmSigned32;

    /// <summary>Opens a WAV stream positioned at the "RIFF" magic.</summary>
    /// <param name="stream">Readable stream. Owned by the decoder unless <paramref name="leaveOpen"/>.</param>
    /// <param name="limits">Limits; <see cref="AudioLimits.Default"/> when null.</param>
    /// <param name="leaveOpen">Keep the stream open on dispose.</param>
    /// <param name="cancellationToken">Observed between chunks.</param>
    /// <returns>A decoder at frame 0. If this throws the stream is left open.</returns>
    /// <exception cref="AudioFormatException">The header is malformed or truncated.</exception>
    /// <exception cref="UnsupportedAudioFormatException">The sample format is not supported.</exception>
    /// <exception cref="AudioLimitExceededException">Channels, rate or chunk counts exceed <paramref name="limits"/>.</exception>
    public static WavDecoder Open(Stream stream, AudioLimits? limits = null, bool leaveOpen = false, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(stream);
        if (!stream.CanRead) throw new ArgumentException("Stream must be readable.", nameof(stream));
        limits ??= AudioLimits.Default;
        bool seekable = stream.CanSeek;
        long origin = seekable ? stream.Position : 0;
        long pos = 0; // bytes consumed since origin

        Span<byte> riff = stackalloc byte[12];
        ReadExact(stream, riff);
        pos += 12;
        bool rf64 = riff[..4].SequenceEqual("RF64"u8);
        if (!(rf64 || riff[..4].SequenceEqual("RIFF"u8)) || !riff[8..12].SequenceEqual("WAVE"u8))
            throw new AudioFormatException("Not a RIFF/WAVE file.");

        long ds64DataSize = -1;
        bool haveFmt = false;
        NativeSampleType type = default;
        int channels = 0, rate = 0, bits = 0;
        ChannelMask mask = ChannelMask.None;
        long dataStart = -1, dataSize = -1;
        int elements = 0;
        Span<byte> hdr = stackalloc byte[8];
        Span<byte> body = stackalloc byte[40];

        while (true)
        {
            cancellationToken.ThrowIfCancellationRequested();
            int got = stream.ReadAtLeast(hdr, 8, throwOnEndOfStream: false);
            if (got == 0) break;
            if (got < 8) throw new AudioFormatException("Truncated chunk header.");
            pos += 8;
            limits.CheckContainerElements(++elements);
            ReadOnlySpan<byte> id = hdr[..4];
            long size = BinaryPrimitives.ReadUInt32LittleEndian(hdr[4..]);

            if (id.SequenceEqual("data"u8))
            {
                bool unsized = size == 0 || size == DeclaredUnsized;
                if (rf64 && size == DeclaredUnsized && ds64DataSize >= 0) { size = ds64DataSize; unsized = false; }
                dataStart = pos;
                dataSize = unsized ? -1 : size;
                if (haveFmt) break;
                // data before fmt: only workable when we can jump over it.
                if (!seekable || unsized) throw new AudioFormatException("The data chunk precedes the fmt chunk.");
                Skip(stream, seekable, ref pos, size + (size & 1));
                continue;
            }

            if (id.SequenceEqual("fmt "u8))
            {
                if (size < 16) throw new AudioFormatException("fmt chunk is too short.");
                limits.CheckMetadataBytes(size);
                int take = (int)Math.Min(size, body.Length);
                ReadExact(stream, body[..take]);
                pos += take;
                Skip(stream, seekable, ref pos, size - take + (size & 1));
                ParseFmt(body[..take], limits, out type, out channels, out rate, out bits, out mask);
                haveFmt = true;
                if (dataStart >= 0) break;
                continue;
            }

            if (rf64 && id.SequenceEqual("ds64"u8))
            {
                if (size < 28) throw new AudioFormatException("ds64 chunk is too short.");
                ReadExact(stream, body[..16]);
                pos += 16;
                ds64DataSize = (long)BinaryPrimitives.ReadUInt64LittleEndian(body[8..]);
                if (ds64DataSize < 0) throw new AudioFormatException("ds64 data size is out of range.");
                Skip(stream, seekable, ref pos, size - 16 + (size & 1));
                continue;
            }

            Skip(stream, seekable, ref pos, size + (size & 1));
        }

        if (!haveFmt) throw new AudioFormatException("Missing fmt chunk.");
        if (dataStart < 0) throw new AudioFormatException("Missing data chunk.");

        int blockAlign = channels * (bits / 8);
        long lengthFrames;
        if (seekable)
        {
            long available = Math.Max(0, stream.Length - origin - dataStart);
            long bytes = dataSize < 0 ? available : Math.Min(dataSize, available);
            lengthFrames = bytes / blockAlign;
            stream.Position = origin + dataStart;
        }
        else
        {
            lengthFrames = dataSize < 0 ? -1 : dataSize / blockAlign;
        }

        var format = new AudioFormat(rate, channels, mask, type, bits);
        return new WavDecoder(stream, leaveOpen, origin, dataStart, lengthFrames, format);
    }

    // Microsoft WAVEFORMATEX / WAVEFORMATEXTENSIBLE layout.
    private static void ParseFmt(ReadOnlySpan<byte> f, AudioLimits limits, out NativeSampleType type, out int channels, out int rate, out int bits, out ChannelMask mask)
    {
        int tag = BinaryPrimitives.ReadUInt16LittleEndian(f);
        channels = BinaryPrimitives.ReadUInt16LittleEndian(f[2..]);
        long rawRate = BinaryPrimitives.ReadUInt32LittleEndian(f[4..]);
        int blockAlign = BinaryPrimitives.ReadUInt16LittleEndian(f[12..]);
        bits = BinaryPrimitives.ReadUInt16LittleEndian(f[14..]);
        uint rawMask = 0;

        if (tag == 0xFFFE)
        {
            if (f.Length < 40) throw new AudioFormatException("WAVE_FORMAT_EXTENSIBLE fmt chunk is too short.");
            if (BinaryPrimitives.ReadUInt16LittleEndian(f[16..]) < 22) throw new AudioFormatException("Invalid extensible format size.");
            rawMask = BinaryPrimitives.ReadUInt32LittleEndian(f[20..]);
            // SubFormat GUID: the first two bytes are the format tag, the rest must be the standard base GUID tail.
            ReadOnlySpan<byte> guidTail = [0x00, 0x00, 0x00, 0x00, 0x10, 0x00, 0x80, 0x00, 0x00, 0xAA, 0x00, 0x38, 0x9B, 0x71];
            if (!f.Slice(26, 14).SequenceEqual(guidTail)) throw new UnsupportedAudioFormatException("Unrecognised WAVE_FORMAT_EXTENSIBLE sub-format.");
            tag = BinaryPrimitives.ReadUInt16LittleEndian(f[24..]);
        }

        limits.CheckChannels(channels);
        limits.CheckSampleRate(rawRate);
        rate = (int)rawRate;

        type = (tag, bits) switch
        {
            (1, 8) => NativeSampleType.PcmUnsigned8,
            (1, 16) => NativeSampleType.PcmSigned16,
            (1, 24) => NativeSampleType.PcmSigned24,
            (1, 32) => NativeSampleType.PcmSigned32,
            (3, 32) => NativeSampleType.IeeeFloat32,
            (3, 64) => NativeSampleType.IeeeFloat64,
            _ => throw new UnsupportedAudioFormatException($"Unsupported WAV sample format (tag 0x{tag:X4}, {bits} bits)."),
        };
        if (blockAlign != channels * (bits / 8)) throw new AudioFormatException("Block alignment does not match channels and sample size.");

        mask = (ChannelMask)(rawMask & 0x7FF) is var m && BitOperations.PopCount((uint)m) == channels ? m : ChannelMask.None;
        if (mask == ChannelMask.None && tag != 0xFFFE) mask = AudioFormat.DefaultMask(channels);
    }

    private static void ReadExact(Stream s, Span<byte> buffer)
    {
        if (s.ReadAtLeast(buffer, buffer.Length, throwOnEndOfStream: false) < buffer.Length)
            throw new AudioFormatException("Unexpected end of file in header.");
    }

    private static void Skip(Stream s, bool seekable, ref long pos, long count)
    {
        if (count <= 0) return;
        if (seekable)
        {
            s.Seek(count, SeekOrigin.Current);
        }
        else
        {
            Span<byte> junk = stackalloc byte[512];
            long left = count;
            while (left > 0)
            {
                int n = s.Read(junk[..(int)Math.Min(left, junk.Length)]);
                if (n == 0) break; // truncated; the caller finds out at the next header read
                left -= n;
            }
        }
        pos += count;
    }

    /// <inheritdoc />
    public int Read(Span<float> destination)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        int ch = Format.Channels;
        int want = destination.Length / ch;
        int total = 0;
        while (total < want)
        {
            int got = Fill(Math.Min(want - total, _scratchFrames));
            if (got == 0) break;
            SampleConvert.ToFloat(Format.NativeType, _scratch, destination.Slice(total * ch, got * ch));
            total += got;
        }
        return total;
    }

    /// <inheritdoc />
    public int ReadInteger(Span<int> destination)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        if (!HasNativeIntegers) throw new NotSupportedException("Floating-point WAV data has no integer representation.");
        int ch = Format.Channels;
        int want = destination.Length / ch;
        int total = 0;
        while (total < want)
        {
            int got = Fill(Math.Min(want - total, _scratchFrames));
            if (got == 0) break;
            SampleConvert.ToInteger(Format.NativeType, _scratch, destination.Slice(total * ch, got * ch));
            total += got;
        }
        return total;
    }

    // Reads up to maxFrames whole frames into the scratch buffer and advances the position.
    private int Fill(int maxFrames)
    {
        if (LengthFrames >= 0) maxFrames = (int)Math.Min(maxFrames, LengthFrames - _position);
        if (maxFrames <= 0) return 0;
        int bytes = _stream.ReadAtLeast(_scratch.AsSpan(0, maxFrames * _blockAlign), maxFrames * _blockAlign, throwOnEndOfStream: false);
        int frames = bytes / _blockAlign;
        _position += frames;
        return frames;
    }

    /// <inheritdoc />
    public void Seek(long frame)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        if (!_stream.CanSeek) throw new NotSupportedException("The underlying stream is not seekable.");
        ArgumentOutOfRangeException.ThrowIfNegative(frame);
        ArgumentOutOfRangeException.ThrowIfGreaterThan(frame, LengthFrames);
        _stream.Position = _origin + _dataStart + frame * _blockAlign;
        _position = frame;
    }

    /// <inheritdoc />
    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        if (!_leaveOpen) _stream.Dispose();
    }
}
