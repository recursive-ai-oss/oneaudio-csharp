using OneAudio.Devices;
using Xunit;

namespace OneAudio.Devices.Tests;

/// <summary>
/// The WASAPI backend against the real Windows audio stack. They skip on other systems. A machine without an audio endpoint (a hosted CI
/// runner usually has none) must be handled as "no device": empty lists, a refused open with a reason, an output that falls back to the
/// silent real-time pace. With an endpoint they play a short, quiet tone (it runs for under a second).
/// </summary>
public class WasapiWindowsTests
{
    private const int Rate = 48_000;

    private static void RequireWindows() => Assert.SkipUnless(OperatingSystem.IsWindows(), "WASAPI exists only on Windows");

    [Fact]
    public void Backend_IdentifiesItself()
    {
        var backend = new WasapiBackend();
        Assert.Equal("wasapi", backend.Name);
        Assert.Equal(DeviceBackendKind.Wasapi, backend.Kind);
        Assert.False(backend.IsManual);
        Assert.True(backend.NotifiesDeviceChanges);
        Assert.True(OperatingSystem.IsWindows() || !backend.IsAvailable);
        if (!OperatingSystem.IsWindows())
        {
            Assert.Empty(backend.List(DeviceDirection.Output));
            Assert.Null(backend.GetDefault(DeviceDirection.Input));
            Assert.False(backend.TryOpen(DeviceDirection.Output, new DeviceStreamOptions(), new NullEngine(), out var stream, out string reason));
            Assert.Null(stream);
            Assert.NotEmpty(reason);
        }
    }

    [Theory]
    [InlineData(DeviceDirection.Output)]
    [InlineData(DeviceDirection.Input)]
    public void Enumeration_ListsEndpointsOrNothing_NeverThrows(DeviceDirection direction)
    {
        RequireWindows();
        var backend = new WasapiBackend();
        Assert.True(backend.IsAvailable, "the MMDevice enumerator should exist on every Windows install");
        var devices = backend.List(direction);
        var def = backend.GetDefault(direction);
        foreach (var d in devices)
        {
            Assert.False(string.IsNullOrEmpty(d.Id));
            Assert.False(string.IsNullOrEmpty(d.Name));
            Assert.Equal(direction, d.Direction);
            Assert.Equal(DeviceBackendKind.Wasapi, d.Backend);
            Assert.InRange(d.DefaultSampleRate, 0, 768_000);
            Assert.InRange(d.MaxChannels, 0, 64);
        }

        if (def is { } defaultDevice)
        {
            Assert.True(defaultDevice.IsDefault);
            Assert.Contains(devices, d => d.Id == defaultDevice.Id && d.IsDefault);
            Assert.Equal(1, devices.Count(d => d.IsDefault));
        }
        else
        {
            Assert.DoesNotContain(devices, d => d.IsDefault);
        }
    }

    [Fact]
    public void Open_UnknownDevice_IsRefusedWithAReason()
    {
        RequireWindows();
        var backend = new WasapiBackend();
        Assert.False(backend.TryOpen(DeviceDirection.Output, new DeviceStreamOptions { DeviceId = "{0.0.0.00000000}.{00000000-0000-0000-0000-000000000000}" }, new NullEngine(), out var stream, out string reason));
        Assert.Null(stream);
        Assert.NotEmpty(reason);
    }

    [Fact]
    public void OutputOnTheDefaultDevice_PlaysOrFallsBackToNoDevice()
    {
        RequireWindows();
        var backend = new WasapiBackend();
        var tone = new ToneSource(Rate, 2, 0.05f);
        using var output = AudioOutputDevice.Open(tone, new DeviceStreamOptions { MeasureRenderAllocations = true }, backend);
        output.Start();
        if (backend.GetDefault(DeviceDirection.Output) is null)
        {
            Assert.Equal(DeviceState.NoDevice, output.State);
            Assert.False(string.IsNullOrEmpty(output.LastError));
            return;
        }

        Assert.Equal(DeviceState.Running, output.State);
        Assert.True(Wait.Until(() => output.Counters.FramesPlayed > Rate / 4), "the device never took the tone: " + output.Counters);
        var c = output.Counters;
        Assert.True(c.FramesWritten >= c.FramesPlayed);
        Assert.InRange(c.PeakPlayed, 0.01f, 0.06f);
        Assert.Equal(0, c.CallbackFaults);
        Assert.Equal(0, c.RenderAllocatedBytes);
        Assert.True(c.BufferFrames > 0 && c.PeriodFrames > 0, c.ToString());
        Assert.Equal("wasapi", output.BackendName);

        // Pausing keeps the device open and the position still.
        output.Suspend();
        Assert.Equal(DeviceState.Suspended, output.State);
        long frozen = output.Counters.FramesPlayed;
        Thread.Sleep(100);
        Assert.Equal(frozen, output.Counters.FramesPlayed);
        output.Resume();
        Assert.True(Wait.Until(() => output.Counters.FramesPlayed > frozen + Rate / 10), output.Counters.ToString());
    }

    [Fact]
    public void ExclusiveOutput_IsGrantedOrRefusedWithAReason()
    {
        RequireWindows();
        var backend = new WasapiBackend();
        var options = new DeviceStreamOptions { Mode = ShareMode.Exclusive, LatencyMilliseconds = 10 };
        bool opened = backend.TryOpen(DeviceDirection.Output, options, new NullEngine(), out var stream, out string reason);
        try
        {
            if (opened)
            {
                Assert.NotNull(stream);
                Assert.Equal(new AudioDeviceFormat(Rate, 2), stream!.Format);
                Assert.Contains("exclusive", stream.Detail);
                Assert.True(stream.BufferFrames > 0);
            }
            else
            {
                Assert.Null(stream);
                Assert.False(string.IsNullOrEmpty(reason));
            }
        }
        finally
        {
            stream?.Dispose();
        }
    }

    [Fact]
    public void InputOnTheDefaultDevice_CapturesOrFallsBackToNoDevice()
    {
        RequireWindows();
        Assert.SkipUnless(Environment.GetEnvironmentVariable("ONEAUDIO_DEVICE_TESTS") == "1", "set ONEAUDIO_DEVICE_TESTS=1 to record from the real default input");
        var backend = new WasapiBackend();
        using var input = AudioInputDevice.Open(new DeviceStreamOptions { SampleRate = Rate, Channels = 1 }, backend);
        input.Start();
        if (backend.GetDefault(DeviceDirection.Input) is null)
        {
            Assert.Equal(DeviceState.NoDevice, input.State);
            return;
        }

        Assert.True(Wait.Until(() => input.Counters.FramesPlayed > Rate / 4), input.Counters.ToString());
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
