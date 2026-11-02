using System.Diagnostics;
using OneAudio.Aac;
using OneAudio.Mp4;
using Xunit;

namespace OneAudio.Tests;

public class AacEncoderTests
{
    private static string Scratch => Environment.GetEnvironmentVariable("TMPDIR") is { Length: > 0 } t ? t : Path.GetTempPath();

    /// <summary>Music-like test signal: harmonic tones with vibrato, a decaying noise burst train and a little hiss, in [-0.7, 0.7].</summary>
    internal static float[] Signal(int rate, int channels, int frames, int seed = 1)
    {
        var rng = new Random(seed);
        var s = new float[frames * channels];
        for (int c = 0; c < channels; c++)
        {
            double ph = 0;
            for (int i = 0; i < frames; i++)
            {
                double t = i / (double)rate;
                ph += 2 * Math.PI * (220 * (c + 1) + 5 * Math.Sin(2 * Math.PI * 5 * t)) / rate;
                double v = 0.25 * Math.Sin(ph) + 0.12 * Math.Sin(2 * ph) + 0.06 * Math.Sin(3 * ph);
                double burst = Math.Exp(-((t * 3) % 1) * 40);
                v += 0.3 * burst * (rng.NextDouble() - 0.5);
                v += 0.004 * (rng.NextDouble() - 0.5);
                s[i * channels + c] = (float)v;
            }
        }
        return s;
    }

    internal static List<byte[]> EncodeAll(AacEncoder enc, ReadOnlySpan<float> pcm, int chunkFrames)
    {
        var units = new List<byte[]>();
        int ch = enc.Channels;
        for (int pos = 0; pos < pcm.Length;)
        {
            var span = pcm.Slice(pos, Math.Min(chunkFrames * ch, pcm.Length - pos));
            pos += span.Length;
            while (span.Length > 0)
            {
                int taken = enc.Write(span);
                span = span[(taken * ch)..];
                while (enc.TryReadPacket(out var p)) units.Add(p.Data.ToArray());
            }
        }
        enc.Finish();
        while (enc.TryReadPacket(out var p)) units.Add(p.Data.ToArray());
        return units;
    }

    private static void WriteWav(Stream s, int rate, int channels, float[] pcm)
    {
        using var w = new WavEncoder(s, new AudioFormat(rate, channels), new WavEncoderSettings(), leaveOpen: true);
        w.Write(pcm);
        w.Finish();
    }

    private static float[] Decode(byte[] adts, out AudioFormat format)
    {
        using var dec = Audio.Open(new MemoryStream(adts));
        format = dec.Format;
        var all = new List<float>();
        var buf = new float[4096 * dec.Format.Channels];
        int n;
        while ((n = dec.Read(buf)) > 0) all.AddRange(buf.AsSpan(0, n * dec.Format.Channels).ToArray());
        return [.. all];
    }

    private static double SnrDb(float[] reference, float[] decoded, int skip = 0)
    {
        double sig = 0, err = 0;
        for (int i = skip; i < reference.Length; i++)
        {
            double d = decoded[i] - reference[i];
            sig += (double)reference[i] * reference[i];
            err += d * d;
        }
        return 10 * Math.Log10(sig / Math.Max(err, 1e-30));
    }

    [Theory]
    [InlineData(44100, 2, 128000)]
    [InlineData(48000, 2, 64000)]
    [InlineData(32000, 1, 48000)]
    [InlineData(24000, 2, 96000)]
    [InlineData(22050, 1, 32000)]
    [InlineData(16000, 1, 32000)]
    [InlineData(11025, 2, 48000)]
    [InlineData(8000, 1, 24000)]
    public void AdtsStreamDecodesToTheInputLength(int rate, int channels, int bitrate)
    {
        int frames = rate * 3 + 77;
        var pcm = Signal(rate, channels, frames);
        var enc = new AacEncoder(rate, channels, new AacEncoderSettings { Bitrate = bitrate, Adts = true });
        var units = EncodeAll(enc, pcm, 1500);
        Assert.Equal((frames + 1023) / 1024 + 1, units.Count);
        Assert.Equal(units.Count * 1024 - AacEncoder.PrimingFrames - frames, enc.EndPaddingFrames);
        var adts = units.SelectMany(u => u).ToArray();
        var dec = Decode(adts, out var fmt);
        Assert.Equal(rate, fmt.SampleRate);
        Assert.Equal(channels, fmt.Channels);
        // The ADTS reader has no edit list: the decoder returns every unit's output, priming first.
        Assert.Equal(units.Count * 1024, dec.Length / channels);
        var aligned = dec.AsSpan(AacEncoder.PrimingFrames * channels, frames * channels).ToArray();
        double snr = SnrDb(pcm, aligned);
        Assert.True(snr > 14, $"SNR {snr:F1} dB");
    }

    [Theory]
    [InlineData(64000)]
    [InlineData(128000)]
    [InlineData(192000)]
    public void CbrBitrateIsWithinFivePercent(int bitrate)
    {
        var pcm = Signal(44100, 2, 44100 * 20);
        var enc = new AacEncoder(44100, 2, new AacEncoderSettings { Bitrate = bitrate });
        var units = EncodeAll(enc, pcm, 4096);
        double seconds = units.Count * 1024 / 44100.0;
        double kbps = units.Sum(u => (long)u.Length) * 8 / seconds;
        Assert.InRange(kbps / bitrate, 0.95, 1.05);
    }

    [Fact]
    public void VbrAndAbrProduceDecodableStreamsAndVbrFollowsQuality()
    {
        var pcm = Signal(44100, 2, 44100 * 6);
        long Size(AacRateControl m, double q)
        {
            var enc = new AacEncoder(44100, 2, new AacEncoderSettings { Bitrate = 128000, RateControl = m, VbrQuality = q, Adts = true });
            var units = EncodeAll(enc, pcm, 4096);
            var dec = Decode(units.SelectMany(u => u).ToArray(), out _);
            Assert.True(SnrDb(pcm, dec.AsSpan(2048, pcm.Length).ToArray()) > 6);
            return units.Sum(u => (long)u.Length);
        }
        long low = Size(AacRateControl.Vbr, 0.1), high = Size(AacRateControl.Vbr, 0.9);
        Assert.True(high > low * 1.3, $"{low} vs {high}");
        long abr = Size(AacRateControl.Abr, 0);
        Assert.InRange(abr * 8 / 6.0 / 128000, 0.9, 1.05);
    }

    [Theory]
    [InlineData(0)]
    [InlineData(1)]
    [InlineData(1023)]
    [InlineData(1024)]
    [InlineData(1025)]
    [InlineData(2049)]
    [InlineData(5000)]
    public void OddLengthsGetTheRightUnitCountAndPadding(int frames)
    {
        var pcm = Signal(44100, 2, frames);
        var enc = new AacEncoder(44100, 2, new AacEncoderSettings { Adts = true });
        var units = EncodeAll(enc, pcm, 333);
        if (frames == 0)
        {
            Assert.Empty(units);
            return;
        }
        int expectUnits = (frames + 1023) / 1024 + 1;
        Assert.Equal(expectUnits, units.Count);
        Assert.Equal(expectUnits * 1024 - 1024 - frames, enc.EndPaddingFrames);
        Assert.InRange(enc.EndPaddingFrames, 0, 1023);
        Assert.Equal(frames, enc.FramesWritten);
        var dec = Decode(units.SelectMany(u => u).ToArray(), out _);
        Assert.Equal(expectUnits * 1024, dec.Length / 2);
    }

    [Fact]
    public void SilenceCodesToTinyUnitsAndDecodesToSilence()
    {
        // Constant bit rate pads silence up to the nominal rate; variable bit rate does not.
        var enc = new AacEncoder(44100, 2, new AacEncoderSettings { Adts = true, RateControl = AacRateControl.Vbr });
        var units = EncodeAll(enc, new float[44100 * 2 * 2], 4096);
        Assert.All(units, u => Assert.True(u.Length < 120, $"{u.Length} bytes"));
        var dec = Decode(units.SelectMany(u => u).ToArray(), out _);
        Assert.All(dec, v => Assert.Equal(0f, v));
    }

    [Theory]
    [InlineData(1)]
    [InlineData(2)]
    public void FullScaleSquareWaveAndClippingInputStayValid(int channels)
    {
        int rate = 44100, frames = rate * 2;
        var pcm = new float[frames * channels];
        for (int i = 0; i < frames; i++)
            for (int c = 0; c < channels; c++)
                pcm[i * channels + c] = ((i / (50 + c)) & 1) == 0 ? 1f : -1f;
        pcm[100] = 3f;          // beyond full scale
        pcm[200] = float.NaN;   // counts as silence
        foreach (var mode in new[] { AacRateControl.Cbr, AacRateControl.Vbr })
        {
            var enc = new AacEncoder(rate, channels, new AacEncoderSettings { Bitrate = 96000 * channels / 2 + 32000, RateControl = mode, Adts = true });
            var units = EncodeAll(enc, pcm, 1024);
            Assert.All(units, u => Assert.True(u.Length <= enc.MaxPacketBytes));
            var dec = Decode(units.SelectMany(u => u).ToArray(), out _);
            Assert.Equal(units.Count * 1024, dec.Length / channels);
            Assert.True(SnrDb(pcm.AsSpan(1000).ToArray(), dec.AsSpan(1024 * channels + 1000, pcm.Length - 1000).ToArray()) > 3);
        }
    }

    [Fact]
    public void ArgumentsAreValidatedAndMisuseIsRefused()
    {
        Assert.Throws<ArgumentException>(() => new AacEncoder(44100, 3));
        Assert.Throws<ArgumentException>(() => new AacEncoder(44100, 0));
        Assert.Throws<ArgumentException>(() => new AacEncoder(44101, 2));
        Assert.Throws<ArgumentException>(() => new AacEncoder(96000, 2));
        Assert.Throws<ArgumentException>(() => new AacEncoder(44100, 2, new AacEncoderSettings { Bitrate = 1000 }));
        Assert.Throws<ArgumentException>(() => new AacEncoder(8000, 1, new AacEncoderSettings { Bitrate = 500000 }));
        Assert.Throws<ArgumentException>(() => new AacEncoder(44100, 2, new AacEncoderSettings { VbrQuality = 2 }));
        var enc = new AacEncoder(44100, 2);
        // A mono buffer offered to a stereo encoder is an odd number of samples here: refused, nothing consumed.
        Assert.Throws<ArgumentException>(() => enc.Write(new float[1001]));
        Assert.Equal(0, enc.FramesWritten);
        enc.Write(new float[2000]);
        enc.Finish();
        enc.Finish();
        Assert.Throws<InvalidOperationException>(() => enc.Write(new float[2]));
        var asc = new AacEncoder(48000, 1).AudioSpecificConfig.ToArray();
        Assert.Equal([0x11, 0x88], asc); // AAC LC, 48 kHz (index 3), one channel
    }

    [Fact]
    public void ConfigParsesWithTheDecoderAndPacketsCarryTiming()
    {
        foreach (var (rate, ch) in new[] { (44100, 2), (22050, 1), (8000, 2) })
        {
            var enc = new AacEncoder(rate, ch);
            Assert.True(AacAudioConfig.TryParse(enc.AudioSpecificConfig.Span, out var cfg));
            Assert.Equal(2, cfg!.ObjectType);
            Assert.Equal(rate, cfg.SampleRate);
            Assert.Equal(ch, cfg.Channels);
            Assert.False(cfg.FrameLength960);
        }
        var e2 = new AacEncoder(44100, 1);
        e2.Write(new float[1024]);
        Assert.False(e2.TryReadPacket(out _)); // one hop of lookahead
        e2.Write(new float[1024]);
        Assert.True(e2.TryReadPacket(out var p0));
        Assert.Equal(0, p0.Index);
        Assert.Equal(0, p0.Timestamp);
        Assert.Equal(1024, p0.Duration);
    }

    [Fact]
    public void OutputIsDeterministic()
    {
        var pcm = Signal(44100, 2, 44100 * 2);
        byte[] Run()
        {
            var enc = new AacEncoder(44100, 2, new AacEncoderSettings { Adts = true });
            return EncodeAll(enc, pcm, 777).SelectMany(u => u).ToArray();
        }
        Assert.True(Run().AsSpan().SequenceEqual(Run()));
    }

    [Fact]
    public void SteadyStateEncodingAllocatesNothing()
    {
        foreach (var channels in new[] { 1, 2 })
        {
            var pcm = Signal(44100, channels, 44100 * 4);
            var enc = new AacEncoder(44100, channels, new AacEncoderSettings { Bitrate = 64000 * channels });
            int hop = 1024 * channels;
            // Warm up: JIT, tiered compilation and every static table.
            for (int pass = 0; pass < 2; pass++)
                for (int pos = 0; pos + hop <= pcm.Length; pos += hop)
                {
                    enc.Write(pcm.AsSpan(pos, hop));
                    while (enc.TryReadPacket(out _)) { }
                }
            long before = GC.GetAllocatedBytesForCurrentThread();
            int units = 0;
            for (int pos = 0; pos + hop <= pcm.Length; pos += hop)
            {
                enc.Write(pcm.AsSpan(pos, hop));
                while (enc.TryReadPacket(out _)) units++;
            }
            long allocated = GC.GetAllocatedBytesForCurrentThread() - before;
            Assert.True(units > 100);
            Assert.Equal(0, allocated);
        }
    }

    // ---- containers and the second decoder ------------------------------------------------------------------------------

    [Fact]
    public void M4aFromTheHappyPathPlaysBackAtTheExactLengthInOurDecoder()
    {
        int rate = 44100, frames = rate * 2 + 321;
        var pcm = Signal(rate, 2, frames);
        string wav = Path.Combine(Scratch, "aac-in-" + Guid.NewGuid().ToString("N")[..8] + ".wav");
        string m4a = Path.ChangeExtension(wav, ".m4a");
        try
        {
            using (var f = File.Create(wav)) WriteWav(f, rate, 2, pcm);
            var result = Audio.Transcode(wav, m4a);
            Assert.Equal(AudioFileFormat.Aac, result.Format);
            Assert.Equal(frames, result.Frames);
            using var dec = Audio.Open(m4a);
            Assert.Equal(frames, dec.LengthFrames);
            var got = new float[frames * 2];
            int filled = 0, n;
            while (filled < got.Length && (n = dec.Read(got.AsSpan(filled))) > 0) filled += n * 2;
            Assert.Equal(got.Length, filled);
            Assert.True(SnrDb(pcm, got) > 14);
            Assert.Equal(0, dec.Read(new float[64]));
        }
        finally { File.Delete(wav); File.Delete(m4a); }
    }

    [Fact]
    public void M4aIsAcceptedByTheSecondDecoderWithoutWarnings()
    {
        string? ffmpeg = ContainerTestEnv.Ffmpeg;
        Assert.SkipUnless(ffmpeg is not null && File.Exists(ffmpeg), "ffmpeg not found.");
        foreach (var (rate, ch, format) in new[] { (44100, 2, AudioFileFormat.Aac), (32000, 1, AudioFileFormat.Aac), (48000, 2, AudioFileFormat.AacAdts), (22050, 1, AudioFileFormat.AacAdts) })
        {
            int frames = rate * 2 + 100;
            var pcm = Signal(rate, ch, frames);
            string path = Path.Combine(Scratch, "aac-ff-" + Guid.NewGuid().ToString("N")[..8] + (format == AudioFileFormat.Aac ? ".m4a" : ".aac"));
            string raw = path + ".f32";
            try
            {
                using (var ms = new MemoryStream())
                {
                    WriteWav(ms, rate, ch, pcm);
                    ms.Position = 0;
                    using var outFile = File.Create(path);
                    Audio.Transcode(ms, outFile, new TranscodeOptions { Format = format });
                }
                var psi = new ProcessStartInfo(ffmpeg!) { RedirectStandardError = true, RedirectStandardOutput = true };
                foreach (var a in new[] { "-v", "warning", "-y", "-i", path, "-f", "f32le", raw }) psi.ArgumentList.Add(a);
                using var p = Process.Start(psi)!;
                string err = p.StandardError.ReadToEnd();
                p.StandardOutput.ReadToEnd();
                p.WaitForExit();
                Assert.Equal(0, p.ExitCode);
                // ffmpeg warns that it estimates the duration of a raw ADTS stream; anything else is a problem.
                var lines = err.Split('\n', StringSplitOptions.RemoveEmptyEntries).Where(l => !l.Contains("Estimating duration", StringComparison.Ordinal)).ToArray();
                Assert.True(lines.Length == 0, string.Join("\n", lines));
                var bytes = File.ReadAllBytes(raw);
                var dec = new float[bytes.Length / 4];
                Buffer.BlockCopy(bytes, 0, dec, 0, bytes.Length);
                // ffmpeg drops the priming of an MP4 but keeps the whole last unit; an ADTS stream keeps everything.
                int skip = format == AudioFileFormat.Aac ? 0 : AacEncoder.PrimingFrames * ch;
                Assert.True(dec.Length - skip >= frames * ch, $"{dec.Length / ch} frames");
                Assert.True(dec.Length - skip < (frames + 1024) * ch);
                Assert.True(SnrDb(pcm, dec.AsSpan(skip, pcm.Length).ToArray()) > 14);
            }
            finally { File.Delete(path); File.Delete(raw); }
        }
    }

    [Fact]
    public void HappyPathRefusesWhatTheEncoderCannotTake()
    {
        var wav = new MemoryStream();
        WriteWav(wav, 96000, 2, new float[2 * 4000]);
        wav.Position = 0;
        var ex = Assert.Throws<ArgumentException>(() => Audio.Transcode(wav, new MemoryStream(), new TranscodeOptions { Format = AudioFileFormat.Aac }));
        Assert.Contains("SampleRate", ex.Message, StringComparison.Ordinal);
        wav.Position = 0;
        // Converting first makes it work, onto a stream that cannot seek too.
        var result = Audio.Transcode(wav, new NonSeekableStream(), new TranscodeOptions { Format = AudioFileFormat.Aac, SampleRate = 48000 });
        Assert.Equal(AudioFileFormat.Aac, result.Format);
        Assert.Equal(48000, result.Input.SampleRate);
    }

    private sealed class NonSeekableStream : Stream
    {
        private readonly MemoryStream _inner = new();
        public override bool CanRead => false;
        public override bool CanSeek => false;
        public override bool CanWrite => true;
        public override long Length => throw new NotSupportedException();
        public override long Position { get => throw new NotSupportedException(); set => throw new NotSupportedException(); }
        public override void Flush() { }
        public override int Read(byte[] buffer, int offset, int count) => throw new NotSupportedException();
        public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
        public override void SetLength(long value) => throw new NotSupportedException();
        public override void Write(byte[] buffer, int offset, int count)
        {
            Assert.True(_inner.Length >= 0);
            _inner.Write(buffer, offset, count);
        }
    }

    [Fact]
    public void Mp4WriterGivesTheReaderTheSamplesTimingAndEditList()
    {
        var ms = new MemoryStream();
        var asc = new byte[] { 0x12, 0x10 };
        using (var w = Mp4AudioWriter.CreateAac(ms, 44100, 2, asc, 1024, leaveOpen: true))
        {
            for (int i = 0; i < 10; i++) w.AddSample(new byte[10 + i], 1024);
            w.Finish(10 * 1024 - 1024 - 5);
        }
        ms.Position = 0;
        using var src = Mp4PacketSource.Open(ms, AudioLimits.Default, leaveOpen: true);
        Assert.Equal(1024, src.PrimingFrames);
        Assert.Equal(10 * 1024 - 1024 - 5, src.LengthFrames);
        Assert.Equal(asc, src.CodecConfig.ToArray());
        Assert.Equal(44100, src.SampleRate);
        Assert.Equal(2, src.Channels);
        int count = 0;
        while (src.TryReadPacket(out var p))
        {
            Assert.Equal(10 + count, p.Data.Length);
            Assert.Equal(count * 1024L, p.Timestamp);
            Assert.Equal(1024, p.Duration);
            count++;
        }
        Assert.Equal(10, count);
        Assert.Throws<ArgumentException>(() => Mp4AudioWriter.CreateAac(new NonSeekableStream(), 44100, 2, asc, 0));
    }
}
