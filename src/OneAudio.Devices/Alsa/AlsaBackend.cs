using System.Runtime.InteropServices;
using OneAudio.Devices.Interop;

namespace OneAudio.Devices;

/// <summary>
/// Linux audio through ALSA's PCM interface. On a PipeWire or PulseAudio desktop the <c>default</c> device is the sound server's own ALSA plug-in,
/// so this reaches the system mixer, follows the user's chosen output inside the plug-in and converts rate and channels; on a bare system it is
/// the hardware. One small, stable C API covers every Linux audio setup.
/// <para>
/// Device ids are ALSA PCM names (<c>default</c>, <c>hw:CARD=...,DEV=...</c>, <c>pulse</c>, <c>file:/path,raw</c> and so on); any name alsa-lib accepts
/// can be passed as <see cref="DeviceStreamOptions.DeviceId"/> even if <see cref="List"/> does not show it. ALSA reports no hot-plug events, so the set
/// of devices is read when asked for, and a system default change is invisible from here: it happens inside the <c>default</c> plug-in, which
/// keeps the stream running on the new device. Exclusive mode does not exist in this backend (a <c>hw:</c> device is exclusive in practice).
/// Samples are 32-bit float; a device that refuses float gets 16-bit with round-to-nearest conversion (the engine has already limited the
/// samples to -1..1, so nothing clips).
/// </para>
/// </summary>
public sealed class AlsaBackend : IAudioDeviceBackend
{
    /// <inheritdoc/>
    public string Name => "alsa";

    /// <inheritdoc/>
    public DeviceBackendKind Kind => DeviceBackendKind.Alsa;

    /// <inheritdoc/>
    public bool IsManual => false;

    /// <inheritdoc/>
    public bool NotifiesDeviceChanges => false;

    /// <inheritdoc/>
    public bool IsAvailable => OperatingSystem.IsLinux() && AlsaNative.IsPresent;

    /// <inheritdoc/>
    public IReadOnlyList<AudioDeviceInfo> List(DeviceDirection direction)
    {
        var result = new List<AudioDeviceInfo>();
        if (!IsAvailable)
        {
            return result;
        }

        try
        {
            AlsaNative.Quiet();
            if (AlsaNative.NameHint(-1, "pcm", out nint hints) < 0 || hints == 0)
            {
                return result;
            }

            try
            {
                string wanted = direction == DeviceDirection.Output ? "Output" : "Input";
                for (int i = 0; ; i++)
                {
                    nint hint = Marshal.ReadIntPtr(hints, i * IntPtr.Size);
                    if (hint == 0)
                    {
                        break;
                    }

                    string? name = TakeString(hint, "NAME");
                    string? io = TakeString(hint, "IOID"); // null means both directions
                    if (name is null || name == "null" || (io is not null && io != wanted))
                    {
                        continue;
                    }

                    string description = TakeString(hint, "DESC")?.Replace('\n', ' ') ?? name;
                    result.Add(new AudioDeviceInfo(name, description, direction, name == "default", 0, 0, 0, DeviceBackendKind.Alsa));
                }
            }
            finally
            {
                _ = AlsaNative.NameFreeHint(hints);
            }
        }
        catch (Exception e) when (e is DllNotFoundException or EntryPointNotFoundException)
        {
            result.Clear();
        }

        return result;
    }

    /// <inheritdoc/>
    public AudioDeviceInfo? GetDefault(DeviceDirection direction) =>
        IsAvailable ? new AudioDeviceInfo("default", "Default", direction, true, 0, 0, 0, DeviceBackendKind.Alsa) : null;

    /// <inheritdoc/>
    public bool TryOpen(DeviceDirection direction, DeviceStreamOptions options, IDeviceEngine engine, out IOpenDevice? stream, out string reason)
    {
        ArgumentNullException.ThrowIfNull(options);
        ArgumentNullException.ThrowIfNull(engine);
        stream = null;
        if (!IsAvailable)
        {
            reason = "libasound is not installed";
            return false;
        }

        if (options.Mode == ShareMode.Exclusive)
        {
            reason = "ALSA has no exclusive mode; open a hw: device by id instead";
            return false;
        }

        try
        {
            AlsaNative.Quiet();
            string name = options.DeviceId ?? "default";
            int streamKind = direction == DeviceDirection.Output ? AlsaNative.StreamPlayback : AlsaNative.StreamCapture;
            string lastFailure = string.Empty;
            foreach (bool isFloat in (ReadOnlySpan<bool>)[true, false])
            {
                int rc = AlsaNative.Open(out nint pcm, name, streamKind, 0);
                if (rc < 0)
                {
                    reason = $"snd_pcm_open('{name}') failed: errno {-rc}";
                    return false;
                }

                if (!Configure(pcm, options, isFloat, out int bufferFrames, out int periodFrames, out lastFailure))
                {
                    _ = AlsaNative.Close(pcm);
                    continue;
                }

                var info = new AudioDeviceInfo(name, name, direction, name == "default", 0, 0, 0, DeviceBackendKind.Alsa);
                stream = new AlsaStream(pcm, direction, engine, new AudioDeviceFormat(options.SampleRate, options.Channels), info, isFloat, periodFrames, bufferFrames);
                reason = string.Empty;
                return true;
            }

            reason = $"'{name}' accepts neither float nor 16-bit {options.SampleRate} Hz x {options.Channels} ({lastFailure})";
            return false;
        }
        catch (Exception e) when (e is DllNotFoundException or EntryPointNotFoundException)
        {
            reason = "libasound is not installed";
            return false;
        }
    }

    private static bool Configure(nint pcm, DeviceStreamOptions o, bool isFloat, out int bufferFrames, out int periodFrames, out string failure)
    {
        int format = isFloat ? AlsaNative.FormatFloatLe : AlsaNative.FormatS16Le;
        bufferFrames = 0;
        periodFrames = 0;
        failure = string.Empty;
        if (o.PeriodFrames > 0 || o.BufferFrames > 0)
        {
            return ConfigureExplicit(pcm, o, format, out bufferFrames, out periodFrames, out failure);
        }

        uint latency = (uint)Math.Clamp(o.LatencyMilliseconds, 5, 500) * 1000u;
        int rc = AlsaNative.SetParams(pcm, format, AlsaNative.AccessRwInterleaved, (uint)o.Channels, (uint)o.SampleRate, 1, latency);
        if (rc < 0)
        {
            failure = $"snd_pcm_set_params errno {-rc}";
            return false;
        }

        rc = AlsaNative.GetParams(pcm, out nuint buffer, out nuint period);
        bufferFrames = (int)buffer;
        periodFrames = (int)period;
        return rc >= 0;
    }

    // Explicit period and buffer sizes need the hardware-parameter container; the library negotiates the nearest values the device allows.
    private static bool ConfigureExplicit(nint pcm, DeviceStreamOptions o, int format, out int bufferFrames, out int periodFrames, out string failure)
    {
        bufferFrames = 0;
        periodFrames = 0;
        failure = string.Empty;
        nint hw = 0;
        nint sw = 0;
        try
        {
            if (AlsaNative.HwMalloc(out hw) < 0)
            {
                failure = "out of memory";
                return false;
            }

            uint rate = (uint)o.SampleRate;
            int dir = 0;
            int period = o.PeriodFrames > 0 ? o.PeriodFrames : Math.Max(16, o.BufferFrames / 4);
            int buffer = o.BufferFrames > 0 ? o.BufferFrames : period * 4;
            nuint bufferSize = (nuint)buffer;
            nuint periodSize = (nuint)period;
            int rc = AlsaNative.HwAny(pcm, hw);
            if (rc >= 0) rc = AlsaNative.HwSetAccess(pcm, hw, AlsaNative.AccessRwInterleaved);
            if (rc >= 0) rc = AlsaNative.HwSetFormat(pcm, hw, format);
            if (rc >= 0) rc = AlsaNative.HwSetChannels(pcm, hw, (uint)o.Channels);
            if (rc >= 0) rc = AlsaNative.HwSetRateResample(pcm, hw, 1);
            if (rc >= 0) rc = AlsaNative.HwSetRateNear(pcm, hw, ref rate, ref dir);
            if (rc >= 0 && rate != (uint)o.SampleRate) rc = -22;
            if (rc >= 0) rc = AlsaNative.HwSetBufferSizeNear(pcm, hw, ref bufferSize);
            if (rc >= 0) rc = AlsaNative.HwSetPeriodSizeNear(pcm, hw, ref periodSize, ref dir);
            if (rc >= 0) rc = AlsaNative.HwApply(pcm, hw);
            if (rc < 0)
            {
                failure = $"hardware parameters errno {-rc}";
                return false;
            }

            // Start once a full buffer is queued, so the first writes prime the device instead of racing it.
            if (AlsaNative.SwMalloc(out sw) >= 0 && AlsaNative.SwCurrent(pcm, sw) >= 0)
            {
                _ = AlsaNative.SwSetStartThreshold(pcm, sw, bufferSize);
                _ = AlsaNative.SwApply(pcm, sw);
            }

            rc = AlsaNative.GetParams(pcm, out nuint finalBuffer, out nuint finalPeriod);
            bufferFrames = (int)finalBuffer;
            periodFrames = (int)finalPeriod;
            return rc >= 0;
        }
        finally
        {
            if (hw != 0)
            {
                AlsaNative.HwFree(hw);
            }

            if (sw != 0)
            {
                AlsaNative.SwFree(sw);
            }
        }
    }

    private static string? TakeString(nint hint, string id)
    {
        nint p = AlsaNative.NameGetHint(hint, id);
        if (p == 0)
        {
            return null;
        }

        string? s = Marshal.PtrToStringUTF8(p);
        Marshal.FreeHGlobal(p); // alsa-lib allocated it with malloc; on Unix FreeHGlobal is free
        return s;
    }
}
