using System.Diagnostics;
using OneAudio.Devices;
using Xunit;

namespace OneAudio.Devices.Tests;

public class RealtimeTests
{
    private const int Rate = 48_000;

    private static AudioMixer MakeMixer(out AudioClip clip)
    {
        var mixer = new AudioMixer(new MixerOptions { SampleRate = Rate, Channels = 2, MaxVoices = 8, MaxBlockFrames = 256 });
        var samples = new float[Rate / 2 * 2];
        for (int i = 0; i < Rate / 2; i++)
        {
            samples[2 * i] = samples[(2 * i) + 1] = 0.3f * MathF.Sin(2f * MathF.PI * 330f * i / Rate);
        }

        clip = new AudioClip(new AudioFormat(Rate, 2), samples);
        return mixer;
    }

    [Fact]
    public void MixerOnAManualDevice_AllocatesNothing_OnTheRenderPath()
    {
        var mixer = MakeMixer(out var clip);
        var backend = new ManualBackend();
        using var output = AudioOutputDevice.Open(mixer, new DeviceStreamOptions { MeasureRenderAllocations = true, Metering = true }, backend);
        output.Start();

        // Warm-up: touch every path the measured loop will use.
        VoiceHandle v = default;
        for (int i = 0; i < 400; i++)
        {
            if (i % 50 == 0)
            {
                v = mixer.Play(new PlayParams(clip) { Loop = i % 100 == 0, Pan = 0.3f });
            }

            if (i % 75 == 74)
            {
                mixer.Stop(v, fadeOutFrames: 64);
            }

            output.Advance(256);
        }

        int collections = GC.CollectionCount(0) + GC.CollectionCount(1) + GC.CollectionCount(2);
        long allocated = 0;
        for (int i = 0; i < 5000; i++)
        {
            if (i % 40 == 0)
            {
                v = mixer.Play(new PlayParams(clip) { Loop = i % 80 == 0, Gain = 0.8f });
            }

            if (i % 60 == 59)
            {
                mixer.Stop(v, fadeOutFrames: 32);
            }

            long before = GC.GetAllocatedBytesForCurrentThread();
            output.Advance(256);
            allocated += GC.GetAllocatedBytesForCurrentThread() - before;
        }

        Assert.Equal(0, allocated);
        Assert.Equal(0, output.Counters.RenderAllocatedBytes);
        Assert.Equal(collections, GC.CollectionCount(0) + GC.CollectionCount(1) + GC.CollectionCount(2));
        Assert.True(output.Counters.PeakPlayed > 0.1f);
    }

    [Fact]
    public void TheRenderPath_NeverWaitsForTheControlLocks()
    {
        var backend = new ManualBackend();
        using var output = AudioOutputDevice.Open(new ToneSource(Rate, 2, 0.5f), null, backend);
        output.Start();
        var engine = output.EngineForTests;
        int rendered = 0;
        lock (engine.LifecycleLock)
        {
            lock (engine.FoldLock)
            {
                var t = new Thread(() =>
                {
                    for (int i = 0; i < 100; i++)
                    {
                        output.Advance(256);
                    }

                    Volatile.Write(ref rendered, 1);
                });
                t.Start();
                Assert.True(t.Join(5000), "the render path blocked on a control lock");
            }
        }

        Assert.Equal(1, rendered);
    }

    [Fact]
    public void ControlThreadHammering_WhileRendering_KeepsTheCountersSane()
    {
        var backend = new ManualBackend();
        using var output = AudioOutputDevice.Open(new ToneSource(Rate, 2, 0.5f), null, backend);
        output.Start();
        using var stop = new CancellationTokenSource();
        Exception? failure = null;
        var control = new Thread(() =>
        {
            try
            {
                int n = 0;
                while (!stop.IsCancellationRequested)
                {
                    switch (n++ % 6)
                    {
                        case 0: output.Suspend(); break;
                        case 1: output.Resume(); break;
                        case 2: output.SelectDevice(n % 12 < 6 ? "manual-out-1" : null); break;
                        case 3: backend.SimulateXrun(8); break;
                        case 4:
                            var c = output.Counters;
                            if (c.FramesPlayed > c.FramesWritten)
                            {
                                throw new InvalidOperationException("played ahead of written");
                            }

                            break;
                        default: output.Pump(); break;
                    }
                }
            }
            catch (Exception e)
            {
                failure = e;
            }
        });
        control.Start();
        var sw = Stopwatch.StartNew();
        long frames = 0;
        while (sw.ElapsedMilliseconds < 1000)
        {
            frames += output.Advance(128);
        }

        stop.Cancel();
        control.Join();
        Assert.Null(failure);
        var cs = output.Counters;
        Assert.True(frames > 0);
        Assert.True(cs.FramesPlayed <= cs.FramesWritten);
        Assert.True(cs.FramesWritten <= cs.FramesRendered);
    }

    [Fact]
    public void SupervisorThread_ReopensALostDevice_WithoutTheHostDoingAnything()
    {
        var backend = new ThreadedBackend();
        using var output = AudioOutputDevice.Open(new ToneSource(Rate, 2, 0.4f), null, backend);
        output.Start();
        Assert.True(Wait.Until(() => output.Counters.FramesPlayed > 2000), "nothing played: " + output.Counters);
        long before = output.Counters.FramesPlayed;
        backend.Lose();
        Assert.True(Wait.Until(() => output.Counters.Reopens == 1), "never reopened: " + output.Counters);
        Assert.True(Wait.Until(() => output.Counters.FramesPlayed > before + 2000), "playback did not continue: " + output.Counters);
        var c = output.Counters;
        Assert.Equal(1, c.DeviceLosses);
        Assert.Equal(0, c.Underruns);
        Assert.True(c.FramesPlayed <= c.FramesWritten);
        Assert.InRange(c.PeakPlayed, 0.35f, 0.41f);
        Assert.True(output.TryGetEvent(out var e) && e.Kind == DeviceEventKind.DeviceLost);
    }

    [Fact]
    public void SupervisorThread_KeepsTimeWithoutADevice_AtRealTimePace()
    {
        var backend = new ThreadedBackend { FailOpens = int.MaxValue };
        var tone = new ToneSource(Rate, 2, 0.4f);
        using var output = AudioOutputDevice.Open(tone, null, backend);
        var sw = Stopwatch.StartNew();
        output.Start();
        Thread.Sleep(1000);
        Assert.Equal(DeviceState.NoDevice, output.State);

        // Suspend takes the supervisor's lock, so pacing has stopped and every counter below is read at rest; the wall clock is read after
        // it, so it can only be later than the last paced frame.
        output.Suspend();
        double seconds = sw.Elapsed.TotalSeconds;
        var c = output.Counters;
        Assert.Equal(0, c.FramesWritten);
        Assert.Equal(c.SilentFrames, c.FramesRendered);
        Assert.Equal(c.SilentFrames, tone.Rendered);

        // The pacer renders what the wall clock says is due, so it can never be ahead of it (one chunk of slack for rounding). That bound
        // holds on any machine and fails a pacer that runs unpaced. The lower bound is deliberately loose: when the supervisor thread is
        // starved it skips ahead after 100 ms rather than render a burst, so a loaded runner may fall well short of real time, but a pacer
        // that is stuck or runs at a fraction of real time renders far less than a fifth.
        Assert.True(c.SilentFrames <= (long)(Rate * seconds) + 480, $"ran ahead of the clock: {c.SilentFrames} frames in {seconds:F3} s");
        Assert.True(c.SilentFrames >= (long)(Rate * seconds * 0.2), $"far behind the clock: {c.SilentFrames} frames in {seconds:F3} s");

        backend.FailOpens = 0;
        output.Resume();
        Assert.True(Wait.Until(() => output.State == DeviceState.Running, 3000), "the device was never restored: " + output.LastError);
        Assert.True(Wait.Until(() => output.Counters.FramesPlayed > 2000));
    }

    [Fact]
    public void SupervisorThread_FollowsTheDefaultDevice_ByPolling()
    {
        var backend = new ThreadedBackend();
        using var output = AudioOutputDevice.Open(new ToneSource(Rate, 2, 0.4f), null, backend);
        output.Start();
        Assert.Equal("t0", output.Device?.Id);
        backend.SetDefault("t1");
        Assert.True(Wait.Until(() => output.Device?.Id == "t1", 4000), "never followed the default");
        Assert.Equal(1, output.Counters.DefaultDeviceSwitches);
        Assert.True(Wait.Until(() => output.Counters.FramesPlayed > 2000));
    }

    [Fact]
    public void DisposingFromAnEventHandler_DoesNotDeadlock()
    {
        var backend = new ThreadedBackend();
        var output = AudioOutputDevice.Open(new ToneSource(Rate, 2, 0.4f), null, backend);
        using var done = new ManualResetEventSlim();
        output.Event += e =>
        {
            if (e.Kind == DeviceEventKind.DeviceLost)
            {
                output.Dispose();
                done.Set();
            }
        };
        output.Start();
        Assert.True(Wait.Until(() => output.Counters.FramesPlayed > 1000));
        backend.Lose();
        Assert.True(done.Wait(5000));
        Assert.Equal(DeviceState.Stopped, output.State);
    }

    [Fact]
    public void ThePlayOneCallHappyPath_PlaysAMixerOnTheBackendInUse()
    {
        var mixer = MakeMixer(out var clip);
        var backend = new ManualBackend();
        AudioDevices.UseBackend(backend);
        try
        {
            using var output = Audio.Play(mixer);
            mixer.Play(new PlayParams(clip));
            Assert.Equal(DeviceState.Running, output.State);
            output.Advance(4800);
            var c = output.Counters;
            Assert.Equal(4800, c.FramesPlayed);
            Assert.True(c.PeakPlayed > 0.2f);
            Assert.Equal("manual", output.BackendName);
            Assert.Equal(["manual-out-0", "manual-out-1"], AudioDevices.List(DeviceDirection.Output).Select(d => d.Id).ToArray());
        }
        finally
        {
            AudioDevices.UseBackend(null);
        }
    }
}
