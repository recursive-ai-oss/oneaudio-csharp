using OneAudio.Aac;
using OneAudio.Mp4;
using Xunit;

namespace OneAudio.Tests;

/// <summary>The raw_data_block length probe: exact on real streams, and well-behaved on damaged or cut-off data.</summary>
public class AacFrameProbeTests
{
    private static readonly string[] Generated =
    [
        "aac-moov-first.m4a", "aac-moov-last.m4a", "aac-stereo.mp4", "aac-mono.3gp", "aac-itunes-gapless.m4a", "aac-two-tracks.mp4", "alac-then-aac.mp4", "aac.aac",
    ];

    public static TheoryData<string> GeneratedFiles() { var d = new TheoryData<string>(); foreach (var f in Generated) d.Add(f); return d; }

    private static IPacketSource OpenAny(string path)
    {
        if (path.EndsWith(".aac", StringComparison.Ordinal)) return AdtsPacketSource.Open(File.OpenRead(path));
        return Mp4PacketSource.Open(File.OpenRead(path));
    }

    private static List<byte[]> Packets(IPacketSource src)
    {
        var l = new List<byte[]>();
        while (src.TryReadPacket(out var p)) l.Add(p.Data.ToArray());
        return l;
    }

    // Every packet of the stream: the probe measures it exactly; any strict prefix is incomplete, never a (shorter) frame.
    private static int CheckStream(string path)
    {
        using var src = OpenAny(path);
        AacFrameProbe probe;
        try { probe = new AacFrameProbe(src.CodecConfig.Span); }
        catch (AudioFormatException) { return -1; } // outside the decoder's scope (config the decoder cannot read): no probe
        var packets = Packets(src);
        Assert.NotEmpty(packets);
        for (int i = 0; i < packets.Count; i++)
        {
            var pk = packets[i];
            Assert.Equal(AacProbeResult.Frame, probe.Probe(pk, out int len));
            Assert.True(len == pk.Length, $"{Path.GetFileName(path)} packet {i}: probe says {len}, packet has {pk.Length} bytes");
            // Followed by the next packet's bytes, the probe still stops at the end element.
            if (i + 1 < packets.Count)
            {
                byte[] joined = [.. pk, .. packets[i + 1]];
                Assert.Equal(AacProbeResult.Frame, probe.Probe(joined, out int len2));
                Assert.Equal(pk.Length, len2);
            }
            if (i < 40)
                for (int cut = 0; cut < pk.Length; cut++)
                {
                    var r = probe.Probe(pk.AsSpan(0, cut), out int l3);
                    Assert.True(r == AacProbeResult.Incomplete, $"{Path.GetFileName(path)} packet {i} cut to {cut}: {r}");
                    Assert.Equal(0, l3);
                }
        }
        return packets.Count;
    }

    [Theory]
    [MemberData(nameof(GeneratedFiles))]
    public void ProbeLengthEqualsPacketSizeOnGeneratedFiles(string name)
    {
        // aac-two-tracks and alac-then-aac pick their AAC track by default; a file without an in-scope AAC stream would be -1.
        int n = CheckStream(ContainerTestEnv.TestData(name));
        Assert.True(n > 0, $"{name}: not probed");
    }

    [Theory]
    [MemberData(nameof(Conformance))]
    public void ProbeLengthEqualsPacketSizeOnConformanceStreams(string name)
    {
        int n = CheckStream(ContainerTestEnv.Conformance(name));
        Assert.SkipWhen(n < 0, $"{name} is outside the decoder's scope.");
    }

    // The ISO/IEC 14496-26 streams the decoder handles (the 72 in-scope ones and the implicit-SBR ones, which decode as the AAC-LC core).
    public static TheoryData<string> InScope()
    {
        var d = new TheoryData<string>();
        foreach (var n in AacTestEnv.Names(0)) d.Add(n);
        foreach (var n in AacTestEnv.Names(2)) d.Add(n);
        return d;
    }

    [Theory]
    [MemberData(nameof(InScope))]
    public void ProbeLengthEqualsPacketSizeOnEveryInScopeStream(string name)
    {
        int n = CheckStream(AacTestEnv.Mp4(name));
        Assert.True(n > 0, $"{name}: config rejected by the probe");
    }

    public static TheoryData<string> Conformance => ContainerTestEnv.ConformanceData();

    [Fact]
    public void EmptyDataIsIncomplete()
    {
        var probe = new AacFrameProbe(new byte[] { 0x12, 0x10 });
        Assert.Equal(AacProbeResult.Incomplete, probe.Probe([], out int len));
        Assert.Equal(0, len);
    }

    [Fact]
    public void HandMadeFramesAreMeasured()
    {
        var probe = new AacFrameProbe(new byte[] { 0x12, 0x08 }); // LC, 44.1 kHz, mono
        byte[] silent = [0x00, 0x00, 0x00, 0x07]; // SCE, gain 0, max_sfb 0, flags 0, END, padding
        Assert.Equal(AacProbeResult.Frame, probe.Probe(silent, out int len));
        Assert.Equal(4, len);
        // A fill element (one byte of payload) ahead of the channel element: 15 + 29 bits, padded after the end element.
        var bits = new List<bool>();
        void Put(int v, int n) { for (int i = n - 1; i >= 0; i--) bits.Add(((v >> i) & 1) != 0); }
        Put(6, 3); Put(1, 4); Put(0, 8);
        Put(0, 3); Put(0, 4); Put(0, 8); Put(0, 11); Put(0, 3); Put(7, 3);
        int expected = (bits.Count + 7) / 8;
        while (bits.Count % 8 != 0) bits.Add(false);
        var bytes = new byte[bits.Count / 8];
        for (int i = 0; i < bits.Count; i++) if (bits[i]) bytes[i / 8] |= (byte)(0x80 >> (i % 8));
        Assert.Equal(AacProbeResult.Frame, probe.Probe([.. bytes, 0xAA, 0xBB], out len));
        Assert.Equal(expected, len);
        Assert.Equal(AacProbeResult.Incomplete, probe.Probe(bytes.AsSpan(0, bytes.Length - 1), out len));
    }

    [Fact]
    public void GarbageAndDamagedFramesNeverThrowOrHang()
    {
        using var src = Mp4PacketSource.Open(File.OpenRead(ContainerTestEnv.TestData("aac-stereo.mp4")));
        var packets = Packets(src);
        var probe = new AacFrameProbe(src.CodecConfig.Span);
        var rnd = new Random(32);
        int notAac = 0, incomplete = 0, frame = 0;
        void Count(AacProbeResult r) { if (r == AacProbeResult.Frame) frame++; else if (r == AacProbeResult.Incomplete) incomplete++; else notAac++; }
        for (int i = 0; i < 4000; i++)
        {
            var buf = new byte[rnd.Next(0, 600)];
            rnd.NextBytes(buf);
            Count(probe.Probe(buf, out int len));
            Assert.InRange(len, 0, buf.Length);
        }
        foreach (var pk in packets.Take(60))
            for (int k = 0; k < 40; k++)
            {
                var copy = pk.ToArray();
                for (int f = 0; f < 1 + k % 4; f++) copy[rnd.Next(copy.Length)] ^= (byte)(1 << rnd.Next(8));
                Count(probe.Probe(copy, out int len));
                Assert.InRange(len, 0, copy.Length);
            }
        Assert.True(notAac > 0 && incomplete >= 0 && frame >= 0);
    }

    [Fact]
    public void ProbingAllocatesNothingOnGoodOrBadData()
    {
        using var src = Mp4PacketSource.Open(File.OpenRead(ContainerTestEnv.TestData("aac-stereo.mp4")));
        var packets = Packets(src);
        var probe = new AacFrameProbe(src.CodecConfig.Span);
        var garbage = new byte[300];
        new Random(5).NextBytes(garbage);
        void Round()
        {
            foreach (var pk in packets) probe.Probe(pk, out _);
            for (int cut = 0; cut < garbage.Length; cut += 7) probe.Probe(garbage.AsSpan(cut), out _);
            probe.Probe(packets[0].AsSpan(0, packets[0].Length / 2), out _);
        }
        Round(); Round();
        long before = GC.GetAllocatedBytesForCurrentThread();
        for (int i = 0; i < 5; i++) Round();
        Assert.Equal(0, GC.GetAllocatedBytesForCurrentThread() - before);
    }

    [Fact]
    public void UnsupportedConfigsAreRejectedWhenTheProbeIsMade()
    {
        Assert.Throws<UnsupportedAudioFormatException>(() => new AacFrameProbe(new byte[] { 0x0A, 0x10 })); // AAC Main
        Assert.Throws<ArgumentNullException>(() => new AacFrameProbe((AacAudioConfig)null!));
    }
}
