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

        Console.WriteLine($"ok: encoders wrote wav and flac (level 0 {sizes[0]} bytes, level 8 {sizes[1]} bytes) and both read back exactly");
        return 0;
    }

    private static int Fail(string why)
    {
        Console.Error.WriteLine("FAIL: encode " + why);
        return 1;
    }
}
