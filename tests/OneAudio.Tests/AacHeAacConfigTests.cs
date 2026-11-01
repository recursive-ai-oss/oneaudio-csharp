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
        var ex = Assert.Throws<UnsupportedAudioFormatException>(c.CheckSupported);
        Assert.Contains("explicit SBR signalling", ex.Message, StringComparison.Ordinal);
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
        var ex = Assert.Throws<UnsupportedAudioFormatException>(c.CheckSupported);
        Assert.Contains("explicit SBR and parametric stereo", ex.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void BackwardCompatibleSbrIsNamedAsSuch()
    {
        var c = AacAudioConfig.Parse([0x13, 0x88, 0x56, 0xe5, 0xa0]);
        Assert.True(c.SbrSignalled);
        Assert.False(c.PsSignalled);
        Assert.False(c.ExplicitSignalling);
        Assert.Equal(44100, c.ExtensionSampleRate);
        var ex = Assert.Throws<UnsupportedAudioFormatException>(c.CheckSupported);
        Assert.Contains("backward-compatible SBR signalling", ex.Message, StringComparison.Ordinal);
        Assert.DoesNotContain("explicit", ex.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void TruncatedConfigsStillFail()
    {
        Assert.Throws<AudioFormatException>(() => AacAudioConfig.Parse([0x2b, 0x8a])); // explicit SBR cut before the core object type ends
        Assert.Throws<AudioFormatException>(() => AacAudioConfig.Parse([0x2b]));
        Assert.Throws<AudioFormatException>(() => AacAudioConfig.Parse([0x13])); // plain LC without its flags
        Assert.Throws<AudioFormatException>(() => AacAudioConfig.Parse([0xeb, 0x92])); // explicit PS cut short
    }

    [Theory]
    [InlineData("sbr-compatible.m4a", "backward-compatible SBR")]
    [InlineData("sbr-explicit.m4a", "explicit SBR")]
    [InlineData("sbrps-explicit.m4a", "explicit SBR and parametric stereo")]
    public void HeAacFilesAreUnsupportedNotDamaged(string name, string wording)
    {
        var ex = Assert.Throws<UnsupportedAudioFormatException>(() => AudioDecoder.OpenFile(Path_(name), ContainerTestEnv.Registry));
        Assert.Contains(wording, ex.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void ImpliedSbrDecodesAsTheLcCore()
    {
        using var d = AudioDecoder.OpenFile(Path_("sbr-implied.m4a"), ContainerTestEnv.Registry);
        Assert.Equal(22050, d.Format.SampleRate);
        Assert.Equal(1, d.Format.Channels);
        var buf = new float[4096];
        Assert.True(d.Read(buf) > 0);
    }
}
