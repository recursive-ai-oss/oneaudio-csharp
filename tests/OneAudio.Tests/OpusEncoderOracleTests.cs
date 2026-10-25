using OneAudio.Opus;
using Xunit;

namespace OneAudio.Tests;

/// <summary>
/// Compares our encoder with the RFC 6716 reference encoder (opus_demo -e) on generated signals. Needs OPUS_DEMO or
/// OPUS_COMPARE (the demo sits beside it) and is skipped otherwise.
/// </summary>
public class OpusEncoderOracleTests
{
    // channels, bitrate, frame (tenths of ms), application, complexity, rate mode (0 VBR, 1 CVBR, 2 CBR), bandwidth (0 auto, 1..5)
    public static TheoryData<int, int, int, string, int, int, int> Cases()
    {
        var d = new TheoryData<int, int, int, string, int, int, int>();
        foreach (int ch in new[] { 1, 2 })
            foreach (int rate in new[] { 64000, 128000 })
                foreach (int ms10 in new[] { 25, 50, 100, 200, 400, 600 })
                    d.Add(ch, rate, ms10, "audio", 10, 0, 0);
        foreach (int ch in new[] { 1, 2 })
            foreach (int cx in new[] { 0, 1, 2, 3, 4, 5, 6, 7, 8, 9 })
                d.Add(ch, 96000, 200, "audio", cx, 0, 0);
        foreach (int ch in new[] { 1, 2 })
            foreach (int mode in new[] { 1, 2 })
                foreach (int rate in new[] { 48000, 96000, 256000 })
                    foreach (int ms10 in new[] { 100, 200 })
                        d.Add(ch, rate, ms10, "audio", 10, mode, 0);
        foreach (int ch in new[] { 1, 2 })
            foreach (int bw in new[] { 1, 3, 4, 5 })
                d.Add(ch, 64000, 200, "audio", 10, 0, bw);
        d.Add(2, 64000, 200, "restricted-lowdelay", 10, 0, 0);
        d.Add(1, 64000, 100, "restricted-lowdelay", 10, 2, 0);
        d.Add(2, 128000, 200, "voip", 10, 0, 0);
        d.Add(1, 128000, 200, "voip", 10, 1, 0);
        return d;
    }

    private static readonly string[] BandwidthNames = ["", "NB", "MB", "WB", "SWB", "FB"];

    [Theory]
    [MemberData(nameof(Cases))]
    public void MatchesReferenceBytes(int channels, int bitrate, int ms10, string app, int complexity, int rateMode, int bandwidth)
    {
        var demo = OpusEncoderSupport.DemoPath();
        if (demo is null) return;
        int frameSize = ms10 * 48 / 10;
        var pcm = OpusEncoderSupport.MakeSignal(channels, 48000 * 2 / frameSize * frameSize, 3);
        var work = Path.Combine(Path.GetTempPath(), "oneaudio-enc-" + Guid.NewGuid().ToString("N"));
        try
        {
            string fs = ms10 == 25 ? "2.5" : (ms10 / 10).ToString();
            var opts = new List<string> { "-framesize", fs, "-complexity", complexity.ToString() };
            if (rateMode == 1) opts.Add("-cvbr");
            if (rateMode == 2) opts.Add("-cbr");
            if (bandwidth > 0) { opts.Add("-bandwidth"); opts.Add(BandwidthNames[bandwidth]); }
            var refPk = OpusEncoderSupport.RunReference(demo, work, pcm, app, channels, bitrate, opts.ToArray());
            var enc = new OpusEncoder(new OpusEncoderSettings
            {
                Channels = channels, BitrateBps = bitrate, Complexity = complexity,
                BitrateMode = rateMode == 0 ? OpusBitrateMode.Vbr : rateMode == 1 ? OpusBitrateMode.ConstrainedVbr : OpusBitrateMode.Cbr,
                Bandwidth = bandwidth == 0 ? OpusBandwidth.Auto : (OpusBandwidth)(1100 + bandwidth),
                Application = app == "voip" ? OpusApplication.Voip : app == "audio" ? OpusApplication.Audio : OpusApplication.RestrictedLowDelay,
            });
            var ours = OpusEncoderSupport.Encode(enc, pcm, frameSize);
            Assert.True(refPk.Count >= ours.Count);
            for (int i = 0; i < ours.Count; i++)
            {
                if (!refPk[i].Data.AsSpan().SequenceEqual(ours[i].Data))
                    Assert.Fail($"packet {i}: ref {refPk[i].Data.Length} bytes, ours {ours[i].Data.Length} bytes; first diff at {FirstDiff(refPk[i].Data, ours[i].Data)}");
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
