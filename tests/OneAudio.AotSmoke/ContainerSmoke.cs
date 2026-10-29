using OneAudio;
using OneAudio.Aac;
using OneAudio.Amr;
using OneAudio.Mp4;

/// <summary>
/// NativeAOT check for the AAC and AMR containers: an AMR file, an ADTS stream and a 3GP file are built in memory and read
/// packet by packet with seeking, and the format-detecting entry point reports the missing codec with a typed error.
/// </summary>
internal static class ContainerSmoke
{
    public static int Run()
    {
        var registry = new AudioCodecRegistry().AddMp4().AddAdts().AddAmr();

        // AMR: 30 frames of 4.75 kbit/s (TOC byte 0x04, 12 speech bytes).
        var amr = new List<byte>("#!AMR\n"u8.ToArray());
        for (int i = 0; i < 30; i++) { amr.Add(0x04); for (int k = 0; k < 12; k++) amr.Add((byte)(i + k)); }
        using (var s = AmrPacketSource.Open(new MemoryStream(amr.ToArray())))
        {
            if (s.Codec != CodecId.AmrNb || s.LengthFrames != 30 * 160) return Fail("amr length");
            s.SeekToFrame(160 * 7 + 5, out long start);
            if (start != 160 * 7 || !s.TryReadPacket(out var p) || p.Data.Length != 13 || p.Data[1] != 7) return Fail("amr seek");
        }

        // ADTS: 20 AAC-LC 44.1 kHz stereo frames with 10-byte payloads (17-byte frames).
        var adts = new List<byte>();
        for (int i = 0; i < 20; i++)
        {
            adts.AddRange(new byte[] { 0xFF, 0xF1, 0x50, 0x80, 0x02, 0x20 | 0x1F, 0xFC });
            for (int k = 0; k < 10; k++) adts.Add((byte)(i * 3 + k));
        }
        using (var s = AdtsPacketSource.Open(new MemoryStream(adts.ToArray())))
        {
            if (s.SampleRate != 44100 || s.Channels != 2 || s.LengthFrames != 20 * 1024) return Fail("adts format");
            if (s.CodecConfig.Span[0] != 0x12 || s.CodecConfig.Span[1] != 0x10) return Fail("adts config");
            s.SeekToFrame(1024 * 9, out _);
            if (!s.TryReadPacket(out var p) || p.Data.Length != 10 || p.Data[0] != 27) return Fail("adts seek");
        }

        // 3GP with an AMR-NB track of 12 frames in two chunks.
        byte[] threeGp = BuildThreeGp();
        using (var s = Mp4PacketSource.Open(new MemoryStream(threeGp)))
        {
            if (s.Codec != CodecId.AmrNb || s.SelectedTrack.SampleCount != 12 || s.LengthFrames != 12 * 160) return Fail("3gp track");
            s.SeekToFrame(160 * 8 + 1, out long start);
            if (start != 160 * 8 || !s.TryReadPacket(out var p) || p.Data.Length != 13 || p.Data[1] != 40) return Fail("3gp seek");
        }

        // AddAmr registers the AMR decoders too, so the 3GP opens and decodes; without them a typed error names the codec.
        using (var d = AudioDecoder.Open(new MemoryStream(threeGp), registry))
        {
            var pcm = new float[160];
            if (d.Read(pcm) != 160) return Fail("3gp amr decode");
        }
        try
        {
            using var d = AudioDecoder.Open(new MemoryStream(threeGp), new AudioCodecRegistry().AddMp4());
            return Fail("3gp opened without a codec");
        }
        catch (UnsupportedAudioFormatException ex) when (ex.Message.Contains("amr-nb", StringComparison.Ordinal)) { }
        Console.WriteLine("ok: amr, adts and 3gp containers read and sought, 3gp decoded, and rejected cleanly without a codec");
        return 0;
    }

    private static byte[] BuildThreeGp()
    {
        // Twelve 13-byte frames, six per chunk.
        var media = new List<byte>();
        for (int i = 0; i < 12; i++) { media.Add(0x04); for (int k = 0; k < 12; k++) media.Add((byte)(i * 5 + k)); }
        var ftyp = Box("ftyp", "3gp4"u8.ToArray(), U32(0), "3gp4"u8.ToArray());
        var mdat = Box("mdat", media.ToArray());
        long mdatData = ftyp.Length + 8;
        var samr = Box("samr", new byte[6], new byte[] { 0, 1 }, new byte[8], new byte[] { 0, 1, 0, 16 }, new byte[4], U32(8000u << 16),
            Box("damr", "test"u8.ToArray(), new byte[] { 0, 0x81, 0xFF, 0, 1 }));
        var stbl = Box("stbl",
            Box("stsd", U32(0, 1), samr),
            Box("stts", U32(0, 1, 12, 160)),
            Box("stsc", U32(0, 1, 1, 6, 1)),
            Box("stsz", U32(0, 13, 12)),
            Box("stco", U32(0, 2, (uint)mdatData, (uint)(mdatData + 6 * 13))));
        var mdia = Box("mdia",
            Box("mdhd", U32(0, 0, 0, 8000, 12 * 160, 0)),
            Box("hdlr", U32(0, 0), "soun"u8.ToArray(), new byte[13]),
            Box("minf", Box("smhd", new byte[8]), stbl));
        var moov = Box("moov", Box("mvhd", U32(0, 0, 0, 1000, 1920, 0x10000)), Box("trak", Box("tkhd", U32(7, 0, 0, 1)), mdia));
        return [.. ftyp, .. mdat, .. moov];
    }

    private static byte[] U32(params uint[] v)
    {
        var b = new byte[v.Length * 4];
        for (int i = 0; i < v.Length; i++)
        {
            b[4 * i] = (byte)(v[i] >> 24); b[4 * i + 1] = (byte)(v[i] >> 16); b[4 * i + 2] = (byte)(v[i] >> 8); b[4 * i + 3] = (byte)v[i];
        }
        return b;
    }

    private static byte[] Box(string type, params byte[][] parts)
    {
        int len = 8;
        foreach (var p in parts) len += p.Length;
        var b = new List<byte>(len) { (byte)(len >> 24), (byte)(len >> 16), (byte)(len >> 8), (byte)len };
        b.AddRange(System.Text.Encoding.ASCII.GetBytes(type));
        foreach (var p in parts) b.AddRange(p);
        return b.ToArray();
    }

    private static int Fail(string why)
    {
        Console.Error.WriteLine("FAIL: " + why);
        return 1;
    }
}
