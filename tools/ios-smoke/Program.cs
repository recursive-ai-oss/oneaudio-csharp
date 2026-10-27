using System.Diagnostics;
using OneAudio;
using OneAudio.Devices;

// Runs the Apple device backend inside an iOS simulator app: lists the route, plays a very quiet tone through the session, pushes an
// interruption through the notification path and checks the stream pauses and resumes, then tries to capture. Prints one line per step and
// exits non-zero when a check fails.

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

// Naming a platform type also gives the iOS toolchain the platform assembly it expects to find.
Console.WriteLine($"ios-smoke: IsIOS={OperatingSystem.IsIOS()} IsMacOS={OperatingSystem.IsMacOS()} arch={System.Runtime.InteropServices.RuntimeInformation.ProcessArchitecture} iOS {UIKit.UIDevice.CurrentDevice.SystemVersion}");
var backend = new CoreAudioBackend();
if (!backend.IsAvailable)
{
    return Fail("the backend is not available");
}

foreach (var direction in new[] { DeviceDirection.Output, DeviceDirection.Input })
{
    foreach (var d in backend.List(direction))
    {
        Console.WriteLine($"{direction}: {d.Name} [{d.Id}] {d.DefaultSampleRate} Hz x{d.MaxChannels}{(d.IsDefault ? " default" : string.Empty)}");
    }
}

var source = new Tone(48_000, 2, 0.003f);
using (var output = AudioOutputDevice.Open(source, new DeviceStreamOptions { AppleSession = AppleSessionCategory.Playback, MeasureRenderAllocations = true }, backend))
{
    output.Start();
    Console.WriteLine($"output: state {output.State} {output.LastError}");
    if (output.State != DeviceState.Running)
    {
        return Fail("the output did not start");
    }

    if (!Until(() => output.Counters.FramesPlayed > 9_600))
    {
        return Fail("nothing played: " + output.Counters);
    }

    var running = output.Counters;
    Console.WriteLine("output running: " + running);
    Console.WriteLine("detail: " + output.Detail);
    if (running.PeakPlayed < 0.002f || running.Underruns != 0 || running.RenderAllocatedBytes != 0)
    {
        return Fail("counters " + running);
    }

    // Interruption: the system posts the same notification the smoke posts here.
    CoreAudioBackend.SimulateInterruption(true);
    if (!Until(() => output.State == DeviceState.Suspended || HasEvent(output, DeviceEventKind.Interrupted)))
    {
        return Fail("the interruption never arrived: " + output.State);
    }

    Thread.Sleep(400); // the stream drains what the hardware still holds before it settles
    var held = output.Counters;
    Thread.Sleep(150);
    Console.WriteLine($"interrupted: state {output.State}, played {held.FramesPlayed} -> {output.Counters.FramesPlayed}");
    if (output.Counters.FramesPlayed != held.FramesPlayed)
    {
        return Fail("sound continued during the interruption");
    }

    CoreAudioBackend.SimulateInterruption(false);
    if (!Until(() => output.Counters.FramesPlayed > held.FramesPlayed + 9_600))
    {
        return Fail("did not resume after the interruption: " + output.Counters + " " + output.LastError);
    }

    var resumed = output.Counters;
    Console.WriteLine("resumed: " + resumed);
    if (resumed.Underruns != 0 || resumed.RenderAllocatedBytes != 0 || resumed.CallbackFaults != 0)
    {
        return Fail("counters after resume " + resumed);
    }
}

using (var input = AudioInputDevice.Open(new DeviceStreamOptions { SampleRate = 16_000, Channels = 1 }, backend))
{
    input.Start();
    Console.WriteLine($"input: state {input.State} {input.LastError}");
    if (input.State == DeviceState.Running)
    {
        Until(() => input.Counters.FramesWritten > 8_000, 3000);
        Console.WriteLine("input counters: " + input.Counters);
    }
}

Console.WriteLine("ios-smoke: ok");
return 0;

static bool HasEvent(AudioOutputDevice device, DeviceEventKind kind)
{
    while (device.TryGetEvent(out var e))
    {
        if (e.Kind == kind)
        {
            return true;
        }
    }

    return false;
}

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
