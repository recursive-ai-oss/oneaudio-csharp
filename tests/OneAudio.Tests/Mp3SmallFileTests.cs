using Xunit;

namespace OneAudio.Tests;

/// <summary>
/// Decoder checks on the small LAME encodes committed in testdata/mp3 (0.6 s of the generated test signal each, made by
/// tools/gen-mp3-testset.py --small), so they run without the fetched corpus.
/// </summary>
public class Mp3SmallFileTests
{
    public static TheoryData<string, int, int> Files() => new()
    {
        { "small_44100_2_joint.mp3", 44100, 2 },
        { "small_48000_2_stereo.mp3", 48000, 2 },
        { "small_22050_1_vbr.mp3", 22050, 1 },
        { "small_8000_1_cbr.mp3", 8000, 1 },
    };

    private static byte[] Load(string name) => File.ReadAllBytes(Path.Combine(AppContext.BaseDirectory, "testdata", "mp3", name));

    [Theory]
    [MemberData(nameof(Files))]
    public void GaplessLengthIsTheSourceLength(string file, int rate, int channels)
    {
        using var dec = AudioDecoder.Open(new MemoryStream(Load(file)), Mp3TestEnv.Registry);
        Assert.Equal(rate, dec.Format.SampleRate);
        Assert.Equal(channels, dec.Format.Channels);
        int expected = (int)(rate * 0.6);
        Assert.Equal(expected, dec.LengthFrames);
        var pcm = Mp3TestEnv.ReadAll(dec);
        Assert.Equal(expected, pcm.Length / channels);
        float peak = 0;
        foreach (float s in pcm) peak = Math.Max(peak, Math.Abs(s));
        Assert.InRange(peak, 0.2f, 1.5f);
    }

    [Theory]
    [MemberData(nameof(Files))]
    public void SeekMatchesLinearDecodeBitForBit(string file, int rate, int channels)
    {
        Assert.True(rate > 0);
        var data = Load(file);
        var linear = Mp3TestEnv.DecodeAll(new MemoryStream(data), out _);
        long frames = linear.Length / channels;
        using var dec = AudioDecoder.Open(new MemoryStream(data), Mp3TestEnv.Registry);
        var rng = new Random(5);
        var buf = new float[channels * 700];
        for (int i = 0; i < 30; i++)
        {
            long target = i == 0 ? 0 : i == 1 ? frames - 1 : rng.NextInt64(0, frames);
            dec.Seek(target);
            int n = dec.Read(buf);
            Assert.Equal(Math.Min(700, frames - target), n);
            for (int k = 0; k < n * channels; k++)
                Assert.Equal(BitConverter.SingleToInt32Bits(linear[target * channels + k]), BitConverter.SingleToInt32Bits(buf[k]));
        }
    }

    [Theory]
    [MemberData(nameof(Files))]
    public void ReadAllocatesNothing(string file, int rate, int channels)
    {
        Assert.True(rate > 0);
        using var dec = AudioDecoder.Open(new MemoryStream(Load(file)), Mp3TestEnv.Registry);
        var buf = new float[256 * channels];
        for (int i = 0; i < 5; i++) dec.Read(buf);
        long before = GC.GetAllocatedBytesForCurrentThread();
        while (dec.Read(buf) > 0) { }
        Assert.Equal(0, GC.GetAllocatedBytesForCurrentThread() - before);
    }

    [Fact]
    public void VbriHeaderGivesTheFrameCount()
    {
        // Rewrite the Info frame of a stereo MPEG-1 file as a VBRI frame (both sit 36 bytes into the frame): the header
        // carries a frame count only, so the length is that many whole frames and nothing is trimmed.
        var data = Load("small_44100_2_joint.mp3");
        Assert.True(data.AsSpan(36, 4).SequenceEqual("Info"u8));
        "VBRI"u8.CopyTo(data.AsSpan(36));
        System.Buffers.Binary.BinaryPrimitives.WriteUInt32BigEndian(data.AsSpan(36 + 14), 7);
        using var dec = AudioDecoder.Open(new MemoryStream(data), Mp3TestEnv.Registry);
        Assert.Equal(7 * 1152, dec.LengthFrames);
        Assert.Equal(7 * 1152, Mp3TestEnv.ReadAll(dec).Length / 2);
    }

    [Fact]
    public void XingHeaderWithoutLameExtensionIsNotTrimmed()
    {
        // Same idea with the Xing flags set to "frame count only" and the LAME extension bytes blanked.
        var data = Load("small_44100_2_joint.mp3");
        "Xing"u8.CopyTo(data.AsSpan(36));
        System.Buffers.Binary.BinaryPrimitives.WriteUInt32BigEndian(data.AsSpan(40), 1);   // flags: frames present
        System.Buffers.Binary.BinaryPrimitives.WriteUInt32BigEndian(data.AsSpan(44), 9);   // frames
        data.AsSpan(48, 100).Clear();
        using var dec = AudioDecoder.Open(new MemoryStream(data), Mp3TestEnv.Registry);
        Assert.Equal(9 * 1152, dec.LengthFrames);
        Assert.Equal(9 * 1152, Mp3TestEnv.ReadAll(dec).Length / 2);
    }

    [Fact]
    public void ReopenedAfterSeekToStartDecodesIdentically()
    {
        var data = Load("small_44100_2_joint.mp3");
        using var dec = AudioDecoder.Open(new MemoryStream(data), Mp3TestEnv.Registry);
        var first = Mp3TestEnv.ReadAll(dec);
        dec.Seek(0);
        var second = Mp3TestEnv.ReadAll(dec);
        Assert.True(first.AsSpan().SequenceEqual(second));
    }
}
