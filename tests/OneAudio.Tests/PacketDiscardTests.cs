using OneAudio.Aac;
using OneAudio.Amr;
using OneAudio.Flac;
using OneAudio.Opus;
using OneAudio.Vorbis;
using OneAudio.Mp4;
using OneAudio.Ogg;
using Xunit;

namespace OneAudio.Tests;

/// <summary>PacketView.DiscardEndFrames: a container-declared trim of the end of any packet, honoured by every packet-stream decoder.</summary>
public class PacketDiscardTests
{
    /// <summary>Forwards a source and sets DiscardEndFrames on chosen audio packets (counted from 0, headers not counted).</summary>
    private sealed class DiscardingSource(IPacketSource inner, Dictionary<int, int> discards) : IPacketSource
    {
        private int _index;
        public CodecId Codec => inner.Codec;
        public long LengthFrames => inner.LengthFrames;
        public bool CanSeek => inner.CanSeek;
        public ReadOnlyMemory<byte> CodecConfig => inner.CodecConfig;
        public int SampleRate => inner.SampleRate;
        public int Channels => inner.Channels;
        public int TimeScale => inner.TimeScale;
        public int PrimingFrames => inner.PrimingFrames;

        public bool TryReadPacket(out PacketView packet)
        {
            if (!inner.TryReadPacket(out var p)) { packet = default; return false; }
            int d = 0;
            if (!p.IsHeader) discards.TryGetValue(_index++, out d);
            packet = new PacketView(p.Data, p.EndFrame, p.IsHeader, p.Timestamp, p.Duration, d);
            return true;
        }

        public void SeekToFrame(long frame, out long startFrame) => inner.SeekToFrame(frame, out startFrame);
        public void Dispose() => inner.Dispose();
    }

    /// <summary>Concatenates every <c>group</c> audio packets into one, as a 3GP sample holding several AMR frames does.</summary>
    private sealed class JoiningSource(IPacketSource inner, int group, Dictionary<int, int> discards) : IPacketSource
    {
        private byte[] _buf = new byte[4096];
        private int _index;
        public CodecId Codec => inner.Codec;
        public long LengthFrames => inner.LengthFrames;
        public bool CanSeek => false;

        public bool TryReadPacket(out PacketView packet)
        {
            int len = 0, got = 0;
            while (got < group && inner.TryReadPacket(out var p))
            {
                p.Data.CopyTo(_buf.AsSpan(len));
                len += p.Data.Length;
                got++;
            }
            if (got == 0) { packet = default; return false; }
            discards.TryGetValue(_index++, out int d);
            packet = new PacketView(_buf.AsSpan(0, len), -1, false, 0, 0, d);
            return true;
        }

        public void SeekToFrame(long frame, out long startFrame) => throw new NotSupportedException();
        public void Dispose() => inner.Dispose();
    }

    private static float[] ReadAll(IAudioDecoder d)
    {
        var list = new List<float>();
        var buf = new float[1000 * d.Format.Channels];
        int n;
        while ((n = d.Read(buf)) > 0) list.AddRange(buf.AsSpan(0, n * d.Format.Channels).ToArray());
        return [.. list];
    }

    private static float[] Without(float[] all, int channels, params (long at, long len)[] gaps)
    {
        var keep = new List<float>();
        for (long f = 0; f < all.Length / channels; f++)
        {
            bool dropped = false;
            foreach (var (at, len) in gaps) dropped |= f >= at && f < at + len;
            if (!dropped) keep.AddRange(all.AsSpan((int)f * channels, channels).ToArray());
        }
        return [.. keep];
    }

    // One gap of `d` frames somewhere in the output: everything before it and after it equals the undiscarded decode.
    private static long AssertOneGap(float[] full, float[] got, int channels, int d)
    {
        Assert.Equal(full.Length - d * channels, got.Length);
        int m = 0;
        while (m < got.Length && got[m] == full[m]) m++;
        Assert.True(m < got.Length, "no gap found");
        Assert.Equal(0, m % channels);
        for (int i = m; i < got.Length; i++) Assert.Equal(full[i + d * channels], got[i]);
        return m / channels;
    }

    // --- AAC ----------------------------------------------------------------------------------------------------------

    [Theory]
    [InlineData("aac-moov-first.m4a")]
    [InlineData("aac-stereo.mp4")]
    [InlineData("aac-itunes-gapless.m4a")]
    public void AacDropsTheTailOfMidStreamPackets(string name)
    {
        string path = ContainerTestEnv.TestData(name);
        using var baseline = new AacDecoder(Mp4PacketSource.Open(File.OpenRead(path)));
        int ch = baseline.Format.Channels;
        float[] full = ReadAll(baseline);
        int prime;
        using (var s = Mp4PacketSource.Open(File.OpenRead(path))) prime = s.PrimingFrames;

        var discards = new Dictionary<int, int> { [3] = 100, [7] = 1024, [9] = 1500 }; // 1500 > a packet: clamps to the whole packet
        using var d = new AacDecoder(new DiscardingSource(Mp4PacketSource.Open(File.OpenRead(path)), discards));
        float[] got = ReadAll(d);
        // Packet k's audio ends at (k + 1) * 1024 - priming on the timeline.
        long End(int k) => (k + 1) * 1024L - prime;
        var expected = Without(full, ch, (End(3) - 100, 100), (End(7) - 1024, 1024), (End(9) - 1024, 1024));
        Assert.Equal(expected.Length, got.Length);
        Assert.True(expected.AsSpan().SequenceEqual(got), "samples differ from the undiscarded decode with the tails removed");
        // The container's length counts the frames the discards remove.
        Assert.Equal(full.Length / ch - 100 - 1024 - 1024, got.Length / ch);
    }

    [Fact]
    public void AacDiscardOnTheLastPacketShortensTheEnd()
    {
        string path = ContainerTestEnv.TestData("aac-moov-first.m4a");
        using var baseline = new AacDecoder(Mp4PacketSource.Open(File.OpenRead(path)));
        int ch = baseline.Format.Channels;
        float[] full = ReadAll(baseline);
        int count = 0, prime;
        using (var s = Mp4PacketSource.Open(File.OpenRead(path))) { prime = s.PrimingFrames; while (s.TryReadPacket(out _)) count++; }
        // The container length already ends inside the last packet; a discard larger than that end padding cuts into real audio.
        long padding = count * 1024L - prime - baseline.LengthFrames;
        using var d = new AacDecoder(new DiscardingSource(Mp4PacketSource.Open(File.OpenRead(path)), new() { [count - 1] = (int)padding + 50 }));
        float[] got = ReadAll(d);
        Assert.Equal(full.Length - 50 * ch, got.Length);
        Assert.True(full.AsSpan(0, got.Length).SequenceEqual(got));
    }

    [Fact]
    public void AacWithoutDiscardsIsUnchanged()
    {
        string path = ContainerTestEnv.TestData("aac-stereo.mp4");
        using var a = new AacDecoder(Mp4PacketSource.Open(File.OpenRead(path)));
        using var b = new AacDecoder(new DiscardingSource(Mp4PacketSource.Open(File.OpenRead(path)), []));
        Assert.True(ReadAll(a).AsSpan().SequenceEqual(ReadAll(b)));
    }

    [Fact]
    public void NegativeDiscardIsRejected() =>
        Assert.Throws<ArgumentOutOfRangeException>(() => new PacketView([], -1, false, 0, 0, -1));

    // --- Opus ---------------------------------------------------------------------------------------------------------

    [Theory]
    [InlineData("celt-stereo.opus")]
    [InlineData("silk-mono.opus")]
    [InlineData("hybrid-mono.opus")]
    public void OpusDropsTheTailOfAMidStreamPacket(string name)
    {
        string path = ContainerTestEnv.TestData("../opus/" + name);
        using var baseline = OpusCodec.Instance.CreateDecoder(new DiscardingSource(OggPacketSource.Open(File.OpenRead(path)), []), AudioLimits.Default);
        int ch = baseline.Format.Channels;
        float[] full = ReadAll(baseline);
        using var d = OpusCodec.Instance.CreateDecoder(new DiscardingSource(OggPacketSource.Open(File.OpenRead(path)), new() { [10] = 123 }), AudioLimits.Default);
        float[] got = ReadAll(d);
        long gap = AssertOneGap(full, got, ch, 123);
        Assert.True(gap > 0);

        // Two discards, one longer than the packet's audio: that packet vanishes entirely, its neighbours are intact.
        using var d2 = OpusCodec.Instance.CreateDecoder(new DiscardingSource(OggPacketSource.Open(File.OpenRead(path)), new() { [10] = 123, [20] = 100_000 }), AudioLimits.Default);
        float[] got2 = ReadAll(d2);
        Assert.True(got2.Length < got.Length - 100);
        Assert.True(full.AsSpan(0, (int)gap * ch).SequenceEqual(got2.AsSpan(0, (int)gap * ch)));
    }

    // --- Vorbis -------------------------------------------------------------------------------------------------------

    [Fact]
    public void VorbisDropsTheTailOfAMidStreamPacket()
    {
        var builder = new VorbisStreamBuilder { Channels = 2, ResidueType = 1, Coupling = true, Seed = 7 };
        byte[] file = builder.BuildFile(count: 60, trimEnd: 0, packetsPerPage: 5);
        using var baseline = VorbisCodec.Instance.CreateDecoder(new DiscardingSource(OggPacketSource.Open(new MemoryStream(file)), []), AudioLimits.Default);
        float[] full = ReadAll(baseline);
        using var d = VorbisCodec.Instance.CreateDecoder(new DiscardingSource(OggPacketSource.Open(new MemoryStream(file)), new() { [20] = 37 }), AudioLimits.Default);
        float[] got = ReadAll(d);
        Assert.True(AssertOneGap(full, got, 2, 37) > 0);
    }

    // --- FLAC ---------------------------------------------------------------------------------------------------------

    [Theory]
    [InlineData("ogg_stereo16.oga")]
    [InlineData("ogg_mono24.oga")]
    public void FlacDropsTheTailOfAMidStreamPacket(string name)
    {
        string path = Path.Combine(AppContext.BaseDirectory, "testdata", "flac", name);
        using var baseline = FlacCodec.Instance.CreateDecoder(new DiscardingSource(OggPacketSource.Open(File.OpenRead(path)), []), AudioLimits.Default);
        int ch = baseline.Format.Channels;
        float[] full = ReadAll(baseline);
        using var d = FlacCodec.Instance.CreateDecoder(new DiscardingSource(OggPacketSource.Open(File.OpenRead(path)), new() { [1] = 77 }), AudioLimits.Default);
        float[] got = ReadAll(d);
        Assert.True(AssertOneGap(full, got, ch, 77) > 0);
    }

    // --- AMR ----------------------------------------------------------------------------------------------------------

    [Fact]
    public void AmrDropsTheTailOfASingleFramePacket()
    {
        string path = ContainerTestEnv.TestData("synthetic-nb.amr");
        using var baseline = AmrNbCodec.Instance.CreateDecoder(new DiscardingSource(AmrPacketSource.Open(File.OpenRead(path)), []), AudioLimits.Default);
        float[] full = ReadAll(baseline);
        using var d = AmrNbCodec.Instance.CreateDecoder(new DiscardingSource(AmrPacketSource.Open(File.OpenRead(path)), new() { [5] = 50, [6] = 160 }), AudioLimits.Default);
        float[] got = ReadAll(d);
        Assert.True(Without(full, 1, (6 * 160 - 50, 50), (7 * 160 - 160, 160)).AsSpan().SequenceEqual(got));
    }

    [Theory]
    [InlineData("synthetic-nb.amr", false, 0)]
    [InlineData("synthetic-wb.awb", true, 0)]
    public void AmrDiscardMayCoverSeveralFramesOfAMultiFrameSample(string name, bool wide, int _)
    {
        string path = ContainerTestEnv.TestData(name);
        int n = wide ? 320 : 160;
        IAudioCodec codec = wide ? AmrWbCodec.Instance : AmrNbCodec.Instance;
        using var baseline = codec.CreateDecoder(new JoiningSource(AmrPacketSource.Open(File.OpenRead(path)), 4, []), AudioLimits.Default);
        float[] full = ReadAll(baseline);
        Assert.Equal(150 * n, full.Length);
        // Samples of four frames; sample 2 spans frames 8..11. Drop 2.5 frames from its end and 10 samples from sample 5.
        using var d = codec.CreateDecoder(new JoiningSource(AmrPacketSource.Open(File.OpenRead(path)), 4, new() { [2] = n * 5 / 2, [5] = 10 }), AudioLimits.Default);
        float[] got = ReadAll(d);
        var expected = Without(full, 1, (12L * n - n * 5 / 2, n * 5 / 2), (24L * n - 10, 10));
        Assert.Equal(expected.Length, got.Length);
        Assert.True(expected.AsSpan().SequenceEqual(got));
    }
}
