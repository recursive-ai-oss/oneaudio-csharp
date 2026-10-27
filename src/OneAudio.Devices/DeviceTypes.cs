namespace OneAudio.Devices;

/// <summary>Whether a device plays sound or records it.</summary>
public enum DeviceDirection
{
    /// <summary>A playback device (speakers, headphones, an HDMI sink).</summary>
    Output,

    /// <summary>A capture device (a microphone, a line input).</summary>
    Input,
}

/// <summary>How a stream shares the hardware with other applications.</summary>
public enum ShareMode
{
    /// <summary>Mixed with other applications by the operating system. The default everywhere.</summary>
    Shared,

    /// <summary>
    /// The stream owns the device and bypasses the system mixer, for the lowest latency. Only WASAPI offers it; a backend
    /// that cannot honour it fails the open with a reason, and the device then runs without sound.
    /// </summary>
    Exclusive,
}

/// <summary>The operating-system audio API behind a backend.</summary>
public enum DeviceBackendKind
{
    /// <summary>ALSA (Linux; PulseAudio and PipeWire through their ALSA plug-ins).</summary>
    Alsa,

    /// <summary>WASAPI (Windows).</summary>
    Wasapi,

    /// <summary>Core Audio (macOS) or the Audio Unit remote I/O unit (iOS).</summary>
    CoreAudio,

    /// <summary>AAudio (Android).</summary>
    AAudio,

    /// <summary>A <see cref="ManualBackend"/>: no hardware, time advances when the caller says so.</summary>
    Manual,

    /// <summary>A backend supplied by the application.</summary>
    Custom,
}

/// <summary>The iOS audio-session category a stream asks for. Ignored on every other platform.</summary>
public enum AppleSessionCategory
{
    /// <summary>Mixes with other apps' audio and follows the silent switch. The default.</summary>
    Ambient,

    /// <summary>Silences other apps' audio and follows the silent switch.</summary>
    SoloAmbient,

    /// <summary>Plays regardless of the silent switch.</summary>
    Playback,

    /// <summary>Plays and records at once.</summary>
    PlayAndRecord,
}

/// <summary>The sample format a device stream runs at: always interleaved 32-bit float.</summary>
/// <param name="SampleRate">Frames per second.</param>
/// <param name="Channels">Samples per frame.</param>
public readonly record struct AudioDeviceFormat(int SampleRate, int Channels);

/// <summary>One device a backend can open.</summary>
/// <param name="Id">The stable text to pass as <see cref="DeviceStreamOptions.DeviceId"/>. Backend specific; do not parse it.</param>
/// <param name="Name">A human-readable label.</param>
/// <param name="Direction">Whether the device plays or records.</param>
/// <param name="IsDefault">Whether the system currently makes this device the default for its direction.</param>
/// <param name="DefaultSampleRate">The rate the device prefers, in Hz; 0 when the backend cannot tell.</param>
/// <param name="MaxChannels">The most channels the device offers; 0 when the backend cannot tell.</param>
/// <param name="MinLatencyFrames">The smallest buffer the device accepts, in frames; 0 when the backend cannot tell.</param>
/// <param name="Backend">The API this device belongs to.</param>
public readonly record struct AudioDeviceInfo(
    string Id,
    string Name,
    DeviceDirection Direction,
    bool IsDefault,
    int DefaultSampleRate,
    int MaxChannels,
    int MinLatencyFrames,
    DeviceBackendKind Backend);

/// <summary>How the output stage keeps samples in range before they reach the device.</summary>
public enum OutputLimiterMode
{
    /// <summary>Hard clamp to -1..1. Not-a-number becomes 0.</summary>
    Clamp,

    /// <summary>Samples at or below 0.9 in magnitude pass untouched; above that a smooth knee approaches 1 without reaching it. Not-a-number becomes 0.</summary>
    SoftKnee,
}

/// <summary>What a program asks of a device stream. The device may deliver a different buffer size; rate and channel count are always honoured (the backend converts when the hardware can't).</summary>
public sealed record DeviceStreamOptions
{
    /// <summary>
    /// The device to open, from <see cref="AudioDevices.List"/>; <see langword="null"/> (the default) opens the system default device.
    /// </summary>
    public string? DeviceId { get; init; }

    /// <summary>Sample rate in Hz, 8000..384000. Default 48000. Ignored when the stream is opened over an <see cref="IAudioSource"/>, which brings its own.</summary>
    public int SampleRate { get; init; } = 48_000;

    /// <summary>Channels per frame, 1..16. Default 2. Ignored when the stream is opened over an <see cref="IAudioSource"/>.</summary>
    public int Channels { get; init; } = 2;

    /// <summary>Frames per device period (one callback's worth); 0 (the default) lets the backend choose, normally a quarter of the buffer.</summary>
    public int PeriodFrames { get; init; }

    /// <summary>Total device buffer in frames; 0 (the default) derives it from <see cref="LatencyMilliseconds"/>.</summary>
    public int BufferFrames { get; init; }

    /// <summary>Requested total device buffer in milliseconds when <see cref="BufferFrames"/> is 0. Smaller is lower latency and more underrun risk. Default 20.</summary>
    public int LatencyMilliseconds { get; init; } = 20;

    /// <summary>Shared (default) or exclusive access to the hardware.</summary>
    public ShareMode Mode { get; init; } = ShareMode.Shared;

    /// <summary>
    /// With <see cref="DeviceId"/> null: move to the new default device when the system changes it, and reopen on the default when
    /// the current device is lost. Default true. With an explicit id the stream stays on that device and retries it after a loss.
    /// </summary>
    public bool FollowDefault { get; init; } = true;

    /// <summary>
    /// For output: when the device runs at another sample rate than the source (a browser's audio context may run at 44.1 or 48 kHz whatever was
    /// asked), convert in the package with the library's polyphase resampler instead of failing the open. Default true. The device then asks for
    /// its own number of frames: <see cref="DeviceCounters.FramesWritten"/>, <see cref="DeviceCounters.FramesPlayed"/> and the buffer sizes count
    /// device frames, <see cref="DeviceCounters.FramesRendered"/> counts the source's frames. The resampler adds a few milliseconds of latency
    /// and is allocated when the device opens, so the render path still allocates nothing. Ratios between 1/4 and 16 are supported.
    /// </summary>
    public bool AllowRateConversion { get; init; } = true;

    /// <summary>
    /// For output: how samples are kept inside -1..1 on their way to the device. Default <see cref="OutputLimiterMode.SoftKnee"/>.
    /// </summary>
    public OutputLimiterMode Limiter { get; init; } = OutputLimiterMode.SoftKnee;

    /// <summary>Measure BS.1770 loudness at the output (after the limiter), readable from <see cref="AudioOutputDevice.Loudness"/>. Default false.</summary>
    public bool Metering { get; init; }

    /// <summary>
    /// Count the bytes the render callback allocates into <see cref="DeviceCounters.RenderAllocatedBytes"/> (skipping the first few
    /// callbacks, which still warm up). A diagnostic for tests and hosts; costs one call per block. Default false.
    /// </summary>
    public bool MeasureRenderAllocations { get; init; }

    /// <summary>For input devices read with <see cref="AudioInputDevice.Read"/>: how much audio the read buffer holds, in milliseconds. Default 1000.</summary>
    public int CaptureBufferMilliseconds { get; init; } = 1000;

    /// <summary>The iOS audio-session category. Default <see cref="AppleSessionCategory.Ambient"/>.</summary>
    public AppleSessionCategory AppleSession { get; init; } = AppleSessionCategory.Ambient;
}

/// <summary>Where a block sits in the stream, passed to render and capture callbacks.</summary>
/// <param name="FramePosition">Frames handed to (or received from) the current stream before this block. Restarts at 0 when the device is reopened.</param>
/// <param name="FramesPlayed">Frames the device has consumed (or captured) so far across the device's whole life, from its own position, as last polled (within a few milliseconds); 0 until known.</param>
public readonly record struct RenderContext(long FramePosition, long FramesPlayed);

/// <summary>
/// Fills <paramref name="interleaved"/> (a whole number of frames) on the device's real-time thread. Must not allocate, lock, block or do I/O.
/// An exception it throws is caught, counted in <see cref="DeviceCounters.CallbackFaults"/> and the block is replaced by silence.
/// </summary>
/// <param name="interleaved">Overwrite it; values outside -1..1 are limited afterwards.</param>
/// <param name="context">Where the block sits in the stream.</param>
public delegate void RenderCallback(Span<float> interleaved, in RenderContext context);

/// <summary>
/// Receives captured audio on the device's real-time thread. Must not allocate, lock, block or do I/O. An exception it throws is caught
/// and counted in <see cref="DeviceCounters.CallbackFaults"/>.
/// </summary>
/// <param name="interleaved">Interleaved samples at the stream's format; valid only during the call.</param>
/// <param name="context">Where the block sits in the stream.</param>
public delegate void CaptureCallback(ReadOnlySpan<float> interleaved, in RenderContext context);
