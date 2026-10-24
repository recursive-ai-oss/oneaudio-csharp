using OneAudio.Ogg;
using Xunit;

namespace OneAudio.Tests;

public class OggContainerTests
{
    private static byte[] Pk(int length, byte fill)
    {
        var p = new byte[length];
        Array.Fill(p, fill);
        return p;
    }

    /// <summary>Wraps a stream so that it cannot seek.</summary>
    private sealed class ForwardOnly(byte[] data) : Stream
    {
        private readonly MemoryStream _ms = new(data);
        public override bool CanRead => true;
        public override bool CanSeek => false;
        public override bool CanWrite => false;
        public override long Length => throw new NotSupportedException();
        public override long Position { get => throw new NotSupportedException(); set => throw new NotSupportedException(); }
        public override void Flush() { }
        public override int Read(byte[] buffer, int offset, int count) => _ms.Read(buffer, offset, count);
        public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
        public override void SetLength(long value) => throw new NotSupportedException();
        public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();
    }

    private static byte[] OpusHead(byte fill = 0) => [.. "OpusHead"u8, 1, 2, .. new byte[10], fill];
    private static byte[] OpusTags() => [.. "OpusTags"u8, 0, 0, 0, 0, 0, 0, 0, 0];

    [Fact]
    public void PacketsSurviveEveryLacingAndPageSplit()
    {
        int[] sizes = [0, 1, 100, 254, 255, 256, 509, 510, 511, 1000, 65025, 70000, 3, 0, 255];
        var packets = new List<byte[]> { OpusHead(), OpusTags() };
        for (int i = 0; i < sizes.Length; i++) packets.Add(Pk(sizes[i], (byte)(i + 1)));
        var granules = packets.Select((_, i) => (long)i * 100).ToList();
        foreach (int maxBody in new[] { 300, 4000, 1 << 20 })
        {
            var file = new OggBuilder().AddStream(7, packets, granules, maxBody, pageBreakAfter: new HashSet<int> { 1 }).ToArray();
            using var src = OggPacketSource.Open(new MemoryStream(file));
            Assert.Equal(CodecId.Opus, src.Codec);
            for (int i = 0; i < packets.Count; i++)
            {
                Assert.True(src.TryReadPacket(out var p), $"packet {i} missing at maxBody {maxBody}");
                Assert.True(p.Data.SequenceEqual(packets[i]), $"packet {i} differs at maxBody {maxBody}");
                Assert.Equal(i < 2, p.IsHeader);
            }
            Assert.False(src.TryReadPacket(out _));
        }
    }

    [Fact]
    public void EndFrameIsOnlyOnTheLastPacketOfAPage()
    {
        var packets = new List<byte[]> { OpusHead(), OpusTags(), Pk(10, 1), Pk(10, 2), Pk(10, 3), Pk(10, 4) };
        var granules = new List<long> { 0, 0, -1, 960, -1, 1920 };
        var file = new OggBuilder().AddStream(1, packets, granules, 1 << 20, pageBreakAfter: new HashSet<int> { 1, 3 }).ToArray();
        using var src = OggPacketSource.Open(new MemoryStream(file));
        var ends = new List<long>();
        while (src.TryReadPacket(out var p)) ends.Add(p.EndFrame);
        Assert.Equal([0L, 0L, -1L, 960L, -1L, 1920L], ends);
        Assert.Equal(1920, src.LengthFrames);
    }

    [Theory]
    [InlineData("vorbis")]
    [InlineData("opus")]
    [InlineData("flac")]
    [InlineData("flac-native")]
    public void CodecComesFromTheFirstPacket(string kind)
    {
        byte[] first = kind switch
        {
            "vorbis" => [1, .. "vorbis"u8, 0, 0, 0, 0],
            "opus" => OpusHead(),
            "flac" => [0x7F, .. "FLAC"u8, 1, 0, 0, 0, .. "fLaC"u8],
            _ => [.. "fLaC"u8, 0, 0, 0, 34],
        };
        var file = new OggBuilder().AddStream(3, [first, Pk(5, 1)], [0, 0]).ToArray();
        using var src = OggPacketSource.Open(new MemoryStream(file));
        Assert.Equal(kind.StartsWith("flac") ? CodecId.Flac : kind == "vorbis" ? CodecId.Vorbis : CodecId.Opus, src.Codec);
    }

    [Fact]
    public void UnknownCodecIsUnsupported()
    {
        var file = new OggBuilder().AddStream(3, [Pk(30, 9)], [0]).ToArray();
        Assert.Throws<UnsupportedAudioFormatException>(() => OggPacketSource.Open(new MemoryStream(file)));
    }

    [Fact]
    public void NotOggIsAFormatError()
    {
        Assert.Throws<AudioFormatException>(() => OggPacketSource.Open(new MemoryStream(Pk(200, 0x55))));
    }

    [Fact]
    public void DamagedPageIsSkippedAndReadingResynchronises()
    {
        var packets = new List<byte[]> { OpusHead(), OpusTags(), Pk(30, 1), Pk(30, 2), Pk(30, 3) };
        var granules = new List<long> { 0, 0, 100, 200, 300 };
        var file = new OggBuilder().AddStream(1, packets, granules, 1 << 20, new HashSet<int> { 1, 2, 3 }).ToArray();
        // Flip a payload byte in the page that carries packet 3 (breaking its checksum) and add junk in front.
        int pos = 0, page = 0;
        while (page < 4) { int n = file[pos + 26]; int body = 0; for (int i = 0; i < n; i++) body += file[pos + 27 + i]; if (page == 3) { file[pos + 27 + n + 2] ^= 0xFF; } pos += 27 + n + body; page++; }
        var damaged = new byte[] { 1, 2, 3, (byte)'O', (byte)'g', 9 }.Concat(file).ToArray();
        using var src = OggPacketSource.Open(new MemoryStream(damaged));
        var got = new List<byte>();
        while (src.TryReadPacket(out var p)) if (!p.IsHeader) got.Add(p.Data[0]);
        Assert.Equal([1, 3], got); // packet 2 died with its page
    }

    [Fact]
    public void ChainedLinksAreFoundAndStreamedInBand()
    {
        var b = new OggBuilder();
        b.AddStream(1, [OpusHead(), OpusTags(), Pk(10, 1), Pk(10, 2)], [0, 0, 960, 1920], 1 << 20, new HashSet<int> { 1 });
        b.AddStream(2, [OpusHead(), OpusTags(), Pk(10, 3)], [0, 0, 960], 1 << 20, new HashSet<int> { 1 });
        var file = b.ToArray();

        using (var src = OggPacketSource.Open(new MemoryStream(file)))
        {
            Assert.Equal(2, src.LinkCount);
            Assert.Equal(new OggLinkInfo(1, CodecId.Opus, 0, 1920), src.GetLink(0));
            Assert.Equal(new OggLinkInfo(2, CodecId.Opus, 1920, 960), src.GetLink(1));
            Assert.Equal(2880, src.LengthFrames);
            var seen = new List<string>();
            while (src.TryReadPacket(out var p)) seen.Add(p.IsHeader ? "H" : p.Data[0].ToString());
            Assert.Equal(["H", "H", "1", "2", "H", "H", "3"], seen);
            Assert.Equal(1, src.CurrentLink);
        }

        // The same file through a stream that cannot seek: links show up as they are read.
        using (var src = OggPacketSource.Open(new ForwardOnly(file)))
        {
            Assert.False(src.CanSeek);
            Assert.Equal(-1, src.LengthFrames);
            var seen = new List<string>();
            while (src.TryReadPacket(out var p)) seen.Add(p.IsHeader ? "H" : p.Data[0].ToString());
            Assert.Equal(["H", "H", "1", "2", "H", "H", "3"], seen);
            Assert.Equal(2, src.LinkCount);
            Assert.Throws<NotSupportedException>(() => src.SeekToFrame(0, out _));
        }
    }

    [Fact]
    public void ForeignStreamsInTheSameFileAreSkipped()
    {
        var a = new OggBuilder();
        // A BOS page of an unknown stream, then the Opus stream, then a data page of the unknown stream in the middle.
        a.AddStream(99, [Pk(20, 7)], [0], eos: false);
        var opus = new OggBuilder().AddStream(5, [OpusHead(), OpusTags(), Pk(10, 1), Pk(10, 2)], [0, 0, 960, 1920], 1 << 20, new HashSet<int> { 1 }).ToArray();
        var junkData = OggBuilder.Page(99, 1, 0, 5, [10], Pk(10, 8));
        var file = a.ToArray().Concat(opus.Take(opus.Length)).Concat(junkData).ToArray();
        using var src = OggPacketSource.Open(new MemoryStream(file));
        Assert.Equal(CodecId.Opus, src.Codec);
        int audio = 0;
        while (src.TryReadPacket(out var p)) if (!p.IsHeader) audio++;
        Assert.Equal(2, audio);
    }

    [Fact]
    public void OversizedPacketHitsTheLimit()
    {
        var packets = new List<byte[]> { OpusHead(), OpusTags(), Pk(200_000, 1) };
        var file = new OggBuilder().AddStream(1, packets, [0, 0, 100], 4000, new HashSet<int> { 1 }).ToArray();
        using var src = OggPacketSource.Open(new MemoryStream(file), new AudioLimits { MaxPacketBytes = 100_000 });
        Assert.True(src.TryReadPacket(out _));
        Assert.True(src.TryReadPacket(out _));
        var ex = Assert.Throws<AudioLimitExceededException>(() => src.TryReadPacket(out _));
        Assert.Equal("MaxPacketBytes", ex.LimitName);
    }

    [Fact]
    public void SeekReturnsAPrimingPacketAtOrBeforeTheTarget()
    {
        // 200 audio packets of 960 frames, 4 per page, so page granules are multiples of 3840.
        var packets = new List<byte[]> { OpusHead(), OpusTags() };
        var granules = new List<long> { 0, 0 };
        for (int i = 0; i < 200; i++)
        {
            packets.Add(Pk(300, (byte)(i % 250 + 1)));
            granules.Add((i + 1) % 4 == 0 ? (i + 1) * 960L : -1);
        }
        var breaks = new HashSet<int> { 1 };
        for (int i = 3; i < 200; i += 4) breaks.Add(2 + i);
        var file = new OggBuilder().AddStream(1, packets, granules, 1 << 20, breaks).ToArray();
        using var src = OggPacketSource.Open(new MemoryStream(file));
        foreach (long target in new long[] { 0, 5, 3839, 3840, 3841, 50_000, 191_999, 192_000 })
        {
            src.SeekToFrame(target, out long start);
            Assert.True(start <= target, $"start {start} beyond target {target}");
            Assert.True(src.TryReadPacket(out var p));
            Assert.False(p.IsHeader);
            if (start > 0)
            {
                Assert.Equal(start, p.EndFrame);   // the first packet is the one that ends the chosen page
                Assert.Equal((byte)((start / 960 - 1) % 250 + 1), p.Data[0]);
                Assert.True(target - start < 3840 + 3840, "seek page is too far before the target");
            }
            // The stream then continues with consecutive packets.
            bool more = src.TryReadPacket(out var q);
            if (start < 192_000) Assert.True(more);
            if (more && start > 0) Assert.Equal((byte)(p.Data[0] % 250 + 1), q.Data[0]);
        }
    }
}
