using OneAudio.Opus;
using Xunit;

namespace OneAudio.Tests;

/// <summary>
/// Compares our encoder with the RFC 6716 reference encoder (opus_demo -e) where the encoder changes its mind mid-stream:
/// bitrate sweeps across the SILK/hybrid/CELT thresholds (mode transitions with redundancy, prefill and bandwidth switches),
/// stereo to mono, in-band FEC with packet loss, and DTX. Needs OPUS_DEMO or OPUS_COMPARE; skipped otherwise.
/// </summary>
public class OpusEncoderTransitionTests
{
    // signal (0 music, 1 speech-like, 2 speech-like with digital silence), channels, start bitrate, sweep per frame, sweep max (0 none), frame ms, app, flags (1 forcemono, 2 fec+loss, 4 dtx, 8 cvbr, 16 cbr)
    public static TheoryData<int, int, int, int, int, int, string, int> Cases()
    {
        var d = new TheoryData<int, int, int, int, int, int, string, int>();
        foreach (int sig in new[] { 0, 1 })
            foreach (int ch in new[] { 1, 2 })
                foreach (int ms in new[] { 10, 20 })
                    d.Add(sig, ch, 6000, 400, 70000, ms, "audio", 0);
        d.Add(1, 1, 64000, -250, 0, 20, "audio", 0);
        d.Add(1, 2, 64000, -250, 0, 20, "audio", 0);
        d.Add(1, 2, 12000, 300, 64000, 20, "voip", 0);
        d.Add(1, 1, 12000, 300, 64000, 20, "voip", 0);
        d.Add(0, 2, 30000, 0, 0, 20, "audio", 1);
        d.Add(1, 2, 20000, 0, 0, 20, "audio", 1);
        d.Add(1, 2, 40000, 0, 0, 20, "audio", 1);
        d.Add(1, 1, 16000, 0, 0, 20, "audio", 2);
        d.Add(1, 2, 16000, 0, 0, 20, "audio", 2);
        d.Add(1, 1, 16000, 0, 0, 40, "audio", 2);
        d.Add(1, 1, 16000, 0, 0, 60, "audio", 2);
        d.Add(2, 1, 16000, 0, 0, 20, "voip", 4);
        d.Add(2, 2, 16000, 0, 0, 20, "voip", 4);
        d.Add(2, 1, 40000, 0, 0, 20, "voip", 4);
        d.Add(2, 1, 16000, 0, 0, 10, "voip", 4);
        d.Add(2, 1, 16000, 0, 0, 60, "voip", 4);
        d.Add(2, 1, 24000, 0, 0, 20, "audio", 2 | 4);
        d.Add(1, 1, 16000, 0, 0, 20, "audio", 8);
        d.Add(1, 1, 16000, 0, 0, 20, "audio", 16);
        d.Add(1, 2, 24000, 0, 0, 20, "audio", 16);
        d.Add(1, 2, 24000, 0, 0, 40, "audio", 8);
        d.Add(1, 2, 8000, 200, 40000, 40, "audio", 0);
        d.Add(1, 2, 8000, 200, 40000, 60, "audio", 0);
        return d;
    }

    [Theory]
    [MemberData(nameof(Cases))]
    public void MatchesReferenceBytes(int signal, int channels, int bitrate, int sweep, int sweepMax, int ms, string app, int flags)
    {
        var demo = (flags & 2) != 0 ? OpusEncoderSupport.DemoLossPath() : OpusEncoderSupport.DemoPath();
        if (demo is null) return;
        int frameSize = ms * 48;
        int frames = 8 * 48000 / frameSize;
        var pcm = signal == 0 ? OpusEncoderSupport.MakeSignal(channels, frames * frameSize, 5) : OpusEncoderSupport.MakeSpeechLike(channels, frames * frameSize, 5);
        if (signal == 2)
        {
            // digital silence for 1.2 s and 1.8 s: long enough for DTX to start sending nothing
            foreach (var (from, to) in new[] { (1.0, 2.2), (4.5, 6.3) })
                Array.Clear(pcm, (int)(from * 48000) * channels, (int)((to - from) * 48000) * channels);
        }
        var work = Path.Combine(Path.GetTempPath(), "oneaudio-enc-" + Guid.NewGuid().ToString("N"));
        try
        {
            var opts = new List<string> { "-framesize", ms.ToString() };
            if ((flags & 1) != 0) opts.Add("-forcemono");
            if ((flags & 2) != 0) { opts.Add("-inbandfec"); opts.Add("-loss"); opts.Add("20"); }
            if ((flags & 4) != 0) opts.Add("-dtx");
            if ((flags & 8) != 0) opts.Add("-cvbr");
            if ((flags & 16) != 0) opts.Add("-cbr");
            if (sweep != 0) { opts.Add("-sweep"); opts.Add(sweep.ToString()); }
            if (sweepMax != 0) { opts.Add("-sweep_max"); opts.Add(sweepMax.ToString()); }
            var refPk = OpusEncoderSupport.RunReference(demo, work, pcm, app, channels, bitrate, opts.ToArray());
            if ((flags & 4) != 0 && signal == 2)
                Assert.Contains(refPk, p => p.Data.Length == 1); // the test only means something if DTX really engages
            var settings = new OpusEncoderSettings
            {
                Channels = channels, BitrateBps = bitrate, Complexity = 10,
                BitrateMode = (flags & 16) != 0 ? OpusBitrateMode.Cbr : (flags & 8) != 0 ? OpusBitrateMode.ConstrainedVbr : OpusBitrateMode.Vbr,
                Application = app == "voip" ? OpusApplication.Voip : OpusApplication.Audio,
                ForceChannels = (flags & 1) != 0 ? 1 : 0,
                InBandFec = (flags & 2) != 0, PacketLossPercent = (flags & 2) != 0 ? 20 : 0,
                Dtx = (flags & 4) != 0,
            };
            var enc = new OpusEncoder(settings);
            var buf = new byte[1500];
            int br = bitrate, sw = sweep, mismatches = 0, first = -1;
            var frame = new short[frameSize * channels];
            string detail = "";
            for (int i = 0; i < frames; i++)
            {
                pcm.AsSpan(i * frame.Length, frame.Length).CopyTo(frame);
                int n = enc.Encode(frame, frameSize, buf);
                if (sweep != 0)
                {
                    br += sw;
                    if (sweepMax != 0)
                    {
                        if (br > sweepMax) sw = -sw;
                        else if (br < bitrate) sw = -sw;
                    }
                    if (br < 1000) br = 1000;
                    enc.Settings = enc.Settings with { BitrateBps = br };
                }
                bool same = n == refPk[i].Data.Length && buf.AsSpan(0, n).SequenceEqual(refPk[i].Data) && enc.FinalRange == refPk[i].Range;
                if (!same)
                {
                    mismatches++;
                    if (first < 0)
                    {
                        first = i;
                        detail = $"ref {refPk[i].Data.Length} B toc {refPk[i].Data[0]:x2} range {refPk[i].Range:x8}, ours {n} B toc {buf[0]:x2} range {enc.FinalRange:x8}";
                    }
                }
            }
            Assert.True(mismatches == 0, $"{mismatches} of {frames} packets differ; first {first}: {detail}");
        }
        finally
        {
            if (Directory.Exists(work)) Directory.Delete(work, true);
        }
    }

    // random frame sizes and random FEC toggling, replaying opus_demo's own control flow and its rand() sequence
    public static TheoryData<int, int, int, int, bool, bool> RandomCases()
    {
        var d = new TheoryData<int, int, int, int, bool, bool>();
        foreach (int ch in new[] { 1, 2 })
            foreach (int br in new[] { 12000, 20000, 40000 })
                d.Add(ch, br, 0, 0, true, false);
        d.Add(1, 12000, 400, 30000, true, true);
        d.Add(2, 16000, 300, 48000, true, true);
        d.Add(2, 30000, 0, 0, true, true);
        return d;
    }

    [Theory]
    [MemberData(nameof(RandomCases))]
    public void MatchesReferenceWithRandomFrameSizes(int channels, int bitrate, int sweep, int sweepMax, bool randomSize, bool randomFec)
    {
        var demo = OpusEncoderSupport.DemoLossPath();
        if (demo is null) return;
        const int Seconds = 14;
        var pcm = OpusEncoderSupport.MakeSpeechLike(channels, Seconds * 48000, 9);
        var work = Path.Combine(Path.GetTempPath(), "oneaudio-enc-" + Guid.NewGuid().ToString("N"));
        try
        {
            var opts = new List<string> { "-framesize", "20", "-loss", "10", "-inbandfec" };
            if (randomSize) opts.Add("-random_framesize");
            if (randomFec) opts.Add("-random_fec");
            if (sweep != 0) { opts.Add("-sweep"); opts.Add(sweep.ToString()); }
            if (sweepMax != 0) { opts.Add("-sweep_max"); opts.Add(sweepMax.ToString()); }
            var refPk = OpusEncoderSupport.RunReference(demo, work, pcm, "audio", channels, bitrate, opts.ToArray());
            var enc = new OpusEncoder(new OpusEncoderSettings { Channels = channels, BitrateBps = bitrate, InBandFec = true, PacketLossPercent = 10 });
            var rand = new OpusEncoderSupport.GlibcRand();
            var sizes = new[] { 120, 240, 480, 960, 1920, 2880 };
            int frameSize = 960, newSize = 960, br = bitrate, sw = sweep, pos = 0, index = 0, mismatches = 0, first = -1;
            bool delayed = false, fec = true;
            var buf = new byte[1500];
            var sizeSeen = new HashSet<int>();
            string detail = "";
            while (index < refPk.Count - 1)
            {
                if (delayed) { frameSize = newSize; delayed = false; }
                else if (randomSize && rand.Next() % 20 == 0)
                {
                    newSize = sizes[rand.Next() % 6];
                    while (newSize < 1920 && br - Math.Abs(sw) <= 3 * 12 * 48000 / newSize) newSize *= 2;
                    if (newSize < 480 && frameSize >= 480) { enc.ForceMode(1002); delayed = true; }
                    else frameSize = newSize;
                }
                if (randomFec && rand.Next() % 30 == 0)
                {
                    rand.Next(); // the reference's int-checking macro evaluates its argument twice
                    fec = rand.Next() % 4 == 0;
                    enc.Settings = enc.Settings with { InBandFec = fec };
                }
                var frame = new short[frameSize * channels];
                int have = Math.Min(frame.Length, pcm.Length - pos);
                if (have <= 0) break;
                Array.Copy(pcm, pos, frame, 0, have);
                pos += frame.Length;
                sizeSeen.Add(frameSize);
                int n = enc.Encode(frame, frameSize, buf);
                if (sweep != 0)
                {
                    br += sw;
                    if (sweepMax != 0)
                    {
                        if (br > sweepMax) sw = -sw;
                        else if (br < bitrate) sw = -sw;
                    }
                    if (br < 1000) br = 1000;
                    enc.Settings = enc.Settings with { BitrateBps = br };
                }
                bool same = n == refPk[index].Data.Length && buf.AsSpan(0, n).SequenceEqual(refPk[index].Data) && enc.FinalRange == refPk[index].Range;
                if (!same)
                {
                    mismatches++;
                    if (first < 0) { first = index; detail = $"frame {frameSize}: ref {refPk[index].Data.Length} B toc {refPk[index].Data[0]:x2}, ours {n} B toc {buf[0]:x2}"; }
                }
                index++;
            }
            Assert.True(mismatches == 0, $"{mismatches} of {index} packets differ; first {first}: {detail}");
        }
        finally
        {
            if (Directory.Exists(work)) Directory.Delete(work, true);
        }
    }

    /// <summary>
    /// Randomly drawn configurations (seeded, so reproducible): channels, signal, frame size, application, complexity, rate mode,
    /// forced bandwidth, FEC and loss, DTX, force-mono and bitrate sweeps, 6 s each.
    /// </summary>
    public static TheoryData<int> Seeds()
    {
        var d = new TheoryData<int>();
        for (int i = 0; i < 80; i++) d.Add(i);
        return d;
    }

    [Theory]
    [MemberData(nameof(Seeds))]
    public void MatchesReferenceOnRandomConfigurations(int seed)
    {
        var r = new Random(1000 + seed);
        int channels = r.Next(1, 3);
        int signal = r.Next(0, 3);
        int ms = new[] { 10, 20, 20, 20, 40, 60 }[r.Next(6)];
        string app = r.Next(8) == 0 ? "restricted-lowdelay" : r.Next(3) == 0 ? "voip" : "audio";
        int complexity = r.Next(0, 11);
        int rateMode = r.Next(0, 3);
        int bandwidth = r.Next(3) == 0 ? r.Next(1, 6) : 0;
        bool fec = r.Next(3) == 0, dtx = r.Next(4) == 0, mono = channels == 2 && r.Next(5) == 0;
        int bitrate = r.Next(6000, 90000);
        int sweep = r.Next(3) == 0 ? 0 : r.Next(-300, 400);
        int sweepMax = sweep > 0 && r.Next(2) == 0 ? bitrate + r.Next(5000, 60000) : 0;
        if (app == "restricted-lowdelay") { fec = false; dtx = false; }

        var demo = fec ? OpusEncoderSupport.DemoLossPath() : OpusEncoderSupport.DemoPath();
        if (demo is null) return;
        int frameSize = ms * 48;
        int frames = 6 * 48000 / frameSize;
        var pcm = signal == 0 ? OpusEncoderSupport.MakeSignal(channels, frames * frameSize, seed) : OpusEncoderSupport.MakeSpeechLike(channels, frames * frameSize, seed);
        if (signal == 2)
            Array.Clear(pcm, 2 * 48000 * channels, 96000 * channels);
        var work = Path.Combine(Path.GetTempPath(), "oneaudio-enc-" + Guid.NewGuid().ToString("N"));
        string config = $"seed {seed}: {channels} ch, signal {signal}, {ms} ms, {app}, complexity {complexity}, rate mode {rateMode}, bandwidth {bandwidth}, fec {fec}, dtx {dtx}, mono {mono}, {bitrate} bps, sweep {sweep}..{sweepMax}";
        try
        {
            var opts = new List<string> { "-framesize", ms.ToString(), "-complexity", complexity.ToString() };
            if (mono) opts.Add("-forcemono");
            if (fec) { opts.Add("-inbandfec"); opts.Add("-loss"); opts.Add("15"); }
            if (dtx) opts.Add("-dtx");
            if (rateMode == 1) opts.Add("-cvbr");
            if (rateMode == 2) opts.Add("-cbr");
            if (bandwidth > 0) { opts.Add("-bandwidth"); opts.Add(new[] { "", "NB", "MB", "WB", "SWB", "FB" }[bandwidth]); }
            if (sweep != 0) { opts.Add("-sweep"); opts.Add(sweep.ToString()); }
            if (sweepMax != 0) { opts.Add("-sweep_max"); opts.Add(sweepMax.ToString()); }
            var refPk = OpusEncoderSupport.RunReference(demo, work, pcm, app, channels, bitrate, opts.ToArray());
            var enc = new OpusEncoder(new OpusEncoderSettings
            {
                Channels = channels, BitrateBps = bitrate, Complexity = complexity,
                BitrateMode = rateMode == 2 ? OpusBitrateMode.Cbr : rateMode == 1 ? OpusBitrateMode.ConstrainedVbr : OpusBitrateMode.Vbr,
                Bandwidth = bandwidth == 0 ? OpusBandwidth.Auto : (OpusBandwidth)(1100 + bandwidth),
                Application = app == "voip" ? OpusApplication.Voip : app == "audio" ? OpusApplication.Audio : OpusApplication.RestrictedLowDelay,
                ForceChannels = mono ? 1 : 0, InBandFec = fec, PacketLossPercent = fec ? 15 : 0, Dtx = dtx,
            });
            var buf = new byte[1500];
            var frame = new short[frameSize * channels];
            int br = bitrate, sw = sweep, mismatches = 0, first = -1;
            string detail = "";
            for (int i = 0; i < frames; i++)
            {
                pcm.AsSpan(i * frame.Length, frame.Length).CopyTo(frame);
                int n = enc.Encode(frame, frameSize, buf);
                if (sweep != 0)
                {
                    br += sw;
                    if (sweepMax != 0)
                    {
                        if (br > sweepMax) sw = -sw;
                        else if (br < bitrate) sw = -sw;
                    }
                    if (br < 1000) br = 1000;
                    enc.Settings = enc.Settings with { BitrateBps = br };
                }
                bool same = n == refPk[i].Data.Length && buf.AsSpan(0, n).SequenceEqual(refPk[i].Data) && enc.FinalRange == refPk[i].Range;
                if (!same)
                {
                    mismatches++;
                    if (first < 0) { first = i; detail = $"ref {refPk[i].Data.Length} B toc {refPk[i].Data[0]:x2}, ours {n} B toc {buf[0]:x2}"; }
                }
            }
            Assert.True(mismatches == 0, $"{config}: {mismatches} of {frames} packets differ; first {first}: {detail}");
        }
        finally
        {
            if (Directory.Exists(work)) Directory.Delete(work, true);
        }
    }
}
