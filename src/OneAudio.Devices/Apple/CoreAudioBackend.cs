using OneAudio.Devices.Interop;

namespace OneAudio.Devices;

/// <summary>
/// Apple audio: the Core Audio hardware layer with an AUHAL audio unit on macOS, the remote I/O unit with an <c>AVAudioSession</c> on iOS.
/// <para>
/// <b>macOS.</b> <see cref="List"/> enumerates the hardware devices with their channel counts, nominal rates and smallest buffers; ids are the
/// devices' persistent UIDs. The backend raises <see cref="IDeviceEngine.NotifyDefaultChanged"/> when the system default changes, tells
/// <see cref="AudioDevices.DevicesChanged"/> about plug and unplug, and reports a removed device as lost. The unit converts the sample rate to
/// the hardware's for playback; channel counts are mapped here (mono feeds the first two channels, other sources fill the leading channels,
/// and a device with fewer channels keeps the leading ones, or the average for mono). Capture runs at the hardware rate and is mapped and
/// resampled to the requested format here. The hardware buffer size is left to the system unless the options ask for a period or buffer
/// explicitly, because it is shared with every other program using the device. Exclusive mode is not offered.
/// </para>
/// <para>
/// <b>Microphone permission.</b> Opening an input device fails with a reason when the user has denied the process access. When the user has
/// not been asked yet the system asks on the first start; a program that is not an app bundle with a microphone usage description may not be
/// allowed to ask, in which case it receives silence.
/// </para>
/// <para>
/// <b>iOS.</b> The one route is exposed as a device with id <c>default</c>. The audio session is configured from
/// <see cref="DeviceStreamOptions.AppleSession"/> (an input stream always uses play-and-record, routed to the speaker); an interruption
/// (call, alarm, another app) pauses the stream and ends with the session reactivated; a route change counts as a default-device change, which
/// the unit follows by itself; a media-services reset reports the device lost so the engine reopens it.
/// </para>
/// </summary>
public sealed class CoreAudioBackend : IAudioDeviceBackend
{
    private static readonly Lock s_gate = new();
    private static HalListener? s_devicesListener;

    /// <inheritdoc/>
    public string Name => "coreaudio";

    /// <inheritdoc/>
    public DeviceBackendKind Kind => DeviceBackendKind.CoreAudio;

    /// <inheritdoc/>
    public bool IsManual => false;

    /// <inheritdoc/>
    public bool NotifiesDeviceChanges => true;

    /// <inheritdoc/>
    public bool IsAvailable => AppleNative.IsPresent;

    /// <inheritdoc/>
    public IReadOnlyList<AudioDeviceInfo> List(DeviceDirection direction)
    {
        if (!IsAvailable)
        {
            return [];
        }

        try
        {
            if (!OperatingSystem.IsMacOS())
            {
                return [RouteDevice(direction)];
            }

            EnsureDevicesListener();
            bool output = direction == DeviceDirection.Output;
            uint defaultDevice = Hal.DefaultDevice(output);
            var result = new List<AudioDeviceInfo>();
            foreach (var device in Hal.Devices())
            {
                if (Channels(device, output) > 0)
                {
                    result.Add(ToInfo(device, direction, device.Object == defaultDevice));
                }
            }

            return result;
        }
        catch (Exception e) when (e is DllNotFoundException or EntryPointNotFoundException)
        {
            return [];
        }
    }

    /// <inheritdoc/>
    public AudioDeviceInfo? GetDefault(DeviceDirection direction)
    {
        if (!IsAvailable)
        {
            return null;
        }

        try
        {
            if (!OperatingSystem.IsMacOS())
            {
                return RouteDevice(direction);
            }

            uint id = Hal.DefaultDevice(direction == DeviceDirection.Output);
            return id != 0 && Hal.Describe(id) is { } device ? ToInfo(device, direction, true) : null;
        }
        catch (Exception e) when (e is DllNotFoundException or EntryPointNotFoundException)
        {
            return null;
        }
    }

    /// <inheritdoc/>
    public bool TryOpen(DeviceDirection direction, DeviceStreamOptions options, IDeviceEngine engine, out IOpenDevice? stream, out string reason)
    {
        ArgumentNullException.ThrowIfNull(options);
        ArgumentNullException.ThrowIfNull(engine);
        stream = null;
        if (!IsAvailable)
        {
            reason = "Audio Toolbox is not available on this system";
            return false;
        }

        if (options.Mode == ShareMode.Exclusive)
        {
            reason = "Core Audio opens shared streams only; exclusive mode is a WASAPI feature";
            return false;
        }

        bool output = direction == DeviceDirection.Output;
        try
        {
            if (!output && Denied(out reason))
            {
                return false;
            }

            AudioDeviceInfo info;
            uint deviceObject = 0;
            if (OperatingSystem.IsMacOS())
            {
                EnsureDevicesListener();
                HalDevice? chosen = null;
                if (options.DeviceId is null)
                {
                    uint id = Hal.DefaultDevice(output);
                    chosen = id != 0 ? Hal.Describe(id) : null;
                    if (chosen is null)
                    {
                        reason = $"there is no default {(output ? "output" : "input")} device";
                        return false;
                    }
                }
                else
                {
                    foreach (var candidate in Hal.Devices())
                    {
                        if (candidate.Uid == options.DeviceId)
                        {
                            chosen = candidate;
                            break;
                        }
                    }

                    if (chosen is null || Channels(chosen.Value, output) == 0)
                    {
                        reason = $"'{options.DeviceId}' is not a {(output ? "playback" : "capture")} device here";
                        return false;
                    }
                }

                deviceObject = chosen.Value.Object;
                info = ToInfo(chosen.Value, direction, options.DeviceId is null || chosen.Value.Object == Hal.DefaultDevice(output));
            }
            else
            {
                if (options.DeviceId is not null && options.DeviceId != "default")
                {
                    reason = $"'{options.DeviceId}' is not a device here; iOS has the one route, id 'default'";
                    return false;
                }

                info = RouteDevice(direction);
            }

            var opened = AppleStream.TryCreate(engine, output, options, info, deviceObject, options.DeviceId is null, out reason);
            stream = opened;
            return opened is not null;
        }
        catch (Exception e) when (e is DllNotFoundException or EntryPointNotFoundException)
        {
            reason = "Core Audio is not available: " + e.Message;
            return false;
        }
    }

    /// <summary>Posts the notification the system sends when a call or another app takes the audio session, so the whole interruption path runs without a phone call. iOS only.</summary>
    internal static bool SimulateInterruption(bool began) => AppleSession.PostInterruption(began);

    private static bool Denied(out string reason)
    {
        switch (AppleSession.Microphone())
        {
            case MicrophoneAccess.Denied:
                reason = OperatingSystem.IsIOS()
                    ? "microphone access is denied; allow it for this app in Settings > Privacy"
                    : "microphone access is denied; allow it for this program in System Settings > Privacy & Security > Microphone";
                return true;
            default:
                reason = string.Empty;
                return false;
        }
    }

    private static int Channels(HalDevice device, bool output) => output ? device.OutputChannels : device.InputChannels;

    private static AudioDeviceInfo ToInfo(HalDevice device, DeviceDirection direction, bool isDefault) =>
        new(device.Uid, device.Name, direction, isDefault, (int)device.Rate, Channels(device, direction == DeviceDirection.Output), device.MinFrames, DeviceBackendKind.CoreAudio);

    private static AudioDeviceInfo RouteDevice(DeviceDirection direction)
    {
        bool input = direction == DeviceDirection.Input;
        return new AudioDeviceInfo("default", AppleSession.RouteName(input, input ? "Default input" : "Default output"), direction, true, AppleSession.SampleRate(), AppleSession.RouteChannels(input), 0, DeviceBackendKind.CoreAudio);
    }

    // One listener for the process: any change to the device list tells AudioDevices.DevicesChanged, with or without an open stream.
    private static void EnsureDevicesListener()
    {
        lock (s_gate)
        {
            s_devicesListener ??= Hal.ListenDevices(AudioDevices.RaiseDevicesChanged);
        }
    }
}
