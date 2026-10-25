using System.Buffers.Binary;
using OneAudio.Kernels;
using OneAudio.Mp3;
using Xunit;

namespace OneAudio.Tests;

/// <summary>Structure, gapless, seek, limit and allocation tests for the MP3 decoder.</summary>
public class Mp3DecoderTests
{
    private static string? GenDir => Mp3TestEnv.CorpusDir is { } c && Directory.Exists(Path.Combine(c, "mp3-gen")) ? Path.Combine(c, "mp3-gen") : null;

    private static void RequireGen() =>
        Assert.SkipUnless(GenDir is not null, "Generated MP3 set missing (run tools/gen-mp3-testset.py into $ONEAUDIO_CORPUS/mp3-gen).");

    private static byte[] Gen(string name) => File.ReadAllBytes(Path.Combine(GenDir!, name));

    private static int WavFrames(string wavName)
    {
        using var w = File.OpenRead(Path.Combine(GenDir!, wavName));
        using var br = new BinaryReader(w);
        w.Position = 22; int ch = br.ReadInt16();
        w.Position = 40; int bytes = br.ReadInt32();
        return bytes / (2 * ch);
    }

    private static IAudioDecoder Open(byte[] data, bool seekable = true, AudioLimits? limits = null)
    {
        Stream s = seekable ? new MemoryStream(data) : new NonSeekableStream(data);
        return AudioDecoder.Open(s, Mp3TestEnv.Registry, limits);
    }

    private sealed class NonSeekableStream(byte[] data) : Stream
    {
        private readonly MemoryStream _m = new(data);
        public override bool CanRead => true;
        public override bool CanSeek => false;
        public override bool CanWrite => false;
        public override long Length => throw new NotSupportedException();
        public override long Position { get => throw new NotSupportedException(); set => throw new NotSupportedException(); }
        public override void Flush() { }
        public override int Read(byte[] buffer, int offset, int count) => _m.Read(buffer, offset, count);
        public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
        public override void SetLength(long value) => throw new NotSupportedException();
        public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();
    }

    // ------------------------------------------------------------------ tables

    [Fact]
    public void HuffmanTablesArePrefixCodes()
    {
        // Building a table throws when a code is the prefix of another; Kraft's sum shows how complete each code is.
        for (int t = 0; t < 32; t++)
        {
            var table = Mp3Constants.BigTables[t];
            if (t == 0 || t == 4 || t == 14) { Assert.Null(table); continue; }
            Assert.NotNull(table);
        }
        foreach (var codes in new[] { Mp3Tables.Huff1, Mp3Tables.Huff2, Mp3Tables.Huff3, Mp3Tables.Huff5, Mp3Tables.Huff6, Mp3Tables.Huff7, Mp3Tables.Huff8,
                     Mp3Tables.Huff9, Mp3Tables.Huff10, Mp3Tables.Huff11, Mp3Tables.Huff12, Mp3Tables.Huff13, Mp3Tables.Huff15, Mp3Tables.Huff16, Mp3Tables.Huff24,
                     Mp3Tables.HuffA, Mp3Tables.HuffB })
        {
            double kraft = 0;
            foreach (uint c in codes) kraft += Math.Pow(2, -(int)(c >> 20));
            Assert.InRange(kraft, 0.9, 1.0 + 1e-12);
        }
    }

    [Fact]
    public void HuffmanDecodeRoundTripsEveryEntry()
    {
        // Every (length, code) pair must decode back to its own symbol and consume exactly its length.
        void Check(uint[] codes, int dim, int lin)
        {
            var table = new Mp3HuffmanTable(codes, dim, lin);
            for (int sym = 0; sym < codes.Length; sym++)
            {
                int len = (int)(codes[sym] >> 20);
                uint code = codes[sym] & 0xFFFFF;
                var bytes = new byte[8];
                ulong bits = (ulong)code << (64 - len);
                BinaryPrimitives.WriteUInt64BigEndian(bytes, bits);
                var br = new Mp3BitReader(bytes);
                int got = table.Decode(ref br);
                int expect = dim == 0 ? sym : ((sym / dim) << 4) | (sym % dim);
                Assert.Equal(expect, got);
                Assert.Equal(len, br.Position);
            }
        }
        Check(Mp3Tables.Huff13, 16, 0);
        Check(Mp3Tables.Huff16, 16, 1);
        Check(Mp3Tables.Huff24, 16, 4);
        Check(Mp3Tables.Huff5, 4, 0);
        Check(Mp3Tables.HuffA, 0, 0);
        Check(Mp3Tables.HuffB, 0, 0);
    }

    [Fact]
    public void BandTablesAreMonotonicAndCoverTheSpectrum()
    {
        foreach (var t in Mp3Tables.LongBands) { Assert.Equal(23, t.Length); Assert.Equal(576, t[^1]); for (int i = 1; i < t.Length; i++) Assert.True(t[i] > t[i - 1]); }
        foreach (var t in Mp3Tables.ShortBands) { Assert.Equal(14, t.Length); Assert.Equal(192, t[^1]); for (int i = 1; i < t.Length; i++) Assert.True(t[i] > t[i - 1]); }
        Assert.Equal(512, Mp3Tables.SynthesisWindow65536.Length);
    }

    // ------------------------------------------------------------------ detection and rejection

    [Fact]
    public void LayerTwoIsRejectedAsUnsupported()
    {
        // FF FD = MPEG-1 Layer II, no CRC; bitrate index 9, 44.1 kHz.
        var data = new byte[2048];
        data[0] = 0xFF; data[1] = 0xFD; data[2] = 0x90; data[3] = 0x00;
        Assert.Throws<UnsupportedAudioFormatException>(() => Open(data));
        // Behind an ID3v2 tag too.
        var tagged = new byte[10 + 100 + 2048];
        "ID3"u8.CopyTo(tagged); tagged[3] = 3; tagged[9] = 100;
        data.CopyTo(tagged, 110);
        Assert.Throws<UnsupportedAudioFormatException>(() => Open(tagged));
    }

    [Fact]
    public void GarbageIsNotMp3()
    {
        var rng = new Random(1);
        var data = new byte[4096];
        rng.NextBytes(data);
        data[0] = 0x12;
        Assert.Throws<UnsupportedAudioFormatException>(() => Open(data));
    }

    [Fact]
    public void HugeId3TagIsRefusedBeforeAnyWork()
    {
        // A tag header that claims 256 MB: refused from the limit, nothing is read or allocated for it.
        var data = new byte[64];
        "ID3"u8.CopyTo(data); data[3] = 4; data[6] = 0x7F; data[7] = 0x7F; data[8] = 0x7F; data[9] = 0x7F;
        var ex = Assert.Throws<AudioLimitExceededException>(() => Open(data));
        Assert.Equal(nameof(AudioLimits.MaxMetadataBytes), ex.LimitName);
    }

    [Fact]
    public void LimitsOnChannelsAndRateApply()
    {
        RequireGen();
        var data = Gen("lame_44100_2_cbr.mp3");
        Assert.Throws<AudioLimitExceededException>(() => Open(data, limits: new AudioLimits { MaxChannels = 1 }));
        Assert.Throws<AudioLimitExceededException>(() => Open(data, limits: new AudioLimits { MaxSampleRate = 32000 }));
    }

    // ------------------------------------------------------------------ gapless length

    [Theory]
    [InlineData(8000, 1)]
    [InlineData(11025, 2)]
    [InlineData(12000, 1)]
    [InlineData(16000, 2)]
    [InlineData(22050, 1)]
    [InlineData(24000, 2)]
    [InlineData(32000, 1)]
    [InlineData(44100, 2)]
    [InlineData(48000, 1)]
    public void LameGaplessLengthEqualsSource(int rate, int channels)
    {
        RequireGen();
        int expected = WavFrames($"src_{rate}_{channels}.wav");
        foreach (var kind in new[] { "cbr", "vbr", "abr" })
        {
            var data = Gen($"lame_{rate}_{channels}_{kind}.mp3");
            using var dec = Open(data);
            Assert.Equal(expected, dec.LengthFrames);
            var pcm = Mp3TestEnv.ReadAll(dec);
            Assert.Equal(expected, pcm.Length / channels);
        }
    }

    [Fact]
    public void FfmpegHeaderGapless()
    {
        RequireGen();
        int expected = WavFrames("src_44100_2.wav");
        using var dec = Open(Gen("ffmpeg_44100_2.mp3"));
        var pcm = Mp3TestEnv.ReadAll(dec);
        Assert.Equal(expected, dec.LengthFrames);
        Assert.Equal(expected, pcm.Length / 2);
    }

    [Fact]
    public void FilesWithoutHeaderAreDecodedInFull()
    {
        RequireGen();
        var data = Gen("lame_44100_2_notag.mp3");
        using var dec = Open(data);
        var pcm = Mp3TestEnv.ReadAll(dec);
        Assert.Equal(0, pcm.Length % (2 * 1152));
        Assert.Equal(pcm.Length / 2, dec.LengthFrames);
    }

    // ------------------------------------------------------------------ tags

    [Fact]
    public void Id3AndApeTagsAreIgnored()
    {
        RequireGen();
        var plain = Gen("lame_44100_2_cbr.mp3");
        var expected = Mp3TestEnv.DecodeAll(new MemoryStream(plain), out _);

        var tagged = new List<byte>();
        tagged.AddRange("ID3"u8.ToArray()); tagged.AddRange(new byte[] { 3, 0, 0, 0, 0, 0x08, 0x00 }); // 1024 bytes of tag, with 0xFF noise inside
        var body = new byte[1024]; for (int i = 0; i < body.Length; i++) body[i] = (byte)(i % 3 == 0 ? 0xFF : 0xE0 | (i & 0x1F));
        tagged.AddRange(body);
        tagged.AddRange(plain);
        tagged.AddRange("APETAGEX"u8.ToArray()); tagged.AddRange(new byte[100]);
        tagged.AddRange("TAG"u8.ToArray()); tagged.AddRange(new byte[125]);
        var got = Mp3TestEnv.DecodeAll(new MemoryStream(tagged.ToArray()), out _);
        Assert.Equal(expected.Length, got.Length);
        int firstDiff = expected.AsSpan().CommonPrefixLength(got);
        Assert.True(firstDiff == expected.Length, $"first difference at sample {firstDiff} of {expected.Length}");
    }

    [Fact]
    public void NonSeekableStreamDecodesTheSame()
    {
        RequireGen();
        var data = Gen("lame_22050_2_vbr.mp3");
        var expected = Mp3TestEnv.DecodeAll(new MemoryStream(data), out _);
        using var dec = Open(data, seekable: false);
        Assert.False(dec.CanSeek);
        Assert.Throws<NotSupportedException>(() => dec.Seek(10));
        var got = Mp3TestEnv.ReadAll(dec);
        Assert.True(expected.AsSpan().SequenceEqual(got));
    }

    [Fact]
    public void DamagedStreamsDoNotThrowAfterOpen()
    {
        RequireGen();
        var data = Gen("lame_44100_2_cbr.mp3");
        var rng = new Random(7);
        for (int round = 0; round < 40; round++)
        {
            var copy = (byte[])data.Clone();
            for (int k = 0; k < 30; k++) copy[1000 + rng.Next(copy.Length - 1000)] = (byte)rng.Next(256);
            if (round % 4 == 0) Array.Resize(ref copy, copy.Length - rng.Next(copy.Length / 2));
            using var dec = Open(copy);
            var buf = new float[2048];
            while (dec.Read(buf) > 0) { }
        }
    }

    // ------------------------------------------------------------------ seeking

    [Theory]
    [InlineData("lame_44100_2_cbr.mp3")]
    [InlineData("lame_44100_2_vbr.mp3")]
    [InlineData("lame_22050_1_abr.mp3")]
    [InlineData("lame_8000_1_vbr.mp3")]
    [InlineData("lame_48000_2_joint.mp3")]
    [InlineData("lame_44100_2_notag.mp3")]
    [InlineData("lame_32000_2_freeformat.mp3")]
    [InlineData("ffmpeg_22050_1.mp3")]
    public void SeekIsIdenticalToLinearDecode(string file)
    {
        RequireGen();
        var data = Gen(file);
        var linear = Mp3TestEnv.DecodeAll(new MemoryStream(data), out var fmt);
        int ch = fmt.Channels;
        long frames = linear.Length / ch;
        using var dec = Open(data);
        Assert.Equal(frames, dec.LengthFrames);
        var rng = new Random(11);
        var buf = new float[ch * 3000];
        for (int i = 0; i < 25; i++)
        {
            long target = i switch { 0 => 0, 1 => frames, 2 => frames - 1, 3 => 1, _ => rng.NextInt64(0, frames) };
            dec.Seek(target);
            Assert.Equal(target, dec.PositionFrames);
            int n = dec.Read(buf);
            long expectN = Math.Min(3000, frames - target);
            Assert.Equal(expectN, n);
            for (int k = 0; k < n * ch; k++)
                Assert.True(BitConverter.SingleToInt32Bits(buf[k]) == BitConverter.SingleToInt32Bits(linear[target * ch + k]), $"{file}: seek to {target}, sample {k}");
        }
    }

    [Fact]
    public void SeekRejectsOutOfRange()
    {
        RequireGen();
        using var dec = Open(Gen("lame_44100_2_cbr.mp3"));
        Assert.Throws<ArgumentOutOfRangeException>(() => dec.Seek(-1));
        Assert.Throws<ArgumentOutOfRangeException>(() => dec.Seek(dec.LengthFrames + 1));
    }

    // ------------------------------------------------------------------ allocation

    [Fact]
    public void ReadAllocatesNothingAfterWarmUp()
    {
        RequireGen();
        foreach (var file in new[] { "lame_44100_2_cbr.mp3", "lame_22050_1_vbr.mp3", "lame_48000_2_joint.mp3" })
        {
            using var dec = Open(Gen(file));
            var buf = new float[1024 * dec.Format.Channels];
            for (int i = 0; i < 20; i++) dec.Read(buf);
            long before = GC.GetAllocatedBytesForCurrentThread();
            int total = 0, n;
            while ((n = dec.Read(buf)) > 0) total += n;
            long allocated = GC.GetAllocatedBytesForCurrentThread() - before;
            Assert.True(total > 10000);
            Assert.Equal(0, allocated);
            // And after a seek.
            dec.Seek(1000);
            dec.Read(buf);
            before = GC.GetAllocatedBytesForCurrentThread();
            for (int i = 0; i < 50; i++) dec.Read(buf);
            Assert.Equal(0, GC.GetAllocatedBytesForCurrentThread() - before);
        }
    }

    // ------------------------------------------------------------------ kernels

    private static float[] RandomFloats(int n, int seed)
    {
        var rng = new Random(seed);
        var a = new float[n];
        for (int i = 0; i < n; i++) a[i] = (float)(rng.NextDouble() * 2 - 1);
        return a;
    }

    private static void AssertSame(float[] expected, float[] actual)
    {
        for (int i = 0; i < expected.Length; i++)
            Assert.True(BitConverter.SingleToInt32Bits(expected[i]) == BitConverter.SingleToInt32Bits(actual[i]), $"index {i}: {expected[i]} vs {actual[i]}");
    }

    [Fact]
    public void KernelsMatchScalar()
    {
        string state = $"mp3 kernels: Vector128={System.Runtime.Intrinsics.Vector128.IsHardwareAccelerated} Vector256={System.Runtime.Intrinsics.Vector256.IsHardwareAccelerated} " +
                       $"Vector512={System.Runtime.Intrinsics.Vector512.IsHardwareAccelerated} Avx2={System.Runtime.Intrinsics.X86.Avx2.IsSupported} AdvSimd={System.Runtime.Intrinsics.Arm.AdvSimd.IsSupported}";
        Mp3TestEnv.Report(state);
        foreach (int n in new[] { 1, 7, 8, 12, 15, 16, 17, 31, 32, 33, 36, 40, 64, 100 })
            for (int off = 0; off < 4; off++)
            {
                var row = RandomFloats(n + 4, n * 5 + off);
                var a = RandomFloats(n + 4, n * 3 + 1);
                var b = (float[])a.Clone();
                Mp3Kernels.MulAdd(a.AsSpan(off, n), row.AsSpan(off, n), 0.37f);
                Mp3Kernels.MulAddScalar(b.AsSpan(off, n), row.AsSpan(off, n), 0.37f);
                AssertSame(b, a);

                var x = RandomFloats(n + 4, n * 7 + off);
                var c = (float[])a.Clone();
                var d = (float[])a.Clone();
                Mp3Kernels.MulAddPairs(c.AsSpan(off, n), row.AsSpan(off, n), x.AsSpan(off, n));
                Mp3Kernels.MulAddPairsScalar(d.AsSpan(off, n), row.AsSpan(off, n), x.AsSpan(off, n));
                AssertSame(d, c);
            }
    }

    [Fact]
    public void HeaderParsing()
    {
        // MPEG-1 Layer III, 128 kbit/s, 44.1 kHz, stereo, no padding: 417 bytes.
        Assert.True(Mp3Header.TryParse([0xFF, 0xFB, 0x90, 0x00], out var h));
        Assert.Equal(MpegVersion.Mpeg1, h.Version);
        Assert.Equal(417, h.FrameBytes);
        Assert.Equal(1152, h.SamplesPerFrame);
        Assert.Equal(32, h.SideInfoBytes);
        // MPEG-2.5, 8 kHz, 8 kbit/s mono: 72 * 8000 / 8000 = 72 bytes; 576 samples; 9 bytes of side information.
        Assert.True(Mp3Header.TryParse([0xFF, 0xE3, 0x18, 0xC0], out var h25));
        Assert.Equal(MpegVersion.Mpeg25, h25.Version);
        Assert.Equal(8000, h25.SampleRate);
        Assert.Equal(576, h25.SamplesPerFrame);
        Assert.Equal(9, h25.SideInfoBytes);
        Assert.False(Mp3Header.TryParse([0xFF, 0xFB, 0xF0, 0x00], out _)); // bitrate index 15
        Assert.False(Mp3Header.TryParse([0xFF, 0xFB, 0x9C, 0x00], out _)); // sampling frequency 3
        Assert.False(Mp3Header.TryParse([0xFF, 0xEB, 0x90, 0x00], out _)); // reserved version
    }
}
