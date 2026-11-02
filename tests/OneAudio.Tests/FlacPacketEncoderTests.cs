using System.Diagnostics;
using System.Security.Cryptography;
using OneAudio.Flac;
using Xunit;

namespace OneAudio.Tests;

/// <summary>An in-memory <see cref="IPacketSource"/> over a list of packets, with no container around them.</summary>
internal sealed class MemoryPacketSource(CodecId codec, List<byte[]> packets, int headerPackets, ReadOnlyMemory<byte> config = default, int sampleRate = 0, int channels = 0) : IPacketSource
{
    private int _next;
    public CodecId Codec => codec;
    public long LengthFrames => -1;
    public bool CanSeek => false;
    public ReadOnlyMemory<byte> CodecConfig => config;
    public int SampleRate => sampleRate;
    public int Channels => channels;

    public bool TryReadPacket(out PacketView packet)
    {
        if (_next >= packets.Count) { packet = default; return false; }
        packet = new PacketView(packets[_next], -1, _next < headerPackets, 0, 0);
        _next++;
        return true;
    }

    public void SeekToFrame(long frame, out long startFrame) => throw new NotSupportedException();
    public void Dispose() { }
}

public class FlacPacketEncoderTests
{
    private static (List<FlacPacket> Frames, List<byte[]> Bytes, FlacPacketEncoder Enc) Run(int[] samples, int bits, int channels, int rate, FlacEncoderSettings? settings, int chunkFrames)
    {
        settings = (settings ?? new FlacEncoderSettings()) with { BitsPerSample = bits };
        var enc = new FlacPacketEncoder(new AudioFormat(rate, channels), settings);
        var frames = new List<FlacPacket>();
        var bytes = new List<byte[]>();
        void Drain()
        {
            while (enc.TryReadPacket(out var p)) { frames.Add(p with { Data = default }); bytes.Add(p.Data.ToArray()); }
        }
        // Chunk sizes that do not line up with the block size exercise the partial-take protocol.
        for (int pos = 0; pos < samples.Length;)
        {
            var span = samples.AsSpan(pos, Math.Min(chunkFrames * channels, samples.Length - pos));
            while (span.Length > 0)
            {
                int taken = enc.WriteInteger(span);
                span = span[(taken * channels)..];
                pos += taken * channels;
                Drain();
            }
        }
        enc.Finish();
        Drain();
        return (frames, bytes, enc);
    }

    [Theory]
    [InlineData(16, 2, 44100, 5, 12345, 1000)]
    [InlineData(16, 1, 48000, 0, 4096, 4096)]
    [InlineData(24, 2, 96000, 8, 20000, 777)]
    [InlineData(8, 1, 8000, 3, 5000, 100)]
    [InlineData(16, 2, 44100, 5, 1, 1)]
    [InlineData(32, 2, 44100, 5, 9000, 4096)]
    public void PacketsDecodeBitExactThroughOurDecoder(int bits, int channels, int rate, int level, int frames, int chunk)
    {
        var src = EncoderSignals.Make("music", bits, channels, frames, rate);
        var (list, bytes, enc) = Run(src, bits, channels, rate, new FlacEncoderSettings { CompressionLevel = level }, chunk);

        // Timing: first samples run back to back, every block but the last is full.
        long expect = 0;
        for (int i = 0; i < list.Count; i++)
        {
            Assert.Equal(expect, list[i].FirstSample);
            Assert.Equal(i, list[i].Index);
            if (i < list.Count - 1) Assert.Equal(enc.BlockSize, list[i].Samples);
            expect += list[i].Samples;
        }
        Assert.Equal(frames, expect);

        var packets = new List<byte[]> { enc.GetOggIdentificationPacket() };
        packets.AddRange(bytes);
        using var dec = FlacCodec.Instance.CreateDecoder(new MemoryPacketSource(CodecId.Flac, packets, 1), AudioLimits.Default);
        Assert.Equal(frames, dec.LengthFrames);
        var got = new int[src.Length];
        int filled = 0, n;
        while (filled < got.Length && (n = dec.ReadInteger(got.AsSpan(filled))) > 0) filled += n * channels;
        Assert.Equal(src.Length, filled);
        Assert.True(src.AsSpan().SequenceEqual(got));
    }

    [Fact]
    public void StreamInfoCarriesLengthAndMd5OfThePcm()
    {
        var src = EncoderSignals.Make("music", 16, 2, 10000);
        var (_, bytes, enc) = Run(src, 16, 2, 44100, null, 4096);
        byte[] si = enc.GetStreamInfo();
        Assert.Equal(34, si.Length);
        var raw = new byte[src.Length * 2];
        for (int i = 0; i < src.Length; i++) { raw[2 * i] = (byte)src[i]; raw[2 * i + 1] = (byte)(src[i] >> 8); }
        using var md5 = IncrementalHash.CreateHash(HashAlgorithmName.MD5);
        md5.AppendData(raw);
        Assert.Equal(md5.GetHashAndReset(), si[18..34]);
        // 20-bit sample rate, 3-bit channels - 1, 5-bit bits - 1, 36-bit total
        ulong packed = System.Buffers.Binary.BinaryPrimitives.ReadUInt64BigEndian(si.AsSpan(10));
        Assert.Equal(44100UL, packed >> 44);
        Assert.Equal(1UL, (packed >> 41) & 7);
        Assert.Equal(15UL, (packed >> 36) & 31);
        Assert.Equal(10000UL, packed & 0xF_FFFF_FFFFUL);
        var block = enc.GetStreamInfoBlock();
        Assert.Equal(0x80, block[0]);
        Assert.Equal(34, block[3]);
        Assert.True(block.AsSpan(4).SequenceEqual(si));
        Assert.Equal(bytes.Max(b => b.Length), (si[7] << 16) | (si[8] << 8) | si[9]);
    }

    [Fact]
    public void FramesEqualThoseOfTheFileEncoder()
    {
        var src = EncoderSignals.Make("music", 16, 2, 30000);
        var settings = new FlacEncoderSettings { CompressionLevel = 5, SeekPoints = 0 };
        var (_, bytes, enc) = Run(src, 16, 2, 44100, settings, 5000);
        var file = EncoderSignals.Encode(src, 16, 2, 44100, settings);
        var cat = bytes.SelectMany(b => b).ToArray();
        Assert.True(file.AsSpan(file.Length - cat.Length).SequenceEqual(cat), "the packets are the file's frames, byte for byte");
        _ = enc;
    }

    [Fact]
    public void ApiRefusesMisuse()
    {
        var enc = new FlacPacketEncoder(new AudioFormat(44100, 2), new FlacEncoderSettings { BitsPerSample = 16 });
        Assert.Throws<ArgumentException>(() => enc.WriteInteger(new int[3]));
        Assert.Throws<ArgumentOutOfRangeException>(() => enc.WriteInteger([40000, 0]));
        Assert.Equal(0, enc.WriteInteger([]));
        var big = new int[2 * 5000];
        Assert.Equal(4096, enc.WriteInteger(big));
        Assert.Equal(0, enc.WriteInteger(big)); // a packet is waiting
        Assert.Throws<InvalidOperationException>(enc.Finish);
        Assert.True(enc.TryReadPacket(out _));
        enc.Finish();
        Assert.False(enc.TryReadPacket(out _));
        Assert.Throws<InvalidOperationException>(() => enc.WriteInteger(big));
        Assert.Throws<ArgumentException>(() => new FlacPacketEncoder(new AudioFormat(44100, 9)));
        enc.Dispose();
    }

    [Fact]
    public void NativeFileMadeFromPacketsDecodesThroughFlacBinary()
    {
        string? flac = FlacTestEnv.FlacBinary;
        Assert.SkipUnless(flac is not null, "flac binary not available.");
        foreach (var (bits, ch, rate) in new[] { (16, 2, 44100), (24, 1, 48000), (8, 2, 22050) })
        {
            var src = EncoderSignals.Make("music", bits, ch, 25000, rate);
            var (_, bytes, enc) = Run(src, bits, ch, rate, new FlacEncoderSettings { CompressionLevel = 6 }, 3000);
            string dir = Environment.GetEnvironmentVariable("TMPDIR") is { Length: > 0 } t ? t : Path.GetTempPath();
            string path = Path.Combine(dir, "oneaudio-pkt-" + Guid.NewGuid().ToString("N")[..8] + ".flac");
            string wav = path + ".raw";
            try
            {
                using (var f = File.Create(path))
                {
                    f.Write("fLaC"u8);
                    f.Write(enc.GetStreamInfoBlock());
                    foreach (var b in bytes) f.Write(b);
                }
                var psi = new ProcessStartInfo(flac!) { RedirectStandardError = true, RedirectStandardOutput = true };
                foreach (var a in new[] { "-d", "-f", "-s", "--force-raw-format", "--endian=little", "--sign=signed", "-o", wav, path }) psi.ArgumentList.Add(a);
                using var p = Process.Start(psi)!;
                string err = p.StandardError.ReadToEnd();
                p.StandardOutput.ReadToEnd();
                p.WaitForExit();
                // flac -d verifies the MD5 in STREAMINFO and fails on a mismatch.
                Assert.True(p.ExitCode == 0, err);
                var raw = File.ReadAllBytes(wav);
                int bytesPer = (bits + 7) / 8;
                Assert.Equal(src.Length * bytesPer, raw.Length);
                for (int i = 0; i < src.Length; i++)
                {
                    int v = 0;
                    for (int k = 0; k < bytesPer; k++) v |= raw[i * bytesPer + k] << (8 * k);
                    v = bytesPer == 4 ? v : (v << (32 - 8 * bytesPer)) >> (32 - 8 * bytesPer);
                    if (bits == 8) v = (sbyte)raw[i];
                    Assert.Equal(src[i], v);
                }
            }
            finally
            {
                File.Delete(path);
                File.Delete(wav);
            }
        }
    }

    [Fact]
    public void FlacInMp4MadeByTheWriterDecodesBitExactInFfmpeg()
    {
        string? ffmpeg = ContainerTestEnv.Ffmpeg;
        Assert.SkipUnless(ffmpeg is not null && File.Exists(ffmpeg), "ffmpeg not found.");
        foreach (var (bits, ch, rate) in new[] { (16, 2, 44100), (24, 1, 48000) })
        {
            var src = EncoderSignals.Make("music", bits, ch, 20000, rate);
            var (list, bytes, enc) = Run(src, bits, ch, rate, null, 2500);
            string dir = Environment.GetEnvironmentVariable("TMPDIR") is { Length: > 0 } t ? t : Path.GetTempPath();
            string path = Path.Combine(dir, "oneaudio-flacmp4-" + Guid.NewGuid().ToString("N")[..8] + ".mp4");
            try
            {
                using (var f = File.Create(path))
                using (var w = OneAudio.Mp4.Mp4AudioWriter.CreateFlac(f, rate, ch, bits, leaveOpen: true))
                {
                    for (int i = 0; i < bytes.Count; i++) w.AddSample(bytes[i], list[i].Samples);
                    w.Finish(20000, enc.GetStreamInfoBlock());
                }
                var psi = new ProcessStartInfo(ffmpeg!) { RedirectStandardError = true, RedirectStandardOutput = true };
                foreach (var a in new[] { "-v", "error", "-i", path, "-f", "s32le", "-" }) psi.ArgumentList.Add(a);
                using var p = Process.Start(psi)!;
                using var o = new MemoryStream();
                p.StandardOutput.BaseStream.CopyTo(o);
                string err = p.StandardError.ReadToEnd();
                p.WaitForExit();
                Assert.True(p.ExitCode == 0 && err.Length == 0, err);
                var raw = o.ToArray();
                Assert.Equal(src.Length * 4, raw.Length);
                int shift = 32 - bits;
                for (int i = 0; i < src.Length; i++)
                    Assert.Equal(src[i] << shift, BitConverter.ToInt32(raw, 4 * i));
            }
            finally { File.Delete(path); }
        }
    }
}
