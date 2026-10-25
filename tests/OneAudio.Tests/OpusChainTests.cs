using System.Buffers.Binary;
using OneAudio.Ogg;
using OneAudio.Opus;
using Xunit;

namespace OneAudio.Tests;

/// <summary>
/// Chained Ogg Opus files built by concatenating encoder output (one second each: mono SILK, mono hybrid, stereo CELT)
/// with the pre-skip of each link rewritten, so links differ in pre-skip as well as in channel count.
/// </summary>
public class OpusChainTests
{
    private const int FramesPerFile = 48000;
    private const int FileGranule = 48312; // the files' own pre-skip is 312

    private static byte[] Load(string name) => File.ReadAllBytes(Path.Combine(AppContext.BaseDirectory, "testdata", "opus", name));

    /// <summary>Rewrites the pre-skip in the OpusHead of a single-link file (and the page checksum that covers it).</summary>
    private static byte[] WithPreSkip(byte[] file, int preSkip)
    {
        var f = (byte[])file.Clone();
        int nseg = f[26];
        int bodyLen = 0;
        for (int i = 0; i < nseg; i++) bodyLen += f[27 + i];
        int head = 27 + nseg;
        Assert.True(f.AsSpan(head, 8).SequenceEqual("OpusHead"u8));
        BinaryPrimitives.WriteUInt16LittleEndian(f.AsSpan(head + 10), (ushort)preSkip);
        BinaryPrimitives.WriteUInt32LittleEndian(f.AsSpan(22), 0);
        BinaryPrimitives.WriteUInt32LittleEndian(f.AsSpan(22), OggBuilder.Crc(f.AsSpan(0, head + bodyLen)));
        return f;
    }

    private static byte[] Chain(params byte[][] links) => links.SelectMany(l => l).ToArray();

    private static OpusStreamDecoder Open(byte[] file) =>
        (OpusStreamDecoder)AudioDecoder.Open(new MemoryStream(file), new AudioCodecRegistry().AddOgg().AddOpus());

    private static float[] ReadAll(OpusStreamDecoder d)
    {
        var all = new List<float>();
        var buf = new float[4096 * d.Format.Channels];
        int n;
        while ((n = d.Read(buf)) > 0) all.AddRange(buf.AsSpan(0, n * d.Format.Channels));
        return all.ToArray();
    }

    // Seeks to many frames and compares with the linear decode. 80 ms of pre-roll lets the decoder converge; for the
    // steady tones used here the leftover is far below one quantiser step, so the bound is 5e-3 as for single links.
    private static void CheckSeeks(byte[] file, Action<OpusStreamDecoder>? selectRun, int seed)
    {
        using var linear = Open(file);
        selectRun?.Invoke(linear);
        var all = ReadAll(linear);
        int ch = linear.Format.Channels;
        long length = all.Length / ch;
        Assert.Equal(length, linear.LengthFrames);

        using var d = Open(file);
        selectRun?.Invoke(d);
        var rng = new Random(seed);
        var frames = new List<long> { 0, 1, length - 1, length / 2, length };
        // Frames just on each side of link boundaries are the interesting ones.
        for (long b = FramesPerFile - 400; b < length; b += FramesPerFile)
            for (long k = 0; k < 800 && b + k < length; k += 97) frames.Add(b + k);
        for (int i = 0; i < 40; i++) frames.Add(rng.NextInt64(length + 1));
        var buf = new float[2000 * ch];
        foreach (long f in frames)
        {
            d.Seek(f);
            Assert.Equal(f, d.PositionFrames);
            int want = (int)Math.Min(2000, length - f);
            int total = 0;
            while (total < want)
            {
                int n = d.Read(buf.AsSpan(total * ch, (want - total) * ch));
                if (n == 0) break;
                total += n;
            }
            Assert.Equal(want, total);
            for (int i = 0; i < want * ch; i++)
                Assert.True(Math.Abs(buf[i] - all[(int)f * ch + i]) < 5e-3, $"seek {f} of {length}, sample {i}");
        }
    }

    [Fact]
    public void Links_with_the_same_layout_play_through_with_each_pre_skip()
    {
        byte[] a = WithPreSkip(Load("silk-mono.opus"), 100), b = Load("hybrid-mono.opus"), c = WithPreSkip(Load("silk-mono.opus"), 1500);
        var file = Chain(a, b, c);
        using var d = Open(file);
        Assert.Equal(3, d.LinkCount);
        long expected = (FileGranule - 100) + (FileGranule - 312) + (FileGranule - 1500);
        Assert.Equal(expected, d.LengthFrames);

        // Linear reading is each link decoded alone, one after the other.
        var got = ReadAll(d);
        Assert.Equal(expected, got.Length);
        Assert.Equal(expected, d.PositionFrames);
        var want = new List<float>();
        foreach (var link in new[] { a, b, c })
        {
            using var one = Open(link);
            want.AddRange(ReadAll(one));
        }
        Assert.True(want.ToArray().AsSpan().SequenceEqual(got), "chain differs from its links decoded alone");
    }

    [Fact]
    public void Seeking_across_links_equals_linear_decode()
    {
        var file = Chain(WithPreSkip(Load("silk-mono.opus"), 100), Load("hybrid-mono.opus"), WithPreSkip(Load("silk-mono.opus"), 1500));
        CheckSeeks(file, null, 1);
    }

    [Fact]
    public void A_different_channel_count_ends_the_stream_and_is_reachable_as_its_own_run()
    {
        var file = Chain(Load("silk-mono.opus"), WithPreSkip(Load("hybrid-mono.opus"), 700), Load("celt-stereo.opus"), WithPreSkip(Load("celt-stereo.opus"), 2000));
        using var d = Open(file);
        Assert.Equal(4, d.LinkCount);
        Assert.Equal(1, d.GetLinkFormat(1)!.Value.Channels);
        Assert.Equal(2, d.GetLinkFormat(2)!.Value.Channels);

        // The first run is the two mono links; reading stops where the stereo link begins.
        Assert.Equal(1, d.Format.Channels);
        long monoRun = (FileGranule - 312) + (FileGranule - 700);
        Assert.Equal(monoRun, d.LengthFrames);
        Assert.Equal(monoRun, ReadAll(d).Length);

        d.SelectRun(3);
        Assert.Equal(2, d.Format.Channels);
        long stereoRun = (FileGranule - 312) + (FileGranule - 2000);
        Assert.Equal(stereoRun, d.LengthFrames);
        Assert.Equal(0, d.PositionFrames);
        Assert.Equal(stereoRun * 2, ReadAll(d).Length);

        CheckSeeks(file, null, 2);
        CheckSeeks(file, x => x.SelectRun(2), 3);
    }

    [Fact]
    public void Seeking_works_on_a_link_reached_after_a_read_from_another()
    {
        var file = Chain(Load("celt-stereo.opus"), WithPreSkip(Load("celt-stereo.opus"), 960));
        using var d = Open(file);
        var buf = new float[2 * 1000];
        Assert.Equal(1000, d.Read(buf));
        d.Seek(FileGranule - 312 + 5); // into the second link
        Assert.Equal(1000, d.Read(buf));
        d.Seek(10);                    // back into the first
        Assert.Equal(1000, d.Read(buf));
        Assert.Equal(1010, d.PositionFrames);
    }

    [Fact]
    public void Unseekable_chain_plays_through_in_band()
    {
        var file = Chain(WithPreSkip(Load("silk-mono.opus"), 100), WithPreSkip(Load("hybrid-mono.opus"), 700), Load("celt-stereo.opus"));
        using var d = (OpusStreamDecoder)AudioDecoder.Open(new ForwardOnlyStream(file), new AudioCodecRegistry().AddOgg().AddOpus());
        Assert.False(d.CanSeek);
        Assert.Equal(-1, d.LengthFrames);
        // The stereo link ends the stream, as in the seekable case.
        Assert.Equal((FileGranule - 100) + (FileGranule - 700), ReadAll(d).Length);
    }

    [Fact]
    public void A_stray_page_of_an_earlier_link_does_not_confuse_the_link_list()
    {
        var one = Load("silk-mono.opus");
        var two = Load("hybrid-mono.opus");
        // The first page after the headers of link one, replayed after link two: same serial, old sequence number.
        int pos = 0;
        for (int page = 0; page < 3; page++) pos += 27 + one[pos + 26] + Enumerable.Range(0, one[pos + 26]).Sum(i => one[pos + 27 + i]);
        int len = 27 + one[pos + 26] + Enumerable.Range(0, one[pos + 26]).Sum(i => one[pos + 27 + i]);
        var stray = one.AsSpan(pos, len).ToArray();
        var file = Chain(one, two, stray);

        using var src = OggPacketSource.Open(new MemoryStream(file));
        int links = src.LinkCount;
        long length = src.LengthFrames;
        int last = -1;
        while (src.TryReadPacket(out _))
        {
            Assert.InRange(src.CurrentLink, 0, src.LinkCount - 1);
            Assert.True(src.CurrentLink >= last);
            last = src.CurrentLink;
        }
        Assert.Equal(links, src.LinkCount);
        Assert.Equal(links - 1, last);
        Assert.Equal(length, src.LengthFrames);
    }

    private sealed class ForwardOnlyStream(byte[] data) : Stream
    {
        private readonly MemoryStream _m = new(data);
        public override bool CanRead => true;
        public override bool CanSeek => false;
        public override bool CanWrite => false;
        public override long Length => throw new NotSupportedException();
        public override long Position { get => throw new NotSupportedException(); set => throw new NotSupportedException(); }
        public override void Flush() { }
        public override int Read(byte[] buffer, int offset, int count) => _m.Read(buffer, offset, count);
        public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
        public override void SetLength(long value) => throw new NotSupportedException();
        public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();
    }
}
