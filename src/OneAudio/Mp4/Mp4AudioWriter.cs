using System.Buffers.Binary;
using System.Text;

namespace OneAudio.Mp4;

/// <summary>
/// Writes a single-audio-track MP4 / M4A file (ISO/IEC 14496-12, with the AAC sample description of ISO/IEC 14496-14 and the
/// FLAC-in-ISOBMFF sample description): <c>ftyp</c>, <c>moov</c> with the track's sample tables, and the media data.
/// Samples are written to the stream as they arrive; only their sizes and durations are kept in memory (about 4 bytes per sample),
/// and the output stream must be seekable to patch the size of the data box. By default <c>moov</c> is moved in front of the media
/// data when the stream is also readable (<see cref="FastStart"/>); otherwise, or with FastStart false, it follows the data.
/// </summary>
/// <remarks>
/// <para>For AAC the track gets an edit list that skips <c>primingFrames</c> frames at the start and plays <c>validFrames</c> frames
/// (movie and media time scales both equal the sampling rate), which is what players use for gapless playback and what
/// <see cref="Mp4PacketSource"/> reads back as <see cref="IPacketSource.PrimingFrames"/> and the length. FLAC needs no edit list.</para>
/// <para>All samples are in one chunk; chunk offsets switch to 64 bits (<c>co64</c>) when they pass 4 GiB. Progressive playback
/// over HTTP needs <c>moov</c> before the media data, which <see cref="FastStart"/> (on by default) provides.</para>
/// Not thread-safe.
/// </remarks>
public sealed class Mp4AudioWriter : IDisposable
{
    private enum Kind { Aac, Flac }

    private readonly Stream _out;
    private readonly bool _leaveOpen;
    private readonly Kind _kind;
    private readonly int _rate, _channels, _bits, _priming;
    private readonly byte[] _asc;
    private readonly long _origin;
    private long _mdatStart;
    private long _dataBytes;
    private int[] _sizes = new int[1024];
    private int _count;
    // Run-length coded durations.
    private int[] _runCount = new int[16], _runDelta = new int[16];
    private int _runs;
    private long _totalTicks;
    private int _maxSample;
    private bool _finished, _disposed, _fastStart = true;

    private Mp4AudioWriter(Stream output, bool leaveOpen, Kind kind, int rate, int channels, int bits, byte[] asc, int priming)
    {
        ArgumentNullException.ThrowIfNull(output);
        if (!output.CanWrite || !output.CanSeek) throw new ArgumentException("The output stream must be writable and seekable.", nameof(output));
        if (rate < 1 || rate > 1_048_575) throw new ArgumentOutOfRangeException(nameof(rate));
        if (channels < 1 || channels > 8) throw new ArgumentOutOfRangeException(nameof(channels));
        _out = output;
        _leaveOpen = leaveOpen;
        _kind = kind;
        _rate = rate;
        _channels = channels;
        _bits = bits;
        _asc = asc;
        _priming = priming;
        _origin = output.Position;
        WriteHeader();
    }

    /// <summary>Starts an AAC track.</summary>
    /// <param name="output">A writable, seekable stream; writing starts at its current position.</param>
    /// <param name="sampleRate">Sampling rate in Hz (also the time scale).</param>
    /// <param name="channels">Channel count, 1 to 8.</param>
    /// <param name="audioSpecificConfig">The AudioSpecificConfig bytes for the <c>esds</c> box (<see cref="OneAudio.Aac.AacEncoder.AudioSpecificConfig"/>).</param>
    /// <param name="primingFrames">Frames at the start of the decoded stream that the player skips (<see cref="OneAudio.Aac.AacEncoder.PrimingFrames"/>); 0 for none.</param>
    /// <param name="leaveOpen">Keep <paramref name="output"/> open on dispose (default false).</param>
    /// <exception cref="ArgumentException">The stream cannot seek or the config is empty.</exception>
    public static Mp4AudioWriter CreateAac(Stream output, int sampleRate, int channels, ReadOnlyMemory<byte> audioSpecificConfig, int primingFrames, bool leaveOpen = false)
    {
        if (audioSpecificConfig.Length is < 2 or > 64) throw new ArgumentException("The AudioSpecificConfig must be 2 to 64 bytes.", nameof(audioSpecificConfig));
        ArgumentOutOfRangeException.ThrowIfNegative(primingFrames);
        return new Mp4AudioWriter(output, leaveOpen, Kind.Aac, sampleRate, channels, 16, audioSpecificConfig.ToArray(), primingFrames);
    }

    /// <summary>Starts a FLAC track. The <c>dfLa</c> box is written by <see cref="Finish"/>, once the final STREAMINFO is known.</summary>
    /// <param name="output">A writable, seekable stream; writing starts at its current position.</param>
    /// <param name="sampleRate">Sampling rate in Hz (also the time scale).</param>
    /// <param name="channels">Channel count, 1 to 8.</param>
    /// <param name="bitsPerSample">Bits per sample, 4 to 32 (recorded in the sample entry, capped at 32).</param>
    /// <param name="leaveOpen">Keep <paramref name="output"/> open on dispose (default false).</param>
    public static Mp4AudioWriter CreateFlac(Stream output, int sampleRate, int channels, int bitsPerSample, bool leaveOpen = false)
    {
        ArgumentOutOfRangeException.ThrowIfLessThan(bitsPerSample, 4);
        ArgumentOutOfRangeException.ThrowIfGreaterThan(bitsPerSample, 32);
        return new Mp4AudioWriter(output, leaveOpen, Kind.Flac, sampleRate, channels, bitsPerSample, [], 0);
    }

    /// <summary>
    /// Put <c>moov</c> before the media data (so a player can start while the file is still downloading). Default true, the same as
    /// <c>TranscodeOptions.FastStart</c>: a file that plays while it downloads is what almost every use wants. Set it
    /// before <see cref="Finish"/>; false keeps <c>moov</c> last and costs no extra pass. <see cref="Finish"/> shifts the media data forward by the size of <c>moov</c> inside the
    /// stream, in 1 MiB steps from the end, which costs one extra pass of reads and writes over the file and no extra memory;
    /// the stream must therefore be readable as well as writable and seekable. Offsets are written as <c>co64</c> when the data
    /// ends up past 4 GiB.
    /// On a stream that cannot be read, the default (never set by the caller) quietly falls back to <c>moov</c> last, and the
    /// getter still reads true; asking for true explicitly on such a stream throws, so a request is never ignored.
    /// </summary>
    /// <exception cref="InvalidOperationException">Set after <see cref="Finish"/>.</exception>
    /// <exception cref="ArgumentException">Set to true on a stream that cannot be read.</exception>
    public bool FastStart
    {
        get => _fastStart;
        set
        {
            if (_finished) throw new InvalidOperationException("The writer is finished.");
            if (value && !_out.CanRead) throw new ArgumentException("FastStart needs a stream that can also be read.", nameof(value));
            _fastStart = value;
        }
    }

    /// <summary>Samples added so far.</summary>
    public int SampleCount => _count;

    private void WriteHeader()
    {
        var ftyp = new BoxBuffer();
        int ft = ftyp.Begin("ftyp");
        ftyp.Fourcc(_kind == Kind.Aac ? "M4A " : "isom");
        ftyp.U32(0);
        if (_kind == Kind.Aac) ftyp.Fourcc("M4A ");
        ftyp.Fourcc("isom");
        ftyp.Fourcc("iso2");
        ftyp.Fourcc("mp42");
        ftyp.End(ft);
        _out.Write(ftyp.Span);
        // mdat with a 64-bit size so the data may pass 4 GiB; the size is patched in Finish.
        var head = new byte[16];
        BinaryPrimitives.WriteUInt32BigEndian(head, 1);
        "mdat"u8.CopyTo(head.AsSpan(4));
        _mdatStart = _out.Position;
        _out.Write(head);
    }

    /// <summary>Appends one sample (an AAC access unit or a FLAC frame).</summary>
    /// <param name="data">The sample bytes; must not be empty.</param>
    /// <param name="duration">Frames (samples per channel) the sample covers, in time-scale ticks: 1024 for AAC, the block size for FLAC.</param>
    /// <exception cref="InvalidOperationException">The writer is finished.</exception>
    public void AddSample(ReadOnlySpan<byte> data, int duration)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        if (_finished) throw new InvalidOperationException("The writer is finished.");
        if (data.IsEmpty) throw new ArgumentException("A sample must not be empty.", nameof(data));
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(duration);
        _out.Write(data);
        if (_count == _sizes.Length) Array.Resize(ref _sizes, _sizes.Length * 2);
        _sizes[_count++] = data.Length;
        _dataBytes += data.Length;
        _maxSample = Math.Max(_maxSample, data.Length);
        _totalTicks += duration;
        if (_runs > 0 && _runDelta[_runs - 1] == duration) _runCount[_runs - 1]++;
        else
        {
            if (_runs == _runDelta.Length)
            {
                Array.Resize(ref _runDelta, _runs * 2);
                Array.Resize(ref _runCount, _runs * 2);
            }
            _runDelta[_runs] = duration;
            _runCount[_runs++] = 1;
        }
    }

    /// <summary>
    /// Completes the file: patches the data box and writes <c>moov</c> (after the data, or before it with <see cref="FastStart"/>).
    /// The stream is left at the end of the file.
    /// </summary>
    /// <param name="validFrames">Frames of real audio, after the priming: the length the edit list plays (AAC only; for FLAC it is the media length and is used for the header).</param>
    /// <param name="flacStreamInfoBlock">FLAC only: the 38-byte metadata block (header and STREAMINFO) from <see cref="OneAudio.Flac.FlacPacketEncoder.GetStreamInfoBlock"/>, after its <c>Finish</c>.</param>
    /// <exception cref="ArgumentException">A FLAC track is missing its STREAMINFO block, or no sample was added.</exception>
    public void Finish(long validFrames, ReadOnlySpan<byte> flacStreamInfoBlock = default)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        if (_finished) return;
        if (_count == 0) throw new InvalidOperationException("No samples were added.");
        if (_kind == Kind.Flac && flacStreamInfoBlock.Length != 38) throw new ArgumentException("A FLAC track needs the 38-byte STREAMINFO block.", nameof(flacStreamInfoBlock));
        ArgumentOutOfRangeException.ThrowIfNegative(validFrames);
        _finished = true;
        // Fast start needs a readable stream; on a write-only one the default falls back to moov last.
        bool fast = _fastStart && _out.CanRead;

        long end = _out.Position;
        long dataOffset = _mdatStart + 16 - _origin;
        byte[] moov;
        if (!fast) moov = BuildMoov(validFrames, flacStreamInfoBlock, dataOffset);
        else
        {
            // The size of moov depends on whether the chunk offset needs 64 bits, and the offset depends on the size of moov, so
            // settle it by trying again with the size just learned (two rounds at most: a larger offset can only keep co64).
            moov = BuildMoov(validFrames, flacStreamInfoBlock, dataOffset);
            for (int round = 0; round < 3; round++)
            {
                var next = BuildMoov(validFrames, flacStreamInfoBlock, dataOffset + moov.Length);
                bool settled = next.Length == moov.Length;
                moov = next;
                if (settled) break;
            }
            ShiftForward(_mdatStart, end, moov.Length);
        }

        long mdat = _mdatStart + (fast ? moov.Length : 0);
        _out.Position = mdat + 8;
        Span<byte> size = stackalloc byte[8];
        BinaryPrimitives.WriteUInt64BigEndian(size, 16UL + (ulong)_dataBytes);
        _out.Write(size);
        if (fast)
        {
            _out.Position = _mdatStart;
            _out.Write(moov);
            _out.Position = mdat + 16 + _dataBytes;
        }
        else
        {
            _out.Position = end;
            _out.Write(moov);
        }
        _out.Flush();
    }

    // Moves the bytes [from, to) forward by distance, copying from the end so that overlapping ranges stay intact.
    private void ShiftForward(long from, long to, int distance)
    {
        var buffer = new byte[1 << 20];
        for (long hi = to; hi > from;)
        {
            int n = (int)Math.Min(buffer.Length, hi - from);
            long lo = hi - n;
            _out.Position = lo;
            _out.ReadExactly(buffer.AsSpan(0, n));
            _out.Position = lo + distance;
            _out.Write(buffer.AsSpan(0, n));
            hi = lo;
        }
    }

    private byte[] BuildMoov(long validFrames, ReadOnlySpan<byte> dfla, long dataOffset)
    {
        var b = new BoxBuffer();
        long duration = _totalTicks;
        // The movie uses the sampling rate as its time scale, so durations are frame counts.
        long playFrames = _kind == Kind.Aac ? validFrames : duration;

        int moov = b.Begin("moov");
        int mvhd = b.Begin("mvhd");
        b.U32(0); // version 0, flags 0
        b.U32(0); b.U32(0);                // creation, modification
        b.U32((uint)_rate);                // time scale
        b.U32((uint)Math.Min(playFrames, uint.MaxValue));
        b.U32(0x00010000);                 // rate 1.0
        b.U16(0x0100); b.U16(0);           // volume 1.0, reserved
        b.U32(0); b.U32(0);
        b.Matrix();
        for (int i = 0; i < 6; i++) b.U32(0); // pre_defined
        b.U32(2);                          // next track id
        b.End(mvhd);

        int trak = b.Begin("trak");
        int tkhd = b.Begin("tkhd");
        b.U32(0x00000003);                 // version 0, flags: enabled, in movie
        b.U32(0); b.U32(0);
        b.U32(1);                          // track id
        b.U32(0);
        b.U32((uint)Math.Min(playFrames, uint.MaxValue));
        b.U32(0); b.U32(0);
        b.U16(0); b.U16(0);                // layer, alternate group
        b.U16(0x0100); b.U16(0);           // volume
        b.Matrix();
        b.U32(0); b.U32(0);                // width, height
        b.End(tkhd);

        if (_kind == Kind.Aac)
        {
            int edts = b.Begin("edts");
            int elst = b.Begin("elst");
            b.U32(1 << 24);                // version 1: 64-bit times
            b.U32(1);
            b.U64((ulong)playFrames);
            b.U64((ulong)_priming);        // media_time: where playback starts inside the media
            b.U16(1); b.U16(0);            // media rate 1.0
            b.End(elst);
            b.End(edts);
        }

        int mdia = b.Begin("mdia");
        int mdhd = b.Begin("mdhd");
        b.U32(0);
        b.U32(0); b.U32(0);
        b.U32((uint)_rate);
        b.U32((uint)Math.Min(duration, uint.MaxValue));
        b.U16(0x55C4); b.U16(0);           // language "und"
        b.End(mdhd);
        int hdlr = b.Begin("hdlr");
        b.U32(0); b.U32(0);
        b.Fourcc("soun");
        b.U32(0); b.U32(0); b.U32(0);
        b.Bytes("SoundHandler\0"u8);
        b.End(hdlr);

        int minf = b.Begin("minf");
        int smhd = b.Begin("smhd");
        b.U32(0); b.U16(0); b.U16(0);
        b.End(smhd);
        int dinf = b.Begin("dinf");
        int dref = b.Begin("dref");
        b.U32(0); b.U32(1);
        int url = b.Begin("url ");
        b.U32(1);                          // self-contained
        b.End(url);
        b.End(dref);
        b.End(dinf);

        int stbl = b.Begin("stbl");
        int stsd = b.Begin("stsd");
        b.U32(0); b.U32(1);
        int entry = b.Begin(_kind == Kind.Aac ? "mp4a" : "fLaC");
        b.Zeros(6); b.U16(1);              // reserved, data reference index
        b.U32(0); b.U32(0);                // version 0 sound description, reserved
        b.U16((ushort)_channels);
        b.U16((ushort)(_kind == Kind.Aac ? 16 : _bits));
        b.U16(0); b.U16(0);
        b.U32((uint)(Math.Min(_rate, 65535) << 16));
        if (_kind == Kind.Aac) WriteEsds(b, duration);
        else
        {
            int dfLa = b.Begin("dfLa");
            b.U32(0);                      // version 0, flags 0
            b.Bytes(dfla);
            b.End(dfLa);
        }
        b.End(entry);
        b.End(stsd);

        int stts = b.Begin("stts");
        b.U32(0); b.U32((uint)_runs);
        for (int i = 0; i < _runs; i++) { b.U32((uint)_runCount[i]); b.U32((uint)_runDelta[i]); }
        b.End(stts);

        int stsc = b.Begin("stsc");
        b.U32(0); b.U32(1);
        b.U32(1); b.U32((uint)_count); b.U32(1);
        b.End(stsc);

        int stsz = b.Begin("stsz");
        b.U32(0); b.U32(0); b.U32((uint)_count);
        for (int i = 0; i < _count; i++) b.U32((uint)_sizes[i]);
        b.End(stsz);

        if (dataOffset <= uint.MaxValue)
        {
            int stco = b.Begin("stco");
            b.U32(0); b.U32(1); b.U32((uint)dataOffset);
            b.End(stco);
        }
        else
        {
            int co64 = b.Begin("co64");
            b.U32(0); b.U32(1); b.U64((ulong)dataOffset);
            b.End(co64);
        }
        b.End(stbl);
        b.End(minf);
        b.End(mdia);
        b.End(trak);
        if (_kind == Kind.Aac) WriteGaplessTag(b, validFrames);
        b.End(moov);
        return b.ToArray();
    }

    // The iTunes gapless tag: movie > udta > meta > ilst > '----' (mean, name, data), as players that ignore edit lists expect.
    // The text holds the delay, the end padding and the valid frame count in hexadecimal.
    private void WriteGaplessTag(BoxBuffer b, long validFrames)
    {
        long padding = Math.Max(0, _totalTicks - _priming - validFrames);
        string text = $" 00000000 {_priming:X8} {padding:X8} {validFrames:X16} 00000000 00000000 00000000 00000000 00000000 00000000 00000000 00000000";
        int udta = b.Begin("udta");
        int meta = b.Begin("meta");
        b.U32(0);
        int hdlr = b.Begin("hdlr");
        b.U32(0); b.U32(0);
        b.Fourcc("mdir");
        b.Fourcc("appl");
        b.U32(0); b.U32(0); b.U16(0);
        b.End(hdlr);
        int ilst = b.Begin("ilst");
        int item = b.Begin("----");
        int mean = b.Begin("mean");
        b.U32(0);
        b.Bytes("com.apple.iTunes"u8);
        b.End(mean);
        int name = b.Begin("name");
        b.U32(0);
        b.Bytes("iTunSMPB"u8);
        b.End(name);
        int data = b.Begin("data");
        b.U32(1); b.U32(0);                // type 1 (UTF-8), locale 0
        b.Bytes(Encoding.ASCII.GetBytes(text));
        b.End(data);
        b.End(item);
        b.End(ilst);
        b.End(meta);
        b.End(udta);
    }

    // ISO/IEC 14496-14 5.6: esds holds an ES_Descriptor > DecoderConfigDescriptor > DecoderSpecificInfo, plus an SLConfigDescriptor.
    private void WriteEsds(BoxBuffer b, long mediaTicks)
    {
        int esds = b.Begin("esds");
        b.U32(0);
        double seconds = mediaTicks / (double)_rate;
        uint avg = seconds > 0 ? (uint)Math.Min(_dataBytes * 8 / seconds, uint.MaxValue) : 0;
        uint max = (uint)Math.Min((long)_maxSample * 8 * _rate / 1024, uint.MaxValue);
        int decSpecific = 2 + _asc.Length;                 // tag + 1-byte length + payload
        int decoderConfig = 13 + decSpecific;
        int es = 3 + decoderConfig + 3;                    // ES_ID(2) + flags(1) + descriptors + SL (tag, length, 1 byte)
        b.Byte(0x03); b.Byte((byte)es);
        b.U16(0); b.Byte(0);                               // ES_ID, no dependency/URL/OCR
        b.Byte(0x04); b.Byte((byte)decoderConfig);
        b.Byte(0x40);                                      // objectTypeIndication: MPEG-4 audio
        b.Byte(0x15);                                      // streamType audio (5) << 2 | upstream 0 | reserved 1
        b.Byte((byte)(_maxSample >> 16)); b.U16((ushort)_maxSample); // bufferSizeDB (24 bits)
        b.U32(max); b.U32(avg);
        b.Byte(0x05); b.Byte((byte)_asc.Length);
        b.Bytes(_asc);
        b.Byte(0x06); b.Byte(1); b.Byte(0x02);             // SLConfigDescriptor: predefined 2 (MP4)
        b.End(esds);
    }

    /// <summary>Releases the stream unless it was left open. A writer disposed before <see cref="Finish"/> leaves an incomplete file (no <c>moov</c> box).</summary>
    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        if (!_leaveOpen) _out.Dispose();
    }

    // A growable buffer with nested box sizes patched on End.
    private sealed class BoxBuffer
    {
        private byte[] _buf = new byte[4096];
        private int _len;

        public ReadOnlySpan<byte> Span => _buf.AsSpan(0, _len);
        public byte[] ToArray() => _buf.AsSpan(0, _len).ToArray();

        private void Need(int n)
        {
            if (_len + n > _buf.Length) Array.Resize(ref _buf, Math.Max(_buf.Length * 2, _len + n));
        }

        public int Begin(string type)
        {
            int at = _len;
            U32(0);
            Fourcc(type);
            return at;
        }

        public void End(int at) => BinaryPrimitives.WriteUInt32BigEndian(_buf.AsSpan(at), (uint)(_len - at));
        public void Byte(byte v) { Need(1); _buf[_len++] = v; }
        public void U16(ushort v) { Need(2); BinaryPrimitives.WriteUInt16BigEndian(_buf.AsSpan(_len), v); _len += 2; }
        public void U32(uint v) { Need(4); BinaryPrimitives.WriteUInt32BigEndian(_buf.AsSpan(_len), v); _len += 4; }
        public void U64(ulong v) { Need(8); BinaryPrimitives.WriteUInt64BigEndian(_buf.AsSpan(_len), v); _len += 8; }
        public void Fourcc(string s) { Need(4); Encoding.ASCII.GetBytes(s, _buf.AsSpan(_len, 4)); _len += 4; }
        public void Bytes(ReadOnlySpan<byte> d) { Need(d.Length); d.CopyTo(_buf.AsSpan(_len)); _len += d.Length; }
        public void Zeros(int n) { Need(n); _buf.AsSpan(_len, n).Clear(); _len += n; }

        // The unity transformation matrix (ISO/IEC 14496-12 8.2.2).
        public void Matrix()
        {
            U32(0x00010000); U32(0); U32(0);
            U32(0); U32(0x00010000); U32(0);
            U32(0); U32(0); U32(0x40000000);
        }
    }
}
