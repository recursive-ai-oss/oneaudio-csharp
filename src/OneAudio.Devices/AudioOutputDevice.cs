using OneAudio.Loudness;

namespace OneAudio.Devices;

/// <summary>
/// A playback stream: pulls interleaved float frames from an <see cref="IAudioSource"/> (a mixer) or a <see cref="RenderCallback"/> on the
/// device's real-time thread and plays them. It follows the system default device, survives a lost device, and reports what the
/// hardware really did in <see cref="Counters"/>.
/// <para>
/// <b>Threading.</b> The source is called from one thread at a time: the device thread while a device is open, and the device's supervisor thread
/// (at real-time pace, output discarded) while none is. The render path allocates nothing, takes no lock and never blocks; a source that throws
/// is caught, counted in <see cref="DeviceCounters.CallbackFaults"/> and replaced by silence for that block. Control methods may be called from
/// any thread. <see cref="Event"/> handlers run on the supervisor thread.
/// </para>
/// <para>
/// <b>Warm-up.</b> The first blocks run code that may still have to be compiled. In a JIT application, render a few silent blocks (for example
/// through a <see cref="ManualBackend"/> device) before the first sound that matters; NativeAOT needs nothing.
/// </para>
/// </summary>
public sealed class AudioOutputDevice : IDisposable
{
    private readonly DeviceEngine _engine;

    private AudioOutputDevice(DeviceEngine engine) => _engine = engine;

    internal DeviceEngine EngineForTests => _engine;

    /// <summary>Creates an output over a source (a mixer). The source's rate and channel count are used; the options' are ignored. Call <see cref="Start"/> to open the device.</summary>
    /// <param name="source">What to play.</param>
    /// <param name="options">Device options; null means all defaults.</param>
    /// <param name="backend">The backend to open; null uses <see cref="AudioDevices.Backend"/>.</param>
    /// <returns>The output, not yet started.</returns>
    /// <exception cref="ArgumentOutOfRangeException">The source's rate is outside 8000..384000 or its channels outside 1..16.</exception>
    public static AudioOutputDevice Open(IAudioSource source, DeviceStreamOptions? options = null, IAudioDeviceBackend? backend = null)
    {
        ArgumentNullException.ThrowIfNull(source);
        return Create(options, backend, new AudioDeviceFormat(source.SampleRate, source.Channels), source, null);
    }

    /// <summary>Creates an output over a render callback at <see cref="DeviceStreamOptions.SampleRate"/> and <see cref="DeviceStreamOptions.Channels"/>. Call <see cref="Start"/> to open the device.</summary>
    /// <param name="callback">Fills each block; see <see cref="RenderCallback"/> for the rules.</param>
    /// <param name="options">Device options; null means all defaults.</param>
    /// <param name="backend">The backend to open; null uses <see cref="AudioDevices.Backend"/>.</param>
    /// <returns>The output, not yet started.</returns>
    /// <exception cref="ArgumentOutOfRangeException">The rate is outside 8000..384000 or the channels outside 1..16.</exception>
    public static AudioOutputDevice Open(RenderCallback callback, DeviceStreamOptions? options = null, IAudioDeviceBackend? backend = null)
    {
        ArgumentNullException.ThrowIfNull(callback);
        options ??= new DeviceStreamOptions();
        return Create(options, backend, new AudioDeviceFormat(options.SampleRate, options.Channels), null, callback);
    }

    private static AudioOutputDevice Create(DeviceStreamOptions? options, IAudioDeviceBackend? backend, AudioDeviceFormat format, IAudioSource? source, RenderCallback? callback)
    {
        options ??= new DeviceStreamOptions();
        DeviceValidation.Check(format, options);
        var engine = new DeviceEngine(DeviceDirection.Output, backend ?? AudioDevices.Backend, options, format, source, callback, null);
        return new AudioOutputDevice(engine);
    }

    /// <summary>The format the device runs at: the source's, or the options', always 32-bit float interleaved.</summary>
    public AudioDeviceFormat Format => _engine.Format;

    /// <summary>
    /// The format the open device really runs at; equal to <see cref="Format"/> unless the device runs at another rate and
    /// <see cref="DeviceStreamOptions.AllowRateConversion"/> converts, and while no device is open.
    /// </summary>
    public AudioDeviceFormat DeviceFormat => _engine.DeviceFormat;

    /// <summary>Where the device is in its life.</summary>
    public DeviceState State => _engine.State;

    /// <summary>The device that is open now, or <see langword="null"/> while none is.</summary>
    public AudioDeviceInfo? Device => _engine.CurrentDevice;

    /// <summary>The name of the backend in use, such as <c>alsa</c>.</summary>
    public string BackendName => _engine.Backend.Name;

    /// <summary>Why the last open failed, or <see langword="null"/> when the device is open. Route and session extras while open are in <see cref="Detail"/>.</summary>
    public string? LastError => _engine.LastError;

    /// <summary>Backend-specific extras for diagnostics (route, session category), or <see langword="null"/>.</summary>
    public string? Detail => _engine.DetailText;

    /// <summary>The backend's own state word for the open stream (for example <c>awaiting_gesture</c> in a browser), or <see langword="null"/> when it has none or no device is open. Also appended to <see cref="Detail"/>.</summary>
    public string? StateText => _engine.StateText;

    /// <summary>What the device has really done. A snapshot; safe from any thread, allocation-free.</summary>
    public DeviceCounters Counters => _engine.Counters();

    /// <summary>The loudness at the output after the limiter; all <see cref="double.NegativeInfinity"/> unless <see cref="DeviceStreamOptions.Metering"/> is on.</summary>
    public LoudnessSnapshot Loudness => _engine.Loudness;

    /// <summary>
    /// True after <see cref="Suspend"/> until <see cref="Resume"/>. Read it instead of mirroring state from events. <see cref="State"/> is
    /// <see cref="DeviceState.Suspended"/> when this or <see cref="IsSystemSuspended"/> is true.
    /// </summary>
    public bool IsSuspended => _engine.IsUserSuspended;

    /// <summary>
    /// True while the system has the device taken away (an interruption such as a call, or the app in the background on a phone), between the
    /// <see cref="DeviceEventKind.Interrupted"/> and <see cref="DeviceEventKind.Resumed"/> events.
    /// </summary>
    public bool IsSystemSuspended => _engine.IsSystemSuspended;

    /// <summary>
    /// When true the device is fed silence without asking the source for audio, so the source's clock (a mixer's timeline, its voices) stands
    /// still; set false and it continues where it stopped. The cut is immediate: fade with the mixer's master gain first if a click matters.
    /// <see cref="DeviceCounters.MutedFrames"/> counts the silence; the rest of the counters keep saying what was played (<see cref="DeviceCounters.PeakPlayed"/>
    /// stays put). Default false. Safe from any thread.
    /// </summary>
    public bool Muted
    {
        get => _engine.Muted;
        set => _engine.Muted = value;
    }

    /// <summary>
    /// Called on the device thread at the start of every block, before the source renders it, with the number of frames handed to the device
    /// so far (<see cref="DeviceCounters.FramesWritten"/>, which keeps counting across reopens). It follows the same rules as the source: no
    /// allocation, no lock, no blocking; a throw is caught and counted in <see cref="DeviceCounters.CallbackFaults"/>. Null (the default) means none.
    /// </summary>
    public Action<long>? BlockHook
    {
        get => _engine.BlockHook;
        set => _engine.BlockHook = value;
    }

    /// <summary>Clears the meter's history (integrated loudness, true peak) at the next block.</summary>
    public void ResetLoudness() => _engine.ResetLoudness();

    /// <summary>Resets <see cref="DeviceCounters.PeakWritten"/> and <see cref="DeviceCounters.PeakPlayed"/> to 0.</summary>
    public void ResetPeaks() => _engine.ResetPeaks();

    /// <summary>
    /// Raised on the supervisor thread when something happens to the device (lost, restored, default changed, interrupted, ran dry).
    /// Manual devices raise it from <see cref="Pump"/>. A handler that throws is caught and counted.
    /// </summary>
    public event Action<DeviceEvent>? Event
    {
        add => _engine.Event += value;
        remove => _engine.Event -= value;
    }

    /// <summary>
    /// Manual backends only: how many frames the device would like next (for example the free room in a device-side buffer); 0 when the backend
    /// does not say or no device is open.
    /// </summary>
    public int WantedFrames => _engine.WantedFrames;

    /// <summary>Takes the oldest unread event, for programs that poll instead of subscribing. Hold one reader; the queue keeps 64 and counts drops.</summary>
    /// <param name="deviceEvent">The event.</param>
    /// <returns>False when there is none.</returns>
    public bool TryGetEvent(out DeviceEvent deviceEvent) => _engine.TryGetEvent(out deviceEvent);

    /// <summary>
    /// Opens the device and starts playing. A device that cannot be opened is not an error: the output enters <see cref="DeviceState.NoDevice"/>,
    /// keeps rendering silently at real-time pace and retries twice a second (see <see cref="LastError"/>). Does nothing if already started.
    /// </summary>
    /// <exception cref="ObjectDisposedException">The output was disposed.</exception>
    public void Start() => _engine.Start();

    /// <summary>
    /// Stops the device without closing it (an app moving to the background); nothing renders until <see cref="Resume"/>. The source's clock stops
    /// (no frames are rendered, so <see cref="DeviceCounters.FramesRendered"/> and <see cref="DeviceCounters.FramesWritten"/> hold). The device's clock
    /// stops once what was already queued has played out: <see cref="DeviceCounters.FramesPlayed"/> catches up with <see cref="DeviceCounters.FramesWritten"/> on
    /// backends that drain (ALSA) and then holds. In manual mode <see cref="Advance"/> returns 0.
    /// </summary>
    public void Suspend() => _engine.Suspend();

    /// <summary>Starts the device again after <see cref="Suspend"/>.</summary>
    public void Resume() => _engine.Resume();

    /// <summary>Moves to another device, from <see cref="AudioDevices.List"/>; null returns to the system default (and following it). The old device closes first, so there is a short gap.</summary>
    /// <param name="deviceId">The device id, or null.</param>
    public void SelectDevice(string? deviceId) => _engine.Select(deviceId);

    /// <summary>
    /// Manual backends only (<see cref="IAudioDeviceBackend.IsManual"/>): runs the device for <paramref name="frames"/> device frames on the calling thread,
    /// which plays the role of the device thread (call it from one thread only; no thread exists in this mode). It first does whatever supervisor
    /// work is pending (reopen after a loss, follow a default change, raise events, retry a missing device twice a second), so a separate
    /// <see cref="Pump"/> is not needed; in the steady state it takes no lock. With no device open the frames are rendered and discarded.
    /// A backend that drives its stream from its own callbacks (so the stream is not an <see cref="IManualDevice"/>) runs nothing here and returns 0.
    /// Returns the frames run, 0 while suspended.
    /// </summary>
    /// <param name="frames">How many frames to run.</param>
    /// <exception cref="InvalidOperationException">The backend is not manual.</exception>
    public int Advance(int frames) => _engine.Advance(frames);

    /// <summary>
    /// Manual backends only: does the supervisor's work now (reopen after a loss, follow a default change, raise events), without running any
    /// frames. <see cref="Advance"/> does this by itself when something is pending; call <c>Pump</c> to see the effect of an event right away,
    /// for example when the host is idle and not advancing.
    /// </summary>
    /// <exception cref="InvalidOperationException">The backend is not manual.</exception>
    public void Pump() => _engine.Pump();

    /// <summary>Stops and closes the device and ends the supervisor thread. Safe to call more than once and from an event handler.</summary>
    public void Dispose() => _engine.Dispose();
}

internal static class DeviceValidation
{
    public static void Check(AudioDeviceFormat format, DeviceStreamOptions options)
    {
        ArgumentOutOfRangeException.ThrowIfLessThan(format.SampleRate, 8000);
        ArgumentOutOfRangeException.ThrowIfGreaterThan(format.SampleRate, 384_000);
        ArgumentOutOfRangeException.ThrowIfLessThan(format.Channels, 1);
        ArgumentOutOfRangeException.ThrowIfGreaterThan(format.Channels, 16);
        ArgumentOutOfRangeException.ThrowIfNegative(options.PeriodFrames);
        ArgumentOutOfRangeException.ThrowIfNegative(options.BufferFrames);
        ArgumentOutOfRangeException.ThrowIfLessThan(options.LatencyMilliseconds, 1);
        ArgumentOutOfRangeException.ThrowIfLessThan(options.CaptureBufferMilliseconds, 10);
        ArgumentOutOfRangeException.ThrowIfNegative(options.AllocationWarmupBlocks);
    }
}
