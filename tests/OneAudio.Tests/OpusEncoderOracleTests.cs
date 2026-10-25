using OneAudio.Opus;
using Xunit;

namespace OneAudio.Tests;

/// <summary>
/// Compares our encoder with the RFC 6716 reference encoder (opus_demo -e) on generated signals. Needs OPUS_DEMO or
/// OPUS_COMPARE (the demo sits beside it) and is skipped otherwise.
/// </summary>
public class OpusEncoderOracleTests
{
    public static TheoryData<int, int, int, string, int> Cases()
    {
        var d = new TheoryData<int, int, int, string, int>();
        foreach (int ch in new[] { 1, 2 })
            foreach (int rate in new[] { 64000, 128000 })
                foreach (int ms10 in new[] { 25, 50, 100, 200, 400, 600 })
                    d.Add(ch, rate, ms10, "audio", 10);
        return d;
    }

    [Theory]
    [MemberData(nameof(Cases))]
    public void MatchesReferenceBytes(int channels, int bitrate, int ms10, string app, int complexity)
    {
        var demo = OpusEncoderSupport.DemoPath();
        if (demo is null) return;
        int frameSize = ms10 * 48 / 10;
        var pcm = OpusEncoderSupport.MakeSignal(channels, 48000 * 2 / frameSize * frameSize, 3);
        var work = Path.Combine(Path.GetTempPath(), "oneaudio-enc-" + Guid.NewGuid().ToString("N"));
        try
        {
            string fs = ms10 == 25 ? "2.5" : (ms10 / 10).ToString();
            var refPk = OpusEncoderSupport.RunReference(demo, work, pcm, app, channels, bitrate, "-framesize", fs, "-complexity", complexity.ToString());
            var enc = new OpusEncoder(new OpusEncoderSettings { Channels = channels, BitrateBps = bitrate, Complexity = complexity });
            var ours = OpusEncoderSupport.Encode(enc, pcm, frameSize);
            Assert.True(refPk.Count >= ours.Count);
            for (int i = 0; i < ours.Count; i++)
            {
                if (!refPk[i].Data.AsSpan().SequenceEqual(ours[i].Data))
                    Assert.Fail($"head ref {Convert.ToHexString(refPk[i].Data.AsSpan(0, 6))} ours {Convert.ToHexString(ours[i].Data.AsSpan(0, 6))} packet {i}: ref {refPk[i].Data.Length} bytes, ours {ours[i].Data.Length} bytes; first diff at {FirstDiff(refPk[i].Data, ours[i].Data)}");
                Assert.Equal(refPk[i].Range, ours[i].Range);
            }
        }
        finally
        {
            if (Directory.Exists(work)) Directory.Delete(work, true);
        }
    }

    private static int FirstDiff(byte[] a, byte[] b)
    {
        int n = Math.Min(a.Length, b.Length);
        for (int i = 0; i < n; i++) if (a[i] != b[i]) return i;
        return n;
    }
}
