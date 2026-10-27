using OneAudio.Devices;
using Xunit;

namespace OneAudio.Devices.Tests;

/// <summary>
/// A custom manual backend, the kind a single-threaded host (a browser, a game loop that owns the audio pump) writes: its streams are stepped by
/// the caller and the package must not start any thread. Every callback records the thread it ran on.
/// </summary>
internal sealed class SteppedBackend : IAudioDeviceBackend
{
    public readonly HashSet<int> Threads = [];
    public bool DriveSelf; // true: the stream is not an IManualDevice; the host calls Step itself, as a worklet callback would
    public SteppedStream? Live;
    public int Opens;
    public int FailOpens;

    public string Name => "stepped";

    public DeviceBackendKind Kind => DeviceBackendKind.Custom;

    public bool IsManual => true;

    public bool NotifiesDeviceChanges => true;

    public bool IsAvailable => true;

    public void Mark()
    {
        lock (Threads)
        {
            Threads.Add(Environment.CurrentManagedThreadId);
        }
    }

    public IReadOnlyList<AudioDeviceInfo> List(DeviceDirection direction) =>
        [new("page", "Page output", direction, true, 48000, 2, 128, DeviceBackendKind.Custom)];

    public AudioDeviceInfo? GetDefault(DeviceDirection direction) => List(direction)[0];

    public bool TryOpen(DeviceDirection direction, DeviceStreamOptions options, IDeviceEngine engine, out IOpenDevice? stream, out string reason)
    {
        Mark();
        reason = string.Empty;
        stream = null;
        if (FailOpens > 0)
        {
            FailOpens--;
            reason = "not yet";
            return false;
        }

        Opens++;
        Live = DriveSelf
            ? new SteppedStream(this, engine, options)
            : new SteppedManualStream(this, engine, options);
        stream = Live;
        return true;
    }

    public class SteppedStream(SteppedBackend owner, IDeviceEngine engine, DeviceStreamOptions options) : IOpenDevice
    {
        protected readonly SteppedBackend Owner = owner;
        protected readonly IDeviceEngine Engine = engine;
        protected readonly float[] Block = new float[128 * options.Channels];
        private long _frames;
        protected bool Running;
        public int Capacity = 2048; // the "worklet ring"
        public int Queued;

        public AudioDeviceFormat Format { get; } = new(options.SampleRate, options.Channels);

        public AudioDeviceInfo? Device => Owner.List(DeviceDirection.Output)[0];

        public int PeriodFrames => 128;

        public int BufferFrames => Capacity;

        public long FramesPlayed => Volatile.Read(ref _frames) - Queued;

        public int QueuedFrames => Queued;

        public string? Detail => "stepped";

        public bool Start(out string reason)
        {
            Owner.Mark();
            reason = string.Empty;
            Running = true;
            return true;
        }

        public void Pause()
        {
            Owner.Mark();
            Running = false;
        }

        public void Dispose()
        {
            Owner.Mark();
            GC.SuppressFinalize(this);
        }

        /// <summary>The host's pump: render one period into the "ring".</summary>
        public int Step()
        {
            Owner.Mark();
            if (!Running || Queued + 128 > Capacity)
            {
                return 0;
            }

            Engine.Render(Block);
            Queued += 128;
            _frames += 128;
            return 128;
        }

        /// <summary>The "audio thread" taking audio out of the ring.</summary>
        public void Consume(int frames) => Queued = Math.Max(0, Queued - frames);

        public void Lose() => Engine.NotifyLost("page went away");
    }

    public sealed class SteppedManualStream(SteppedBackend owner, IDeviceEngine engine, DeviceStreamOptions options)
        : SteppedStream(owner, engine, options), IManualDevice
    {
        public int Advance(int frames)
        {
            int done = 0;
            while (done < frames && Step() > 0)
            {
                done += 128;
            }

            return done;
        }

        public int WantedFrames => Capacity - Queued;
    }
}

public class SingleThreadTests
{
    [Fact]
    public void CustomManualBackend_RunsEverythingOnTheCallersThread_WithNoThreadStarted()
    {
        int me = Environment.CurrentManagedThreadId;
        var backend = new SteppedBackend();
        var tone = new ToneSource(48_000, 2, 0.3f);
        var eventThreads = new List<int>();
        var source = new ThreadRecordingSource(tone, backend);
        using var output = AudioOutputDevice.Open(source, null, backend);
        output.Event += _ => eventThreads.Add(Environment.CurrentManagedThreadId);
        output.Start();
        Assert.Equal(DeviceState.Running, output.State);

        // A host loop: ask what the device wants, step that much, let the "audio thread" take some.
        long rendered = 0;
        for (int i = 0; i < 40; i++)
        {
            int wanted = output.WantedFrames;
            Assert.True(wanted > 0);
            rendered += output.Advance(wanted);
            backend.Live!.Consume(512);
        }

        Assert.True(rendered >= 40 * 512);

        // Loss and recovery happen inside Advance: no Pump, no thread.
        backend.Live!.Lose();
        Assert.True(output.Advance(256) > 0);
        Assert.Equal(2, backend.Opens);

        var c = output.Counters;
        Assert.Equal(1, c.DeviceLosses);
        Assert.Equal(1, c.Reopens);
        Assert.Equal(c.FramesWritten, c.FramesRendered);
        Assert.True(c.PeakWritten > 0.25f);
        Assert.True(output.TryGetEvent(out var first) && first.Kind == DeviceEventKind.DeviceLost);

        Assert.False(output.EngineForTests.HasSupervisorThread);
        Assert.NotEmpty(eventThreads);
        Assert.All(eventThreads, t => Assert.Equal(me, t));
        lock (backend.Threads)
        {
            Assert.Equal([me], backend.Threads.ToArray()); // open, start, pause, render, dispose: all on this thread
        }

        output.Dispose();
        Assert.False(output.EngineForTests.HasSupervisorThread);
    }

    [Fact]
    public void CustomManualBackend_ThatDrivesItsOwnStream_IsNotRenderedByAdvance()
    {
        var backend = new SteppedBackend { DriveSelf = true };
        var tone = new ToneSource(48_000, 2, 0.3f);
        using var output = AudioOutputDevice.Open(tone, null, backend);
        output.Start();
        Assert.Equal(0, output.Advance(1000)); // nothing here; the host's own callback renders
        Assert.Equal(0, tone.Rendered);
        Assert.Equal(0, output.Counters.SilentFrames);
        Assert.Equal(128, backend.Live!.Step());
        Assert.Equal(128, output.Counters.FramesWritten);

        // Supervisor work still runs from Advance.
        backend.Live.Lose();
        output.Advance(0);
        Assert.Equal(2, backend.Opens);
        Assert.Equal(1, output.Counters.Reopens);
    }

    [Fact]
    public void ManualDevice_RetriesAMissingDeviceFromAdvance_WithoutAThread()
    {
        var backend = new SteppedBackend { FailOpens = 1 };
        using var output = AudioOutputDevice.Open(new ToneSource(48_000, 2, 0.3f), null, backend);
        output.Start();
        Assert.Equal(DeviceState.NoDevice, output.State);
        Assert.Equal(300, output.Advance(300)); // no device: rendered and discarded, by the caller's steps rather than a wall clock
        Assert.Equal(300, output.Counters.SilentFrames);
        Thread.Sleep(600);
        Assert.True(output.Advance(128) > 0);
        Assert.Equal(DeviceState.Running, output.State);
        Assert.True(output.TryGetEvent(out var e) && e.Kind == DeviceEventKind.DeviceRestored);
        Assert.Equal(300, output.Counters.SilentFrames); // no wall-clock pacing crept in
    }

    [Fact]
    public void ManualAdvance_TakesNoLock_WhenNothingIsPending()
    {
        using var output = AudioOutputDevice.Open(new ToneSource(48_000, 2, 0.3f), null, new ManualBackend());
        output.Start();
        var engine = output.EngineForTests;
        lock (engine.LifecycleLock)
        {
            lock (engine.FoldLock)
            {
                int ran = 0;
                var t = new Thread(() => ran = output.Advance(256));
                t.Start();
                Assert.True(t.Join(5000), "Advance waited for a control lock with nothing to do");
                Assert.Equal(256, ran);
            }
        }
    }

    [Fact]
    public void SourceFormat_ReachesTheBackend_NotTheDefaultsOfTheOptions()
    {
        var backend = new ManualBackend();
        using var output = AudioOutputDevice.Open(new ToneSource(44_100, 1, 0.3f), null, backend);
        output.Start();
        Assert.Equal(DeviceState.Running, output.State);
        Assert.Equal(new AudioDeviceFormat(44_100, 1), output.DeviceFormat);
        Assert.Equal(256, output.Advance(256));
    }

    private sealed class ThreadRecordingSource(ToneSource inner, SteppedBackend backend) : IAudioSource
    {
        public int SampleRate => inner.SampleRate;

        public int Channels => inner.Channels;

        public void Render(Span<float> output)
        {
            backend.Mark();
            inner.Render(output);
        }
    }
}
