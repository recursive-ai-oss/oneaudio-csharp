using System.Threading;

namespace OneAudio.Devices;

/// <summary>
/// A backend with no hardware. Its devices consume audio when the caller says so (<see cref="AudioOutputDevice.Advance"/>), which makes
/// runs exact and repeatable: tests, offline rendering and hosts without sound cards use it. It can also act out the things real devices do —
/// lose the device, change the default, run dry, be interrupted — so a program can test how it copes.
/// <para>
/// A manual device has no supervisor thread: call <see cref="AudioOutputDevice.Pump"/> to let it react to what happened (reopen,
/// follow the default, raise events).
/// </para>
/// </summary>
public sealed class ManualBackend : IAudioDeviceBackend
{
    private readonly Lock _gate = new();
    private readonly List<ManualStream> _streams = [];
    private readonly float[]? _recording;
    private readonly int _recordingChannels;
    private long _recorded;
    private long _recordingDropped;
    private IReadOnlyList<AudioDeviceInfo> _devices;
    private int _failOpens;
    private int _opens;
    private float[] _input = [];
    private bool _inputLoop;
    private long _inputPosition;

    /// <summary>Creates a manual backend with two output devices (<c>manual-out-0</c>, the default, and <c>manual-out-1</c>) and one input (<c>manual-in-0</c>).</summary>
    /// <param name="recordFrames">How many frames of output to keep for <see cref="CopyRecording"/>; 0 (the default) keeps none. Allocated once, here.</param>
    /// <param name="recordChannels">The channel count of the output being recorded; required when <paramref name="recordFrames"/> is above 0.</param>
    public ManualBackend(int recordFrames = 0, int recordChannels = 2)
    {
        ArgumentOutOfRangeException.ThrowIfNegative(recordFrames);
        _devices = DefaultDevices();
        if (recordFrames > 0)
        {
            ArgumentOutOfRangeException.ThrowIfLessThan(recordChannels, 1);
            _recordingChannels = recordChannels;
            _recording = new float[checked(recordFrames * recordChannels)];
        }
    }

    /// <inheritdoc/>
    public string Name => "manual";

    /// <inheritdoc/>
    public DeviceBackendKind Kind => DeviceBackendKind.Manual;

    /// <inheritdoc/>
    public bool IsManual => true;

    /// <summary>Whether this backend reports default changes itself (default true). Set false to test the polling path.</summary>
    public bool NotifiesDeviceChanges { get; set; } = true;

    /// <inheritdoc/>
    public bool IsAvailable => true;

    /// <summary>The number of successful opens so far.</summary>
    public int OpenCount => Volatile.Read(ref _opens);

    /// <summary>
    /// How many frames behind the writes the simulated device clock runs: the device reports
    /// <c>written - LagFrames</c> as played (never below 0). Default 0, meaning it consumes everything at once. Set it to test peak and
    /// played-position bookkeeping.
    /// </summary>
    public int LagFrames { get; set; }

    /// <summary>
    /// The sample rate the simulated devices run at; 0 (the default) means the rate each stream asks for. A different rate lets tests and hosts
    /// act out a device (a browser's audio context, say) that does not honour the request; the output device then converts if
    /// <see cref="DeviceStreamOptions.AllowRateConversion"/> allows. Set before opening.
    /// </summary>
    public int DeviceSampleRate { get; set; }

    /// <summary>The period each stream asks for, in frames. Default 256. Set before opening.</summary>
    public int PeriodFrames { get; set; } = 256;

    /// <inheritdoc/>
    public IReadOnlyList<AudioDeviceInfo> List(DeviceDirection direction)
    {
        var all = Volatile.Read(ref _devices);
        var result = new List<AudioDeviceInfo>();
        foreach (var d in all)
        {
            if (d.Direction == direction)
            {
                result.Add(d);
            }
        }

        return result;
    }

    /// <inheritdoc/>
    public AudioDeviceInfo? GetDefault(DeviceDirection direction)
    {
        foreach (var d in Volatile.Read(ref _devices))
        {
            if (d.Direction == direction && d.IsDefault)
            {
                return d;
            }
        }

        return null;
    }

    /// <summary>Replaces the device list (to simulate hot-plug). Raises <see cref="AudioDevices.DevicesChanged"/> through the open streams.</summary>
    /// <param name="devices">The new list; one device per direction should be the default.</param>
    public void SetDevices(IReadOnlyList<AudioDeviceInfo> devices)
    {
        ArgumentNullException.ThrowIfNull(devices);
        Volatile.Write(ref _devices, devices);
        foreach (var s in Snapshot())
        {
            s.Engine.NotifyDevicesChanged();
        }
    }

    /// <summary>Makes the device with this id the default for its direction and tells every open stream, as an operating system would.</summary>
    /// <param name="id">An id from <see cref="List"/>.</param>
    /// <exception cref="ArgumentException">There is no such device.</exception>
    public void SetDefault(string id)
    {
        ArgumentNullException.ThrowIfNull(id);
        var all = Volatile.Read(ref _devices);
        DeviceDirection? direction = null;
        foreach (var d in all)
        {
            if (d.Id == id)
            {
                direction = d.Direction;
            }
        }

        if (direction is null)
        {
            throw new ArgumentException("No such device.", nameof(id));
        }

        var updated = new List<AudioDeviceInfo>(all.Count);
        foreach (var d in all)
        {
            updated.Add(d.Direction == direction ? d with { IsDefault = d.Id == id } : d);
        }

        Volatile.Write(ref _devices, updated);
        if (NotifiesDeviceChanges)
        {
            foreach (var s in Snapshot())
            {
                if (s.Direction == direction)
                {
                    s.Engine.NotifyDefaultChanged();
                }
            }
        }
    }

    /// <summary>Makes the next <paramref name="count"/> opens fail, as a device that is busy or missing would.</summary>
    /// <param name="count">How many opens to refuse; use <see cref="int.MaxValue"/> for "until reset" and 0 to stop.</param>
    public void FailOpens(int count) => Volatile.Write(ref _failOpens, count);

    /// <summary>Acts out the loss of every open stream (unplugged device): their counters stop and the engine is notified.</summary>
    public void SimulateLoss()
    {
        foreach (var s in Snapshot())
        {
            s.Lose();
        }
    }

    /// <summary>Acts out a run-dry (output) or overflow (input) of <paramref name="frames"/> frames on every open stream.</summary>
    /// <param name="frames">The size of the gap.</param>
    public void SimulateXrun(int frames)
    {
        foreach (var s in Snapshot())
        {
            s.Engine.NotifyXrun(frames);
        }
    }

    /// <summary>Acts out a system interruption beginning (true) or ending (false) on every open stream.</summary>
    /// <param name="began">True when the interruption begins.</param>
    public void SimulateInterruption(bool began)
    {
        foreach (var s in Snapshot())
        {
            s.Engine.NotifyInterruption(began);
        }
    }

    /// <summary>Sets what input devices capture: this signal, played from the start, then silence (or repeated when <paramref name="loop"/>).</summary>
    /// <param name="interleaved">Samples at the format the input stream opens with.</param>
    /// <param name="loop">Repeat the signal.</param>
    public void SetInputSignal(float[] interleaved, bool loop)
    {
        ArgumentNullException.ThrowIfNull(interleaved);
        lock (_gate)
        {
            _input = interleaved;
            _inputLoop = loop;
            _inputPosition = 0;
        }
    }

    /// <summary>Frames recorded so far (up to the capacity given at construction).</summary>
    public long RecordedFrames => Math.Min(Volatile.Read(ref _recorded), _recording is null ? 0 : _recording.Length / _recordingChannels);

    /// <summary>Frames that did not fit the recording capacity.</summary>
    public long RecordingDropped => Volatile.Read(ref _recordingDropped);

    /// <summary>Copies the recorded output (everything the devices consumed) into <paramref name="destination"/>; returns the samples copied.</summary>
    /// <param name="destination">Receives interleaved samples.</param>
    public int CopyRecording(Span<float> destination)
    {
        if (_recording is null)
        {
            return 0;
        }

        int n = (int)Math.Min(destination.Length, RecordedFrames * _recordingChannels);
        _recording.AsSpan(0, n).CopyTo(destination);
        return n;
    }

    /// <inheritdoc/>
    public bool TryOpen(DeviceDirection direction, DeviceStreamOptions options, IDeviceEngine engine, out IOpenDevice? stream, out string reason)
    {
        ArgumentNullException.ThrowIfNull(options);
        ArgumentNullException.ThrowIfNull(engine);
        stream = null;
        reason = string.Empty;
        if (Volatile.Read(ref _failOpens) > 0)
        {
            if (Volatile.Read(ref _failOpens) != int.MaxValue)
            {
                Interlocked.Decrement(ref _failOpens);
            }

            reason = "the manual backend was told to refuse this open";
            return false;
        }

        AudioDeviceInfo? device = null;
        foreach (var d in Volatile.Read(ref _devices))
        {
            if (d.Direction == direction && (options.DeviceId is null ? d.IsDefault : d.Id == options.DeviceId))
            {
                device = d;
                break;
            }
        }

        if (device is null)
        {
            reason = options.DeviceId is null ? "there is no default device" : "no such device: " + options.DeviceId;
            return false;
        }

        int period = options.PeriodFrames > 0 ? options.PeriodFrames : PeriodFrames;
        int buffer = options.BufferFrames > 0 ? options.BufferFrames : Math.Max(period * 4, options.LatencyMilliseconds * options.SampleRate / 1000);
        var s = new ManualStream(this, direction, engine, new AudioDeviceFormat(DeviceSampleRate > 0 ? DeviceSampleRate : options.SampleRate, options.Channels), device.Value, period, buffer);
        lock (_gate)
        {
            _streams.Add(s);
        }

        Interlocked.Increment(ref _opens);
        stream = s;
        return true;
    }

    private ManualStream[] Snapshot()
    {
        lock (_gate)
        {
            return [.. _streams];
        }
    }

    private void Forget(ManualStream s)
    {
        lock (_gate)
        {
            _streams.Remove(s);
        }
    }

    private void Record(ReadOnlySpan<float> block)
    {
        if (_recording is null)
        {
            return;
        }

        long frames = block.Length / _recordingChannels;
        long at = Volatile.Read(ref _recorded);
        long capacity = _recording.Length / _recordingChannels;
        long fit = Math.Max(0, Math.Min(frames, capacity - at));
        block[..(int)(fit * _recordingChannels)].CopyTo(_recording.AsSpan((int)(at * _recordingChannels)));
        Volatile.Write(ref _recorded, at + fit);
        if (fit < frames)
        {
            Interlocked.Add(ref _recordingDropped, frames - fit);
        }
    }

    private void NextInput(Span<float> destination)
    {
        lock (_gate)
        {
            int i = 0;
            while (i < destination.Length)
            {
                if (_input.Length == 0 || (!_inputLoop && _inputPosition >= _input.Length))
                {
                    destination[i..].Clear();
                    return;
                }

                int at = (int)(_inputPosition % _input.Length);
                int n = Math.Min(destination.Length - i, _input.Length - at);
                _input.AsSpan(at, n).CopyTo(destination[i..]);
                i += n;
                _inputPosition += n;
            }
        }
    }

    private static AudioDeviceInfo[] DefaultDevices() =>
    [
        new("manual-out-0", "Manual output 0", DeviceDirection.Output, true, 48_000, 8, 64, DeviceBackendKind.Manual),
        new("manual-out-1", "Manual output 1", DeviceDirection.Output, false, 48_000, 8, 64, DeviceBackendKind.Manual),
        new("manual-in-0", "Manual input 0", DeviceDirection.Input, true, 48_000, 2, 64, DeviceBackendKind.Manual),
    ];

    private sealed class ManualStream : IOpenDevice, IManualDevice
    {
        private readonly ManualBackend _owner;
        private readonly int _channels;
        private readonly float[] _scratch;
        private long _frames;
        private int _running;
        private int _inside;
        private int _lost;

        public ManualStream(ManualBackend owner, DeviceDirection direction, IDeviceEngine engine, AudioDeviceFormat format, AudioDeviceInfo device, int period, int buffer)
        {
            _owner = owner;
            Direction = direction;
            Engine = engine;
            Format = format;
            Device = device;
            PeriodFrames = period;
            BufferFrames = buffer;
            _channels = format.Channels;
            _scratch = new float[period * _channels];
        }

        public DeviceDirection Direction { get; }

        public IDeviceEngine Engine { get; }

        public AudioDeviceFormat Format { get; }

        public AudioDeviceInfo? Device { get; }

        public int PeriodFrames { get; }

        public int BufferFrames { get; }

        public long FramesPlayed => Math.Max(0, Volatile.Read(ref _frames) - _owner.LagFrames);

        public int QueuedFrames => Math.Min(_owner.LagFrames, BufferFrames);

        public string? Detail => "manual";

        public bool Start(out string reason)
        {
            reason = string.Empty;
            if (Volatile.Read(ref _lost) != 0)
            {
                reason = "the device was lost";
                return false;
            }

            Volatile.Write(ref _running, 1);
            return true;
        }

        public void Pause()
        {
            Interlocked.Exchange(ref _running, 0);
            var wait = new SpinWait();
            while (Volatile.Read(ref _inside) != 0)
            {
                wait.SpinOnce();
            }
        }

        public void Dispose()
        {
            Pause();
            _owner.Forget(this);
        }

        public void Lose()
        {
            Volatile.Write(ref _lost, 1);
            Volatile.Write(ref _running, 0);
            Engine.NotifyLost("the manual device was lost");
        }

        public int Advance(int frames)
        {
            Interlocked.Exchange(ref _inside, 1);
            try
            {
                if (Volatile.Read(ref _running) == 0)
                {
                    return 0;
                }

                int done = 0;
                while (done < frames)
                {
                    int n = Math.Min(frames - done, PeriodFrames);
                    var block = _scratch.AsSpan(0, n * _channels);
                    if (Direction == DeviceDirection.Output)
                    {
                        Engine.Render(block);
                        _owner.Record(block);
                    }
                    else
                    {
                        _owner.NextInput(block);
                        Engine.Capture(block);
                    }

                    Volatile.Write(ref _frames, Volatile.Read(ref _frames) + n);
                    done += n;
                }

                return done;
            }
            finally
            {
                Volatile.Write(ref _inside, 0);
            }
        }
    }
}
