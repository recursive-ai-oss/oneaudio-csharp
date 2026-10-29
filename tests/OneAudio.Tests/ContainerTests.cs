using System.Buffers.Binary;
using OneAudio.Aac;
using OneAudio.Amr;
using OneAudio.Mp4;
using Xunit;

namespace OneAudio.Tests;

/// <summary>Track info, priming, metadata, forward-only reading, registry behaviour and hostile input for the MP4, ADTS and AMR readers.</summary>
public class ContainerTests
{
    private static byte[] Bytes(string name) => File.ReadAllBytes(ContainerTestEnv.TestData(name));

    // --- track info and priming ---------------------------------------------------------------------------------------

    [Fact]
    public void AacTrackReportsConfigAndFormat()
    {
        using var src = Mp4PacketSource.Open(new MemoryStream(Bytes("aac-moov-last.m4a")));
        Assert.Equal(CodecId.Aac, src.Codec);
        Assert.Equal(44100, src.SampleRate);
        Assert.Equal(2, src.Channels);
        Assert.Equal(44100, src.TimeScale);
        Assert.Equal(2, src.SelectedTrack.AacObjectType);
        Assert.Equal(0x40, src.SelectedTrack.ObjectTypeIndication);
        Assert.Equal("M4A ", src.MajorBrand);
        // AudioSpecificConfig of AAC-LC, 44.1 kHz, stereo: 00010 0100 0010 000.
        // Handed over untouched: ffmpeg's encoder appends an SBR-signalling extension after the two config bytes.
        Assert.Equal(new byte[] { 0x12, 0x10, 0x56, 0xE5, 0x00 }, src.CodecConfig.ToArray());
        Assert.Equal(66, src.SelectedTrack.SampleCount);
    }

    [Fact]
    public void FfmpegEditListGivesPrimingAndLength()
    {
        using var src = Mp4PacketSource.Open(new MemoryStream(Bytes("aac-moov-last.m4a")));
        Assert.Equal(GaplessSource.EditList, src.SelectedTrack.Gapless);
        Assert.Equal(1024, src.PrimingFrames);
        Assert.Equal(1024, src.SelectedTrack.EditListMediaTime);
        Assert.Equal(66150, src.LengthFrames); // 1.5 s at 44.1 kHz
        Assert.Equal(67174, src.SelectedTrack.MediaTicks); // 65 packets of 1024 and a last one of 614
    }

    [Fact]
    public void ITunSmpbWinsOverTheEditList()
    {
        using var src = Mp4PacketSource.Open(new MemoryStream(Bytes("aac-itunes-gapless.m4a")));
        Assert.Equal(GaplessSource.ITunSmpb, src.SelectedTrack.Gapless);
        Assert.Equal(2112, src.PrimingFrames);
        Assert.Equal(65000, src.LengthFrames);
        // The edit list is still there and says something else.
        Assert.Equal(1024, src.SelectedTrack.EditListMediaTime);
        Assert.Equal("Gapless test", src.Title);
        Assert.Equal("Test Artist", src.Artist);
        Assert.Contains("TITLE=Gapless test", src.Comments);
        Assert.Equal(TimeSpan.FromSeconds(65000 / 44100.0), src.Duration);
    }

    [Fact]
    public void EndFrameIsClampedToTheTrimmedLength()
    {
        using var src = Mp4PacketSource.Open(new MemoryStream(Bytes("aac-moov-last.m4a")));
        var ends = new List<long>();
        while (src.TryReadPacket(out var p)) ends.Add(p.EndFrame);
        Assert.Equal(0, ends[0]); // the first packet is entirely priming
        Assert.Equal(src.LengthFrames, ends[^1]);
        Assert.Equal(ends.Order().ToList(), ends);
    }

    [Theory]
    [InlineData("00000000 00000840 000001D8 000000000000FDE8 00000000", true, 2112, 65000)]
    [InlineData(" 00000000 00000000 00000000 0000000000000001", true, 0, 1)]
    [InlineData("0 1 2", false, 0, 0)]
    [InlineData("00000000 zzzz 00000000 00000001", false, 0, 0)]
    [InlineData("", false, 0, 0)]
    public void ITunSmpbParsing(string text, bool ok, long delay, long valid)
    {
        Assert.Equal(ok, Mp4PacketSource.TryParseSmpb(text, out long d, out long v));
        if (ok) { Assert.Equal(delay, d); Assert.Equal(valid, v); }
    }

    [Fact]
    public void AmrIn3gpReportsNarrowAndWide()
    {
        using var nb = Mp4PacketSource.Open(new MemoryStream(Bytes("amrnb.3gp")));
        Assert.Equal(CodecId.AmrNb, nb.Codec);
        Assert.Equal((8000, 1, 8000), (nb.SampleRate, nb.Channels, nb.TimeScale));
        Assert.Equal(150 * 160, nb.LengthFrames);
        Assert.Equal("3gp4", nb.MajorBrand);
        Assert.False(nb.CodecConfig.IsEmpty); // damr payload
        using var wb = Mp4PacketSource.Open(new MemoryStream(Bytes("amrwb.3gp")));
        Assert.Equal(CodecId.AmrWb, wb.Codec);
        Assert.Equal((16000, 1), (wb.SampleRate, wb.Channels));
        Assert.Equal(150 * 320, wb.LengthFrames);
    }

    [Fact]
    public void FirstSupportedAudioTrackIsDefault()
    {
        using var src = Mp4PacketSource.Open(new MemoryStream(Bytes("alac-then-aac.mp4")));
        Assert.Equal(2, src.Tracks.Count);
        Assert.Null(src.Tracks[0].Codec);
        Assert.Contains("alac", src.Tracks[0].UnsupportedReason, StringComparison.Ordinal);
        Assert.Equal(1, src.SelectedTrack.Index);
        Assert.Equal(CodecId.Aac, src.Codec);
        Assert.Throws<UnsupportedAudioFormatException>(() => src.SelectTrack(0));
        Assert.Throws<ArgumentOutOfRangeException>(() => src.SelectTrack(2));
    }

    [Fact]
    public void FileWithOnlyAnUnsupportedCodecIsRejected()
    {
        var ex = Assert.Throws<UnsupportedAudioFormatException>(() => Mp4PacketSource.Open(new MemoryStream(Bytes("alac-only.m4a"))));
        Assert.Contains("alac", ex.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void FragmentedFilesAreRejectedWithATypedError()
    {
        Assert.Throws<UnsupportedAudioFormatException>(() => Mp4PacketSource.Open(new MemoryStream(Bytes("aac-fragmented.mp4"))));
    }

    [Fact]
    public void MoovFirstAndMoovLastGiveTheSamePackets()
    {
        using var a = Mp4PacketSource.Open(new MemoryStream(Bytes("aac-moov-first.m4a")));
        using var b = Mp4PacketSource.Open(new MemoryStream(Bytes("aac-moov-last.m4a")));
        var pa = ContainerTestEnv.ReadAll(a);
        var pb = ContainerTestEnv.ReadAll(b);
        Assert.Equal(pb.Count, pa.Count);
        for (int i = 0; i < pa.Count; i++) Assert.Equal(pb[i].Data, pa[i].Data);
        // The two layouts really differ.
        Assert.True(IndexOf(Bytes("aac-moov-first.m4a"), "moov"u8) < IndexOf(Bytes("aac-moov-first.m4a"), "mdat"u8));
        Assert.True(IndexOf(Bytes("aac-moov-last.m4a"), "moov"u8) > IndexOf(Bytes("aac-moov-last.m4a"), "mdat"u8));
    }

    private static int IndexOf(byte[] d, ReadOnlySpan<byte> m) => d.AsSpan().IndexOf(m);

    // --- forward-only streams -----------------------------------------------------------------------------------------

    [Fact]
    public void ForwardOnlyMp4WorksWhenMoovComesFirst()
    {
        var data = Bytes("aac-moov-first.m4a");
        using var seekable = Mp4PacketSource.Open(new MemoryStream(data));
        using var fwd = Mp4PacketSource.Open(new ContainerTestEnv.ForwardOnly(data));
        Assert.False(fwd.CanSeek);
        var pa = ContainerTestEnv.ReadAll(seekable);
        var pb = ContainerTestEnv.ReadAll(fwd);
        Assert.Equal(pa.Count, pb.Count);
        for (int i = 0; i < pa.Count; i++) Assert.Equal(pa[i].Data, pb[i].Data);
        Assert.Throws<NotSupportedException>(() => fwd.SeekToFrame(0, out _));
    }

    [Fact]
    public void ForwardOnlyMp4WithMoovLastIsRejected()
    {
        Assert.Throws<UnsupportedAudioFormatException>(() => Mp4PacketSource.Open(new ContainerTestEnv.ForwardOnly(Bytes("aac-moov-last.m4a"))));
    }

    [Fact]
    public void ForwardOnlyAdtsAndAmrMatchSeekableReads()
    {
        var adts = Bytes("aac.aac");
        using (var a = AdtsPacketSource.Open(new MemoryStream(adts)))
        using (var b = AdtsPacketSource.Open(new ContainerTestEnv.ForwardOnly(adts)))
        {
            Assert.Equal(-1, b.LengthFrames);
            Assert.False(b.CanSeek);
            Assert.Equal(a.CodecConfig.ToArray(), b.CodecConfig.ToArray());
            var pa = ContainerTestEnv.ReadAll(a);
            var pb = ContainerTestEnv.ReadAll(b);
            Assert.Equal(pa.Count, pb.Count);
            for (int i = 0; i < pa.Count; i++) Assert.Equal(pa[i].Data, pb[i].Data);
            Assert.Throws<NotSupportedException>(() => b.SeekToFrame(0, out _));
        }
        foreach (var name in new[] { "synthetic-nb.amr", "synthetic-wb.awb" })
        {
            var amr = Bytes(name);
            using var a = AmrPacketSource.Open(new MemoryStream(amr));
            using var b = AmrPacketSource.Open(new ContainerTestEnv.ForwardOnly(amr));
            Assert.Equal(-1, b.LengthFrames);
            var pa = ContainerTestEnv.ReadAll(a);
            var pb = ContainerTestEnv.ReadAll(b);
            Assert.Equal(pa.Count, pb.Count);
            for (int i = 0; i < pa.Count; i++) Assert.Equal(pa[i].Data, pb[i].Data);
        }
    }

    // --- ADTS details -------------------------------------------------------------------------------------------------

    [Fact]
    public void AdtsHeaderFieldsBuildTheConfig()
    {
        using var src = AdtsPacketSource.Open(new MemoryStream(Bytes("aac.aac")));
        Assert.Equal((44100, 2, 2), (src.SampleRate, src.Channels, src.AudioObjectType));
        Assert.Equal(new byte[] { 0x12, 0x10 }, src.CodecConfig.ToArray());
        Assert.True(AacConfigInfo.TryParse(src.CodecConfig.Span, out var info));
        Assert.Equal(new AacConfigInfo(2, 44100, 2, 0), info);
    }

    [Fact]
    public void AdtsSurvivesAnId3TagAndTrailingTag()
    {
        var plain = Bytes("aac.aac");
        var tagged = new List<byte> { (byte)'I', (byte)'D', (byte)'3', 3, 0, 0, 0, 0, 0x01, 0x00 }; // 128-byte tag body
        tagged.AddRange(new byte[128]);
        tagged.AddRange(plain);
        tagged.Add((byte)'T'); tagged.Add((byte)'A'); tagged.Add((byte)'G');
        tagged.AddRange(new byte[125]);
        Assert.True(AdtsFormat.Instance.Matches(tagged.ToArray().AsSpan(0, 512)));
        using var a = AdtsPacketSource.Open(new MemoryStream(plain));
        using var b = AdtsPacketSource.Open(new MemoryStream(tagged.ToArray()));
        Assert.Equal(a.LengthFrames, b.LengthFrames);
        Assert.Equal(ContainerTestEnv.ReadAll(a).Count, ContainerTestEnv.ReadAll(b).Count);
    }

    [Fact]
    public void AdtsCutOffLastFrameIsDropped()
    {
        var d = Bytes("aac.aac");
        using var full = AdtsPacketSource.Open(new MemoryStream(d));
        using var cut = AdtsPacketSource.Open(new MemoryStream(d, 0, d.Length - 5));
        Assert.Equal(full.FrameCount - 1, cut.FrameCount);
        Assert.Equal(cut.FrameCount, ContainerTestEnv.ReadAll(cut).Count);
    }

    [Fact]
    public void AdtsRejectsUnsupportedLayouts()
    {
        var d = Bytes("aac.aac");
        var zeroChannels = (byte[])d.Clone();
        zeroChannels[2] &= 0xFE; zeroChannels[3] &= 0x3F; // channel_configuration 0
        Assert.Throws<UnsupportedAudioFormatException>(() => AdtsPacketSource.Open(new MemoryStream(zeroChannels)));
        var blocks = (byte[])d.Clone();
        blocks[6] |= 1; // two raw data blocks in the first frame
        Assert.Throws<UnsupportedAudioFormatException>(() => AdtsPacketSource.Open(new MemoryStream(blocks)));
    }

    [Fact]
    public void AdtsParameterChangeEndsTheStream()
    {
        var d = Bytes("aac.aac");
        using var plain = AdtsPacketSource.Open(new MemoryStream(d));
        var offsets = new List<int>();
        for (int i = 0; i + 7 < d.Length;)
        {
            offsets.Add(i);
            i += ((d[i + 3] & 3) << 11) | (d[i + 4] << 3) | (d[i + 5] >> 5);
        }
        var changed = (byte[])d.Clone();
        changed[offsets[10] + 2] = (byte)((changed[offsets[10] + 2] & ~0x3C) | (3 << 2)); // 48 kHz from frame 10
        using var src = AdtsPacketSource.Open(new MemoryStream(changed));
        Assert.Equal(10, src.FrameCount);
        Assert.Equal(10, ContainerTestEnv.ReadAll(src).Count);
    }

    // --- AMR details --------------------------------------------------------------------------------------------------

    [Fact]
    public void AmrFrameTableMatchesTheStandard()
    {
        int[] nb = [12, 13, 15, 17, 19, 20, 26, 31, 5];
        for (int i = 0; i < nb.Length; i++) Assert.Equal(nb[i], AmrFrameTable.PayloadBytes(false, i));
        int[] wb = [17, 23, 32, 36, 40, 46, 50, 58, 60, 5];
        for (int i = 0; i < wb.Length; i++) Assert.Equal(wb[i], AmrFrameTable.PayloadBytes(true, i));
        Assert.Equal(0, AmrFrameTable.PayloadBytes(false, 15));
        Assert.Equal(0, AmrFrameTable.PayloadBytes(true, 15));
        Assert.Equal(0, AmrFrameTable.PayloadBytes(true, 14));
        Assert.Equal(-1, AmrFrameTable.PayloadBytes(false, 14));
        Assert.Equal(-1, AmrFrameTable.PayloadBytes(true, 11));
        Assert.Equal(-1, AmrFrameTable.PayloadBytes(false, 16));
        Assert.Equal(160, AmrFrameTable.SamplesPerFrame(false));
        Assert.Equal(320, AmrFrameTable.SamplesPerFrame(true));
    }

    [Fact]
    public void AmrNoDataFramesAreOneBytePackets()
    {
        byte[] file = [.. "#!AMR\n"u8, 0x78, /* NO_DATA */ 0x04, .. new byte[12], /* 4.75k */ 0x78];
        using var src = AmrPacketSource.Open(new MemoryStream(file));
        var packets = ContainerTestEnv.ReadAll(src);
        Assert.Equal([1, 13, 1], packets.Select(p => p.Data.Length));
        Assert.Equal(3 * 160, src.LengthFrames);
    }

    [Fact]
    public void AmrMultiChannelHeaders()
    {
        byte[] two = [.. "#!AMR_MC1.0\n"u8, 0, 0, 0, 2, 0x78];
        Assert.Throws<UnsupportedAudioFormatException>(() => AmrPacketSource.Open(new MemoryStream(two)));
        byte[] one = [.. "#!AMR-WB_MC1.0\n"u8, 0, 0, 0, 1, 0x78, 0x78];
        using var src = AmrPacketSource.Open(new MemoryStream(one));
        Assert.True(src.IsWideband);
        Assert.Equal(2, ContainerTestEnv.ReadAll(src).Count);
        Assert.True(AmrFormat.Instance.Matches(two));
    }

    [Fact]
    public void AmrReservedFrameTypeIsAnErrorUnlessTrailing()
    {
        // 1500 frames of 4.75 kbit/s (13 bytes each) is well past the 8192 bytes that count as a trailer.
        var file = new List<byte>("#!AMR\n"u8.ToArray());
        for (int i = 0; i < 1500; i++) { file.Add(0x04); file.AddRange(new byte[12]); }
        var bad = file.ToArray();
        bad[6 + 13 * 400] = 12 << 3; // a reserved frame type in the middle
        Assert.Throws<AudioFormatException>(() => AmrPacketSource.Open(new MemoryStream(bad)));
        var late = file.ToArray();
        late[6 + 13 * 1495] = 12 << 3; // 5 frames from the end: trailing bytes
        using (var src = AmrPacketSource.Open(new MemoryStream(late))) Assert.Equal(1495, src.FrameCount);
        file.Add(12 << 3);
        using (var src = AmrPacketSource.Open(new MemoryStream(file.ToArray()))) Assert.Equal(1500, src.FrameCount);
    }

    // --- registry and detection ---------------------------------------------------------------------------------------

    [Fact]
    public void ContainersAreSniffedAndTheMissingCodecIsReported()
    {
        // AddAac registers the AAC-LC decoder, so these open; without it the codec is named.
        foreach (var (name, codec) in new[] { ("aac-moov-last.m4a", "aac"), ("aac.aac", "aac") })
        {
            using var fs = File.OpenRead(ContainerTestEnv.TestData(name));
            using (AudioDecoder.Open(fs, ContainerTestEnv.Registry, leaveOpen: true)) { }
            fs.Position = 0;
            var noAac = new AudioCodecRegistry().AddMp4().AddAdts();
            var ex = Assert.Throws<UnsupportedAudioFormatException>(() => AudioDecoder.Open(fs, noAac, leaveOpen: true));
            Assert.Contains(codec, ex.Message, StringComparison.Ordinal);
            Assert.True(fs.CanRead); // the stream is left to the caller when opening fails
        }

        // AddAmr registers the AMR decoders, so these open; without them the codec is named.
        foreach (var (name, codec) in new[] { ("amrnb.3gp", "amr-nb"), ("synthetic-wb.awb", "amr-wb"), ("synthetic-nb.amr", "amr-nb") })
        {
            using var fs = File.OpenRead(ContainerTestEnv.TestData(name));
            using (AudioDecoder.Open(fs, ContainerTestEnv.Registry, leaveOpen: true)) { }
            fs.Position = 0;
            var bare = new AudioCodecRegistry().Add(Mp4Format.Instance).Add(AmrFormat.Instance);
            var ex = Assert.Throws<UnsupportedAudioFormatException>(() => AudioDecoder.Open(fs, bare, leaveOpen: true));
            Assert.Contains(codec, ex.Message, StringComparison.Ordinal);
        }
    }

    [Fact]
    public void ExtensionsFindTheirContainers()
    {
        var r = ContainerTestEnv.Registry;
        Assert.Same(Mp4Format.Instance, r.DetectByExtension(".M4A"));
        Assert.Same(Mp4Format.Instance, r.DetectByExtension("3gp"));
        Assert.Same(AdtsFormat.Instance, r.DetectByExtension(".aac"));
        Assert.Same(AmrFormat.Instance, r.DetectByExtension(".awb"));
        Assert.Null(r.DetectByExtension(".txt"));
        Assert.Null(r.DetectByExtension(null));
        Assert.Null(r.DetectByExtension(""));
    }

    [Fact]
    public void OpenFileFallsBackToTheExtension()
    {
        // An ADTS stream with 20 bytes of junk in front does not sniff, but its extension names the container.
        var data = new byte[20].Concat(Bytes("aac.aac")).ToArray();
        string dir = Environment.GetEnvironmentVariable("TMPDIR") is { Length: > 0 } t ? t : Path.GetTempPath();
        string path = Path.Combine(dir, "oneaudio-junk-" + Guid.NewGuid().ToString("N") + ".aac");
        File.WriteAllBytes(path, data);
        try
        {
            Assert.Null(ContainerTestEnv.Registry.Detect(data.AsSpan(0, 512)));
            // The format is found; the first frame is not at the start, so the reader itself rejects it with a typed error.
            Assert.Throws<AudioFormatException>(() => AudioDecoder.OpenFile(path, ContainerTestEnv.Registry));
        }
        finally { File.Delete(path); }
    }

    [Fact]
    public void Mp3FilesAreNotTakenForAdts()
    {
        foreach (var f in Directory.GetFiles(Path.Combine(AppContext.BaseDirectory, "testdata", "mp3"), "*.mp3"))
        {
            var head = new byte[512];
            using var fs = File.OpenRead(f);
            int n = fs.ReadAtLeast(head, head.Length, throwOnEndOfStream: false);
            Assert.False(AdtsFormat.Instance.Matches(head.AsSpan(0, n)), f);
        }
    }

    // --- hostile input ------------------------------------------------------------------------------------------------

    private static void ExpectTypedOrSuccess(byte[] data, Func<Stream, IPacketSource> open)
    {
        try
        {
            using var src = open(new MemoryStream(data));
            for (int i = 0; i < 100_000 && src.TryReadPacket(out _); i++) { }
            if (src.CanSeek) { src.SeekToFrame(Math.Max(src.LengthFrames / 2, 0), out _); src.TryReadPacket(out _); }
        }
        catch (OneAudioException) { }
    }

    public static TheoryData<string> AllFiles => new()
    {
        "aac-moov-last.m4a", "aac-moov-first.m4a", "aac-itunes-gapless.m4a", "aac-two-tracks.mp4", "alac-then-aac.mp4", "aac-fragmented.mp4",
        "amrnb.3gp", "amrwb.3gp", "aac.aac", "synthetic-nb.amr", "synthetic-wb.awb",
    };

    private static IPacketSource OpenAny(Stream s, string name) =>
        name.EndsWith(".aac", StringComparison.Ordinal) ? AdtsPacketSource.Open(s)
        : name.EndsWith(".amr", StringComparison.Ordinal) || name.EndsWith(".awb", StringComparison.Ordinal) ? AmrPacketSource.Open(s)
        : Mp4PacketSource.Open(s);

    [Theory]
    [MemberData(nameof(AllFiles))]
    public void TruncationsGiveTypedErrorsOrValidStreams(string name)
    {
        var d = Bytes(name);
        int step = Math.Max(1, d.Length / 400);
        for (int len = 0; len < d.Length; len += step)
            ExpectTypedOrSuccess(d[..len], s => OpenAny(s, name));
        // The last bytes one at a time.
        for (int len = Math.Max(0, d.Length - 40); len < d.Length; len++)
            ExpectTypedOrSuccess(d[..len], s => OpenAny(s, name));
    }

    [Theory]
    [MemberData(nameof(AllFiles))]
    public void CorruptedBytesGiveTypedErrorsOrValidStreams(string name)
    {
        var d = Bytes(name);
        var rng = new Random(7);
        int limit = Math.Min(d.Length, name.Contains("alac", StringComparison.Ordinal) ? 2000 : d.Length);
        for (int n = 0; n < 400; n++)
        {
            var c = (byte[])d.Clone();
            int flips = 1 + rng.Next(4);
            for (int k = 0; k < flips; k++) c[rng.Next(name.EndsWith(".m4a", StringComparison.Ordinal) || name.EndsWith(".mp4", StringComparison.Ordinal) || name.EndsWith(".3gp", StringComparison.Ordinal) ? Math.Min(limit, 1400) : Math.Min(limit, 600))] = (byte)rng.Next(256);
            ExpectTypedOrSuccess(c, s => OpenAny(s, name));
        }
    }

    [Fact]
    public void GarbageIsRejectedWithTypedErrors()
    {
        var rng = new Random(3);
        for (int n = 0; n < 200; n++)
        {
            var junk = new byte[rng.Next(1, 3000)];
            rng.NextBytes(junk);
            ExpectTypedOrSuccess(junk, s => Mp4PacketSource.Open(s));
            ExpectTypedOrSuccess(junk, s => AdtsPacketSource.Open(s));
            ExpectTypedOrSuccess(junk, s => AmrPacketSource.Open(s));
        }
        Assert.Throws<AudioFormatException>(() => Mp4PacketSource.Open(new MemoryStream([])));
        Assert.Throws<AudioFormatException>(() => AdtsPacketSource.Open(new MemoryStream([])));
        Assert.Throws<AudioFormatException>(() => AmrPacketSource.Open(new MemoryStream([])));
    }

    private static byte[] Box(string type, params byte[][] parts)
    {
        int len = 8 + parts.Sum(p => p.Length);
        var b = new List<byte>();
        b.AddRange(BitConverter.GetBytes(BinaryPrimitives.ReverseEndianness(len)));
        b.AddRange(System.Text.Encoding.ASCII.GetBytes(type));
        foreach (var p in parts) b.AddRange(p);
        return b.ToArray();
    }

    private static byte[] U32(params uint[] v) => v.SelectMany(x => BitConverter.GetBytes(BinaryPrimitives.ReverseEndianness(x))).ToArray();

    [Fact]
    public void OversizeBoxesAreRejectedWithoutAllocating()
    {
        // 64-bit box size far beyond the file.
        byte[] huge = [0, 0, 0, 1, (byte)'m', (byte)'o', (byte)'o', (byte)'v', 0x7F, 0xFF, 0xFF, 0xFF, 0xFF, 0xFF, 0xFF, 0xF0];
        long before = GC.GetAllocatedBytesForCurrentThread();
        Assert.Throws<AudioFormatException>(() => Mp4PacketSource.Open(new MemoryStream([.. Box("ftyp", "M4A "u8.ToArray(), U32(0)), .. huge])));
        // Sizes below the header size and a size that is not a 64-bit size.
        Assert.Throws<AudioFormatException>(() => Mp4PacketSource.Open(new MemoryStream([.. Box("ftyp", "M4A "u8.ToArray(), U32(0)), 0, 0, 0, 4, (byte)'m', (byte)'o', (byte)'o', (byte)'v'])));
        Assert.Throws<AudioFormatException>(() => Mp4PacketSource.Open(new MemoryStream([.. Box("ftyp", "M4A "u8.ToArray(), U32(0)), 0, 0, 0, 1, (byte)'m', (byte)'o', (byte)'o', (byte)'v', 0, 0, 0, 0, 0, 0, 0, 8])));
        Assert.True(GC.GetAllocatedBytesForCurrentThread() - before < 256 * 1024);
    }

    [Fact]
    public void HugeTableCountsInTinyBoxesDoNotAllocate()
    {
        byte[] d = File.ReadAllBytes(ContainerTestEnv.TestData("aac-moov-last.m4a"));
        foreach (var table in new[] { "stsz", "stts", "stsc", "stco" })
        {
            int at = d.AsSpan().IndexOf(System.Text.Encoding.ASCII.GetBytes(table));
            Assert.True(at > 0);
            var c = (byte[])d.Clone();
            // The entry count of stts/stsc/stco is the second 32-bit field after the type; stsz's is the third.
            int countAt = at + 4 + (table == "stsz" ? 8 : 4);
            BinaryPrimitives.WriteUInt32BigEndian(c.AsSpan(countAt), 0x7FFFFFF0);
            long before = GC.GetAllocatedBytesForCurrentThread();
            Assert.ThrowsAny<OneAudioException>(() => Mp4PacketSource.Open(new MemoryStream(c)));
            Assert.True(GC.GetAllocatedBytesForCurrentThread() - before < 512 * 1024, table);
        }
    }

    [Fact]
    public void TableLimitsAreEnforced()
    {
        var tight = new AudioLimits { MaxSamples = 10 };
        var ex = Assert.Throws<AudioLimitExceededException>(() => Mp4PacketSource.Open(new MemoryStream(Bytes("aac-moov-last.m4a")), tight));
        Assert.Equal("MaxSamples", ex.LimitName);
        Assert.Throws<AudioLimitExceededException>(() => Mp4PacketSource.Open(new MemoryStream(Bytes("aac-moov-last.m4a")), new AudioLimits { MaxPacketBytes = 100 }));
        Assert.Throws<AudioLimitExceededException>(() => AdtsPacketSource.Open(new MemoryStream(Bytes("aac.aac")), new AudioLimits { MaxSamples = 10 }));
        Assert.Throws<AudioLimitExceededException>(() => AmrPacketSource.Open(new MemoryStream(Bytes("synthetic-nb.amr")), new AudioLimits { MaxSamples = 10 }));
        Assert.Throws<AudioLimitExceededException>(() => Mp4PacketSource.Open(new MemoryStream(Bytes("aac-moov-last.m4a")), new AudioLimits { MaxContainerElements = 5 }));
    }

    [Fact]
    public void CancellationIsObserved()
    {
        using var cts = new CancellationTokenSource();
        cts.Cancel();
        Assert.Throws<OperationCanceledException>(() => Mp4PacketSource.Open(new MemoryStream(Bytes("aac-moov-last.m4a")), null, false, cts.Token));
        Assert.Throws<OperationCanceledException>(() => AdtsPacketSource.Open(new MemoryStream(Bytes("aac.aac")), null, false, cts.Token));
        Assert.Throws<OperationCanceledException>(() => AmrPacketSource.Open(new MemoryStream(Bytes("synthetic-nb.amr")), null, false, cts.Token));
    }

    [Fact]
    public void FailedOpenLeavesTheStreamToTheCaller()
    {
        var ms = new MemoryStream([1, 2, 3, 4, 5, 6, 7, 8, 9, 10, 11, 12]);
        Assert.ThrowsAny<OneAudioException>(() => Mp4PacketSource.Open(ms));
        Assert.True(ms.CanRead);
        Assert.ThrowsAny<OneAudioException>(() => AdtsPacketSource.Open(ms));
        Assert.True(ms.CanRead);
        Assert.ThrowsAny<OneAudioException>(() => AmrPacketSource.Open(ms));
        Assert.True(ms.CanRead);
    }

    [Fact]
    public void AacConfigInfoParsing()
    {
        Assert.True(AacConfigInfo.TryParse([0x12, 0x10], out var lc));
        Assert.Equal(new AacConfigInfo(2, 44100, 2, 0), lc);
        // HE-AAC, explicit hierarchical signalling: object type 5, 24 kHz core, stereo, 48 kHz extension, then AAC-LC (2).
        Assert.True(AacConfigInfo.TryParse([0x2B, 0x11, 0x88], out var he));
        Assert.Equal(new AacConfigInfo(5, 24000, 2, 48000), he);
        Assert.False(AacConfigInfo.TryParse([0x12], out _));
        Assert.False(AacConfigInfo.TryParse([], out _));
        Assert.False(AacConfigInfo.TryParse([0x17, 0xF0], out _)); // sampling index 14 is reserved
    }
}
