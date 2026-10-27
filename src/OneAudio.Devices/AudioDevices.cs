namespace OneAudio.Devices;

/// <summary>
/// The audio devices of this machine: list them, find the default, and be told when the set changes. Everything here goes through one
/// <see cref="IAudioDeviceBackend"/>, the operating system's by default; a program can replace it with <see cref="UseBackend"/>.
/// </summary>
public static class AudioDevices
{
    private static readonly Lock s_gate = new();
    private static IAudioDeviceBackend? s_backend;

    /// <summary>
    /// Raised when the set of devices changes (plug, unplug, enable, disable) on backends that can tell. It runs on an operating-system
    /// thread, never on an audio thread; keep the handler short. Not raised for default changes; devices following the default handle those.
    /// </summary>
    public static event Action? DevicesChanged;

    /// <summary>
    /// The backend in use: the operating system's (WASAPI, Core Audio, ALSA) unless <see cref="UseBackend"/> replaced it. Never null; on a
    /// platform without a backend it is one that reports <see cref="IAudioDeviceBackend.IsAvailable"/> false and opens nothing.
    /// </summary>
    public static IAudioDeviceBackend Backend
    {
        get
        {
            lock (s_gate)
            {
                return s_backend ??= PlatformBackends.Create() ?? UnavailableBackend.Instance;
            }
        }
    }

    /// <summary>Whether the operating system's audio library was found. When false, outputs run silent and inputs deliver nothing.</summary>
    public static bool IsAvailable => Backend.IsAvailable;

    /// <summary>Replaces the backend for devices opened from now on (a browser backend, a test double). Pass <see langword="null"/> to go back to the operating system's.</summary>
    /// <param name="backend">The backend, or null for the platform default.</param>
    public static void UseBackend(IAudioDeviceBackend? backend)
    {
        lock (s_gate)
        {
            s_backend = backend;
        }
    }

    /// <summary>Lists the devices of one direction. Allocates; call from a control thread, never from an audio callback.</summary>
    /// <param name="direction">Playback or capture devices.</param>
    /// <returns>The devices; empty when the audio system is unavailable.</returns>
    public static IReadOnlyList<AudioDeviceInfo> List(DeviceDirection direction) => Backend.List(direction);

    /// <summary>The device the system uses by default for a direction, or <see langword="null"/> when there is none.</summary>
    /// <param name="direction">Playback or capture.</param>
    public static AudioDeviceInfo? Default(DeviceDirection direction) => Backend.GetDefault(direction);

    internal static void RaiseDevicesChanged()
    {
        var handler = DevicesChanged;
        if (handler is null)
        {
            return;
        }

        // The handler is the program's code on an operating-system thread; its failure must not take the process down.
        try
        {
            handler();
        }
        catch (Exception)
        {
            // Nothing to report to; the next change tries again.
        }
    }
}

/// <summary>Chooses the backend for the platform this process runs on. Each platform's backend is created here.</summary>
internal static class PlatformBackends
{
    public static IAudioDeviceBackend? Create()
    {
        if (OperatingSystem.IsLinux() || OperatingSystem.IsFreeBSD())
        {
            return CreateLinux();
        }

        if (OperatingSystem.IsMacOS() || OperatingSystem.IsIOS())
        {
            return CreateApple();
        }

        if (OperatingSystem.IsWindows())
        {
            return CreateWindows();
        }

        return null;
    }

    private static IAudioDeviceBackend? CreateLinux() => null;

    private static IAudioDeviceBackend? CreateApple() => null;

    private static IAudioDeviceBackend? CreateWindows() => null;
}

/// <summary>The backend of a platform with no audio support: lists nothing and opens nothing, so devices run silent.</summary>
internal sealed class UnavailableBackend : IAudioDeviceBackend
{
    public static readonly UnavailableBackend Instance = new();

    public string Name => "none";

    public DeviceBackendKind Kind => DeviceBackendKind.Custom;

    public bool IsManual => false;

    public bool NotifiesDeviceChanges => false;

    public bool IsAvailable => false;

    public IReadOnlyList<AudioDeviceInfo> List(DeviceDirection direction) => [];

    public AudioDeviceInfo? GetDefault(DeviceDirection direction) => null;

    public bool TryOpen(DeviceDirection direction, DeviceStreamOptions options, IDeviceEngine engine, out IOpenDevice? stream, out string reason)
    {
        stream = null;
        reason = "there is no audio backend on this platform";
        return false;
    }
}
