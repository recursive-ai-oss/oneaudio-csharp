using OneAudio.Aac;
using Xunit;

namespace OneAudio.Tests;

/// <summary><see cref="AacDecoder.SbrActive"/> and <see cref="AacDecoder.PsActive"/> report what the decoder does, however the stream signals it.</summary>
public class AacHeAacActiveFlagsTests
{
    private static string Path_(string name) => Path.Combine(AppContext.BaseDirectory, "testdata", "heaac", name);

    // The sample files carry plain AAC-LC frames under a rewritten config, so only the signalling decides.
    [Theory]
    [InlineData("sbr-implied.m4a", false, false, false, 22050, 1)]
    [InlineData("sbr-compatible.m4a", true, false, false, 44100, 1)]
    [InlineData("sbr-explicit.m4a", true, false, false, 44100, 1)]
    [InlineData("sbrps-explicit.m4a", true, true, false, 44100, 2)]   // stereo core: SBR stereo, no PS applied
    [InlineData("sbrps-mono-core.m4a", true, true, true, 44100, 2)]
    public void FlagsFollowTheSignallingOfTheSampleFiles(string name, bool sbrSignalled, bool psSignalled, bool psActive, int rate, int channels)
    {
        using var d = (AacDecoder)AudioDecoder.OpenFile(Path_(name), ContainerTestEnv.Registry);
        Assert.Equal(sbrSignalled, d.Config.SbrSignalled);
        Assert.Equal(psSignalled, d.Config.PsSignalled);
        Assert.Equal(sbrSignalled, d.SbrActive);
        Assert.Equal(psActive, d.PsActive);
        Assert.Equal(rate, d.Format.SampleRate);
        Assert.Equal(channels, d.Format.Channels);
        Assert.False(d.SbrDataIgnored);
    }

    [Fact]
    public void PlainLcFilesReportNeitherFlag()
    {
        using var d = (AacDecoder)AudioDecoder.OpenFile(ContainerTestEnv.TestData("aac.aac"), ContainerTestEnv.Registry);
        Assert.False(d.SbrActive);
        Assert.False(d.PsActive);
        Assert.False(d.Config.SbrSignalled);
        var buf = new float[4096 * d.Format.Channels];
        while (d.Read(buf) > 0) { }
        Assert.False(d.SbrDataIgnored);
    }

    // Implicit signalling (a plain AAC-LC config), backward-compatible and explicit signalling, the downsampled mode, and
    // parametric stereo that no config announces: the decoder finds the mono-core PS data and outputs two channels.
    [Theory]
    [InlineData("al_sbr_e_48_1", false, false, 48000, 1)]
    [InlineData("al_sbr_i_44_1_new", false, false, 44100, 1)]
    [InlineData("al_sbr_i_44_2", false, false, 44100, 2)]
    [InlineData("al_sbr_sig_48_2_sig0", false, false, 48000, 2)]
    [InlineData("al_sbr_cm_48_1", true, false, 48000, 1)]
    [InlineData("al_sbr_cm_48_2", true, false, 48000, 2)]
    [InlineData("al_sbr_sig_48_2_sig1", true, false, 48000, 2)]
    [InlineData("al_sbr_sig_24_2_fsaac24_sig1", true, false, 24000, 2)]
    [InlineData("al_sbr_ps_00", true, true, 32000, 2)]
    [InlineData("al_sbr_ps_03_new", true, true, 32000, 2)]
    public void ConformanceStreamsReportWhatTheyDecode(string name, bool sbrSignalled, bool psActive, int rate, int channels)
    {
        using var f = File.OpenRead(AacTestEnv.Mp4(name));
        using var d = (AacDecoder)AudioDecoder.Open(f, AacTestEnv.Registry, leaveOpen: true);
        Assert.True(d.SbrActive);
        Assert.Equal(sbrSignalled, d.Config.SbrSignalled);
        Assert.False(d.Config.PsSignalled);   // no stream announces parametric stereo; PsActive comes from the data
        Assert.Equal(psActive, d.PsActive);
        Assert.Equal(rate, d.Format.SampleRate);
        Assert.Equal(channels, d.Format.Channels);
        var buf = new float[4096 * d.Format.Channels];
        while (d.Read(buf) > 0) { }
        Assert.False(d.SbrDataIgnored);
    }

    private sealed class CountingSource(IPacketSource inner) : IPacketSource
    {
        public int Reads;
        public CodecId Codec => inner.Codec;
        public long LengthFrames => inner.LengthFrames;
        public bool CanSeek => inner.CanSeek;
        public ReadOnlyMemory<byte> CodecConfig => inner.CodecConfig;
        public int SampleRate => inner.SampleRate;
        public int PrimingFrames => inner.PrimingFrames;
        public bool TryReadPacket(out PacketView packet) { Reads++; return inner.TryReadPacket(out packet); }
        public void SeekToFrame(long frame, out long startFrame) => inner.SeekToFrame(frame, out startFrame);
        public void Dispose() => inner.Dispose();
    }

    [Fact]
    public void OpeningWithoutTheProbeReadsNoAccessUnitsAndTrustsTheConfig()
    {
        byte[] silent = [0x00, 0x00, 0x00, 0x07];
        var units = Enumerable.Range(0, 50).Select(_ => silent).ToList();
        byte[] plain = [0x12, 0x08];                  // AAC-LC, 44.1 kHz, mono
        byte[] explicitSbr = [0x2b, 0x8a, 0x08];       // object type 5 over 22.05 kHz, extension 44.1 kHz, mono
        foreach (var (config, sbr) in new[] { (plain, false), (explicitSbr, true) })
        {
            var probed = new CountingSource(new AacPacketList(units, config));
            using (new AacDecoder(probed)) { }
            Assert.True(probed.Reads >= 50, $"default open reads the probe window ({probed.Reads})");
            var trusting = new CountingSource(new AacPacketList(units, config));
            using var d = new AacDecoder(trusting, null, probeAtOpen: false);
            Assert.Equal(0, trusting.Reads);
            Assert.Equal(sbr, d.SbrActive);
            Assert.False(d.PsActive);
        }
    }

    [Fact]
    public void CodecProbeAtOpenSwitchesTheProbeOffForRegistryOpens()
    {
        string path = Path_("sbr-implied.m4a");
        var registry = new AudioCodecRegistry().AddMp4().AddAdts().Add(new AacCodec { ProbeAtOpen = false });
        using var d = (AacDecoder)AudioDecoder.OpenFile(path, registry);
        Assert.False(d.SbrActive);
        Assert.True(new AacCodec().ProbeAtOpen);
    }

    [Fact]
    public void ConformanceStreamWithoutTheProbeDecodesAsLcAndReportsTheSkippedSbr()
    {
        var registry = new AudioCodecRegistry().AddMp4().AddAdts().Add(new AacCodec { ProbeAtOpen = false });
        using var f = File.OpenRead(AacTestEnv.Mp4("al_sbr_e_48_1"));
        using var d = (AacDecoder)AudioDecoder.Open(f, registry, leaveOpen: true);
        Assert.False(d.SbrActive);
        Assert.Equal(d.Config.SampleRate, d.Format.SampleRate);   // core rate only
        var buf = new float[4096];
        while (d.Read(buf) > 0) { }
        Assert.True(d.SbrDataIgnored);
    }
}
