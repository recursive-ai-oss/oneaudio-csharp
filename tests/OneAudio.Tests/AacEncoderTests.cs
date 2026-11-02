using System.Buffers;
using System.Diagnostics;
using OneAudio.Aac;
using OneAudio.Mp4;
using Xunit;

namespace OneAudio.Tests;

public class AacEncoderTests
{
    private static string Scratch => Environment.GetEnvironmentVariable("TMPDIR") is { Length: > 0 } t ? t : Path.GetTempPath();

    // These tests keep their garbage small: other tests in the assembly assert that no garbage collection happens during their
    // (short) windows, and the collection count is process-wide.
    private static readonly Dictionary<(int, int, int), float[]> SignalCache = [];

    /// <summary>Music-like test signal: harmonic tones with vibrato, a decaying noise burst train and a little hiss, in [-0.7, 0.7]. Cached; do not modify.</summary>
    internal static float[] Signal(int rate, int channels, int frames)
    {
        lock (SignalCache)
        {
            if (SignalCache.TryGetValue((rate, channels, frames), out var cached)) return cached;
            var rng = new Random(1);
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
            return SignalCache[(rate, channels, frames)] = s;
        }
    }

    /// <summary>The units of one encode, back to back, with their lengths.</summary>
    internal sealed class Encoded
    {
        public readonly System.Buffers.ArrayBufferWriter<byte> Bytes = new();
        public readonly List<int> Lengths = [];
        public int Count => Lengths.Count;
        public byte[] ToArray() => Bytes.WrittenSpan.ToArray();
        public long Length => Bytes.WrittenCount;
        public int MaxLength => Lengths.Max();
    }

    internal static Encoded EncodeAll(AacEncoder enc, ReadOnlySpan<float> pcm, int chunkFrames)
    {
        var units = new Encoded();
        int ch = enc.Channels;
        for (int pos = 0; pos < pcm.Length;)
        {
            var span = pcm.Slice(pos, Math.Min(chunkFrames * ch, pcm.Length - pos));
            pos += span.Length;
            while (span.Length > 0)
            {
                int taken = enc.Write(span);
                span = span[(taken * ch)..];
                while (enc.TryReadPacket(out var p)) { units.Bytes.Write(p.Data.Span); units.Lengths.Add(p.Data.Length); }
            }
        }
        enc.Finish();
        while (enc.TryReadPacket(out var p)) { units.Bytes.Write(p.Data.Span); units.Lengths.Add(p.Data.Length); }
        return units;
    }

    private static void WriteWav(Stream s, int rate, int channels, float[] pcm)
    {
        using var w = new WavEncoder(s, new AudioFormat(rate, channels), new WavEncoderSettings(), leaveOpen: true);
        w.Write(pcm);
        w.Finish();
    }

    /// <summary>Decodes an ADTS stream whose decoded length (in frames) is known to the caller.</summary>
    private static float[] Decode(Encoded units, int channels, out AudioFormat format)
    {
        using var ms = new MemoryStream(units.ToArray());
        using var dec = Audio.Open(ms, leaveOpen: true);
        format = dec.Format;
        var all = new float[units.Count * 1024 * channels];
        int filled = 0, n;
        while (filled < all.Length && (n = dec.Read(all.AsSpan(filled))) > 0) filled += n * channels;
        Assert.Equal(all.Length, filled);
        Assert.Equal(0, dec.Read(new float[2 * channels]));
        return all;
    }

    private static double SnrDb(ReadOnlySpan<float> reference, ReadOnlySpan<float> decoded)
    {
        double sig = 0, err = 0;
        for (int i = 0; i < reference.Length; i++)
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
    [InlineData(22050, 1, 32000)]
    [InlineData(16000, 1, 32000)]
    [InlineData(11025, 2, 48000)]
    [InlineData(8000, 1, 24000)]
    public void AdtsStreamDecodesToTheInputLength(int rate, int channels, int bitrate)
    {
        int frames = rate + 77;
        var pcm = Signal(rate, channels, frames);
        var enc = new AacEncoder(rate, channels, new AacEncoderSettings { Bitrate = bitrate, Adts = true });
        var units = EncodeAll(enc, pcm, 1500);
        Assert.Equal((frames + 1023) / 1024 + 1, units.Count);
        Assert.Equal(units.Count * 1024 - AacEncoder.PrimingFrames - frames, enc.EndPaddingFrames);
        var dec = Decode(units, channels, out var fmt);
        Assert.Equal(rate, fmt.SampleRate);
        Assert.Equal(channels, fmt.Channels);
        // The decoder returns every unit's output, priming first (an ADTS stream has no edit list).
        double snr = SnrDb(pcm, dec.AsSpan(AacEncoder.PrimingFrames * channels, frames * channels));
        Assert.True(snr > 14, $"SNR {snr:F1} dB");
    }

    [Theory]
    [InlineData(64000)]
    [InlineData(128000)]
    [InlineData(192000)]
    public void CbrBitrateIsWithinFivePercent(int bitrate)
    {
        var pcm = Signal(44100, 2, 44100 * 4);
        var enc = new AacEncoder(44100, 2, new AacEncoderSettings { Bitrate = bitrate });
        var units = EncodeAll(enc, pcm, 4096);
        double seconds = units.Count * 1024 / 44100.0;
        double kbps = units.Length * 8 / seconds;
        Assert.InRange(kbps / bitrate, 0.95, 1.05);
    }

    [Fact]
    public void VbrAndAbrProduceDecodableStreamsAndVbrFollowsQuality()
    {
        var pcm = Signal(44100, 2, 44100 * 2);
        long Size(AacRateControl m, double q)
        {
            var enc = new AacEncoder(44100, 2, new AacEncoderSettings { Bitrate = 128000, RateControl = m, VbrQuality = q, Adts = true });
            var units = EncodeAll(enc, pcm, 4096);
            var dec = Decode(units, 2, out _);
            Assert.True(SnrDb(pcm, dec.AsSpan(2048, pcm.Length)) > 6);
            return units.Length;
        }
        long low = Size(AacRateControl.Vbr, 0.1), high = Size(AacRateControl.Vbr, 0.9);
        Assert.True(high > low * 1.3, $"{low} vs {high}");
        long abr = Size(AacRateControl.Abr, 0);
        Assert.InRange(abr * 8 / 2.0 / 128000, 0.9, 1.05);
    }

    [Theory]
    [InlineData(2)]
    [InlineData(1)]
    public void AbrFollowsTheMaterialWhereCbrDoesNotAndBothHitTheTarget(int channels)
    {
        int rate = 44100, frames = rate * 8;
        // Easy first half (a few steady tones), hard second half (dense noise bursts over a chord).
        var rng = new Random(7);
        var pcm = new float[frames * channels];
        for (int i = 0; i < frames; i++)
        {
            double t = i / (double)rate;
            double v = 0.2 * Math.Sin(2 * Math.PI * 440 * t) + 0.1 * Math.Sin(2 * Math.PI * 660 * t);
            if (i >= frames / 2) v += 0.4 * (rng.NextDouble() - 0.5) * (((i / 4410) & 1) == 0 ? 1.0 : 0.15);
            for (int c = 0; c < channels; c++) pcm[i * channels + c] = (float)v;
        }
        int bitrate = 64000 * channels;
        Encoded Run(AacRateControl m) => EncodeAll(new AacEncoder(rate, channels, new AacEncoderSettings { Bitrate = bitrate, RateControl = m }), pcm, 4096);
        var cbr = Run(AacRateControl.Cbr);
        var abr = Run(AacRateControl.Abr);
        double Rate(Encoded e) => e.Length * 8 / (e.Count * 1024.0 / rate) / bitrate;
        double Spread(Encoded e) { double mean = e.Lengths.Average(); return Math.Sqrt(e.Lengths.Average(l => (l - mean) * (l - mean))) / mean; }
        Assert.InRange(Rate(cbr), 0.95, 1.05);
        Assert.InRange(Rate(abr), 0.95, 1.05);
        Assert.False(cbr.Bytes.WrittenSpan.SequenceEqual(abr.Bytes.WrittenSpan));
        Assert.True(Spread(abr) > Spread(cbr) * 1.5, $"spread cbr {Spread(cbr):F3} abr {Spread(abr):F3}");
        Assert.True(abr.MaxLength > cbr.MaxLength, $"largest unit {cbr.MaxLength} vs {abr.MaxLength}");
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
            Assert.Equal(0, units.Count);
            return;
        }
        int expectUnits = (frames + 1023) / 1024 + 1;
        Assert.Equal(expectUnits, units.Count);
        Assert.Equal(expectUnits * 1024 - 1024 - frames, enc.EndPaddingFrames);
        Assert.InRange(enc.EndPaddingFrames, 0, 1023);
        Assert.Equal(frames, enc.FramesWritten);
        Decode(units, 2, out _); // the decoded length equals the unit count times 1024
    }

    [Fact]
    public void SilenceCodesToTinyUnitsAndDecodesToSilence()
    {
        // Constant bit rate pads silence up to the nominal rate; variable bit rate does not.
        var enc = new AacEncoder(44100, 2, new AacEncoderSettings { Adts = true, RateControl = AacRateControl.Vbr });
        var units = EncodeAll(enc, new float[44100 * 2], 4096);
        Assert.True(units.MaxLength < 120, $"{units.MaxLength} bytes");
        var dec = Decode(units, 2, out _);
        for (int i = 0; i < dec.Length; i++) Assert.True(dec[i] == 0f, $"sample {i} is {dec[i]}");
    }

    [Theory]
    [InlineData(1)]
    [InlineData(2)]
    public void FullScaleSquareWaveAndClippingInputStayValid(int channels)
    {
        int rate = 44100, frames = rate;
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
            Assert.True(units.MaxLength <= enc.MaxPacketBytes);
            var dec = Decode(units, channels, out _);
            Assert.True(SnrDb(pcm.AsSpan(1000), dec.AsSpan(1024 * channels + 1000, pcm.Length - 1000)) > 3);
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
        var pcm = Signal(44100, 2, 44100);
        byte[] Run()
        {
            var enc = new AacEncoder(44100, 2, new AacEncoderSettings { Adts = true });
            return EncodeAll(enc, pcm, 777).ToArray();
        }
        Assert.True(Run().AsSpan().SequenceEqual(Run()));
    }

    [Fact]
    public void SteadyStateEncodingAllocatesNothing()
    {
        foreach (var channels in new[] { 1, 2 })
        {
            var pcm = Signal(44100, channels, 44100 * 3);
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
        int rate = 44100, frames = rate + 321;
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
            int frames = rate + 100;
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
                Assert.True(SnrDb(pcm, dec.AsSpan(skip, pcm.Length)) > 14);
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

    private static List<string> TopLevelBoxes(byte[] file)
    {
        var names = new List<string>();
        for (long pos = 0; pos < file.Length;)
        {
            long size = System.Buffers.Binary.BinaryPrimitives.ReadUInt32BigEndian(file.AsSpan((int)pos));
            names.Add(System.Text.Encoding.ASCII.GetString(file, (int)pos + 4, 4));
            if (size == 1) size = (long)System.Buffers.Binary.BinaryPrimitives.ReadUInt64BigEndian(file.AsSpan((int)pos + 8));
            Assert.True(size >= 8);
            pos += size;
            Assert.True(pos <= file.Length);
        }
        return names;
    }

    private static float[] ReadAll(string path)
    {
        using var dec = Audio.Open(path);
        var got = new List<float>();
        var buf = new float[4096 * dec.Format.Channels];
        int n;
        while ((n = dec.Read(buf)) > 0) got.AddRange(buf.AsSpan(0, n * dec.Format.Channels));
        return got.ToArray();
    }

    [Fact]
    public void FastStartPutsMoovFirstAndBothLayoutsHoldTheSameFile()
    {
        int rate = 44100, frames = rate * 2 + 77;
        var pcm = Signal(rate, 2, frames);
        string id = Guid.NewGuid().ToString("N")[..8];
        string wav = Path.Combine(Scratch, "fs-" + id + ".wav"), fast = Path.ChangeExtension(wav, ".fast.m4a"), slow = Path.ChangeExtension(wav, ".slow.m4a");
        try
        {
            using (var f = File.Create(wav)) WriteWav(f, rate, 2, pcm);
            Audio.Transcode(wav, fast); // fast start is the default
            Audio.Transcode(wav, slow, new TranscodeOptions { FastStart = false });
            var fastBytes = File.ReadAllBytes(fast);
            var slowBytes = File.ReadAllBytes(slow);
            Assert.Equal(["ftyp", "moov", "mdat"], TopLevelBoxes(fastBytes));
            Assert.Equal(["ftyp", "mdat", "moov"], TopLevelBoxes(slowBytes));
            Assert.Equal(slowBytes.Length, fastBytes.Length);

            // Same samples through our reader, and the same decoded audio.
            var a = ReadAll(fast);
            var b = ReadAll(slow);
            Assert.Equal(frames * 2, a.Length);
            Assert.True(a.AsSpan().SequenceEqual(b));

            string? ffprobe = ContainerTestEnv.Ffprobe;
            if (ffprobe is not null && File.Exists(ffprobe))
            {
                foreach (var path in new[] { fast, slow })
                {
                    var psi = new ProcessStartInfo(ffprobe) { RedirectStandardError = true, RedirectStandardOutput = true };
                    foreach (var arg in new[] { "-v", "error", "-count_packets", "-select_streams", "a:0", "-show_entries", "stream=codec_name,nb_read_packets", "-of", "csv=p=0", path }) psi.ArgumentList.Add(arg);
                    using var proc = Process.Start(psi)!;
                    string output = proc.StandardOutput.ReadToEnd();
                    string err = proc.StandardError.ReadToEnd();
                    proc.WaitForExit();
                    Assert.True(proc.ExitCode == 0 && err.Length == 0, err);
                    Assert.Equal("aac," + (frames / 1024 + 2), output.Trim());
                }
            }
        }
        finally { File.Delete(wav); File.Delete(fast); File.Delete(slow); }
    }

    [Fact]
    public void FastStartWriterShiftsAMultiChunkSizedFileAndRefusesChangesAfterFinish()
    {
        byte[] Build(bool fast, out MemoryStream ms)
        {
            ms = new MemoryStream();
            ms.Write(new byte[5]); // the writer starts at the stream's current position
            using var w = Mp4AudioWriter.CreateAac(ms, 44100, 2, new byte[] { 0x12, 0x10 }, 1024, leaveOpen: true);
            w.FastStart = fast;
            for (int i = 0; i < 3000; i++) w.AddSample(new byte[200 + i % 7], 1024);
            w.Finish(3000 * 1024 - 1024);
            return ms.ToArray()[5..];
        }
        var fastBytes = Build(true, out _);
        var slowBytes = Build(false, out _);
        Assert.Equal(["ftyp", "moov", "mdat"], TopLevelBoxes(fastBytes));
        Assert.Equal(["ftyp", "mdat", "moov"], TopLevelBoxes(slowBytes));
        foreach (var bytes in new[] { fastBytes, slowBytes })
        {
            using var src = Mp4PacketSource.Open(new MemoryStream(bytes), AudioLimits.Default, leaveOpen: false);
            int count = 0;
            while (src.TryReadPacket(out var pk)) { Assert.Equal(200 + count % 7, pk.Data.Length); count++; }
            Assert.Equal(3000, count);
        }
        var w2 = Mp4AudioWriter.CreateAac(new MemoryStream(), 44100, 2, new byte[] { 0x12, 0x10 }, 1024);
        w2.FastStart = true;
        w2.AddSample(new byte[10], 1024);
        w2.Finish(1);
        Assert.Throws<InvalidOperationException>(() => w2.FastStart = false);
        w2.Dispose();
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
