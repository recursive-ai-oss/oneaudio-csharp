using System.Buffers.Binary;
using OneAudio.Flac;
using Xunit;

namespace OneAudio.Tests;

public class FlacDecoderTests
{
    public static TheoryData<string> Fixtures()
    {
        var d = new TheoryData<string>();
        foreach (var n in FlacFixtures.Names) d.Add(n);
        return d;
    }

    [Theory, MemberData(nameof(Fixtures))]
    public void Fixture_MatchesStreamInfoMd5_AndCountsFrames(string name)
    {
        using var d = FlacFixtures.Open(FlacFixtures.Bytes(name));
        Assert.True(d.VerifyMd5());
        Assert.Equal(0, d.PositionFrames);
        var ints = FlacFixtures.DecodeAll(d);
        Assert.Equal(d.LengthFrames * d.Format.Channels, ints.Length);
        Assert.Equal(d.LengthFrames, d.PositionFrames);
        Assert.Equal(0, d.Read(new float[d.Format.Channels]));
    }

    [Fact]
    public void Format_ReportsContainerWidthAndMask()
    {
        using var d = FlacFixtures.Open(FlacFixtures.Bytes("six16"));
        Assert.Equal(6, d.Format.Channels);
        Assert.Equal(22050, d.Format.SampleRate);
        Assert.Equal(NativeSampleType.PcmSigned16, d.Format.NativeType);
        Assert.Equal(AudioFormat.DefaultMask(6), d.Format.ChannelMask);
        Assert.True(d.HasNativeIntegers);
        using var m8 = FlacFixtures.Open(FlacFixtures.Bytes("mono8"));
        Assert.Equal(NativeSampleType.PcmUnsigned8, m8.Format.NativeType);
        Assert.Equal(8, m8.Format.BitsPerSample);
        using var m24 = FlacFixtures.Open(FlacFixtures.Bytes("mono24_lpc"));
        Assert.Equal(24, m24.BitsPerSample);
    }

    [Theory, MemberData(nameof(Fixtures))]
    public void FloatRead_IsIntegerOverFullScale(string name)
    {
        var data = FlacFixtures.Bytes(name);
        using var a = FlacFixtures.Open(data);
        using var b = FlacFixtures.Open(data);
        var ints = FlacFixtures.DecodeAll(a);
        var floats = new float[ints.Length];
        Assert.Equal(a.LengthFrames, b.Read(floats));
        double fs = Math.Pow(2, a.Format.BitsPerSample - 1);
        for (int i = 0; i < ints.Length; i++) Assert.Equal((float)(ints[i] / fs), floats[i]);
    }

    [Theory, MemberData(nameof(Fixtures))]
    public void ScalarAndVectorLpc_AgreeBitExactly(string name)
    {
        var data = FlacFixtures.Bytes(name);
        using var vec = FlacFixtures.Open(data);
        using var sca = FlacFixtures.Open(data);
        sca.ForceScalarLpc = true;
        Assert.Equal(FlacFixtures.DecodeAll(vec), FlacFixtures.DecodeAll(sca));
    }

    [Fact]
    public void ReadSizes_DoNotChangeTheResult()
    {
        var data = FlacFixtures.Bytes("stereo16_seektable");
        using var a = FlacFixtures.Open(data);
        var want = FlacFixtures.DecodeAll(a);
        foreach (int chunk in new[] { 1, 7, 4095, 4096, 4097, 100000 })
        {
            using var d = FlacFixtures.Open(data);
            var got = new List<int>();
            var buf = new int[2 * chunk];
            int n;
            while ((n = d.ReadInteger(buf)) > 0) got.AddRange(buf.AsSpan(0, n * 2).ToArray());
            Assert.Equal(want, got);
        }
    }

    [Fact]
    public void VorbisComment_IsExposed()
    {
        using var d = FlacFixtures.Open(FlacFixtures.Bytes("stereo16_seektable"));
        Assert.StartsWith("reference libFLAC", d.Vendor, StringComparison.Ordinal);
        Assert.Empty(d.Comments);
    }

    [Fact]
    public void NonSeekableStream_DecodesIdentically_AndRefusesSeek()
    {
        var data = FlacFixtures.Bytes("stereo16_seektable");
        using var seekable = FlacFixtures.Open(data);
        var want = FlacFixtures.DecodeAll(seekable);
        using var d = AudioDecoder.Open(new NonSeekableStream(data), FlacTestEnv.Registry);
        Assert.False(d.CanSeek);
        Assert.Throws<NotSupportedException>(() => d.Seek(5));
        Assert.Equal(want, FlacFixtures.DecodeAll(d));
    }

    [Fact]
    public void Md5Mismatch_IsReported()
    {
        var data = FlacFixtures.Bytes("mono8");
        data[4 + 4 + 18] ^= 0xFF; // first MD5 byte in STREAMINFO
        using var d = FlacFixtures.Open(data);
        Assert.False(d.VerifyMd5());
        Assert.Equal(0, d.PositionFrames); // restored
    }

    [Fact]
    public void Md5Verification_RestoresPosition()
    {
        using var d = FlacFixtures.Open(FlacFixtures.Bytes("stereo16_seektable"));
        d.Seek(30001);
        var before = new int[20];
        d.ReadInteger(before);
        d.Seek(30001);
        Assert.True(d.VerifyMd5());
        Assert.Equal(30001, d.PositionFrames);
        var after = new int[20];
        d.ReadInteger(after);
        Assert.Equal(before, after);
    }

    // ---- Robustness ------------------------------------------------------------------------------------

    [Theory, MemberData(nameof(Fixtures))]
    public void TruncatedAndCorruptedFiles_FailOnlyWithTypedExceptions(string name)
    {
        var data = FlacFixtures.Bytes(name);
        var rnd = new Random(1234);
        for (int iter = 0; iter < 150; iter++)
        {
            var copy = (byte[])data.Clone();
            int mode = iter % 3;
            if (mode == 0) copy = copy.AsSpan(0, rnd.Next(copy.Length)).ToArray();
            else for (int k = 0; k < 1 + mode * 3; k++) copy[rnd.Next(Math.Min(copy.Length, mode == 1 ? 400 : copy.Length))] ^= (byte)(1 << rnd.Next(8));
            try
            {
                using var d = FlacFixtures.Open(copy);
                var buf = new int[d.Format.Channels * 4096];
                long guard = 0;
                while (d.ReadInteger(buf) > 0) guard++;
                Assert.True(guard < 1_000_000);
            }
            catch (OneAudioException)
            {
            }
        }
    }

    private static byte[] StreamInfo(int minBlock, int maxBlock, int rate, int channels, int bps, long total, byte lastFlag = 0x80)
    {
        var b = new byte[4 + 4 + 38];
        "fLaC"u8.CopyTo(b);
        b[4] = (byte)(lastFlag | 0);
        b[7] = 34;
        var si = b.AsSpan(8);
        BinaryPrimitives.WriteUInt16BigEndian(si, (ushort)minBlock);
        BinaryPrimitives.WriteUInt16BigEndian(si[2..], (ushort)maxBlock);
        ulong packed = ((ulong)rate << 44) | ((ulong)(channels - 1) << 41) | ((ulong)(bps - 1) << 36) | (ulong)total;
        BinaryPrimitives.WriteUInt64BigEndian(si[10..], packed);
        return b;
    }

    [Fact]
    public void Limits_AreCheckedBeforeBuffersAreAllocated()
    {
        // Eight channels of 65535-sample blocks would be 2 MB of buffers; a tighter frame-size limit rejects it first.
        var big = StreamInfo(16, 65535, 44100, 8, 16, 1000);
        var ex = Assert.Throws<AudioLimitExceededException>(() => FlacFixtures.Open(big, new AudioLimits { MaxFrameSize = 4096 }));
        Assert.Equal(nameof(AudioLimits.MaxFrameSize), ex.LimitName);
        Assert.Throws<AudioLimitExceededException>(() => FlacFixtures.Open(StreamInfo(16, 4096, 44100, 8, 16, 1000), new AudioLimits { MaxChannels = 2 }));
        Assert.Throws<AudioLimitExceededException>(() => FlacFixtures.Open(StreamInfo(16, 4096, 1_000_000, 2, 16, 1000), new AudioLimits { MaxSampleRate = 192000 }));
        // Defaults accept the same header.
        using var ok = FlacFixtures.Open(StreamInfo(16, 65535, 44100, 8, 16, 1000));
        Assert.Equal(8, ok.Format.Channels);
    }

    [Fact]
    public void StreamInfoValues_RFC9639Forbids_AreRejected()
    {
        Assert.Throws<AudioFormatException>(() => FlacFixtures.Open(StreamInfo(16, 4096, 0, 2, 16, 0)));
        Assert.Throws<AudioFormatException>(() => FlacFixtures.Open(StreamInfo(15, 4096, 44100, 2, 16, 0)));
        Assert.Throws<AudioFormatException>(() => FlacFixtures.Open(StreamInfo(4096, 1024, 44100, 2, 16, 0)));
        Assert.Throws<AudioFormatException>(() => FlacFixtures.Open(StreamInfo(16, 4096, 44100, 2, 3, 0)));
    }

    [Fact]
    public void MetadataSizes_AreBoundedByTheFileNotByTheirClaims()
    {
        // A 16 MiB PICTURE block claimed in a 50-byte file is skipped without being buffered and ends the stream cleanly as truncated.
        var h = StreamInfo(16, 4096, 44100, 2, 16, 100, lastFlag: 0);
        var file = h.Concat(new byte[] { 0x86, 0xFF, 0xFF, 0xFF }).ToArray();
        Assert.Throws<AudioFormatException>(() => FlacFixtures.Open(file));

        // A VORBIS_COMMENT above the metadata limit is skipped, not allocated: the file opens without comments.
        var vc = h.Concat(new byte[] { 0x84, 0x10, 0x00, 0x00 }).Concat(new byte[0x100000]).ToArray();
        using var d = FlacFixtures.Open(vc, new AudioLimits { MaxMetadataBytes = 1024 });
        Assert.Null(d.Vendor);
        Assert.Empty(d.Comments);
        Assert.Equal(0, d.Read(new float[8]));
    }

    [Fact]
    public void VorbisComment_WithImpossibleCounts_IsRejected()
    {
        var h = StreamInfo(16, 4096, 44100, 2, 16, 100, lastFlag: 0);
        var body = new byte[12];
        BinaryPrimitives.WriteUInt32LittleEndian(body.AsSpan(4), 0xFFFFFFF0); // comment count far beyond the block
        var file = h.Concat(new byte[] { 0x84, 0, 0, (byte)body.Length }).Concat(body).ToArray();
        Assert.Throws<AudioFormatException>(() => FlacFixtures.Open(file));
    }

    // ---- Allocation ------------------------------------------------------------------------------------

    [Fact]
    public void Read_AllocatesNothing_AfterWarmup()
    {
        var data = FlacFixtures.Bytes("stereo16_noseektable");
        using var d = FlacFixtures.Open(data);
        var f = new float[2 * 1000];
        var ints = new int[2 * 1000];
        for (int i = 0; i < 8; i++) { d.Read(f); d.ReadInteger(ints); }
        long before = GC.GetAllocatedBytesForCurrentThread();
        int reads = 0;
        while (d.Read(f) > 0 && reads++ < 20) d.ReadInteger(ints);
        long bytes = GC.GetAllocatedBytesForCurrentThread() - before;
        Assert.True(reads >= 20);
        Assert.Equal(0, bytes);
        // Seeking decodes frames too, and must not allocate either.
        d.Seek(1);
        before = GC.GetAllocatedBytesForCurrentThread();
        d.Seek(60000);
        d.Read(f);
        d.Seek(20);
        bytes = GC.GetAllocatedBytesForCurrentThread() - before;
        Assert.Equal(0, bytes);
    }
}
