using System.Globalization;
using System.Text;
using OneAudio.Aac;
using Xunit;

namespace OneAudio.Tests;

/// <summary>Registry, file locations and helpers for the AAC conformance tests.</summary>
internal static class AacTestEnv
{
    public static readonly AudioCodecRegistry Registry = new AudioCodecRegistry().AddMp4().AddAdts().AddAac();

    private static string? Dir => Environment.GetEnvironmentVariable("ONEAUDIO_CORPUS") is { Length: > 0 } c ? Path.Combine(c, "aac-iso-lc") : null;

    /// <summary>Stream names from tools/aac-conformance.manifest of one section: "in" (with reference waveforms) or "out" (out of scope).</summary>
    public static List<string> Names(int section)
    {
        var list = new List<string>();
        string manifest = Path.Combine(AppContext.BaseDirectory, "testdata", "aac", "aac-conformance.manifest");
        int current = 0; // 0 in scope, 1 out of scope, 2 implicit SBR
        foreach (var line in File.ReadAllLines(manifest))
        {
            if (line.StartsWith("# out of scope", StringComparison.Ordinal)) current = 1;
            else if (line.StartsWith("# implicit SBR", StringComparison.Ordinal)) current = 2;
            var f = line.Split(' ', StringSplitOptions.RemoveEmptyEntries);
            if (f.Length == 4 && f[0] == "mp4" && current == section) list.Add(f[1]);
        }
        return list;
    }

    public static string Mp4(string name)
    {
        var p = Dir is null ? null : Path.Combine(Dir, "mp4", name + ".mp4");
        Assert.SkipUnless(p is not null && File.Exists(p), "AAC conformance streams not fetched (run tools/fetch-aac-conformance.sh, set ONEAUDIO_CORPUS).");
        return p!;
    }

    public static string Wav(string name) => Path.Combine(Dir!, "wav", name + ".wav");

    public static float[] DecodeAll(string path, out AudioFormat format, out AacTools tools, out long declaredLength)
    {
        using var f = File.OpenRead(path);
        using var d = AudioDecoder.Open(f, Registry, leaveOpen: true);
        format = d.Format;
        declaredLength = d.LengthFrames;
        var list = new List<float>();
        var buf = new float[4096 * format.Channels];
        int n;
        while ((n = d.Read(buf)) > 0) list.AddRange(buf.AsSpan(0, n * format.Channels).ToArray());
        tools = ((AacDecoder)d).ToolsSeen;
        return list.ToArray();
    }

    /// <summary>Reads a 16- or 24-bit PCM WAV into floats where 1.0 is full scale.</summary>
    public static float[] ReadWav(string path, out int channels)
    {
        var b = File.ReadAllBytes(path);
        int p = 12, bits = 0;
        channels = 0;
        while (p + 8 <= b.Length)
        {
            string id = Encoding.ASCII.GetString(b, p, 4);
            int size = BitConverter.ToInt32(b, p + 4);
            if (id == "fmt ")
            {
                channels = BitConverter.ToInt16(b, p + 10);
                bits = BitConverter.ToInt16(b, p + 22);
            }
            else if (id == "data")
            {
                int bytes = bits / 8;
                int n = Math.Min(size, b.Length - p - 8) / bytes;
                var o = new float[n];
                for (int i = 0; i < n; i++)
                {
                    int at = p + 8 + i * bytes;
                    o[i] = bytes == 3 ? ((b[at] << 8) | (b[at + 1] << 16) | (b[at + 2] << 24)) / 2147483648f : BitConverter.ToInt16(b, at) / 32768f;
                }
                return o;
            }
            p += 8 + size + (size & 1);
        }
        throw new InvalidDataException("no data chunk");
    }

    private static readonly object TableLock = new();

    public static void Row(string line)
    {
        if (Environment.GetEnvironmentVariable("ONEAUDIO_AAC_TABLE") is { Length: > 0 } path)
            lock (TableLock) File.AppendAllText(path, line + "\n");
    }

    public static string Fmt(double v) => v.ToString("E2", CultureInfo.InvariantCulture);
}

/// <summary>
/// The AAC-LC conformance streams of ISO/IEC 14496-26 against their reference waveforms, plus the typed errors for the
/// streams outside what the decoder implements. The streams are fetched by tools/fetch-aac-conformance.sh.
/// <para>
/// Accuracy criterion we apply. It is our definition, not a quotation of the ISO text (ISO/IEC 14496-4 is not available
/// to us). The RMS bound is reconstructed: 2^-15/sqrt(12) is the RMS of uniform rounding noise at half a 16-bit LSB
/// (the same figure MP3's ISO/IEC 11172-4 "full accuracy" uses), written as accuracy level K = -log2(rms * sqrt(12)) with
/// 1.0 as 16-bit full scale, so the bound is K >= 15; the 3GPP TS 26.406 conformance criteria point to such an RMS/LSB
/// measurement. The maximum-difference bound of 2^-14 is borrowed from the same MP3 definition (ISO/IEC 11172-4) and is
/// not taken from an AAC document. The reference waveforms are 24-bit, so they carry no 16-bit rounding of their own.
/// </para>
/// </summary>
public class AacConformanceTests
{
    private static readonly double RmsLimit = Math.Pow(2, -15) / Math.Sqrt(12);
    private static readonly double MaxLimit = Math.Pow(2, -14);

    public static TheoryData<string> Exact()
    {
        var d = new TheoryData<string>();
        foreach (var n in AacTestEnv.Names(0)) if (!n.StartsWith("al18_", StringComparison.Ordinal) && !n.StartsWith("al19_", StringComparison.Ordinal)) d.Add(n);
        return d;
    }

    public static TheoryData<string> Noise()
    {
        var d = new TheoryData<string>();
        foreach (var n in AacTestEnv.Names(0)) if (n.StartsWith("al18_", StringComparison.Ordinal) || n.StartsWith("al19_", StringComparison.Ordinal)) d.Add(n);
        return d;
    }

    public static TheoryData<string> OutOfScope()
    {
        var d = new TheoryData<string>();
        foreach (var n in AacTestEnv.Names(1)) d.Add(n);
        return d;
    }

    private static int Align(int oursFrames, int refFrames)
    {
        // The reference waveforms of streams without an edit list start after two frames of decoder delay (2048 samples);
        // streams with one are already trimmed.
        int lag = oursFrames - refFrames;
        Assert.True(lag is 0 or 2048, $"decoded {oursFrames} frames, reference has {refFrames}");
        return lag;
    }

    [Theory]
    [MemberData(nameof(Exact))]
    public void MeetsTheAccuracyCriterion(string name)
    {
        var ours = AacTestEnv.DecodeAll(AacTestEnv.Mp4(name), out var fmt, out var tools, out long declared);
        var reference = AacTestEnv.ReadWav(AacTestEnv.Wav(name), out int refChannels);
        Assert.Equal(refChannels, fmt.Channels);
        int c = fmt.Channels;
        int lag = Align(ours.Length / c, reference.Length / c);
        Assert.Equal(ours.Length / c, declared);
        double sum = 0, max = 0;
        int count = reference.Length;
        for (int i = 0; i < count; i++)
        {
            double d = ours[lag * c + i] - (double)reference[i];
            sum += d * d;
            max = Math.Max(max, Math.Abs(d));
        }
        double rms = Math.Sqrt(sum / count);
        double k = -Math.Log2(rms * Math.Sqrt(12));
        AacTestEnv.Row($"| {name} | {fmt.SampleRate} | {c} | {tools} | {AacTestEnv.Fmt(rms)} | {AacTestEnv.Fmt(max)} | {k:F1} | {(rms <= RmsLimit && max <= MaxLimit ? "pass" : "FAIL")} |");
        Assert.True(rms <= RmsLimit, $"{name}: RMS {rms:E3} exceeds {RmsLimit:E3} (K={k:F1})");
        Assert.True(max <= MaxLimit, $"{name}: max difference {max:E3} exceeds {MaxLimit:E3}");
    }

    /// <summary>
    /// Perceptual noise substitution fills bands with noise whose waveform the standard leaves to the decoder (two correct
    /// decoders disagree sample by sample), so these streams are held to the level the standard does fix: the long-term
    /// energy in each of 16 equal frequency bands must be within 0.25 dB of the reference's, and the overall level within 0.05 dB.
    /// </summary>
    [Theory]
    [MemberData(nameof(Noise))]
    public void NoiseSubstitutionMatchesTheReferenceEnergy(string name)
    {
        var ours = AacTestEnv.DecodeAll(AacTestEnv.Mp4(name), out var fmt, out var tools, out _);
        var reference = AacTestEnv.ReadWav(AacTestEnv.Wav(name), out _);
        int lag = Align(ours.Length, reference.Length);
        int n = 1 << 20;
        var a = BandEnergies(ours.AsSpan(lag, n), 16);
        var b = BandEnergies(reference.AsSpan(0, n), 16);
        double worst = 0, ta = 0, tb = 0;
        for (int i = 0; i < 16; i++)
        {
            worst = Math.Max(worst, Math.Abs(10 * Math.Log10(a[i] / b[i])));
            ta += a[i];
            tb += b[i];
        }
        double total = Math.Abs(10 * Math.Log10(ta / tb));
        AacTestEnv.Row($"| {name} | {fmt.SampleRate} | {fmt.Channels} | {tools} | band energy within {worst:F2} dB, total {total:F3} dB | - | - | {(worst <= 0.25 && total <= 0.05 ? "pass" : "FAIL")} |");
        Assert.True(worst <= 0.25, $"{name}: a band's energy differs by {worst:F2} dB");
        Assert.True(total <= 0.05, $"{name}: overall energy differs by {total:F3} dB");
    }

    private static double[] BandEnergies(ReadOnlySpan<float> x, int bands)
    {
        int n = x.Length;
        var re = new double[n];
        var im = new double[n];
        for (int i = 0; i < n; i++) re[i] = x[i];
        Fft(re, im);
        var e = new double[bands];
        int half = n / 2;
        for (int k = 0; k < half; k++) e[k * bands / half] += re[k] * re[k] + im[k] * im[k];
        return e;
    }

    private static void Fft(double[] re, double[] im)
    {
        int n = re.Length;
        for (int i = 1, j = 0; i < n; i++)
        {
            int bit = n >> 1;
            for (; (j & bit) != 0; bit >>= 1) j ^= bit;
            j ^= bit;
            if (i < j) { (re[i], re[j]) = (re[j], re[i]); (im[i], im[j]) = (im[j], im[i]); }
        }
        for (int len = 2; len <= n; len <<= 1)
        {
            double ang = -2 * Math.PI / len;
            double wr = Math.Cos(ang), wi = Math.Sin(ang);
            for (int i = 0; i < n; i += len)
            {
                double cr = 1, ci = 0;
                for (int k = 0; k < len / 2; k++)
                {
                    int u = i + k, v = i + k + len / 2;
                    double tr = re[v] * cr - im[v] * ci, ti = re[v] * ci + im[v] * cr;
                    re[v] = re[u] - tr; im[v] = im[u] - ti;
                    re[u] += tr; im[u] += ti;
                    (cr, ci) = (cr * wr - ci * wi, cr * wi + ci * wr);
                }
            }
        }
    }

    public static TheoryData<string> Implicit()
    {
        var d = new TheoryData<string>();
        foreach (var n in AacTestEnv.Names(2)) d.Add(n);
        return d;
    }

    /// <summary>A plain LC config whose frames carry SBR (and parametric stereo) in fill elements decodes as its core and says so.</summary>
    [Theory]
    [MemberData(nameof(Implicit))]
    public void ImplicitSbrDecodesAsTheCoreAndIsFlagged(string name)
    {
        var pcm = AacTestEnv.DecodeAll(AacTestEnv.Mp4(name), out var fmt, out var tools, out long declared);
        Assert.Equal(declared * fmt.Channels, pcm.Length);
        Assert.True(declared > 0);
        Assert.True(tools.HasFlag(AacTools.SbrFill));
        float peak = 0;
        foreach (var v in pcm) peak = Math.Max(peak, Math.Abs(v));
        Assert.InRange(peak, 0.01f, 4f);
        AacTestEnv.Row($"| {name} | {fmt.SampleRate} | {fmt.Channels} | {tools} | implicit SBR skipped, {declared} frames at the core rate | - | - | decoded |");
    }

    [Theory]
    [MemberData(nameof(OutOfScope))]
    public void StreamsOutsideTheSupportedSetFailWithATypedError(string name)
    {
        string path = AacTestEnv.Mp4(name);
        using var f = File.OpenRead(path);
        var ex = Assert.Throws<UnsupportedAudioFormatException>(() => AudioDecoder.Open(f, AacTestEnv.Registry, leaveOpen: true));
        AacTestEnv.Row($"| {name} | out of scope | | | | | | {ex.Message} |");
    }
}
