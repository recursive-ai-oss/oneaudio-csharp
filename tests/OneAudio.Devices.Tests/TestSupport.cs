using System.Diagnostics;
using OneAudio.Devices;
using Xunit;

[assembly: CollectionBehavior(CollectionBehavior.CollectionPerAssembly)]

namespace OneAudio.Devices.Tests;

/// <summary>A deterministic source: a sine at a fixed amplitude, continuing across blocks.</summary>
internal sealed class ToneSource(int rate, int channels, float amplitude) : IAudioSource
{
    private long _frame;

    public int SampleRate => rate;

    public int Channels => channels;

    public long Rendered => _frame;

    public bool Throw { get; set; }

    public void Render(Span<float> output)
    {
        if (Throw)
        {
            throw new InvalidOperationException("boom");
        }

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

/// <summary>
/// A backend that owns a real thread per stream and paces it at about real time, like a hardware API: it checks the supervisor, the
/// silent fallback and the polling paths that a manual backend cannot reach.
/// </summary>
internal sealed class ThreadedBackend : IAudioDeviceBackend
{
    private readonly object _gate = new();
    private string _default = "t0";
    private int _failOpens;
    private ThreadedStream? _live;
    public int Opens;

    public string Name => "threaded";

    public DeviceBackendKind Kind => DeviceBackendKind.Custom;

    public bool IsManual => false;

    public bool NotifiesDeviceChanges { get; set; }

    public bool IsAvailable => true;

    public int FailOpens { get => Volatile.Read(ref _failOpens); set => Volatile.Write(ref _failOpens, value); }

    public IReadOnlyList<AudioDeviceInfo> List(DeviceDirection direction) =>
    [
        new("t0", "t0", direction, _default == "t0", 48000, 2, 64, DeviceBackendKind.Custom),
        new("t1", "t1", direction, _default == "t1", 48000, 2, 64, DeviceBackendKind.Custom),
    ];

    public AudioDeviceInfo? GetDefault(DeviceDirection direction) => List(direction).First(d => d.IsDefault);

    public void SetDefault(string id)
    {
        lock (_gate)
        {
            _default = id;
        }
    }

    public void Lose() => _live?.Lose();

    public bool TryOpen(DeviceDirection direction, DeviceStreamOptions options, IDeviceEngine engine, out IOpenDevice? stream, out string reason)
    {
        stream = null;
        reason = string.Empty;
        if (FailOpens > 0)
        {
            FailOpens--;
            reason = "refused";
            return false;
        }

        Interlocked.Increment(ref Opens);
        string id = options.DeviceId ?? _default;
        var s = new ThreadedStream(engine, new AudioDeviceFormat(options.SampleRate, options.Channels), List(direction).First(d => d.Id == id));
        _live = s;
        stream = s;
        return true;
    }

    private sealed class ThreadedStream : IOpenDevice
    {
        private readonly IDeviceEngine _engine;
        private readonly Thread _thread;
        private readonly float[] _buffer;
        private long _frames;
        private int _run;
        private int _lost;
        private int _quit;

        public ThreadedStream(IDeviceEngine engine, AudioDeviceFormat format, AudioDeviceInfo device)
        {
            _engine = engine;
            Format = format;
            Device = device;
            _buffer = new float[480 * format.Channels];
            _thread = new Thread(Loop) { IsBackground = true, Name = "threaded-fake" };
            _thread.Start();
        }

        public AudioDeviceFormat Format { get; }

        public AudioDeviceInfo? Device { get; }

        public int PeriodFrames => 480;

        public int BufferFrames => 1920;

        public long FramesPlayed => Math.Max(0, Volatile.Read(ref _frames) - 240);

        public int QueuedFrames => 240;

        public string? Detail => null;

        public bool Start(out string reason)
        {
            reason = string.Empty;
            Volatile.Write(ref _run, 1);
            return true;
        }

        public void Pause()
        {
            Volatile.Write(ref _run, 0);
            lock (_buffer)
            {
                // Taking the lock waits for a callback in progress.
            }
        }

        public void Lose()
        {
            Volatile.Write(ref _lost, 1);
            _engine.NotifyLost("fake loss");
        }

        public void Dispose()
        {
            Volatile.Write(ref _quit, 1);
            _thread.Join();
        }

        private void Loop()
        {
            var next = Stopwatch.GetTimestamp();
            long period = Stopwatch.Frequency / 100; // 10 ms for 480 frames at 48 kHz
            while (Volatile.Read(ref _quit) == 0)
            {
                next += period;
                while (Stopwatch.GetTimestamp() < next && Volatile.Read(ref _quit) == 0)
                {
                    Thread.Sleep(1);
                }

                if (Volatile.Read(ref _run) == 0 || Volatile.Read(ref _lost) != 0)
                {
                    continue;
                }

                lock (_buffer)
                {
                    if (Volatile.Read(ref _run) == 0)
                    {
                        continue;
                    }

                    _engine.Render(_buffer);
                    Volatile.Write(ref _frames, _frames + 480);
                }
            }
        }
    }
}

internal static class Wait
{
    public static bool Until(Func<bool> condition, int milliseconds = 5000)
    {
        var end = Stopwatch.GetTimestamp() + (Stopwatch.Frequency * milliseconds / 1000);
        while (Stopwatch.GetTimestamp() < end)
        {
            if (condition())
            {
                return true;
            }

            Thread.Sleep(5);
        }

        return condition();
    }

    public static List<DeviceEvent> Drain(Func<(bool, DeviceEvent)> next)
    {
        var list = new List<DeviceEvent>();
        while (true)
        {
            var (ok, e) = next();
            if (!ok)
            {
                return list;
            }

            list.Add(e);
        }
    }
}
