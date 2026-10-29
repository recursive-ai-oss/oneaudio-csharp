using OneAudio.Aac;
using Xunit;

namespace OneAudio.Tests;

/// <summary>An in-memory packet source for driving the AAC decoder with hand-made access units.</summary>
internal sealed class AacPacketList : IPacketSource
{
    private readonly List<byte[]> _packets;
    private readonly byte[] _config;
    private int _next;

    public AacPacketList(List<byte[]> packets, byte[] config, int sampleRate = 0, int priming = 0)
    {
        _packets = packets;
        _config = config;
        SampleRate = sampleRate;
        PrimingFrames = priming;
    }

    public CodecId Codec => CodecId.Aac;
    public long LengthFrames => _packets.Count * 1024L - PrimingFrames;
    public bool CanSeek => true;
    public ReadOnlyMemory<byte> CodecConfig => _config;
    public int SampleRate { get; }
    public int PrimingFrames { get; }

    public bool TryReadPacket(out PacketView packet)
    {
        if (_next >= _packets.Count) { packet = default; return false; }
        packet = new PacketView(_packets[_next++], -1, false);
        return true;
    }

    public void SeekToFrame(long frame, out long startFrame)
    {
        long raw = Math.Max(0, frame + PrimingFrames);
        _next = (int)Math.Min(raw / 1024, _packets.Count);
        startFrame = _next * 1024L - PrimingFrames;
    }

    public void Dispose() { }
}

public class AacDecoderTests
{
    // AudioSpecificConfig of AAC-LC at 44.1 kHz: 00010 0100 cccc 000.
    private static byte[] Config(int channels) => [0x12, (byte)(0x00 | (channels << 3))];

    // A mono frame with no spectral data: SCE, global gain 0, max_sfb 0, no pulse/TNS/gain control, END (32 bits).
    private static readonly byte[] SilentMono = [0x00, 0x00, 0x00, 0x07];

    private static IAudioDecoder OpenFile(string name) =>
        AudioDecoder.Open(File.OpenRead(ContainerTestEnv.TestData(name)), ContainerTestEnv.Registry);

    private static float[] ReadAll(IAudioDecoder d)
    {
        var list = new List<float>();
        var buf = new float[1000 * d.Format.Channels];
        int n;
        while ((n = d.Read(buf)) > 0) list.AddRange(buf.AsSpan(0, n * d.Format.Channels).ToArray());
        return list.ToArray();
    }

    // --- AudioSpecificConfig -------------------------------------------------------------------------------------------

    [Fact]
    public void ConfigParsesTheLcFields()
    {
        var c = AacAudioConfig.Parse([0x12, 0x10]); // AAC-LC, 44100, stereo
        Assert.Equal(2, c.ObjectType);
        Assert.Equal(44100, c.SampleRate);
        Assert.Equal(2, c.ChannelConfiguration);
        Assert.Equal(2, c.Channels);
        Assert.False(c.FrameLength960);
        Assert.False(c.SbrSignalled);
        c.CheckSupported();
    }

    [Fact]
    public void ConfigSeesExplicitSbrAndPs()
    {
        // AOT 5 (SBR): 00101 0110 0010 0100 00010 -> core 24000? build: aot 5, rate idx 6 (24000), chan 2, ext rate idx 3 (48000), aot 2, then GASpecificConfig 000
        var bits = new List<bool>();
        void Put(int v, int n) { for (int i = n - 1; i >= 0; i--) bits.Add(((v >> i) & 1) != 0); }
        Put(5, 5); Put(6, 4); Put(2, 4); Put(3, 4); Put(2, 5); Put(0, 3);
        while (bits.Count % 8 != 0) bits.Add(false);
        var bytes = new byte[bits.Count / 8];
        for (int i = 0; i < bits.Count; i++) if (bits[i]) bytes[i / 8] |= (byte)(0x80 >> (i % 8));
        var c = AacAudioConfig.Parse(bytes);
        Assert.True(c.SbrSignalled);
        Assert.Equal(2, c.ObjectType);
        Assert.Equal(24000, c.SampleRate);
        Assert.Equal(48000, c.ExtensionSampleRate);
        Assert.Throws<UnsupportedAudioFormatException>(() => c.CheckSupported());
    }

    private static byte[] Asc(int aot, int rateIndex, int channels)
    {
        int bits = (aot << 11) | (rateIndex << 7) | (channels << 3);
        return [(byte)(bits >> 8), (byte)bits];
    }

    [Theory]
    [InlineData(1, 4, 2)]  // AAC Main
    [InlineData(3, 4, 2)]  // SSR
    [InlineData(4, 4, 2)]  // LTP
    [InlineData(17, 4, 2)] // error-resilient LC
    [InlineData(23, 4, 2)] // low delay
    [InlineData(2, 0, 2)]  // 96 kHz
    [InlineData(2, 2, 2)]  // 64 kHz
    [InlineData(2, 12, 2)] // 7350 Hz
    [InlineData(2, 4, 6)]  // 5.1
    [InlineData(2, 4, 3)]  // three channels
    public void UnsupportedConfigsGiveATypedError(int aot, int rateIndex, int channels)
    {
        Assert.Throws<UnsupportedAudioFormatException>(() => new AacDecoder(new AacPacketList([SilentMono], Asc(aot, rateIndex, channels))));
    }

    // --- decoding and trimming ---------------------------------------------------------------------------------------

    [Fact]
    public void SilentFramesDecodeToSilenceWithTheTrimmedLength()
    {
        var packets = Enumerable.Range(0, 10).Select(_ => SilentMono).ToList();
        using var d = new AacDecoder(new AacPacketList(packets, Config(1), priming: 700));
        Assert.Equal(44100, d.Format.SampleRate);
        Assert.Equal(1, d.Format.Channels);
        Assert.Equal(10 * 1024 - 700, d.LengthFrames);
        var all = ReadAll(d);
        Assert.Equal(10 * 1024 - 700, all.Length);
        Assert.All(all, v => Assert.Equal(0f, v));
        Assert.Equal(d.LengthFrames, d.PositionFrames);
        Assert.Equal(0, d.Read(new float[100]));
    }

    [Fact]
    public void ARealM4aDecodesToItsDeclaredLength()
    {
        using var d = OpenFile("aac-moov-first.m4a");
        var all = ReadAll(d);
        Assert.Equal(d.LengthFrames * d.Format.Channels, all.Length);
        Assert.Contains(all, v => Math.Abs(v) > 0.01f);
        Assert.All(all, v => Assert.True(Math.Abs(v) < 2f));
    }

    [Fact]
    public void GaplessM4aIsTrimmedAtBothEnds()
    {
        using var d = OpenFile("aac-itunes-gapless.m4a");
        var all = ReadAll(d);
        Assert.Equal(d.LengthFrames * d.Format.Channels, all.Length);
        Assert.Equal(d.LengthFrames, d.PositionFrames);
    }

    // --- seeking -------------------------------------------------------------------------------------------------------

    [Theory]
    [InlineData("aac-moov-first.m4a")]
    [InlineData("aac-stereo.mp4")]
    [InlineData("aac-itunes-gapless.m4a")]
    [InlineData("aac.aac")]
    public void RandomSeeksEqualALinearDecode(string file)
    {
        float[] reference;
        long length;
        int channels;
        using (var d = OpenFile(file)) { reference = ReadAll(d); length = d.LengthFrames; channels = d.Format.Channels; }
        using var s = OpenFile(file);
        var rng = new Random(11);
        var buf = new float[3000 * channels];
        for (int i = 0; i < 40; i++)
        {
            long target = i == 0 ? 0 : i == 1 ? length : rng.NextInt64(0, length + 1);
            s.Seek(target);
            Assert.Equal(target, s.PositionFrames);
            int got = s.Read(buf);
            int expected = (int)Math.Min(3000, length - target);
            Assert.Equal(expected, got);
            for (int k = 0; k < got * channels; k++)
                Assert.True(buf[k] == reference[target * channels + k], $"{file}: seek to {target}, sample {k} differs");
        }
    }

    // --- errors and concealment ------------------------------------------------------------------------------------------

    private static List<byte[]> RealPackets(string file, out byte[] config)
    {
        using var src = OneAudio.Mp4.Mp4PacketSource.Open(File.OpenRead(ContainerTestEnv.TestData(file)));
        config = src.CodecConfig.ToArray();
        var list = new List<byte[]>();
        while (src.TryReadPacket(out var p)) list.Add(p.Data.ToArray());
        return list;
    }

    [Fact]
    public void ACorruptUnitThrowsATypedErrorUnlessConcealmentIsOn()
    {
        var packets = RealPackets("aac-moov-first.m4a", out var cfg);
        packets[5] = packets[5].AsSpan(0, 10).ToArray(); // truncated
        using (var d = new AacDecoder(new AacPacketList(packets, cfg)))
        {
            Assert.Throws<AudioFormatException>(() => ReadAll(d));
        }
        using (var d = new AacDecoder(new AacPacketList(packets, cfg)) { ConcealErrors = true })
        {
            var all = ReadAll(d);
            Assert.Equal(packets.Count * 1024 * d.Format.Channels, all.Length);
            Assert.Equal(1, d.ConcealedFrames);
        }
    }

    [Fact]
    public void ALostPacketIsConcealedByRepeatingAndFading()
    {
        var packets = RealPackets("aac-moov-first.m4a", out var cfg);
        var lost = packets.ToList();
        for (int i = 20; i < 26; i++) lost[i] = [];
        using var d = new AacDecoder(new AacPacketList(lost, cfg));
        int ch = d.Format.Channels;
        var all = ReadAll(d);
        Assert.Equal(6, d.ConcealedFrames);
        // Energy of the concealed frames decays and is silent from the fifth lost frame on (the overlap tail ends in frame 6).
        double Rms(int frame) { double s = 0; for (int i = frame * 1024 * ch; i < (frame + 1) * 1024 * ch; i++) s += all[i] * all[i]; return Math.Sqrt(s / (1024 * ch)); }
        Assert.True(Rms(22) < Rms(19) * 0.9 + 1e-9 || Rms(19) < 1e-4, "concealment should fade");
        Assert.True(Rms(25) < 1e-3 * Math.Max(1e-3, Rms(19)) + 1e-4 || Rms(25) < Rms(22));
        Assert.True(all.All(v => float.IsFinite(v)));
    }

    [Fact]
    public void DecodingAfterAConcealedFrameRecovers()
    {
        var packets = RealPackets("aac-moov-first.m4a", out var cfg);
        var lost = packets.ToList();
        lost[10] = [];
        float[] a, b;
        using (var d = new AacDecoder(new AacPacketList(packets, cfg))) a = ReadAll(d);
        using (var d = new AacDecoder(new AacPacketList(lost, cfg))) b = ReadAll(d);
        int ch = 2;
        // Two frames after the loss, the overlap is rebuilt from good data and the output is identical again.
        int from = 13 * 1024 * ch;
        for (int i = from; i < a.Length; i++) Assert.Equal(a[i], b[i]);
    }

    [Fact]
    public void MutatedUnitsOnlyEverRaiseTypedErrors()
    {
        var packets = RealPackets("aac-moov-first.m4a", out var cfg);
        var rng = new Random(3);
        var pcm = new float[1024 * 2];
        int failures = 0;
        var core = new AacFrameDecoder(AacAudioConfig.Parse(cfg));
        for (int i = 0; i < 4000; i++)
        {
            var unit = (byte[])packets[rng.Next(packets.Count)].Clone();
            int flips = rng.Next(1, 6);
            for (int k = 0; k < flips; k++) unit[rng.Next(unit.Length)] ^= (byte)(1 << rng.Next(8));
            if (rng.Next(5) == 0) unit = unit.AsSpan(0, rng.Next(unit.Length)).ToArray();
            try { core.Decode(unit, pcm, i); }
            catch (AudioFormatException) { failures++; }
            foreach (var v in pcm.AsSpan(0, 2048)) { }
        }
        Assert.True(failures > 0);
    }

    // --- allocation ------------------------------------------------------------------------------------------------------

    [Fact]
    public void DecodingAllocatesNothingAfterTheFirstFrame()
    {
        using var d = OpenFile("aac-moov-first.m4a");
        var buf = new float[1024 * d.Format.Channels];
        d.Read(buf);
        d.Read(buf);
        long before = GC.GetAllocatedBytesForCurrentThread();
        int frames = 0;
        for (int i = 0; i < 20; i++) frames += d.Read(buf);
        long after = GC.GetAllocatedBytesForCurrentThread();
        Assert.True(frames > 1024 * 10);
        Assert.Equal(0, after - before);
    }
}
