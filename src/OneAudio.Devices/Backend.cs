namespace OneAudio.Devices;

/// <summary>
/// An operating-system audio API, or a stand-in for one. Backends are written against this seam; applications rarely touch it
/// except to plug in their own (for example a browser backend) with <see cref="AudioDevices.UseBackend"/>.
/// </summary>
public interface IAudioDeviceBackend
{
    /// <summary>A short name for diagnostics, such as <c>alsa</c>.</summary>
    string Name { get; }

    /// <summary>The API behind the backend.</summary>
    DeviceBackendKind Kind { get; }

    /// <summary>
    /// When true nothing paces the backend's streams, the device runs no supervisor thread and the caller advances it with
    /// <see cref="AudioOutputDevice.Advance"/>. Only <see cref="ManualBackend"/> is manual.
    /// </summary>
    bool IsManual { get; }

    /// <summary>
    /// True when the backend itself calls <see cref="IDeviceEngine.NotifyDefaultChanged"/> and <see cref="IDeviceEngine.NotifyDevicesChanged"/>;
    /// otherwise a stream following the default polls <see cref="GetDefault"/> about once a second.
    /// </summary>
    bool NotifiesDeviceChanges { get; }

    /// <summary>Whether the operating-system audio library could be loaded here. Never throws.</summary>
    bool IsAvailable { get; }

    /// <summary>Lists devices of one direction. Allocates; call from a control thread. Returns an empty list when the audio system is unavailable.</summary>
    /// <param name="direction">Playback or capture devices.</param>
    IReadOnlyList<AudioDeviceInfo> List(DeviceDirection direction);

    /// <summary>The device the system currently uses by default, or <see langword="null"/> when there is none. Cheap enough to call once a second.</summary>
    /// <param name="direction">Playback or capture.</param>
    AudioDeviceInfo? GetDefault(DeviceDirection direction);

    /// <summary>
    /// Opens a stream without starting it. Never throws for an unavailable device: failure returns false with a reason. The stream must
    /// honour <see cref="DeviceStreamOptions.SampleRate"/> and <see cref="DeviceStreamOptions.Channels"/> (converting in the operating
    /// system or itself) and run in 32-bit float. After <see cref="IOpenDevice.Start"/> it calls <paramref name="engine"/>
    /// from the device thread.
    /// </summary>
    /// <param name="direction">Playback or capture.</param>
    /// <param name="options">What to open; <see cref="DeviceStreamOptions.DeviceId"/> null means the system default.</param>
    /// <param name="engine">Where the stream sends audio and notifications.</param>
    /// <param name="stream">The unstarted stream on success.</param>
    /// <param name="reason">Why the open failed; empty on success.</param>
    bool TryOpen(DeviceDirection direction, DeviceStreamOptions options, IDeviceEngine engine, out IOpenDevice? stream, out string reason);
}

/// <summary>An open, negotiated device stream. Its members may be called from any thread unless stated.</summary>
public interface IOpenDevice : IDisposable
{
    /// <summary>The format the stream runs at (equal to the one asked for).</summary>
    AudioDeviceFormat Format { get; }

    /// <summary>The device this stream really opened.</summary>
    AudioDeviceInfo? Device { get; }

    /// <summary>The negotiated period in frames.</summary>
    int PeriodFrames { get; }

    /// <summary>The negotiated total buffer in frames.</summary>
    int BufferFrames { get; }

    /// <summary>
    /// Frames the device has consumed (output) or captured (input) since this stream started, read from the device's own clock or
    /// position. Monotonic, thread-safe, cheap.
    /// </summary>
    long FramesPlayed { get; }

    /// <summary>Audio queued ahead of the speaker (output) or waiting to be read (input), in frames, as last measured; may be 0 when the backend cannot tell.</summary>
    int QueuedFrames { get; }

    /// <summary>Route and session extras for diagnostics, or <see langword="null"/>.</summary>
    string? Detail { get; }

    /// <summary>Starts the device's callbacks. Returns false with a reason when the device refuses.</summary>
    bool Start(out string reason);

    /// <summary>Stops the callbacks but keeps the device open. When it returns no callback is running. May be started again.</summary>
    void Pause();
}

/// <summary>
/// What a device stream talks to: the engine owned by <see cref="AudioOutputDevice"/> or <see cref="AudioInputDevice"/>. The first two
/// members are called from the stream's real-time thread; the notifications from any thread. All of them are allocation-free,
/// lock-free and never throw.
/// </summary>
public interface IDeviceEngine
{
    /// <summary>
    /// Output: the stream asks for the next block. <paramref name="interleaved"/> is the device buffer (or a scratch the stream copies
    /// from); the engine overwrites it with limited samples. A stream calls it with any block size.
    /// </summary>
    /// <param name="interleaved">A whole number of frames at the stream's format.</param>
    void Render(Span<float> interleaved);

    /// <summary>Input: the stream delivers a captured block.</summary>
    /// <param name="interleaved">A whole number of frames at the stream's format.</param>
    void Capture(ReadOnlySpan<float> interleaved);

    /// <summary>The device went away; the engine reopens (or falls back to silence). The stream stays valid for disposal.</summary>
    /// <param name="reason">A short constant text, or <see langword="null"/>.</param>
    void NotifyLost(string? reason);

    /// <summary>The device ran dry (output) or overflowed (input) by about this many frames.</summary>
    /// <param name="frames">The size of the gap, or 0 when unknown.</param>
    void NotifyXrun(int frames);

    /// <summary>The system changed its default device for this direction. Only the engine's own follow-default policy decides what happens next.</summary>
    void NotifyDefaultChanged();

    /// <summary>The set of devices changed (hot-plug); raises <see cref="AudioDevices.DevicesChanged"/>.</summary>
    void NotifyDevicesChanged();

    /// <summary>The system took the device away (<paramref name="began"/> true) or gave it back (false).</summary>
    /// <param name="began">True when the interruption started.</param>
    void NotifyInterruption(bool began);
}
