using System.Diagnostics;
using OneAudio.Flac;
using Xunit;

namespace OneAudio.Tests;

/// <summary>Generated test signals and helpers shared by the encoder tests.</summary>
internal static class EncoderSignals
{
    public static int Min(int bits) => bits == 32 ? int.MinValue : -(1 << (bits - 1));
    public static int Max(int bits) => bits == 32 ? int.MaxValue : (1 << (bits - 1)) - 1;

    /// <summary>Deterministic interleaved integer signal in the signed range of <paramref name="bits"/>.</summary>
    public static int[] Make(string kind, int bits, int channels, int frames, int rate = 44100, int seed = 1)
    {
        var rng = new Random(seed * 7919 + bits * 31 + channels);
        var s = new int[frames * channels];
        double amp = Max(bits);
        for (int i = 0; i < frames; i++)
            for (int c = 0; c < channels; c++)
            {
                double t = (double)i / rate;
                double v = kind switch
                {
                    "silence" => 0,
                    "constant" => 0.25,
                    "sine" => 0.8 * Math.Sin(2 * Math.PI * (440 + 110 * c) * t),
                    "noise" => 0, // filled below
                    "music" => 0.30 * Math.Sin(2 * Math.PI * 220 * t) * (0.6 + 0.4 * Math.Sin(2 * Math.PI * 0.7 * t))
                               + 0.20 * Math.Sin(2 * Math.PI * (330 + 3 * c) * t + 0.3 * Math.Sin(2 * Math.PI * 5 * t))
                               + 0.10 * Math.Sin(2 * Math.PI * 1760 * t) + 0.01 * (rng.NextDouble() - 0.5),
                    "clicks" => i % 997 == 0 ? 0.9 : (i % 1009 == 0 ? -0.9 : 0.001 * Math.Sin(i * 0.1)),
                    "square" => (i / 50) % 2 == 0 ? 0.7 : -0.7,
                    "wasted" => 0.5 * Math.Sin(2 * Math.PI * 300 * t),
                    "steps" => 0, // filled below
                    "antiphase" => (c == 0 ? 1 : -1) * 0.6 * Math.Sin(2 * Math.PI * 500 * t),
                    _ => throw new ArgumentException(kind),
                };
                int q;
                if (kind == "steps") q = (i / 640) % 2 == 0 ? 0 : -1; // isolated -1 residuals: a run of them ORs to zero in the folded form
                else if (kind == "noise") q = bits == 32 ? rng.Next(int.MinValue, int.MaxValue) : rng.Next(Min(bits), Max(bits) + 1);
                else q = (int)Math.Clamp(Math.Round(v * amp), Min(bits), Max(bits));
                if (kind == "wasted") q = (q >> 3) << 3;
                if (kind == "wasted" && bits <= 4) q = 0;
                s[i * channels + c] = q;
            }
        return s;
    }

    public static byte[] Encode(int[] samples, int bits, int channels, int rate, FlacEncoderSettings? settings = null, int writeChunk = 0)
    {
        using var ms = new MemoryStream();
        using (var enc = new FlacEncoder(ms, new AudioFormat(rate, channels), (settings ?? new FlacEncoderSettings()) with { BitsPerSample = bits }, leaveOpen: true))
        {
            if (writeChunk <= 0) enc.WriteInteger(samples);
            else
                for (int o = 0; o < samples.Length; o += writeChunk * channels)
                    enc.WriteInteger(samples.AsSpan(o, Math.Min(writeChunk * channels, samples.Length - o)));
            enc.Finish();
        }
        return ms.ToArray();
    }

    /// <summary>Decodes with our decoder, undoing the container shift, and checks the STREAMINFO MD5.</summary>
    public static int[] Decode(byte[] flac, out FlacDecoder dec, bool verifyMd5 = true)
    {
        dec = FlacDecoder.Open(new MemoryStream(flac), new AudioLimits { MaxSampleRate = 1_048_575 });
        int ch = dec.Format.Channels;
        int shift = dec.Format.BitsPerSample - dec.BitsPerSample;
        var list = new List<int>();
        var buf = new int[4096 * ch];
        int n;
        while ((n = dec.ReadInteger(buf)) > 0)
            for (int i = 0; i < n * ch; i++) list.Add(buf[i] >> shift);
        if (verifyMd5) Assert.True(dec.VerifyMd5(), "STREAMINFO MD5 mismatch");
        return list.ToArray();
    }

    /// <summary>SHA-256 of the samples packed the way <c>flac -d</c> writes them: little-endian at the whole-byte width, left-aligned, 8-bit unsigned.</summary>
    public static string PackedHash(int[] samples, int bits)
    {
        int bytes = (bits + 7) / 8, shift = bytes * 8 - bits;
        var raw = new byte[samples.Length * bytes];
        for (int i = 0; i < samples.Length; i++)
        {
            int v = samples[i] << shift;
            for (int b = 0; b < bytes; b++) raw[i * bytes + b] = (byte)(v >> (8 * b));
            if (bytes == 1) raw[i] ^= 0x80;
        }
        return Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(raw));
    }

    /// <summary>The reference decoder's output hash for these bytes (see <see cref="PackedHash"/>), or null without the reference binary.</summary>
    public static string? ReferenceDecodeHash(byte[] flac, string name)
    {
        if (FlacTestEnv.FlacBinary is null) return null;
        string path = Path.Combine(Path.GetTempPath(), "oneaudio-enc-" + name + "-" + Guid.NewGuid().ToString("N")[..8] + ".flac");
        File.WriteAllBytes(path, flac);
        try { return FlacTestEnv.ReferenceHash(path); }
        finally { File.Delete(path); }
    }

    /// <summary>Runs <c>flac -t</c> on the bytes. Returns null when the reference binary is not available.</summary>
    public static string? FlacTest(byte[] flac, string name)
    {
        string? bin = FlacTestEnv.FlacBinary;
        if (bin is null) return null;
        string dir = Path.Combine(Path.GetTempPath(), "oneaudio-enc-tests");
        Directory.CreateDirectory(dir);
        string path = Path.Combine(dir, name + "-" + Guid.NewGuid().ToString("N")[..8] + ".flac");
        File.WriteAllBytes(path, flac);
        try
        {
            var psi = new ProcessStartInfo(bin) { RedirectStandardError = true, RedirectStandardOutput = true, UseShellExecute = false };
            foreach (var a in new[] { "-t", "-s", path }) psi.ArgumentList.Add(a);
            using var p = Process.Start(psi)!;
            string err = p.StandardError.ReadToEnd();
            p.WaitForExit();
            return p.ExitCode == 0 ? "" : err;
        }
        finally { File.Delete(path); }
    }
}

/// <summary>FLAC encoder: round trips through our decoder and the reference <c>flac -t</c>, format rules, determinism.</summary>
public class FlacEncoderTests
{
    public static TheoryData<string, int, int, int, int> Cases()
    {
        var d = new TheoryData<string, int, int, int, int>();
        // kind, bits, channels, rate, frames
        foreach (int bits in new[] { 4, 8, 12, 16, 20, 24, 32 })
            foreach (var kind in new[] { "music", "noise", "sine" })
                d.Add(kind, bits, 2, 44100, 9000);
        foreach (int ch in new[] { 1, 3, 4, 5, 6, 7, 8 }) d.Add("music", 16, ch, 48000, 5000);
        d.Add("silence", 16, 2, 44100, 10000);
        d.Add("steps", 16, 1, 44100, 20000);
        d.Add("steps", 8, 2, 8000, 9000);
        d.Add("constant", 24, 2, 96000, 10000);
        d.Add("clicks", 16, 2, 44100, 12000);
        d.Add("square", 8, 1, 8000, 9000);
        d.Add("wasted", 16, 2, 44100, 9000);
        d.Add("wasted", 24, 1, 44100, 9000);
        d.Add("antiphase", 16, 2, 44100, 9000);
        d.Add("music", 16, 2, 22050, 9000);
        d.Add("music", 16, 2, 11025, 9000);
        d.Add("music", 16, 2, 1048575, 9000);
        d.Add("music", 16, 2, 100000, 9000);
        d.Add("music", 16, 2, 65535, 9000);
        d.Add("music", 16, 2, 7, 9000);
        d.Add("music", 16, 2, 44100, 4096);
        d.Add("music", 16, 2, 44100, 4097);
        d.Add("music", 16, 2, 44100, 8192);
        foreach (int n in new[] { 0, 1, 2, 3, 5, 15, 16, 17, 191, 192, 193, 1151, 1152, 1153 }) d.Add("music", 16, 2, 44100, n);
        return d;
    }

    [Theory, MemberData(nameof(Cases))]
    public void RoundTrip_IsBitExact(string kind, int bits, int channels, int rate, int frames)
    {
        var src = EncoderSignals.Make(kind, bits, channels, frames, Math.Min(rate, 192000));
        foreach (int level in new[] { 0, 5, 8 })
        {
            var flac = EncoderSignals.Encode(src, bits, channels, rate, new FlacEncoderSettings { CompressionLevel = level, SeekPoints = 8 });
            var back = EncoderSignals.Decode(flac, out var dec, verifyMd5: frames > 0);
            using (dec)
            {
                if (frames > 0) Assert.Equal(frames, dec.LengthFrames);
                Assert.Equal(rate, dec.Format.SampleRate);
                Assert.Equal(channels, dec.Format.Channels);
                Assert.Equal(bits, dec.BitsPerSample);
            }
            Assert.True(src.AsSpan().SequenceEqual(back), $"level {level}: decoded samples differ");
            string? err = EncoderSignals.FlacTest(flac, $"{kind}-{bits}-{channels}-{frames}-L{level}");
            if (err is not null) Assert.True(err.Length == 0, $"flac -t failed at level {level}: {err}");
            if (frames > 0 && EncoderSignals.ReferenceDecodeHash(flac, "rt") is { } refHash)
                Assert.Equal(EncoderSignals.PackedHash(src, bits), refHash); // flac -d returns exactly the source samples
        }
    }

    [Fact]
    public void Output_IsIdenticalForIdenticalInput_AndIndependentOfWriteSplit()
    {
        var src = EncoderSignals.Make("music", 16, 2, 30000);
        var a = EncoderSignals.Encode(src, 16, 2, 44100);
        var b = EncoderSignals.Encode(src, 16, 2, 44100);
        var c = EncoderSignals.Encode(src, 16, 2, 44100, writeChunk: 777);
        Assert.Equal(a, b);
        Assert.Equal(a, c);
    }

    [Fact]
    public void ReportOutputHash()
    {
        // The hash must not change with DOTNET_EnableAVX512/AVX2/HWIntrinsic; compare the printed values across those runs.
        var src = EncoderSignals.Make("music", 16, 2, 60000);
        var parts = new List<string>();
        foreach (int level in new[] { 0, 3, 5, 8 })
            parts.Add($"L{level}={Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(EncoderSignals.Encode(src, 16, 2, 44100, new FlacEncoderSettings { CompressionLevel = level })))[..16]}");
        TestContext.Current.SendDiagnosticMessage("flac-output-hash " + string.Join(" ", parts));
    }

    [Fact]
    public void Float_IsRoundedToTheBitDepth()
    {
        var f = new float[2 * 5000];
        for (int i = 0; i < f.Length; i++) f[i] = 0.6f * MathF.Sin(i * 0.01f);
        f[10] = float.NaN; f[11] = 3f; f[12] = -3f; f[13] = float.PositiveInfinity;
        using var ms = new MemoryStream();
        using (var enc = new FlacEncoder(ms, new AudioFormat(44100, 2), new FlacEncoderSettings { BitsPerSample = 16 }, true))
        {
            enc.Write(f);
            enc.Finish();
        }
        var back = EncoderSignals.Decode(ms.ToArray(), out var dec);
        dec.Dispose();
        for (int i = 0; i < f.Length; i++)
        {
            int expect = float.IsNaN(f[i]) ? 0 : (int)Math.Clamp(Math.Round(f[i] * 32768.0, MidpointRounding.ToEven), -32768, 32767);
            Assert.Equal(expect, back[i]);
        }
    }

    [Fact]
    public void Dither_IsDeterministicAndStaysWithinOneLsbOfTheRounded()
    {
        var f = new float[4000];
        for (int i = 0; i < f.Length; i++) f[i] = 0.3f * MathF.Sin(i * 0.02f);
        byte[] Run(bool dither)
        {
            using var ms = new MemoryStream();
            using var enc = new FlacEncoder(ms, new AudioFormat(44100, 1), new FlacEncoderSettings { BitsPerSample = 16, Dither = dither }, true);
            enc.Write(f.AsSpan(0, 1234)); enc.Write(f.AsSpan(1234));
            enc.Finish();
            return ms.ToArray();
        }
        var plain = EncoderSignals.Decode(Run(false), out var d0); d0.Dispose();
        var dith = EncoderSignals.Decode(Run(true), out var d1); d1.Dispose();
        Assert.Equal(Run(true), Run(true));
        Assert.NotEqual(plain, dith);
        for (int i = 0; i < plain.Length; i++) Assert.InRange(Math.Abs(plain[i] - dith[i]), 0, 2);
    }

    [Fact]
    public void Header_HasStreamInfoSeekTableAndComments()
    {
        var src = EncoderSignals.Make("music", 16, 2, 5 * 44100);
        using var ms = new MemoryStream();
        using (var enc = new FlacEncoder(ms, new AudioFormat(44100, 2), new FlacEncoderSettings { SeekPoints = 10, Comments = ["TITLE=Tone", "ARTIST=Test"] }, true))
        {
            enc.WriteInteger(src);
            enc.Finish();
        }
        var bytes = ms.ToArray();
        using var dec = FlacDecoder.Open(new MemoryStream(bytes));
        Assert.Equal("RecursiveAI.OneAudio", dec.Vendor);
        Assert.Contains("TITLE=Tone", dec.Comments);
        Assert.Contains("ARTIST=Test", dec.Comments);
        Assert.Equal(5 * 44100, dec.LengthFrames);
        Assert.True(dec.CanSeek);
        // Seeking exercises the table the encoder wrote.
        var all = EncoderSignals.Decode(bytes, out var d2); d2.Dispose();
        foreach (long target in new long[] { 0, 1, 4095, 44100, 100000, 200000, 5 * 44100 - 1 })
        {
            dec.Seek(target);
            var one = new int[2];
            Assert.Equal(1, dec.ReadInteger(one));
            Assert.Equal(all[(int)target * 2], one[0] >> 0);
        }
        string? err = EncoderSignals.FlacTest(bytes, "header");
        if (err is not null) Assert.Equal("", err);
        string? bin = FlacTestEnv.FlacBinary;
        if (bin is not null)
        {
            string path = Path.Combine(Path.GetTempPath(), "oneaudio-enc-" + Guid.NewGuid().ToString("N")[..8] + ".flac");
            File.WriteAllBytes(path, bytes);
            try
            {
                var meta = Path.Combine(Path.GetDirectoryName(bin)!, "metaflac");
                if (File.Exists(meta))
                {
                    var psi = new ProcessStartInfo(meta) { RedirectStandardOutput = true, UseShellExecute = false };
                    foreach (var a in new[] { "--list", "--block-type=SEEKTABLE", path }) psi.ArgumentList.Add(a);
                    using var p = Process.Start(psi)!;
                    string o = p.StandardOutput.ReadToEnd();
                    p.WaitForExit();
                    Assert.Contains("seek points: 10", o);
                }
            }
            finally { File.Delete(path); }
        }
    }

    [Fact]
    public void NonSeekableOutput_WritesUsableStreamWithDeclaredLength()
    {
        var src = EncoderSignals.Make("music", 16, 2, 20000);
        using var ms = new MemoryStream();
        using (var ns = new NonSeekable(ms))
        using (var enc = new FlacEncoder(ns, new AudioFormat(44100, 2), new FlacEncoderSettings { TotalFrames = 20000 }, true))
        {
            enc.WriteInteger(src);
            enc.Finish();
        }
        var bytes = ms.ToArray();
        var back = EncoderSignals.Decode(bytes, out var dec, verifyMd5: false);
        using (dec)
        {
            Assert.Equal(20000, dec.LengthFrames);
            Assert.Equal(0, dec.Md5.Length);
        }
        Assert.Equal(src, back);
        string? err = EncoderSignals.FlacTest(bytes, "nonseekable");
        if (err is not null) Assert.Equal("", err);

        using var ns2 = new NonSeekable(new MemoryStream());
        using var enc2 = new FlacEncoder(ns2, new AudioFormat(44100, 2), new FlacEncoderSettings { TotalFrames = 100 });
        enc2.WriteInteger(src.AsSpan(0, 2 * 50));
        Assert.Throws<InvalidOperationException>(() => enc2.Finish());
    }

    [Fact]
    public void Settings_AreValidatedUpFront()
    {
        using var ms = new MemoryStream();
        var fmt = new AudioFormat(44100, 2);
        Assert.Throws<ArgumentException>(() => new FlacEncoder(ms, fmt, new FlacEncoderSettings { CompressionLevel = 9 }));
        Assert.Throws<ArgumentException>(() => new FlacEncoder(ms, fmt, new FlacEncoderSettings { BitsPerSample = 33 }));
        Assert.Throws<ArgumentException>(() => new FlacEncoder(ms, fmt, new FlacEncoderSettings { BitsPerSample = 3 }));
        Assert.Throws<ArgumentException>(() => new FlacEncoder(ms, fmt, new FlacEncoderSettings { BlockSize = 8 }));
        Assert.Throws<ArgumentException>(() => new FlacEncoder(ms, fmt, new FlacEncoderSettings { BlockSize = 8192 })); // beyond the subset at 44.1 kHz
        Assert.Throws<ArgumentException>(() => new FlacEncoder(ms, fmt, new FlacEncoderSettings { MaxLpcOrder = 13 }));
        Assert.Throws<ArgumentException>(() => new FlacEncoder(ms, fmt, new FlacEncoderSettings { MaxPartitionOrder = 9 }));
        Assert.Throws<ArgumentException>(() => new FlacEncoder(ms, new AudioFormat(44100, 9), new FlacEncoderSettings()));
        Assert.Throws<ArgumentException>(() => new FlacEncoder(ms, new AudioFormat(0, 2), new FlacEncoderSettings()));
        Assert.Throws<ArgumentException>(() => new FlacEncoder(ms, fmt, new FlacEncoderSettings { Comments = ["noequals"] }));
        Assert.Equal(0, ms.Length); // nothing is written for rejected settings
        // Outside the subset the same values are accepted and round-trip.
        var src = EncoderSignals.Make("music", 16, 2, 20000);
        var flac = EncoderSignals.Encode(src, 16, 2, 44100, new FlacEncoderSettings { Subset = false, BlockSize = 8192, MaxLpcOrder = 20, MaxPartitionOrder = 12 });
        Assert.Equal(src, EncoderSignals.Decode(flac, out var d)); d.Dispose();
        string? err = EncoderSignals.FlacTest(flac, "nonsubset");
        if (err is not null) Assert.Equal("", err);
    }

    [Fact]
    public void WriteInteger_RejectsOutOfRangeAndPartialFrames()
    {
        using var enc = new FlacEncoder(new MemoryStream(), new AudioFormat(44100, 2), new FlacEncoderSettings { BitsPerSample = 12 });
        Assert.Throws<ArgumentOutOfRangeException>(() => enc.WriteInteger([2048, 0]));
        Assert.Throws<ArgumentOutOfRangeException>(() => enc.WriteInteger([0, -2049]));
        Assert.Throws<ArgumentException>(() => enc.WriteInteger([1, 2, 3]));
        enc.WriteInteger([2047, -2048]);
        enc.Finish();
        Assert.Throws<InvalidOperationException>(() => enc.WriteInteger([0, 0]));
    }

    [Fact]
    public void EncodeFromDecoder_CopiesALosslessSourceExactly()
    {
        var src = EncoderSignals.Make("music", 24, 2, 30000);
        var first = EncoderSignals.Encode(src, 24, 2, 44100);
        using var dec = FlacDecoder.Open(new MemoryStream(first));
        using var ms = new MemoryStream();
        long n = FlacEncoder.Encode(dec, ms, new FlacEncoderSettings { CompressionLevel = 8 }, leaveOpen: true);
        Assert.Equal(30000, n);
        Assert.Equal(src, EncoderSignals.Decode(ms.ToArray(), out var d2)); d2.Dispose();
        using var cancelled = new CancellationTokenSource();
        cancelled.Cancel();
        using var dec2 = FlacDecoder.Open(new MemoryStream(first));
        Assert.Throws<OperationCanceledException>(() => FlacEncoder.Encode(dec2, new MemoryStream(), null, false, cancelled.Token));
    }

    [Fact]
    public void Write_AllocatesNothingAfterWarmUp()
    {
        const int Frames = 8192;
        var f = new float[Frames * 2];
        for (int i = 0; i < f.Length; i++) f[i] = 0.5f * MathF.Sin(i * 0.013f) + 0.05f * MathF.Sin(i * 0.9f);
        foreach (int level in new[] { 0, 5, 8 })
        {
            using var sink = new NullSeekableSink();
            using var enc = new FlacEncoder(sink, new AudioFormat(44100, 2), new FlacEncoderSettings { CompressionLevel = level }, true);
            for (int i = 0; i < 3; i++) enc.Write(f);
            long before = GC.GetAllocatedBytesForCurrentThread();
            for (int i = 0; i < 6; i++) enc.Write(f);
            long allocated = GC.GetAllocatedBytesForCurrentThread() - before;
            Assert.Equal(0, allocated);
        }
    }

    private sealed class NonSeekable(Stream inner) : Stream
    {
        public override bool CanRead => false;
        public override bool CanSeek => false;
        public override bool CanWrite => true;
        public override long Length => throw new NotSupportedException();
        public override long Position { get => throw new NotSupportedException(); set => throw new NotSupportedException(); }
        public override void Flush() => inner.Flush();
        public override int Read(byte[] buffer, int offset, int count) => throw new NotSupportedException();
        public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
        public override void SetLength(long value) => throw new NotSupportedException();
        public override void Write(byte[] buffer, int offset, int count) => inner.Write(buffer, offset, count);
        public override void Write(ReadOnlySpan<byte> buffer) => inner.Write(buffer);
    }

    private sealed class NullSeekableSink : Stream
    {
        private long _pos, _len;
        public override bool CanRead => false;
        public override bool CanSeek => true;
        public override bool CanWrite => true;
        public override long Length => _len;
        public override long Position { get => _pos; set => _pos = value; }
        public override void Flush() { }
        public override int Read(byte[] buffer, int offset, int count) => throw new NotSupportedException();
        public override long Seek(long offset, SeekOrigin origin) => _pos = origin switch { SeekOrigin.Begin => offset, SeekOrigin.Current => _pos + offset, _ => _len + offset };
        public override void SetLength(long value) => _len = value;
        public override void Write(byte[] buffer, int offset, int count) => Write(buffer.AsSpan(offset, count));
        public override void Write(ReadOnlySpan<byte> buffer) { _pos += buffer.Length; _len = Math.Max(_len, _pos); }
    }
}
