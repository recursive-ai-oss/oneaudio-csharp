using OneAudio;
using OneAudio.Devices;

/// <summary>NativeAOT check for the device layer over a manual backend: play a mixer, lose the device, follow the default, capture.</summary>
internal static class DeviceSmoke
{
    public static int Run()
    {
        const int rate = 48_000;
        var tone = new float[rate];
        for (int i = 0; i < tone.Length; i++) tone[i] = 0.3f * MathF.Sin(2 * MathF.PI * 440 * i / rate);
        var clip = new AudioClip(new AudioFormat(rate, 1), tone);
        var mixer = new AudioMixer(new MixerOptions { SampleRate = rate, Channels = 2 });

        var backend = new ManualBackend();
        AudioDevices.UseBackend(backend);
        try
        {
            using var output = Audio.Play(mixer);
            mixer.Play(new PlayParams(clip) { Loop = true });
            output.Advance(4800);
            backend.SimulateLoss();
            output.Pump();
            backend.SetDefault("manual-out-1");
            output.Pump();
            output.Advance(4800);
            var c = output.Counters;
            if (c.FramesPlayed != 9600 || c.PeakPlayed < 0.2f || c.DeviceLosses != 1 || c.DefaultDeviceSwitches != 1 || c.Reopens != 2)
                return Fail("output counters " + c);
            if (output.Device?.Id != "manual-out-1") return Fail("device id");

            backend.SetInputSignal([0.5f, 0.5f], loop: true);
            using var input = Audio.CreateInput();
            input.Advance(480);
            var got = new float[960];
            if (input.Read(got) != 480 || got[0] != 0.5f) return Fail("input");
        }
        finally
        {
            AudioDevices.UseBackend(null);
        }

        // The operating system's own backend, on a PCM that needs no hardware where one exists (ALSA's null device).
        var alsa = new AlsaBackend();
        if (alsa.IsAvailable)
        {
            using var real = AudioOutputDevice.Open(mixer, new DeviceStreamOptions { DeviceId = "null" }, alsa);
            real.Start();
            for (int i = 0; i < 500 && real.Counters.FramesPlayed < 4800; i++) Thread.Sleep(5);
            if (real.State != DeviceState.Running || real.Counters.FramesPlayed < 4800) return Fail("alsa null " + real.Counters + " " + real.LastError);
        }

        if (WasapiSmoke.Run() is var wasapi and not 0) return wasapi;

        _ = AudioDevices.IsAvailable;
        return 0;
    }

    private static int Fail(string why)
    {
        Console.Error.WriteLine("FAIL: devices: " + why);
        return 1;
    }
}
