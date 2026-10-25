using System.Buffers.Binary;
using System.Security.Cryptography;
using System.Text;

namespace OneAudio.Flac;

/// <summary>
/// Streaming FLAC decoder (RFC 9639) for native <c>.flac</c> files and for FLAC carried in Ogg, bit-exact on the
/// integer samples. Open it through <see cref="AudioDecoder"/> after <see cref="FlacRegistryExtensions.AddFlac"/>, or
/// directly with <see cref="Open"/>.
/// <para>
/// <b>Samples.</b> <see cref="IAudioDecoder.ReadInteger"/> returns the stream's exact integers, interleaved, left-shifted
/// to the whole-byte container width in <see cref="AudioFormat.BitsPerSample"/> (a 12-bit stream reads as 16-bit
/// values with the low four bits zero, a 20-bit stream as 24-bit); this is the layout a WAV writer would store.
/// 8-bit streams read as signed -128..127 and are reported as <see cref="NativeSampleType.PcmUnsigned8"/>, the closest
/// native type. <see cref="IAudioDecoder.Read"/> divides by 2^(bits-1) of the stream's own depth, so full scale is
/// +-1.0 whatever the container width. The true bit depth is <see cref="BitsPerSample"/>.
/// </para>
/// <para>
/// <b>Channels.</b> FLAC's channel order already matches ascending speaker bits for 1 to 8 channels, so no reordering
/// happens and <see cref="AudioFormat.ChannelMask"/> carries the layout RFC 9639 section 9.1.3 defines.
/// </para>
/// <para>
/// <b>Strictness.</b> Every frame is checked against its CRC-8 and CRC-16. A frame that changes the sample rate,
/// channel count or bit depth, exceeds the STREAMINFO block size or maximum frame size, or pushes the stream past the STREAMINFO sample
/// count is rejected with <see cref="AudioFormatException"/> after the frames before it have been delivered.
/// A STREAMINFO sample count of 0 means "unknown": <see cref="LengthFrames"/> is then -1 (or the container's figure for
/// Ogg). Metadata the decoder does not use (pictures, cue sheets, padding, application blocks) is skipped without
/// being buffered; a SEEKTABLE or VORBIS_COMMENT block larger than <see cref="AudioLimits.MaxMetadataBytes"/> is skipped too.
/// </para>
/// <para>
/// <b>Seeking</b> is sample-accurate. With a SEEKTABLE the decoder jumps to the closest earlier point; without one (or
/// when the table does not match the file) it bisects the file for frame headers, validating each candidate with the
/// header CRC-8 and a full frame decode, which needs a known sample count. With an unknown count it decodes forward from
/// the current position, or from the start for a backward seek.
/// </para>
/// </summary>
public sealed class FlacDecoder : IAudioDecoder
{
    private readonly FlacStreamInfo _si;
    private readonly FlacFrameDecoder _fd;
    private readonly AudioFormat _format;
    private readonly float _scale;
    private readonly int _shift;
    private readonly IPacketSource? _packets;
    private readonly Stream? _stream;
    private readonly bool _leaveOpen;
    private readonly AudioLimits _limits;
    private readonly List<string> _comments = [];
    private string? _vendor;
    private bool _haveComments;

    // Stream mode state.
    private StreamReaderState? _io;
    private long _firstFrameOff;
    private long _fileLen = -1;
    private long[]? _seekSamples;
    private long[]? _seekOffsets;

    // Position state: the last decoded frame covers [_blockStart, _blockStart + _blockLen).
    private long _blockStart;
    private int _blockLen;
    private int _blockPos;
    private bool _ended;
    private bool _positionFromHeader;
    private bool _disposed;

    private FlacDecoder(FlacStreamInfo si, AudioLimits limits, IPacketSource? packets, Stream? stream, bool leaveOpen)
    {
        _si = si;
        _limits = limits;
        _packets = packets;
        _stream = stream;
        _leaveOpen = leaveOpen;
        _fd = new FlacFrameDecoder(si);
        int width = (si.BitsPerSample + 7) / 8 * 8;
        _shift = width - si.BitsPerSample;
        _scale = 1f / (1L << (si.BitsPerSample - 1));
        var type = width switch { 8 => NativeSampleType.PcmUnsigned8, 16 => NativeSampleType.PcmSigned16, 24 => NativeSampleType.PcmSigned24, _ => NativeSampleType.PcmSigned32 };
        _format = new AudioFormat(si.SampleRate, si.Channels, MaskFor(si.Channels), type, width);
    }

    private static ChannelMask MaskFor(int channels) => channels switch
    {
        5 => ChannelMask.FrontLeft | ChannelMask.FrontRight | ChannelMask.FrontCenter | ChannelMask.BackLeft | ChannelMask.BackRight,
        7 => ChannelMask.FrontLeft | ChannelMask.FrontRight | ChannelMask.FrontCenter | ChannelMask.LowFrequency
             | ChannelMask.BackCenter | ChannelMask.SideLeft | ChannelMask.SideRight,
        _ => AudioFormat.DefaultMask(channels),
    };

    /// <inheritdoc />
    public AudioFormat Format => _format;

    /// <summary>The stream's true bit depth (4 to 32); <see cref="AudioFormat.BitsPerSample"/> is the byte-rounded container width.</summary>
    public int BitsPerSample => _si.BitsPerSample;

    /// <inheritdoc />
    public long LengthFrames => _si.TotalSamples > 0 ? _si.TotalSamples : Math.Max(_packets?.LengthFrames ?? -1, -1);

    /// <inheritdoc />
    public long PositionFrames => _blockStart + _blockPos;

    /// <inheritdoc />
    public bool CanSeek => _packets is not null ? _packets.CanSeek : _stream!.CanSeek;

    /// <inheritdoc />
    public bool HasNativeIntegers => true;

    /// <summary>The vendor string of the VORBIS_COMMENT block, or null when the file has none or it was skipped.</summary>
    public string? Vendor => _vendor;

    /// <summary>The <c>NAME=value</c> comments of the first VORBIS_COMMENT block; empty when absent or skipped for size.</summary>
    public IReadOnlyList<string> Comments => _comments;

    /// <summary>
    /// The MD5 signature of the unencoded audio from STREAMINFO, or an empty span when the encoder did not store one
    /// (16 zero bytes).
    /// </summary>
    public ReadOnlySpan<byte> Md5 => _si.HasMd5 ? _si.Md5 : default;

    // ---- Opening ------------------------------------------------------------------------------------

    /// <summary>Opens a native FLAC stream (starting with <c>fLaC</c>) positioned at its first byte.</summary>
    /// <param name="stream">Readable stream. Seekable streams get seeking; others decode forward only.</param>
    /// <param name="limits">Limits for untrusted input; <see cref="AudioLimits.Default"/> when null.</param>
    /// <param name="leaveOpen">Keep <paramref name="stream"/> open when the decoder is disposed. If this method throws the stream is never disposed.</param>
    /// <param name="cancellationToken">Observed while scanning metadata.</param>
    /// <returns>A decoder at frame 0.</returns>
    /// <exception cref="AudioFormatException">The data is not valid FLAC.</exception>
    /// <exception cref="AudioLimitExceededException">A header asks for more than <paramref name="limits"/> allow.</exception>
    public static FlacDecoder Open(Stream stream, AudioLimits? limits = null, bool leaveOpen = false, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(stream);
        if (!stream.CanRead) throw new ArgumentException("Stream must be readable.", nameof(stream));
        limits ??= AudioLimits.Default;
        var io = new StreamReaderState(stream, limits);
        return OpenStream(io, limits, leaveOpen, cancellationToken);
    }

    /// <summary>Opens FLAC packets from a container (Ogg FLAC): the mapping header, then one frame per packet.</summary>
    internal static FlacDecoder Create(IPacketSource packets, AudioLimits limits)
    {
        if (!packets.TryReadPacket(out var first) || first.Data.Length < 51)
            throw new AudioFormatException("Missing or truncated Ogg FLAC identification header.");
        var d = first.Data;
        // Ogg FLAC mapping: 0x7F "FLAC", major, minor, big-endian header packet count, then "fLaC" and STREAMINFO's block header.
        if (d[0] != 0x7F || !d.Slice(1, 4).SequenceEqual("FLAC"u8) || !d.Slice(9, 4).SequenceEqual("fLaC"u8))
            throw new AudioFormatException("Not an Ogg FLAC identification header.");
        if (d[5] != 1) throw new UnsupportedAudioFormatException("Unsupported Ogg FLAC mapping version.");
        int headerPackets = (d[7] << 8) | d[8];
        if ((d[13] & 0x7F) != 0 || ((d[14] << 16) | (d[15] << 8) | d[16]) != FlacStreamInfo.Size)
            throw new AudioFormatException("Ogg FLAC header does not start with STREAMINFO.");
        var si = FlacStreamInfo.Parse(d.Slice(17, FlacStreamInfo.Size), limits);
        var dec = new FlacDecoder(si, limits, packets, null, false);
        limits.CheckContainerElements(headerPackets);
        for (int i = 0; i < headerPackets; i++)
        {
            if (!packets.TryReadPacket(out var p)) throw new AudioFormatException("Ogg FLAC stream ends inside its headers.");
            if (p.Data.Length < 4) throw new AudioFormatException("Truncated FLAC metadata packet.");
            int type = p.Data[0] & 0x7F;
            int len = (p.Data[1] << 16) | (p.Data[2] << 8) | p.Data[3];
            if (len != p.Data.Length - 4) throw new AudioFormatException("FLAC metadata packet length mismatch.");
            if (type == 4 && !dec._haveComments && len <= limits.MaxMetadataBytes) dec.ParseVorbisComment(p.Data[4..]);
        }
        return dec;
    }

    internal static FlacDecoder OpenStream(StreamReaderState io, AudioLimits limits, bool leaveOpen, CancellationToken ct)
    {
        var stream = io.Stream;
        var tmp = io;
        if (!tmp.EnsureBytes(4) || !tmp.Peek(4).SequenceEqual("fLaC"u8)) throw new AudioFormatException("Missing fLaC marker.");
        tmp.Consume(4);

        FlacStreamInfo? si = null;
        long[]? seekSamples = null, seekOffsets = null;
        string? vendor = null;
        List<string>? comments = null;
        int blocks = 0;
        while (true)
        {
            ct.ThrowIfCancellationRequested();
            if (!tmp.EnsureBytes(4)) throw new AudioFormatException("Stream ends inside the FLAC metadata.");
            var h = tmp.Peek(4);
            bool last = (h[0] & 0x80) != 0;
            int type = h[0] & 0x7F;
            int len = (h[1] << 16) | (h[2] << 8) | h[3];
            tmp.Consume(4);
            if (++blocks > limits.MaxContainerElements) throw new AudioLimitExceededException(nameof(AudioLimits.MaxContainerElements), limits.MaxContainerElements, blocks);
            if (type == 127) throw new AudioFormatException("Invalid FLAC metadata block type.");
            if (si is null)
            {
                // RFC 9639 section 8.1: STREAMINFO must be the first block.
                if (type != 0) throw new AudioFormatException("FLAC stream does not start with a STREAMINFO block.");
                if (len != FlacStreamInfo.Size) throw new AudioFormatException("FLAC STREAMINFO block has the wrong length.");
                if (!tmp.EnsureBytes(len)) throw new AudioFormatException("Truncated FLAC STREAMINFO block.");
                si = FlacStreamInfo.Parse(tmp.Peek(len), limits);
                tmp.Consume(len);
            }
            else if (type == 3 && seekSamples is null && len % 18 == 0 && len <= limits.MaxMetadataBytes)
            {
                // RFC 9639 section 8.5: 18-byte points, sample number then byte offset from the first frame.
                int count = len / 18;
                var ss = new long[count];
                var so = new long[count];
                int kept = 0;
                var rec = new byte[18];
                bool sorted = true;
                for (int i = 0; i < count; i++)
                {
                    if (!tmp.ReadExact(rec)) throw new AudioFormatException("Truncated FLAC SEEKTABLE block.");
                    ulong sample = BinaryPrimitives.ReadUInt64BigEndian(rec);
                    ulong off = BinaryPrimitives.ReadUInt64BigEndian(rec.AsSpan(8));
                    if (sample == ulong.MaxValue) continue; // placeholder point
                    if (sample > long.MaxValue / 2 || off > long.MaxValue / 2) { sorted = false; continue; }
                    if (kept > 0 && (long)sample <= ss[kept - 1]) { sorted = false; continue; }
                    ss[kept] = (long)sample;
                    so[kept] = (long)off;
                    kept++;
                }
                if (sorted && kept > 0)
                {
                    Array.Resize(ref ss, kept);
                    Array.Resize(ref so, kept);
                    seekSamples = ss;
                    seekOffsets = so;
                }
            }
            else if (type == 4 && comments is null && len <= limits.MaxMetadataBytes)
            {
                var body = new byte[len];
                if (!tmp.ReadExact(body)) throw new AudioFormatException("Truncated FLAC VORBIS_COMMENT block.");
                var probe = new List<string>();
                if (!TryParseVorbisComment(body, limits, probe, out vendor)) throw new AudioFormatException("Invalid FLAC VORBIS_COMMENT block.");
                comments = probe;
            }
            else
            {
                if (!tmp.Skip(len)) throw new AudioFormatException("Stream ends inside a FLAC metadata block.");
            }
            if (last) break;
        }
        if (si is null) throw new AudioFormatException("FLAC stream has no STREAMINFO block.");

        var dec = new FlacDecoder(si, limits, null, stream, leaveOpen);
        dec.AdoptStreamState(tmp, seekSamples, seekOffsets);
        if (comments is not null)
        {
            dec._vendor = vendor;
            dec._comments.AddRange(comments);
            dec._haveComments = true;
        }
        return dec;
    }

    private void AdoptStreamState(StreamReaderState io, long[]? seekSamples, long[]? seekOffsets)
    {
        _io = io;
        _seekSamples = seekSamples;
        _seekOffsets = seekOffsets;
        _firstFrameOff = io.Offset;
        _fileLen = io.Stream.CanSeek ? io.Stream.Length : -1;
        // A frame is usually well under the STREAMINFO maximum; keep that much buffered ahead to avoid retries.
        io.SetHint(_si.MaxFrameSize);
    }

    /// <summary>Test hook: ignore the SEEKTABLE so seeking exercises the frame-sync search.</summary>
    internal void DisableSeekTable()
    {
        _seekSamples = null;
        _seekOffsets = null;
    }

    /// <summary>Test hook: use the scalar LPC reconstruction loop.</summary>
    internal bool ForceScalarLpc { set => _fd.ForceScalar = value; }

    // ---- Vorbis comments (RFC 9639 section 8.6) -------------------------------------------------------

    private void ParseVorbisComment(ReadOnlySpan<byte> body)
    {
        var list = new List<string>();
        if (!TryParseVorbisComment(body, _limits, list, out var vendor)) throw new AudioFormatException("Invalid FLAC VORBIS_COMMENT block.");
        _vendor = vendor;
        _comments.AddRange(list);
        _haveComments = true;
    }

    private static bool TryParseVorbisComment(ReadOnlySpan<byte> b, AudioLimits limits, List<string> comments, out string? vendor)
    {
        vendor = null;
        if (b.Length < 4) return false;
        uint vlen = BinaryPrimitives.ReadUInt32LittleEndian(b);
        int pos = 4;
        if (vlen > (uint)(b.Length - pos)) return false;
        vendor = Encoding.UTF8.GetString(b.Slice(pos, (int)vlen));
        pos += (int)vlen;
        if (b.Length - pos < 4) return false;
        uint count = BinaryPrimitives.ReadUInt32LittleEndian(b[pos..]);
        pos += 4;
        // Each comment needs at least its 4-byte length, which bounds the count by the block size.
        if (count > (uint)((b.Length - pos) / 4) || count > (uint)limits.MaxContainerElements) return false;
        for (uint i = 0; i < count; i++)
        {
            if (b.Length - pos < 4) return false;
            uint clen = BinaryPrimitives.ReadUInt32LittleEndian(b[pos..]);
            pos += 4;
            if (clen > (uint)(b.Length - pos)) return false;
            comments.Add(Encoding.UTF8.GetString(b.Slice(pos, (int)clen)));
            pos += (int)clen;
        }
        return true;
    }

    // ---- Frame fetching ------------------------------------------------------------------------------

    /// <summary>Decodes the next frame into the frame decoder's buffers and advances the block position. False at the end.</summary>
    private bool NextFrame()
    {
        long start = _blockStart + _blockLen;
        _blockStart = start;
        _blockLen = 0;
        _blockPos = 0;
        long total = _si.TotalSamples;
        if (_ended) return false;
        if (total > 0 && start >= total && !_positionFromHeader) { _ended = true; return false; }

        FlacFrameHeader h;
        int used = 0;
        bool got;
        if (_packets is not null)
        {
            got = _packets.TryReadPacket(out var pkt);
            h = default;
            if (got && _fd.Decode(pkt.Data, out h, out used) == FlacFrameStatus.NeedMore)
                throw new AudioFormatException("Truncated FLAC frame.");
        }
        else got = DecodeStreamFrame(out h, out used);
        if (!got) { _ended = true; return false; }

        if (_positionFromHeader)
        {
            // After a container seek the first frame's own number says where it sits; the container's figure marks the end of
            // the packet it chose, which is not the start of the frame.
            _positionFromHeader = false;
            start = HeaderSample(h);
            _blockStart = start;
        }
        int n = h.BlockSize;
        if (_si.MaxFrameSize > 0 && used > _si.MaxFrameSize) throw new AudioFormatException("FLAC frame is larger than the STREAMINFO maximum frame size.");
        if (total > 0 && start + n > total) throw new AudioFormatException("FLAC stream holds more samples than STREAMINFO declares.");
        // RFC 9639 section 8.2: only the last frame may be shorter than the minimum block size.
        if (n < _si.MinBlockSize && total > 0 && start + n < total) throw new AudioFormatException("FLAC frame is shorter than the STREAMINFO minimum block size.");
        _blockLen = n;
        return true;
    }

    private bool DecodeStreamFrame(out FlacFrameHeader h, out int used)
    {
        var io = _io!;
        while (true)
        {
            int avail = io.Available;
            while (avail < io.Hint && !io.Eof) { io.Fill(); avail = io.Available; }
            if (avail == 0) { h = default; used = 0; return false; }
            var st = _fd.Decode(io.Peek(avail), out h, out used);
            if (st == FlacFrameStatus.Ok) { io.Consume(used); io.NoteFrame(used); return true; }
            if (io.Eof) throw new AudioFormatException("Truncated FLAC frame.");
            // Ask for a good deal more before retrying, so a source that returns a few bytes per read does not cost a decode per read.
            int want = avail + Math.Max(avail / 2, 1024);
            while (io.Available < want && !io.Eof) io.Fill();
        }
    }

    // ---- Reading ------------------------------------------------------------------------------------

    /// <inheritdoc />
    public int Read(Span<float> destination)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        int ch = _format.Channels;
        int frames = destination.Length / ch;
        int done = 0;
        float scale = _scale;
        while (done < frames)
        {
            if (_blockPos >= _blockLen && !NextFrame()) break;
            int take = Math.Min(frames - done, _blockLen - _blockPos);
            var dst = destination.Slice(done * ch, take * ch);
            int from = _blockPos;
            var chans = _fd.Channels;
            if (ch == 1)
            {
                var a = chans[0].AsSpan(from, take);
                for (int i = 0; i < a.Length; i++) dst[i] = a[i] * scale;
            }
            else if (ch == 2)
            {
                var a = chans[0].AsSpan(from, take);
                var b = chans[1].AsSpan(from, take);
                for (int i = 0; i < a.Length; i++)
                {
                    dst[2 * i] = a[i] * scale;
                    dst[2 * i + 1] = b[i] * scale;
                }
            }
            else
            {
                for (int c = 0; c < ch; c++)
                {
                    var a = chans[c].AsSpan(from, take);
                    for (int i = 0; i < a.Length; i++) dst[i * ch + c] = a[i] * scale;
                }
            }
            _blockPos += take;
            done += take;
        }
        return done;
    }

    /// <inheritdoc />
    public int ReadInteger(Span<int> destination)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        int ch = _format.Channels;
        int frames = destination.Length / ch;
        int done = 0;
        int shift = _shift;
        while (done < frames)
        {
            if (_blockPos >= _blockLen && !NextFrame()) break;
            int take = Math.Min(frames - done, _blockLen - _blockPos);
            var dst = destination.Slice(done * ch, take * ch);
            int from = _blockPos;
            var chans = _fd.Channels;
            if (ch == 2)
            {
                var a = chans[0].AsSpan(from, take);
                var b = chans[1].AsSpan(from, take);
                for (int i = 0; i < a.Length; i++)
                {
                    dst[2 * i] = a[i] << shift;
                    dst[2 * i + 1] = b[i] << shift;
                }
            }
            else
            {
                for (int c = 0; c < ch; c++)
                {
                    var a = chans[c].AsSpan(from, take);
                    for (int i = 0; i < a.Length; i++) dst[i * ch + c] = a[i] << shift;
                }
            }
            _blockPos += take;
            done += take;
        }
        return done;
    }

    // ---- Seeking ------------------------------------------------------------------------------------

    /// <inheritdoc />
    public void Seek(long frame)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        if (!CanSeek) throw new NotSupportedException("The FLAC source is not seekable.");
        long length = LengthFrames;
        if (frame < 0 || (length >= 0 && frame > length)) throw new ArgumentOutOfRangeException(nameof(frame));
        if (_blockLen > 0 && frame >= _blockStart && frame < _blockStart + _blockLen)
        {
            _blockPos = (int)(frame - _blockStart);
            return;
        }
        SeekCore(frame);
    }

    private void SeekCore(long frame)
    {
        if (_packets is not null)
        {
            _packets.SeekToFrame(frame, out long start);
            _ended = false;
            _positionFromHeader = true;
            _blockStart = start;
            _blockLen = 0;
            _blockPos = 0;
        }
        else if (!SeekStream(frame)) return;
        DiscardTo(frame);
    }

    /// <summary>Decodes forward until the frame holding <paramref name="target"/> and positions inside it.</summary>
    private void DiscardTo(long target)
    {
        while (true)
        {
            if (_blockLen > 0 && target < _blockStart + _blockLen)
            {
                if (target < _blockStart) throw new AudioFormatException("FLAC seek landed after its target.");
                _blockPos = (int)(target - _blockStart);
                return;
            }
            if (!NextFrame()) return; // end of stream: position is the end
        }
    }

    /// <summary>
    /// Positions the stream at or before <paramref name="frame"/>. Returns false when it already landed inside the right frame,
    /// true when the caller still has to decode forward to the target.
    /// </summary>
    private bool SeekStream(long frame)
    {
        var io = _io!;
        _ended = false;
        if (frame == 0)
        {
            io.SetOffset(_firstFrameOff);
            _blockStart = 0;
            _blockLen = 0;
            _blockPos = 0;
            return true;
        }
        // The stream always sits at the start of the frame after the current block, so a target past it can be reached by decoding on.
        long next = _blockStart + _blockLen;
        bool canContinue = frame >= next;
        // Best SEEKTABLE point at or before the target.
        if (_seekSamples is { } ss && _seekOffsets is { } so)
        {
            int idx = Array.BinarySearch(ss, frame);
            if (idx < 0) idx = ~idx - 1;
            if (idx >= 0 && (!canContinue || ss[idx] > next))
            {
                if (TryEnterSeekPoint(ss[idx], _firstFrameOff + so[idx])) return true;
                _seekSamples = null;
                _seekOffsets = null;
                canContinue = false; // the failed probe moved the stream
            }
        }
        long total = _si.TotalSamples;
        if (canContinue && (total <= 0 || frame - next <= 16L * _si.MaxBlockSize)) return true;
        if (total <= 0 || _fileLen <= 0 || frame >= total)
        {
            if (canContinue) return true;
            io.SetOffset(_firstFrameOff);
            _blockStart = 0;
            _blockLen = 0;
            _blockPos = 0;
            return true;
        }
        return Bisect(frame);
    }

    private bool TryEnterSeekPoint(long sample, long offset)
    {
        var io = _io!;
        if (offset < _firstFrameOff || (_fileLen >= 0 && offset >= _fileLen)) return false;
        io.SetOffset(offset);
        // The point is only trusted when a frame header with the promised sample number sits exactly there.
        io.EnsureBytes(16); // longest possible frame header; a shorter tail at end of file is fine
        if (FlacFrameHeaderParser.Parse(io.Peek(io.Available), out var h) != FlacHeaderStatus.Ok) return false;
        if (HeaderSample(h) != sample) return false;
        _blockStart = sample;
        _blockLen = 0;
        _blockPos = 0;
        return true;
    }

    private long HeaderSample(in FlacFrameHeader h) => h.Variable ? (long)h.Number : (long)h.Number * _si.MaxBlockSize;

    /// <summary>
    /// Binary search over byte offsets for the frame containing <paramref name="frame"/>. Every probe finds the first
    /// frame that starts at or after an offset and decodes it fully, so a stray sync pattern in audio data (which
    /// passes the CRC-8 once in 256 tries) is discarded by the frame's CRC-16.
    /// </summary>
    private bool Bisect(long frame)
    {
        var io = _io!;
        long loOff = _firstFrameOff, loSample = 0, hiOff = _fileLen;
        while (hiOff - loOff > 1)
        {
            long mid = loOff + (hiOff - loOff) / 2;
            if (!Probe(mid, hiOff, out long fOff, out long fSample, out int fLen)) { hiOff = mid; continue; }
            if (fSample > frame) { hiOff = fOff; continue; }
            if (frame < fSample + fLen)
            {
                _blockStart = fSample;
                _blockLen = fLen;
                _blockPos = (int)(frame - fSample);
                return false;
            }
            loOff = fOff;
            loSample = fSample;
        }
        io.SetOffset(loOff);
        _blockStart = loSample;
        _blockLen = 0;
        _blockPos = 0;
        return true;
    }

    private bool Probe(long from, long limit, out long frameOffset, out long sample, out int length)
    {
        var io = _io!;
        frameOffset = 0;
        sample = 0;
        length = 0;
        io.SetOffset(from);
        while (true)
        {
            int avail = io.Available;
            if (avail < 2)
            {
                if (io.Eof) return false;
                io.Fill();
                continue;
            }
            int idx = io.Peek(avail - 1).IndexOf((byte)0xFF);
            if (idx < 0) { io.Consume(avail - 1); if (io.Offset >= limit) return false; continue; }
            io.Consume(idx);
            long cand = io.Offset;
            if (cand >= limit) return false;
            avail = io.Available;
            var span = io.Peek(avail);
            if ((span[1] & 0xFE) != 0xF8) { io.Consume(1); continue; }
            FlacFrameStatus st;
            FlacFrameHeader h;
            int used;
            try { st = _fd.Decode(span, out h, out used); }
            catch (AudioFormatException) { io.Consume(1); continue; }
            if (st == FlacFrameStatus.NeedMore)
            {
                if (io.Eof) { io.Consume(1); continue; }
                io.Fill();
                continue;
            }
            io.Consume(used);
            frameOffset = cand;
            sample = HeaderSample(h);
            length = h.BlockSize;
            return true;
        }
    }

    // ---- MD5 -----------------------------------------------------------------------------------------

    /// <summary>
    /// Decodes the whole stream and compares the MD5 of the samples with the signature in STREAMINFO (RFC 9639
    /// section 8.2: the samples as signed little-endian integers of whole bytes, interleaved). The read position is
    /// restored afterwards. The decode cost is that of playing the file once.
    /// </summary>
    /// <param name="cancellationToken">Observed between frames.</param>
    /// <returns>True when the hash matches, false when it does not (or the sample count is wrong), null when the file stores no signature.</returns>
    /// <exception cref="NotSupportedException">The source is not seekable.</exception>
    /// <exception cref="AudioFormatException">A frame is invalid.</exception>
    public bool? VerifyMd5(CancellationToken cancellationToken = default)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        if (!_si.HasMd5) return null;
        if (!CanSeek) throw new NotSupportedException("MD5 verification needs a seekable source.");
        long saved = PositionFrames;
        try
        {
            SeekCore(0);
            using var md5 = IncrementalHash.CreateHash(HashAlgorithmName.MD5);
            int bytes = (_si.BitsPerSample + 7) / 8;
            int ch = _si.Channels;
            var chunk = new byte[4096 * bytes * ch];
            long count = 0;
            while (_blockLen > 0)
            {
                cancellationToken.ThrowIfCancellationRequested();
                int pos = 0;
                while (pos < _blockLen)
                {
                    int take = Math.Min(4096, _blockLen - pos);
                    int o = 0;
                    for (int i = 0; i < take; i++)
                        for (int c = 0; c < ch; c++)
                        {
                            int v = _fd.Channels[c][pos + i];
                            for (int b = 0; b < bytes; b++) chunk[o++] = (byte)(v >> (8 * b));
                        }
                    md5.AppendData(chunk, 0, o);
                    pos += take;
                }
                count += _blockLen;
                if (!NextFrame()) break;
            }
            if (_si.TotalSamples > 0 && count != _si.TotalSamples) return false;
            return md5.GetHashAndReset().AsSpan().SequenceEqual(_si.Md5);
        }
        finally
        {
            SeekCore(saved);
        }
    }

    /// <inheritdoc />
    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        if (_packets is not null) _packets.Dispose();
        else if (!_leaveOpen) _stream!.Dispose();
    }
}
