using OneAudio.Devices;
using OneAudio.Devices.Interop;
using Xunit;

namespace OneAudio.Devices.Tests;

/// <summary>
/// The AAudio backend's arithmetic and its behaviour on a machine without AAudio (everywhere but an Android process). Tests that need the
/// real library skip themselves elsewhere; the on-device run is tools/android-smoke.
/// </summary>
public class AAudioTests
{
    [Theory]
    [InlineData(192, 0, 0, 20, 48_000, 960)]      // 20 ms = 960 frames, already a whole number of bursts
    [InlineData(192, 0, 0, 10, 48_000, 576)]      // 480 rounds up to 3 bursts
    [InlineData(192, 0, 0, 1, 48_000, 384)]       // never below two bursts
    [InlineData(192, 0, 1000, 20, 48_000, 1152)]  // an explicit size wins over the latency, rounded up
    [InlineData(192, 768, 0, 100, 48_000, 768)]    // capped at the capacity
    [InlineData(0, 0, 0, 0, 48_000, 32)]           // a missing burst is taken as 16
    public void BufferTarget_RoundsToBursts(int burst, int capacity, int requested, int ms, int rate, int expected) =>
        Assert.Equal(expected, AAudioLogic.BufferTarget(burst, capacity, requested, ms, rate));

    [Fact]
    public void PlayedFrames_AdvancesTheTimestamp_ButNeverPastWhatTheDeviceRead()
    {
        // The timestamp says frame 48000 was presented 10 ms ago: 480 frames have passed since.
        Assert.Equal(48_480, AAudioLogic.PlayedFrames(48_000, 1_000_000_000, 1_010_000_000, 48_000, 60_000, 0));
        // The device has read only 48200 so far.
        Assert.Equal(48_200, AAudioLogic.PlayedFrames(48_000, 1_000_000_000, 1_010_000_000, 48_000, 48_200, 0));
        // Never backwards.
        Assert.Equal(50_000, AAudioLogic.PlayedFrames(48_000, 1_000_000_000, 1_010_000_000, 48_000, 48_200, 50_000));
        // A timestamp from the future (clock skew) adds nothing.
        Assert.Equal(48_000, AAudioLogic.PlayedFrames(48_000, 2_000_000_000, 1_000_000_000, 48_000, 60_000, 0));
    }

    [Theory]
    [InlineData(null, true, 0)]
    [InlineData("", true, 0)]
    [InlineData("default", true, 0)]
    [InlineData("12", true, 12)]
    [InlineData("0", false, 0)]
    [InlineData("-3", false, 0)]
    [InlineData("hw:0", false, 0)]
    [InlineData("99999999999", false, 0)]
    public void DeviceIds_AreDefaultOrDecimal(string? id, bool ok, int expected)
    {
        Assert.Equal(ok, AAudioLogic.TryParseDeviceId(id, out int value));
        Assert.Equal(expected, value);
    }

    [Fact]
    public void WithoutAndroid_TheBackendIsUnavailableAndOpensNothing()
    {
        Assert.SkipWhen(AAudioBackend.IsAndroidProcess(), "this is an Android process");
        var backend = new AAudioBackend();
        Assert.False(backend.IsAvailable);
        Assert.Empty(backend.List(DeviceDirection.Output));
        Assert.Null(backend.GetDefault(DeviceDirection.Output));
        Assert.Equal(DeviceBackendKind.AAudio, backend.Kind);
        Assert.False(backend.TryOpen(DeviceDirection.Output, new DeviceStreamOptions(), new ManualBackendEngineStub(), out var stream, out string reason));
        Assert.Null(stream);
        Assert.NotEmpty(reason);
    }

    [Fact]
    public void TheAndroidProcessChoosesAAudio_AndOtherLinuxStillAlsa()
    {
        var chosen = PlatformBackends.Create();
        Assert.NotNull(chosen);
        if (AAudioBackend.IsAndroidProcess())
        {
            Assert.IsType<AAudioBackend>(chosen);
        }
        else
        {
            Assert.IsNotType<AAudioBackend>(chosen);
        }
    }

    [Fact]
    public void OnAndroid_AQuietToneReachesTheDevice()
    {
        Assert.SkipUnless(AAudioBackend.IsAndroidProcess() && AAudioNative.IsPresent, "not an Android process with AAudio");
        var backend = new AAudioBackend();
        using var output = AudioOutputDevice.Open(new ToneSource(48_000, 2, 0.02f), new DeviceStreamOptions { MeasureRenderAllocations = true }, backend);
        output.Start();
        Assert.Equal(DeviceState.Running, output.State);
        Assert.True(Wait.Until(() => output.Counters.FramesPlayed > 24_000), output.Counters.ToString());
        Assert.Equal(0, output.Counters.RenderAllocatedBytes);
    }

    [Fact]
    public void ADisconnectedStream_IsReportedLostOnce_AndALaterStartIsRefused()
    {
        var engine = new LossRecorder();
        var stream = new AAudioStream(DeviceDirection.Output, engine, new AudioDeviceFormat(48_000, 2));
        IAAudioClient client = stream;
        client.OnError(AAudioNative.ErrorDisconnected);
        client.OnError(AAudioNative.ErrorDisconnected);
        Assert.Equal(1, engine.Lost);
        Assert.Equal("AAudio device disconnected", engine.Reason);
        Assert.False(stream.Start(out string reason));
        Assert.NotEmpty(reason);
        stream.Dispose();
    }

    private sealed class LossRecorder : IDeviceEngine
    {
        public int Lost;
        public string? Reason;

        public void Render(Span<float> interleaved) => interleaved.Clear();

        public void Capture(ReadOnlySpan<float> interleaved)
        {
        }

        public void NotifyLost(string? reason)
        {
            Lost++;
            Reason = reason;
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

    private sealed class ManualBackendEngineStub : IDeviceEngine
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
