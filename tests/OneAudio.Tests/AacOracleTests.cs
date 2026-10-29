using System.Diagnostics;
using System.Globalization;
using Xunit;

namespace OneAudio.Tests;

/// <summary>
/// Our AAC-LC decoder against a second, independent decoder run as a black box (ffmpeg), on files made by ffmpeg's own AAC
/// encoder from generated signals: mono and stereo, 8 to 48 kHz, constant and variable bitrate, in M4A, MP4, 3GP and ADTS.
/// The decoded length (after priming and trim) must be identical, the largest sample difference tiny and the SNR very high.
/// </summary>
public class AacOracleTests
{
    // name, sample rate, channels, encoder arguments, output arguments (container), extension
    public static TheoryData<string, int, int, string, string, string> Cases => new()
    {
        { "cbr128-44k-stereo", 44100, 2, "-b:a 128k", "-f ipod", ".m4a" },
        { "vbr-q2-48k-stereo", 48000, 2, "-q:a 2", "-f mp4", ".mp4" },
        { "cbr64-32k-mono", 32000, 1, "-b:a 64k", "-f ipod", ".m4a" },
        { "cbr48-22k-stereo", 22050, 2, "-b:a 48k", "-f ipod", ".m4a" },
        { "cbr32-16k-mono", 16000, 1, "-b:a 32k", "-f ipod", ".m4a" },
        { "cbr24-11k-mono-3gp", 11025, 1, "-b:a 24k", "-f 3gp", ".3gp" },
        { "cbr16-8k-mono", 8000, 1, "-b:a 16k", "-f ipod", ".m4a" },
        { "cbr64-24k-stereo-adts", 24000, 2, "-b:a 64k", "-f adts", ".aac" },
        { "vbr-q1-44k-mono-ms", 44100, 1, "-q:a 1 -aac_tns 1", "-f ipod", ".m4a" },
        { "cbr20-12k-mono", 12000, 1, "-b:a 20k", "-f ipod", ".m4a" },
        { "cbr96-44k-stereo-is-ms", 44100, 2, "-b:a 96k -aac_is 1 -aac_ms 1 -aac_pns 0", "-f mp4", ".mp4" },
    };

    private static string Scratch => Environment.GetEnvironmentVariable("TMPDIR") is { Length: > 0 } t ? t : Path.GetTempPath();

    private static void Ffmpeg(params string[] args)
    {
        var psi = new ProcessStartInfo(ContainerTestEnv.Ffmpeg!) { RedirectStandardError = true, RedirectStandardOutput = true };
        psi.ArgumentList.Add("-v"); psi.ArgumentList.Add("error"); psi.ArgumentList.Add("-y");
        foreach (var a in args) psi.ArgumentList.Add(a);
        using var p = Process.Start(psi)!;
        var err = p.StandardError.ReadToEndAsync();
        p.StandardOutput.ReadToEnd();
        p.WaitForExit();
        Assert.True(p.ExitCode == 0, "ffmpeg failed: " + err.Result);
    }

    private static float[] ReadRaw(string path)
    {
        var b = File.ReadAllBytes(path);
        var f = new float[b.Length / 4];
        Buffer.BlockCopy(b, 0, f, 0, f.Length * 4);
        return f;
    }

    private static string Signal(int rate, int channels)
    {
        // A sweep, a tone burst, a click train (to force short blocks) and a little noise.
        string left = "0.3*sin(2*PI*t*(200+500*t))+0.2*sin(2*PI*1500*t)*lt(t,1.2)+0.6*lt(mod(t,0.37),0.002)+0.05*(random(0)-0.5)";
        string right = "0.25*sin(2*PI*t*(300+300*t))+0.15*sin(2*PI*900*t)*gt(t,1)+0.05*(random(1)-0.5)";
        string expr = channels == 2 ? left + "|" + right : left;
        return $"aevalsrc={expr.Replace(",", "\\,", StringComparison.Ordinal)}:s={rate}:d=3"; // commas inside the expression must be escaped
    }

    [Theory]
    [MemberData(nameof(Cases))]
    public void DecodesLikeFfmpeg(string name, int rate, int channels, string encoderArgs, string muxerArgs, string ext)
    {
        Assert.SkipUnless(ContainerTestEnv.Ffmpeg is not null && File.Exists(ContainerTestEnv.Ffmpeg), "ffmpeg not found.");
        string file = Path.Combine(Scratch, "oneaudio-aac-" + name + ext);
        string raw = Path.Combine(Scratch, "oneaudio-aac-" + name + ".f32");
        try
        {
            var enc = new List<string> { "-f", "lavfi", "-i", Signal(rate, channels), "-c:a", "aac" };
            enc.AddRange(encoderArgs.Split(' '));
            // Noise substitution is left to each decoder's own noise, so samples only compare without it (the conformance streams cover it).
            if (!encoderArgs.Contains("aac_pns", StringComparison.Ordinal)) enc.AddRange(["-aac_pns", "0"]);
            enc.AddRange(muxerArgs.Split(' '));
            enc.Add(file);
            Ffmpeg(enc.ToArray());
            Ffmpeg("-i", file, "-f", "f32le", "-acodec", "pcm_f32le", raw);
            var theirs = ReadRaw(raw);

            var ours = AacTestEnv.DecodeAll(file, out var fmt, out var tools, out long declared);
            Assert.Equal(rate, fmt.SampleRate);
            Assert.Equal(channels, fmt.Channels);
            Assert.Equal(declared * channels, ours.Length);
            // After priming and trim the length is the true length of the 3 s input. ffmpeg trims the start from the edit list but
            // leaves the end padding, so it returns a little more; an ADTS file has no trim information and both return whole frames.
            if (ext == ".aac") Assert.Equal(theirs.Length, ours.Length);
            else
            {
                Assert.Equal(3 * rate, ours.Length / channels);
                Assert.True(theirs.Length >= ours.Length);
            }
            double maxAbs = 0, err = 0, sig = 0;
            for (int i = 0; i < ours.Length; i++)
            {
                double d = ours[i] - theirs[i];
                maxAbs = Math.Max(maxAbs, Math.Abs(d));
                err += d * d;
                sig += (double)theirs[i] * theirs[i];
            }
            double snr = 10 * Math.Log10(sig / Math.Max(err, 1e-30));
            AacTestEnv.Row(string.Create(CultureInfo.InvariantCulture, $"| {name} | {rate} | {channels} | {tools} | {declared} frames | max abs {maxAbs:E2} | SNR {snr:F1} dB | |"));
            Assert.True(maxAbs < 1e-5, $"{name}: max difference {maxAbs:E3}");
            Assert.True(snr > 100, $"{name}: SNR {snr:F1} dB");
        }
        finally
        {
            File.Delete(file);
            File.Delete(raw);
        }
    }

    [Fact]
    public void GaplessM4aMatchesFfmpegLengthAndSamples()
    {
        Assert.SkipUnless(ContainerTestEnv.Ffmpeg is not null && File.Exists(ContainerTestEnv.Ffmpeg), "ffmpeg not found.");
        string file = ContainerTestEnv.TestData("aac-itunes-gapless.m4a");
        string raw = Path.Combine(Scratch, "oneaudio-aac-gapless.f32");
        try
        {
            Ffmpeg("-i", file, "-f", "f32le", "-acodec", "pcm_f32le", raw);
            var theirs = ReadRaw(raw);
            var ours = AacTestEnv.DecodeAll(file, out var fmt, out _, out long declared);
            // The iTunSMPB tag of this file says 0x840 (2112) frames of delay, 0x1D8 of padding and 0xFDE8 = 65000 valid frames.
            Assert.Equal(65000, declared);
            Assert.Equal(65000 * fmt.Channels, ours.Length);
            // ffmpeg honours the same iTunSMPB delay (2112 frames) but leaves the end padding. The file was encoded with noise
            // substitution on, whose noise differs between decoders, so the samples agree only to the level of that noise.
            double err = 0, sig = 0, maxAbs = 0;
            for (int i = 0; i < ours.Length; i++)
            {
                double d = ours[i] - theirs[i];
                err += d * d;
                sig += (double)theirs[i] * theirs[i];
                maxAbs = Math.Max(maxAbs, Math.Abs(d));
            }
            double snr = 10 * Math.Log10(sig / err);
            Assert.True(snr > 40, $"SNR {snr:F1} dB");
            AacTestEnv.Row(string.Create(CultureInfo.InvariantCulture, $"| gapless-itunes (noise substitution on) | {fmt.SampleRate} | {fmt.Channels} | | {declared} frames (iTunSMPB) | max abs {maxAbs:E2} | SNR {snr:F1} dB | |"));
        }
        finally
        {
            File.Delete(raw);
        }
    }
}
