using OneAudio;

/// <summary>The one-call layer under NativeAOT: load with conversion, resample, measure, normalise, transcode to every encoder, play.</summary>
internal static class HappySmoke
{
    public static int Run()
    {
        using var flac = Resource("smoke.flac");
        var clip = Audio.Load(flac, new LoadOptions { SampleRate = 44_100, Channels = 1 });
        if (clip.Format.SampleRate != 44_100 || clip.Format.Channels != 1 || clip.Frames < 100) return Fail("load");

        var again = Audio.Resample(clip, 48_000);
        if (again.Format.SampleRate != 48_000 || again.Frames != (long)Math.Round(clip.Frames * 48_000.0 / 44_100)) return Fail("resample");

        var tone = new float[48_000 * 2 * 4];
        for (int i = 0; i < 48_000 * 4; i++) tone[2 * i] = tone[2 * i + 1] = 0.05f * MathF.Sin(2 * MathF.PI * 1000 * i / 48_000);
        var toneFormat = new AudioFormat(48_000, 2);
        var norm = Audio.Normalize(tone, toneFormat);
        var after = Audio.Measure(new AudioClip(toneFormat, tone));
        if (Math.Abs(after.IntegratedLufs - Audio.DefaultTargetLufs) > 0.1 || norm.GainDb <= 0) return Fail($"normalize ({after.IntegratedLufs})");

        foreach (var format in new[] { AudioFileFormat.Wav, AudioFileFormat.Flac, AudioFileFormat.Opus })
        {
            flac.Position = 0;
            using var output = new MemoryStream();
            var r = Audio.Transcode(flac, output, new TranscodeOptions { Format = format });
            if (r.Frames < 100 || output.Length < 100) return Fail($"transcode {format}");
            output.Position = 0;
            if (Audio.Open(output).Format.Channels != r.Input.Channels) return Fail($"reopen {format}");
        }

        var mixer = Audio.CreateMixer();
        if (!mixer.Play(new AudioClip(toneFormat, tone)).IsValid) return Fail("play");
        var block = new float[512 * 2];
        mixer.Render(block);
        if (block.AsSpan().IndexOfAnyExceptInRange(0f, 0f) < 0) return Fail("silent mix");

        Console.WriteLine("ok: happy path load, resample, normalize, transcode (wav, flac, opus), play");
        return 0;
    }

    private static MemoryStream Resource(string name)
    {
        using var s = typeof(HappySmoke).Assembly.GetManifestResourceStream(name)!;
        var m = new MemoryStream();
        s.CopyTo(m);
        m.Position = 0;
        return m;
    }

    private static int Fail(string why)
    {
        Console.Error.WriteLine("FAIL: happy path: " + why);
        return 1;
    }
}
