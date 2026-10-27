using OneAudio.Devices;
using Xunit;

namespace OneAudio.Devices.Tests;

public class ManualDeviceTests
{
    private const int Rate = 48_000;

    private static List<DeviceEventKind> Kinds(AudioOutputDevice d)
    {
        var list = new List<DeviceEventKind>();
        while (d.TryGetEvent(out var e))
        {
            list.Add(e.Kind);
        }

        return list;
    }

    [Fact]
    public void Output_IsBitEqualToDirectRender_AndCountersAgree()
    {
        var backend = new ManualBackend(recordFrames: 10_000, recordChannels: 2);
        var tone = new ToneSource(Rate, 2, 0.5f);
        var twin = new ToneSource(Rate, 2, 0.5f);
        using var output = AudioOutputDevice.Open(tone, new DeviceStreamOptions { Limiter = OutputLimiterMode.Clamp }, backend);
        output.Start();
        Assert.Equal(DeviceState.Running, output.State);
        Assert.Equal(1000, output.Advance(1000));

        var expected = new float[2000];
        twin.Render(expected);

        var got = new float[2000];
        Assert.Equal(2000, backend.CopyRecording(got));
        Assert.Equal(expected, got);

        var c = output.Counters;
        Assert.Equal(1000, c.FramesRendered);
        Assert.Equal(1000, c.FramesWritten);
        Assert.Equal(1000, c.FramesPlayed);
        Assert.InRange(c.PeakWritten, 0.49f, 0.5f);
        Assert.InRange(c.PeakPlayed, 0.49f, 0.5f);
        Assert.Equal(0, c.Underruns);
        Assert.Equal(0, c.CallbackFaults);
        Assert.Equal(256, c.PeriodFrames);
        Assert.Equal("manual-out-0", output.Device?.Id);
    }

    [Fact]
    public void PlayedPosition_LagsWrites_AndPlayedPeakWaitsForTheDevice()
    {
        var backend = new ManualBackend { LagFrames = 1024 };
        // 2048 frames of silence, then a loud tone: the device must not count the tone as played until its clock gets there.
        long frame = 0;
        using var output = AudioOutputDevice.Open(
            (Span<float> block, in RenderContext ctx) =>
            {
                for (int i = 0; i < block.Length; i += 2)
                {
                    float v = frame < 2048 ? 0f : 0.8f;
                    block[i] = v;
                    block[i + 1] = v;
                    frame++;
                }
            },
            new DeviceStreamOptions { Limiter = OutputLimiterMode.Clamp },
            backend);
        output.Start();
        output.Advance(2560);
        var c = output.Counters;
        Assert.Equal(2560, c.FramesWritten);
        Assert.Equal(2560 - 1024, c.FramesPlayed);
        Assert.InRange(c.PeakWritten, 0.79f, 0.81f);
        Assert.Equal(0f, c.PeakPlayed); // played up to frame 1536: all silence

        output.Advance(1024);
        c = output.Counters;
        Assert.Equal(3584 - 1024, c.FramesPlayed);
        Assert.InRange(c.PeakPlayed, 0.79f, 0.81f);
        Assert.True(c.FramesPlayed <= c.FramesWritten);
    }

    [Fact]
    public void LostDevice_ReopensWithoutGap_AndPlayedKeepsCounting()
    {
        var backend = new ManualBackend();
        using var output = AudioOutputDevice.Open(new ToneSource(Rate, 2, 0.25f), null, backend);
        output.Start();
        output.Advance(1000);
        backend.SimulateLoss();
        Assert.Equal(0, output.Advance(500)); // the lost stream consumes nothing
        output.Pump();

        var kinds = Kinds(output);
        Assert.Equal([DeviceEventKind.DeviceLost, DeviceEventKind.Reopened], kinds);
        Assert.Equal(2, backend.OpenCount);
        Assert.Equal(DeviceState.Running, output.State);
        Assert.Equal(500, output.Advance(500));
        var c = output.Counters;
        Assert.Equal(1, c.DeviceLosses);
        Assert.Equal(1, c.Reopens);
        Assert.Equal(1500, c.FramesWritten);
        Assert.Equal(1500, c.FramesPlayed);
        Assert.Equal(0, c.SilentFrames);
    }

    [Fact]
    public void LostDevice_ThatCannotReopen_RendersSilentlyThenRestores()
    {
        var backend = new ManualBackend();
        var events = new List<DeviceEvent>();
        using var output = AudioOutputDevice.Open(new ToneSource(Rate, 2, 0.25f), null, backend);
        output.Event += events.Add;
        output.Start();
        output.Advance(480);
        backend.FailOpens(1);
        backend.SimulateLoss();
        output.Pump();

        Assert.Equal(DeviceState.NoDevice, output.State);
        Assert.NotNull(output.LastError);
        Assert.Null(output.Device);
        Assert.Equal(960, output.Advance(960)); // keeps time with the program's clock, output discarded
        var c = output.Counters;
        Assert.Equal(960, c.SilentFrames);
        Assert.Equal(480, c.FramesWritten);
        Assert.Equal(1440, c.FramesRendered);

        Thread.Sleep(600); // the retry period is half a second
        output.Pump();
        Assert.Equal(DeviceState.Running, output.State);
        Assert.Equal(
            [DeviceEventKind.DeviceLost, DeviceEventKind.DeviceRestored],
            events.Select(e => e.Kind).ToArray());
        Assert.Equal(1, output.Counters.Reopens);
        Assert.Equal(480, output.Advance(480));
        Assert.Equal(960, output.Counters.FramesWritten);
    }

    [Fact]
    public void NeverOpenedDevice_StartsSilent_AndIsRestoredWhenItAppears()
    {
        var backend = new ManualBackend();
        backend.FailOpens(1);
        using var output = AudioOutputDevice.Open(new ToneSource(Rate, 2, 0.25f), null, backend);
        output.Start();
        Assert.Equal(DeviceState.NoDevice, output.State);
        output.Advance(100);
        Assert.Equal(100, output.Counters.SilentFrames);
        Thread.Sleep(600);
        output.Pump();
        Assert.Equal(DeviceState.Running, output.State);
        Assert.Equal([DeviceEventKind.DeviceRestored], Kinds(output));
        Assert.Equal(0, output.Counters.Reopens); // the first successful open is not a reopen
    }

    [Fact]
    public void DefaultChange_IsFollowed_WhenNotified()
    {
        var backend = new ManualBackend();
        using var output = AudioOutputDevice.Open(new ToneSource(Rate, 2, 0.25f), null, backend);
        output.Start();
        output.Advance(300);
        backend.SetDefault("manual-out-1");
        output.Pump();

        Assert.Equal("manual-out-1", output.Device?.Id);
        Assert.Equal(1, output.Counters.DefaultDeviceSwitches);
        Assert.Equal(1, output.Counters.Reopens);
        Assert.Equal(0, output.Counters.DeviceLosses);
        Assert.Equal([DeviceEventKind.DefaultChanged, DeviceEventKind.Reopened], Kinds(output));
        Assert.Equal(300, output.Advance(300));
        Assert.Equal(600, output.Counters.FramesPlayed);

        // The same default again is not a change.
        backend.SetDefault("manual-out-1");
        output.Pump();
        Assert.Equal(1, output.Counters.DefaultDeviceSwitches);
    }

    [Fact]
    public void DefaultChange_IsFound_ByPolling_WhenTheBackendCannotTell()
    {
        var backend = new ManualBackend { NotifiesDeviceChanges = false };
        using var output = AudioOutputDevice.Open(new ToneSource(Rate, 2, 0.25f), null, backend);
        output.Start();
        backend.SetDefault("manual-out-1");
        Thread.Sleep(1100);
        output.Pump();
        Assert.Equal("manual-out-1", output.Device?.Id);
        Assert.Equal(1, output.Counters.DefaultDeviceSwitches);
    }

    [Fact]
    public void ExplicitDevice_StaysPut_WhenTheDefaultChanges_AndSelectDeviceMoves()
    {
        var backend = new ManualBackend();
        using var output = AudioOutputDevice.Open(new ToneSource(Rate, 2, 0.25f), new DeviceStreamOptions { DeviceId = "manual-out-1" }, backend);
        output.Start();
        Assert.Equal("manual-out-1", output.Device?.Id);
        backend.SimulateLoss();
        backend.SetDefault("manual-out-0");
        output.Pump();
        Assert.Equal("manual-out-1", output.Device?.Id); // reopened on the same device

        output.SelectDevice("manual-out-0");
        Assert.Equal("manual-out-0", output.Device?.Id);
        Assert.Equal(0, output.Counters.DefaultDeviceSwitches);
    }

    [Fact]
    public void FollowDefaultOff_IgnoresDefaultChanges()
    {
        var backend = new ManualBackend();
        using var output = AudioOutputDevice.Open(new ToneSource(Rate, 2, 0.25f), new DeviceStreamOptions { FollowDefault = false }, backend);
        output.Start();
        backend.SetDefault("manual-out-1");
        output.Pump();
        Assert.Equal("manual-out-0", output.Device?.Id);
    }

    [Fact]
    public void Suspend_StopsRendering_AndResumeContinues()
    {
        var backend = new ManualBackend();
        var tone = new ToneSource(Rate, 2, 0.25f);
        using var output = AudioOutputDevice.Open(tone, null, backend);
        output.Start();
        output.Advance(256);
        output.Suspend();
        Assert.Equal(DeviceState.Suspended, output.State);
        Assert.Equal(0, output.Advance(256));
        Assert.Equal(256, tone.Rendered);
        output.Resume();
        Assert.Equal(DeviceState.Running, output.State);
        Assert.Equal(256, output.Advance(256));
        Assert.Equal(512, output.Counters.FramesPlayed);
    }

    [Fact]
    public void SystemInterruption_SuspendsAndResumes_WithEvents()
    {
        var backend = new ManualBackend();
        using var output = AudioOutputDevice.Open(new ToneSource(Rate, 2, 0.25f), null, backend);
        output.Start();
        backend.SimulateInterruption(true);
        output.Pump();
        Assert.Equal(DeviceState.Suspended, output.State);
        Assert.Equal(0, output.Advance(256));
        backend.SimulateInterruption(false);
        output.Pump();
        Assert.Equal(DeviceState.Running, output.State);
        Assert.Equal(256, output.Advance(256));
        Assert.Equal([DeviceEventKind.Interrupted, DeviceEventKind.Resumed], Kinds(output));
    }

    [Fact]
    public void Xrun_IsCountedAndReported()
    {
        var backend = new ManualBackend();
        using var output = AudioOutputDevice.Open(new ToneSource(Rate, 2, 0.25f), null, backend);
        output.Start();
        backend.SimulateXrun(96);
        output.Pump();
        Assert.Equal(1, output.Counters.Underruns);
        Assert.True(output.TryGetEvent(out var e));
        Assert.Equal(DeviceEventKind.Xrun, e.Kind);
        Assert.Equal("96", e.Detail);
    }

    [Fact]
    public void ThrowingSource_IsCaught_CountedAndReplacedBySilence()
    {
        var backend = new ManualBackend(recordFrames: 1024, recordChannels: 2);
        var tone = new ToneSource(Rate, 2, 0.5f) { Throw = true };
        using var output = AudioOutputDevice.Open(tone, null, backend);
        output.Start();
        Assert.Equal(512, output.Advance(512));
        var c = output.Counters;
        Assert.Equal(2, c.CallbackFaults);
        Assert.Equal(0f, c.PeakWritten);
        var rec = new float[1024];
        backend.CopyRecording(rec);
        Assert.All(rec, v => Assert.Equal(0f, v));

        tone.Throw = false; // it recovers on its own
        output.Advance(256);
        Assert.True(output.Counters.PeakWritten > 0.4f);
    }

    [Fact]
    public void ThrowingEventHandler_DoesNotBreakTheDevice()
    {
        var backend = new ManualBackend();
        using var output = AudioOutputDevice.Open(new ToneSource(Rate, 2, 0.25f), null, backend);
        output.Event += _ => throw new InvalidOperationException("handler");
        output.Start();
        backend.SimulateXrun(1);
        output.Pump();
        Assert.Equal(1, output.Counters.CallbackFaults);
        Assert.Equal(256, output.Advance(256));
    }

    [Fact]
    public void Metering_ReadsTheOutputAfterTheLimiter()
    {
        var backend = new ManualBackend();
        using var output = AudioOutputDevice.Open(new ToneSource(Rate, 2, 0.5f), new DeviceStreamOptions { Metering = true }, backend);
        Assert.Equal(double.NegativeInfinity, output.Loudness.MomentaryLufs);
        output.Start();
        output.Advance(Rate);
        var l = output.Loudness;
        Assert.InRange(l.MomentaryLufs, -7.5, -6.0); // 0.5 full-scale sine on both channels
        Assert.True(l.Frames >= Rate - 256);
        Assert.InRange(l.TruePeakDbtp, -6.5, -5.5);
        output.ResetLoudness();
        output.Advance(256);
        Assert.Equal(256, output.Loudness.Frames);
    }

    [Fact]
    public void NoBackendOnThePlatform_RunsSilent_WithAReason()
    {
        using var output = AudioOutputDevice.Open(new ToneSource(Rate, 2, 0.5f), null, UnavailableBackend.Instance);
        output.Start();
        Assert.Equal(DeviceState.NoDevice, output.State);
        Assert.Contains("no audio backend", output.LastError);
        Assert.False(UnavailableBackend.Instance.IsAvailable);
    }

    [Fact]
    public void Arguments_AreValidated()
    {
        var backend = new ManualBackend();
        Assert.Throws<ArgumentOutOfRangeException>(() => AudioOutputDevice.Open(new ToneSource(1000, 2, 0.5f), null, backend));
        Assert.Throws<ArgumentOutOfRangeException>(() => AudioOutputDevice.Open(new ToneSource(Rate, 17, 0.5f), null, backend));
        Assert.Throws<ArgumentNullException>(() => AudioOutputDevice.Open((IAudioSource)null!, null, backend));
        using var output = AudioOutputDevice.Open(new ToneSource(Rate, 2, 0.5f), null, backend);
        output.Dispose();
        output.Dispose();
        Assert.Throws<ObjectDisposedException>(output.Start);
        Assert.Equal(DeviceState.Stopped, output.State);
    }

    [Fact]
    public void Limiter_KeepsEverythingInRange()
    {
        Assert.Equal(0f, OutputLimiter.Apply(float.NaN, OutputLimiterMode.Clamp));
        Assert.Equal(0f, OutputLimiter.Apply(float.NaN, OutputLimiterMode.SoftKnee));
        Assert.Equal(1f, OutputLimiter.Apply(3f, OutputLimiterMode.Clamp));
        Assert.Equal(-1f, OutputLimiter.Apply(float.NegativeInfinity, OutputLimiterMode.Clamp));
        Assert.Equal(0.9f, OutputLimiter.Apply(0.9f, OutputLimiterMode.SoftKnee));
        float prev = 0.9f;
        for (float x = 0.91f; x < 100f; x *= 1.1f)
        {
            float y = OutputLimiter.Apply(x, OutputLimiterMode.SoftKnee);
            Assert.InRange(y, prev, 1f);
            Assert.Equal(-y, OutputLimiter.Apply(-x, OutputLimiterMode.SoftKnee));
            prev = y;
        }

        var buffer = new float[] { 0.1f, -2f, float.PositiveInfinity, float.NaN, 0.95f };
        float peak = OutputLimiter.Apply(buffer, OutputLimiterMode.SoftKnee);
        Assert.All(buffer, v => Assert.InRange(v, -1f, 1f));
        Assert.Equal(buffer.Max(MathF.Abs), peak);
    }

    [Fact]
    public void ManualBackend_ListsAndNamesItsDevices()
    {
        var backend = new ManualBackend();
        Assert.Equal(["manual-out-0", "manual-out-1"], backend.List(DeviceDirection.Output).Select(d => d.Id).ToArray());
        Assert.Equal("manual-in-0", backend.GetDefault(DeviceDirection.Input)?.Id);
        Assert.True(backend.GetDefault(DeviceDirection.Output)?.IsDefault);
        Assert.Throws<ArgumentException>(() => backend.SetDefault("nope"));
    }

    [Fact]
    public void Input_DeliversTheSignal_ToTheReadBuffer_AndCountsOverruns()
    {
        var backend = new ManualBackend();
        var signal = new float[2 * 1000];
        for (int i = 0; i < signal.Length; i++)
        {
            signal[i] = ((i % 200) - 100) / 200f;
        }

        backend.SetInputSignal(signal, loop: false);
        using var input = AudioInputDevice.Open(new DeviceStreamOptions { CaptureBufferMilliseconds = 40 }, backend); // 1920 frames
        input.Start();
        Assert.Equal(1000, input.Advance(1000));
        Assert.Equal(1000, input.AvailableFrames);
        var got = new float[2 * 1000];
        Assert.Equal(1000, input.Read(got));
        Assert.Equal(signal, got);
        var c = input.Counters;
        Assert.Equal(1000, c.FramesWritten);
        Assert.Equal(1000, c.FramesPlayed);
        Assert.InRange(c.PeakPlayed, 0.49f, 0.5f);
        Assert.Equal(0, c.Overruns);

        // Nobody reads: the 1920-frame buffer fills and the rest is dropped and counted.
        input.Advance(4000);
        Assert.True(input.Counters.Overruns > 0);
        Assert.Equal(1920, input.AvailableFrames);
    }

    [Fact]
    public void Input_Callback_ReceivesBlocks_AndAThrowIsCounted()
    {
        var backend = new ManualBackend();
        backend.SetInputSignal([0.5f, 0.5f], loop: true);
        long frames = 0;
        bool boom = true;
        using var input = AudioInputDevice.Open(
            (ReadOnlySpan<float> block, in RenderContext ctx) =>
            {
                frames += block.Length / 2;
                Assert.All(block.ToArray(), v => Assert.Equal(0.5f, v));
                if (boom)
                {
                    boom = false;
                    throw new InvalidOperationException("boom");
                }
            },
            null,
            backend);
        input.Start();
        input.Advance(700);
        Assert.Equal(700, frames);
        Assert.Equal(1, input.Counters.CallbackFaults);
        Assert.Throws<InvalidOperationException>(() => input.Read(new float[4]));
    }

    [Fact]
    public void Input_ReopensAfterALoss()
    {
        var backend = new ManualBackend();
        backend.SetInputSignal([0.25f, 0.25f], loop: true);
        using var input = AudioInputDevice.Open(null, backend);
        input.Start();
        input.Advance(300);
        backend.SimulateLoss();
        input.Pump();
        Assert.Equal(300, input.Advance(300));
        var c = input.Counters;
        Assert.Equal(1, c.DeviceLosses);
        Assert.Equal(1, c.Reopens);
        Assert.Equal(600, c.FramesPlayed);
    }
}
