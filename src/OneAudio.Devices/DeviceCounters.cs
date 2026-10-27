namespace OneAudio.Devices;

/// <summary>Where a device is in its life.</summary>
public enum DeviceState
{
    /// <summary>Created, <see cref="AudioOutputDevice.Start"/> not called yet.</summary>
    Created,

    /// <summary>Samples are flowing between the program and a device.</summary>
    Running,

    /// <summary>Stopped by <c>Suspend</c> (or by the system, see <see cref="DeviceEventKind.Interrupted"/>); the device stays open.</summary>
    Suspended,

    /// <summary>
    /// No device is open (none exists, or it was lost). An output keeps rendering at real-time pace and discards the samples so the
    /// program behaves the same without sound; an input delivers nothing. The device retries opening twice a second.
    /// </summary>
    NoDevice,

    /// <summary>Disposed.</summary>
    Stopped,
}

/// <summary>The kinds of <see cref="DeviceEvent"/>.</summary>
public enum DeviceEventKind
{
    /// <summary>The system changed its default device and the stream is following (<see cref="DeviceEvent.FromId"/> to <see cref="DeviceEvent.ToId"/>).</summary>
    DefaultChanged,

    /// <summary>The open device went away (unplugged, disabled, invalidated). The stream reopens at once, or drops to <see cref="DeviceState.NoDevice"/>.</summary>
    DeviceLost,

    /// <summary>A device opened again after <see cref="DeviceState.NoDevice"/>.</summary>
    DeviceRestored,

    /// <summary>A device opened again right after a loss, a default change or <c>SelectDevice</c>, without a gap in rendering.</summary>
    Reopened,

    /// <summary>The device ran dry (output) or overflowed (input). <see cref="DeviceEvent.Detail"/> carries the frame count.</summary>
    Xrun,

    /// <summary>The system took the device away (a phone call, another app, the app moved to the background).</summary>
    Interrupted,

    /// <summary>The system returned the device after <see cref="Interrupted"/>.</summary>
    Resumed,
}

/// <summary>Something that happened to a device. Delivered on the device's supervisor thread, never on the audio thread.</summary>
/// <param name="Kind">What happened.</param>
/// <param name="FromId">The device id before the change, or <see langword="null"/>.</param>
/// <param name="ToId">The device id after the change, or <see langword="null"/>.</param>
/// <param name="FramesPlayed">The value of <see cref="DeviceCounters.FramesPlayed"/> when it happened.</param>
/// <param name="Detail">A short reason or number, or <see langword="null"/>.</param>
public readonly record struct DeviceEvent(DeviceEventKind Kind, string? FromId, string? ToId, long FramesPlayed, string? Detail);

/// <summary>
/// What a device has really done, read from the device side where the operating system allows. A state such as
/// <see cref="DeviceState.Running"/> says nothing about sound; these numbers do. Direction matters for three fields: for an input
/// device <see cref="FramesWritten"/> and <see cref="FramesRendered"/> count frames received from the device, and
/// <see cref="FramesPlayed"/> is the device's own capture position.
/// </summary>
/// <param name="FramesRendered">Frames the program's render callback produced for this device, silent frames while no device was open included.</param>
/// <param name="FramesWritten">Frames handed to the device since the stream opened for the first time (not counting <see cref="SilentFrames"/>).</param>
/// <param name="FramesPlayed">Frames the device has consumed, from its own clock or position, not from our writes. Always at most <see cref="FramesWritten"/>.</param>
/// <param name="PeakWritten">The largest absolute sample handed to the device, after limiting.</param>
/// <param name="PeakPlayed">The largest absolute sample among frames the device has consumed; 0 means everything it played was silence.</param>
/// <param name="Underruns">Times the device ran dry because audio arrived late (output).</param>
/// <param name="Overruns">Times the device or the read buffer overflowed because audio was taken late (input).</param>
/// <param name="SilentFrames">Frames rendered at real-time pace and discarded because no device was open.</param>
/// <param name="CallbackFaults">Times the program's callback or source threw; each replaced its block by silence.</param>
/// <param name="DeviceLosses">Times an open device went away.</param>
/// <param name="Reopens">Successful opens after the first one (after a loss, a default change or an explicit switch).</param>
/// <param name="DefaultDeviceSwitches">Times the stream moved because the system default changed.</param>
/// <param name="BufferFrames">The negotiated total device buffer; 0 while no device is open.</param>
/// <param name="PeriodFrames">The negotiated period; 0 while no device is open.</param>
/// <param name="LatencyMilliseconds">Audio queued ahead of the speaker, as last measured, in milliseconds (BufferFrames when the backend cannot measure).</param>
/// <param name="MaxRenderMilliseconds">The longest a render or capture callback has taken, in milliseconds. Compare with the period.</param>
/// <param name="RenderAllocatedBytes">Bytes allocated by the render callback after its first few warm-up calls; should stay 0 (the probe is always on).</param>
/// <param name="EventsDropped">Events lost because the event queue was full.</param>
/// <param name="MutedFrames">Frames fed to the device as silence, or discarded with no device, while <see cref="AudioOutputDevice.Muted"/> was on; the source was not asked for them.</param>
/// <param name="DeviceUnderruns">Underruns the device itself counted, for backends that count them (for example an audio API's xrun counter), across reopens. <see cref="Underruns"/> is the package's own tally of times it saw the device run dry; the two can differ, and a backend that has no counter leaves this at 0.</param>
public readonly record struct DeviceCounters(
    long FramesRendered,
    long FramesWritten,
    long FramesPlayed,
    float PeakWritten,
    float PeakPlayed,
    long Underruns,
    long Overruns,
    long SilentFrames,
    long CallbackFaults,
    long DeviceLosses,
    long Reopens,
    long DefaultDeviceSwitches,
    int BufferFrames,
    int PeriodFrames,
    double LatencyMilliseconds,
    double MaxRenderMilliseconds,
    long RenderAllocatedBytes,
    long EventsDropped,
    long MutedFrames = 0,
    long DeviceUnderruns = 0);
