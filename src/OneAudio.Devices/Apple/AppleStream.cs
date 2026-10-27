using System.Threading;
using OneAudio.Devices.Interop;

namespace OneAudio.Devices;

/// <summary>
/// One open Apple audio unit. Output: the unit pulls blocks at the requested rate (it converts to the hardware rate itself) and this class
/// asks the engine for them, mapping channels when the device has a different count. Input: the unit delivers blocks at the hardware rate
/// and channel count; this class maps channels, resamples when the rates differ, and hands the engine the requested format. Frames played
/// come from the device clock: the sample time of the first block and the device's current time tell how much of what was handed over the
/// hardware has consumed.
/// </summary>
internal sealed class AppleStream : IOpenDevice, IAudioUnitClient
{
    private const int Idle = 0;
    private const int Running = 1;
    private const int Draining = 2;
    private const int ChunkFrames = 4096;

    private readonly IDeviceEngine _engine;
    private readonly bool _output;
    private readonly int _channels;
    private readonly int _rate;
    private readonly AppleSessionCategory _category;
    private readonly List<IDisposable> _listeners = [];
    private AudioUnitHost? _host;
    private float[] _scratch = [];
    private IResampler? _resampler;
    private float[] _resampled = [];
    private bool _iosSession;

    private int _state;
    private int _inCallback;
    private int _haveOrigin;
    private long _originBits;
    private long _runFrames;
    private long _runPlayed;
    private long _base;

    private AppleStream(IDeviceEngine engine, bool output, AudioDeviceFormat format, AudioDeviceInfo device, AppleSessionCategory category)
    {
        _engine = engine;
        _output = output;
        _channels = format.Channels;
        _rate = format.SampleRate;
        Format = format;
        Device = device;
        _category = category;
    }

    public AudioDeviceFormat Format { get; }

    public AudioDeviceInfo? Device { get; }

    public int PeriodFrames { get; private set; }

    public int BufferFrames { get; private set; }

    public string? Detail { get; private set; }

    public long FramesPlayed => Volatile.Read(ref _base) + RunPlayed();

    public int QueuedFrames => _output ? (int)Math.Clamp(Volatile.Read(ref _runFrames) - RunPlayed(), 0, int.MaxValue) : 0;

    /// <summary>Creates the unit and the stream over it, not started. <paramref name="deviceObject"/> is the Core Audio device on macOS and ignored on iOS.</summary>
    public static AppleStream? TryCreate(IDeviceEngine engine, bool output, DeviceStreamOptions options, AudioDeviceInfo device, uint deviceObject, bool followDefault, out string reason)
    {
        var stream = new AppleStream(engine, output, new AudioDeviceFormat(options.SampleRate, options.Channels), device, options.AppleSession);
        if (OperatingSystem.IsIOS())
        {
            if (!AppleSession.TryActivate(options.AppleSession, !output, out reason))
            {
                return null;
            }

            stream._iosSession = true;
        }

        int hint = 0;
        if (OperatingSystem.IsMacOS() && (options.PeriodFrames > 0 || options.BufferFrames > 0) && device.DefaultSampleRate > 0)
        {
            int period = options.PeriodFrames > 0 ? options.PeriodFrames : options.BufferFrames / 4;
            hint = (int)Math.Max(16L, (long)period * device.DefaultSampleRate / options.SampleRate);
        }

        var host = AudioUnitHost.TryCreate(output, deviceObject, options.SampleRate, hint, stream, out reason);
        if (host is null)
        {
            return null;
        }

        stream.Attach(host, deviceObject, followDefault);
        return stream;
    }

    private void Attach(AudioUnitHost host, uint deviceObject, bool followDefault)
    {
        _host = host;
        PrepareConversion(host.HwRate);
        PeriodFrames = (int)Math.Max(1L, (long)host.HwPeriodFrames * _rate / host.HwRate);
        BufferFrames = PeriodFrames * 2 + (int)((long)host.HwSafetyFrames * _rate / host.HwRate);
        string route = _iosSession ? "; " + AppleSession.Route(!_output) : string.Empty;
        Detail = $"coreaudio {(_output ? "output" : "input")} hardware {host.HwRate} Hz x{host.HwChannels}, period {host.HwPeriodFrames} frames{route}";

        if (OperatingSystem.IsMacOS())
        {
            if (followDefault && Hal.ListenDefault(_output, _engine.NotifyDefaultChanged) is { } defaults)
            {
                _listeners.Add(defaults);
            }

            uint device = deviceObject;
            if (Hal.ListenAlive(device, () =>
            {
                if (!Hal.IsAlive(device))
                {
                    _engine.NotifyLost("the audio device was removed");
                }
            }) is { } alive)
            {
                _listeners.Add(alive);
            }
        }
        else
        {
            AppleSession.Subscribe(_engine);
        }
    }

    // Buffers for the channel mapping and, on capture, the resampler from the hardware rate to the requested one; all allocated here so the
    // real-time callbacks allocate nothing.
    private void PrepareConversion(int hardwareRate)
    {
        _scratch = new float[ChunkFrames * _channels];
        if (!_output && hardwareRate != _rate)
        {
            _resampler = AudioResampler.Create(hardwareRate, _rate, _channels, ResamplerQuality.PolyphaseFast);
            _resampled = new float[_resampler.MaxOutputFrames(ChunkFrames) * _channels];
        }
    }

    /// <summary>A stream with no audio unit behind it, running, so the capture conversion can be driven with synthetic blocks.</summary>
    internal static AppleStream CreateDetachedCapture(IDeviceEngine engine, AudioDeviceFormat format, int hardwareRate)
    {
        var stream = new AppleStream(engine, false, format, default, AppleSessionCategory.Ambient);
        stream.PrepareConversion(hardwareRate);
        Volatile.Write(ref stream._state, Running);
        return stream;
    }

    public bool Start(out string reason)
    {
        var host = _host;
        if (host is null)
        {
            reason = "the stream is closed";
            return false;
        }

        if (_iosSession && !AppleSession.TryActivate(_category, !_output, out reason))
        {
            return false;
        }

        if (Volatile.Read(ref _state) == Running)
        {
            reason = string.Empty;
            return true;
        }

        Volatile.Write(ref _haveOrigin, 0);
        _runFrames = 0;
        _runPlayed = 0;
        Volatile.Write(ref _state, Running);
        int status = host.Start();
        if (status != 0)
        {
            Volatile.Write(ref _state, Idle);
            reason = $"the audio unit would not start ({status})";
            return false;
        }

        reason = string.Empty;
        return true;
    }

    public void Pause()
    {
        var host = _host;
        if (host is null || Volatile.Read(ref _state) == Idle)
        {
            return;
        }

        if (_output)
        {
            // Let what the hardware already holds play out so the position stays exact (as a drain does elsewhere): the callbacks go on
            // running but deliver silence without asking the engine.
            Volatile.Write(ref _state, Draining);
            long deadline = Environment.TickCount64 + 250;
            while (RunPlayed() < Volatile.Read(ref _runFrames) && Environment.TickCount64 < deadline)
            {
                Thread.Sleep(1);
            }
        }

        Volatile.Write(ref _state, Idle);
        _ = host.Stop();
        var wait = new SpinWait();
        long limit = Environment.TickCount64 + 1000;
        while (Volatile.Read(ref _inCallback) != 0 && Environment.TickCount64 < limit)
        {
            wait.SpinOnce();
        }

        long played = _output ? Math.Min(RunPlayed(), Volatile.Read(ref _runFrames)) : Volatile.Read(ref _runFrames);
        Volatile.Write(ref _base, Volatile.Read(ref _base) + played);
        _runFrames = 0;
        _runPlayed = 0;
        Volatile.Write(ref _haveOrigin, 0);
    }

    public void Dispose()
    {
        var host = _host;
        if (host is null)
        {
            return;
        }

        Volatile.Write(ref _state, Idle);
        foreach (var listener in _listeners)
        {
            listener.Dispose();
        }

        _listeners.Clear();
        if (!OperatingSystem.IsMacOS())
        {
            AppleSession.Unsubscribe(_engine);
        }

        host.Dispose();
        _host = null;
    }

    /// <summary>
    /// Frames the device has consumed in the current run, from its own clock; never decreases within a run and never exceeds what was
    /// handed over. The hardware reads a block one buffer plus the device's safety offset after the callback that filled it ran, so the
    /// frame at position <c>w</c> is consumed at <c>origin + w + delay</c> on the device clock. The origin is the earliest value any
    /// callback implies (callbacks can only run late), which removes the jitter of the first one.
    /// </summary>
    private long RunPlayed()
    {
        var host = _host;
        long last = Volatile.Read(ref _runPlayed);
        if (host is null || !_output)
        {
            return _output ? last : Volatile.Read(ref _runFrames);
        }

        if (Volatile.Read(ref _haveOrigin) == 0)
        {
            return last;
        }

        double now = host.DeviceSampleTimeNow();
        double origin = BitConverter.Int64BitsToDouble(Volatile.Read(ref _originBits));
        double elapsed = now - origin - (host.HwPeriodFrames + host.HwSafetyFrames);
        if (double.IsNaN(now) || elapsed <= 0)
        {
            return last;
        }

        long played = Math.Min((long)(elapsed * _rate / host.HwRate), Volatile.Read(ref _runFrames));
        while (played > last)
        {
            long seen = Interlocked.CompareExchange(ref _runPlayed, played, last);
            if (seen == last)
            {
                return played;
            }

            last = seen;
        }

        return last;
    }

    // ---- real-time callbacks (the system's audio thread)

    public void RenderOutput(Span<float> buffer, int frames, double sampleTime)
    {
        Interlocked.Increment(ref _inCallback);
        try
        {
            if (Volatile.Read(ref _state) != Running)
            {
                buffer.Clear();
                return;
            }

            // What the device clock read when this block was asked for, less the time the frames before it account for.
            var host = _host!;
            double now = host.DeviceSampleTimeNow();
            if (!double.IsNaN(now))
            {
                double candidate = now - (Volatile.Read(ref _runFrames) * (double)host.HwRate / _rate);
                if (Volatile.Read(ref _haveOrigin) == 0 || candidate < BitConverter.Int64BitsToDouble(Volatile.Read(ref _originBits)))
                {
                    Volatile.Write(ref _originBits, BitConverter.DoubleToInt64Bits(candidate));
                    Volatile.Write(ref _haveOrigin, 1);
                }
            }

            int hwChannels = buffer.Length / Math.Max(frames, 1);
            if (hwChannels == _channels)
            {
                _engine.Render(buffer);
            }
            else
            {
                for (int done = 0; done < frames;)
                {
                    int n = Math.Min(frames - done, ChunkFrames);
                    var block = _scratch.AsSpan(0, n * _channels);
                    _engine.Render(block);
                    AppleChannelMap.Convert(block, _channels, buffer.Slice(done * hwChannels, n * hwChannels), hwChannels, n);
                    done += n;
                }
            }

            Interlocked.Add(ref _runFrames, frames);
        }
        finally
        {
            Interlocked.Decrement(ref _inCallback);
        }
    }

    public void DeliverInput(ReadOnlySpan<float> buffer, int frames)
    {
        Interlocked.Increment(ref _inCallback);
        try
        {
            if (Volatile.Read(ref _state) != Running)
            {
                return;
            }

            int hwChannels = buffer.Length / Math.Max(frames, 1);
            var resampler = _resampler;
            for (int done = 0; done < frames;)
            {
                int n = Math.Min(frames - done, ChunkFrames);
                ReadOnlySpan<float> block;
                if (hwChannels == _channels)
                {
                    block = buffer.Slice(done * hwChannels, n * hwChannels);
                }
                else
                {
                    var mapped = _scratch.AsSpan(0, n * _channels);
                    AppleChannelMap.Convert(buffer.Slice(done * hwChannels, n * hwChannels), hwChannels, mapped, _channels, n);
                    block = mapped;
                }

                if (resampler is null)
                {
                    _engine.Capture(block);
                    Interlocked.Add(ref _runFrames, n);
                }
                else
                {
                    while (!block.IsEmpty)
                    {
                        resampler.Process(block, _resampled, out int used, out int produced);
                        if (produced > 0)
                        {
                            _engine.Capture(_resampled.AsSpan(0, produced * _channels));
                            Interlocked.Add(ref _runFrames, produced);
                        }

                        if (used == 0 && produced == 0)
                        {
                            break;
                        }

                        block = block[(used * _channels)..];
                    }
                }

                done += n;
            }
        }
        finally
        {
            Interlocked.Decrement(ref _inCallback);
        }
    }
}
