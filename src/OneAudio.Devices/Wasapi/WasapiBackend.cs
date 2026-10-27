using OneAudio.Devices.Interop;

namespace OneAudio.Devices;

/// <summary>
/// Windows audio through WASAPI. Devices are the active MMDevice endpoints; ids are the endpoint ids Windows assigns
/// (<c>{0.0.0.00000000}.{guid}</c>), which stay the same across restarts. The default is the console-role endpoint.
/// <para>
/// <b>Shared mode</b> (the default) is event driven and opens the stream with automatic format conversion and the default sample-rate converter,
/// so any rate and channel count is accepted and the Windows mixer converts to the device's format. The buffer is the larger of the requested
/// latency and what the audio engine allows; Microsoft documents the shared-mode engine period as 10 ms by default (device dependent), so the
/// default 20 ms request normally yields about 20 ms.
/// </para>
/// <para>
/// <b>Exclusive mode</b> (<see cref="ShareMode.Exclusive"/>) owns the endpoint and bypasses the mixer, which allows the smallest buffers the
/// hardware accepts (the minimum period the driver reports, documented by Microsoft as typically 2 to 10 ms) but needs a format the device takes
/// directly: no rate or channel conversion happens. The backend tries 32-bit float, 32-bit integer, 24-bit integer (in 32 or packed in 24 bits)
/// and 16-bit integer, in that order, at exactly the requested rate and channel count; integers are rounded to nearest and clamped. When none is
/// accepted, or another application holds the device, the open is refused with the reason. In exclusive event-driven mode buffer and period are
/// equal, so <see cref="DeviceStreamOptions.PeriodFrames"/> (else <see cref="DeviceStreamOptions.BufferFrames"/>, else the latency) sets the buffer, raised to the
/// device's minimum period and aligned when the driver demands it.
/// </para>
/// <para>
/// Output position comes from the endpoint's <c>IAudioClock</c>. Capture delivers every packet the device produced and counts a data discontinuity
/// as an overrun; a silent packet is delivered as zeros. Plugging, unplugging and default changes arrive through <c>IMMNotificationClient</c>; the
/// notifications are registered the first time the backend lists, opens or queries a device. A device that disappears or is invalidated is
/// reported as lost and the engine reopens it. The device thread is registered with the multimedia class scheduler as "Pro Audio".
/// </para>
/// </summary>
public sealed class WasapiBackend : IAudioDeviceBackend
{
    private static readonly Lock s_gate = new();
    private static readonly WasapiNotifications s_notifications = new();
    private static bool s_registered;
    private static bool s_present;

    /// <inheritdoc/>
    public string Name => "wasapi";

    /// <inheritdoc/>
    public DeviceBackendKind Kind => DeviceBackendKind.Wasapi;

    /// <inheritdoc/>
    public bool IsManual => false;

    /// <inheritdoc/>
    public bool NotifiesDeviceChanges => true;

    /// <inheritdoc/>
    public bool IsAvailable
    {
        get
        {
            if (!OperatingSystem.IsWindows())
            {
                return false;
            }

            lock (s_gate)
            {
                if (s_present)
                {
                    return true;
                }

                try
                {
                    WasapiNative.EnterCom();
                    if (WasapiNative.CreateEnumerator(out nint e) >= 0)
                    {
                        WasapiNative.Release(e);
                        s_present = true;
                    }
                }
                catch (Exception ex) when (ex is DllNotFoundException or EntryPointNotFoundException)
                {
                    return false;
                }

                return s_present;
            }
        }
    }

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
            EnsureNotifications();
            WasapiNative.EnterCom();
            int flow = WasapiLogic.Flow(direction);
            if (WasapiNative.CreateEnumerator(out nint enumerator) < 0)
            {
                return result;
            }

            nint collection = 0;
            try
            {
                string? defaultId = DefaultId(enumerator, flow);
                if (WasapiNative.EnumAudioEndpoints(enumerator, flow, WasapiLogic.DeviceStateActive, out collection) < 0
                    || WasapiNative.CollectionGetCount(collection, out uint count) < 0)
                {
                    return result;
                }

                for (uint i = 0; i < count; i++)
                {
                    if (WasapiNative.CollectionItem(collection, i, out nint device) < 0)
                    {
                        continue;
                    }

                    try
                    {
                        if (BuildInfo(device, direction, defaultId) is { } info)
                        {
                            result.Add(info);
                        }
                    }
                    finally
                    {
                        WasapiNative.Release(device);
                    }
                }
            }
            finally
            {
                WasapiNative.Release(collection);
                WasapiNative.Release(enumerator);
            }
        }
        catch (Exception e) when (e is DllNotFoundException or EntryPointNotFoundException)
        {
            result.Clear();
        }

        return result;
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
            EnsureNotifications();
            WasapiNative.EnterCom();
            if (WasapiNative.CreateEnumerator(out nint enumerator) < 0)
            {
                return null;
            }

            nint device = 0;
            try
            {
                if (WasapiNative.GetDefaultAudioEndpoint(enumerator, WasapiLogic.Flow(direction), WasapiLogic.RoleConsole, out device) < 0)
                {
                    return null;
                }

                return BuildInfo(device, direction, null, isDefault: true);
            }
            finally
            {
                WasapiNative.Release(device);
                WasapiNative.Release(enumerator);
            }
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
            reason = "WASAPI is only available on Windows";
            return false;
        }

        try
        {
            EnsureNotifications();
            WasapiNative.EnterCom();
            return Open(direction, options, engine, out stream, out reason);
        }
        catch (Exception e) when (e is DllNotFoundException or EntryPointNotFoundException)
        {
            reason = "the Windows audio libraries are missing";
            return false;
        }
    }

    private static void EnsureNotifications()
    {
        lock (s_gate)
        {
            if (s_registered)
            {
                return;
            }

            s_registered = true; // one attempt per process; the callbacks are a convenience over polling on the engine side
            WasapiNative.EnterCom();
            if (WasapiNative.CreateEnumerator(out nint enumerator) < 0)
            {
                return;
            }

            // The enumerator and the callback object stay registered for the life of the process.
            nint callback = EndpointNotificationClient.Create(s_notifications);
            if (WasapiNative.RegisterEndpointNotificationCallback(enumerator, callback) < 0)
            {
                WasapiNative.Release(callback);
                WasapiNative.Release(enumerator);
            }
        }
    }

    private static string? DefaultId(nint enumerator, int flow)
    {
        if (WasapiNative.GetDefaultAudioEndpoint(enumerator, flow, WasapiLogic.RoleConsole, out nint device) < 0)
        {
            return null;
        }

        try
        {
            _ = WasapiNative.GetId(device, out string? id);
            return id;
        }
        finally
        {
            WasapiNative.Release(device);
        }
    }

    private static AudioDeviceInfo? BuildInfo(nint device, DeviceDirection direction, string? defaultId, bool isDefault = false)
    {
        if (WasapiNative.GetId(device, out string? id) < 0 || id is null)
        {
            return null;
        }

        string name = WasapiNative.GetFriendlyName(device) ?? id;
        int rate = 0;
        int channels = 0;
        int minFrames = 0;
        if (WasapiNative.ActivateAudioClient(device, out nint client) >= 0)
        {
            try
            {
                if (WasapiNative.GetMixFormat(client, out var mix) >= 0)
                {
                    rate = (int)Math.Min(mix.SamplesPerSec, int.MaxValue);
                    channels = mix.Channels;
                }

                if (rate > 0 && WasapiNative.GetDevicePeriod(client, out _, out long min) >= 0)
                {
                    minFrames = WasapiLogic.ReferenceTimeToFrames(min, rate);
                }
            }
            finally
            {
                WasapiNative.Release(client);
            }
        }

        return new AudioDeviceInfo(id, name, direction, isDefault || id == defaultId, rate, channels, minFrames, DeviceBackendKind.Wasapi);
    }

    private static bool Open(DeviceDirection direction, DeviceStreamOptions options, IDeviceEngine engine, out IOpenDevice? stream, out string reason)
    {
        stream = null;
        int flow = WasapiLogic.Flow(direction);
        string side = direction == DeviceDirection.Output ? "output" : "input";
        int hr = WasapiNative.CreateEnumerator(out nint enumerator);
        if (hr < 0)
        {
            reason = WasapiLogic.Describe("no audio device enumerator", hr);
            return false;
        }

        nint device = 0;
        nint client = 0;
        nint render = 0;
        nint capture = 0;
        nint clock = 0;
        nint bufferEvent = 0;
        nint controlEvent = 0;
        bool handedOver = false;
        try
        {
            hr = options.DeviceId is null
                ? WasapiNative.GetDefaultAudioEndpoint(enumerator, flow, WasapiLogic.RoleConsole, out device)
                : WasapiNative.GetDevice(enumerator, options.DeviceId, out device);
            if (hr < 0)
            {
                reason = options.DeviceId is null
                    ? WasapiLogic.Describe($"there is no default {side} device", hr)
                    : WasapiLogic.Describe($"cannot find {side} device '{options.DeviceId}'", hr);
                return false;
            }

            string? defaultId = options.DeviceId is null ? null : DefaultId(enumerator, flow);
            var info = BuildInfo(device, direction, defaultId, isDefault: options.DeviceId is null);
            bool exclusive = options.Mode == ShareMode.Exclusive;
            int rate = options.SampleRate;
            int channels = options.Channels;

            hr = WasapiNative.ActivateAudioClient(device, out client);
            if (hr < 0)
            {
                reason = WasapiLogic.Describe("cannot activate the audio client", hr);
                return false;
            }

            WasapiSampleKind kind = WasapiSampleKind.Float32;
            if (exclusive)
            {
                if (!InitializeExclusive(device, ref client, options, out kind, out reason))
                {
                    return false;
                }
            }
            else
            {
                var format = WasapiLogic.MakeFormat(kind, rate, channels);
                const uint flags = WasapiLogic.StreamFlagEventCallback | WasapiLogic.StreamFlagAutoConvertPcm | WasapiLogic.StreamFlagSrcDefaultQuality;
                hr = WasapiNative.Initialize(client, 0, flags, WasapiLogic.SharedDuration(options), 0, ref format);
                if (hr < 0)
                {
                    reason = WasapiLogic.Describe($"the device rejected {rate} Hz x {channels} in shared mode", hr);
                    return false;
                }
            }

            bufferEvent = WasapiNative.CreateAutoResetEvent();
            controlEvent = WasapiNative.CreateAutoResetEvent();
            if (bufferEvent == 0 || controlEvent == 0)
            {
                reason = "cannot create the stream's events";
                return false;
            }

            hr = WasapiNative.SetEventHandle(client, bufferEvent);
            if (hr < 0)
            {
                reason = WasapiLogic.Describe("cannot set the stream's event", hr);
                return false;
            }

            hr = WasapiNative.GetBufferSize(client, out uint bufferFrames);
            if (hr < 0 || bufferFrames == 0)
            {
                reason = WasapiLogic.Describe("cannot read the buffer size", hr);
                return false;
            }

            hr = direction == DeviceDirection.Output
                ? WasapiNative.GetRenderClient(client, out render)
                : WasapiNative.GetCaptureClient(client, out capture);
            if (hr < 0)
            {
                reason = WasapiLogic.Describe($"cannot get the {side} client", hr);
                return false;
            }

            _ = WasapiNative.GetClock(client, out clock); // without a clock the position stays 0 and the engine reports what it knows

            int period = (int)bufferFrames;
            if (!exclusive && WasapiNative.GetDevicePeriod(client, out long defaultPeriod, out _) >= 0)
            {
                period = Math.Clamp(WasapiLogic.ReferenceTimeToFrames(defaultPeriod, rate), 1, (int)bufferFrames);
            }

            var parts = new WasapiParts(client, render, capture, clock, bufferEvent, controlEvent);
            stream = new WasapiStream(parts, direction, engine, new AudioDeviceFormat(rate, channels), info, exclusive, kind, period, (int)bufferFrames, s_notifications);
            s_notifications.Add(direction, engine);
            handedOver = true;
            reason = string.Empty;
            return true;
        }
        finally
        {
            if (!handedOver)
            {
                WasapiNative.Release(clock);
                WasapiNative.Release(render);
                WasapiNative.Release(capture);
                WasapiNative.Release(client);
                WasapiNative.CloseEvent(bufferEvent);
                WasapiNative.CloseEvent(controlEvent);
            }

            WasapiNative.Release(device);
            WasapiNative.Release(enumerator);
        }
    }

    /// <summary>
    /// Exclusive negotiation and initialisation. A client whose Initialize failed is released and activated again before the next
    /// attempt, as the documentation requires after AUDCLNT_E_BUFFER_SIZE_NOT_ALIGNED.
    /// </summary>
    private static bool InitializeExclusive(nint device, ref nint client, DeviceStreamOptions options, out WasapiSampleKind kind, out string reason)
    {
        int rate = options.SampleRate;
        int channels = options.Channels;
        nint probe = client;
        bool found = WasapiLogic.NegotiateExclusive(
            k =>
            {
                var f = WasapiLogic.MakeFormat(k, rate, channels);
                return WasapiNative.IsFormatSupported(probe, 1, ref f) == 0;
            },
            out kind);
        if (!found)
        {
            reason = WasapiLogic.ExclusiveRefusal(rate, channels);
            return false;
        }

        long minimum = 0;
        _ = WasapiNative.GetDevicePeriod(client, out _, out minimum);
        long duration = WasapiLogic.ExclusiveDuration(options, minimum);
        var format = WasapiLogic.MakeFormat(kind, rate, channels);
        bool minimumTried = false;
        int hr = 0;
        for (int attempt = 0; attempt < 3; attempt++)
        {
            // Exclusive + event driven: buffer duration and periodicity are equal; AUTOCONVERTPCM is not allowed.
            hr = WasapiNative.Initialize(client, 1, WasapiLogic.StreamFlagEventCallback, duration, duration, ref format);
            if (hr >= 0)
            {
                reason = string.Empty;
                return true;
            }

            long next;
            if (hr == WasapiLogic.AudclntEBufferSizeNotAligned && WasapiNative.GetBufferSize(client, out uint aligned) >= 0 && aligned > 0)
            {
                next = WasapiLogic.AlignedDuration((int)aligned, rate);
            }
            else if (hr == WasapiLogic.AudclntEInvalidDevicePeriod && !minimumTried && minimum > 0)
            {
                minimumTried = true;
                next = minimum;
            }
            else
            {
                break;
            }

            WasapiNative.Release(client);
            client = 0;
            if (WasapiNative.ActivateAudioClient(device, out client) < 0)
            {
                break;
            }

            duration = next;
        }

        reason = WasapiLogic.Describe($"the device refused exclusive mode at {rate} Hz x {channels}", hr);
        return false;
    }
}
