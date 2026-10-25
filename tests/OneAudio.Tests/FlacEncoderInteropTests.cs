using System.Diagnostics;
using OneAudio.Flac;
using Xunit;

namespace OneAudio.Tests;

/// <summary>Our FLAC output read by a second independent decoder (ffmpeg, used as a black box).</summary>
public class FlacEncoderInteropTests
{
    [Theory]
    [InlineData(8, 2, 44100, 0)]
    [InlineData(16, 1, 48000, 5)]
    [InlineData(16, 2, 44100, 8)]
    [InlineData(20, 2, 44100, 2)]
    [InlineData(24, 2, 96000, 5)]
    [InlineData(24, 6, 48000, 5)]
    [InlineData(32, 2, 44100, 5)]
    public void IndependentDecoder_ReadsTheSameSamples(int bits, int channels, int rate, int level)
    {
        string? ffmpeg = null;
        foreach (var dir in (Environment.GetEnvironmentVariable("PATH") ?? "").Split(':'))
            if (File.Exists(Path.Combine(dir, "ffmpeg"))) { ffmpeg = Path.Combine(dir, "ffmpeg"); break; }
        Assert.SkipUnless(ffmpeg is not null, "ffmpeg not available.");

        var src = EncoderSignals.Make("music", bits, channels, 30000, rate);
        var flac = EncoderSignals.Encode(src, bits, channels, rate, new FlacEncoderSettings { CompressionLevel = level });
        string path = Path.Combine(Path.GetTempPath(), "oneaudio-interop-" + Guid.NewGuid().ToString("N")[..8] + ".flac");
        File.WriteAllBytes(path, flac);
        try
        {
            var psi = new ProcessStartInfo(ffmpeg!) { RedirectStandardOutput = true, RedirectStandardError = true, UseShellExecute = false };
            foreach (var a in new[] { "-v", "error", "-i", path, "-f", "s32le", "-" }) psi.ArgumentList.Add(a);
            using var p = Process.Start(psi)!;
            using var o = new MemoryStream();
            p.StandardOutput.BaseStream.CopyTo(o);
            string err = p.StandardError.ReadToEnd();
            p.WaitForExit();
            Assert.True(p.ExitCode == 0 && err.Length == 0, err);
            var raw = o.ToArray();
            Assert.Equal(src.Length * 4, raw.Length);
            for (int i = 0; i < src.Length; i++)
            {
                int expected = bits == 32 ? src[i] : src[i] << (32 - bits);
                Assert.Equal(expected, System.Buffers.Binary.BinaryPrimitives.ReadInt32LittleEndian(raw.AsSpan(4 * i)));
            }
        }
        finally { File.Delete(path); }
    }
}
