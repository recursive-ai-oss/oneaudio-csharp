using OneAudio;

/// <summary>
/// NativeAOT check for the MP3 path: a stream of 40 hand-built MPEG-1 Layer III frames (one Huffman pair each, so
/// the frame finder, side information, Huffman, requantization, IMDCT and polyphase paths all run) is opened through
/// the format-detecting entry point, decoded, sought and played through the mixer.
/// </summary>
internal static class Mp3Smoke
{
    public static int Run()
    {
        const int frameCount = 40;
        byte[] file = BuildFile(frameCount);
        var registry = new AudioCodecRegistry().AddMp3();
        using var decoder = AudioDecoder.Open(new MemoryStream(file), registry);
        if (decoder.Format.SampleRate != 44_100 || decoder.Format.Channels != 2) return Fail("mp3 format");
        if (!decoder.CanSeek || decoder.LengthFrames != frameCount * 1152) return Fail($"mp3 length {decoder.LengthFrames}");

        var block = new float[1024 * 2];
        long frames = 0;
        float peak = 0;
        int n;
        while ((n = decoder.Read(block)) > 0)
        {
            frames += n;
            for (int i = 0; i < n * 2; i++) peak = MathF.Max(peak, MathF.Abs(block[i]));
        }
        if (frames != decoder.LengthFrames) return Fail($"mp3 decoded {frames} of {decoder.LengthFrames} frames");
        if (!(peak > 0f) || !float.IsFinite(peak)) return Fail($"mp3 peak {peak}");

        decoder.Seek(20 * 1152 + 77);
        if (decoder.PositionFrames != 20 * 1152 + 77 || decoder.Read(block) <= 0) return Fail("mp3 read after seek");
        decoder.Seek(0);

        var clip = AudioClip.FromDecoder(decoder);
        var mixer = new AudioMixer(new MixerOptions { SampleRate = 44_100, Channels = 2 });
        if (!mixer.Play(new PlayParams(clip) { Gain = 0.5f }).IsValid) return Fail("mp3 play");
        var out2 = new float[512 * 2];
        float mixPeak = 0;
        for (int pass = 0; pass < 8; pass++)
        {
            mixer.Render(out2);
            foreach (float s in out2) mixPeak = MathF.Max(mixPeak, MathF.Abs(s));
        }
        if (!(mixPeak > 0f)) return Fail("mp3 mix is silent");

        Console.WriteLine($"ok: mp3 {frames} frames decoded, mixed peak {mixPeak:F3}");
        return 0;
    }

    private static byte[] BuildFile(int count)
    {
        var ms = new MemoryStream();
        for (int f = 0; f < count; f++)
        {
            var frame = new byte[417];
            frame[0] = 0xFF; frame[1] = 0xFB; frame[2] = 0x90; frame[3] = 0x00; // MPEG-1 Layer III, 128 kbit/s, 44.1 kHz, stereo, no padding
            var bits = new BitWriter(frame, 4);
            bits.Put(0, 9);  // main_data_begin
            bits.Put(0, 3);  // private bits
            bits.Put(0, 8);  // scfsi, both channels
            for (int gr = 0; gr < 2; gr++)
                for (int ch = 0; ch < 2; ch++)
                {
                    bool coded = ch == 0;
                    bits.Put(coded ? 3 : 0, 12);          // part2_3_length: one pair, code '01' and one sign bit
                    bits.Put(coded ? 1 : 0, 9);           // big_values
                    bits.Put(200, 8);                     // global_gain
                    bits.Put(0, 4);                       // scalefac_compress
                    bits.Put(0, 1);                       // window_switching_flag
                    bits.Put(coded ? 1 : 0, 5);           // table_select[0]
                    bits.Put(0, 10);                      // table_select[1..2]
                    bits.Put(0, 4); bits.Put(0, 3);       // region counts
                    bits.Put(0, 1); bits.Put(0, 1); bits.Put(0, 1); // preflag, scalefac_scale, count1table_select
                }
            // main data: granule 0 channel 0 holds '01' + sign '0' (x = +1, y = 0 in Huffman table 1).
            // Granule 1 repeats it; its own side information carries the same lengths.
            int mainStart = 4 + 32;
            bits = new BitWriter(frame, mainStart);
            bits.Put(0b010, 3);
            bits.Put(0b010, 3);
            ms.Write(frame);
        }
        return ms.ToArray();
    }

    private sealed class BitWriter(byte[] data, int startByte)
    {
        private int _bit = startByte * 8;
        public void Put(int value, int count)
        {
            for (int i = count - 1; i >= 0; i--, _bit++)
                if (((value >> i) & 1) != 0) data[_bit >> 3] |= (byte)(0x80 >> (_bit & 7));
        }
    }

    private static int Fail(string why)
    {
        Console.Error.WriteLine("FAIL: " + why);
        return 1;
    }
}
