using OneAudio.Flac;
using Xunit;

namespace OneAudio.Tests;

/// <summary>
/// The IETF CELLAR FLAC test files: every <c>subset</c> file bit-exact against <c>flac -d</c> and its STREAMINFO MD5,
/// <c>uncommon</c> files decoded or cleanly rejected, <c>faulty</c> files cleanly rejected.
/// </summary>
public class FlacCorpusTests
{
    public static TheoryData<string> Subset() => FlacTestEnv.Group("subset");
    public static TheoryData<string> Uncommon() => FlacTestEnv.Group("uncommon");
    public static TheoryData<string> Faulty() => FlacTestEnv.Group("faulty");

    [Theory, MemberData(nameof(Subset))]
    public void Subset_IsBitExact_AndMd5Matches(string rel)
    {
        var path = FlacTestEnv.Path_(rel);
        Assert.SkipUnless(FlacTestEnv.FlacBinary is not null, "reference flac binary not available (set ONEAUDIO_FLAC).");
        using var dec = (FlacDecoder)AudioDecoder.OpenFile(path, FlacTestEnv.Registry);
        Assert.True(dec.VerifyMd5(), "STREAMINFO MD5 mismatch");
        Assert.Equal(0, dec.PositionFrames);
        var (hash, frames) = FlacTestEnv.HashDecode(dec);
        if (dec.LengthFrames >= 0) Assert.Equal(dec.LengthFrames, frames);
        Assert.Equal(FlacTestEnv.ReferenceHash(path), hash);
    }

    /// <summary>Per-file outcome the decoder promises for the uncommon files: decoded bit-exact, or rejected.</summary>
    private static readonly Dictionary<string, string> UncommonRejected = new()
    {
        ["01 - changing samplerate.flac"] = "sample rate",
        ["02 - increasing number of channels.flac"] = "channel",
        ["03 - decreasing number of channels.flac"] = "channel",
        ["04 - changing bitdepth.flac"] = "bit depth",
    };

    [Theory, MemberData(nameof(Uncommon))]
    public void Uncommon_DecodesBitExact_OrIsRejected(string rel)
    {
        var path = FlacTestEnv.Path_(rel);
        string name = Path.GetFileName(path);
        if (name.StartsWith("10 ", StringComparison.Ordinal) || name.StartsWith("11 ", StringComparison.Ordinal))
        {
            // No fLaC marker: not recognised as FLAC at all.
            Assert.Throws<UnsupportedAudioFormatException>(() => AudioDecoder.OpenFile(path, FlacTestEnv.Registry).Dispose());
            return;
        }
        if (UncommonRejected.TryGetValue(name, out var why))
        {
            var ex = Assert.Throws<AudioFormatException>(() =>
            {
                using var d = AudioDecoder.OpenFile(path, FlacTestEnv.Registry);
                FlacTestEnv.HashDecode(d);
            });
            Assert.Contains(why, ex.Message, StringComparison.Ordinal);
            return;
        }
        Assert.SkipUnless(FlacTestEnv.FlacBinary is not null, "reference flac binary not available (set ONEAUDIO_FLAC).");
        using var dec = (FlacDecoder)AudioDecoder.OpenFile(path, FlacTestEnv.Registry);
        Assert.True(dec.VerifyMd5() ?? true);
        Assert.Equal(FlacTestEnv.ReferenceHash(path), FlacTestEnv.HashDecode(dec).Hash);
    }

    /// <summary>Every faulty file ends in a typed rejection (never another exception, never a hang or a silent decode).</summary>
    [Theory, MemberData(nameof(Faulty))]
    public void Faulty_IsRejectedCleanly(string rel)
    {
        var path = FlacTestEnv.Path_(rel);
        Assert.ThrowsAny<OneAudioException>(() =>
        {
            using var d = AudioDecoder.OpenFile(path, FlacTestEnv.Registry);
            FlacTestEnv.HashDecode(d);
        });
    }
}
