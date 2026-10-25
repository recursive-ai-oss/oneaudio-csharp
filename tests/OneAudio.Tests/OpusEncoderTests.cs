using System.Diagnostics;
using OneAudio.Opus;
using Xunit;

namespace OneAudio.Tests;

/// <summary>Conformance, rate, allocation and argument tests of <see cref="OpusEncoder"/>, <see cref="OpusMultistreamEncoder"/> and <see cref="OpusFileWriter"/>.</summary>
public class OpusEncoderTests
{
    public static TheoryData<int, int, int, int, OpusBitrateMode, int, OpusBandwidth, OpusApplication> ConformanceCases()
    {
        var d = new TheoryData<int, int, int, int, OpusBitrateMode, int, OpusBandwidth, OpusApplication>();
        foreach (int ch in new[] { 1, 2 })
            foreach (int ms10 in new[] { 25, 50, 100, 200, 400, 600 })
                foreach (var bw in new[] { OpusBandwidth.Narrowband, OpusBandwidth.Wideband, OpusBandwidth.Fullband })
                    d.Add(ch, ms10, ch == 1 ? 16000 : 32000, 5, OpusBitrateMode.Vbr, 3, bw, OpusApplication.Audio);
        foreach (int ch in new[] { 1, 2 })
            foreach (var mode in new[] { OpusBitrateMode.Vbr, OpusBitrateMode.ConstrainedVbr, OpusBitrateMode.Cbr })
                foreach (int rate in new[] { 12000, 64000, 256000 })
                    foreach (int cx in new[] { 0, 10 })
                        d.Add(ch, 200, rate, cx, mode, 1, OpusBandwidth.Auto, OpusApplication.Audio);
        d.Add(2, 200, 40000, 10, OpusBitrateMode.Vbr, 1, OpusBandwidth.Auto, OpusApplication.Voip);
        d.Add(1, 100, 40000, 10, OpusBitrateMode.Cbr, 1, OpusBandwidth.Auto, OpusApplication.RestrictedLowDelay);
        return d;
    }

    /// <summary>Our packets decode with the reference decoder without error, the final ranges agree, and our decoder matches it sample for sample.</summary>
    [Theory]
    [MemberData(nameof(ConformanceCases))]
    public void PacketsDecodeWithReference(int channels, int ms10, int bitrate, int complexity, OpusBitrateMode mode, int seed,
        OpusBandwidth bandwidth, OpusApplication app)
    {
        var demo = OpusEncoderSupport.DemoPath();
        if (demo is null) return;
        int frameSize = ms10 * 48 / 10;
        var pcm = OpusEncoderSupport.MakeSignal(channels, 24000 / frameSize * frameSize + frameSize * 3, seed);
        var enc = new OpusEncoder(new OpusEncoderSettings
        {
            Channels = channels, BitrateBps = bitrate, Complexity = complexity, BitrateMode = mode, Bandwidth = bandwidth, Application = app,
        });
        var packets = OpusEncoderSupport.Encode(enc, pcm, frameSize);
        var work = Path.Combine(Path.GetTempPath(), "oneaudio-enc-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(work);
        try
        {
            var bit = new MemoryStream();
            foreach (var p in packets)
            {
                var hdr = new byte[8];
                System.Buffers.Binary.BinaryPrimitives.WriteInt32BigEndian(hdr, p.Data.Length);
                System.Buffers.Binary.BinaryPrimitives.WriteUInt32BigEndian(hdr.AsSpan(4), p.Range);
                bit.Write(hdr);
                bit.Write(p.Data);
            }
            File.WriteAllBytes(Path.Combine(work, "in.bit"), bit.ToArray());
            var psi = new ProcessStartInfo(demo) { RedirectStandardError = true, RedirectStandardOutput = true };
            foreach (var a in new[] { "-d", "48000", channels.ToString(), Path.Combine(work, "in.bit"), Path.Combine(work, "ref.pcm") })
                psi.ArgumentList.Add(a);
            using var proc = Process.Start(psi)!;
            string err = proc.StandardError.ReadToEnd() + proc.StandardOutput.ReadToEnd();
            proc.WaitForExit();
            Assert.True(proc.ExitCode == 0 && !err.Contains("Error", StringComparison.OrdinalIgnoreCase), err);

            var refPcm = File.ReadAllBytes(Path.Combine(work, "ref.pcm"));
            var dec = new OpusDecoder(48000, channels);
            var buf = new float[5760 * channels];
            int pos = 0;
            for (int i = 0; i < packets.Count; i++)
            {
                int n = dec.Decode(packets[i].Data, buf);
                Assert.Equal(packets[i].Range, dec.FinalRange);
                for (int k = 0; k < n * channels; k++, pos += 2)
                {
                    double v = Math.Floor(buf[k] * 32768.0 + 0.5);
                    short s = (short)Math.Clamp(v, -32768, 32767);
                    short r = (short)(refPcm[pos] | (refPcm[pos + 1] << 8));
                    Assert.True(s == r, $"packet {i} sample {k}: ours {s}, reference {r}");
                }
            }
            Assert.Equal(refPcm.Length, pos);
        }
        finally
        {
            Directory.Delete(work, true);
        }
    }

    [Fact]
    public void EncodingAllocatesNothingAfterWarmUp()
    {
        foreach (int ch in new[] { 1, 2 })
        {
            var enc = new OpusEncoder(new OpusEncoderSettings { Channels = ch, BitrateBps = 64000 * ch / 2 });
            var pcm = OpusEncoderSupport.ToFloat(OpusEncoderSupport.MakeSignal(ch, 960 * 40, 5));
            var packet = new byte[1500];
            int frame = 960 * ch;
            for (int i = 0; i < 20; i++) enc.Encode(pcm.AsSpan(i * frame, frame), 960, packet);
            long before = GC.GetAllocatedBytesForCurrentThread();
            int gcs = GC.CollectionCount(0);
            for (int i = 20; i < 40; i++) enc.Encode(pcm.AsSpan(i * frame, frame), 960, packet);
            for (int i = 0; i < 4; i++) enc.Encode(pcm.AsSpan(i * 1920 * ch, 1920 * ch), 1920, packet);
            long allocated = GC.GetAllocatedBytesForCurrentThread() - before;
            Assert.Equal(0, allocated);
            Assert.Equal(gcs, GC.CollectionCount(0));
        }
    }

    [Fact]
    public void IdenticalInputGivesIdenticalBytes()
    {
        var pcm = OpusEncoderSupport.MakeSignal(2, 48000, 9);
        var a = OpusEncoderSupport.Encode(new OpusEncoder(new OpusEncoderSettings { BitrateBps = 48000 }), pcm, 960);
        var b = OpusEncoderSupport.Encode(new OpusEncoder(new OpusEncoderSettings { BitrateBps = 48000 }), pcm, 960);
        Assert.Equal(a.Count, b.Count);
        for (int i = 0; i < a.Count; i++) Assert.True(a[i].Data.AsSpan().SequenceEqual(b[i].Data));
    }

    [Fact]
    public void BadSettingsAndArgumentsThrowTypedExceptions()
    {
        Assert.Throws<ArgumentOutOfRangeException>(() => new OpusEncoder(new OpusEncoderSettings { Channels = 3 }));
        Assert.Throws<ArgumentOutOfRangeException>(() => new OpusEncoder(new OpusEncoderSettings { SampleRate = 44100 }));
        Assert.Throws<ArgumentOutOfRangeException>(() => new OpusEncoder(new OpusEncoderSettings { Complexity = 11 }));
        Assert.Throws<ArgumentOutOfRangeException>(() => new OpusEncoder(new OpusEncoderSettings { BitrateBps = 100 }));
        Assert.Throws<ArgumentOutOfRangeException>(() => new OpusEncoder(new OpusEncoderSettings { BitrateBps = 700000 }));
        Assert.Throws<ArgumentOutOfRangeException>(() => new OpusEncoder(new OpusEncoderSettings { PacketLossPercent = 101 }));
        Assert.Throws<ArgumentOutOfRangeException>(() => new OpusEncoder(new OpusEncoderSettings { Bandwidth = (OpusBandwidth)7 }));
        Assert.Throws<ArgumentOutOfRangeException>(() => new OpusEncoder(new OpusEncoderSettings { ForceChannels = 3 }));
        Assert.Throws<ArgumentOutOfRangeException>(() => new OpusMultistreamEncoder(new OpusEncoderSettings { Channels = 9 }));
        var enc = new OpusEncoder();
        var pcm = new float[2 * 1000];
        var packet = new byte[1000];
        Assert.Throws<ArgumentException>(() => enc.Encode(pcm, 1000, packet));
        Assert.Throws<ArgumentException>(() => enc.Encode(pcm.AsSpan(0, 100), 960, packet));
        Assert.Throws<ArgumentException>(() => enc.Encode(pcm, 960, new byte[1]));
        Assert.Throws<ArgumentException>(() => enc.Settings = new OpusEncoderSettings { Channels = 1 });
        Assert.Throws<ArgumentOutOfRangeException>(() => enc.Settings = new OpusEncoderSettings { Complexity = -1 });
        Assert.Throws<ArgumentOutOfRangeException>(() => new OpusFileWriter(new MemoryStream(), new OpusFileWriterSettings { FrameMilliseconds = 30 }));
        Assert.Throws<ArgumentOutOfRangeException>(() => new OpusFileWriter(new MemoryStream(), new OpusFileWriterSettings { InputSampleRate = 100 }));
        Assert.Throws<ArgumentException>(() => new OpusFileWriter(new MemoryStream([], false), new OpusFileWriterSettings()));
    }

    [Fact]
    public void SilenceAndClippedInputEncode()
    {
        var enc = new OpusEncoder(new OpusEncoderSettings { BitrateBps = 64000, BitrateMode = OpusBitrateMode.ConstrainedVbr });
        var packet = new byte[1500];
        var silence = new float[960 * 2];
        for (int i = 0; i < 5; i++) Assert.InRange(enc.Encode(silence, 960, packet), 2, 40);
        var loud = new float[960 * 2];
        for (int i = 0; i < loud.Length; i++) loud[i] = (i & 2) == 0 ? 50f : -50f;
        loud[7] = float.NaN;
        Assert.True(enc.Encode(loud, 960, packet) > 2);
        var dec = new OpusDecoder(48000, 2);
        var o = new float[960 * 2];
        Assert.Equal(960, dec.Decode(packet.AsSpan(0, enc.Encode(loud, 960, packet)), o));
        Assert.All(o, v => Assert.False(float.IsNaN(v)));
    }
}
