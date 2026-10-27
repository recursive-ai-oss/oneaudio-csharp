using System.Diagnostics;
using OneAudio.Devices;
using OneAudio.Devices.Interop;
using Xunit;

namespace OneAudio.Devices.Tests;

/// <summary>
/// The Apple backend. Channel mapping runs everywhere; enumeration and refusals run on macOS; playing and recording on the real devices
/// (a few seconds at -50 dBFS) run only when ONEAUDIO_DEVICE_TESTS=1, and the test that moves the system default output to another device
/// and back only when ONEAUDIO_DEVICE_SWITCH_TEST=1.
/// </summary>
public class AppleTests
{
    private static void RequireMac() => Assert.SkipUnless(OperatingSystem.IsMacOS(), "needs macOS");

    private static void RequireRealDevices()
    {
        RequireMac();
        Assert.SkipUnless(Environment.GetEnvironmentVariable("ONEAUDIO_DEVICE_TESTS") == "1", "set ONEAUDIO_DEVICE_TESTS=1 to play on the real default device");
    }

    private static void Log(string line)
    {
        string? log = Environment.GetEnvironmentVariable("ONEAUDIO_DEVICE_TESTS_LOG");
        if (log is not null)
        {
            File.AppendAllText(log, line + Environment.NewLine);
        }
    }

    [Fact]
    public void ChannelMap_FollowsTheDocumentedRules()
    {
        var mono = new float[] { 0.25f, -0.5f };
        var stereo = new float[4];
        AppleChannelMap.Convert(mono, 1, stereo, 2, 2);
        Assert.Equal(new[] { 0.25f, 0.25f, -0.5f, -0.5f }, stereo);

        var six = new float[12];
        AppleChannelMap.Convert(stereo, 2, six, 6, 2);
        Assert.Equal(new[] { 0.25f, 0.25f, 0, 0, 0, 0, -0.5f, -0.5f, 0, 0, 0, 0 }, six);

        var back = new float[4];
        AppleChannelMap.Convert(six, 6, back, 2, 2);
        Assert.Equal(stereo, back);

        var average = new float[2];
        AppleChannelMap.Convert(new float[] { 1f, 0f, 0.5f, 0.5f }, 2, average, 1, 2);
        Assert.Equal(new[] { 0.5f, 0.5f }, average);

        var same = new float[4];
        AppleChannelMap.Convert(stereo, 2, same, 2, 2);
        Assert.Equal(stereo, same);

        // A mono destination from many channels averages them all.
        var one = new float[1];
        AppleChannelMap.Convert(new float[] { 0.6f, 0.3f, 0f }, 3, one, 1, 1);
        Assert.Equal(0.3f, one[0], 6);
    }

    [Fact]
    public void CaptureConversion_MapsChannelsAndResamplesToTheRequestedFormat()
    {
        // A 48 kHz stereo device read as 16 kHz mono: a 1 kHz tone on both channels stays a 1 kHz tone, one third as many frames.
        var engine = new CollectingEngine();
        var stream = AppleStream.CreateDetachedCapture(engine, new AudioDeviceFormat(16_000, 1), 48_000);
        const int Frames = 48_000;
        var block = new float[Frames * 2];
        for (int i = 0; i < Frames; i++)
        {
            block[i * 2] = block[(i * 2) + 1] = 0.5f * MathF.Sin(2f * MathF.PI * 1000f * i / 48_000f);
        }

        for (int at = 0; at < Frames; at += 480)
        {
            stream.DeliverInput(block.AsSpan(at * 2, 960), 480);
        }

        Assert.InRange(engine.Captured.Count, 15_000, 16_000);
        Assert.Equal(engine.Captured.Count, stream.FramesPlayed);
        var got = engine.Captured.ToArray();
        double peak = 0;
        for (int i = 2000; i < got.Length; i++)
        {
            peak = Math.Max(peak, Math.Abs(got[i]));
        }

        Assert.InRange(peak, 0.48, 0.52);

        // Zero crossings give the frequency: 1 kHz at 16 kHz is one every 8 frames.
        int crossings = 0;
        for (int i = 2001; i < got.Length; i++)
        {
            if ((got[i - 1] < 0) != (got[i] < 0))
            {
                crossings++;
            }
        }

        double hz = crossings / 2.0 / ((got.Length - 2001) / 16_000.0);
        Assert.InRange(hz, 990, 1010);
    }

    [Fact]
    public void CaptureConversion_AtTheSameRate_PassesTheBlockThroughUntouched()
    {
        var engine = new CollectingEngine();
        var stream = AppleStream.CreateDetachedCapture(engine, new AudioDeviceFormat(48_000, 2), 48_000);
        float[] block = [0.1f, -0.1f, 0.2f, -0.2f, 0.3f, -0.3f];
        stream.DeliverInput(block, 3);
        Assert.Equal(block, engine.Captured.ToArray());
    }

    [Fact]
    public void OffApple_TheBackendIsUnavailable_AndOpensNothing()
    {
        Assert.SkipWhen(OperatingSystem.IsMacOS() || OperatingSystem.IsIOS(), "this is an Apple system");
        var backend = new CoreAudioBackend();
        Assert.False(backend.IsAvailable);
        Assert.Empty(backend.List(DeviceDirection.Output));
        Assert.Null(backend.GetDefault(DeviceDirection.Output));
        Assert.False(backend.TryOpen(DeviceDirection.Output, new DeviceStreamOptions(), new NullEngine(), out var stream, out string reason));
        Assert.Null(stream);
        Assert.NotEmpty(reason);
    }

    [Fact]
    public void Enumeration_ListsOutputDevices_WithOneDefault()
    {
        RequireMac();
        var backend = new CoreAudioBackend();
        Assert.True(backend.IsAvailable);
        var outputs = backend.List(DeviceDirection.Output);
        Assert.NotEmpty(outputs);
        Assert.All(outputs, d =>
        {
            Assert.False(string.IsNullOrEmpty(d.Id));
            Assert.False(string.IsNullOrEmpty(d.Name));
            Assert.True(d.MaxChannels > 0);
            Assert.True(d.DefaultSampleRate >= 8000);
            Assert.Equal(DeviceBackendKind.CoreAudio, d.Backend);
        });
        Assert.Equal(outputs.Count, outputs.Select(d => d.Id).Distinct().Count());
        var defaults = outputs.Where(d => d.IsDefault).ToArray();
        Assert.Single(defaults);
        Assert.Equal(defaults[0].Id, backend.GetDefault(DeviceDirection.Output)?.Id);
        Log("outputs: " + string.Join("; ", outputs.Select(d => $"{d.Name} [{d.Id}] {d.DefaultSampleRate} Hz x{d.MaxChannels} min {d.MinLatencyFrames}{(d.IsDefault ? " default" : string.Empty)}")));

        var inputs = backend.List(DeviceDirection.Input); // a Mac mini has none; only the call must work
        Log("inputs: " + string.Join("; ", inputs.Select(d => $"{d.Name} [{d.Id}] {d.DefaultSampleRate} Hz x{d.MaxChannels}{(d.IsDefault ? " default" : string.Empty)}")));
        if (inputs.Count > 0)
        {
            Assert.Single(inputs, d => d.IsDefault);
        }
    }

    [Fact]
    public void TryOpen_RefusesWithAReason_ExclusiveModeAndUnknownDevices()
    {
        RequireMac();
        var backend = new CoreAudioBackend();
        Assert.False(backend.TryOpen(DeviceDirection.Output, new DeviceStreamOptions { Mode = ShareMode.Exclusive }, new NullEngine(), out var stream, out string reason));
        Assert.Null(stream);
        Assert.Contains("exclusive", reason, StringComparison.OrdinalIgnoreCase);

        Assert.False(backend.TryOpen(DeviceDirection.Output, new DeviceStreamOptions { DeviceId = "no-such-device-uid" }, new NullEngine(), out stream, out reason));
        Assert.Null(stream);
        Assert.Contains("no-such-device-uid", reason, StringComparison.Ordinal);
    }

    [Fact]
    public void OpenStream_ReportsTheRequestedFormat_AndANonZeroPeriod()
    {
        RequireMac();
        var backend = new CoreAudioBackend();
        // An unstarted unit makes no sound, so this is safe to run everywhere.
        Assert.True(backend.TryOpen(DeviceDirection.Output, new DeviceStreamOptions { SampleRate = 44_100, Channels = 1 }, new NullEngine(), out var stream, out string reason), reason);
        using (stream)
        {
            Assert.Equal(new AudioDeviceFormat(44_100, 1), stream!.Format);
            Assert.True(stream.PeriodFrames > 0 && stream.BufferFrames >= stream.PeriodFrames);
            Assert.Equal(0, stream.FramesPlayed);
            Assert.NotNull(stream.Detail);
            Log("detail: " + stream.Detail + $", period {stream.PeriodFrames}, buffer {stream.BufferFrames}");
        }
    }

    [Fact]
    public void RealOutput_PlaysAQuietTone_AndThePositionComesFromTheDeviceClock()
    {
        RequireRealDevices();
        var tone = new ToneSource(48_000, 2, 0.003f); // -50 dBFS
        using var output = AudioOutputDevice.Open(tone, new DeviceStreamOptions { MeasureRenderAllocations = true }, new CoreAudioBackend());
        var wall = Stopwatch.StartNew();
        output.Start();
        Assert.Equal(DeviceState.Running, output.State);
        Assert.True(Wait.Until(() => output.Counters.FramesPlayed > 12_000), "the device never took the tone: " + output.Counters + " " + output.LastError);
        Thread.Sleep(800);

        // While running, the device holds a few periods of audio: played trails written by a bounded amount and is not equal to it.
        // Sampled across several periods, because the lag swings by one period between callbacks.
        long minLag = long.MaxValue;
        long maxLag = 0;
        DeviceCounters mid = output.Counters;
        for (int i = 0; i < 100; i++)
        {
            mid = output.Counters;
            long lag = mid.FramesWritten - mid.FramesPlayed;
            minLag = Math.Min(minLag, lag);
            maxLag = Math.Max(maxLag, lag);
            Thread.Sleep(2);
        }

        Log($"running lag {minLag}..{maxLag} frames, period {mid.PeriodFrames}, buffer {mid.BufferFrames}: {mid}");
        Assert.InRange(maxLag, mid.PeriodFrames / 2, (mid.PeriodFrames * 6) + 4800);

        double seconds = wall.Elapsed.TotalSeconds;
        output.Dispose();
        var c = output.Counters;
        Log($"{seconds:F3} s wall, {c}");
        Assert.True(c.FramesPlayed > 48_000 * 0.8, c.ToString());
        Assert.InRange(c.FramesPlayed / 48_000.0, seconds - 0.5, seconds + 0.05);
        Assert.InRange(c.PeakPlayed, 0.002f, 0.0031f);
        Assert.Equal(0, c.Underruns);
        Assert.Equal(0, c.CallbackFaults);
        Assert.Equal(0, c.RenderAllocatedBytes);
        Assert.Equal("coreaudio", output.BackendName);
    }

    [Fact]
    public void RealOutput_AtAnotherRateAndChannelCount_StillRunsAtTheRequestedFormat()
    {
        RequireRealDevices();
        var tone = new ToneSource(22_050, 1, 0.003f);
        using var output = AudioOutputDevice.Open(tone, new DeviceStreamOptions { MeasureRenderAllocations = true }, new CoreAudioBackend());
        output.Start();
        Assert.True(Wait.Until(() => output.Counters.FramesPlayed > 8_000), "nothing played: " + output.Counters + " " + output.LastError);
        Thread.Sleep(500);
        var c = output.Counters;
        Log("22.05 kHz mono: " + c);
        Assert.InRange(c.PeakPlayed, 0.002f, 0.0031f);
        Assert.Equal(0, c.Underruns);
        Assert.Equal(0, c.RenderAllocatedBytes);
    }

    [Fact]
    public void RealOutput_SuspendDrains_AndResumeContinuesTheCount()
    {
        RequireRealDevices();
        using var output = AudioOutputDevice.Open(new ToneSource(48_000, 2, 0.003f), new DeviceStreamOptions(), new CoreAudioBackend());
        output.Start();
        Assert.True(Wait.Until(() => output.Counters.FramesPlayed > 9_600), output.Counters.ToString());
        output.Suspend();
        var frozen = output.Counters;
        Log("suspended: " + frozen);
        Assert.Equal(DeviceState.Suspended, output.State);
        Assert.Equal(frozen.FramesWritten, frozen.FramesPlayed);
        Thread.Sleep(100);
        Assert.Equal(frozen.FramesPlayed, output.Counters.FramesPlayed);
        output.Resume();
        Assert.True(Wait.Until(() => output.Counters.FramesPlayed > frozen.FramesPlayed + 9_600), output.Counters.ToString());
    }

    [Fact]
    public void RealInput_CapturesFromTheDefaultMicrophone_OrExplainsWhyNot()
    {
        RequireRealDevices();
        var backend = new CoreAudioBackend();
        Assert.SkipWhen(backend.GetDefault(DeviceDirection.Input) is null, "this machine has no input device");
        using var input = AudioInputDevice.Open(new DeviceStreamOptions { SampleRate = 16_000, Channels = 1 }, backend);
        input.Start();
        if (input.State != DeviceState.Running)
        {
            // Permission is the usual reason; the message must say so rather than leave the program guessing.
            Log("capture not started: " + input.LastError);
            Assert.False(string.IsNullOrEmpty(input.LastError));
            return;
        }

        Assert.True(Wait.Until(() => input.Counters.FramesWritten > 8_000, 4000), "nothing captured: " + input.Counters + " " + input.LastError);
        var got = new float[16_000];
        int frames = input.Read(got);
        var c = input.Counters;
        Log($"capture: read {frames} frames, {c}, device {input.Device?.Name}");
        Assert.True(frames > 0);
        Assert.Equal(0, c.CallbackFaults);
    }

    [Fact]
    public void RealDefaultChange_MovesTheStreamToTheNewDevice_AndBack()
    {
        RequireRealDevices();
        Assert.SkipUnless(Environment.GetEnvironmentVariable("ONEAUDIO_DEVICE_SWITCH_TEST") == "1", "set ONEAUDIO_DEVICE_SWITCH_TEST=1 to move the system default output to another device and back");
        var backend = new CoreAudioBackend();
        var outputs = backend.List(DeviceDirection.Output);
        var original = outputs.FirstOrDefault(d => d.IsDefault);
        var other = outputs.FirstOrDefault(d => !d.IsDefault);
        Assert.SkipWhen(original.Id is null || other.Id is null, "needs two output devices");
        uint originalObject = Hal.Devices().First(d => d.Uid == original.Id).Object;
        uint otherObject = Hal.Devices().First(d => d.Uid == other.Id).Object;

        using var output = AudioOutputDevice.Open(new ToneSource(48_000, 2, 0.003f), new DeviceStreamOptions { MeasureRenderAllocations = true }, backend);
        try
        {
            output.Start();
            Assert.True(Wait.Until(() => output.Counters.FramesPlayed > 9_600), output.Counters.ToString());
            Assert.Equal(original.Id, output.Device?.Id);

            Assert.True(Hal.SetDefaultDevice(true, otherObject));
            Assert.True(Wait.Until(() => output.Device?.Id == other.Id && output.Counters.Reopens >= 1, 8000), "did not follow: " + output.Counters + " " + output.LastError);
            long afterSwitch = output.Counters.FramesPlayed;
            Assert.True(Wait.Until(() => output.Counters.FramesPlayed > afterSwitch + 9_600), "no sound after the switch: " + output.Counters);
            Log("after switch: " + output.Counters);
        }
        finally
        {
            Hal.SetDefaultDevice(true, originalObject);
        }

        Assert.True(Wait.Until(() => output.Device?.Id == original.Id, 8000), "did not follow back: " + output.Counters);
        var c = output.Counters;
        Log("after switching back: " + c);
        Assert.True(c.DefaultDeviceSwitches >= 2, c.ToString());
        Assert.Equal(0, c.RenderAllocatedBytes);
    }

    private sealed class CollectingEngine : IDeviceEngine
    {
        public List<float> Captured { get; } = [];

        public void Render(Span<float> interleaved) => interleaved.Clear();

        public void Capture(ReadOnlySpan<float> interleaved) => Captured.AddRange(interleaved.ToArray());

        public void NotifyLost(string? reason)
        {
        }

        public void NotifyXrun(int frames)
        {
        }

        public void NotifyDefaultChanged()
        {
        }

        public void NotifyDevicesChanged()
        {
        }

        public void NotifyInterruption(bool began)
        {
        }
    }

    private sealed class NullEngine : IDeviceEngine
    {
        public void Render(Span<float> interleaved) => interleaved.Clear();

        public void Capture(ReadOnlySpan<float> interleaved)
        {
        }

        public void NotifyLost(string? reason)
        {
        }

        public void NotifyXrun(int frames)
        {
        }

        public void NotifyDefaultChanged()
        {
        }

        public void NotifyDevicesChanged()
        {
        }

        public void NotifyInterruption(bool began)
        {
        }
    }
}
