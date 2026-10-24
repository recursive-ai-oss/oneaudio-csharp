using System.Buffers.Binary;
using OneAudio.Opus;
using Xunit;

namespace OneAudio.Tests;

/// <summary>A packet list standing in for an Ogg demuxer: headers first, then audio packets with optional end positions.</summary>
internal sealed class ListPacketSource : IPacketSource
{
    private readonly List<(byte[] Data, long End, bool Header)> _packets = [];
    private int _next;
    private readonly bool _canSeek;

    public ListPacketSource(byte[] head, byte[] tags, IEnumerable<byte[]> audio, int frameSize, long finalGranule = -1, bool canSeek = true)
    {
        _canSeek = canSeek;
        _packets.Add((head, -1, true));
        _packets.Add((tags, -1, true));
        long g = 0;
        var list = audio.ToList();
        for (int i = 0; i < list.Count; i++)
        {
            g += frameSize;
            // Like Ogg, only the last packet of a page carries a position; here every packet does, for seeking.
            long end = i == list.Count - 1 && finalGranule >= 0 ? finalGranule : g;
            _packets.Add((list[i], end, false));
        }
        LengthFrames = finalGranule >= 0 ? finalGranule : g;
    }

    public CodecId Codec => CodecId.Opus;
    public long LengthFrames { get; }
    public bool CanSeek => _canSeek;
    public bool Disposed { get; private set; }

    public bool TryReadPacket(out PacketView packet)
    {
        if (_next >= _packets.Count) { packet = default; return false; }
        var p = _packets[_next++];
        packet = new PacketView(p.Data, p.End, p.Header);
        return true;
    }

    public void SeekToFrame(long frame, out long startFrame)
    {
        // The last packet ending at or before the target comes first; before any such packet, the first audio packet.
        int pick = 2;
        startFrame = 0;
        for (int i = 2; i < _packets.Count; i++)
        {
            if (_packets[i].End <= frame) { pick = i; startFrame = _packets[i].End; }
        }
        _next = pick;
    }

    public void Dispose() => Disposed = true;

    public static byte[] Head(int channels, int preSkip, int gainQ8 = 0, int family = 0, byte[]? table = null, int streams = 1, int coupled = 0)
    {
        var h = new List<byte>("OpusHead"u8.ToArray()) { 1, (byte)channels };
        h.AddRange(BitConverter.GetBytes((ushort)preSkip));
        h.AddRange(BitConverter.GetBytes(48000));
        h.AddRange(BitConverter.GetBytes((short)gainQ8));
        h.Add((byte)family);
        if (family != 0)
        {
            h.Add((byte)streams);
            h.Add((byte)coupled);
            h.AddRange(table!);
        }
        return h.ToArray();
    }

    public static byte[] Tags()
    {
        var t = new List<byte>("OpusTags"u8.ToArray());
        t.AddRange(BitConverter.GetBytes(4));
        t.AddRange("test"u8.ToArray());
        t.AddRange(BitConverter.GetBytes(0));
        return t.ToArray();
    }
}

public class OpusCodecTests
{
    private static List<byte[]> Packets(int count, int toc = 0xFC, int seed = 1)
    {
        var l = new List<byte[]>();
        for (int i = 0; i < count; i++) l.Add(OpusDecoderTests.RandomPacket(toc, 90, seed * 1000 + i));
        return l;
    }

    private static float[] ReadAll(IAudioDecoder d)
    {
        var all = new List<float>();
        var buf = new float[777 * d.Format.Channels];
        int n;
        while ((n = d.Read(buf)) > 0) all.AddRange(buf.AsSpan(0, n * d.Format.Channels).ToArray());
        return all.ToArray();
    }

    private static float[] Reference(List<byte[]> packets, int channels)
    {
        var dec = new OpusDecoder(48000, channels);
        var all = new List<float>();
        var pcm = new float[5760 * channels];
        foreach (var p in packets)
        {
            int n = dec.Decode(p, pcm);
            all.AddRange(pcm.AsSpan(0, n * channels).ToArray());
        }
        return all.ToArray();
    }

    private static IAudioDecoder Open(ListPacketSource src) => new AudioCodecRegistry().AddOpus().GetCodec(CodecId.Opus).CreateDecoder(src, AudioLimits.Default);

    [Fact]
    public void Pre_skip_is_removed_and_the_format_is_reported()
    {
        var packets = Packets(10, 0xF8); // CELT fullband 20 ms, mono
        using var d = Open(new ListPacketSource(ListPacketSource.Head(1, 312), ListPacketSource.Tags(), packets, 960));
        Assert.Equal(new AudioFormat(48000, 1, ChannelMask.FrontCenter), d.Format);
        Assert.Equal(9600 - 312, d.LengthFrames);
        var got = ReadAll(d);
        var want = Reference(packets, 1).AsSpan(312).ToArray();
        Assert.Equal(want, got);
        Assert.Equal(9600 - 312, d.PositionFrames);
    }

    [Fact]
    public void The_final_position_trims_the_last_packet()
    {
        var packets = Packets(10, 0xFC);
        using var d = Open(new ListPacketSource(ListPacketSource.Head(2, 100), ListPacketSource.Tags(), packets, 960, finalGranule: 9600 - 500));
        var got = ReadAll(d);
        Assert.Equal((9600 - 500 - 100) * 2, got.Length);
        Assert.Equal(9600 - 500 - 100, d.LengthFrames);
        Assert.Equal(Reference(packets, 2).AsSpan(200, got.Length).ToArray(), got);
    }

    [Fact]
    public void Output_gain_scales_the_signal()
    {
        var packets = Packets(5, 0xF8);
        using var plain = Open(new ListPacketSource(ListPacketSource.Head(1, 0), ListPacketSource.Tags(), packets, 960));
        using var loud = Open(new ListPacketSource(ListPacketSource.Head(1, 0, gainQ8: 6 * 256), ListPacketSource.Tags(), packets, 960));
        var a = ReadAll(plain);
        var b = ReadAll(loud);
        double k = Math.Pow(10, 6 / 20.0);
        for (int i = 0; i < a.Length; i++) Assert.Equal(a[i] * k, b[i], 1e-4 * Math.Max(1, Math.Abs(a[i] * k)));
    }

    [Fact]
    public void Seek_pre_rolls_80_ms_and_lands_on_the_frame()
    {
        var packets = Packets(40, 0xFC, seed: 7);
        int preSkip = 312;
        using var d = Open(new ListPacketSource(ListPacketSource.Head(2, preSkip), ListPacketSource.Tags(), packets, 960));
        var full = ReadAll(d);

        long target = 20000;
        d.Seek(target);
        Assert.Equal(target, d.PositionFrames);
        var buf = new float[2 * 3000];
        Assert.Equal(3000, d.Read(buf));

        // The container hands back the packet ending at or before (target + pre-skip - 80 ms); a fresh decoder that
        // starts there must produce the same samples. Packets here are 960 frames, so that is packet index 19.
        long seekTo = target + preSkip - 3840;
        int first = (int)(seekTo / 960) - 1; // packet whose end is the largest multiple of 960 <= seekTo, minus one
        var fresh = new OpusDecoder(48000, 2);
        var tmp = new float[2 * 960];
        long g = (first + 1) * 960L; // granule at the end of packet `first`
        fresh.Decode(packets[first], tmp);
        var expected = new List<float>();
        for (int i = first + 1; i < packets.Count && expected.Count < 2 * 12000; i++)
        {
            fresh.Decode(packets[i], tmp);
            long start = g;
            g += 960;
            for (int j = 0; j < 960; j++)
                if (start + j >= target + preSkip) { expected.Add(tmp[2 * j]); expected.Add(tmp[2 * j + 1]); }
        }
        Assert.Equal(expected.Take(buf.Length).ToArray(), buf);
        Assert.Equal(target, d.PositionFrames - 3000);

        // Seeking is repeatable and back to the start restores a full decode.
        d.Seek(0);
        Assert.Equal(full, ReadAll(d));
        d.Seek(d.LengthFrames);
        Assert.Equal(0, d.Read(buf));
        Assert.Throws<ArgumentOutOfRangeException>(() => d.Seek(d.LengthFrames + 1));
    }

    [Fact]
    public void Seek_requires_a_seekable_source()
    {
        using var d = Open(new ListPacketSource(ListPacketSource.Head(1, 0), ListPacketSource.Tags(), Packets(3, 0xF8), 960, canSeek: false));
        Assert.False(d.CanSeek);
        Assert.Throws<NotSupportedException>(() => d.Seek(10));
        Assert.Throws<NotSupportedException>(() => d.ReadInteger(new int[10]));
    }

    [Fact]
    public void Multistream_family_one_maps_channels_to_wav_order()
    {
        // Three channels in Vorbis order (left, centre, right): one coupled stream (left, right) and one mono stream.
        var stereo = OpusDecoderTests.RandomPacket(0xFC, 70, 11);
        var mono = OpusDecoderTests.RandomPacket(0xF8, 50, 12);
        // The first stream uses self-delimited framing: TOC, frame length, frame.
        var packet = new List<byte> { stereo[0], (byte)(stereo.Length - 1) };
        packet.AddRange(stereo.AsSpan(1).ToArray());
        packet.AddRange(mono);
        byte[] head = ListPacketSource.Head(3, 0, family: 1, table: [0, 2, 1], streams: 2, coupled: 1);
        using var d = Open(new ListPacketSource(head, ListPacketSource.Tags(), [packet.ToArray()], 960));

        Assert.Equal(ChannelMask.FrontLeft | ChannelMask.FrontRight | ChannelMask.FrontCenter, d.Format.ChannelMask);
        var got = ReadAll(d);
        Assert.Equal(960 * 3, got.Length);

        var s = new float[960 * 2];
        var m = new float[960];
        new OpusDecoder(48000, 2).Decode(stereo, s);
        new OpusDecoder(48000, 1).Decode(mono, m);
        for (int i = 0; i < 960; i++)
        {
            Assert.Equal(s[2 * i], got[3 * i]);      // front left
            Assert.Equal(s[2 * i + 1], got[3 * i + 1]); // front right
            Assert.Equal(m[i], got[3 * i + 2]);      // front centre
        }
    }

    [Fact]
    public void Chained_streams_restart_pre_skip()
    {
        var a = Packets(4, 0xF8, 1);
        var b = Packets(4, 0xF8, 2);
        var all = new List<byte[]>(a);
        // A second link: the packet source delivers its headers in-band.
        var src = new ChainedSource(ListPacketSource.Head(1, 200), ListPacketSource.Tags(), a, b);
        using var d = Open2(src);
        var got = ReadAll(d);
        Assert.Equal(2 * (4 * 960 - 200), got.Length);
        Assert.Equal(Reference(a, 1).AsSpan(200).ToArray(), got.AsSpan(0, 4 * 960 - 200).ToArray());
        Assert.Equal(Reference(b, 1).AsSpan(200).ToArray(), got.AsSpan(4 * 960 - 200).ToArray());
        _ = all;
    }

    private static IAudioDecoder Open2(IPacketSource src) => new AudioCodecRegistry().AddOpus().GetCodec(CodecId.Opus).CreateDecoder(src, AudioLimits.Default);

    private sealed class ChainedSource : IPacketSource
    {
        private readonly List<(byte[] Data, bool Header)> _p = [];
        private int _i;

        public ChainedSource(byte[] head, byte[] tags, List<byte[]> a, List<byte[]> b)
        {
            foreach (var link in new[] { a, b })
            {
                _p.Add((head, true));
                _p.Add((tags, true));
                foreach (var x in link) _p.Add((x, false));
            }
        }

        public CodecId Codec => CodecId.Opus;
        public long LengthFrames => -1;
        public bool CanSeek => false;
        public bool TryReadPacket(out PacketView packet)
        {
            if (_i >= _p.Count) { packet = default; return false; }
            var (data, header) = _p[_i++];
            packet = new PacketView(data, -1, header);
            return true;
        }
        public void SeekToFrame(long frame, out long startFrame) => throw new NotSupportedException();
        public void Dispose() { }
    }

    [Fact]
    public void Invalid_and_oversized_headers_are_rejected()
    {
        var tags = ListPacketSource.Tags();
        var audio = Packets(1, 0xF8);
        // Truncated OpusHead.
        var src = new ListPacketSource(ListPacketSource.Head(1, 0)[..12], tags, audio, 960);
        Assert.Throws<AudioFormatException>(() => Open(src));
        Assert.True(src.Disposed);
        // Missing OpusTags.
        Assert.Throws<AudioFormatException>(() => Open(new ListPacketSource(ListPacketSource.Head(1, 0), "NotTags!"u8.ToArray(), audio, 960)));
        // Family 0 with three channels.
        Assert.Throws<AudioFormatException>(() => Open(new ListPacketSource(ListPacketSource.Head(3, 0), tags, audio, 960)));
        // Unsupported mapping family (ambisonics).
        Assert.Throws<UnsupportedAudioFormatException>(() => Open(new ListPacketSource(ListPacketSource.Head(4, 0, family: 2, table: [0, 1, 2, 3], streams: 4), tags, audio, 960)));
        // Mapping entry pointing at a missing stream.
        Assert.Throws<AudioFormatException>(() => Open(new ListPacketSource(ListPacketSource.Head(2, 0, family: 1, table: [0, 9], streams: 1), tags, audio, 960)));
        // Channel count over the configured limit is refused before any decoder is built.
        var limits = AudioLimits.Default with { MaxChannels = 2 };
        var wide = ListPacketSource.Head(8, 0, family: 255, table: [0, 1, 2, 3, 4, 5, 6, 7], streams: 8);
        Assert.Throws<AudioLimitExceededException>(() => new AudioCodecRegistry().AddOpus().GetCodec(CodecId.Opus).CreateDecoder(new ListPacketSource(wide, tags, audio, 960), limits));
        // A huge stream count in a family 255 header would build many decoders; the limit stops it.
        var many = ListPacketSource.Head(1, 0, family: 255, table: [0], streams: 255);
        Assert.Throws<AudioLimitExceededException>(() => new AudioCodecRegistry().AddOpus().GetCodec(CodecId.Opus).CreateDecoder(new ListPacketSource(many, tags, audio, 960), limits));
    }
}
