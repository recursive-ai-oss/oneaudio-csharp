using OneAudio.Amr;
using Xunit;

namespace OneAudio.Tests;

/// <summary>Public AMR frame decoders and the packet-stream codecs: errors, allocation, seeking.</summary>
public class AmrApiTests
{
    private sealed class ListPacketSource(List<byte[]> frames, int samplesPerFrame, CodecId codec, bool seekable = true) : IPacketSource
    {
        private int _next;
        public CodecId Codec => codec;
        public long LengthFrames => (long)frames.Count * samplesPerFrame;
        public bool CanSeek => seekable;
        public bool Disposed { get; private set; }

        public bool TryReadPacket(out PacketView packet)
        {
            if (_next >= frames.Count) { packet = default; return false; }
            packet = new PacketView(frames[_next++], -1, false);
            return true;
        }

        public void SeekToFrame(long frame, out long startFrame)
        {
            if (!seekable) throw new NotSupportedException();
            _next = (int)Math.Min(frame / samplesPerFrame, frames.Count);
            startFrame = (long)_next * samplesPerFrame;
        }

        public void Dispose() => Disposed = true;
    }

    private static readonly int[] NbBytes = [12, 13, 15, 17, 19, 20, 26, 31, 5];
    private static readonly int[] WbBytes = [17, 23, 32, 36, 40, 46, 50, 58, 60, 5];

    /// <summary>Deterministic frames with random payload bits: every mode, SID frames, no-data and damaged frames.</summary>
    private static List<byte[]> RandomFrames(bool wide, int count, uint seed)
    {
        var list = new List<byte[]>();
        int[] bytes = wide ? WbBytes : NbBytes;
        for (int i = 0; i < count; i++)
        {
            seed = seed * 1664525u + 1013904223u;
            int r = (int)(seed >> 16);
            int ft = r % (bytes.Length + 2);
            if (ft >= bytes.Length) ft = 15;
            bool q = (r >> 8) % 7 != 0;
            var f = new byte[ft == 15 ? 1 : 1 + bytes[ft]];
            f[0] = (byte)((ft << 3) | (q ? 4 : 0));
            for (int k = 1; k < f.Length; k++)
            {
                seed = seed * 1664525u + 1013904223u;
                f[k] = (byte)(seed >> 20);
            }
            list.Add(f);
        }
        return list;
    }

    [Fact]
    public void RandomFramesNeverThrowOrOverrun()
    {
        // Garbage payloads reach every quantizer index and pulse position: nothing may escape.
        var nb = new AmrNbDecoder();
        var wb = new AmrWbDecoder();
        var a = new short[160];
        var b = new short[320];
        foreach (byte[] f in RandomFrames(false, 20000, 1)) nb.Decode(f, a);
        foreach (byte[] f in RandomFrames(true, 20000, 2)) wb.Decode(f, b);
    }

    [Theory]
    [InlineData(9)]
    [InlineData(11)]
    [InlineData(14)]
    public void NarrowbandReservedTypeIsRejected(int ft)
    {
        var d = new AmrNbDecoder();
        Assert.Throws<AudioFormatException>(() => d.Decode(new byte[] { (byte)((ft << 3) | 4), 0, 0, 0, 0, 0 }, new short[160]));
    }

    [Theory]
    [InlineData(10)]
    [InlineData(13)]
    public void WidebandReservedTypeIsRejected(int ft)
    {
        var d = new AmrWbDecoder();
        Assert.Throws<AudioFormatException>(() => d.Decode(new byte[] { (byte)((ft << 3) | 4), 0, 0, 0, 0, 0 }, new short[320]));
    }

    [Fact]
    public void WrongLengthAndEmptyFramesAreRejected()
    {
        var nb = new AmrNbDecoder();
        var wb = new AmrWbDecoder();
        Assert.Throws<AudioFormatException>(() => nb.Decode(ReadOnlySpan<byte>.Empty, new short[160]));
        Assert.Throws<AudioFormatException>(() => nb.Decode(new byte[12], new short[160])); // mode 0 needs 12 payload bytes
        Assert.Throws<AudioFormatException>(() => nb.Decode(new byte[] { 15 << 3 | 4, 0 }, new short[160]));
        Assert.Throws<AudioFormatException>(() => wb.Decode(new byte[3], new short[320]));
        Assert.Throws<AudioFormatException>(() => wb.Decode(new byte[] { 14 << 3 | 4, 1 }, new short[320]));
        Assert.Throws<ArgumentException>(() => nb.Decode(new byte[] { 15 << 3 | 4 }, new short[159]));
        Assert.Throws<ArgumentException>(() => wb.Decode(new byte[] { 15 << 3 | 4 }, new float[319]));
    }

    [Fact]
    public void DecodeAllocatesNothingAfterTheFirstFrames()
    {
        foreach (bool wide in new[] { false, true })
        {
            var frames = RandomFrames(wide, 400, 3);
            var src = new ListPacketSource(frames, wide ? 320 : 160, wide ? CodecId.AmrWb : CodecId.AmrNb);
            using IAudioDecoder dec = AmrNbOrWb(wide).CreateDecoder(src, AudioLimits.Default);
            var buf = new float[wide ? 320 : 160];
            for (int i = 0; i < 100; i++) dec.Read(buf); // warm-up
            long before = GC.GetAllocatedBytesForCurrentThread();
            int collections = GC.CollectionCount(0);
            int short_reads = 0; // plain comparison: the assertion library itself allocates
            for (int i = 0; i < 300; i++) if (dec.Read(buf) != buf.Length) short_reads++;
            long allocated = GC.GetAllocatedBytesForCurrentThread() - before;
            Assert.Equal(0, short_reads);
            Assert.Equal(0, allocated);
            Assert.Equal(collections, GC.CollectionCount(0));
        }
    }

    [Fact]
    public void FloatOutputIsTheShortOutputOver32768()
    {
        var frames = RandomFrames(true, 30, 4);
        var a = new AmrWbDecoder();
        var b = new AmrWbDecoder();
        var s = new short[320];
        var f = new float[320];
        foreach (byte[] fr in frames)
        {
            a.Decode(fr, s);
            b.Decode(fr, f);
            for (int i = 0; i < 320; i++) Assert.Equal(s[i] / 32768f, f[i]);
        }
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void SeekWithinPrerollIsExactAndFarSeekConverges(bool wide)
    {
        int n = wide ? 320 : 160;
        var frames = new List<byte[]>();
        for (int i = 0; i < 120; i++) frames.Add(MakeSpeech(wide, i));
        var all = new float[frames.Count * n];
        using (var full = AmrNbOrWb(wide).CreateDecoder(new ListPacketSource(frames, n, Id(wide)), AudioLimits.Default))
            Assert.Equal(frames.Count * n, full.Read(all));

        using var dec = AmrNbOrWb(wide).CreateDecoder(new ListPacketSource(frames, n, Id(wide)), AudioLimits.Default);
        Assert.True(dec.CanSeek);
        Assert.Equal(frames.Count * n, dec.LengthFrames);
        var buf = new float[3 * n];

        // Within the 10-frame preroll the restart is at frame 0, so the audio is identical to a sequential decode.
        long near = 5 * n + 17;
        dec.Seek(near);
        Assert.Equal(near, dec.PositionFrames);
        Assert.Equal(buf.Length, dec.Read(buf));
        for (int i = 0; i < buf.Length; i++) Assert.Equal(all[near + i], buf[i]);
        Assert.Equal(near + buf.Length, dec.PositionFrames);

        // Far seeks restart from a converged-but-not-identical state: the position is exact, the audio close.
        long far = 90 * n + 33;
        dec.Seek(far);
        Assert.Equal(far, dec.PositionFrames);
        Assert.Equal(buf.Length, dec.Read(buf));
        double err = 0, sig = 0;
        for (int i = 0; i < buf.Length; i++)
        {
            err += Math.Pow(all[far + i] - buf[i], 2);
            sig += Math.Pow(all[far + i], 2);
        }
        Assert.True(err <= 0.5 * sig, $"seek error energy {err} vs signal {sig}");

        dec.Seek(frames.Count * n);
        Assert.Equal(0, dec.Read(buf));
        dec.Seek(0);
        Assert.Equal(buf.Length, dec.Read(buf));
        for (int i = 0; i < buf.Length; i++) Assert.Equal(all[i], buf[i]);
        Assert.Throws<ArgumentOutOfRangeException>(() => dec.Seek(-1));
        Assert.Throws<ArgumentOutOfRangeException>(() => dec.Seek((frames.Count + 1) * n));
    }

    private static IAudioCodec AmrNbOrWb(bool wide) => wide ? AmrWbCodec.Instance : AmrNbCodec.Instance;
    private static CodecId Id(bool wide) => wide ? CodecId.AmrWb : CodecId.AmrNb;

    private static byte[] MakeSpeech(bool wide, int i)
    {
        int ft = wide ? 2 : 7; // 12.65 kbit/s / 12.2 kbit/s
        var f = new byte[1 + (wide ? WbBytes[ft] : NbBytes[ft])];
        f[0] = (byte)((ft << 3) | 4);
        uint s = (uint)i * 2654435761u + 1;
        for (int k = 1; k < f.Length; k++)
        {
            s = s * 1664525u + 1013904223u;
            f[k] = (byte)(s >> 20);
        }
        return f;
    }

    [Fact]
    public void NonSeekableSourceCannotSeekAndDisposeReachesTheSource()
    {
        var src = new ListPacketSource(RandomFrames(false, 4, 6), 160, CodecId.AmrNb, seekable: false);
        var dec = AmrNbCodec.Instance.CreateDecoder(src, AudioLimits.Default);
        Assert.False(dec.CanSeek);
        Assert.Throws<NotSupportedException>(() => dec.Seek(0));
        Assert.Throws<NotSupportedException>(() => dec.ReadInteger(new int[160]));
        Assert.Equal(new AudioFormat(8000, 1, ChannelMask.FrontCenter), dec.Format);
        dec.Dispose();
        Assert.True(src.Disposed);
        Assert.Throws<ObjectDisposedException>(() => dec.Read(new float[160]));
    }

    [Fact]
    public void MalformedPacketSurfacesAsFormatError()
    {
        var frames = new List<byte[]> { new byte[] { 12 << 3 | 4 } };
        using var dec = AmrNbCodec.Instance.CreateDecoder(new ListPacketSource(frames, 160, CodecId.AmrNb), AudioLimits.Default);
        Assert.Throws<AudioFormatException>(() => dec.Read(new float[160]));
    }

    [Fact]
    public void RegistryFindsBothCodecs()
    {
        var reg = new AudioCodecRegistry().AddAmrNb().AddAmrWb();
        Assert.Equal(CodecId.AmrNb, reg.GetCodec(CodecId.AmrNb).Id);
        Assert.Equal(CodecId.AmrWb, reg.GetCodec(CodecId.AmrWb).Id);
    }
}
