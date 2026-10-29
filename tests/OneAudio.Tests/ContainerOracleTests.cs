using OneAudio.Aac;
using OneAudio.Amr;
using OneAudio.Mp4;
using Xunit;

namespace OneAudio.Tests;

/// <summary>Packet-level equality of the MP4, ADTS and AMR readers against ffprobe, plus seeking.</summary>
public class ContainerOracleTests
{
    public static TheoryData<string> Mp4Files => new()
    {
        "aac-moov-last.m4a", "aac-moov-first.m4a", "aac-stereo.mp4", "aac-mono.3gp", "aac-itunes-gapless.m4a", "amrnb.3gp", "amrwb.3gp",
    };

    public static TheoryData<string> ConformanceFiles => ContainerTestEnv.ConformanceData();

    private sealed record Packet(byte[] Data, long Timestamp, long Duration)
    {
        public string Hash => Convert.ToHexStringLower(System.Security.Cryptography.SHA256.HashData(Data));
    }

    private static List<Packet> Read(IPacketSource src)
    {
        var l = new List<Packet>();
        while (src.TryReadPacket(out var p)) l.Add(new Packet(p.Data.ToArray(), p.Timestamp, p.Duration));
        return l;
    }

    private static void ComparePackets(List<Packet> mine, List<ContainerTestEnv.Probed> oracle, bool checkTiming)
    {
        Assert.Equal(oracle.Count, mine.Count);
        for (int i = 0; i < mine.Count; i++)
        {
            Assert.True(oracle[i].Size == mine[i].Data.Length, $"packet {i}: size {mine[i].Data.Length} vs ffprobe {oracle[i].Size}");
            Assert.True(oracle[i].Hash == mine[i].Hash, $"packet {i}: bytes differ from ffprobe");
            // Timing is compared when ffprobe reports distinct times (some systems-layer test streams give every sample time 0).
            if (!checkTiming || oracle[^1].Dts == oracle[0].Dts) continue;
            // ffprobe shifts timestamps by the edit-list start; only differences are comparable.
            Assert.True(oracle[i].Dts - oracle[0].Dts == mine[i].Timestamp - mine[0].Timestamp, $"packet {i}: dts {mine[i].Timestamp - mine[0].Timestamp} vs ffprobe {oracle[i].Dts - oracle[0].Dts}");
            // The last packet's duration is trimmed by ffprobe to the edit list's end.
            if (i < mine.Count - 1 && oracle[i].Duration != long.MinValue) Assert.True(oracle[i].Duration == mine[i].Duration, $"packet {i}: duration {mine[i].Duration} vs ffprobe {oracle[i].Duration}");
        }
    }

    [Theory]
    [MemberData(nameof(Mp4Files))]
    public void Mp4PacketsEqualFfprobe(string name)
    {
        string path = ContainerTestEnv.TestData(name);
        var oracle = ContainerTestEnv.Probe(path);
        using var src = ContainerTestEnv.OpenMp4(path);
        ComparePackets(Read(src), oracle, checkTiming: true);
    }

    [Theory]
    [MemberData(nameof(ConformanceFiles))]
    public void ConformanceMp4PacketsEqualFfprobe(string name)
    {
        string path = ContainerTestEnv.Conformance(name);
        var oracle = ContainerTestEnv.Probe(path);
        using var src = ContainerTestEnv.OpenMp4(path);
        Assert.Equal(CodecId.Aac, src.Codec);
        var mine = Read(src);
        // ffprobe cannot open some of these systems-layer streams at all (an explicit 24-bit sampling rate, odd track layouts).
        // Without a reference the sample table itself is the check: the whole track is delivered.
        if (oracle.Count == 0) Assert.Equal(src.SelectedTrack.SampleCount, mine.Count);
        else ComparePackets(mine, oracle, checkTiming: true);
        Assert.NotEmpty(mine);
    }

    [Fact]
    public void EveryTrackOfATwoTrackFileEqualsFfprobe()
    {
        string path = ContainerTestEnv.TestData("aac-two-tracks.mp4");
        using var src = ContainerTestEnv.OpenMp4(path);
        Assert.Equal(2, src.Tracks.Count);
        Assert.Equal(44100, src.Tracks[0].SampleRate);
        Assert.Equal(16000, src.Tracks[1].SampleRate);
        for (int t = 0; t < 2; t++)
        {
            src.SelectTrack(t);
            ComparePackets(Read(src), ContainerTestEnv.Probe(path, t), checkTiming: true);
        }
    }

    [Fact]
    public void AdtsPayloadsEqualRemuxedFfprobe()
    {
        string path = ContainerTestEnv.TestData("aac.aac");
        var oracle = ContainerTestEnv.Probe(ContainerTestEnv.RemuxToMp4(path));
        using var src = AdtsPacketSource.Open(File.OpenRead(path));
        var mine = Read(src);
        ComparePackets(mine, oracle, checkTiming: false);
        Assert.All(mine, p => Assert.Equal(1024, p.Duration));
        Assert.Equal(mine.Count * 1024L, src.LengthFrames);
    }

    [Theory]
    [InlineData("synthetic-nb.amr", 150)]
    [InlineData("synthetic-wb.awb", 150)]
    public void AmrFilePacketsEqualFfprobe(string name, int frames)
    {
        string path = ContainerTestEnv.TestData(name);
        var oracle = ContainerTestEnv.Probe(path);
        using var src = AmrPacketSource.Open(File.OpenRead(path));
        var mine = Read(src);
        Assert.Equal(frames, mine.Count);
        ComparePackets(mine, oracle, checkTiming: true);
        Assert.Equal(frames * (long)src.SamplesPerFrame, src.LengthFrames);
    }

    [Theory]
    [InlineData("amrnb.3gp", "synthetic-nb.amr")]
    [InlineData("amrwb.3gp", "synthetic-wb.awb")]
    public void AmrFileAndThreeGpHoldTheSameFrames(string threeGp, string amr)
    {
        using var a = ContainerTestEnv.OpenMp4(ContainerTestEnv.TestData(threeGp));
        using var b = AmrPacketSource.Open(File.OpenRead(ContainerTestEnv.TestData(amr)));
        Assert.Equal(b.Codec, a.Codec);
        var pa = Read(a);
        var pb = Read(b);
        Assert.Equal(pb.Count, pa.Count);
        for (int i = 0; i < pa.Count; i++) Assert.Equal(pb[i].Data, pa[i].Data);
        Assert.Equal(b.SampleRate, a.SampleRate);
        Assert.Equal(1, a.Channels);
    }

    // --- seeking: the first packet returned contains the requested time -------------------------------------------------

    [Theory]
    [MemberData(nameof(Mp4Files))]
    public void Mp4SeekLandsOnThePacketContainingTheTime(string name)
    {
        using var src = ContainerTestEnv.OpenMp4(ContainerTestEnv.TestData(name));
        SeekCheck(src, src.SelectedTrack.TimeScale, src.SampleRate, src.PrimingFrames);
    }

    [Theory]
    [MemberData(nameof(ConformanceFiles))]
    public void ConformanceSeek(string name)
    {
        using var src = ContainerTestEnv.OpenMp4(ContainerTestEnv.Conformance(name));
        SeekCheck(src, src.SelectedTrack.TimeScale, src.SampleRate, src.PrimingFrames);
    }

    [Fact]
    public void AdtsSeek()
    {
        using var src = AdtsPacketSource.Open(File.OpenRead(ContainerTestEnv.TestData("aac.aac")));
        SeekCheck(src, src.SampleRate, src.SampleRate, 0);
    }

    [Theory]
    [InlineData("synthetic-nb.amr")]
    [InlineData("synthetic-wb.awb")]
    public void AmrSeek(string name)
    {
        using var src = AmrPacketSource.Open(File.OpenRead(ContainerTestEnv.TestData(name)));
        SeekCheck(src, src.SampleRate, src.SampleRate, 0);
    }

    private static void SeekCheck(IPacketSource src, int scale, int rate, int priming)
    {
        var all = new List<(byte[] Data, long Ts, long Dur)>();
        while (src.TryReadPacket(out var p)) all.Add((p.Data.ToArray(), p.Timestamp, p.Duration));
        Assert.NotEmpty(all);
        long length = src.LengthFrames;
        Assert.True(length > 0);
        var rng = new Random(29);
        for (int n = 0; n < 300; n++)
        {
            long frame = n switch { 0 => -priming, 1 => length - 1, _ => rng.NextInt64(-priming, length) };
            src.SeekToFrame(frame, out long start);
            Assert.True(src.TryReadPacket(out var p), $"no packet after seeking to {frame}");
            // The packet that contains media time (frame + priming).
            long ticks = (frame + priming) * scale / rate;
            int expect = all.FindLastIndex(a => a.Ts <= ticks);
            Assert.True(expect >= 0 && ticks < all[expect].Ts + all[expect].Dur, "test oracle: time is inside the packet");
            Assert.Equal(all[expect].Ts, p.Timestamp);
            Assert.Equal(all[expect].Data, p.Data.ToArray());
            Assert.Equal(all[expect].Ts * rate / scale - priming, start);
            Assert.True(start <= frame);
        }
        // Past the end: positioned at the end.
        src.SeekToFrame(length, out long endStart);
        Assert.Equal(length, endStart);
        Assert.False(src.TryReadPacket(out _));
        src.SeekToFrame(-priming, out long s0);
        Assert.Equal(-priming, s0);
        Assert.True(src.TryReadPacket(out var first));
        Assert.Equal(all[0].Data, first.Data.ToArray());
        Assert.Throws<ArgumentOutOfRangeException>(() => src.SeekToFrame(-priming - 1, out _));
    }
}
