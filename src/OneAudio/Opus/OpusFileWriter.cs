using System.Buffers.Binary;
using System.Text;
using OneAudio.Ogg;

namespace OneAudio.Opus;

/// <summary>Settings of an <see cref="OpusFileWriter"/>.</summary>
public sealed record OpusFileWriterSettings
{
    /// <summary>Sample rate of the PCM passed to the writer, 3000 to 768000 Hz (48000 needs no resampling). Default 48000.</summary>
    public int InputSampleRate { get; init; } = 48000;

    /// <summary>
    /// Channels, 1 to 8. One and two channels use channel mapping family 0; more use family 1, with input in the
    /// library's standard speaker order (the order <see cref="OneAudio.AudioFormat"/> uses; the order of a WAV file).
    /// </summary>
    public int Channels { get; init; } = 2;

    /// <summary>Encoder settings. Channels and sample rate are taken from this writer's settings, not from here.</summary>
    public OpusEncoderSettings Encoder { get; init; } = new();

    /// <summary>Packet duration in milliseconds: 2.5, 5, 10, 20, 40 or 60. Default 20.</summary>
    public double FrameMilliseconds { get; init; } = 20;

    /// <summary>The vendor string of the OpusTags header. Default "RecursiveAI.OneAudio".</summary>
    public string Vendor { get; init; } = "RecursiveAI.OneAudio";

    /// <summary>User comments as "NAME=value" pairs for the OpusTags header. Default none.</summary>
    public IReadOnlyList<string> Comments { get; init; } = [];

    /// <summary>The bitstream serial number. Default 1, which keeps files reproducible.</summary>
    public uint SerialNumber { get; init; } = 1;

    /// <summary>Quality of the resampler used when the input is not 48 kHz. Default <see cref="ResamplerQuality.Polyphase"/>.</summary>
    public ResamplerQuality ResamplerQuality { get; init; } = ResamplerQuality.Polyphase;
}

/// <summary>
/// Writes an Ogg Opus file (RFC 7845): OpusHead with the encoder's pre-skip, OpusTags, then one Opus packet per frame in
/// pages of at most one second. The final page's granule position trims the zero padding that completes the last packet, so the
/// decoded length equals the number of 48 kHz samples written exactly. Input that is not 48 kHz goes through the library's
/// polyphase resampler first; the file's input-rate field records the original rate.
/// </summary>
/// <remarks>
/// After construction <see cref="Write"/> allocates nothing. Output is deterministic for identical input and settings.
/// One file holds one logical stream (no chaining). Not thread safe.
/// </remarks>
public sealed class OpusFileWriter : IDisposable
{
    private const int MaxPageSegments = 255;
    private const long MaxPageGranules = 48000;

    private readonly Stream _out;
    private readonly bool _leaveOpen;
    private readonly OpusMultistreamEncoder _enc;
    private readonly int _channels;
    private readonly int _frameSize;
    private readonly int _preSkip;
    private readonly IResampler? _resampler;
    private readonly float[] _resampled;
    private readonly float[] _stage;
    private readonly float[] _reorderBuf;
    private readonly int[] _toVorbis;
    private readonly byte[] _packet = new byte[1276 * 8];
    private readonly byte[] _page = new byte[27 + MaxPageSegments + MaxPageSegments * 255];
    private readonly byte[] _lacing = new byte[MaxPageSegments];
    private readonly uint _serial;
    private int _bodyLen;
    private int _segments;
    private int _staged;
    private long _pageGranule;       // granule position at the end of the last packet put on the current page
    private long _pageStartGranule;
    private long _samplesIn48;       // valid samples at 48 kHz fed to the encoder, including resampled ones
    private long _packetsGranule;    // granules covered by packets encoded so far
    private uint _pageSeq;
    private bool _finished;
    private bool _disposed;

    /// <summary>Starts a file: validates the settings and writes the two header pages.</summary>
    /// <param name="output">Destination; must be writable.</param>
    /// <param name="settings">Writer settings.</param>
    /// <param name="leaveOpen">Keep <paramref name="output"/> open when the writer is disposed.</param>
    /// <exception cref="ArgumentException">A setting is invalid.</exception>
    /// <exception cref="ArgumentOutOfRangeException">A setting is out of range.</exception>
    public OpusFileWriter(Stream output, OpusFileWriterSettings settings, bool leaveOpen = false)
    {
        ArgumentNullException.ThrowIfNull(output);
        ArgumentNullException.ThrowIfNull(settings);
        if (!output.CanWrite) throw new ArgumentException("The stream is not writable.", nameof(output));
        if (settings.Channels is < 1 or > 8)
            throw new ArgumentOutOfRangeException(nameof(settings), settings.Channels, "Channels must be 1 to 8.");
        if (settings.InputSampleRate is < 3000 or > 768000)
            throw new ArgumentOutOfRangeException(nameof(settings), settings.InputSampleRate, "InputSampleRate must be 3000 to 768000.");
        _frameSize = settings.FrameMilliseconds switch
        {
            2.5 => 120,
            5 => 240,
            10 => 480,
            20 => 960,
            40 => 1920,
            60 => 2880,
            _ => throw new ArgumentOutOfRangeException(nameof(settings), settings.FrameMilliseconds, "FrameMilliseconds must be 2.5, 5, 10, 20, 40 or 60."),
        };
        ArgumentNullException.ThrowIfNull(settings.Encoder);
        ArgumentNullException.ThrowIfNull(settings.Comments);
        _out = output;
        _leaveOpen = leaveOpen;
        _channels = settings.Channels;
        _serial = settings.SerialNumber;
        _enc = new OpusMultistreamEncoder(settings.Encoder with { Channels = _channels, SampleRate = 48000 });
        _preSkip = _enc.LookaheadSamples;
        _stage = new float[_frameSize * _channels];
        _toVorbis = BuildOrder(_channels);
        _reorderBuf = new float[4096 * _channels];
        if (settings.InputSampleRate != 48000)
        {
            _resampler = AudioResampler.Create(settings.InputSampleRate, 48000, _channels, settings.ResamplerQuality);
            _resampled = new float[_resampler.MaxOutputFrames(4096) * _channels + 64 * _channels];
        }
        else _resampled = [];

        var head = new byte[19 + (_channels > 2 ? 2 + _channels : 0)];
        "OpusHead"u8.CopyTo(head);
        head[8] = 1;
        head[9] = (byte)_channels;
        BinaryPrimitives.WriteUInt16LittleEndian(head.AsSpan(10), (ushort)_preSkip);
        BinaryPrimitives.WriteUInt32LittleEndian(head.AsSpan(12), (uint)settings.InputSampleRate);
        BinaryPrimitives.WriteInt16LittleEndian(head.AsSpan(16), 0);
        if (_channels > 2)
        {
            head[18] = 1;
            head[19] = (byte)_enc.Streams;
            head[20] = (byte)_enc.CoupledStreams;
            _enc.ChannelMapping.CopyTo(head.AsSpan(21));
        }
        WriteWholePacketPage(head, 0, 0x02);

        var vendor = Encoding.UTF8.GetBytes(settings.Vendor);
        var comments = new List<byte[]>();
        long size = 8 + 4 + vendor.Length + 4;
        foreach (var c in settings.Comments)
        {
            var b = Encoding.UTF8.GetBytes(c);
            comments.Add(b);
            size += 4 + b.Length;
        }
        if (size > 120_000) throw new ArgumentException("The comments are too large.", nameof(settings));
        var tags = new byte[size];
        "OpusTags"u8.CopyTo(tags);
        int p = 8;
        BinaryPrimitives.WriteUInt32LittleEndian(tags.AsSpan(p), (uint)vendor.Length);
        vendor.CopyTo(tags.AsSpan(p + 4));
        p += 4 + vendor.Length;
        BinaryPrimitives.WriteUInt32LittleEndian(tags.AsSpan(p), (uint)comments.Count);
        p += 4;
        foreach (var b in comments)
        {
            BinaryPrimitives.WriteUInt32LittleEndian(tags.AsSpan(p), (uint)b.Length);
            b.CopyTo(tags.AsSpan(p + 4));
            p += 4 + b.Length;
        }
        WriteLargePacketPages(tags);
    }

    /// <summary>Encoder look-ahead in samples at 48 kHz; the pre-skip written to the file.</summary>
    public int PreSkip => _preSkip;

    /// <summary>Number of 48 kHz sample frames written so far (the decoded length of the file once it is finished).</summary>
    public long FramesWritten => _samplesIn48;

    // Maps each position in the Vorbis channel order to the input channel (standard speaker order) holding it.
    private static int[] BuildOrder(int n)
    {
        var map = new int[n];
        if (n <= 2)
        {
            for (int i = 0; i < n; i++) map[i] = i;
            return map;
        }
        uint[] masks = VorbisMasks(n);
        for (int c = 0; c < n; c++)
        {
            int slot = 0;
            for (int o = 0; o < n; o++)
                if (masks[o] < masks[c]) slot++;
            map[c] = slot;
        }
        return map;
    }

    private static uint[] VorbisMasks(int n)
    {
        const uint fl = 0x1, fr = 0x2, fc = 0x4, lfe = 0x8, bl = 0x10, br = 0x20, bc = 0x100, sl = 0x200, sr = 0x400;
        return n switch
        {
            3 => [fl, fc, fr],
            4 => [fl, fr, bl, br],
            5 => [fl, fc, fr, bl, br],
            6 => [fl, fc, fr, bl, br, lfe],
            7 => [fl, fc, fr, sl, sr, bc, lfe],
            _ => [fl, fc, fr, sl, sr, bl, br, lfe],
        };
    }

    /// <summary>Appends audio.</summary>
    /// <param name="pcm">Interleaved samples at the input rate, full scale ±1.0; a multiple of the channel count.</param>
    /// <exception cref="ArgumentException">The length is not a multiple of the channel count.</exception>
    /// <exception cref="InvalidOperationException">The writer is finished.</exception>
    /// <exception cref="IOException">The stream failed.</exception>
    public void Write(ReadOnlySpan<float> pcm)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        if (_finished) throw new InvalidOperationException("The file is already finished.");
        if (pcm.Length % _channels != 0) throw new ArgumentException("pcm must hold whole frames.", nameof(pcm));
        int blockFrames = _reorderBuf.Length / _channels;
        while (!pcm.IsEmpty)
        {
            int frames = Math.Min(blockFrames, pcm.Length / _channels);
            var block = pcm.Slice(0, frames * _channels);
            pcm = pcm.Slice(frames * _channels);
            if (_channels > 2)
            {
                for (int i = 0; i < frames; i++)
                    for (int c = 0; c < _channels; c++)
                        _reorderBuf[i * _channels + c] = block[i * _channels + _toVorbis[c]];
                block = _reorderBuf.AsSpan(0, frames * _channels);
            }
            if (_resampler is null) Push(block);
            else Resample(block);
        }
    }

    private void Resample(ReadOnlySpan<float> block)
    {
        while (!block.IsEmpty)
        {
            _resampler!.Process(block, _resampled, out int used, out int produced);
            if (produced > 0) Push(_resampled.AsSpan(0, produced * _channels));
            block = block.Slice(used * _channels);
            if (used == 0 && produced == 0) break;
        }
    }

    private void Push(ReadOnlySpan<float> samples)
    {
        _samplesIn48 += samples.Length / _channels;
        PushRaw(samples);
    }

    private void PushRaw(ReadOnlySpan<float> samples)
    {
        int frameLen = _frameSize * _channels;
        while (!samples.IsEmpty)
        {
            int take = Math.Min(frameLen - _staged, samples.Length);
            samples.Slice(0, take).CopyTo(_stage.AsSpan(_staged));
            _staged += take;
            samples = samples.Slice(take);
            if (_staged == frameLen)
            {
                EncodeStaged();
                _staged = 0;
            }
        }
    }

    private void EncodeStaged()
    {
        int n = _enc.Encode(_stage, _frameSize, _packet);
        _packetsGranule += _frameSize;
        AddPacket(_packet.AsSpan(0, n), _packetsGranule, false);
    }

    /// <summary>Flushes the encoder and writes the last page. Further writes throw. Called by <see cref="Dispose"/>.</summary>
    /// <exception cref="IOException">The stream failed.</exception>
    public void Finish()
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        if (_finished) return;
        _finished = true;
        if (_resampler is not null)
        {
            int n;
            while ((n = _resampler.Flush(_resampled)) > 0)
            {
                Push(_resampled.AsSpan(0, n * _channels));
            }
        }
        // Zero padding completes the last packet and pushes out the encoder's look-ahead; the end granule trims it.
        long needed = _preSkip + _samplesIn48;
        long packets = (needed + _frameSize - 1) / _frameSize;
        long done = _packetsGranule / _frameSize;
        Array.Clear(_stage, _staged, _stage.Length - _staged);
        if (_staged > 0 || done < packets)
        {
            if (_staged > 0)
            {
                _staged = 0;
                EncodeStaged();
                done++;
            }
            Array.Clear(_stage);
            for (; done < packets; done++) EncodeStaged();
        }
        long endGranule = _preSkip + _samplesIn48;
        AddPacket(default, endGranule, true);
        _out.Flush();
    }

    // ---- Ogg page writing -------------------------------------------------------------------------

    private void AddPacket(ReadOnlySpan<byte> packet, long granuleAfter, bool final)
    {
        if (final)
        {
            // The end of stream: the last page carries the trimmed granule position.
            if (_segments == 0 && _bodyLen == 0) WriteEmptyEos(granuleAfter);
            else FlushPage(granuleAfter, 0x04);
            return;
        }
        int needed = packet.Length / 255 + 1;
        if (_segments + needed > MaxPageSegments || (_segments > 0 && granuleAfter - _pageStartGranule > MaxPageGranules))
            FlushPage(_pageGranule, 0x00);
        if (_segments == 0) _pageStartGranule = _packetsGranule - _frameSize;
        packet.CopyTo(_page.AsSpan(27 + MaxPageSegments + _bodyLen));
        _bodyLen += packet.Length;
        int rest = packet.Length;
        while (rest >= 255)
        {
            _lacing[_segments++] = 255;
            rest -= 255;
        }
        _lacing[_segments++] = (byte)rest;
        _pageGranule = granuleAfter;
    }

    private void WriteEmptyEos(long granule)
    {
        // Every packet is already out; mark the previous page's successor as an empty end-of-stream page.
        WritePage(ReadOnlySpan<byte>.Empty, ReadOnlySpan<byte>.Empty, granule, 0x04);
    }

    private void FlushPage(long granule, byte flags)
    {
        if (_segments == 0) return;
        WritePage(_lacing.AsSpan(0, _segments), _page.AsSpan(27 + MaxPageSegments, _bodyLen), granule, flags);
        _segments = 0;
        _bodyLen = 0;
    }

    private void WriteWholePacketPage(ReadOnlySpan<byte> packet, long granule, byte flags)
    {
        Span<byte> lacing = stackalloc byte[MaxPageSegments];
        int n = 0;
        int rest = packet.Length;
        while (rest >= 255) { lacing[n++] = 255; rest -= 255; }
        lacing[n++] = (byte)rest;
        WritePage(lacing.Slice(0, n), packet, granule, flags);
    }

    // The tags may need several pages when they are large.
    private void WriteLargePacketPages(byte[] packet)
    {
        int pos = 0;
        bool first = true;
        Span<byte> lacing = stackalloc byte[MaxPageSegments];
        while (true)
        {
            int segs = 0;
            int take = 0;
            int remaining = packet.Length - pos;
            while (segs < MaxPageSegments && remaining - take >= 255) { lacing[segs++] = 255; take += 255; }
            bool complete = false;
            if (segs < MaxPageSegments) { lacing[segs++] = (byte)(remaining - take); take = remaining; complete = true; }
            WritePage(lacing.Slice(0, segs), packet.AsSpan(pos, take), complete ? 0 : -1, (byte)(first ? 0 : 0x01));
            pos += take;
            first = false;
            if (complete) break;
        }
    }

    private void WritePage(ReadOnlySpan<byte> lacing, ReadOnlySpan<byte> body, long granule, byte flags)
    {
        // Pages are assembled in a scratch area in front of the body buffer when the body is already in place.
        Span<byte> header = stackalloc byte[27 + MaxPageSegments];
        "OggS"u8.CopyTo(header);
        header[4] = 0;
        header[5] = flags;
        BinaryPrimitives.WriteInt64LittleEndian(header.Slice(6), granule);
        BinaryPrimitives.WriteUInt32LittleEndian(header.Slice(14), _serial);
        BinaryPrimitives.WriteUInt32LittleEndian(header.Slice(18), _pageSeq++);
        header[22] = header[23] = header[24] = header[25] = 0;
        header[26] = (byte)lacing.Length;
        lacing.CopyTo(header.Slice(27));
        int hlen = 27 + lacing.Length;
        uint crc = OggCrc.Update(0, header.Slice(0, hlen));
        crc = OggCrc.Update(crc, body);
        BinaryPrimitives.WriteUInt32LittleEndian(header.Slice(22), crc);
        _out.Write(header.Slice(0, hlen));
        _out.Write(body);
    }

    /// <summary>Finishes the file if needed and releases the stream unless it was left open.</summary>
    public void Dispose()
    {
        if (_disposed) return;
        try
        {
            if (!_finished) Finish();
        }
        finally
        {
            _disposed = true;
            if (!_leaveOpen) _out.Dispose();
        }
    }
}
