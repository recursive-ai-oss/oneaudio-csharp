using System.Runtime.InteropServices;
using OneAudio.Devices.Interop;

namespace OneAudio.Devices;

/// <summary>
/// Android audio through AAudio (the NDK's native audio API, Android 8.0 / API level 26 or later). Streams are shared, 32-bit float, low-latency
/// and driven by AAudio's own data callback on a high-priority system thread, so the engine renders straight into the device buffer.
/// <para>
/// AAudio cannot enumerate devices, so <see cref="List"/> shows one entry, the system default (id <c>default</c>); a specific device is opened by
/// the decimal Android audio device id passed as <see cref="DeviceStreamOptions.DeviceId"/>. When the system changes the route of a default
/// stream (headphones pulled, a Bluetooth device connected) it disconnects the stream; the stream reports that as a lost device and the
/// engine opens a new one on the new route. The rate and channel count must be the ones asked for: a system that gives a different rate
/// fails the open with a reason. Exclusive mode does not exist here. Capture needs the app to hold the microphone permission; without it
/// the open fails with the system's code.
/// </para>
/// <para>
/// A NativeAOT app built for <c>linux-bionic</c> runs on the Linux runtime, so this backend is chosen on Linux too when the process is an
/// Android one (the <c>ANDROID_ROOT</c> variable every Android process has is set).
/// </para>
/// </summary>
public sealed class AAudioBackend : IAudioDeviceBackend
{
    internal const string DefaultId = "default";

    /// <inheritdoc/>
    public string Name => "aaudio";

    /// <inheritdoc/>
    public DeviceBackendKind Kind => DeviceBackendKind.AAudio;

    /// <inheritdoc/>
    public bool IsManual => false;

    /// <inheritdoc/>
    public bool NotifiesDeviceChanges => false;

    /// <inheritdoc/>
    public bool IsAvailable => IsAndroidProcess() && AAudioNative.IsPresent;

    /// <summary>True in an Android process, whether it runs on the Android runtime or as a NativeAOT program on the Linux runtime.</summary>
    internal static bool IsAndroidProcess() =>
        OperatingSystem.IsAndroid() || (OperatingSystem.IsLinux() && !string.IsNullOrEmpty(Environment.GetEnvironmentVariable("ANDROID_ROOT")));

    /// <inheritdoc/>
    public IReadOnlyList<AudioDeviceInfo> List(DeviceDirection direction) =>
        IsAvailable ? [DefaultInfo(direction)] : [];

    /// <inheritdoc/>
    public AudioDeviceInfo? GetDefault(DeviceDirection direction) => IsAvailable ? DefaultInfo(direction) : null;

    private static AudioDeviceInfo DefaultInfo(DeviceDirection direction) =>
        new(DefaultId, "System default", direction, true, 0, 0, 0, DeviceBackendKind.AAudio);

    /// <inheritdoc/>
    public bool TryOpen(DeviceDirection direction, DeviceStreamOptions options, IDeviceEngine engine, out IOpenDevice? stream, out string reason)
    {
        ArgumentNullException.ThrowIfNull(options);
        ArgumentNullException.ThrowIfNull(engine);
        stream = null;
        if (!IsAvailable)
        {
            reason = "AAudio is not available (Android 8.0 or later has it)";
            return false;
        }

        if (options.Mode == ShareMode.Exclusive)
        {
            reason = "AAudio streams here are shared; exclusive mode is not offered on Android";
            return false;
        }

        if (!AAudioLogic.TryParseDeviceId(options.DeviceId, out int deviceId))
        {
            reason = $"'{options.DeviceId}' is not an AAudio device id (use 'default' or the decimal Android audio device id)";
            return false;
        }

        try
        {
            return Open(direction, options, engine, deviceId, out stream, out reason);
        }
        catch (Exception e) when (e is DllNotFoundException or EntryPointNotFoundException)
        {
            reason = "AAudio is not available: " + e.Message;
            return false;
        }
    }

    private static bool Open(DeviceDirection direction, DeviceStreamOptions o, IDeviceEngine engine, int deviceId, out IOpenDevice? stream, out string reason)
    {
        stream = null;
        int result = AAudioNative.CreateStreamBuilder(out nint builder);
        if (result != AAudioNative.Ok || builder == 0)
        {
            reason = $"AAudio could not create a stream builder (code {result})";
            return false;
        }

        var client = new AAudioStream(direction, engine, new AudioDeviceFormat(o.SampleRate, o.Channels));
        var handle = GCHandle.Alloc(client);
        nint native = 0;
        try
        {
            bool output = direction == DeviceDirection.Output;
            AAudioNative.SetDirection(builder, output ? AAudioNative.DirectionOutput : AAudioNative.DirectionInput);
            AAudioNative.SetFormat(builder, AAudioNative.FormatPcmFloat);
            AAudioNative.SetChannelCount(builder, o.Channels);
            AAudioNative.SetSampleRate(builder, o.SampleRate);
            AAudioNative.SetSharingMode(builder, AAudioNative.SharingShared);
            AAudioNative.SetPerformanceMode(builder, AAudioNative.PerformanceLowLatency);
            if (deviceId != 0)
            {
                AAudioNative.SetDeviceId(builder, deviceId);
            }

            if (o.PeriodFrames > 0)
            {
                AAudioNative.SetFramesPerDataCallback(builder, o.PeriodFrames);
            }

            if (output)
            {
                // Game sound, so the system's focus and volume policy treats the app as a game.
                AAudioNative.TrySetUsage(builder, AAudioNative.UsageGame, AAudioNative.ContentSonification);
            }
            else
            {
                AAudioNative.TrySetInputPreset(builder, AAudioNative.InputPresetGeneric);
                int capacity = o.CaptureBufferMilliseconds * o.SampleRate / 1000;
                if (capacity > 0)
                {
                    AAudioNative.SetBufferCapacity(builder, capacity);
                }
            }

            AAudioNative.SetCallbacks(builder, handle);
            result = AAudioNative.OpenStream(builder, out native);
            if (result != AAudioNative.Ok || native == 0)
            {
                reason = $"AAudio could not open the {(output ? "output" : "input")} stream (code {result})";
                handle.Free();
                return false;
            }

            int rate = AAudioNative.GetSampleRate(native);
            int channels = AAudioNative.GetChannelCount(native);
            if (rate != o.SampleRate || channels != o.Channels)
            {
                reason = $"AAudio opened {rate} Hz x {channels} instead of the {o.SampleRate} Hz x {o.Channels} asked for";
                _ = AAudioNative.Close(native);
                handle.Free();
                return false;
            }

            int burst = Math.Max(AAudioNative.GetFramesPerBurst(native), 16);
            int capacityFrames = AAudioNative.GetBufferCapacity(native);
            int target = AAudioLogic.BufferTarget(burst, capacityFrames, o.BufferFrames, o.LatencyMilliseconds, rate);
            int actual = AAudioNative.SetBufferSize(native, target);
            int buffer = actual > 0 ? actual : AAudioNative.GetBufferSize(native);

            int actualId = AAudioNative.GetDeviceId(native);
            string detail = $"aaudio {(AAudioNative.IsMMapUsed(native) ? "mmap" : "legacy path")}, device {actualId}";
            var info = DefaultInfo(direction);
            if (deviceId != 0)
            {
                info = new AudioDeviceInfo(deviceId.ToString(System.Globalization.CultureInfo.InvariantCulture), "Android audio device " + deviceId, direction, false, 0, 0, 0, DeviceBackendKind.AAudio);
            }

            client.Attach(native, handle, info, burst, Math.Max(buffer, burst), detail);
            stream = client;
            reason = string.Empty;
            return true;
        }
        catch
        {
            // Not a catch-all in effect: it only releases what was opened and rethrows.
            if (native != 0)
            {
                _ = AAudioNative.Close(native);
            }

            if (handle.IsAllocated)
            {
                handle.Free();
            }

            throw;
        }
        finally
        {
            _ = AAudioNative.BuilderDelete(builder);
        }
    }
}
