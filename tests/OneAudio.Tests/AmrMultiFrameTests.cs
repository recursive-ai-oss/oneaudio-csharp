using System.Buffers.Binary;
using OneAudio.Mp4;
using Xunit;

namespace OneAudio.Tests;

/// <summary>
/// 3GPP TS 26.244 8.3 lets one <c>samr</c>/<c>sawb</c> sample hold several storage-format frames (damr's frames-per-sample).
/// These tests build 3GP files from real TS 26.074 / 26.174 frames, several per sample, and expect the reference output.
/// </summary>
public partial class AmrFileTests
{
    private static byte[] Box(string type, params byte[][] parts)
    {
        int size = 8 + parts.Sum(p => p.Length);
        var b = new byte[size];
        BinaryPrimitives.WriteInt32BigEndian(b, size);
        for (int i = 0; i < 4; i++) b[4 + i] = (byte)type[i];
        int o = 8;
        foreach (var p in parts) { p.CopyTo(b, o); o += p.Length; }
        return b;
    }

    private static byte[] U32(params uint[] v)
    {
        var b = new byte[v.Length * 4];
        for (int i = 0; i < v.Length; i++) BinaryPrimitives.WriteUInt32BigEndian(b.AsSpan(i * 4), v[i]);
        return b;
    }

    /// <summary>A minimal audio-only 3GP: sample i holds <c>groups[i]</c> frames, and damr advertises <paramref name="advertised"/> per sample.</summary>
    internal static byte[] Build3gp(bool wide, List<byte[]> frames, int[] groups, int advertised)
    {
        int rate = wide ? 16000 : 8000, n = wide ? 320 : 160;
        var samples = new List<byte[]>();
        int at = 0;
        foreach (int g in groups)
        {
            samples.Add([.. frames.Skip(at).Take(g).SelectMany(f => f)]);
            at += g;
        }
        Assert.Equal(frames.Count, at);
        byte[] mdat = [.. samples.SelectMany(s => s)];
        byte[] ftyp = Box("ftyp", "3gp4"u8.ToArray(), U32(0), "3gp4"u8.ToArray());
        uint dataOffset = (uint)(ftyp.Length + 8);

        byte[] damr = Box("damr", "TEST"u8.ToArray(), [0, 0x81, 0xFF, 0, (byte)advertised]);
        byte[] entry = Box(wide ? "sawb" : "samr", new byte[6], [0, 1], new byte[8], [0, 1, 0, 16], U32(0), U32((uint)rate << 16), damr);
        byte[] stsd = Box("stsd", U32(0, 1), entry);
        var stts = new List<uint> { 0, (uint)groups.Length };
        // One entry per distinct run; samples with different frame counts have different durations.
        var runs = new List<(uint count, uint delta)>();
        foreach (int g in groups)
        {
            uint d = (uint)(g * n);
            if (runs.Count > 0 && runs[^1].delta == d) runs[^1] = (runs[^1].count + 1, d);
            else runs.Add((1, d));
        }
        stts = [0, (uint)runs.Count];
        foreach (var (c, d) in runs) { stts.Add(c); stts.Add(d); }
        byte[] sttsBox = Box("stts", U32([.. stts]));
        byte[] stsc = Box("stsc", U32(0, 1, 1, (uint)groups.Length, 1));
        byte[] stsz = Box("stsz", U32([0, 0, (uint)samples.Count, .. samples.Select(s => (uint)s.Length)]));
        byte[] stco = Box("stco", U32(0, 1, dataOffset));
        byte[] stbl = Box("stbl", stsd, sttsBox, stsc, stsz, stco);
        byte[] minf = Box("minf", Box("smhd", new byte[8]), stbl);
        long total = (long)frames.Count * n;
        byte[] mdhd = Box("mdhd", U32(0, 0, 0, (uint)rate, (uint)total, 0x55C40000));
        byte[] hdlr = Box("hdlr", U32(0, 0), "soun"u8.ToArray(), new byte[12], [0]);
        byte[] mdia = Box("mdia", mdhd, hdlr, minf);
        byte[] tkhd = Box("tkhd", U32(3, 0, 0, 1, 0, (uint)(total * 1000 / rate)), new byte[8], [0, 0, 0, 0, 1, 0, 0, 0], Matrix(), new byte[8]);
        byte[] mvhd = Box("mvhd", U32(0, 0, 0, 1000, (uint)(total * 1000 / rate), 0x00010000), [1, 0], new byte[10], Matrix(), new byte[24], U32(2));
        byte[] moov = Box("moov", mvhd, Box("trak", tkhd, mdia));
        return [.. ftyp, .. Box("mdat", mdat), .. moov];
    }

    private static byte[] Matrix() => U32(0x00010000, 0, 0, 0, 0x00010000, 0, 0, 0, 0x40000000);

    private static int[] Groups(int frameCount, int[] pattern)
    {
        var g = new List<int>();
        int left = frameCount, i = 0;
        while (left > 0) { int k = Math.Min(left, pattern[i++ % pattern.Length]); g.Add(k); left -= k; }
        return [.. g];
    }

    [Theory]
    [MemberData(nameof(Nb))]
    public void MultiFrameNarrowbandSamplesDecodeBitExact(string relative) => MultiFrameCase(false, relative);

    [Theory]
    [MemberData(nameof(Wb))]
    public void MultiFrameWidebandSamplesDecodeBitExact(string relative) => MultiFrameCase(true, relative);

    private static void MultiFrameCase(bool wide, string relative)
    {
        string cod = Resolve(wide, relative);
        var frames = Frames(wide, cod);
        int n = wide ? 320 : 160;
        // Four per sample as in the public MrBean clip, then a mix (1, 3, 2, 5) so sample sizes vary and the last one is short.
        foreach (var (groups, advertised) in new[] { (Groups(frames.Count, [4]), 4), (Groups(frames.Count, [1, 3, 2, 5]), 4), (Groups(frames.Count, [2]), 1) })
        {
            string dir = TempDir();
            try
            {
                string path = Path.Combine(dir, "multi.3gp");
                File.WriteAllBytes(path, Build3gp(wide, frames, groups, advertised));
                CheckAgainstOut(wide, relative, path);

                using (var src = Mp4PacketSource.Open(File.OpenRead(path)))
                {
                    // One packet per sample, 20 ms per frame, timestamps back to back.
                    long ts = 0;
                    int i = 0;
                    while (src.TryReadPacket(out var p))
                    {
                        Assert.Equal(ts, p.Timestamp);
                        Assert.Equal((long)groups[i] * n, p.Duration);
                        ts += p.Duration;
                        i++;
                    }
                    Assert.Equal(groups.Length, i);
                    Assert.Equal((long)frames.Count * n, src.LengthFrames);
                }

                using var dec = Audio.Open(path);
                Assert.Equal((long)frames.Count * n, dec.LengthFrames);
                var all = new float[frames.Count * n];
                using (var full = Audio.Open(path)) Assert.Equal(all.Length, full.Read(all));
                // Seeks that land inside the 10-frame preroll restart at frame 0 and are bit-exact; this includes frames in the
                // middle of a multi-frame sample.
                var buf = new float[2 * n];
                foreach (long at in new long[] { 5, n - 1, n, 2 * n + 17, 3 * n + 5, 5 * n + 99, 9 * n + 3 })
                {
                    dec.Seek(at);
                    Assert.Equal(at, dec.PositionFrames);
                    Assert.Equal(buf.Length, dec.Read(buf));
                    for (int k = 0; k < buf.Length; k++) Assert.Equal(all[at + k], buf[k]);
                }
                long far = (frames.Count - 20) * n + 7;
                dec.Seek(far);
                Assert.Equal(buf.Length, dec.Read(buf));
                Assert.Equal(far + buf.Length, dec.PositionFrames);
            }
            finally
            {
                Directory.Delete(dir, true);
            }
        }
    }

    [Fact]
    public void AmrSampleWhoseFramesDoNotFillItIsATypedError()
    {
        // Two 12.2 kbit/s frames (32 bytes each) plus one stray byte: the walk of the TOC bytes ends mid-frame.
        var frame = new byte[32];
        frame[0] = 0x3c;
        byte[] sample = [.. frame, .. frame, 0x3c];
        var frames = new List<byte[]> { sample };
        string dir = TempDir();
        try
        {
            string path = Path.Combine(dir, "bad.3gp");
            File.WriteAllBytes(path, Build3gp(false, frames, [1], 2));
            using var dec = Audio.Open(path);
            Assert.Throws<AudioFormatException>(() => dec.Read(new float[2000]));
        }
        finally
        {
            Directory.Delete(dir, true);
        }
    }

    /// <summary>The public MrBean.3gp clip (fetch-only, tools/fetch-corpus.sh): AMR-NB samples of four 12.2 kbit/s frames each.</summary>
    [Fact]
    public void MrBeanClipDecodesAllFramesOfEachSample()
    {
        string? dir = Environment.GetEnvironmentVariable("ONEAUDIO_CORPUS");
        string path = dir is null ? "" : Path.Combine(dir, "containers-mrbean", "MrBean.3gp");
        Assert.SkipUnless(File.Exists(path), "MrBean.3gp not fetched (tools/fetch-corpus.sh containers-mrbean, set ONEAUDIO_CORPUS).");

        using (var src = Mp4PacketSource.Open(File.OpenRead(path)))
        {
            Assert.Equal(CodecId.AmrNb, src.Codec);
            Assert.True(src.TryReadPacket(out var first));
            Assert.Equal(128, first.Data.Length);
            Assert.Equal(4 * 160, first.Duration * 8000 / src.TimeScale);
            foreach (int off in new[] { 0, 0x20, 0x40, 0x60 }) Assert.Equal(0x3c, first.Data[off]);
        }

        using var dec = Audio.Open(path);
        Assert.Equal(8000, dec.Format.SampleRate);
        var all = ReadAll(dec);
        Assert.True(all.Length > 0);
        Assert.Equal(dec.LengthFrames, all.Length);
        Assert.Equal(0, all.Length % 160);
        // Speech, not silence: every frame of the first sample (four of them) carries signal.
        for (int f = 0; f < 4; f++)
            Assert.Contains(all.AsSpan(f * 160, 160).ToArray(), v => v != 0f);
        Assert.All(all, v => Assert.True(float.IsFinite(v) && Math.Abs(v) <= 1f));
    }
}
