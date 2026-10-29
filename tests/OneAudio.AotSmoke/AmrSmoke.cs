using OneAudio;

/// <summary>
/// NativeAOT check for the AMR speech decoders: pseudo-random frames of every mode (valid input for both codecs) go
/// through the frame decoders and through the registry as .amr and .awb files, with a seek.
/// </summary>
internal static class AmrSmoke
{
    private static readonly int[] NbBytes = [12, 13, 15, 17, 19, 20, 26, 31, 5];
    private static readonly int[] WbBytes = [17, 23, 32, 36, 40, 46, 50, 58, 60, 5];

    public static int Run()
    {
        var registry = new AudioCodecRegistry().AddAmr();
        foreach (bool wide in new[] { false, true })
        {
            int[] bytes = wide ? WbBytes : NbBytes;
            int samples = wide ? 320 : 160;
            var file = new MemoryStream();
            file.Write(wide ? "#!AMR-WB\n"u8 : "#!AMR\n"u8);
            var rng = new Random(11);
            const int count = 60;
            for (int i = 0; i < count; i++)
            {
                int ft = i % bytes.Length;
                var frame = new byte[1 + bytes[ft]];
                rng.NextBytes(frame);
                frame[0] = (byte)((ft << 3) | 4);
                file.Write(frame);
            }
            using var decoder = AudioDecoder.Open(new MemoryStream(file.ToArray()), registry);
            if (decoder.Format.SampleRate != (wide ? 16_000 : 8_000) || decoder.Format.Channels != 1) return Fail("amr format");
            if (decoder.LengthFrames != count * samples) return Fail($"amr length {decoder.LengthFrames}");
            var block = new float[samples * 3];
            long frames = 0;
            int n;
            while ((n = decoder.Read(block)) > 0) frames += n;
            if (frames != count * samples) return Fail($"amr decoded {frames}");
            decoder.Seek(25 * samples + 3);
            if (decoder.Read(block) != block.Length) return Fail("amr read after seek");
        }
        Console.WriteLine("amr smoke ok");
        return 0;
    }

    private static int Fail(string what)
    {
        Console.Error.WriteLine("FAIL: " + what);
        return 1;
    }
}
