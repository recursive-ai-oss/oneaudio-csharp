using Xunit;
using OneAudio.Vorbis;

namespace OneAudio.Tests;

/// <summary>
/// Decodes the Xiph Vorbis test vectors and compares frame counts and samples with libvorbis (ov_read_float).
/// Measured on the 29 vectors against libvorbisfile 1.3.7: files whose setups use floor 1 only agree to 4.8e-7 at
/// worst (SNR above 135 dB); files that use floor 0 agree to 5.1e-5 (SNR above 89 dB), because libvorbis evaluates the
/// floor 0 curve with table-based approximations while this decoder evaluates the specification's formula directly.
/// The bounds below leave a wide margin over those figures.
/// </summary>
public class VorbisCorpusTests
{
    public static TheoryData<string> Files => VorbisTestEnv.NameData();

    [Theory]
    [MemberData(nameof(Files))]
    public void MatchesLibvorbis(string name)
    {
        VorbisTestEnv.RequireReference(name);
        string path = VorbisTestEnv.OggPath(name);
        var refLinks = VorbisTestEnv.ReferenceLinks(name);
        bool floor0 = VorbisTestEnv.UsesFloor0(path);
        double maxBound = floor0 ? 1e-4 : 2e-6;
        double snrBound = floor0 ? 85 : 125;

        using var refStream = File.OpenRead(Path.Combine(VorbisTestEnv.RefsDir!, name + ".f32"));
        using var dec = (VorbisDecoder)AudioDecoder.OpenFile(path, VorbisTestEnv.Registry);
        Assert.Equal(refLinks.Count, dec.LinkCount);

        double maxErr = 0, sumRef = 0, sumErr = 0;
        int link = 0;
        while (link < dec.LinkCount)
        {
            if (link > 0) dec.SelectRun(link);
            var fmt = dec.Format;
            Assert.Equal(refLinks[link].Rate, fmt.SampleRate);
            Assert.Equal(refLinks[link].Channels, fmt.Channels);
            // Consecutive links of one format form a run; their reference frames add up.
            long expectFrames = 0;
            int end = link;
            while (true)
            {
                expectFrames += refLinks[end].Frames;
                if (end + 1 < dec.LinkCount && dec.GetLinkFormat(end + 1) == fmt) end++; else break;
            }
            int ch = fmt.Channels;
            var pcm = new float[4096 * ch];
            var refBytes = new byte[pcm.Length * 4];
            var refF = new float[pcm.Length];
            long frames = 0;
            int n;
            while ((n = dec.Read(pcm)) > 0)
            {
                refStream.ReadExactly(refBytes.AsSpan(0, n * ch * 4));
                Buffer.BlockCopy(refBytes, 0, refF, 0, n * ch * 4);
                for (int i = 0; i < n * ch; i++)
                {
                    double e = pcm[i] - refF[i];
                    if (Math.Abs(e) > maxErr) maxErr = Math.Abs(e);
                    sumErr += e * e;
                    sumRef += (double)refF[i] * refF[i];
                }
                frames += n;
            }
            Assert.Equal(expectFrames, frames);
            link = end + 1;
        }
        Assert.Equal(refStream.Length, refStream.Position);
        double snr = 10 * Math.Log10(sumRef / Math.Max(sumErr, 1e-30));
        Assert.True(maxErr <= maxBound, $"{name}: max abs error {maxErr:g4} exceeds {maxBound:g}");
        Assert.True(snr >= snrBound, $"{name}: SNR {snr:f1} dB is below {snrBound} dB");
    }

    [Fact]
    public void ChainedFileReportsItsLinks()
    {
        VorbisTestEnv.RequireCorpus("chain-test3");
        using var dec = (VorbisDecoder)AudioDecoder.OpenFile(VorbisTestEnv.OggPath("chain-test3"), VorbisTestEnv.Registry);
        Assert.Equal(2, dec.LinkCount);
        Assert.Equal(44100, dec.GetLinkFormat(0)!.Value.SampleRate);
        Assert.Equal(48000, dec.GetLinkFormat(1)!.Value.SampleRate);
        // The formats differ, so the first run is link 0 alone and reading ends at its end.
        long total = 0;
        var buf = new float[8192];
        int n;
        while ((n = dec.Read(buf)) > 0) total += n;
        Assert.Equal(50496, total);
        dec.SelectRun(1);
        Assert.Equal(1, dec.Format.Channels);
        Assert.Equal(0, dec.PositionFrames);
    }
}
