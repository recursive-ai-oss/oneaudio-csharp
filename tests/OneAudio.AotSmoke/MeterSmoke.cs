using OneAudio;

/// <summary>NativeAOT check for per-bus loudness metering and the slot-exhaustion contract.</summary>
internal static class MeterSmoke
{
    public static int Run()
    {
        const int rate = 48_000;
        var tone = new float[rate];
        for (int i = 0; i < tone.Length; i++) tone[i] = 0.1f * MathF.Sin(2 * MathF.PI * 1000 * i / rate);
        var clip = new AudioClip(new AudioFormat(rate, 1), tone);

        var mixer = new AudioMixer(new MixerOptions { SampleRate = rate, Channels = 2, MaxBuses = 3, MaxBusMeters = 1, MaxReverbs = 1 });
        mixer.Play(new PlayParams(clip) { Loop = true, Bus = 1 });
        if (!mixer.EnableBusMeter(1)) return Fail("enable");

        var block = new float[2 * 480];
        for (int pass = 0; pass < 100; pass++) mixer.Render(block);   // one second
        if (!mixer.TryGetBusLoudness(1, out var s)) return Fail("read");
        if (s.FramesMeasured != rate || !(s.MomentaryLufs > -40 && s.MomentaryLufs < -15) || !(s.TruePeakDbtp > -25 && s.TruePeakDbtp < -15))
            return Fail($"readings (M {s.MomentaryLufs}, TP {s.TruePeakDbtp}, frames {s.FramesMeasured})");

        mixer.ResetBusMeter(1);
        if (!mixer.TryGetBusLoudness(1, out s) || s.FramesMeasured != 0) return Fail("reset");

        bool meterLimit = false, reverbLimit = false;
        try { mixer.EnableBusMeter(2); } catch (InvalidOperationException) { meterLimit = true; }
        mixer.SetBusReverb(0, new ReverbSettings());
        try { mixer.SetBusReverb(2, new ReverbSettings()); } catch (InvalidOperationException) { reverbLimit = true; }
        if (!meterLimit || !reverbLimit) return Fail("slot limits");

        mixer.DisableBusMeter(1);
        if (mixer.TryGetBusLoudness(1, out _)) return Fail("disable");
        return 0;
    }

    private static int Fail(string why)
    {
        Console.Error.WriteLine("FAIL: meter: " + why);
        return 1;
    }
}
