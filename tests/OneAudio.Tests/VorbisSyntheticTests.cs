using System.Diagnostics;
using Xunit;

namespace OneAudio.Tests;

/// <summary>
/// Cross-checks generated streams (channel layouts the corpus lacks: mono, 3, 6 channels; residue types 0, 1, 2; coupling;
/// trimmed ends) against libvorbis, when the reference harness built by tools/vorbis-refs.sh is available.
/// </summary>
public class VorbisSyntheticTests
{
    private static string? Harness
    {
        get
        {
            var refs = Environment.GetEnvironmentVariable("ONEAUDIO_REFS");
            var path = refs is null ? null : Path.Combine(refs, "bin", "vorbis_ref_float");
            return path is not null && File.Exists(path) ? path : null;
        }
    }

    // Vorbis channel order to the library's ascending speaker-bit order (see VorbisDecoder).
    private static readonly int[][] Orders = [[], [0], [0, 1], [0, 2, 1], [0, 1, 2, 3], [0, 2, 1, 3, 4], [0, 2, 1, 5, 3, 4]];

    [Theory]
    [InlineData(1, 1, true, 0)]
    [InlineData(2, 1, true, 0)]
    [InlineData(2, 0, true, 123)]
    [InlineData(2, 2, true, 0)]
    [InlineData(3, 2, true, 0)]
    [InlineData(6, 1, true, 0)]
    [InlineData(6, 2, false, 77)]
    public void MatchesLibvorbis(int channels, int residueType, bool coupling, int trim)
    {
        Assert.SkipUnless(Harness is not null, "libvorbis reference harness not built (run tools/vorbis-refs.sh, set ONEAUDIO_REFS).");
        var builder = new VorbisStreamBuilder { Channels = channels, ResidueType = residueType, Coupling = coupling, Seed = 100 + channels + residueType };
        var file = builder.BuildFile(count: 300, trimEnd: trim, packetsPerPage: 7);

        string dir = Path.Combine(Path.GetTempPath(), "oneaudio-vorbis-synth");
        Directory.CreateDirectory(dir);
        string name = $"synth-{channels}-{residueType}-{coupling}-{trim}";
        string ogg = Path.Combine(dir, name + ".ogg");
        File.WriteAllBytes(ogg, file);
        var psi = new ProcessStartInfo(Harness!, [ogg, Path.Combine(dir, name)]) { RedirectStandardError = true };
        using (var proc = Process.Start(psi)!)
        {
            proc.WaitForExit();
            Assert.Equal(0, proc.ExitCode);
        }
        var refBytes = File.ReadAllBytes(Path.Combine(dir, name + ".f32"));
        var refF = new float[refBytes.Length / 4];
        Buffer.BlockCopy(refBytes, 0, refF, 0, refBytes.Length);

        using var d = AudioDecoder.Open(new MemoryStream(file), VorbisTestEnv.Registry);
        var ours = VorbisTestEnv.ReadAll(d);
        Assert.Equal(refF.Length, ours.Length);
        var order = Orders[channels];
        double maxErr = 0;
        int frames = ours.Length / channels;
        for (int i = 0; i < frames; i++)
            for (int o = 0; o < channels; o++)
                maxErr = Math.Max(maxErr, Math.Abs(ours[i * channels + o] - refF[i * channels + order[o]]));
        Assert.True(maxErr < 1e-5, $"max abs error {maxErr:g4}");
    }
}
