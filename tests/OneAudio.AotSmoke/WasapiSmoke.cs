using OneAudio;
using OneAudio.Devices;

/// <summary>
/// NativeAOT check of the WASAPI backend on Windows: enumerate, then open the default output (shared and exclusive). A machine without an audio
/// endpoint must end up with no device rather than an error; elsewhere this does nothing.
/// </summary>
internal static class WasapiSmoke
{
    public static int Run()
    {
        if (!OperatingSystem.IsWindows())
        {
            return 0;
        }

        var backend = new WasapiBackend();
        if (!backend.IsAvailable)
        {
            return Fail("the MMDevice enumerator is missing");
        }

        _ = backend.List(DeviceDirection.Output);
        _ = backend.List(DeviceDirection.Input);
        bool hasOutput = backend.GetDefault(DeviceDirection.Output) is not null;
        var mixer = new AudioMixer(new MixerOptions { SampleRate = 48_000, Channels = 2 });
        foreach (var mode in (ReadOnlySpan<ShareMode>)[ShareMode.Shared, ShareMode.Exclusive])
        {
            using var output = AudioOutputDevice.Open(mixer, new DeviceStreamOptions { Mode = mode }, backend);
            output.Start();
            Thread.Sleep(150);
            var state = output.State;
            if (state is not (DeviceState.Running or DeviceState.NoDevice))
            {
                return Fail($"{mode}: state {state}");
            }

            // Shared mode on a machine with an endpoint must play; exclusive may be refused.
            if (mode == ShareMode.Shared && hasOutput && state != DeviceState.Running)
            {
                return Fail("shared output did not start: " + output.LastError);
            }
        }

        return 0;
    }

    private static int Fail(string why)
    {
        Console.Error.WriteLine("FAIL: wasapi: " + why);
        return 1;
    }
}
