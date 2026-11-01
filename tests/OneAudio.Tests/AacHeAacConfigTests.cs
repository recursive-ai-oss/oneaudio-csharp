using OneAudio.Aac;
using OneAudio.Mp4;
using Xunit;

namespace OneAudio.Tests;

/// <summary>HE-AAC signalling in the AudioSpecificConfig (ISO/IEC 14496-3 1.6.2.1, 4.4.1) as real writers produce it.</summary>
public class AacHeAacConfigTests
{
    private static string Path_(string name) => Path.Combine(AppContext.BaseDirectory, "testdata", "heaac", name);

    [Fact]
    public void ExplicitSbrWithoutTrailingExtensionFlagParses()
    {
        var c = AacAudioConfig.Parse([0x2b, 0x8a, 0x08]);
        Assert.True(c.SbrSignalled);
        Assert.False(c.PsSignalled);
        Assert.True(c.ExplicitSignalling);
        Assert.Equal(2, c.ObjectType);
        Assert.Equal(22050, c.SampleRate);
        Assert.Equal(44100, c.ExtensionSampleRate);
        Assert.Equal(1, c.Channels);
        c.CheckSupported();
        Assert.True(AacConfigInfo.TryParse([0x2b, 0x8a, 0x08], out var info));
        Assert.Equal(new AacConfigInfo(5, 22050, 1, 44100), info);
    }

    [Fact]
    public void ExplicitPsWithoutTrailingExtensionFlagParses()
    {
        var c = AacAudioConfig.Parse([0xeb, 0x92, 0x08]);
        Assert.True(c.SbrSignalled);
        Assert.True(c.PsSignalled);
        Assert.True(c.ExplicitSignalling);
        Assert.Equal(22050, c.SampleRate);
        Assert.Equal(44100, c.ExtensionSampleRate);
        Assert.Equal(2, c.Channels);
        c.CheckSupported();
        Assert.Equal(1, c.CoreChannels);
    }

    [Fact]
    public void BackwardCompatibleSbrIsNamedAsSuch()
    {
        var c = AacAudioConfig.Parse([0x13, 0x88, 0x56, 0xe5, 0xa0]);
        Assert.True(c.SbrSignalled);
        Assert.False(c.PsSignalled);
        Assert.False(c.ExplicitSignalling);
        Assert.Equal(44100, c.ExtensionSampleRate);
        c.CheckSupported();
    }

    [Fact]
    public void TruncatedConfigsStillFail()
    {
        Assert.Throws<AudioFormatException>(() => AacAudioConfig.Parse([0x2b, 0x8a])); // explicit SBR cut before the core object type ends
        Assert.Throws<AudioFormatException>(() => AacAudioConfig.Parse([0x2b]));
        Assert.Throws<AudioFormatException>(() => AacAudioConfig.Parse([0x13])); // plain LC without its flags
        Assert.Throws<AudioFormatException>(() => AacAudioConfig.Parse([0xeb, 0x92])); // explicit PS cut short
    }

    // The sample files are plain AAC-LC frames under a rewritten AudioSpecificConfig, so they carry no SBR payload: the
    // decoder still honours the signalled output rate and channel count, and the high band stays empty.
    [Theory]
    [InlineData("sbr-compatible.m4a", 44100, 1)]
    [InlineData("sbr-explicit.m4a", 44100, 1)]
    [InlineData("sbrps-explicit.m4a", 44100, 2)]
    [InlineData("sbr-implied.m4a", 22050, 1)] // plain LC frames, no SBR anywhere: nothing to expand
    public void HeAacFilesDecodeAtTheSignalledRate(string name, int rate, int channels)
    {
        using var d = AudioDecoder.OpenFile(Path_(name), ContainerTestEnv.Registry);
        Assert.Equal(rate, d.Format.SampleRate);
        Assert.Equal(channels, d.Format.Channels);
        var buf = new float[4096 * channels];
        long frames = 0;
        int n;
        float peak = 0;
        while ((n = d.Read(buf)) > 0)
        {
            frames += n;
            foreach (var v in buf.AsSpan(0, n * channels)) peak = Math.Max(peak, Math.Abs(v));
        }
        Assert.Equal(d.LengthFrames, frames);
        Assert.True(peak > 0.01f);
        Assert.False(((AacDecoder)d).SbrDataIgnored);
    }

    [Fact]
    public void AnOutputRateThatIsNeitherTheCoreRateNorTwiceItIsRefused()
    {
        // explicit SBR: object type 5, core 16 kHz (index 8), mono, output 48 kHz (index 3), then AAC-LC with a plain GASpecificConfig
        var bits = new List<bool>();
        void Put(int v, int n) { for (int i = n - 1; i >= 0; i--) bits.Add(((v >> i) & 1) != 0); }
        Put(5, 5); Put(8, 4); Put(1, 4); Put(3, 4); Put(2, 5); Put(0, 3);
        while (bits.Count % 8 != 0) bits.Add(false);
        var bytes = new byte[bits.Count / 8];
        for (int i = 0; i < bits.Count; i++) if (bits[i]) bytes[i / 8] |= (byte)(0x80 >> (i % 8));
        var c = AacAudioConfig.Parse(bytes);
        Assert.Equal(48000, c.ExtensionSampleRate);
        Assert.Throws<UnsupportedAudioFormatException>(() => c.CheckSupported());
    }
}
