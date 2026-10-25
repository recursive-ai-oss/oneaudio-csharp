using System.Buffers.Binary;

namespace OneAudio.Mp3;

/// <summary>Recognises MP3 files (MPEG audio, optionally behind ID3v2 tags) and opens them as <see cref="Mp3Decoder"/>.</summary>
public sealed class Mp3Format : IContainerFormat
{
    /// <summary>The shared instance.</summary>
    public static Mp3Format Instance { get; } = new();

    private Mp3Format() { }

    /// <inheritdoc />
    public string Name => "MP3";

    /// <inheritdoc />
    public bool Matches(ReadOnlySpan<byte> head)
    {
        if (Mp3Decoder.IsId3v2Header(head, out _)) return true;
        // A header at offset 0 is enough. Streams that begin with padding or junk are recognised by the first header
        // whose successor frame (when it lies inside the sniffed bytes) is also a header, so a stray 0xFF byte in some
        // other format does not qualify when the evidence is visible.
        for (int at = 0; at + 4 <= head.Length; at++)
        {
            if (head[at] != 0xFF || !Mp3Header.TryParse(head[at..], out var h)) continue;
            if (h.Layer != 3) { if (at == 0) return true; continue; } // recognised so that open can say "Layer I/II are not supported"
            int len = h.FrameBytes;
            if (len <= 0) { if (at == 0) return true; continue; } // free format: validated while opening
            if (at + len + 4 <= head.Length)
            {
                if (Mp3Header.TryParse(head[(at + len)..], out var next) && next.IsCompatibleWith(h)) return true;
                continue;
            }
            return true; // the frame runs past the sniffed bytes, so it cannot be confirmed here; opening validates it
        }
        return false;
    }

    /// <inheritdoc />
    public IAudioDecoder Open(Stream stream, AudioCodecRegistry registry, AudioLimits limits, bool leaveOpen, CancellationToken cancellationToken) =>
        Mp3Decoder.Open(stream, limits, leaveOpen, cancellationToken);
}

/// <summary>
/// Decodes MPEG audio Layer III (MP3): MPEG-1 (32, 44.1, 48 kHz), MPEG-2 (16, 22.05, 24 kHz) and the MPEG-2.5
/// extension (8, 11.025, 12 kHz), mono and the three two-channel modes including mid/side and intensity stereo,
/// fixed, variable and free-format bitrates. ID3v2 tags in front are skipped and ID3v1/APE tags at the end ignored.
/// Layers I and II are rejected with <see cref="UnsupportedAudioFormatException"/>. The output channel count is the
/// first frame's; if a later frame changes the channel mode, mono is repeated into both channels or stereo averaged to mono.
/// <para>
/// <b>Gapless playback.</b> When the first frame is a Xing/Info header with a LAME-style extension, the encoder delay
/// and end padding it records are removed: decoding starts <c>delay + 529</c> samples in (529 is the decoder's own
/// filterbank delay) and the length is <c>frames * samplesPerFrame - delay - padding</c>. Files without that
/// information are decoded in full: nothing is trimmed, and the length is the number of frames times the samples per
/// frame. The Xing/Info frame itself is never decoded.
/// </para>
/// <para>
/// <b>Output.</b> Interleaved float, nominal range [-1, 1], neither clipped nor dithered. Frames whose bit-reservoir
/// data is missing (the start of a stream cut out of a longer one) and frames with undecodable data decode as
/// silence and the stream continues.
/// </para>
/// <para>
/// <b>Seeking.</b> Needs a seekable stream. The first seek away from the start (or opening an untagged file) makes one
/// pass over the frame headers to index the file, so every later seek is exact: decoding resumes up to 48 frames
/// before the target so the reservoir, overlap and filterbank history are rebuilt, and the output is
/// bit-identical to a sequential decode. Xing/VBRI seek tables are not used because they are only approximate.
/// </para>
/// </summary>
public sealed class Mp3Decoder : IAudioDecoder
{
    private const int DecoderDelay = 529;     // 528 + 1: the synthesis filterbank's own latency (LAME documentation)
    private const int PrerollFrames = 48;     // enough frames to refill the bit reservoir (at most 511 bytes) and filterbank memory
    private const int ScanStride0 = 8;

    private readonly Stream _stream;
    private readonly bool _leaveOpen;
    private readonly Mp3FrameReader _reader;
    private readonly Mp3Layer3 _layer3;
    private readonly AudioLimits _limits;
    private readonly int _channels;
    private readonly int _spf;
    private readonly float[] _pcm;
    private readonly float[] _native;   // scratch for streams whose channel mode changes between frames
    private readonly long _audioStart;
    private readonly long _startSkip;
    private int _pcmPos, _pcmLen;
    private long _skip;
    private long _pos;
    private long _length;
    private long _frameNo;        // audio frame index of the next frame to decode
    private long _frameCount = -1;
    private long[] _index = [];
    private int _indexCount;
    private int _stride = ScanStride0;
    private bool _indexed;
    private bool _disposed;

    private Mp3Decoder(Stream stream, bool leaveOpen, Mp3FrameReader reader, Mp3Header first, long audioStart, MpegTag tag, AudioLimits limits)
    {
        _stream = stream;
        _leaveOpen = leaveOpen;
        _reader = reader;
        _limits = limits;
        _channels = first.Channels;
        _spf = first.SamplesPerFrame;
        _audioStart = audioStart;
        Format = new AudioFormat(first.SampleRate, _channels, AudioFormat.DefaultMask(_channels));
        _layer3 = new Mp3Layer3(Mp3FrameReader.MaxFrameBytes);
        _pcm = new float[_spf * _channels];
        _native = new float[_spf * 2];
        _pcmPos = _pcmLen = 0;
        CanSeek = stream.CanSeek;

        _length = -1;
        long frames = tag.Frames;
        if (frames < 0 && CanSeek)
        {
            BuildIndex();
            frames = _frameCount;
        }
        if (frames >= 0)
        {
            _frameCount = frames;
            long total = frames * _spf;
            if (tag.HasGaplessInfo && (long)tag.Delay + tag.Padding <= total)
            {
                _startSkip = tag.Delay + DecoderDelay;
                _length = Math.Max(0, Math.Min(total - tag.Delay - tag.Padding, total - _startSkip));
            }
            else _length = total;
        }
        _skip = _startSkip;
    }

    /// <inheritdoc />
    public AudioFormat Format { get; }

    /// <inheritdoc />
    public long LengthFrames => _length;

    /// <inheritdoc />
    public long PositionFrames => _pos;

    /// <inheritdoc />
    public bool CanSeek { get; }

    /// <inheritdoc />
    public bool HasNativeIntegers => false;

    /// <summary>
    /// Opens an MP3 stream. Most callers go through <see cref="AudioDecoder.Open"/> with <c>AddMp3()</c> registered.
    /// </summary>
    /// <param name="stream">Readable stream positioned at the start of the file.</param>
    /// <param name="limits">Limits; <see cref="AudioLimits.Default"/> when null. ID3v2 tags larger than
    /// <see cref="AudioLimits.MaxMetadataBytes"/> are refused (they are skipped without being read into memory, so the
    /// limit only bounds the time a hostile file can cost), as is a first frame further than that from the start.</param>
    /// <param name="leaveOpen">Keep the stream open when the decoder is disposed.</param>
    /// <param name="cancellationToken">Observed while indexing.</param>
    /// <exception cref="UnsupportedAudioFormatException">The stream is Layer I or II audio.</exception>
    /// <exception cref="AudioFormatException">No MPEG audio frame was found.</exception>
    /// <exception cref="AudioLimitExceededException">A tag or the channel count or sample rate exceeds <paramref name="limits"/>.</exception>
    public static Mp3Decoder Open(Stream stream, AudioLimits? limits = null, bool leaveOpen = false, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(stream);
        if (!stream.CanRead) throw new ArgumentException("Stream must be readable.", nameof(stream));
        limits ??= AudioLimits.Default;
        var reader = new Mp3FrameReader(stream);

        // ID3v2 tags (there may be several) come first.
        for (int tags = 0; tags < 8; tags++)
        {
            cancellationToken.ThrowIfCancellationRequested();
            int have = reader.Ensure(10);
            if (!IsId3v2Header(reader.Peek(have), out long size)) break;
            limits.CheckMetadataBytes(size);
            reader.Skip(size);
        }

        // A header right after the tags that names Layer I or II is refused by name, not mistaken for junk.
        if (reader.Ensure(4) >= 4 && Mp3Header.TryParse(reader.Peek(4), out var probe) && probe.Layer != 3)
            throw new UnsupportedAudioFormatException($"MPEG audio Layer {(probe.Layer == 1 ? "I" : "II")} is not supported; only Layer III (MP3) is.");

        if (!reader.Next(out var first, out int length, limits.MaxMetadataBytes))
            throw new AudioFormatException("No MPEG audio frame found.");
        limits.CheckChannels(first.Channels);
        limits.CheckSampleRate(first.SampleRate);
        reader.Lock(first);

        var tag = MpegTag.Parse(first, reader.Peek(length));
        if (tag.IsInfoFrame) reader.Advance(length);
        long audioStart = reader.Position;
        // For fixed-rate streams the frame count can be checked against the stream size; the tag is only a claim.
        var dec = new Mp3Decoder(stream, leaveOpen, reader, first, audioStart, tag, limits);
        return dec;
    }

    internal static bool IsId3v2Header(ReadOnlySpan<byte> b, out long totalSize)
    {
        totalSize = 0;
        if (b.Length < 10 || b[0] != 'I' || b[1] != 'D' || b[2] != '3') return false;
        if (b[3] == 0xFF || b[4] == 0xFF) return false;
        if ((b[6] | b[7] | b[8] | b[9]) >= 0x80) return false; // synchsafe integers have a clear top bit in every byte
        long size = (b[6] << 21) | (b[7] << 14) | (b[8] << 7) | b[9];
        totalSize = 10 + size + ((b[5] & 0x10) != 0 ? 10 : 0); // the footer flag adds a 10-byte footer
        return true;
    }

    /// <inheritdoc />
    public int Read(Span<float> destination)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        int ch = _channels;
        int wanted = destination.Length / ch;
        int done = 0;
        while (done < wanted)
        {
            if (_length >= 0 && _pos >= _length) break;
            if (_pcmPos >= _pcmLen)
            {
                if (!DecodeNextFrame()) break;
                continue;
            }
            int avail = (_pcmLen - _pcmPos) / ch;
            if (_skip > 0)
            {
                int n = (int)Math.Min(_skip, avail);
                _pcmPos += n * ch;
                _skip -= n;
                continue;
            }
            int take = Math.Min(wanted - done, avail);
            if (_length >= 0) take = (int)Math.Min(take, _length - _pos);
            _pcm.AsSpan(_pcmPos, take * ch).CopyTo(destination.Slice(done * ch));
            _pcmPos += take * ch;
            done += take;
            _pos += take;
        }
        return done;
    }

    /// <inheritdoc />
    public int ReadInteger(Span<int> destination) =>
        throw new NotSupportedException("MP3 is a lossy format; use Read for float samples.");

    private bool DecodeNextFrame()
    {
        if (!_reader.Next(out var h, out int length, long.MaxValue)) return false;
        var frame = _reader.Peek(length);
        if (h.Channels == _channels) _layer3.DecodeFrame(h, frame, _pcm);
        else
        {
            // The channel mode changed mid-stream (a rare mono/stereo switch). The output format is fixed, so
            // mono is repeated into both channels and stereo is averaged down to mono.
            _layer3.DecodeFrame(h, frame, _native);
            if (_channels == 2) for (int i = 0; i < _spf; i++) _pcm[2 * i] = _pcm[2 * i + 1] = _native[i];
            else for (int i = 0; i < _spf; i++) _pcm[i] = (_native[2 * i] + _native[2 * i + 1]) * 0.5f;
        }
        _reader.Advance(length);
        _frameNo++;
        _pcmPos = 0;
        _pcmLen = _spf * _channels;
        return true;
    }

    /// <inheritdoc />
    public void Seek(long frame)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        if (!CanSeek) throw new NotSupportedException("The underlying stream is not seekable.");
        if (frame < 0 || (_length >= 0 && frame > _length)) throw new ArgumentOutOfRangeException(nameof(frame));

        long d = frame + _startSkip;           // sample index in the decoded stream, counted from its first audio frame
        long f = d / _spf;
        long startFrame = Math.Max(0, f - PrerollFrames);
        if (startFrame > 0)
        {
            EnsureIndex();
            startFrame = startFrame / _stride * _stride;
            long slot = Math.Min(startFrame / _stride, _indexCount - 1);
            startFrame = slot * _stride;
            _reader.SeekTo(_index[slot]);
        }
        else
        {
            startFrame = 0;
            _reader.SeekTo(_audioStart);
        }
        _layer3.Reset();
        _frameNo = startFrame;
        _pcmPos = _pcmLen = 0;
        _skip = d - startFrame * _spf;
        _pos = frame;
    }

    private void EnsureIndex()
    {
        if (!_indexed) BuildIndex();
        if (_indexCount == 0) throw new AudioFormatException("The stream has no decodable frames to seek in.");
    }

    /// <summary>Walks the frame headers once, remembering the offset of every <c>_stride</c>-th frame.</summary>
    private void BuildIndex()
    {
        long resume = _reader.Position;
        _reader.SeekTo(_audioStart);
        _index = new long[1024];
        _indexCount = 0;
        _stride = ScanStride0;
        long count = 0;
        while (_reader.Next(out _, out int length, long.MaxValue))
        {
            if (count % _stride == 0)
            {
                if (_indexCount == _index.Length)
                {
                    if (_indexCount >= _limits.MaxContainerElements)
                    {
                        // Keep memory bounded on enormous files: drop every other entry and double the spacing.
                        for (int i = 0; i < _indexCount / 2; i++) _index[i] = _index[2 * i];
                        _indexCount /= 2;
                        _stride *= 2;
                        if (count % _stride != 0) { _reader.Advance(length); count++; continue; }
                    }
                    else Array.Resize(ref _index, Math.Min(_index.Length * 2, _limits.MaxContainerElements));
                }
                _index[_indexCount++] = _reader.Position;
            }
            _reader.Advance(length);
            count++;
        }
        _frameCount = count;
        _indexed = true;
        _reader.SeekTo(resume);
    }

    /// <inheritdoc />
    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        if (!_leaveOpen) _stream.Dispose();
    }
}

/// <summary>
/// The Xing/Info or VBRI header carried in the first frame of many MP3 files, with the LAME extension that records
/// encoder delay and padding (public LAME tag documentation).
/// </summary>
internal readonly struct MpegTag
{
    public bool IsInfoFrame { get; init; }
    public long Frames { get; init; }
    public bool HasGaplessInfo { get; init; }
    public int Delay { get; init; }
    public int Padding { get; init; }

    public static MpegTag Parse(in Mp3Header h, ReadOnlySpan<byte> frame)
    {
        var none = new MpegTag { Frames = -1 };
        int xingAt = 4 + h.SideInfoBytes;
        if (frame.Length >= xingAt + 8 && (frame.Slice(xingAt, 4).SequenceEqual("Xing"u8) || frame.Slice(xingAt, 4).SequenceEqual("Info"u8)))
        {
            int p = xingAt + 4;
            uint flags = BinaryPrimitives.ReadUInt32BigEndian(frame.Slice(p, 4));
            p += 4;
            long frames = -1;
            if ((flags & 1) != 0 && p + 4 <= frame.Length) { frames = BinaryPrimitives.ReadUInt32BigEndian(frame.Slice(p, 4)); p += 4; }
            else if ((flags & 1) != 0) return new MpegTag { IsInfoFrame = true, Frames = -1 };
            if ((flags & 2) != 0) p += 4;
            if ((flags & 4) != 0) p += 100;
            if ((flags & 8) != 0) p += 4;
            // LAME extension: 9-byte encoder string, then 12 bytes of flags and gain data, then delay and padding as two 12-bit fields.
            int delay = 0, padding = 0;
            bool gapless = false;
            if (p + 24 <= frame.Length && IsPrintable(frame.Slice(p, 4)))
            {
                delay = (frame[p + 21] << 4) | (frame[p + 22] >> 4);
                padding = ((frame[p + 22] & 15) << 8) | frame[p + 23];
                gapless = frames >= 0;
            }
            return new MpegTag { IsInfoFrame = true, Frames = frames, HasGaplessInfo = gapless, Delay = delay, Padding = padding };
        }
        const int vbriAt = 4 + 32;
        if (frame.Length >= vbriAt + 18 && frame.Slice(vbriAt, 4).SequenceEqual("VBRI"u8))
        {
            long frames = BinaryPrimitives.ReadUInt32BigEndian(frame.Slice(vbriAt + 14, 4));
            return new MpegTag { IsInfoFrame = true, Frames = frames };
        }
        return none;
    }

    private static bool IsPrintable(ReadOnlySpan<byte> s)
    {
        foreach (byte b in s) if (b < 0x20 || b > 0x7E) return false;
        return true;
    }
}
