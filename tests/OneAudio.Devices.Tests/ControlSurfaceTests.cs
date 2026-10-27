using OneAudio.Devices;
using Xunit;

namespace OneAudio.Devices.Tests;

public class ControlSurfaceTests
{
    private const int Rate = 48_000;

    [Fact]
    public void SuspendedState_CanBeReadWithoutEvents_AndTheClocksBehaveAsDocumented()
    {
        var backend = new ManualBackend { LagFrames = 256 };
        var tone = new ToneSource(Rate, 2, 0.2f);
        using var output = AudioOutputDevice.Open(tone, null, backend);
        output.Start();
        Assert.False(output.IsSuspended);
        Assert.False(output.IsSystemSuspended);
        output.Advance(1024);

        output.Suspend();
        Assert.True(output.IsSuspended);
        Assert.False(output.IsSystemSuspended);
        Assert.Equal(DeviceState.Suspended, output.State);
        var frozen = output.Counters;
        Assert.Equal(0, output.Advance(512));
        var after = output.Counters;
        Assert.Equal(frozen.FramesRendered, after.FramesRendered);   // the source's clock stops
        Assert.Equal(frozen.FramesWritten, after.FramesWritten);
        Assert.Equal(frozen.FramesPlayed, after.FramesPlayed);       // and so does the device position here (this device does not drain)
        output.Resume();
        Assert.False(output.IsSuspended);

        backend.SimulateInterruption(true);
        output.Pump();
        Assert.True(output.IsSystemSuspended);
        Assert.False(output.IsSuspended);
        Assert.Equal(DeviceState.Suspended, output.State);
        backend.SimulateInterruption(false);
        output.Pump();
        Assert.False(output.IsSystemSuspended);
        Assert.Equal(DeviceState.Running, output.State);
    }

    [Fact]
    public void Muted_FeedsSilence_WithoutRenderingTheSource()
    {
        var backend = new ManualBackend(recordFrames: 4096, recordChannels: 2);
        var tone = new ToneSource(Rate, 2, 0.5f);
        using var output = AudioOutputDevice.Open(tone, new DeviceStreamOptions { Limiter = OutputLimiterMode.Clamp }, backend);
        output.Start();
        output.Advance(512);
        long renderedBefore = tone.Rendered;
        output.ResetPeaks();

        output.Muted = true;
        Assert.True(output.Muted);
        output.Advance(1024);
        var c = output.Counters;
        Assert.Equal(renderedBefore, tone.Rendered);           // the source's clock stood still
        Assert.Equal(renderedBefore, c.FramesRendered);
        Assert.Equal(1024, c.MutedFrames);
        Assert.Equal(512 + 1024, c.FramesWritten);             // the device was fed
        Assert.Equal(512 + 1024, c.FramesPlayed);
        Assert.Equal(0f, c.PeakWritten);                       // and what it played since the reset is silence
        Assert.Equal(0f, c.PeakPlayed);

        output.Muted = false;
        output.Advance(512);
        Assert.Equal(renderedBefore + 512, tone.Rendered);     // it continues where it stopped
        Assert.True(output.Counters.PeakPlayed > 0.4f);

        var rec = new float[2 * 2048];
        backend.CopyRecording(rec);
        Assert.All(rec.Skip(2 * 512).Take(2 * 1024).ToArray(), v => Assert.Equal(0f, v));
    }

    [Fact]
    public void Muted_WithNoDevice_DoesNotRenderTheSourceEither()
    {
        var backend = new ManualBackend();
        backend.FailOpens(int.MaxValue);
        var tone = new ToneSource(Rate, 2, 0.5f);
        using var output = AudioOutputDevice.Open(tone, null, backend);
        output.Start();
        output.Muted = true;
        output.Advance(480);
        Assert.Equal(0, tone.Rendered);
        Assert.Equal(480, output.Counters.MutedFrames);
        output.Muted = false;
        output.Advance(480);
        Assert.Equal(480, tone.Rendered);
    }

    [Fact]
    public void DeviceUnderruns_AreTheDevicesOwnCount_AndSurviveAReopen()
    {
        var backend = new ManualBackend();
        using var output = AudioOutputDevice.Open(new ToneSource(Rate, 2, 0.2f), null, backend);
        output.Start();
        output.Advance(256);
        backend.SimulateDeviceUnderruns(2);
        backend.SimulateDeviceUnderruns(3);
        var c = output.Counters;
        Assert.Equal(5, c.DeviceUnderruns);
        Assert.Equal(0, c.Underruns);                           // ours are separate

        backend.SimulateXrun(10);
        Assert.Equal(1, output.Counters.Underruns);
        Assert.Equal(5, output.Counters.DeviceUnderruns);

        backend.SimulateLoss();
        output.Advance(256);                                    // reopens
        backend.SimulateDeviceUnderruns(1);
        Assert.Equal(6, output.Counters.DeviceUnderruns);       // the new stream counts from 0; the total keeps the old
    }

    [Fact]
    public void BlockHook_IsCalledPerBlock_WithTheFramesHandedOverSoFar_AndAllocatesNothing()
    {
        var backend = new ManualBackend { PeriodFrames = 128 };
        using var output = AudioOutputDevice.Open(new ToneSource(Rate, 2, 0.2f), null, backend);
        var seen = new long[64];
        int n = 0;
        output.BlockHook = frames =>
        {
            if (n < seen.Length)
            {
                seen[n] = frames;
            }

            n++;
        };
        output.Start();
        output.Advance(128 * 8);
        Assert.Equal(8, n);
        Assert.Equal([0L, 128, 256, 384, 512, 640, 768, 896], seen.Take(8).ToArray());

        for (int i = 0; i < 100; i++)
        {
            output.Advance(128);
        }

        long before = GC.GetAllocatedBytesForCurrentThread();
        for (int i = 0; i < 2000; i++)
        {
            output.Advance(128);
        }

        Assert.Equal(0, GC.GetAllocatedBytesForCurrentThread() - before);

        output.BlockHook = _ => throw new InvalidOperationException("hook");
        output.Advance(256);
        Assert.Equal(2, output.Counters.CallbackFaults);
        output.BlockHook = null;
        Assert.Equal(128, output.Advance(128));
    }

    [Fact]
    public void ManualSink_StreamsEveryBlock_WithoutACapacityLimit()
    {
        var backend = new ManualBackend();
        long total = 0;
        float peak = 0;
        backend.Sink = block =>
        {
            total += block.Length / 2;
            foreach (float v in block)
            {
                peak = Math.Max(peak, Math.Abs(v));
            }
        };
        using var output = AudioOutputDevice.Open(new ToneSource(Rate, 2, 0.4f), null, backend);
        output.Start();
        output.Advance(Rate * 3);
        Assert.Equal(Rate * 3, total);
        Assert.InRange(peak, 0.39f, 0.41f);
        Assert.Equal(0, backend.RecordedFrames); // no recording capacity was asked for
    }

    [Fact]
    public void RenderAllocatedBytes_IsAlwaysKept()
    {
        var backend = new ManualBackend();
        using var output = AudioOutputDevice.Open(new ToneSource(Rate, 2, 0.4f), null, backend); // no option set
        output.Start();
        output.Advance(256 * 40);
        Assert.Equal(0, output.Counters.RenderAllocatedBytes);

        // A source that allocates is caught.
        using var bad = AudioOutputDevice.Open(new AllocatingSource(), null, new ManualBackend());
        bad.Start();
        bad.Advance(256 * 40);
        Assert.True(bad.Counters.RenderAllocatedBytes > 0);
    }

    private sealed class AllocatingSource : IAudioSource
    {
        private object? _sink;

        public int SampleRate => Rate;

        public int Channels => 2;

        public void Render(Span<float> output)
        {
            _sink = new byte[64];
            output.Clear();
        }
    }
}
