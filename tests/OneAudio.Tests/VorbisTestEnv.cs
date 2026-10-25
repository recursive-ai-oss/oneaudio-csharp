using Xunit;
using OneAudio.Vorbis;

namespace OneAudio.Tests;

/// <summary>Locates the fetched Xiph test vectors and the libvorbis reference output (see tools/fetch-corpus.sh and tools/vorbis-refs.sh).</summary>
internal static class VorbisTestEnv
{
    public static readonly AudioCodecRegistry Registry = new AudioCodecRegistry().AddOgg().AddVorbis();

    public static string? CorpusDir => Environment.GetEnvironmentVariable("ONEAUDIO_CORPUS");
    public static string? RefsDir => Environment.GetEnvironmentVariable("ONEAUDIO_REFS") is { } r ? Path.Combine(r, "vorbis") : null;

    public static readonly string[] Names =
    [
        "1.0-test", "1.0.1-test", "48k-mono", "beta3-test", "beta4-test", "bimS-silence", "chain-test1", "chain-test2", "chain-test3",
        "combustion", "highrate-test", "lsp-test", "lsp-test2", "lsp-test3", "lsp-test4", "make", "mono", "moog",
        "one-entry-codebook-test", "out-of-spec-blocksize", "rc1-test", "rc2-test", "rc2-test2", "rc3-test", "singlemap-test",
        "sleepzor", "test-short", "test-short2", "unused-mode-test",
    ];

    public static TheoryData<string> NameData()
    {
        var d = new TheoryData<string>();
        foreach (var n in Names) d.Add(n);
        return d;
    }

    public static string OggPath(string name) => Path.Combine(CorpusDir!, $"vorbis-xiph-{name}", $"{name}.ogg");

    public static void RequireCorpus(string name)
    {
        Assert.SkipUnless(CorpusDir is not null && File.Exists(OggPath(name)), "Xiph test vectors not fetched (run tools/fetch-corpus.sh, set ONEAUDIO_CORPUS).");
    }

    public static void RequireReference(string name)
    {
        RequireCorpus(name);
        Assert.SkipUnless(RefsDir is not null && File.Exists(Path.Combine(RefsDir, name + ".f32")), "libvorbis reference output missing (run tools/vorbis-refs.sh, set ONEAUDIO_REFS).");
    }

    /// <summary>Per-link (channels, rate, frames) lines of the reference output.</summary>
    public static List<(int Channels, int Rate, long Frames)> ReferenceLinks(string name)
    {
        var links = new List<(int, int, long)>();
        foreach (var line in File.ReadLines(Path.Combine(RefsDir!, name + ".txt")))
        {
            var p = line.Split(' ');
            if (p[0] == "total" || p[0] == "errors") continue;
            links.Add((int.Parse(p[1]), int.Parse(p[2]), long.Parse(p[3])));
        }
        return links;
    }

    /// <summary>True when any floor of the first link's setup is type 0.</summary>
    public static bool UsesFloor0(string path)
    {
        using var fs = File.OpenRead(path);
        using var src = Ogg.OggPacketSource.Open(fs, leaveOpen: true);
        src.TryReadPacket(out var id);
        var setup = VorbisSetup.ParseIdentification(id.Data, AudioLimits.Default);
        src.TryReadPacket(out var comment);
        setup.ParseComments(comment.Data, AudioLimits.Default);
        src.TryReadPacket(out var s);
        setup.ParseSetup(s.Data, AudioLimits.Default);
        return setup.Floors.Any(f => f is VorbisFloor0);
    }

    public static float[] ReadAll(IAudioDecoder d)
    {
        var list = new List<float[]>();
        int total = 0;
        var buf = new float[4096 * d.Format.Channels];
        int n;
        while ((n = d.Read(buf)) > 0)
        {
            var c = new float[n * d.Format.Channels];
            Array.Copy(buf, c, c.Length);
            list.Add(c);
            total += c.Length;
        }
        var all = new float[total];
        int o = 0;
        foreach (var c in list) { c.CopyTo(all, o); o += c.Length; }
        return all;
    }
}
