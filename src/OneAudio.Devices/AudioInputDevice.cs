namespace OneAudio.Devices;

/// <summary>
/// A capture stream: records interleaved float frames from a microphone or line input and hands them to a <see cref="CaptureCallback"/> on the
/// device's real-time thread, or keeps them in a read buffer for <see cref="Read"/>. Like <see cref="AudioOutputDevice"/> it follows the default
/// device, survives a loss and reports <see cref="Counters"/>; an input with no device delivers nothing. The operating system may ask the user
/// for permission to record; a refusal shows as <see cref="DeviceState.NoDevice"/> with the reason in <see cref="LastError"/>.
/// </summary>
public sealed class AudioInputDevice : IDisposable
{
    private readonly DeviceEngine _engine;

    private AudioInputDevice(DeviceEngine engine) => _engine = engine;

    /// <summary>Creates an input whose samples are read with <see cref="Read"/>. Call <see cref="Start"/> to open the device.</summary>
    /// <param name="options">Device options (rate, channels, latency, <see cref="DeviceStreamOptions.CaptureBufferMilliseconds"/>); null means defaults.</param>
    /// <param name="backend">The backend to open; null uses <see cref="AudioDevices.Backend"/>.</param>
    /// <returns>The input, not yet started.</returns>
    public static AudioInputDevice Open(DeviceStreamOptions? options = null, IAudioDeviceBackend? backend = null) => Create(options, backend, null);

    /// <summary>Creates an input that delivers each block to <paramref name="callback"/> on the device thread. Call <see cref="Start"/> to open the device.</summary>
    /// <param name="callback">Receives each block; see <see cref="CaptureCallback"/> for the rules.</param>
    /// <param name="options">Device options; null means defaults.</param>
    /// <param name="backend">The backend to open; null uses <see cref="AudioDevices.Backend"/>.</param>
    /// <returns>The input, not yet started.</returns>
    public static AudioInputDevice Open(CaptureCallback callback, DeviceStreamOptions? options = null, IAudioDeviceBackend? backend = null)
    {
        ArgumentNullException.ThrowIfNull(callback);
        return Create(options, backend, callback);
    }

    private static AudioInputDevice Create(DeviceStreamOptions? options, IAudioDeviceBackend? backend, CaptureCallback? callback)
    {
        options ??= new DeviceStreamOptions();
        var format = new AudioDeviceFormat(options.SampleRate, options.Channels);
        DeviceValidation.Check(format, options);
        var engine = new DeviceEngine(DeviceDirection.Input, backend ?? AudioDevices.Backend, options, format, null, null, callback);
        return new AudioInputDevice(engine);
    }

    /// <summary>The format delivered: <see cref="DeviceStreamOptions.SampleRate"/> by <see cref="DeviceStreamOptions.Channels"/>, 32-bit float interleaved.</summary>
    public AudioDeviceFormat Format => _engine.Format;

    /// <summary>Where the device is in its life.</summary>
    public DeviceState State => _engine.State;

    /// <summary>The device that is open now, or <see langword="null"/> while none is.</summary>
    public AudioDeviceInfo? Device => _engine.CurrentDevice;

    /// <summary>The name of the backend in use.</summary>
    public string BackendName => _engine.Backend.Name;

    /// <summary>Why the last open failed, or <see langword="null"/> when the device is open.</summary>
    public string? LastError => _engine.LastError;

    /// <summary>Backend-specific extras for diagnostics, or <see langword="null"/>.</summary>
    public string? Detail => _engine.DetailText;

    /// <summary>The backend's own state word for the open stream (for example <c>awaiting_gesture</c> in a browser), or <see langword="null"/> when it has none or no device is open. Also appended to <see cref="Detail"/>.</summary>
    public string? StateText => _engine.StateText;

    /// <summary>What the device has really done; see <see cref="DeviceCounters"/> for how the fields read for an input. A snapshot, safe from any thread.</summary>
    public DeviceCounters Counters => _engine.Counters();

    /// <summary>Whole frames waiting in the read buffer; 0 for an input that uses a callback.</summary>
    public int AvailableFrames => _engine.CaptureRing?.AvailableFrames ?? 0;

    /// <summary>Raised on the supervisor thread when something happens to the device; see <see cref="AudioOutputDevice.Event"/>.</summary>
    public event Action<DeviceEvent>? Event
    {
        add => _engine.Event += value;
        remove => _engine.Event -= value;
    }

    /// <summary>Takes the oldest unread event, for programs that poll instead of subscribing.</summary>
    /// <param name="deviceEvent">The event.</param>
    /// <returns>False when there is none.</returns>
    public bool TryGetEvent(out DeviceEvent deviceEvent) => _engine.TryGetEvent(out deviceEvent);

    /// <summary>
    /// Copies captured frames into <paramref name="destination"/> without blocking. Call from one thread. When the read buffer overflows because
    /// nobody reads, new audio is dropped and <see cref="DeviceCounters.Overruns"/> counts it.
    /// </summary>
    /// <param name="destination">Receives interleaved samples; only whole frames are written.</param>
    /// <returns>The frames copied, 0 when nothing is waiting.</returns>
    /// <exception cref="InvalidOperationException">The input delivers to a callback.</exception>
    public int Read(Span<float> destination)
    {
        var ring = _engine.CaptureRing ?? throw new InvalidOperationException("This input delivers to a callback; there is no read buffer.");
        return ring.Read(destination);
    }

    /// <summary>Opens the device and starts capturing. A device that cannot be opened is not an error; see <see cref="DeviceState.NoDevice"/> and <see cref="LastError"/>.</summary>
    /// <exception cref="ObjectDisposedException">The input was disposed.</exception>
    public void Start() => _engine.Start();

    /// <summary>Stops the device without closing it; capturing resumes with <see cref="Resume"/>.</summary>
    public void Suspend() => _engine.Suspend();

    /// <summary>True after <see cref="Suspend"/> until <see cref="Resume"/>.</summary>
    public bool IsSuspended => _engine.IsUserSuspended;

    /// <summary>True while the system has the device taken away; see <see cref="AudioOutputDevice.IsSystemSuspended"/>.</summary>
    public bool IsSystemSuspended => _engine.IsSystemSuspended;

    /// <summary>Called on the device thread for every captured block, before it is delivered, with the frames received so far; see <see cref="AudioOutputDevice.BlockHook"/>.</summary>
    public Action<long>? BlockHook
    {
        get => _engine.BlockHook;
        set => _engine.BlockHook = value;
    }

    /// <summary>Starts the device again after <see cref="Suspend"/>.</summary>
    public void Resume() => _engine.Resume();

    /// <summary>Moves to another input device; null returns to the system default (and following it).</summary>
    /// <param name="deviceId">The device id, or null.</param>
    public void SelectDevice(string? deviceId) => _engine.Select(deviceId);

    /// <summary>Manual backends only: runs the device for <paramref name="frames"/> frames on the calling thread; returns the frames run.</summary>
    /// <param name="frames">How many frames to capture.</param>
    /// <exception cref="InvalidOperationException">The backend is not manual.</exception>
    public int Advance(int frames) => _engine.Advance(frames);

    /// <summary>Manual backends only: does the supervisor's work once.</summary>
    /// <exception cref="InvalidOperationException">The backend is not manual.</exception>
    public void Pump() => _engine.Pump();

    /// <summary>Stops and closes the device and ends the supervisor thread. Safe to call more than once.</summary>
    public void Dispose() => _engine.Dispose();
}
