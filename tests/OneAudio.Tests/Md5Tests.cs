// The BCL MD5 is the oracle for our own MD5 here; nothing in this file is security-sensitive.
#pragma warning disable CA5351
using System.Security.Cryptography;
using System.Text;
using OneAudio.Flac;
using OneAudio.Mp4;
using Xunit;

namespace OneAudio.Tests;

public class Md5Tests
{
    private static string Hex(ReadOnlySpan<byte> d) => Convert.ToHexStringLower(d);

    private static string Digest(byte[] data, params int[] chunks)
    {
        var md5 = new Md5();
        int pos = 0, i = 0;
        while (pos < data.Length)
        {
            int n = Math.Min(chunks.Length == 0 ? data.Length : chunks[i++ % chunks.Length], data.Length - pos);
            md5.Append(data.AsSpan(pos, n));
            pos += n;
        }
        Span<byte> d = stackalloc byte[16];
        md5.GetHashAndReset(d);
        return Hex(d);
    }

    // RFC 1321 Appendix A.5.
    [Theory]
    [InlineData("", "d41d8cd98f00b204e9800998ecf8427e")]
    [InlineData("a", "0cc175b9c0f1b6a831c399e269772661")]
    [InlineData("abc", "900150983cd24fb0d6963f7d28e17f72")]
    [InlineData("message digest", "f96b697d7cb7938d525a2f31aaf161d0")]
    [InlineData("abcdefghijklmnopqrstuvwxyz", "c3fcd3d76192e4007dfb496cca67e13b")]
    [InlineData("ABCDEFGHIJKLMNOPQRSTUVWXYZabcdefghijklmnopqrstuvwxyz0123456789", "d174ab98d277d9f5a5611c2c9f419d9f")]
    [InlineData("12345678901234567890123456789012345678901234567890123456789012345678901234567890", "57edf4a22be3c955ac49da2e2107b67a")]
    public void Rfc1321SuiteInUnevenChunks(string text, string expected)
    {
        var data = Encoding.ASCII.GetBytes(text);
        Assert.Equal(expected, Digest(data));
        Assert.Equal(expected, Digest(data, 1));
        Assert.Equal(expected, Digest(data, 7, 64, 3, 65, 1));
        Assert.Equal(expected, Digest(data, 13, 5));
    }

    [Fact]
    public void MatchesTheBclOnRandomInputsAndChunkings()
    {
        var rng = new Random(1321);
        foreach (int len in new[] { 0, 1, 55, 56, 57, 63, 64, 65, 119, 120, 127, 128, 129, 1000, 4096, 100003 })
        {
            var data = new byte[len];
            rng.NextBytes(data);
            string expected = Hex(MD5.HashData(data));
            Assert.Equal(expected, Digest(data));
            Assert.Equal(expected, Digest(data, rng.Next(1, 200), rng.Next(1, 90), 64));
        }
    }

    [Fact]
    public void CurrentHashDoesNotDisturbTheRunningHash()
    {
        var data = new byte[300];
        new Random(5).NextBytes(data);
        var md5 = new Md5();
        Span<byte> d = stackalloc byte[16];
        md5.Append(data.AsSpan(0, 100));
        md5.GetCurrentHash(d);
        Assert.Equal(Hex(MD5.HashData(data.AsSpan(0, 100))), Hex(d));
        md5.Append(data.AsSpan(100));
        md5.GetHashAndReset(d);
        Assert.Equal(Hex(MD5.HashData(data)), Hex(d));
        md5.GetHashAndReset(d); // reset: the empty hash
        Assert.Equal("d41d8cd98f00b204e9800998ecf8427e", Hex(d));
    }

    // The signature FLAC stores covers the samples as little-endian integers of whole bytes (RFC 9639 8.2).
    [Theory]
    [InlineData(16, 2)]
    [InlineData(24, 1)]
    [InlineData(8, 2)]
    public void EncodersStoreTheBclMd5OfThePcm(int bits, int channels)
    {
        var format = new AudioFormat(44100, channels, ChannelMask.None, bits == 8 ? NativeSampleType.PcmUnsigned8 : bits == 16 ? NativeSampleType.PcmSigned16 : NativeSampleType.PcmSigned24, bits);
        var rng = new Random(bits);
        var pcm = new int[channels * 9000];
        int max = (1 << (bits - 1)) - 1;
        for (int i = 0; i < pcm.Length; i++) pcm[i] = (int)(max * 0.5 * Math.Sin(i * 0.01)) + rng.Next(-20, 20);
        int bytes = bits / 8;
        var raw = new byte[pcm.Length * bytes];
        for (int i = 0; i < pcm.Length; i++)
            for (int b = 0; b < bytes; b++) raw[i * bytes + b] = (byte)(pcm[i] >> (8 * b));
        var expected = MD5.HashData(raw);

        var ms = new MemoryStream();
        using (var enc = new FlacEncoder(ms, format, leaveOpen: true)) { enc.WriteInteger(pcm); enc.Finish(); }
        Assert.Equal(Hex(expected), Hex(ms.ToArray().AsSpan(4 + 4 + 18, 16)));
        ms.Position = 0;
        Assert.True(FlacDecoder.Open(ms).VerifyMd5());

        using var pe = new FlacPacketEncoder(format);
        for (int pos = 0; pos < pcm.Length;)
        {
            pos += pe.WriteInteger(pcm.AsSpan(pos)) * channels;
            pe.TryReadPacket(out _);
        }
        pe.Finish();
        Assert.Equal(Hex(expected), Hex(pe.GetStreamInfo().AsSpan(18, 16)));
    }

    private sealed class WriteOnlySeekable : MemoryStream
    {
        public override bool CanRead => false;
    }

    [Fact]
    public void Mp4WriterFastStartDefaultsToTrueAndFallsBackOnAWriteOnlyStream()
    {
        var asc = new byte[] { 0x12, 0x10 };
        using (var w = Mp4AudioWriter.CreateAac(new MemoryStream(), 44100, 2, asc, 1024))
            Assert.True(w.FastStart);

        var ms = new WriteOnlySeekable();
        using (var w = Mp4AudioWriter.CreateAac(ms, 44100, 2, asc, 1024, leaveOpen: true))
        {
            w.AddSample(new byte[10], 1024);
            w.Finish(1); // no exception: moov stays last
            Assert.Throws<ArgumentException>(() => { var w2 = Mp4AudioWriter.CreateAac(new WriteOnlySeekable(), 44100, 2, asc, 1024); w2.FastStart = true; });
        }
        var b = ms.ToArray();
        string text = Encoding.Latin1.GetString(b);
        Assert.True(text.IndexOf("mdat", StringComparison.Ordinal) < text.IndexOf("moov", StringComparison.Ordinal));
    }
}
