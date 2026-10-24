using Xunit;

namespace OneAudio.Tests;

public class WavDecoderTests
{
    private static readonly AudioCodecRegistry Registry = new AudioCodecRegistry().AddWav();

    private static IAudioDecoder Open(byte[] file, AudioLimits? limits = null) =>
        AudioDecoder.Open(new MemoryStream(file), Registry, limits);

    [Fact]
    public void Pcm16_IntegersBitExact_AndFloatScaling()
    {
        short[] src = [0, 1, -1, short.MaxValue, short.MinValue, 1234, -4321, 7];
        using var d = Open(WavBuilder.Build(1, 2, 44100, 16, WavBuilder.Pcm16(src)));
        Assert.Equal(new AudioFormat(44100, 2, ChannelMask.FrontLeft | ChannelMask.FrontRight, NativeSampleType.PcmSigned16, 16), d.Format);
        Assert.Equal(4, d.LengthFrames);
        Assert.True(d.HasNativeIntegers);
        var ints = new int[8];
        Assert.Equal(4, d.ReadInteger(ints));
        Assert.Equal(src.Select(x => (int)x), ints);
        Assert.Equal(0, d.ReadInteger(ints));
        d.Seek(0);
        var f = new float[8];
        Assert.Equal(4, d.Read(f));
        for (int i = 0; i < 8; i++) Assert.Equal(src[i] / 32768f, f[i]);
        Assert.Equal(-1f, f[4]);
    }

    [Fact]
    public void Pcm24_And32_BitExact()
    {
        int[] s24 = [0, 1, -1, 8388607, -8388608, 123456, -654321];
        using (var d = Open(WavBuilder.Build(1, 1, 48000, 24, WavBuilder.Pcm24(s24))))
        {
            var ints = new int[7];
            Assert.Equal(7, d.ReadInteger(ints));
            Assert.Equal(s24, ints);
        }
        int[] s32 = [0, 1, -1, int.MaxValue, int.MinValue, 99999999];
        using (var d = Open(WavBuilder.Build(1, 1, 48000, 32, WavBuilder.Pcm32(s32))))
        {
            var ints = new int[6];
            Assert.Equal(6, d.ReadInteger(ints));
            Assert.Equal(s32, ints);
        }
    }

    [Fact]
    public void Pcm8_IsRecentred()
    {
        using var d = Open(WavBuilder.Build(1, 1, 8000, 8, [0, 128, 255]));
        var ints = new int[3];
        d.ReadInteger(ints);
        Assert.Equal(new[] { -128, 0, 127 }, ints);
        d.Seek(0);
        var f = new float[3];
        d.Read(f);
        Assert.Equal(new[] { -1f, 0f, 127f / 128f }, f);
    }

    [Fact]
    public void Float32_And64_PassThrough_NoIntegers()
    {
        float[] s = [0.25f, -0.5f, 1.5f, float.NaN];
        using (var d = Open(WavBuilder.Build(3, 1, 48000, 32, WavBuilder.Float32(s))))
        {
            Assert.False(d.HasNativeIntegers);
            Assert.Throws<NotSupportedException>(() => d.ReadInteger(new int[4]));
            var f = new float[4];
            Assert.Equal(4, d.Read(f));
            Assert.Equal(0.25f, f[0]); Assert.Equal(1.5f, f[2]); Assert.True(float.IsNaN(f[3]));
        }
        using (var d = Open(WavBuilder.Build(3, 1, 48000, 64, WavBuilder.Float64([0.125, -2.0]))))
        {
            var f = new float[2];
            Assert.Equal(2, d.Read(f));
            Assert.Equal(new[] { 0.125f, -2f }, f);
        }
    }

    [Fact]
    public void Extensible_UsesSubFormatAndMask()
    {
        uint mask = 0x3F; // 5.1
        using var d = Open(WavBuilder.Build(1, 6, 48000, 24, WavBuilder.Pcm24(new int[12]), extensible: true, mask: mask));
        Assert.Equal(6, d.Format.Channels);
        Assert.Equal((ChannelMask)mask, d.Format.ChannelMask);
        Assert.Equal(NativeSampleType.PcmSigned24, d.Format.NativeType);
        Assert.Equal(2, d.LengthFrames);
    }

    [Fact]
    public void ShortReads_AndPartialRequests()
    {
        var src = Enumerable.Range(0, 20000).Select(i => (short)(i * 7)).ToArray();
        using var d = Open(WavBuilder.Build(1, 1, 8000, 16, WavBuilder.Pcm16(src)));
        var ints = new int[777];
        var all = new List<int>();
        int n;
        while ((n = d.ReadInteger(ints)) > 0) all.AddRange(ints.Take(n));
        Assert.Equal(src.Select(x => (int)x), all);
        Assert.Equal(20000, d.PositionFrames);
    }

    [Fact]
    public void Seek_IsSampleAccurate_AndRangeChecked()
    {
        var src = Enumerable.Range(0, 1000).Select(i => (short)i).ToArray();
        using var d = Open(WavBuilder.Build(1, 2, 8000, 16, WavBuilder.Pcm16(src)));
        Assert.True(d.CanSeek);
        d.Seek(250);
        var ints = new int[4];
        Assert.Equal(2, d.ReadInteger(ints));
        Assert.Equal(new[] { 500, 501, 502, 503 }, ints);
        d.Seek(d.LengthFrames);
        Assert.Equal(0, d.ReadInteger(ints));
        Assert.Throws<ArgumentOutOfRangeException>(() => d.Seek(501));
        Assert.Throws<ArgumentOutOfRangeException>(() => d.Seek(-1));
    }

    [Fact]
    public void NonSeekableStream_Streams_AndRejectsSeek()
    {
        var src = Enumerable.Range(0, 300).Select(i => (short)(i - 150)).ToArray();
        var file = WavBuilder.Build(1, 1, 8000, 16, WavBuilder.Pcm16(src));
        using var d = AudioDecoder.Open(new NonSeekableStream(file), Registry);
        Assert.False(d.CanSeek);
        Assert.Equal(300, d.LengthFrames);
        Assert.Throws<NotSupportedException>(() => d.Seek(0));
        var ints = new int[300];
        Assert.Equal(300, d.ReadInteger(ints));
        Assert.Equal(src.Select(x => (int)x), ints);
    }

    [Fact]
    public void UnsizedData_ExtendsToEndOfFile()
    {
        var data = WavBuilder.Pcm16([1, 2, 3, 4]);
        using var d = Open(WavBuilder.Build(1, 1, 8000, 16, data, declaredDataSize: -1 /* 0xFFFFFFFF written below */));
        Assert.Equal(4, d.LengthFrames);
        var file0 = WavBuilder.Build(1, 1, 8000, 16, data, declaredDataSize: 0);
        using var d0 = Open(file0);
        Assert.Equal(4, d0.LengthFrames);
        using var dn = AudioDecoder.Open(new NonSeekableStream(file0), Registry);
        Assert.Equal(-1, dn.LengthFrames);
        var ints = new int[8];
        Assert.Equal(4, dn.ReadInteger(ints));
    }

    [Fact]
    public void TruncatedData_StopsAtRealEnd()
    {
        var data = WavBuilder.Pcm16([1, 2, 3, 4, 5, 6]);
        var file = WavBuilder.Build(1, 1, 8000, 16, data, declaredDataSize: 1000);
        using var d = Open(file);
        Assert.Equal(6, d.LengthFrames);
        using var dn = AudioDecoder.Open(new NonSeekableStream(file), Registry);
        var ints = new int[100];
        Assert.Equal(6, dn.ReadInteger(ints));
        Assert.Equal(0, dn.ReadInteger(ints));
    }

    [Fact]
    public void Rf64_UsesDs64Size()
    {
        using var d = Open(WavBuilder.Build(1, 1, 8000, 16, WavBuilder.Pcm16([5, 6, 7]), rf64: true));
        Assert.Equal(3, d.LengthFrames);
    }

    [Fact]
    public void Limits_RejectBeforeAllocating()
    {
        var file = WavBuilder.Build(1, 2, 48000, 16, new byte[8]);
        var e = Assert.Throws<AudioLimitExceededException>(() => Open(file, new AudioLimits { MaxChannels = 1 }));
        Assert.Equal("MaxChannels", e.LimitName);
        Assert.Throws<AudioLimitExceededException>(() => Open(file, new AudioLimits { MaxSampleRate = 8000 }));

        // A fmt chunk claiming 4 GB must be refused without reading it.
        var huge = WavBuilder.Build(1, 1, 8000, 16, new byte[2]);
        huge[16] = huge[17] = huge[18] = huge[19] = 0xFF;
        Assert.Throws<AudioLimitExceededException>(() => Open(huge));

        // Endless chunk walk is capped.
        var many = new List<byte>(WavBuilder.Build(1, 1, 8000, 16, new byte[2]).Take(12));
        for (int i = 0; i < 50; i++) many.AddRange("JUNK\0\0\0\0"u8.ToArray());
        Assert.Throws<AudioLimitExceededException>(() => Open([.. many], new AudioLimits { MaxContainerElements = 10 }));
    }

    [Fact]
    public void MalformedInput_ThrowsFormatException()
    {
        var good = WavBuilder.Build(1, 1, 8000, 16, new byte[4]);
        Assert.Throws<UnsupportedAudioFormatException>(() => Open("not a wav file at all"u8.ToArray()));
        Assert.Throws<AudioFormatException>(() => AudioDecoder.Open(new MemoryStream(good[..20]), Registry));
        Assert.Throws<AudioFormatException>(() => AudioDecoder.Open(new MemoryStream(good[..12]), Registry)); // no fmt
        var badAlign = (byte[])good.Clone();
        badAlign[32] = 3; // blockAlign
        Assert.Throws<AudioFormatException>(() => Open(badAlign));
        var adpcm = WavBuilder.Build(2, 1, 8000, 4, new byte[4]);
        Assert.Throws<UnsupportedAudioFormatException>(() => Open(adpcm));
        Assert.Throws<AudioFormatException>(() => Open(WavBuilder.Build(1, 0, 8000, 16, new byte[4])));
    }

    [Fact]
    public void Dispose_ClosesStream_UnlessLeaveOpen()
    {
        var file = WavBuilder.Build(1, 1, 8000, 16, new byte[4]);
        var ms = new MemoryStream(file);
        AudioDecoder.Open(ms, Registry, leaveOpen: true).Dispose();
        Assert.True(ms.CanRead);
        var ms2 = new MemoryStream(file);
        var d = AudioDecoder.Open(ms2, Registry);
        d.Dispose();
        Assert.False(ms2.CanRead);
        Assert.Throws<ObjectDisposedException>(() => d.Read(new float[2]));
    }

    [Fact]
    public void AudioClip_FromDecoder_LoadsEverything()
    {
        short[] src = [100, -100, 200, -200];
        using var d = Open(WavBuilder.Build(1, 2, 8000, 16, WavBuilder.Pcm16(src)));
        var clip = AudioClip.FromDecoder(d);
        Assert.Equal(2, clip.Frames);
        Assert.Throws<AudioLimitExceededException>(() => { d.Seek(0); AudioClip.FromDecoder(d, maxFrames: 1); });
    }
}
