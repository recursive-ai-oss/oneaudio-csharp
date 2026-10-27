using System.Diagnostics;
using OneAudio;
using OneAudio.Devices;

// Runs the Android device backend in a shell on a phone or emulator: opens the default output the way an application does (no backend named),
// plays a very quiet tone for several seconds, pauses and resumes it, and prints the device counters. Exits non-zero when a check fails.
// Capture is tried only with --input (a shell process may lack the microphone permission, which is reported, not hidden).

static int Fail(string why)
{
    Console.WriteLine("FAIL: " + why);
    return 1;
}

static bool Until(Func<bool> condition, int milliseconds = 4000)
{
    var watch = Stopwatch.StartNew();
    while (watch.ElapsedMilliseconds < milliseconds)
    {
        if (condition())
        {
            return true;
        }

        Thread.Sleep(5);
    }

    return condition();
}

float amplitude = 0.02f; // -34 dBFS
int seconds = 4;
for (int i = 0; i < args.Length - 1; i++)
{
    if (args[i] == "--seconds")
    {
        seconds = int.Parse(args[i + 1]);
    }
}

Console.WriteLine($"android-smoke: arch={System.Runtime.InteropServices.RuntimeInformation.ProcessArchitecture} os={System.Runtime.InteropServices.RuntimeInformation.OSDescription} androidProcess={Environment.GetEnvironmentVariable("ANDROID_ROOT") is not null} tone={20 * Math.Log10(amplitude):F1} dBFS");
var tone = new Tone(48_000, 2, amplitude);
using (var output = AudioOutputDevice.Open(tone, new DeviceStreamOptions { MeasureRenderAllocations = true }))
{
    Console.WriteLine($"backend: {output.BackendName}");
    if (output.BackendName != "aaudio")
    {
        return Fail("the platform did not choose AAudio");
    }

    foreach (var d in AudioDevices.List(DeviceDirection.Output))
    {
        Console.WriteLine($"output device: {d.Name} [{d.Id}]{(d.IsDefault ? " default" : string.Empty)}");
    }

    output.Start();
    Console.WriteLine($"output: state {output.State} {output.LastError}");
    if (output.State != DeviceState.Running)
    {
        return Fail("the output did not start");
    }

    Console.WriteLine("detail: " + output.Detail);
    if (!Until(() => output.Counters.FramesPlayed > 4_800))
    {
        return Fail("nothing played: " + output.Counters);
    }

    for (int s = 1; s <= seconds; s++)
    {
        Thread.Sleep(1000);
        var c = output.Counters;
        Console.WriteLine($"t={s}s played={c.FramesPlayed} written={c.FramesWritten} peakWritten={c.PeakWritten:F4} peakPlayed={c.PeakPlayed:F4} underruns={c.Underruns} deviceUnderruns={c.DeviceUnderruns} allocBytes={c.RenderAllocatedBytes} period={c.PeriodFrames} buffer={c.BufferFrames}");
    }

    var steady = output.Counters;
    if (steady.FramesPlayed < (seconds - 1) * 48_000L || steady.PeakPlayed < 0.01f || steady.PeakWritten > 0.0316f || steady.Underruns != 0 || steady.RenderAllocatedBytes != 0 || steady.CallbackFaults != 0)
    {
        return Fail("steady-state counters " + steady);
    }

    output.Suspend();
    Thread.Sleep(300);
    var held = output.Counters;
    Thread.Sleep(300);
    Console.WriteLine($"paused: state {output.State}, played {held.FramesPlayed} -> {output.Counters.FramesPlayed}");
    if (output.Counters.FramesPlayed != held.FramesPlayed)
    {
        return Fail("sound continued while paused");
    }

    output.Resume();
    if (!Until(() => output.Counters.FramesPlayed > held.FramesPlayed + 9_600))
    {
        return Fail("did not resume: " + output.Counters + " " + output.LastError);
    }

    var resumed = output.Counters;
    Console.WriteLine("resumed: " + resumed);
    if (resumed.Underruns != 0 || resumed.RenderAllocatedBytes != 0 || resumed.CallbackFaults != 0)
    {
        return Fail("counters after resume " + resumed);
    }
}

Console.WriteLine("output closed");

// An id the system does not know: informational, since some systems route an unknown id to the default and others refuse it.
using (var missing = AudioOutputDevice.Open(new Tone(48_000, 2, amplitude), new DeviceStreamOptions { DeviceId = "9999", FollowDefault = false }))
{
    missing.Start();
    Console.WriteLine($"device id 9999: state {missing.State} {missing.LastError} {missing.Detail}");
}

if (args.Contains("--input"))
{
    using var input = AudioInputDevice.Open(new DeviceStreamOptions { SampleRate = 16_000, Channels = 1 });
    input.Start();
    Console.WriteLine($"input: state {input.State} {input.LastError}");
    if (input.State == DeviceState.Running)
    {
        Until(() => input.Counters.FramesWritten > 8_000, 3000);
        Console.WriteLine("input counters: " + input.Counters);
    }
}
else
{
    Console.WriteLine("input: skipped (pass --input to try; the app needs the microphone permission)");
}

Console.WriteLine("android-smoke: ok");
return 0;

internal sealed class Tone(int rate, int channels, float amplitude) : IAudioSource
{
    private long _frame;

    public int SampleRate => rate;

    public int Channels => channels;

    public void Render(Span<float> output)
    {
        int frames = output.Length / channels;
        for (int i = 0; i < frames; i++)
        {
            float v = amplitude * MathF.Sin(2f * MathF.PI * 440f * (_frame + i) / rate);
            for (int c = 0; c < channels; c++)
            {
                output[(i * channels) + c] = v;
            }
        }

        _frame += frames;
    }
}
