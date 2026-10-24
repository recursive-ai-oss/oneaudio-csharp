using Xunit;

namespace OneAudio.Tests;

/// <summary>Seeking must land on exactly the samples a linear decode produces.</summary>
public class VorbisSeekTests
{
    private static void CheckSeeks(Func<IAudioDecoder> open, int seed, int trials, Action<IAudioDecoder>? selectRun = null, long maxFrame = long.MaxValue)
    {
        using var linear = open();
        selectRun?.Invoke(linear);
        var all = VorbisTestEnv.ReadAll(linear);
        int ch = linear.Format.Channels;
        long length = Math.Min(all.Length / ch, maxFrame);
        long decodedFrames = all.Length / ch;

        using var d = open();
        selectRun?.Invoke(d);
        var rng = new Random(seed);
        var frames = new List<long> { 0, 1, length - 1, length / 2, length };
        for (int i = 0; i < trials; i++) frames.Add(rng.NextInt64(length + 1));
        var buf = new float[3000 * ch];
        foreach (long f in frames)
        {
            d.Seek(f);
            Assert.Equal(f, d.PositionFrames);
            int got = d.Read(buf);
            int want = (int)Math.Min(3000, decodedFrames - f);
            // A read may be short, so keep reading until the block is full or the stream ends.
            int total = got;
            while (total < want)
            {
                int n = d.Read(buf.AsSpan(total * ch));
                if (n == 0) break;
                total += n;
            }
            Assert.Equal(want, total);
            Assert.True(buf.AsSpan(0, want * ch).SequenceEqual(all.AsSpan((int)f * ch, want * ch)), $"samples differ after seeking to frame {f} of {length}");
        }
    }

    // unused-mode-test loses packets (it uses a mode number that does not exist), and the granule positions that follow
    // jump ahead of the audio actually decoded. Positions after the first gap cannot match a linear decode, so only the
    // part before the gap is checked.
    [Fact]
    public void SeekBeforeTheFirstGapInUnusedModeTest()
    {
        VorbisTestEnv.RequireCorpus("unused-mode-test");
        CheckSeeks(() => AudioDecoder.OpenFile(VorbisTestEnv.OggPath("unused-mode-test"), VorbisTestEnv.Registry), seed: 4, trials: 60, maxFrame: 390_000);
    }

    [Theory]
    [InlineData("rc3-test")]
    [InlineData("48k-mono")]
    [InlineData("singlemap-test")]
    [InlineData("lsp-test")]
    [InlineData("test-short")]
    [InlineData("test-short2")]
    [InlineData("one-entry-codebook-test")]
    public void SeekEqualsLinearDecode(string name)
    {
        VorbisTestEnv.RequireCorpus(name);
        CheckSeeks(() => AudioDecoder.OpenFile(VorbisTestEnv.OggPath(name), VorbisTestEnv.Registry), seed: name.Length, trials: 60);
    }

    [Theory]
    [InlineData("chain-test1", 0)]
    [InlineData("chain-test1", 1)]
    [InlineData("chain-test3", 0)]
    [InlineData("chain-test3", 1)]
    public void SeekWithinChainedRuns(string name, int run)
    {
        VorbisTestEnv.RequireCorpus(name);
        CheckSeeks(() => AudioDecoder.OpenFile(VorbisTestEnv.OggPath(name), VorbisTestEnv.Registry), seed: 5 + run, trials: 40,
            selectRun: d => ((OneAudio.Vorbis.VorbisDecoder)d).SelectRun(run));
    }

    [Theory]
    [InlineData(1, 1, true, 0)]
    [InlineData(2, 1, true, 0)]
    [InlineData(2, 0, true, 700)]
    [InlineData(2, 2, true, 1)]
    [InlineData(6, 1, true, 0)]
    [InlineData(3, 2, false, 0)]
    public void SyntheticStreamsSeekExactly(int channels, int residueType, bool coupling, int trim)
    {
        var builder = new VorbisStreamBuilder { Channels = channels, ResidueType = residueType, Coupling = coupling, Seed = channels * 10 + residueType };
        var file = builder.BuildFile(count: 400, trimEnd: trim, packetsPerPage: 5);
        CheckSeeks(() => AudioDecoder.Open(new MemoryStream(file), VorbisTestEnv.Registry), seed: 11, trials: 80);
    }

    [Fact]
    public void SeekToEndThenBackAndForth()
    {
        var file = new VorbisStreamBuilder().BuildFile(200);
        using var d = AudioDecoder.Open(new MemoryStream(file), VorbisTestEnv.Registry);
        var buf = new float[2048];
        d.Seek(d.LengthFrames);
        Assert.Equal(0, d.Read(buf));
        d.Seek(0);
        Assert.True(d.Read(buf) > 0);
        Assert.Throws<ArgumentOutOfRangeException>(() => d.Seek(-1));
        Assert.Throws<ArgumentOutOfRangeException>(() => d.Seek(d.LengthFrames + 1));
    }
}
