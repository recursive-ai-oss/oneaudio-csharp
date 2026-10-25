using OneAudio.Loudness;
using Xunit;

namespace OneAudio.Tests;

/// <summary>Shared helpers for the loudness tests.</summary>
internal static class LoudnessTestEnv
{
    public static readonly AudioCodecRegistry Registry = new AudioCodecRegistry().AddWav();

    public static string? EbuDir
    {
        get
        {
            var root = Environment.GetEnvironmentVariable("ONEAUDIO_CORPUS");
            if (root is null) return null;
            var dir = Path.Combine(root, "ebu-loudness-v05");
            return Directory.Exists(dir) ? dir : null;
        }
    }

    /// <summary>Writes to the test output and, when ONEAUDIO_TEST_VERBOSE is set, to the console so passing runs can print their tables.</summary>
    public static void Log(Xunit.ITestOutputHelper output, string line)
    {
        output.WriteLine(line);
        if (Environment.GetEnvironmentVariable("ONEAUDIO_TEST_VERBOSE") is not null) Console.Error.WriteLine(line);
    }

    public static string Require(string file)
    {
        Assert.SkipUnless(EbuDir is not null, "EBU loudness test set not fetched (tools/fetch-corpus.sh ebu-loudness-v05, set ONEAUDIO_CORPUS).");
        var path = Path.Combine(EbuDir!, file);
        Assert.True(File.Exists(path), "missing " + path);
        return path;
    }

    public static (float[] Samples, AudioFormat Format) Load(string path)
    {
        using var dec = AudioDecoder.OpenFile(path, Registry);
        var fmt = dec.Format;
        var all = new List<float[]>();
        long total = 0;
        var buf = new float[4096 * fmt.Channels];
        int n;
        while ((n = dec.Read(buf)) > 0)
        {
            all.Add(buf.AsSpan(0, n * fmt.Channels).ToArray());
            total += (long)n * fmt.Channels;
        }
        var result = new float[total];
        long pos = 0;
        foreach (var a in all) { a.CopyTo(result, pos); pos += a.Length; }
        return (result, fmt);
    }

    /// <summary>Feeds the whole signal in blocks of <paramref name="frames"/> frames.</summary>
    public static void Feed(LoudnessMeter m, float[] x, int frames)
    {
        int ch = m.Channels;
        for (int i = 0; i < x.Length; i += frames * ch)
            m.Process(x.AsSpan(i, Math.Min(frames * ch, x.Length - i)));
    }
}

/// <summary>
/// EBU Tech 3341 minimum-requirement cases 1 to 23 and Tech 3342 cases 1 to 6, run on the EBU loudness test set v05.
/// Each case prints its expected and measured value. Skipped when the set has not been fetched.
/// </summary>
public class LoudnessEbuTests(Xunit.ITestOutputHelper output)
{
    private const double Tol = 0.1;

    private void Report(string id, string what, double expected, double measured, double lo, double hi)
    {
        LoudnessTestEnv.Log(output, $"{id,-10} {what,-26} expected {expected,7:F1}  measured {measured,8:F3}  (allowed {expected - lo:F1}..{expected + hi:F1})");
        Assert.InRange(measured, expected - lo, expected + hi);
    }

    private static (LoudnessMeter Meter, float[] X) Measure(string file)
    {
        var (x, fmt) = LoudnessTestEnv.Load(LoudnessTestEnv.Require(file));
        var m = new LoudnessMeter(fmt);
        LoudnessTestEnv.Feed(m, x, 480);
        return (m, x);
    }

    [Theory]
    [InlineData("seq-3341-1-16bit.wav", "3341-1", -23.0)]
    [InlineData("seq-3341-2-16bit.wav", "3341-2", -33.0)]
    public void Cases1And2_Momentary_ShortTerm_Integrated(string file, string id, double expected)
    {
        var (m, _) = Measure(file);
        Report(id, "M", expected, m.MomentaryLufs, Tol, Tol);
        Report(id, "S", expected, m.ShortTermLufs, Tol, Tol);
        Report(id, "I", expected, m.IntegratedLufs, Tol, Tol);
    }

    [Theory]
    [InlineData("seq-3341-3-16bit-v02.wav", "3341-3")]
    [InlineData("seq-3341-4-16bit-v02.wav", "3341-4")]
    [InlineData("seq-3341-5-16bit-v02.wav", "3341-5")]
    [InlineData("seq-3341-6-5channels-16bit.wav", "3341-6 5.0")]
    [InlineData("seq-3341-6-6channels-WAVEEX-16bit.wav", "3341-6 5.1 ex")]
    [InlineData("seq-3341-7_seq-3342-5-24bit.wav", "3341-7")]
    [InlineData("seq-3341-2011-8_seq-3342-6-24bit-v02.wav", "3341-8")]
    public void Cases3To8_Integrated(string file, string id)
    {
        var (m, _) = Measure(file);
        Report(id, "I", -23.0, m.IntegratedLufs, Tol, Tol);
    }

    [Fact]
    public void Case9_ShortTerm_Constant_After_3s()
    {
        var (x, fmt) = LoudnessTestEnv.Load(LoudnessTestEnv.Require("seq-3341-9-24bit.wav"));
        var m = new LoudnessMeter(fmt);
        int step = fmt.SampleRate / 100 * fmt.Channels;
        double min = double.MaxValue, max = double.MinValue;
        for (int i = 0; i < x.Length; i += step)
        {
            m.Process(x.AsSpan(i, Math.Min(step, x.Length - i)));
            if ((i / fmt.Channels) >= 3 * fmt.SampleRate)
            {
                min = Math.Min(min, m.ShortTermLufs);
                max = Math.Max(max, m.ShortTermLufs);
            }
        }
        Report("3341-9", "S min after 3 s", -23.0, min, Tol, Tol);
        Report("3341-9", "S max after 3 s", -23.0, max, Tol, Tol);
    }

    [Fact]
    public void Case12_Momentary_Constant_After_1s()
    {
        var (x, fmt) = LoudnessTestEnv.Load(LoudnessTestEnv.Require("seq-3341-12-24bit.wav"));
        var m = new LoudnessMeter(fmt);
        int step = fmt.SampleRate / 100 * fmt.Channels;
        double min = double.MaxValue, max = double.MinValue;
        for (int i = 0; i < x.Length; i += step)
        {
            m.Process(x.AsSpan(i, Math.Min(step, x.Length - i)));
            if ((i / fmt.Channels) >= fmt.SampleRate)
            {
                min = Math.Min(min, m.MomentaryLufs);
                max = Math.Max(max, m.MomentaryLufs);
            }
        }
        Report("3341-12", "M min after 1 s", -23.0, min, Tol, Tol);
        Report("3341-12", "M max after 1 s", -23.0, max, Tol, Tol);
    }

    public static TheoryData<int> Twenty()
    {
        var d = new TheoryData<int>();
        for (int i = 1; i <= 20; i++) d.Add(i);
        return d;
    }

    [Theory]
    [MemberData(nameof(Twenty))]
    public void Case10_MaxShortTerm_PerFile(int n)
    {
        var (m, _) = Measure($"seq-3341-10-{n}-24bit.wav");
        Report($"3341-10-{n}", "max S", -23.0, m.MaxShortTermLufs, Tol, Tol);
    }

    [Theory]
    [MemberData(nameof(Twenty))]
    public void Case13_MaxMomentary_PerFile(int n)
    {
        var name = n == 1 || n == 2 ? $"seq-3341-13-{n}-24bit.wav" : $"seq-3341-13-{n}-24bit.wav.wav";
        var (m, _) = Measure(name);
        Report($"3341-13-{n}", "max M", -23.0, m.MaxMomentaryLufs, Tol, Tol);
    }

    /// <summary>The 'live meter' files: one long file holding 20 consecutive segments; the maximum is read per segment.</summary>
    private void LiveSegments(string file, string id, double segmentSeconds, bool shortTerm)
    {
        var (x, fmt) = LoudnessTestEnv.Load(LoudnessTestEnv.Require(file));
        int segFrames = (int)Math.Round(segmentSeconds * fmt.SampleRate);
        var m = new LoudnessMeter(fmt, truePeak: false, loudnessRange: false);
        for (int i = 0; i < 20; i++)
        {
            m.Reset();
            LoudnessTestEnv.Feed(m, x.AsSpan(i * segFrames * fmt.Channels, segFrames * fmt.Channels).ToArray(), 480);
            Report($"{id}-{i}", shortTerm ? "max S" : "max M", -38.0 + i, shortTerm ? m.MaxShortTermLufs : m.MaxMomentaryLufs, Tol, Tol);
        }
    }

    [Fact]
    public void Case11_Live_MaxShortTerm_Successive() => LiveSegments("seq-3341-11-24bit.wav", "3341-11", 6.0, true);

    [Fact]
    public void Case14_Live_MaxMomentary_Successive() => LiveSegments("seq-3341-14-24bit.wav.wav", "3341-14", 0.8, false);

    [Theory]
    [InlineData("seq-3341-15-24bit.wav.wav", "3341-15", -6.0)]
    [InlineData("seq-3341-16-24bit.wav.wav", "3341-16", -6.0)]
    [InlineData("seq-3341-17-24bit.wav.wav", "3341-17", -6.0)]
    [InlineData("seq-3341-18-24bit.wav.wav", "3341-18", -6.0)]
    [InlineData("seq-3341-19-24bit.wav.wav", "3341-19", 3.0)]
    [InlineData("seq-3341-20-24bit.wav.wav", "3341-20", 0.0)]
    [InlineData("seq-3341-21-24bit.wav.wav", "3341-21", 0.0)]
    [InlineData("seq-3341-22-24bit.wav.wav", "3341-22", 0.0)]
    [InlineData("seq-3341-23-24bit.wav.wav", "3341-23", 0.0)]
    public void Cases15To23_TruePeak(string file, string id, double expected)
    {
        var (m, _) = Measure(file);
        Report(id, "true peak dBTP", expected, m.TruePeakDbtp, 0.4, 0.2);
    }

    [Theory]
    [InlineData("seq-3342-1-16bit.wav", "3342-1", 10.0)]
    [InlineData("seq-3342-2-16bit.wav", "3342-2", 5.0)]
    [InlineData("seq-3342-3-16bit.wav", "3342-3", 20.0)]
    [InlineData("seq-3342-4-16bit.wav", "3342-4", 15.0)]
    [InlineData("seq-3341-7_seq-3342-5-24bit.wav", "3342-5", 5.0)]
    [InlineData("seq-3341-2011-8_seq-3342-6-24bit-v02.wav", "3342-6", 15.0)]
    public void Tech3342_LoudnessRange(string file, string id, double expected)
    {
        var (x, fmt) = LoudnessTestEnv.Load(LoudnessTestEnv.Require(file));
        var m = new LoudnessMeter(fmt);
        LoudnessTestEnv.Feed(m, x, 480);
        // Tech 3342 asks for 1.5 s of trailing silence so the last window is complete.
        LoudnessTestEnv.Feed(m, new float[fmt.SampleRate * 3 / 2 * fmt.Channels], 480);
        Report(id, "LRA", expected, m.LoudnessRange, 1.0, 1.0);
    }

    /// <summary>
    /// The same signals resampled with the library's own polyphase resampler must still measure within tolerance:
    /// proves the K-weighting design at 44.1 kHz and 96 kHz against the expected values (which are defined at 48 kHz).
    /// </summary>
    [Theory]
    [InlineData("seq-3341-1-16bit.wav", -23.0)]
    [InlineData("seq-3341-3-16bit-v02.wav", -23.0)]
    [InlineData("seq-3341-4-16bit-v02.wav", -23.0)]
    [InlineData("seq-3341-5-16bit-v02.wav", -23.0)]
    [InlineData("seq-3341-6-6channels-WAVEEX-16bit.wav", -23.0)]
    [InlineData("seq-3341-7_seq-3342-5-24bit.wav", -23.0)]
    [InlineData("seq-3341-2011-8_seq-3342-6-24bit-v02.wav", -23.0)]
    public void Integrated_Holds_After_Resampling(string file, double expected)
    {
        var (x, fmt) = LoudnessTestEnv.Load(LoudnessTestEnv.Require(file));
        foreach (int rate in new[] { 44100, 96000, 32000 })
        {
            var r = AudioResampler.Create(fmt.SampleRate, rate, fmt.Channels, ResamplerQuality.Polyphase);
            var y = Dsp.Convert(r, x);
            var m = new LoudnessMeter(rate, fmt.Channels, fmt.ChannelMask, truePeak: false);
            LoudnessTestEnv.Feed(m, y, 997);
            Report($"{file[..Math.Min(14, file.Length)]}@{rate}", "I", expected, m.IntegratedLufs, Tol, Tol);
        }
    }
}
