using System.Diagnostics;
using OneAudio.Opus;
using Xunit;

namespace OneAudio.Tests;

/// <summary>Ogg Opus files from <see cref="OpusFileWriter"/>: length, quality, rate, channel order and playback by other decoders.</summary>
public class OpusFileWriterTests
{
    private static IAudioDecoder Open(byte[] file) =>
        AudioDecoder.Open(new MemoryStream(file), new AudioCodecRegistry().AddOgg().AddOpus());

    private static float[] ReadAll(IAudioDecoder d)
    {
        var all = new List<float>();
        var buf = new float[4800 * d.Format.Channels];
        int n;
        while ((n = d.Read(buf)) > 0) all.AddRange(buf.AsSpan(0, n * d.Format.Channels).ToArray());
        return all.ToArray();
    }

    private static byte[] Write(float[] pcm, OpusFileWriterSettings s, int chunkFrames = 1000)
    {
        var ms = new MemoryStream();
        using (var w = new OpusFileWriter(ms, s, leaveOpen: true))
        {
            for (int i = 0; i < pcm.Length; i += chunkFrames * s.Channels)
                w.Write(pcm.AsSpan(i, Math.Min(chunkFrames * s.Channels, pcm.Length - i)));
        }
        return ms.ToArray();
    }

    private static double SnrDb(float[] reference, float[] decoded, int ch)
    {
        int n = Math.Min(reference.Length, decoded.Length);
        double sig = 0, err = 0;
        for (int i = 0; i < n; i++)
        {
            sig += (double)reference[i] * reference[i];
            double e = reference[i] - decoded[i];
            err += e * e;
        }
        return 10 * Math.Log10(sig / Math.Max(err, 1e-30));
    }

    [Theory]
    [InlineData(1, 48000, 20.0, 5)]
    [InlineData(2, 48000, 20.0, 5)]
    [InlineData(2, 48000, 60.0, 7)]
    [InlineData(2, 48000, 2.5, 3)]
    [InlineData(1, 44100, 20.0, 5)]
    [InlineData(2, 16000, 10.0, 4)]
    public void LengthIsExactAndAudioSurvives(int channels, int rate, double ms, int seconds)
    {
        int frames = rate * seconds + 123;
        var src = OpusEncoderSupport.ToFloat(OpusEncoderSupport.MakeSignal(channels, frames, 2));
        var file = Write(src, new OpusFileWriterSettings
        {
            Channels = channels, InputSampleRate = rate, FrameMilliseconds = ms,
            Encoder = new OpusEncoderSettings { BitrateBps = 96000 * channels / 2, Complexity = 5 },
        });
        using var d = Open(file);
        long expected = rate == 48000 ? frames : (long)Math.Round(frames * 48000.0 / rate);
        Assert.Equal(48000, d.Format.SampleRate);
        Assert.Equal(channels, d.Format.Channels);
        // The resampler decides the exact output count; it must be within one frame of the ideal ratio.
        Assert.InRange(d.LengthFrames, expected - 1, expected + 1);
        var dec = ReadAll(d);
        Assert.Equal(d.LengthFrames * channels, dec.Length);
        if (rate == 48000)
        {
            Assert.Equal(frames * channels, dec.Length);
            Assert.True(SnrDb(src, dec, channels) > 10, $"SNR {SnrDb(src, dec, channels):F1} dB");
        }
    }

    [Fact]
    public void BitrateIsWithinFivePercentOverAMinute()
    {
        foreach (var (channels, bitrate) in new[] { (2, 64000), (2, 128000), (1, 32000) })
        {
            var src = OpusEncoderSupport.ToFloat(OpusEncoderSupport.MakeSignal(channels, 48000 * 60, 11));
            var file = Write(src, new OpusFileWriterSettings
            {
                Channels = channels, Encoder = new OpusEncoderSettings { BitrateBps = bitrate },
            }, 4800);
            double actual = file.Length * 8.0 / 60;
            Assert.InRange(actual / bitrate, 0.95, 1.05);
        }
    }

    [Theory]
    [InlineData(1, 12000, OpusApplication.Voip)]
    [InlineData(1, 24000, OpusApplication.Audio)]
    [InlineData(2, 20000, OpusApplication.Audio)]
    [InlineData(2, 40000, OpusApplication.Voip)]
    [InlineData(6, 72000, OpusApplication.Audio)]
    public void SpeechRateFilesAreExactAndNearTheTarget(int channels, int bitrate, OpusApplication app)
    {
        int frames = 48000 * 30 + 77;
        var src = OpusEncoderSupport.ToFloat(OpusEncoderSupport.MakeSignal(channels, frames, 6));
        var file = Write(src, new OpusFileWriterSettings
        {
            Channels = channels, Encoder = new OpusEncoderSettings { BitrateBps = bitrate, Application = app },
        }, 4800);
        using var d = Open(file);
        Assert.Equal(frames, d.LengthFrames);
        var dec = ReadAll(d);
        Assert.Equal(frames * channels, dec.Length);
        Assert.InRange(file.Length * 8.0 / (frames / 48000.0) / bitrate, 0.9, 1.1);
    }

    [Fact]
    public void SixChannelsKeepTheirOwnChannel()
    {
        const int ch = 6;
        int frames = 48000 * 2;
        var src = new float[frames * ch];
        // Standard speaker order: FL FR FC LFE BL BR. Put a distinct tone in each channel.
        double[] freq = [440, 660, 880, 110, 1320, 1760];
        for (int i = 0; i < frames; i++)
            for (int c = 0; c < ch; c++)
                src[i * ch + c] = (float)(0.3 * Math.Sin(2 * Math.PI * freq[c] * i / 48000));
        var file = Write(src, new OpusFileWriterSettings { Channels = ch, Encoder = new OpusEncoderSettings { BitrateBps = 192000 } });
        using var d = Open(file);
        Assert.Equal(ch, d.Format.Channels);
        var dec = ReadAll(d);
        Assert.Equal(src.Length, dec.Length);
        for (int c = 0; c < ch; c++)
        {
            double inChannel = 0, leak = 0;
            for (int i = 24000; i < frames - 24000; i++)
            {
                inChannel += Math.Pow(dec[i * ch + c] - src[i * ch + c], 2);
                leak += Math.Pow(src[i * ch + c], 2);
            }
            Assert.True(10 * Math.Log10(leak / inChannel) > 10, $"channel {c} SNR {10 * Math.Log10(leak / inChannel):F1} dB");
        }
    }

    [Fact]
    public void WritingAllocatesNothingAfterWarmUp()
    {
        foreach (int rate in new[] { 48000, 44100 })
        {
            var src = OpusEncoderSupport.ToFloat(OpusEncoderSupport.MakeSignal(2, rate * 3, 4));
            using var w = new OpusFileWriter(Stream.Null, new OpusFileWriterSettings { InputSampleRate = rate });
            for (int i = 0; i < 20; i++) w.Write(src.AsSpan(i * 2000, 2000));
            long before = GC.GetAllocatedBytesForCurrentThread();
            for (int i = 20; i < 60; i++) w.Write(src.AsSpan(i * 2000, 2000));
            Assert.Equal(0, GC.GetAllocatedBytesForCurrentThread() - before);
        }
    }

    [Fact]
    public void TagsAndHeaderAreWritten()
    {
        var ms = new MemoryStream();
        using (var w = new OpusFileWriter(ms, new OpusFileWriterSettings { Comments = ["TITLE=Test", "ARTIST=Nobody"], InputSampleRate = 44100, Channels = 1 }, true))
            w.Write(new float[441]);
        var file = ms.ToArray();
        Assert.Equal("OggS", System.Text.Encoding.ASCII.GetString(file, 0, 4));
        Assert.Equal("OpusHead", System.Text.Encoding.ASCII.GetString(file, 28, 8));
        Assert.Equal(312, BitConverter.ToUInt16(file, 28 + 10));
        Assert.Equal(44100u, BitConverter.ToUInt32(file, 28 + 12));
        Assert.Contains("ARTIST=Nobody", System.Text.Encoding.UTF8.GetString(file));
    }

    [Fact]
    public void FfmpegAndTheReferenceDecoderAgreeOnTheLength()
    {
        var ffmpeg = Environment.GetEnvironmentVariable("FFMPEG") ?? "/usr/bin/ffmpeg";
        if (!File.Exists(ffmpeg)) return;
        int frames = 44100 * 3 + 17;
        var src = OpusEncoderSupport.ToFloat(OpusEncoderSupport.MakeSignal(2, frames, 6));
        var file = Write(src, new OpusFileWriterSettings
        {
            InputSampleRate = 44100, Encoder = new OpusEncoderSettings { BitrateBps = 96000 },
        });
        var path = Path.Combine(Path.GetTempPath(), "oneaudio-" + Guid.NewGuid().ToString("N") + ".opus");
        File.WriteAllBytes(path, file);
        try
        {
            var psi = new ProcessStartInfo(ffmpeg) { RedirectStandardOutput = true, RedirectStandardError = true };
            foreach (var a in new[] { "-v", "error", "-i", path, "-f", "f32le", "-ar", "48000", "-" }) psi.ArgumentList.Add(a);
            using var p = Process.Start(psi)!;
            var ms = new MemoryStream();
            p.StandardOutput.BaseStream.CopyTo(ms);
            string err = p.StandardError.ReadToEnd();
            p.WaitForExit();
            Assert.True(p.ExitCode == 0 && err.Length == 0, err);
            using var ours = Open(file);
            Assert.Equal(ours.LengthFrames * 2 * 4, ms.Length);
        }
        finally
        {
            File.Delete(path);
        }
    }
}
