using System.Diagnostics;
using Xunit;

namespace OneAudio.Tests;

/// <summary>
/// A short burst followed by digital silence (or silence followed by a burst that ends the file) must survive decoding.
/// The files in testdata/transients come from tools/gen-transient-testdata.py; file names carry the burst position:
/// <c>kind_channels_rate_start_length</c>. Vorbis is compared with libvorbis, Opus with the libopus decoder run by
/// ffmpeg, FLAC bit for bit with the generating signal (re-synthesised from the same name).
/// </summary>
public class TransientTests
{
    private static readonly AudioCodecRegistry Registry = new AudioCodecRegistry().AddOgg().AddVorbis().AddOpus().AddFlac();

    private static string Dir => Path.Combine(AppContext.BaseDirectory, "testdata", "transients");

    private static TheoryData<string> Files(string ext)
    {
        var d = new TheoryData<string>();
        foreach (var f in Directory.GetFiles(Dir, "*." + ext).Order(StringComparer.Ordinal)) d.Add(Path.GetFileName(f));
        return d;
    }

    public static TheoryData<string> VorbisFiles() => Files("ogg");
    public static TheoryData<string> OpusFiles() => Files("opus");
    public static TheoryData<string> FlacFiles() => Files("flac");

    /// <summary>Channels, rate, burst start and burst length from a file name.</summary>
    private static (int Channels, int Rate, int Start, int Length) Parse(string name)
    {
        var p = Path.GetFileNameWithoutExtension(name).Split('_');
        return (int.Parse(p[2].Replace("ch", "")), int.Parse(p[3]), int.Parse(p[4]), int.Parse(p[5]));
    }

    private static float[] ReadAll(IAudioDecoder d)
    {
        var all = new List<float>();
        var buf = new float[4096 * d.Format.Channels];
        int n;
        while ((n = d.Read(buf)) > 0) all.AddRange(buf.AsSpan(0, n * d.Format.Channels).ToArray());
        return all.ToArray();
    }

    private static void AssertBurstPresent(string name, float[] pcm, int channels, int start, int length)
    {
        // The original burst's peak, re-synthesised from the name; a lossy codec keeps at least a third of it.
        var kind = name.Split('_')[0] is "vorbis" or "opus" or "flac" ? name.Split('_')[1] : "sine2k";
        int rate = name.Split('_').Length > 3 && int.TryParse(name.Split('_')[3], out var r) ? r : 48000;
        float expected = Synthesize(kind, channels, rate, start, length).Max(v => Math.Abs(v)) / 32768f;
        int from = Math.Max(0, start - 64) * channels, to = Math.Min(pcm.Length / channels, start + length + 64) * channels;
        float peak = 0;
        for (int i = from; i < to; i++) peak = Math.Max(peak, Math.Abs(pcm[i]));
        Assert.True(peak > expected / 3, $"{name}: burst missing, peak {peak:F4} in frames {start}..{start + length}");
        Assert.True(peak < expected * 1.5f + 0.05f, $"{name}: peak {peak:F4}");
    }

    private static string? VorbisHarness
    {
        get
        {
            var refs = Environment.GetEnvironmentVariable("ONEAUDIO_REFS");
            var path = refs is null ? null : Path.Combine(refs, "bin", "vorbis_ref_float");
            return path is not null && File.Exists(path) ? path : null;
        }
    }

    [Theory]
    [MemberData(nameof(VorbisFiles))]
    public void Vorbis_burst_survives_and_matches_libvorbis(string name)
    {
        var (ch, rate, start, length) = Parse(name);
        using var d = AudioDecoder.OpenFile(Path.Combine(Dir, name), Registry);
        Assert.Equal(rate, d.Format.SampleRate);
        Assert.Equal(ch, d.Format.Channels);
        var ours = ReadAll(d);
        Assert.Equal(rate / 2, ours.Length / ch);
        AssertBurstPresent(name, ours, ch, start, length);

        Assert.SkipUnless(VorbisHarness is not null, "libvorbis reference harness not built (run tools/vorbis-refs.sh, set ONEAUDIO_REFS).");
        string tmp = Path.Combine(Path.GetTempPath(), "oneaudio-transients");
        Directory.CreateDirectory(tmp);
        string prefix = Path.Combine(tmp, Path.GetFileNameWithoutExtension(name));
        var psi = new ProcessStartInfo(VorbisHarness!, [Path.Combine(Dir, name), prefix]) { RedirectStandardError = true };
        using (var proc = Process.Start(psi)!)
        {
            proc.WaitForExit();
            Assert.Equal(0, proc.ExitCode);
        }
        var bytes = File.ReadAllBytes(prefix + ".f32");
        var refF = new float[bytes.Length / 4];
        Buffer.BlockCopy(bytes, 0, refF, 0, bytes.Length);
        Assert.Equal(refF.Length, ours.Length);
        // Mono and stereo need no channel reordering.
        double maxErr = 0, sig = 0, err = 0;
        for (int i = 0; i < ours.Length; i++)
        {
            double e = ours[i] - refF[i];
            maxErr = Math.Max(maxErr, Math.Abs(e));
            sig += (double)refF[i] * refF[i];
            err += e * e;
        }
        double snr = err == 0 ? double.PositiveInfinity : 10 * Math.Log10(sig / err);
        Assert.True(maxErr < 2e-6, $"{name}: max abs error {maxErr:g4}");
        Assert.True(snr >= 125, $"{name}: SNR {snr:F1} dB");
    }

    private static float[]? FfmpegDecode(string path)
    {
        try
        {
            var psi = new ProcessStartInfo("ffmpeg") { RedirectStandardOutput = true, RedirectStandardError = true };
            foreach (var a in new[] { "-v", "error", "-c:a", "libopus", "-i", path, "-f", "f32le", "-c:a", "pcm_f32le", "-" }) psi.ArgumentList.Add(a);
            using var p = Process.Start(psi)!;
            using var ms = new MemoryStream();
            p.StandardOutput.BaseStream.CopyTo(ms);
            p.WaitForExit();
            if (p.ExitCode != 0) return null;
            var bytes = ms.ToArray();
            var f = new float[bytes.Length / 4];
            Buffer.BlockCopy(bytes, 0, f, 0, f.Length * 4);
            return f;
        }
        catch (System.ComponentModel.Win32Exception)
        {
            return null;
        }
    }

    [Theory]
    [MemberData(nameof(OpusFiles))]
    public void Opus_burst_survives_and_matches_libopus(string name)
    {
        var (ch, rate, start, length) = Parse(name);
        using var d = AudioDecoder.OpenFile(Path.Combine(Dir, name), Registry);
        Assert.Equal(ch, d.Format.Channels);
        var ours = ReadAll(d);
        Assert.Equal(rate / 2, ours.Length / ch);
        AssertBurstPresent(name, ours, ch, start, length);

        var oracle = FfmpegDecode(Path.Combine(Dir, name));
        if (oracle is null) return;
        Assert.Equal(oracle.Length, ours.Length);
        double maxErr = 0;
        for (int i = 0; i < ours.Length; i++) maxErr = Math.Max(maxErr, Math.Abs(ours[i] - oracle[i]));
        Assert.True(maxErr < 1e-4, $"{name}: max difference from libopus {maxErr:g4}");
    }

    /// <summary>The 16-bit signal the generator writes: same burst shapes, integer-exact.</summary>
    private static int[] Synthesize(string kind, int ch, int rate, int start, int length)
    {
        int frames = rate / 2;
        var pcm = new int[frames * ch];
        uint rng = 12345;
        for (int i = 0; i < length; i++)
        {
            double v;
            if (kind == "click")
            {
                rng = (uint)((rng * 1103515245UL + 12345UL) & 0x7FFFFFFF);
                v = (rng / (double)0x3FFFFFFF - 1.0) * Math.Exp(-4.0 * i / length);
            }
            else
            {
                double f = kind == "sine2k" ? 2000.0 : 1000.0;
                v = Math.Min(1.0, (length - i) / 16.0) * Math.Sin(2 * Math.PI * f * (i + 0.5) / rate);
            }
            int s = (int)Math.Clamp(Math.Round(0.8 * v * 32767), -32768, 32767);
            for (int c = 0; c < ch; c++) pcm[(start + i) * ch + c] = s;
        }
        return pcm;
    }

    [Theory]
    [MemberData(nameof(FlacFiles))]
    public void Flac_file_is_bit_exact_and_our_encoder_round_trips(string name)
    {
        var (ch, rate, start, length) = Parse(name);
        var kind = Path.GetFileNameWithoutExtension(name).Split('_')[1];
        var expected = Synthesize(kind, ch, rate, start, length);

        using (var d = AudioDecoder.OpenFile(Path.Combine(Dir, name), Registry))
            Assert.Equal(expected, ReadAllInteger(d));

        // Our encoder: same samples in, same samples out.
        var ms = new MemoryStream();
        using (var enc = new OneAudio.Flac.FlacEncoder(ms, new AudioFormat(rate, ch), new OneAudio.Flac.FlacEncoderSettings { BitsPerSample = 16 }, leaveOpen: true))
        {
            enc.WriteInteger(expected);
            enc.Finish();
        }
        ms.Position = 0;
        using var back = AudioDecoder.Open(ms, Registry);
        var got = ReadAllInteger(back);
        Assert.Equal(expected, got);
        Assert.Contains(got, v => Math.Abs(v) > 3000);
    }

    private static int[] ReadAllInteger(IAudioDecoder d)
    {
        var all = new List<int>();
        var buf = new int[4096 * d.Format.Channels];
        int n;
        while ((n = d.ReadInteger(buf)) > 0) all.AddRange(buf.AsSpan(0, n * d.Format.Channels).ToArray());
        return all.ToArray();
    }

    [Theory]
    [InlineData(1, 0, 200)]
    [InlineData(2, 0, 16)]
    [InlineData(2, -1, 500)]
    [InlineData(1, 12000, 64)]
    public void Opus_written_by_OpusFileWriter_keeps_the_burst(int ch, int start, int length)
    {
        const int rate = 48000;
        if (start < 0) start = rate / 2 - length;
        var pcm = Array.ConvertAll(Synthesize("sine2k", ch, rate, start, length), s => s / 32768f);
        var ms = new MemoryStream();
        using (var w = new OneAudio.Opus.OpusFileWriter(ms, new OneAudio.Opus.OpusFileWriterSettings { Channels = ch }, leaveOpen: true))
        {
            w.Write(pcm);
            w.Finish();
        }
        ms.Position = 0;
        using var d = AudioDecoder.Open(ms, Registry);
        var got = ReadAll(d);
        Assert.Equal(pcm.Length, got.Length);
        AssertBurstPresent("OpusFileWriter", got, ch, start, length);

        string path = Path.Combine(Path.GetTempPath(), $"oneaudio-transient-{ch}-{start}.opus");
        File.WriteAllBytes(path, ms.ToArray());
        var oracle = FfmpegDecode(path);
        if (oracle is null) return;
        Assert.Equal(oracle.Length, got.Length);
        double maxErr = 0;
        for (int i = 0; i < got.Length; i++) maxErr = Math.Max(maxErr, Math.Abs(got[i] - oracle[i]));
        Assert.True(maxErr < 1e-4, $"max difference from libopus {maxErr:g4}");
    }
}
