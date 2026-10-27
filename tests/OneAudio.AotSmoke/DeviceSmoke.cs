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

        // A device at another rate than the source: converted in the package, stepped by the caller, no thread.
        {
            var odd = new ManualBackend { DeviceSampleRate = 44_100 };
            using var converted = AudioOutputDevice.Open(mixer, null, odd);
            converted.Start();
            if (converted.Advance(4410) != 4410 || converted.Counters.FramesWritten != 4410 || converted.DeviceFormat.SampleRate != 44_100) return Fail("rate conversion");
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

        // Core Audio on an Apple system: list the devices and, where there is a default output, run it for a moment, very quietly.
        if (OperatingSystem.IsMacOS() || OperatingSystem.IsIOS())
        {
            var apple = new CoreAudioBackend();
            if (!apple.IsAvailable) return Fail("coreaudio unavailable");
            if (apple.GetDefault(DeviceDirection.Output) is not null)
            {
                if (apple.List(DeviceDirection.Output).Count == 0) return Fail("coreaudio lists no outputs");
                var quiet = new AudioMixer(new MixerOptions { SampleRate = rate, Channels = 2 });
                var hum = new float[rate];
                for (int i = 0; i < hum.Length; i++) hum[i] = 0.003f * MathF.Sin(2 * MathF.PI * 440 * i / rate);
                quiet.Play(new PlayParams(new AudioClip(new AudioFormat(rate, 1), hum)) { Loop = true });
                using var real = AudioOutputDevice.Open(quiet, new DeviceStreamOptions { MeasureRenderAllocations = true }, apple);
                real.Start();
                for (int i = 0; i < 400 && real.Counters.FramesPlayed < 4800; i++) Thread.Sleep(5);
                var rc = real.Counters;
                if (real.State == DeviceState.Running && (rc.FramesPlayed < 4800 || rc.RenderAllocatedBytes != 0 || rc.CallbackFaults != 0)) return Fail("coreaudio " + rc);
                Console.WriteLine("coreaudio: " + real.State + " " + rc);
            }
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
