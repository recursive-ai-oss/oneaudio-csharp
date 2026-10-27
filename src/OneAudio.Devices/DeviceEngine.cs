using System.Diagnostics;
using System.Threading;
using OneAudio.Loudness;

namespace OneAudio.Devices;

/// <summary>
/// Everything a device does apart from talking to the operating system: the render and capture hand-over, limiting, counters, the played-peak
/// bookkeeping, and the supervisor that reopens a lost device, follows the default device and keeps time while no device is open.
/// <see cref="AudioOutputDevice"/> and <see cref="AudioInputDevice"/> are thin faces over it.
/// <para>
/// Threads: the stream's own real-time thread runs <see cref="Render"/>/<see cref="Capture"/> and takes no lock. One supervisor thread (none for a
/// manual backend, see <see cref="Pump"/>) does the slow work. Control calls hold <c>_lifecycle</c> briefly and never wait for the audio thread
/// except through <see cref="IOpenDevice.Pause"/>, which guarantees the callbacks have ended.
/// </para>
/// </summary>
internal sealed class DeviceEngine : IDeviceEngine, IDisposable
{
    private const int TickMilliseconds = 5;
    private static readonly long RetryTicks = Stopwatch.Frequency / 2;
    private static readonly long DefaultPollTicks = Stopwatch.Frequency;
    private const int MaxSilentChunkFrames = 2048;

    private readonly DeviceDirection _direction;
    private readonly IAudioDeviceBackend _backend;
    private readonly DeviceStreamOptions _options;
    private readonly AudioDeviceFormat _format;
    private readonly IAudioSource? _source;
    private readonly RenderCallback? _renderCallback;
    private readonly CaptureCallback? _captureCallback;
    private readonly FloatRing? _captureRing;
    private readonly PeakRing _peaks = new(1024);
    private readonly float[] _silent;
    private LoudnessMeter? _meter;
    private RateAdapter? _adapter;
    private int _deviceRate;
    private int _pendingEvents;
    private readonly EventQueue _events = new(64);
    private readonly Lock _lifecycle = new();
    private readonly Lock _fold = new();
    private readonly ManualResetEventSlim _wake = new(false);
    private List<DeviceEvent> _batch = new(16);
    private List<DeviceEvent> _batchSwap = new(16);
    private Thread? _thread;

    // State written under _lifecycle, read anywhere.
    private IOpenDevice? _stream;
    private bool _started;
    private bool _everOpened;
    private bool _userSuspended;
    private bool _systemSuspended;
    private bool _gap; // a device was lost or missing since the last successful open
    private string? _currentId;
    private string? _lastId;
    private string? _requestedId;
    private string? _lastError;
    private long _nextRetry;
    private long _nextDefaultPoll;
    private long _silentOrigin;
    private long _silentPaced;
    private int _disposed;

    // Notification flags set by device and OS threads.
    private int _pendingLost;
    private string? _lostReason;
    private int _pendingDefault;
    private long _interruptBegan;
    private long _interruptEnded;
    private long _seenBegan;
    private long _seenEnded;
    private long _xrunFramesPending;

    // Counters. Single logical writer each, but every update is atomic so the hand-over between threads is safe.
    private long _framesRendered;
    private long _framesWritten;
    private long _streamWritten; // frames handed to the current stream (render thread; zeroed at open)
    private long _playedBase;    // played frames of streams already closed
    private long _playedHint;
    private int _peakWrittenBits;
    private int _peakPlayedBits;
    private long _underruns;
    private long _overruns;
    private long _silentFrames;
    private long _faults;
    private long _losses;
    private long _reopens;
    private long _defaultSwitches;
    private long _maxRenderTicks;
    private long _allocBytes;
    private long _callbacksSeen;
    private long _eventsDropped;
    private int _muted;
    private long _mutedFrames;
    private long _deviceUnderrunsBase;
    private long _streamDeviceUnderruns;
    private Action<long>? _blockHook;
    private double _maxMomentary = double.NegativeInfinity;
    private double _maxShortTerm = double.NegativeInfinity;

    // Meter snapshot.
    private int _meterReset;
    private double _momentary = double.NegativeInfinity;
    private double _shortTerm = double.NegativeInfinity;
    private double _integrated = double.NegativeInfinity;
    private double _truePeak = double.NegativeInfinity;
    private long _meteredFrames;

    public DeviceEngine(
        DeviceDirection direction,
        IAudioDeviceBackend backend,
        DeviceStreamOptions options,
        AudioDeviceFormat format,
        IAudioSource? source,
        RenderCallback? renderCallback,
        CaptureCallback? captureCallback)
    {
        _direction = direction;
        _backend = backend;
        // Backends read the rate and channel count from the options, so they must say what the program renders at.
        _options = options with { SampleRate = format.SampleRate, Channels = format.Channels };
        _format = format;
        _deviceRate = format.SampleRate;
        _source = source;
        _renderCallback = renderCallback;
        _captureCallback = captureCallback;
        _requestedId = options.DeviceId;
        _silent = direction == DeviceDirection.Output ? new float[MaxSilentChunkFrames * format.Channels] : [];
        if (direction == DeviceDirection.Input && captureCallback is null)
        {
            int frames = Math.Max(256, (int)((long)format.SampleRate * options.CaptureBufferMilliseconds / 1000));
            _captureRing = new FloatRing(frames, format.Channels);
        }

        if (direction == DeviceDirection.Output && options.Metering)
        {
            _meter = new LoudnessMeter(format.SampleRate, format.Channels, ChannelMask.None, truePeak: true, loudnessRange: false);
        }
    }

    public AudioDeviceFormat Format => _format;

    public AudioDeviceFormat DeviceFormat => Volatile.Read(ref _stream)?.Format ?? _format;

    public Lock LifecycleLock => _lifecycle;

    public Lock FoldLock => _fold;

    public IAudioDeviceBackend Backend => _backend;

    public bool IsManual => _backend.IsManual;

    public bool HasSupervisorThread => _thread is not null;

    public bool IsUserSuspended => Volatile.Read(ref _userSuspended);

    public bool IsSystemSuspended => Volatile.Read(ref _systemSuspended);

    public bool Muted
    {
        get => Volatile.Read(ref _muted) != 0;
        set => Volatile.Write(ref _muted, value ? 1 : 0);
    }

    public Action<long>? BlockHook
    {
        get => Volatile.Read(ref _blockHook);
        set => Volatile.Write(ref _blockHook, value);
    }

    public FloatRing? CaptureRing => _captureRing;

    public string? LastError => Volatile.Read(ref _lastError);

    public AudioDeviceInfo? CurrentDevice => Volatile.Read(ref _stream)?.Device;

    public string? StateText => Volatile.Read(ref _stream)?.StateText;

    /// <summary>The stream's route extras, followed by its state word when it has one.</summary>
    public string? DetailText
    {
        get
        {
            var s = Volatile.Read(ref _stream);
            string? detail = s?.Detail;
            string? state = s?.StateText;
            if (string.IsNullOrEmpty(state))
            {
                return detail;
            }

            return string.IsNullOrEmpty(detail) ? state : detail + "; " + state;
        }
    }

    public DeviceState State
    {
        get
        {
            if (Volatile.Read(ref _disposed) != 0)
            {
                return DeviceState.Stopped;
            }

            if (!Volatile.Read(ref _started))
            {
                return DeviceState.Created;
            }

            if (Volatile.Read(ref _userSuspended) || Volatile.Read(ref _systemSuspended))
            {
                return DeviceState.Suspended;
            }

            return Volatile.Read(ref _stream) is null ? DeviceState.NoDevice : DeviceState.Running;
        }
    }

    public event Action<DeviceEvent>? Event;

    // ------------------------------------------------------------------ the real-time side (device thread)

    public void Render(Span<float> interleaved)
    {
        long startTicks = Stopwatch.GetTimestamp();
        long allocBefore = GC.GetAllocatedBytesForCurrentThread();

        int ch = _format.Channels;
        int whole = interleaved.Length / ch * ch;
        if (whole < interleaved.Length)
        {
            interleaved[whole..].Clear();
        }

        var block = interleaved[..whole];
        long streamStart = Volatile.Read(ref _streamWritten);
        RunHook();
        var adapter = Volatile.Read(ref _adapter);
        if (Volatile.Read(ref _muted) != 0)
        {
            // Silence without asking the source, so its clock stands still while muted.
            block.Clear();
            Interlocked.Add(ref _mutedFrames, whole / ch);
        }
        else if (adapter is null)
        {
            RunSource(block, streamStart);
            Interlocked.Add(ref _framesRendered, whole / ch);
        }
        else
        {
            adapter.Fill(block);
        }

        float peak = OutputLimiter.Apply(block, _options.Limiter);

        long frames = whole / ch;
        long end = streamStart + frames;
        Volatile.Write(ref _streamWritten, end);
        Interlocked.Add(ref _framesWritten, frames);
        RaiseMax(ref _peakWrittenBits, peak);
        _peaks.Add(end, peak);
        if (_meter is not null)
        {
            Meter(block);
        }

        FinishCallback(startTicks, allocBefore);
    }

    public void Capture(ReadOnlySpan<float> interleaved)
    {
        long startTicks = Stopwatch.GetTimestamp();
        long allocBefore = GC.GetAllocatedBytesForCurrentThread();

        int ch = _format.Channels;
        var block = interleaved[..(interleaved.Length / ch * ch)];
        long frames = block.Length / ch;
        long streamStart = Volatile.Read(ref _streamWritten);
        RunHook();
        float peak = 0f;
        for (int i = 0; i < block.Length; i++)
        {
            float a = MathF.Abs(block[i]);
            if (a > peak)
            {
                peak = a;
            }
        }

        Volatile.Write(ref _streamWritten, streamStart + frames);
        Interlocked.Add(ref _framesRendered, frames);
        Interlocked.Add(ref _framesWritten, frames);
        RaiseMax(ref _peakWrittenBits, peak);
        RaiseMax(ref _peakPlayedBits, peak);
        if (_captureRing is not null)
        {
            if (_captureRing.Write(block) < frames)
            {
                Interlocked.Increment(ref _overruns);
                Interlocked.Add(ref _xrunFramesPending, frames);
            }
        }
        else
        {
            // The user's callback must not break the device thread; a throw is counted and the block is dropped.
            try
            {
                _captureCallback!(block, new RenderContext(streamStart, Volatile.Read(ref _playedHint)));
            }
            catch (Exception)
            {
                Interlocked.Increment(ref _faults);
            }
        }

        FinishCallback(startTicks, allocBefore);
    }

    /// <summary>Calls the per-block hook with the frames handed to (or received from) the device before this block; a throw is counted.</summary>
    private void RunHook()
    {
        var hook = Volatile.Read(ref _blockHook);
        if (hook is null)
        {
            return;
        }

        try
        {
            hook(Interlocked.Read(ref _framesWritten));
        }
        catch (Exception)
        {
            Interlocked.Increment(ref _faults);
        }
    }

    private void RunSource(Span<float> block, long streamStart)
    {
        // The source is the program's code running on the device thread. A throw there must not take the thread (and the process)
        // down: count it and play silence for the block.
        try
        {
            if (_source is not null)
            {
                _source.Render(block);
            }
            else
            {
                _renderCallback!(block, new RenderContext(streamStart, Volatile.Read(ref _playedHint)));
            }
        }
        catch (Exception)
        {
            block.Clear();
            Interlocked.Increment(ref _faults);
        }
    }

    private void FinishCallback(long startTicks, long allocBefore)
    {
        long took = Stopwatch.GetTimestamp() - startTicks;
        if (took > Volatile.Read(ref _maxRenderTicks))
        {
            Volatile.Write(ref _maxRenderTicks, took);
        }

        // The probe costs two reads of a per-thread counter (about the noise of a callback's own time), so it is always on.
        if (Interlocked.Increment(ref _callbacksSeen) > _options.AllocationWarmupBlocks)
        {
            Interlocked.Add(ref _allocBytes, GC.GetAllocatedBytesForCurrentThread() - allocBefore);
        }
    }

    private void Meter(ReadOnlySpan<float> block)
    {
        if (Interlocked.Exchange(ref _meterReset, 0) != 0)
        {
            _meter!.Reset();
            Volatile.Write(ref _meteredFrames, 0);
        }

        var meter = _meter!;
        meter.Process(block);
        Volatile.Write(ref _momentary, meter.MomentaryLufs);
        Volatile.Write(ref _shortTerm, meter.ShortTermLufs);
        Volatile.Write(ref _integrated, meter.IntegratedLufs);
        Volatile.Write(ref _truePeak, meter.TruePeakDbtp);
        Volatile.Write(ref _maxMomentary, meter.MaxMomentaryLufs);
        Volatile.Write(ref _maxShortTerm, meter.MaxShortTermLufs);
        Interlocked.Add(ref _meteredFrames, block.Length / _format.Channels);
    }

    private static void RaiseMax(ref int bits, float value)
    {
        // Writers are the one render thread and, for played peaks, the fold lock holder; a lost race only delays a peak by one block.
        if (value > BitConverter.Int32BitsToSingle(Volatile.Read(ref bits)))
        {
            Volatile.Write(ref bits, BitConverter.SingleToInt32Bits(value));
        }
    }

    public LoudnessSnapshot Loudness => new(
        Volatile.Read(ref _momentary), Volatile.Read(ref _shortTerm), Volatile.Read(ref _integrated), Volatile.Read(ref _truePeak),
        Volatile.Read(ref _maxMomentary), Volatile.Read(ref _maxShortTerm), Interlocked.Read(ref _meteredFrames));

    public void ResetLoudness()
    {
        Volatile.Write(ref _meterReset, 1);
    }

    public void ResetPeaks()
    {
        // Blocks the device has already finished belong to the time before the reset; take them in first so they do not show up afterwards.
        lock (_fold)
        {
            FoldPlayed();
            Volatile.Write(ref _peakWrittenBits, 0);
            Volatile.Write(ref _peakPlayedBits, 0);
        }
    }

    // ------------------------------------------------------------------ notifications (any thread, lock-free)

    public void NotifyLost(string? reason)
    {
        Volatile.Write(ref _lostReason, reason);
        Volatile.Write(ref _pendingLost, 1);
    }

    public void NotifyXrun(int frames)
    {
        if (_direction == DeviceDirection.Output)
        {
            Interlocked.Increment(ref _underruns);
        }
        else
        {
            Interlocked.Increment(ref _overruns);
        }

        Interlocked.Add(ref _xrunFramesPending, Math.Max(frames, 1));
    }

    public void NotifyDeviceUnderruns(long total)
    {
        // The device's own cumulative count for the stream; only ever moves forward within a stream.
        long seen = Volatile.Read(ref _streamDeviceUnderruns);
        while (total > seen)
        {
            long witnessed = Interlocked.CompareExchange(ref _streamDeviceUnderruns, total, seen);
            if (witnessed == seen)
            {
                break;
            }

            seen = witnessed;
        }
    }

    public void NotifyDefaultChanged() => Volatile.Write(ref _pendingDefault, 1);

    public void NotifyDevicesChanged() => AudioDevices.RaiseDevicesChanged();

    public void NotifyInterruption(bool began)
    {
        if (began)
        {
            Interlocked.Increment(ref _interruptBegan);
        }
        else
        {
            Interlocked.Increment(ref _interruptEnded);
        }
    }

    // ------------------------------------------------------------------ counters

    public DeviceCounters Counters()
    {
        long played;
        int bufferFrames = 0;
        int periodFrames = 0;
        double latency = 0;
        lock (_fold)
        {
            FoldPlayed();
            played = Volatile.Read(ref _playedHint);
            var s = Volatile.Read(ref _stream);
            if (s is not null)
            {
                bufferFrames = s.BufferFrames;
                periodFrames = s.PeriodFrames;
                int queued = s.QueuedFrames;
                latency = (queued > 0 ? queued : bufferFrames) * 1000.0 / s.Format.SampleRate;
            }
        }

        long written = Interlocked.Read(ref _framesWritten);
        return new DeviceCounters(
            Interlocked.Read(ref _framesRendered),
            written,
            Math.Min(played, written),
            BitConverter.Int32BitsToSingle(Volatile.Read(ref _peakWrittenBits)),
            BitConverter.Int32BitsToSingle(Volatile.Read(ref _peakPlayedBits)),
            Interlocked.Read(ref _underruns),
            Interlocked.Read(ref _overruns),
            Interlocked.Read(ref _silentFrames),
            Interlocked.Read(ref _faults),
            Interlocked.Read(ref _losses),
            Interlocked.Read(ref _reopens),
            Interlocked.Read(ref _defaultSwitches),
            bufferFrames,
            periodFrames,
            latency,
            Volatile.Read(ref _maxRenderTicks) * 1000.0 / Stopwatch.Frequency,
            Interlocked.Read(ref _allocBytes),
            Interlocked.Read(ref _eventsDropped),
            Interlocked.Read(ref _mutedFrames),
            Interlocked.Read(ref _deviceUnderrunsBase) + Interlocked.Read(ref _streamDeviceUnderruns));
    }

    /// <summary>Reads the device position and takes the peaks of blocks it has finished. Call under <c>_fold</c>.</summary>
    private void FoldPlayed()
    {
        var s = Volatile.Read(ref _stream);
        long streamPlayed = s?.FramesPlayed ?? 0;
        long streamWritten = Volatile.Read(ref _streamWritten);
        if (streamPlayed > streamWritten)
        {
            streamPlayed = streamWritten;
        }

        if (_direction == DeviceDirection.Output)
        {
            float peak = _peaks.Take(streamPlayed);
            RaiseMax(ref _peakPlayedBits, peak);
        }

        Volatile.Write(ref _playedHint, _playedBase + streamPlayed);
    }

    // ------------------------------------------------------------------ control (any thread, short lock)

    public void Start()
    {
        lock (_lifecycle)
        {
            ObjectDisposedException.ThrowIf(_disposed != 0, this);
            if (_started)
            {
                return;
            }

            _started = true;
            _silentOrigin = Stopwatch.GetTimestamp();
            TryOpenLocked(_requestedId);
            if (!_backend.IsManual)
            {
                _thread = new Thread(SupervisorLoop) { IsBackground = true, Name = "oneaudio-device" };
                _thread.Start();
            }
        }
    }

    public void Suspend()
    {
        lock (_lifecycle)
        {
            if (_disposed != 0 || !_started || _userSuspended)
            {
                return;
            }

            _userSuspended = true;
            _stream?.Pause();
            FoldLocked();
        }
    }

    public void Resume()
    {
        lock (_lifecycle)
        {
            if (_disposed != 0 || !_started || !_userSuspended)
            {
                return;
            }

            _userSuspended = false;
            _silentOrigin = Stopwatch.GetTimestamp();
            _silentPaced = 0;
            StartStreamLocked();
        }
    }

    public void Select(string? deviceId)
    {
        lock (_lifecycle)
        {
            ObjectDisposedException.ThrowIf(_disposed != 0, this);
            _requestedId = deviceId;
            if (!_started)
            {
                return;
            }

            CloseStreamLocked();
            TryOpenLocked(deviceId);
        }

        _wake.Set();
    }

    public int Advance(int frames)
    {
        if (!_backend.IsManual)
        {
            throw new InvalidOperationException("Advance is for manual backends; this device runs on its own thread.");
        }

        ArgumentOutOfRangeException.ThrowIfNegative(frames);
        if (Volatile.Read(ref _disposed) != 0 || !Volatile.Read(ref _started))
        {
            return 0;
        }

        // The supervisor's work (reopen after a loss, follow a default change, raise events) happens here, on the caller's thread, and only
        // when something is pending, so a steady stream of calls takes no lock.
        if (NeedsTick(Stopwatch.GetTimestamp()))
        {
            TickAndDispatch();
        }

        if (Volatile.Read(ref _userSuspended) || Volatile.Read(ref _systemSuspended))
        {
            return 0;
        }

        var stream = Volatile.Read(ref _stream);
        if (stream is IManualDevice manual)
        {
            return manual.Advance(frames);
        }

        if (stream is not null)
        {
            return 0; // the backend drives its stream itself (it calls Render from its own callbacks)
        }

        return _direction == DeviceDirection.Output ? RenderSilent(frames) : 0;
    }

    public int WantedFrames => Volatile.Read(ref _stream) is IManualDevice manual ? manual.WantedFrames : 0;

    private bool NeedsTick(long now)
    {
        if (Volatile.Read(ref _pendingLost) != 0 || Volatile.Read(ref _pendingDefault) != 0 || Volatile.Read(ref _pendingEvents) != 0
            || Volatile.Read(ref _xrunFramesPending) != 0
            || Interlocked.Read(ref _interruptBegan) != Volatile.Read(ref _seenBegan) || Interlocked.Read(ref _interruptEnded) != Volatile.Read(ref _seenEnded))
        {
            return true;
        }

        if (Volatile.Read(ref _stream) is null)
        {
            return !Volatile.Read(ref _userSuspended) && !Volatile.Read(ref _systemSuspended) && now >= Volatile.Read(ref _nextRetry);
        }

        return _requestedId is null && _options.FollowDefault && !_backend.NotifiesDeviceChanges && now >= Volatile.Read(ref _nextDefaultPoll);
    }

    /// <summary>Runs one round of supervisor work (reopen, default switch, events). The supervisor thread calls the same code.</summary>
    public void Pump()
    {
        if (!_backend.IsManual)
        {
            throw new InvalidOperationException("Pump is for manual backends; this device has a supervisor thread.");
        }

        TickAndDispatch();
    }

    public bool TryGetEvent(out DeviceEvent e) => _events.TryDequeue(out e);

    public void Dispose()
    {
        if (Interlocked.Exchange(ref _disposed, 1) != 0)
        {
            return;
        }

        _wake.Set();
        var t = _thread;
        if (t is not null && t != Thread.CurrentThread)
        {
            t.Join();
        }

        lock (_lifecycle)
        {
            CloseStreamLocked();
        }

        _wake.Dispose();
    }

    // ------------------------------------------------------------------ supervisor

    private void SupervisorLoop()
    {
        while (Volatile.Read(ref _disposed) == 0)
        {
            TickAndDispatch();
            if (Volatile.Read(ref _disposed) != 0)
            {
                break;
            }

            _wake.Wait(TickMilliseconds);
            _wake.Reset();
        }
    }

    private void TickAndDispatch()
    {
        lock (_lifecycle)
        {
            if (_disposed == 0 && _started)
            {
                TickLocked();
            }
        }

        DispatchEvents();
    }

    private void TickLocked()
    {
        long now = Stopwatch.GetTimestamp();

        // System interruptions (an audio session taken by a call, the app in the background).
        long began = Interlocked.Read(ref _interruptBegan);
        long ended = Interlocked.Read(ref _interruptEnded);
        if (began != _seenBegan)
        {
            _seenBegan = began;
            if (!_systemSuspended)
            {
                _systemSuspended = true;
                _stream?.Pause();
                FoldLocked();
                Emit(DeviceEventKind.Interrupted, _currentId, null, null);
            }
        }

        if (ended != _seenEnded)
        {
            _seenEnded = ended;
            if (_systemSuspended)
            {
                _systemSuspended = false;
                _silentOrigin = now;
                _silentPaced = 0;
                Emit(DeviceEventKind.Resumed, null, _currentId, null);
                StartStreamLocked();
            }
        }

        // The device went away.
        if (Interlocked.Exchange(ref _pendingLost, 0) != 0 && _stream is not null)
        {
            HandleLossLocked(Volatile.Read(ref _lostReason));
        }

        // The system default changed, told by the OS or noticed by polling.
        if (_requestedId is null && _options.FollowDefault && _stream is not null)
        {
            bool notified = Interlocked.Exchange(ref _pendingDefault, 0) != 0;
            bool changed = notified;
            if (!changed && !_backend.NotifiesDeviceChanges && now >= _nextDefaultPoll)
            {
                _nextDefaultPoll = now + DefaultPollTicks;
                changed = true;
            }

            if (changed)
            {
                // A stream that cannot name its device (null id) is "unknown", not "different": polling compares only when both sides are known.
                // An explicit notice from the backend is trusted even then, because the backend saw the change.
                string? target = _backend.GetDefault(_direction)?.Id;
                bool differs = _currentId is null ? notified : target != _currentId;
                if (target is not null && differs)
                {
                    SwitchDefaultLocked(target);
                }
            }
        }
        else
        {
            Volatile.Write(ref _pendingDefault, 0);
        }

        // No device: try again twice a second.
        if (_stream is null && !_userSuspended && !_systemSuspended && now >= _nextRetry)
        {
            TryOpenLocked(_requestedId);
        }

        long xrunFrames = Interlocked.Exchange(ref _xrunFramesPending, 0);
        if (xrunFrames > 0)
        {
            Emit(DeviceEventKind.Xrun, null, _currentId, xrunFrames.ToString(System.Globalization.CultureInfo.InvariantCulture));
        }

        if (_stream is null && !_userSuspended && !_systemSuspended && _direction == DeviceDirection.Output && !_backend.IsManual)
        {
            // A manual device is moved by its caller, who renders the silent frames through Advance; no wall clock is involved.
            PaceSilentLocked(now);
        }

        lock (_fold)
        {
            FoldPlayed();
        }
    }

    private void HandleLossLocked(string? reason)
    {
        string? from = _currentId;
        Interlocked.Increment(ref _losses);
        _lastError = reason ?? "the device was lost";
        Emit(DeviceEventKind.DeviceLost, from, null, reason);
        CloseStreamLocked();
        TryOpenLocked(_requestedId);
    }

    private void SwitchDefaultLocked(string target)
    {
        string? from = _currentId;
        Interlocked.Increment(ref _defaultSwitches);
        Emit(DeviceEventKind.DefaultChanged, from, target, null);
        CloseStreamLocked();
        TryOpenLocked(null);
    }

    /// <summary>Opens the device and starts it unless suspended. Emits Reopened/DeviceRestored for opens after the first.</summary>
    private bool TryOpenLocked(string? id)
    {
        string? from = _lastId;
        // The stream runs at the source's format (or the callback's), which the options only carry when no source brought its own.
        var options = _options with { DeviceId = id, SampleRate = _format.SampleRate, Channels = _format.Channels };
        IOpenDevice? stream = null;
        string reason;
        bool ok = _backend.TryOpen(_direction, options, this, out stream, out reason) && stream is not null;
        RateAdapter? adapter = null;
        if (ok && stream!.Format.Channels != _format.Channels)
        {
            reason = $"the device runs at {stream.Format.Channels} channels but {_format.Channels} were requested";
            stream.Dispose();
            ok = false;
        }
        else if (ok && stream!.Format.SampleRate != _format.SampleRate)
        {
            double ratio = (double)stream.Format.SampleRate / _format.SampleRate;
            if (_direction != DeviceDirection.Output || !_options.AllowRateConversion)
            {
                reason = $"the device runs at {stream.Format.SampleRate} Hz but {_format.SampleRate} Hz was requested (rate conversion is {(_direction == DeviceDirection.Output ? "switched off by AllowRateConversion" : "only done for output")})";
            }
            else if (ratio is < 0.25 or > 16)
            {
                reason = $"the device runs at {stream.Format.SampleRate} Hz, too far from the {_format.SampleRate} Hz the program renders at to convert";
            }
            else
            {
                reason = string.Empty;
                adapter = new RateAdapter(this, _format.SampleRate, stream.Format.SampleRate, _format.Channels);
            }

            if (adapter is null)
            {
                stream.Dispose();
                ok = false;
            }
        }

        if (!ok)
        {
            _lastError = string.IsNullOrEmpty(reason) ? "the device could not be opened" : reason;
            _nextRetry = Stopwatch.GetTimestamp() + RetryTicks;
            _silentOrigin = Stopwatch.GetTimestamp();
            _silentPaced = 0;
            _gap = true;
            return false;
        }

        // Nothing renders into the new stream yet: reset the per-stream position before it can start.
        lock (_fold)
        {
            Volatile.Write(ref _streamWritten, 0);
            _peaks.Clear();
            _captureRing?.Clear();
        }

        Volatile.Write(ref _adapter, adapter);
        int deviceRate = stream!.Format.SampleRate;
        if (_options.Metering && _direction == DeviceDirection.Output && deviceRate != _deviceRate)
        {
            _meter = new LoudnessMeter(deviceRate, _format.Channels, ChannelMask.None, truePeak: true, loudnessRange: false);
        }

        _deviceRate = deviceRate;
        _currentId = stream.Device?.Id ?? id;
        _lastId = _currentId;
        Volatile.Write(ref _stream, stream);
        bool reopen = _everOpened;
        bool restored = _gap;
        _everOpened = true;
        _gap = false;
        _lastError = null;
        if (reopen)
        {
            Interlocked.Increment(ref _reopens);
            Emit(restored ? DeviceEventKind.DeviceRestored : DeviceEventKind.Reopened, from, _currentId, null);
        }
        else if (restored)
        {
            Emit(DeviceEventKind.DeviceRestored, null, _currentId, null);
        }

        if (!_userSuspended && !_systemSuspended)
        {
            StartStreamLocked();
        }

        return true;
    }

    /// <summary>Starts the open stream; a device that refuses is treated as lost.</summary>
    private void StartStreamLocked()
    {
        var s = _stream;
        if (s is null)
        {
            return;
        }

        if (!s.Start(out string reason))
        {
            Interlocked.Increment(ref _losses);
            _gap = true;
            _lastError = string.IsNullOrEmpty(reason) ? "the device refused to start" : reason;
            Emit(DeviceEventKind.DeviceLost, _currentId, null, _lastError);
            CloseStreamLocked();
            _nextRetry = Stopwatch.GetTimestamp() + RetryTicks;
        }
    }

    private void CloseStreamLocked()
    {
        var s = _stream;
        if (s is null)
        {
            return;
        }

        s.Pause();
        lock (_fold)
        {
            FoldPlayed();
            long streamPlayed = Math.Min(s.FramesPlayed, Volatile.Read(ref _streamWritten));
            _playedBase += streamPlayed;
            Interlocked.Add(ref _deviceUnderrunsBase, Interlocked.Exchange(ref _streamDeviceUnderruns, 0));
            Volatile.Write(ref _stream, null);
            Volatile.Write(ref _playedHint, _playedBase);
            _peaks.Clear();
        }

        s.Dispose();
        _currentId = null;
        _silentOrigin = Stopwatch.GetTimestamp();
        _silentPaced = 0;
    }

    private void FoldLocked()
    {
        lock (_fold)
        {
            FoldPlayed();
        }
    }

    /// <summary>Keeps the program's clock moving at real-time pace while no device exists, and throws the samples away.</summary>
    private void PaceSilentLocked(long now)
    {
        long due = (now - _silentOrigin) * _format.SampleRate / Stopwatch.Frequency - _silentPaced;
        long cap = _format.SampleRate / 10;
        if (due > cap)
        {
            // Far behind (a long pause in the supervisor): skip ahead rather than render a burst.
            _silentPaced += due - cap;
            due = cap;
        }

        while (due > 0)
        {
            int n = (int)Math.Min(due, MaxSilentChunkFrames);
            RenderSilent(n);
            _silentPaced += n;
            due -= n;
        }
    }

    private int RenderSilent(int frames)
    {
        int ch = _format.Channels;
        int done = 0;
        while (done < frames)
        {
            int n = Math.Min(frames - done, MaxSilentChunkFrames);
            var block = _silent.AsSpan(0, n * ch);
            if (Volatile.Read(ref _muted) != 0)
            {
                Interlocked.Add(ref _mutedFrames, n);
            }
            else
            {
                RunSource(block, 0);
                Interlocked.Add(ref _framesRendered, n);
            }

            Interlocked.Add(ref _silentFrames, n);
            done += n;
        }

        return done;
    }

    // ------------------------------------------------------------------ events

    private void Emit(DeviceEventKind kind, string? from, string? to, string? detail)
    {
        var e = new DeviceEvent(kind, from, to, Volatile.Read(ref _playedHint), detail);
        if (!_events.TryEnqueue(e))
        {
            Interlocked.Increment(ref _eventsDropped);
        }

        _batch.Add(e);
        Volatile.Write(ref _pendingEvents, 1);
    }

    private void DispatchEvents()
    {
        List<DeviceEvent> batch;
        lock (_lifecycle)
        {
            if (_batch.Count == 0)
            {
                return;
            }

            batch = _batch;
            _batch = _batchSwap;
            _batchSwap = batch;
            Volatile.Write(ref _pendingEvents, 0);
        }

        var handler = Event;
        if (handler is not null)
        {
            for (int i = 0; i < batch.Count; i++)
            {
                // A handler is the program's code on our thread; its failure must not end the supervisor.
                try
                {
                    handler(batch[i]);
                }
                catch (Exception)
                {
                    Interlocked.Increment(ref _faults);
                }
            }
        }

        batch.Clear();
    }

    /// <summary>
    /// Sits between the program's source and a device that runs at another rate: asks the source for fixed chunks at its own rate and converts
    /// them to the device's with the library's polyphase resampler, giving the device exactly the frames it asks for. All buffers are made when
    /// the device opens; <see cref="Fill"/> allocates nothing.
    /// </summary>
    private sealed class RateAdapter
    {
        private const int ChunkFrames = 512;

        private readonly DeviceEngine _engine;
        private readonly IResampler _resampler;
        private readonly float[] _input;
        private readonly int _channels;
        private int _position;
        private int _length;

        public RateAdapter(DeviceEngine engine, int sourceRate, int deviceRate, int channels)
        {
            _engine = engine;
            _channels = channels;
            _resampler = AudioResampler.Create(sourceRate, deviceRate, channels, ResamplerQuality.Polyphase);
            _input = new float[ChunkFrames * channels];
        }

        public void Fill(Span<float> output)
        {
            int ch = _channels;
            int need = output.Length / ch;
            int done = 0;
            while (done < need)
            {
                if (_position == _length)
                {
                    _engine.RunSource(_input, 0);
                    Interlocked.Add(ref _engine._framesRendered, ChunkFrames);
                    _position = 0;
                    _length = ChunkFrames;
                }

                _resampler.Process(_input.AsSpan(_position * ch, (_length - _position) * ch), output.Slice(done * ch, (need - done) * ch), out int used, out int written);
                _position += used;
                done += written;
                if (used == 0 && written == 0)
                {
                    output[(done * ch)..].Clear();
                    return;
                }
            }
        }
    }
}
