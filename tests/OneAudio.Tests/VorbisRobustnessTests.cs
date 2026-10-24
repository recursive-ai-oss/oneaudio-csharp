using Xunit;

namespace OneAudio.Tests;

/// <summary>Limits, malformed headers and allocation behaviour.</summary>
public class VorbisRobustnessTests
{
    private static byte[] Ogg(byte[] ident, byte[] comment, byte[] setup, params byte[][] audio)
    {
        var packets = new List<byte[]> { ident, comment, setup };
        packets.AddRange(audio);
        var granules = packets.Select(_ => 0L).ToList();
        return new OggBuilder().AddStream(1, packets, granules, 1 << 20, new HashSet<int> { 2 }).ToArray();
    }

    private static byte[] SetupWithBook(Action<VorbisBitWriter> book)
    {
        var w = new VorbisBitWriter();
        w.Write(5, 8);
        foreach (char c in "vorbis") w.Write(c, 8);
        w.Write(0, 8); // one codebook
        book(w);
        return w.ToArray();
    }

    private static void Open(byte[] file, AudioLimits? limits = null) =>
        AudioDecoder.Open(new MemoryStream(file), VorbisTestEnv.Registry, limits).Dispose();

    [Fact]
    public void HugeOrderedCodebookIsRefusedBeforeAllocation()
    {
        // 16.7 million entries declared in a few bytes: an ordered book needs one length run to claim them all.
        var setup = SetupWithBook(w =>
        {
            w.Write(0x564342, 24); w.Write(1, 16); w.Write((1 << 24) - 1, 24);
            w.Write(1, 1);  // ordered
            w.Write(0, 5);  // length 1
            w.Write((1 << 24) - 1, 24);
        });
        var b = new VorbisStreamBuilder();
        var ex = Assert.Throws<AudioLimitExceededException>(() => Open(Ogg(b.Identification(), VorbisStreamBuilder.Comment(), setup)));
        Assert.Equal("MaxSetupBytes", ex.LimitName);
    }

    [Fact]
    public void HugeLookupTableIsRefusedBeforeAllocation()
    {
        // 4M entries of 65535 dimensions each would be hundreds of gigabytes of vectors.
        var setup = SetupWithBook(w =>
        {
            w.Write(0x564342, 24); w.Write(65535, 16); w.Write(1 << 20, 24);
            w.Write(0, 1); w.Write(1, 1);        // unordered, sparse
            for (int i = 0; i < (1 << 20); i++) w.Write(0, 1);
            w.Write(2, 4);                       // lookup type 2: entries * dimensions values
        });
        var b = new VorbisStreamBuilder();
        Assert.Throws<AudioLimitExceededException>(() => Open(Ogg(b.Identification(), VorbisStreamBuilder.Comment(), setup)));
    }

    [Fact]
    public void SetupBudgetIsConfigurable()
    {
        var b = new VorbisStreamBuilder();
        var file = b.BuildFile(10);
        Open(file);
        Assert.Throws<AudioLimitExceededException>(() => Open(file, new AudioLimits { MaxSetupBytes = 100 }));
    }

    [Fact]
    public void ChannelsAndBlockSizeLimitsApply()
    {
        var file = new VorbisStreamBuilder { Channels = 6 }.BuildFile(10);
        Assert.Throws<AudioLimitExceededException>(() => Open(file, new AudioLimits { MaxChannels = 2 }));
        var big = new VorbisStreamBuilder { LongExp = 13 }.BuildFile(10);
        Assert.Throws<AudioLimitExceededException>(() => Open(big, new AudioLimits { MaxFrameSize = 4096 }));
    }

    [Fact]
    public void MalformedHeadersAreFormatErrors()
    {
        var b = new VorbisStreamBuilder();
        var ident = b.Identification();
        var comment = VorbisStreamBuilder.Comment();
        var setup = b.Setup();

        // Truncated setup, a wrong framing bit, version 1, zero channels, bad block sizes, a missing setup header.
        Assert.Throws<AudioFormatException>(() => Open(Ogg(ident, comment, setup[..(setup.Length / 2)])));
        var noFraming = (byte[])setup.Clone();
        noFraming[^1] = 0;
        Assert.Throws<AudioFormatException>(() => Open(Ogg(ident, comment, noFraming)));
        var v1 = (byte[])ident.Clone();
        v1[7] = 1;
        Assert.Throws<UnsupportedAudioFormatException>(() => Open(Ogg(v1, comment, setup)));
        var noCh = (byte[])ident.Clone();
        noCh[11] = 0;
        Assert.Throws<AudioFormatException>(() => Open(Ogg(noCh, comment, setup)));
        var badBs = (byte[])ident.Clone();
        badBs[28] = 0x8B; // blocksize_0 = 2^11 and blocksize_1 = 2^8: the short block would be the longer one
        Assert.Throws<AudioFormatException>(() => Open(Ogg(badBs, comment, setup)));
        Assert.Throws<AudioFormatException>(() => AudioDecoder.Open(
            new MemoryStream(new OggBuilder().AddStream(1, [ident, comment], [0, 0]).ToArray()), VorbisTestEnv.Registry));
    }

    [Fact]
    public void DamagedAudioPacketsNeverThrowFromRead()
    {
        var b = new VorbisStreamBuilder();
        var file = b.BuildFile(120);
        var rng = new Random(3);
        var buf = new float[4096];
        for (int round = 0; round < 40; round++)
        {
            var f = (byte[])file.Clone();
            for (int k = 0; k < 30; k++) f[rng.Next(f.Length)] = (byte)rng.Next(256);
            try
            {
                using var d = AudioDecoder.Open(new MemoryStream(f), VorbisTestEnv.Registry);
                while (d.Read(buf) > 0) { }
            }
            catch (OneAudioException) { }
        }
    }

    [Fact]
    public void ReadAllocatesNothingAfterWarmUp()
    {
        var file = new VorbisStreamBuilder { Channels = 2 }.BuildFile(900, packetsPerPage: 6);
        using var d = AudioDecoder.Open(new MemoryStream(file), VorbisTestEnv.Registry);
        var buf = new float[1000 * 2];
        for (int i = 0; i < 40; i++) d.Read(buf);
        long before = GC.GetAllocatedBytesForCurrentThread();
        int reads = 0;
        long frames = 0;
        int n;
        while ((n = d.Read(buf)) > 0) { frames += n; reads++; }
        long allocated = GC.GetAllocatedBytesForCurrentThread() - before;
        Assert.True(reads > 100, $"only {reads} reads");
        Assert.Equal(0, allocated);
        Assert.True(frames > 100_000);
    }

    [Fact]
    public void SeekAndReadAllocateNothingAfterWarmUp()
    {
        var file = new VorbisStreamBuilder { Channels = 2 }.BuildFile(900, packetsPerPage: 6);
        using var d = AudioDecoder.Open(new MemoryStream(file), VorbisTestEnv.Registry);
        var buf = new float[1000 * 2];
        var rng = new Random(1);
        for (int i = 0; i < 20; i++) { d.Seek(rng.NextInt64(d.LengthFrames)); d.Read(buf); }
        long before = GC.GetAllocatedBytesForCurrentThread();
        for (int i = 0; i < 200; i++) { d.Seek(rng.NextInt64(d.LengthFrames)); d.Read(buf); }
        Assert.Equal(0, GC.GetAllocatedBytesForCurrentThread() - before);
    }
}
