using Xunit;

namespace OneAudio.Tests;

public class FlacSeekTests
{
    public static TheoryData<string> Fixtures()
    {
        var d = new TheoryData<string>();
        foreach (var n in FlacFixtures.Names) d.Add(n);
        return d;
    }

    /// <summary>Seeks to many targets (edges, frame boundaries, random) and compares 300 frames with the sequential decode.</summary>
    internal static void CheckSeeks(Func<IAudioDecoder> open, int seed, int randomTargets = 40)
    {
        using var lin = open();
        var all = FlacFixtures.DecodeAll(lin);
        int ch = lin.Format.Channels;
        long len = all.Length / ch;
        var rnd = new Random(seed);
        var targets = new List<long> { 0, 1, len - 1, len, len / 2, Math.Max(0, len - 301) };
        for (int i = 0; i < randomTargets; i++) targets.Add(rnd.NextInt64(len + 1));
        foreach (int bs in new[] { 192, 576, 1000, 1152, 4096, 4608, 16384 }) targets.Add(Math.Min(len, bs - 1)); // around typical block boundaries
        foreach (int bs in new[] { 192, 576, 1000, 1152, 4096, 4608, 16384 }) targets.Add(Math.Min(len, bs));

        using var d = open();
        var buf = new int[300 * ch];
        foreach (long t in targets.Where(t => t >= 0 && t <= len))
        {
            d.Seek(t);
            Assert.Equal(t, d.PositionFrames);
            int n = d.ReadInteger(buf);
            int want = (int)Math.Min(300, len - t);
            Assert.Equal(want, n);
            Assert.True(all.AsSpan((int)(t * ch), want * ch).SequenceEqual(buf.AsSpan(0, want * ch)), $"mismatch after seeking to {t}");
            Assert.Equal(t + n, d.PositionFrames);
        }
    }

    [Theory, MemberData(nameof(Fixtures))]
    public void Seek_MatchesLinearDecode_WithAndWithoutSeekTable(string name)
    {
        var data = FlacFixtures.Bytes(name);
        CheckSeeks(() => FlacFixtures.Open(data), 1);
        CheckSeeks(() =>
        {
            var d = FlacFixtures.Open(data);
            d.DisableSeekTable();
            return d;
        }, 2);
    }

    [Fact]
    public void Seek_BeyondLength_Throws()
    {
        using var d = FlacFixtures.Open(FlacFixtures.Bytes("mono8"));
        Assert.Throws<ArgumentOutOfRangeException>(() => d.Seek(d.LengthFrames + 1));
        Assert.Throws<ArgumentOutOfRangeException>(() => d.Seek(-1));
        d.Seek(d.LengthFrames);
        Assert.Equal(0, d.ReadInteger(new int[10]));
    }

    [Fact]
    public void Seek_ThenReadSequentially_StaysAligned()
    {
        var data = FlacFixtures.Bytes("stereo16_noseektable");
        using var lin = FlacFixtures.Open(data);
        var all = FlacFixtures.DecodeAll(lin);
        using var d = FlacFixtures.Open(data);
        d.Seek(12345);
        var got = new List<int>();
        var buf = new int[2 * 777];
        int n;
        while ((n = d.ReadInteger(buf)) > 0) got.AddRange(buf.AsSpan(0, n * 2).ToArray());
        Assert.Equal(all.AsSpan(12345 * 2).ToArray(), got);
    }

    [Fact]
    public void WrongSeekTable_FallsBackToSearch()
    {
        var data = FlacFixtures.Bytes("stereo16_seektable");
        // SEEKTABLE body starts after the 4 + 38 STREAMINFO bytes and its own 4-byte header; corrupt every point's offset.
        int bodyStart = 4 + 4 + 34 + 4;
        for (int i = 0; i < 12; i++) data[bodyStart + i * 18 + 15] ^= 0x55;
        CheckSeeks(() => FlacFixtures.Open(data), 3, 15);
    }

    [Fact]
    public void Seek_InsideCurrentBlock_AndBackwards()
    {
        var data = FlacFixtures.Bytes("stereo16_seektable");
        using var lin = FlacFixtures.Open(data);
        var all = FlacFixtures.DecodeAll(lin);
        using var d = FlacFixtures.Open(data);
        var buf = new int[2 * 10];
        foreach (long t in new long[] { 100, 105, 50, 5000, 4999, 0, 65999 })
        {
            d.Seek(t);
            int n = d.ReadInteger(buf);
            Assert.True(all.AsSpan((int)t * 2, n * 2).SequenceEqual(buf.AsSpan(0, n * 2)));
        }
    }

    // ---- Ogg FLAC -------------------------------------------------------------------------------------

    [Theory, MemberData(nameof(Fixtures))]
    public void OggFlac_DecodesAndSeeksLikeNative(string name)
    {
        var native = FlacFixtures.Bytes(name);
        var ogg = FlacFixtures.ToOgg(native);
        using var n = FlacFixtures.Open(native);
        using var o = AudioDecoder.Open(new MemoryStream(ogg), FlacTestEnv.Registry);
        Assert.Equal(n.Format, o.Format);
        Assert.Equal(n.LengthFrames, o.LengthFrames);
        Assert.Equal(FlacFixtures.DecodeAll(n), FlacFixtures.DecodeAll(o));
        CheckSeeks(() => AudioDecoder.Open(new MemoryStream(ogg), FlacTestEnv.Registry), 7, 25);
    }

    [Fact]
    public void OggFlac_SmallPages_SplitFramesAcrossPages()
    {
        var native = FlacFixtures.Bytes("stereo16_midside_small");
        var ogg = FlacFixtures.ToOgg(native, maxBody: 300);
        using var n = FlacFixtures.Open(native);
        using var o = AudioDecoder.Open(new MemoryStream(ogg), FlacTestEnv.Registry);
        Assert.Equal(FlacFixtures.DecodeAll(n), FlacFixtures.DecodeAll(o));
        CheckSeeks(() => AudioDecoder.Open(new MemoryStream(ogg), FlacTestEnv.Registry), 9, 25);
    }

    [Fact]
    public void OggFlac_NonSeekable_DecodesForward()
    {
        var native = FlacFixtures.Bytes("mono24_lpc");
        var ogg = FlacFixtures.ToOgg(native);
        using var n = FlacFixtures.Open(native);
        using var o = AudioDecoder.Open(new NonSeekableStream(ogg), FlacTestEnv.Registry);
        Assert.False(o.CanSeek);
        Assert.Equal(FlacFixtures.DecodeAll(n), FlacFixtures.DecodeAll(o));
    }
}

/// <summary>Seeking on the corpus: files with and without a SEEKTABLE, fixed and variable block sizes, odd depths and channel counts.</summary>
public class FlacCorpusSeekTests
{
    public static TheoryData<string> Files() => FlacTestEnv.Group("subset");

    [Theory, MemberData(nameof(Files))]
    public void Subset_SeekMatchesLinear(string rel)
    {
        var path = FlacTestEnv.Path_(rel);
        FlacSeekTests.CheckSeeks(() => AudioDecoder.OpenFile(path, FlacTestEnv.Registry), rel.Length, 12);
        FlacSeekTests.CheckSeeks(() =>
        {
            var d = (OneAudio.Flac.FlacDecoder)AudioDecoder.OpenFile(path, FlacTestEnv.Registry);
            d.DisableSeekTable();
            return d;
        }, rel.Length + 1, 12);
    }
}
