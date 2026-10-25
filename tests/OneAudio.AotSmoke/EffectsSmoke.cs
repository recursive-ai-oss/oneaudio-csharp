using OneAudio;

/// <summary>
/// NativeAOT check for the mixer effects: filters on voices, buses and the master output, a reverb, the lookahead limiter, and
/// the streaming position members. Each must change the output the way it should, and the limiter must hold its ceiling.
/// </summary>
internal static class EffectsSmoke
{
    public static int Run()
    {
        const int rate = 48_000;
        var tone = new float[rate];
        for (int i = 0; i < tone.Length; i++) tone[i] = 0.9f * MathF.Sin(2 * MathF.PI * 6000 * i / rate);
        var clip = new AudioClip(new AudioFormat(rate, 1), tone);

        var mixer = new AudioMixer(new MixerOptions { SampleRate = rate, Channels = 2, MaxBuses = 4, MaxReverbs = 1, LimiterLookaheadMilliseconds = 5 });
        if (!mixer.HasLimiter || mixer.LatencyFrames != 240) return Fail("limiter latency");

        var plain = mixer.Play(new PlayParams(clip) { Loop = true, Pan = -1f, Bus = 1 });
        var muffled = mixer.Play(new PlayParams(clip) { Loop = true, Pan = 1f, Bus = 2 });
        if (!plain.IsValid || !muffled.IsValid) return Fail("play");
        mixer.SetBusFilter(2, FilterType.LowPass, 300f, rampFrames: 0);
        mixer.SetVoiceFilter(plain, FilterType.HighPass, 100f, 0.7f, 0);
        mixer.SetBusReverb(AudioMixer.MasterBus, new ReverbSettings { DecaySeconds = 0.4f, Wet = 0.05f });
        mixer.SetLimiter(new LimiterSettings { CeilingDb = -6f });

        var block = new float[2 * 1024];
        float left = 0, right = 0, peak = 0;
        for (int pass = 0; pass < 10; pass++)
        {
            mixer.Render(block);
            if (pass < 4) continue;
            for (int i = 0; i < block.Length; i += 2)
            {
                left = MathF.Max(left, MathF.Abs(block[i]));
                right = MathF.Max(right, MathF.Abs(block[i + 1]));
                peak = MathF.Max(peak, MathF.Max(MathF.Abs(block[i]), MathF.Abs(block[i + 1])));
            }
        }
        if (left < 0.2f || right > 0.05f) return Fail($"filters (left {left}, right {right})");
        if (peak > 0.5013f) return Fail($"limiter ceiling ({peak})");

        mixer.StopAll();
        mixer.ClearBusReverb(AudioMixer.MasterBus);
        mixer.SetLimiter(null);
        mixer.ClearBusFilter(2, FilterType.LowPass);
        mixer.SetDucking(1, 2, DuckingSettings.None);
        for (int pass = 0; pass < 12; pass++) mixer.Render(block);
        foreach (float v in block) if (v != 0f) return Fail("output did not drain");

        using var source = new StreamingSource(new SilentDecoder(), capacityFrames: 256);
        if (source.LengthFrames != 1234 || source.PositionFrames != 0) return Fail("streaming length");
        return 0;
    }

    private static int Fail(string why)
    {
        Console.Error.WriteLine("FAIL: effects: " + why);
        return 1;
    }

    private sealed class SilentDecoder : IAudioDecoder
    {
        public AudioFormat Format { get; } = new(48_000, 1);
        public long LengthFrames => 1234;
        public long PositionFrames => 0;
        public bool CanSeek => false;
        public bool HasNativeIntegers => false;
        public int Read(Span<float> destination) => 0;
        public int ReadInteger(Span<int> destination) => throw new NotSupportedException();
        public void Seek(long frame) => throw new NotSupportedException();
        public void Dispose() { }
    }
}
