using System.Diagnostics;
using System.Text;
using Xunit;

namespace OneAudio.Tests;

/// <summary>
/// Compares the decoder with the native libmpg123 command-line player on encoder output (LAME and ffmpeg's libmp3lame
/// over generated signals; see tools/gen-mp3-testset.py): all nine sampling rates, mono and stereo, fixed, variable and
/// average bitrate, joint/forced stereo, free format, files with and without a Xing/LAME header. mpg123 is used as a black
/// box and only when present; its difference is measured and reported, not a conformance gate (the gate is the ISO/IEC
/// 11172-4 test in <see cref="Mp3ComplianceTests"/>).
/// </summary>
public class Mp3OracleTests
{
    private static string? GenDir => Mp3TestEnv.CorpusDir is { } c && Directory.Exists(Path.Combine(c, "mp3-gen")) ? Path.Combine(c, "mp3-gen") : null;

    private static string? Mpg123 => FindOnPath("mpg123");

    private static string? FindOnPath(string exe)
    {
        foreach (var dir in (Environment.GetEnvironmentVariable("PATH") ?? "").Split(Path.PathSeparator, StringSplitOptions.RemoveEmptyEntries))
        {
            var p = Path.Combine(dir, exe);
            if (File.Exists(p)) return p;
        }
        return null;
    }

    public static TheoryData<string> Files()
    {
        var d = new TheoryData<string>();
        var dir = GenDir;
        if (dir is null) { d.Add("none"); return d; }
        foreach (var f in Directory.GetFiles(dir, "*.mp3").OrderBy(x => x, StringComparer.Ordinal)) d.Add(Path.GetFileName(f));
        return d;
    }

    /// <summary>Decodes with mpg123 to 32-bit float, native channels, with its default gapless handling.</summary>
    internal static float[]? Mpg123Decode(string path)
    {
        var exe = Mpg123;
        if (exe is null) return null;
        var psi = new ProcessStartInfo(exe) { RedirectStandardOutput = true, RedirectStandardError = true, UseShellExecute = false };
        foreach (var a in new[] { "-q", "-e", "f32", "-s", path }) psi.ArgumentList.Add(a);
        using var p = Process.Start(psi)!;
        using var ms = new MemoryStream();
        p.StandardOutput.BaseStream.CopyTo(ms);
        p.StandardError.ReadToEnd();
        p.WaitForExit();
        var bytes = ms.ToArray();
        var f = new float[bytes.Length / 4];
        Buffer.BlockCopy(bytes, 0, f, 0, f.Length * 4);
        return f;
    }

    [Theory]
    [MemberData(nameof(Files))]
    public void MatchesMpg123(string file)
    {
        Assert.SkipUnless(GenDir is not null && file != "none", "Generated MP3 set missing (run tools/gen-mp3-testset.py into $ONEAUDIO_CORPUS/mp3-gen).");
        Assert.SkipUnless(Mpg123 is not null, "mpg123 not on PATH.");
        var path = Path.Combine(GenDir!, file);
        var ours = Mp3TestEnv.DecodeFile(path, out var fmt);
        var theirs = Mpg123Decode(path)!;
        var (rms, max) = Mp3TestEnv.Compare(ours, theirs);
        string line = $"mpg123 {file}: {fmt.SampleRate} Hz x{fmt.Channels}, ours {ours.Length / fmt.Channels} frames, mpg123 {theirs.Length / fmt.Channels} frames, rms={rms:E2} max={max:E2}";
        Mp3TestEnv.Report(line);
        Assert.True(ours.Length == theirs.Length, "length differs: " + line);
        Assert.True(max < 1e-5, line);
        Assert.True(rms < 1e-6, line);
    }

    [Fact]
    public void ReportTable()
    {
        Assert.SkipUnless(GenDir is not null && Mpg123 is not null, "needs the generated set and mpg123");
        var sb = new StringBuilder();
        double worstRms = 0, worstMax = 0;
        int count = 0, lengthEqual = 0;
        foreach (var f in Directory.GetFiles(GenDir!, "*.mp3"))
        {
            var ours = Mp3TestEnv.DecodeFile(f, out _);
            var theirs = Mpg123Decode(f)!;
            var (rms, max) = Mp3TestEnv.Compare(ours, theirs);
            worstRms = Math.Max(worstRms, rms); worstMax = Math.Max(worstMax, max);
            count++;
            if (ours.Length == theirs.Length) lengthEqual++;
        }
        string line = $"mpg123 comparison over {count} generated files: worst rms={worstRms:E2}, worst max={worstMax:E2}, equal lengths {lengthEqual}/{count}";
        Mp3TestEnv.Report(line);
    }
}
