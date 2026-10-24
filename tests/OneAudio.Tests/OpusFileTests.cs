using System.Diagnostics;
using Xunit;

namespace OneAudio.Tests;

/// <summary>
/// End to end: Ogg Opus files made by an independent encoder, opened through the format-detecting entry point.
/// When ffmpeg is on the PATH its decoder output is the oracle; otherwise only structure is checked.
/// </summary>
public class OpusFileTests
{
    private static string Path(string name) => System.IO.Path.Combine(AppContext.BaseDirectory, "testdata", "opus", name);

    private static IAudioDecoder Open(string name) =>
        AudioDecoder.OpenFile(Path(name), new AudioCodecRegistry().AddOgg().AddOpus());

    private static float[] ReadAll(IAudioDecoder d)
    {
        var all = new List<float>();
        var buf = new float[4096 * d.Format.Channels];
        int n;
        while ((n = d.Read(buf)) > 0) all.AddRange(buf.AsSpan(0, n * d.Format.Channels).ToArray());
        return all.ToArray();
    }

    private static float[]? FfmpegDecode(string name)
    {
        try
        {
            var psi = new ProcessStartInfo("ffmpeg") { RedirectStandardOutput = true, RedirectStandardError = true };
            foreach (var a in new[] { "-v", "error", "-c:a", "libopus", "-i", Path(name), "-f", "f32le", "-c:a", "pcm_f32le", "-" }) psi.ArgumentList.Add(a);
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
    [InlineData("celt-stereo.opus", 2)]
    [InlineData("silk-mono.opus", 1)]
    [InlineData("hybrid-mono.opus", 1)]
    public void File_decodes_to_the_announced_length_and_matches_an_independent_decoder(string name, int channels)
    {
        using var d = Open(name);
        Assert.Equal(48000, d.Format.SampleRate);
        Assert.Equal(channels, d.Format.Channels);
        Assert.Equal(48000, d.LengthFrames);
        var got = ReadAll(d);
        Assert.Equal(48000 * channels, got.Length);
        Assert.Equal(48000, d.PositionFrames);
        Assert.All(got, v => Assert.True(float.IsFinite(v) && Math.Abs(v) < 2f));
        double rms = Math.Sqrt(got.Sum(v => (double)v * v) / got.Length);
        Assert.InRange(rms, 0.05, 0.6);

        var oracle = FfmpegDecode(name);
        if (oracle is null) return;
        Assert.Equal(oracle.Length, got.Length);
        double maxErr = 0;
        for (int i = 0; i < got.Length; i++) maxErr = Math.Max(maxErr, Math.Abs(got[i] - oracle[i]));
        Assert.True(maxErr < 1e-4, $"max difference from the libopus decoder run by ffmpeg {maxErr}");
    }

    [Theory]
    [InlineData("celt-stereo.opus")]
    [InlineData("silk-mono.opus")]
    [InlineData("hybrid-mono.opus")]
    public void Seek_lands_on_the_requested_frame(string name)
    {
        using var d = Open(name);
        int ch = d.Format.Channels;
        var all = ReadAll(d);
        Assert.True(d.CanSeek);
        foreach (long target in new long[] { 0, 1, 959, 960, 20000, 33333, 47000, 47999 })
        {
            d.Seek(target);
            Assert.Equal(target, d.PositionFrames);
            var buf = new float[ch * 500];
            int n = d.Read(buf);
            Assert.Equal(Math.Min(500, 48000 - target), n);
            // 80 ms of pre-roll lets the decoder state converge; a steady tone converges to well under a quantiser step.
            for (int i = 0; i < n * ch; i++)
                Assert.True(Math.Abs(buf[i] - all[target * ch + i]) < 5e-3, $"{name} seek {target} sample {i}");
        }
        d.Seek(48000);
        Assert.Equal(0, d.Read(new float[ch * 10]));
    }
}
