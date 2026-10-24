using OneAudio.Opus;
using Xunit;

namespace OneAudio.Tests;

public class OpusDecoderTests
{
    internal static byte[] RandomPacket(int toc, int length, int seed)
    {
        var rng = new Random(seed);
        var p = new byte[length];
        rng.NextBytes(p);
        p[0] = (byte)toc;
        return p;
    }

    [Fact]
    public void Every_configuration_decodes_random_payloads_to_finite_audio()
    {
        foreach (int rate in new[] { 48000, 24000 })
        foreach (int channels in new[] { 1, 2 })
        {
            var dec = new OpusDecoder(rate, channels);
            var pcm = new float[5760 * channels];
            for (int config = 0; config < 32; config++)
            for (int stereo = 0; stereo < 2; stereo++)
            for (int seed = 0; seed < 3; seed++)
            {
                var packet = RandomPacket((config << 3) | (stereo << 2), 60 + seed * 40, config * 7 + seed);
                int n = dec.Decode(packet, pcm);
                Assert.Equal(OpusPacket.GetSampleCount(packet, rate), n);
                foreach (float v in pcm.AsSpan(0, n * channels))
                    Assert.True(float.IsFinite(v) && Math.Abs(v) < 1e6f, $"{v}");
            }
        }
    }

    [Fact]
    public void Multi_frame_packets_decode_every_frame()
    {
        // Code 3, CBR, three 20 ms CELT frames of 30 bytes each.
        var packet = new byte[2 + 90];
        new Random(5).NextBytes(packet);
        packet[0] = 0xFB; // CELT fullband 20 ms, stereo, code 3
        packet[1] = 3;
        var dec = new OpusDecoder(48000, 2);
        var pcm = new float[2 * 2880];
        Assert.Equal(2880, dec.Decode(packet, pcm));
        Assert.Equal(2880, OpusPacket.GetSampleCount(packet, 48000));
    }

    [Fact]
    public void Lost_packets_are_concealed_with_bounded_finite_output()
    {
        foreach (int config in new[] { 1, 9, 13, 16, 19, 28, 31 })
        {
            var dec = new OpusDecoder(48000, 2);
            int frame = OpusPacket.GetSamplesPerFrame(new byte[] { (byte)(config << 3 | 4) }, 48000);
            var pcm = new float[2 * 5760];
            for (int i = 0; i < 20; i++)
            {
                int n;
                if (i % 4 == 3)
                {
                    n = dec.DecodePacketLoss(pcm.AsSpan(0, frame * 2));
                    Assert.Equal(frame, n);
                }
                else
                {
                    n = dec.Decode(RandomPacket(config << 3 | 4, 80, i + config), pcm);
                }
                foreach (float v in pcm.AsSpan(0, n * 2))
                    Assert.True(float.IsFinite(v) && Math.Abs(v) < 1e6f, $"{v}");
            }
            // Loss before any packet is silence; a long loss on a CELT stream is concealed in 20 ms steps.
            var fresh = new OpusDecoder(48000, 2);
            Assert.Equal(960, fresh.DecodePacketLoss(pcm.AsSpan(0, 960 * 2)));
            Assert.All(pcm.AsSpan(0, 1920).ToArray(), v => Assert.Equal(0f, v));
            Assert.Equal(2880, dec.DecodePacketLoss(pcm.AsSpan(0, 2880 * 2)));
        }
    }

    [Fact]
    public void Concealment_decays_toward_silence()
    {
        var dec = new OpusDecoder(48000, 1);
        var pcm = new float[960];
        for (int i = 0; i < 5; i++) dec.Decode(RandomPacket(0xF8, 120, i), pcm);
        float last = 0;
        for (int i = 0; i < 40; i++)
        {
            dec.DecodePacketLoss(pcm);
            last = 0;
            foreach (float v in pcm) last = Math.Max(last, Math.Abs(v));
        }
        Assert.True(last < 0.1f, $"peak after 800 ms of loss was {last}");
    }

    [Fact]
    public void Decoding_allocates_nothing_after_warm_up()
    {
        var dec = new OpusDecoder(48000, 2);
        var packets = new List<byte[]>();
        for (int config = 0; config < 32; config += 3)
            packets.Add(RandomPacket(config << 3 | 4, 100, config));
        packets.Add(new byte[] { 0xFB, 3 }.Concat(new byte[90]).ToArray());
        var pcm = new float[2 * 5760];
        for (int round = 0; round < 60; round++)
            foreach (var p in packets) { dec.Decode(p, pcm); dec.DecodePacketLoss(pcm.AsSpan(0, 960 * 2)); }

        long before = GC.GetAllocatedBytesForCurrentThread();
        int gcs = GC.CollectionCount(0);
        for (int round = 0; round < 50; round++)
            foreach (var p in packets) { dec.Decode(p, pcm); dec.DecodePacketLoss(pcm.AsSpan(0, 960 * 2)); }
        long allocated = GC.GetAllocatedBytesForCurrentThread() - before;
        Assert.Equal(0, allocated);
        Assert.Equal(gcs, GC.CollectionCount(0));
    }

    [Fact]
    public void Malformed_packets_raise_format_errors()
    {
        var dec = new OpusDecoder(48000, 2);
        var pcm = new float[2 * 5760];
        // Code 1 needs an even payload; code 2 needs a valid size; code 3 needs a frame count.
        Assert.Throws<AudioFormatException>(() => dec.Decode(new byte[] { 0xF9, 1, 2, 3 }, pcm));
        Assert.Throws<AudioFormatException>(() => dec.Decode(new byte[] { 0xFA, 255 }, pcm));
        Assert.Throws<AudioFormatException>(() => dec.Decode(new byte[] { 0xFB }, pcm));
        Assert.Throws<AudioFormatException>(() => dec.Decode(new byte[] { 0xFB, 0 }, pcm));
        // 49 frames of 2.5 ms exceed 120 ms.
        Assert.Throws<AudioFormatException>(() => dec.Decode(new byte[] { 0xE3, 49 }, pcm));
        // A frame over 1275 bytes.
        var big = new byte[1300];
        big[0] = 0xF8;
        Assert.Throws<AudioFormatException>(() => dec.Decode(big, pcm));
        // Output buffer too small.
        Assert.Throws<ArgumentException>(() => dec.Decode(RandomPacket(0xF8, 50, 1), new float[10]));
        Assert.Throws<ArgumentOutOfRangeException>(() => new OpusDecoder(44100, 2));
        Assert.Throws<ArgumentOutOfRangeException>(() => new OpusDecoder(48000, 3));
    }

    [Fact]
    public void Arbitrary_bytes_never_escape_as_other_exceptions()
    {
        var rng = new Random(99);
        var dec = new OpusDecoder(48000, 2);
        var pcm = new float[2 * 5760];
        for (int i = 0; i < 3000; i++)
        {
            var p = new byte[rng.Next(1, 200)];
            rng.NextBytes(p);
            try { dec.Decode(p, pcm); }
            catch (AudioFormatException) { }
        }
    }
}
