using Xunit;

namespace OneAudio.Tests;

/// <summary>Shared helpers for the MP3 tests: registry, corpus lookup, whole-file decoding and error metrics.</summary>
internal static class Mp3TestEnv
{
    public static readonly AudioCodecRegistry Registry = new AudioCodecRegistry().AddMp3();

    public static string? CorpusDir => Environment.GetEnvironmentVariable("ONEAUDIO_CORPUS");

    public static readonly string[] ComplianceNames =
        ["compl", "he_32khz", "he_44khz", "he_48khz", "he_free", "he_mode", "hecommon", "si", "si_block", "si_huff", "sin1k0db"];

    public static TheoryData<string> ComplianceData()
    {
        var d = new TheoryData<string>();
        foreach (var n in ComplianceNames) d.Add(n);
        return d;
    }

    public static string ComplianceBit(string name) => Path.Combine(CorpusDir!, $"mp3-iso-{name}-bit", name + ".bit");
    public static string CompliancePcm(string name) => Path.Combine(CorpusDir!, $"mp3-iso-{name}-pcm", name + ".pcm");

    public static void RequireCompliance(string name) =>
        Assert.SkipUnless(CorpusDir is not null && File.Exists(ComplianceBit(name)) && File.Exists(CompliancePcm(name)),
            "ISO/IEC 11172-4 streams not fetched (run tools/fetch-corpus.sh, set ONEAUDIO_CORPUS).");

    /// <summary>Decodes a whole stream to interleaved floats.</summary>
    public static float[] DecodeAll(Stream s, out AudioFormat format, AudioLimits? limits = null)
    {
        using var dec = AudioDecoder.Open(s, Registry, limits, leaveOpen: true);
        format = dec.Format;
        return ReadAll(dec);
    }

    public static float[] ReadAll(IAudioDecoder dec)
    {
        var list = new List<float>();
        var buf = new float[4096 * dec.Format.Channels];
        int n;
        while ((n = dec.Read(buf)) > 0) list.AddRange(buf.AsSpan(0, n * dec.Format.Channels).ToArray());
        return [.. list];
    }

    /// <summary>
    /// Decodes every frame at its own channel count and concatenates the results, which is what a reference decoder writes for
    /// a stream whose channel mode changes (the public decoder holds the output channel count fixed instead).
    /// </summary>
    public static float[] DecodeNative(string path)
    {
        using var fs = File.OpenRead(path);
        var reader = new OneAudio.Mp3.Mp3FrameReader(fs);
        var layer3 = new OneAudio.Mp3.Mp3Layer3(OneAudio.Mp3.Mp3FrameReader.MaxFrameBytes);
        var all = new List<float>();
        var pcm = new float[1152 * 2];
        bool locked = false;
        while (reader.Next(out var h, out int length, long.MaxValue))
        {
            if (!locked) { reader.Lock(h); locked = true; }
            layer3.DecodeFrame(h, reader.Peek(length), pcm);
            reader.Advance(length);
            all.AddRange(pcm.AsSpan(0, h.SamplesPerFrame * h.Channels).ToArray());
        }
        return [.. all];
    }

    public static float[] DecodeFile(string path, out AudioFormat format)
    {
        using var fs = File.OpenRead(path);
        return DecodeAll(fs, out format);
    }

    public static short[] ReadPcm16(string path)
    {
        var bytes = File.ReadAllBytes(path);
        var s = new short[bytes.Length / 2];
        for (int i = 0; i < s.Length; i++) s[i] = (short)(bytes[2 * i] | bytes[2 * i + 1] << 8);
        return s;
    }

    /// <summary>Records a measurement in the test diagnostics, and appends it to the file named by ONEAUDIO_REPORT when set.</summary>
    public static void Report(string line)
    {
        Console.WriteLine(line);
        TestContext.Current.SendDiagnosticMessage(line);
        if (Environment.GetEnvironmentVariable("ONEAUDIO_REPORT") is { Length: > 0 } f) File.AppendAllText(f, line + Environment.NewLine);
    }

    public static (double Rms, double Max) Compare(ReadOnlySpan<float> a, ReadOnlySpan<float> b)
    {
        int n = Math.Min(a.Length, b.Length);
        double sum = 0, max = 0;
        for (int i = 0; i < n; i++)
        {
            double d = Math.Abs((double)a[i] - b[i]);
            sum += d * d;
            if (d > max) max = d;
        }
        return (n == 0 ? 0 : Math.Sqrt(sum / n), max);
    }
}
