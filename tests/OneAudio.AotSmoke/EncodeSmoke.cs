using OneAudio;
using OneAudio.Flac;

/// <summary>WAV and FLAC encoders under NativeAOT: write, read back through the decoders, compare.</summary>
internal static class EncodeSmoke
{
    public static int Run()
    {
        const int Rate = 44_100, Frames = 20_000;
        var pcm = new int[Frames * 2];
        for (int i = 0; i < Frames; i++)
        {
            pcm[2 * i] = (int)(MathF.Sin(2 * MathF.PI * 440 * i / Rate) * 12000) + (i % 7);
            pcm[2 * i + 1] = (int)(MathF.Sin(2 * MathF.PI * 660 * i / Rate) * 9000) - (i % 5);
        }

        // WAV: 24-bit extensible, exact integers back.
        var wav = new MemoryStream();
        using (var w = new WavEncoder(wav, new AudioFormat(Rate, 2), new WavEncoderSettings { SampleType = NativeSampleType.PcmSigned24 }, leaveOpen: true))
        {
            var wide = new int[pcm.Length];
            for (int i = 0; i < pcm.Length; i++) wide[i] = pcm[i] * 256;
            w.WriteInteger(wide);
            w.Finish();
        }
        wav.Position = 0;
        var wavRegistry = new AudioCodecRegistry().AddWav();
        using (var wd = AudioDecoder.Open(wav, wavRegistry))
        {
            var back = new int[pcm.Length];
            if (wd.Format.NativeType != NativeSampleType.PcmSigned24 || wd.ReadInteger(back) != Frames) return Fail("wav read");
            for (int i = 0; i < pcm.Length; i++) if (back[i] != pcm[i] * 256) return Fail("wav samples");
        }

        // FLAC at the fastest and the smallest level, plus float input and the decoder convenience.
        int[] sizes = new int[2];
        int k = 0;
        foreach (int level in new[] { 0, 8 })
        {
            var flac = new MemoryStream();
            using (var enc = new FlacEncoder(flac, new AudioFormat(Rate, 2), new FlacEncoderSettings { BitsPerSample = 16, CompressionLevel = level }, leaveOpen: true))
            {
                enc.WriteInteger(pcm);
                enc.Finish();
            }
            sizes[k++] = (int)flac.Length;
            flac.Position = 0;
            using var fd = FlacDecoder.Open(flac);
            if (fd.VerifyMd5() != true) return Fail("flac md5");
            var back = new int[pcm.Length];
            if (fd.ReadInteger(back) != Frames || !back.AsSpan().SequenceEqual(pcm)) return Fail("flac samples level " + level);
        }
        if (sizes[1] >= sizes[0]) return Fail($"level 8 ({sizes[1]}) is not smaller than level 0 ({sizes[0]})");

        var floats = new float[pcm.Length];
        for (int i = 0; i < floats.Length; i++) floats[i] = pcm[i] / 32768f;
        var ff = new MemoryStream();
        using (var enc = new FlacEncoder(ff, new AudioFormat(Rate, 2), new FlacEncoderSettings { BitsPerSample = 16 }, leaveOpen: true))
        {
            enc.Write(floats);
            enc.Finish();
        }
        ff.Position = 0;
        var viaDecoder = new MemoryStream();
        using (var fd = FlacDecoder.Open(ff))
        {
            var back = new int[pcm.Length];
            if (fd.ReadInteger(back) != Frames || !back.AsSpan().SequenceEqual(pcm)) return Fail("flac float input");
            fd.Seek(0);
            if (FlacEncoder.Encode(fd, viaDecoder, leaveOpen: true) != Frames) return Fail("encode from decoder");
        }
        viaDecoder.Position = 0;
        using (var fd = FlacDecoder.Open(viaDecoder))
            if (fd.VerifyMd5() != true) return Fail("re-encoded md5");

        // Packet-level FLAC: frames out of the packet encoder decode bit-exactly as an Ogg-FLAC-style packet stream.
        using (var pe = new FlacPacketEncoder(new AudioFormat(Rate, 2), new FlacEncoderSettings { BitsPerSample = 16 }))
        {
            var packets = new List<byte[]>();
            var rest = pcm.AsSpan();
            while (rest.Length > 0)
            {
                int taken = pe.WriteInteger(rest);
                rest = rest[(taken * 2)..];
                while (pe.TryReadPacket(out var p)) packets.Add(p.Data.ToArray());
            }
            pe.Finish();
            while (pe.TryReadPacket(out var p)) packets.Add(p.Data.ToArray());
            var native = new MemoryStream();
            native.Write("fLaC"u8);
            native.Write(pe.GetStreamInfoBlock());
            foreach (var b in packets) native.Write(b);
            native.Position = 0;
            using var pd = FlacDecoder.Open(native);
            var back = new int[pcm.Length];
            if (pd.VerifyMd5() != true) return Fail("packet flac md5");
            if (pd.ReadInteger(back) != Frames || !back.AsSpan().SequenceEqual(pcm)) return Fail("packet flac samples");
        }

        // AAC-LC: encode a tone to an MP4 in memory, read it back through the registered MP4 and AAC decoders.
        {
            var tone = new float[Rate * 2 * 2];
            for (int i = 0; i < Rate * 2; i++)
            {
                tone[2 * i] = 0.4f * MathF.Sin(2 * MathF.PI * 440 * i / Rate);
                tone[2 * i + 1] = 0.3f * MathF.Sin(2 * MathF.PI * 1320 * i / Rate);
            }
            var enc = new OneAudio.Aac.AacEncoder(Rate, 2, new OneAudio.Aac.AacEncoderSettings { Bitrate = 96_000 });
            var mp4 = new MemoryStream();
            using (var mw = OneAudio.Mp4.Mp4AudioWriter.CreateAac(mp4, Rate, 2, enc.AudioSpecificConfig, OneAudio.Aac.AacEncoder.PrimingFrames, leaveOpen: true))
            {
                var left = tone.AsSpan();
                while (left.Length > 0)
                {
                    int taken = enc.Write(left);
                    left = left[(taken * 2)..];
                    while (enc.TryReadPacket(out var p)) mw.AddSample(p.Data.Span, p.Duration);
                }
                enc.Finish();
                while (enc.TryReadPacket(out var p)) mw.AddSample(p.Data.Span, p.Duration);
                mw.Finish(enc.FramesWritten);
            }
            mp4.Position = 0;
            using var ad = AudioDecoder.Open(mp4, new AudioCodecRegistry().AddMp4().AddAac());
            var got = new float[tone.Length];
            int filled = 0, n;
            while (filled < got.Length && (n = ad.Read(got.AsSpan(filled))) > 0) filled += n * 2;
            if (filled != tone.Length || ad.LengthFrames != Rate * 2) return Fail("aac length");
            double err = 0, sig = 0;
            for (int i = 0; i < tone.Length; i++) { err += (got[i] - tone[i]) * (double)(got[i] - tone[i]); sig += tone[i] * (double)tone[i]; }
            if (10 * Math.Log10(sig / err) < 20) return Fail("aac snr");
        }

        Console.WriteLine($"ok: encoders wrote wav, flac (level 0 {sizes[0]} bytes, level 8 {sizes[1]} bytes), packet flac and an AAC-LC MP4, and all read back");
        return 0;
    }

    private static int Fail(string why)
    {
        Console.Error.WriteLine("FAIL: encode " + why);
        return 1;
    }
}
