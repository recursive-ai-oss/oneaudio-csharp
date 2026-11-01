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
        var registry = new AudioCodecRegistry().AddMp4().AddAdts().AddAmr().AddAac();

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

        // AAC-LC decode: 5 silent mono 44.1 kHz frames in ADTS (SCE, gain 0, no bands, END).
        var silent = new List<byte>();
        for (int i = 0; i < 5; i++) { silent.AddRange(new byte[] { 0xFF, 0xF1, 0x50, 0x40, 0x01, 0x7F, 0xFC, 0x00, 0x00, 0x00, 0x07 }); }
        using (var d = AudioDecoder.Open(new MemoryStream(silent.ToArray()), registry))
        {
            var pcm = new float[1024];
            int total = 0, n;
            while ((n = d.Read(pcm)) > 0) { for (int i = 0; i < n; i++) if (pcm[i] != 0f) return Fail("aac not silent"); total += n; }
            if (d.Format.SampleRate != 44100 || d.Format.Channels != 1 || total != 5 * 1024) return Fail("aac decode length " + total);
        }

        // HE-AAC v2: an explicit parametric-stereo config over silent mono frames. Exercises the SBR filter banks and the
        // parametric stereo stages (hybrid filter, decorrelator, rotation) under AOT; silence stays silent.
        {
            var units = new List<byte[]>();
            for (int i = 0; i < 6; i++) units.Add(new byte[] { 0x00, 0x00, 0x00, 0x07 });
            using var d = new AacDecoder(new SilentUnits(units, new byte[] { 0xEB, 0x92, 0x08 }));
            var pcm = new float[2 * 512];
            int total = 0, n;
            while ((n = d.Read(pcm)) > 0) { for (int i = 0; i < n * 2; i++) if (pcm[i] != 0f) return Fail("he-aac not silent"); total += n; }
            if (d.Format.SampleRate != 44100 || d.Format.Channels != 2 || total < 5 * 2048) return Fail("he-aac format/length " + total);
        }

        // Frame-length probe: the silent mono unit is 4 bytes; a cut-off one is incomplete; garbage is not AAC.
        var probe = new AacFrameProbe(new byte[] { 0x12, 0x08 });
        if (probe.Probe(new byte[] { 0x00, 0x00, 0x00, 0x07, 0x55 }, out int unitLength) != AacProbeResult.Frame || unitLength != 4) return Fail("aac probe length");
        if (probe.Probe(new byte[] { 0x00, 0x00 }, out _) != AacProbeResult.Incomplete) return Fail("aac probe incomplete");
        if (probe.Probe(new byte[] { 0xFF, 0xFF, 0xFF, 0xFF }, out _) == AacProbeResult.Frame) return Fail("aac probe garbage");

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

/// <summary>A packet source of prepared HE-AAC access units (2048 output frames each), for decoding a hand-made stream without a container.</summary>
sealed class SilentUnits(List<byte[]> units, byte[] config) : IPacketSource
{
    private int _next;
    public CodecId Codec => CodecId.Aac;
    public long LengthFrames => units.Count * 2048L;
    public bool CanSeek => true;
    public ReadOnlyMemory<byte> CodecConfig => config;
    public bool TryReadPacket(out PacketView packet)
    {
        if (_next >= units.Count) { packet = default; return false; }
        packet = new PacketView(units[_next++], -1, false, 0, 0, 0);
        return true;
    }
    public void SeekToFrame(long frame, out long startFrame)
    {
        _next = (int)Math.Clamp(frame / 2048, 0, units.Count);
        startFrame = _next * 2048L;
    }
    public void Dispose() { }
}
