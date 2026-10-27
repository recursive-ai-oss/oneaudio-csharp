using System.Diagnostics;
using System.Runtime.InteropServices;

namespace OneAudio.Devices.Interop;

/// <summary>
/// The AAudio calls the Android backend uses (Android NDK reference, "Audio" module; API level 26 or later). The library is loaded lazily, so a
/// system without it just has no devices. Calls added after level 26 (usage, content type, input preset, MMAP query) are looked up by name and
/// are optional.
/// </summary>
internal static unsafe partial class AAudioNative
{
    private const string Lib = "libaaudio.so";

    public const int Ok = 0;
    public const int ErrorDisconnected = -899;

    public const int DirectionOutput = 0;
    public const int DirectionInput = 1;
    public const int FormatPcmFloat = 2;
    public const int SharingShared = 1;
    public const int PerformanceNone = 10;
    public const int PerformanceLowLatency = 12;
    public const int UsageGame = 14;
    public const int ContentSonification = 4;
    public const int InputPresetGeneric = 1;

    public const int CallbackContinue = 0;
    public const int CallbackStop = 1;

    public const int StateStarting = 3;
    public const int StateStarted = 4;
    public const int StatePausing = 5;
    public const int StatePaused = 6;
    public const int StateStopping = 9;
    public const int StateStopped = 10;
    public const int StateDisconnected = 13;

    /// <summary>CLOCK_MONOTONIC, the clock <c>AAudioStream_getTimestamp</c> reports against.</summary>
    public const int ClockMonotonic = 1;

    private static nint s_lib;
    private static nint s_setUsage;
    private static nint s_setContentType;
    private static nint s_setInputPreset;
    private static nint s_isMmapUsed;

    /// <summary>Whether libaaudio can be loaded here. Checked once.</summary>
    public static bool IsPresent { get; } = Probe();

    private static bool Probe()
    {
        if (!NativeLibrary.TryLoad(Lib, out nint lib))
        {
            return false;
        }

        s_lib = lib; // kept for the life of the process: the optional entry points below point into it
        _ = NativeLibrary.TryGetExport(lib, "AAudioStreamBuilder_setUsage", out s_setUsage);
        _ = NativeLibrary.TryGetExport(lib, "AAudioStreamBuilder_setContentType", out s_setContentType);
        _ = NativeLibrary.TryGetExport(lib, "AAudioStreamBuilder_setInputPreset", out s_setInputPreset);
        _ = NativeLibrary.TryGetExport(lib, "AAudioStream_isMMapUsed", out s_isMmapUsed);
        return s_lib != 0;
    }

    /// <summary>Usage and content type (API level 28). Older systems keep their default, which is fine.</summary>
    public static void TrySetUsage(nint builder, int usage, int contentType)
    {
        if (s_setUsage != 0)
        {
            ((delegate* unmanaged<nint, int, void>)s_setUsage)(builder, usage);
        }

        if (s_setContentType != 0)
        {
            ((delegate* unmanaged<nint, int, void>)s_setContentType)(builder, contentType);
        }
    }

    /// <summary>The capture preset (API level 28).</summary>
    public static void TrySetInputPreset(nint builder, int preset)
    {
        if (s_setInputPreset != 0)
        {
            ((delegate* unmanaged<nint, int, void>)s_setInputPreset)(builder, preset);
        }
    }

    /// <summary>Whether the stream runs on the low-latency MMAP path; false when the system cannot say.</summary>
    public static bool IsMMapUsed(nint stream) =>
        s_isMmapUsed != 0 && ((delegate* unmanaged<nint, byte>)s_isMmapUsed)(stream) != 0;

    /// <summary>The monotonic clock in nanoseconds, the same clock as <c>CLOCK_MONOTONIC</c> on Linux and Android.</summary>
    public static long MonotonicNanoseconds()
    {
        long ticks = Stopwatch.GetTimestamp();
        return Stopwatch.Frequency == 1_000_000_000 ? ticks : (long)(ticks * (1_000_000_000.0 / Stopwatch.Frequency));
    }

    [LibraryImport(Lib, EntryPoint = "AAudio_createStreamBuilder")]
    public static partial int CreateStreamBuilder(out nint builder);

    [LibraryImport(Lib, EntryPoint = "AAudioStreamBuilder_delete")]
    public static partial int BuilderDelete(nint builder);

    [LibraryImport(Lib, EntryPoint = "AAudioStreamBuilder_setDeviceId")]
    public static partial void SetDeviceId(nint builder, int id);

    [LibraryImport(Lib, EntryPoint = "AAudioStreamBuilder_setDirection")]
    public static partial void SetDirection(nint builder, int direction);

    [LibraryImport(Lib, EntryPoint = "AAudioStreamBuilder_setFormat")]
    public static partial void SetFormat(nint builder, int format);

    [LibraryImport(Lib, EntryPoint = "AAudioStreamBuilder_setChannelCount")]
    public static partial void SetChannelCount(nint builder, int channels);

    [LibraryImport(Lib, EntryPoint = "AAudioStreamBuilder_setSampleRate")]
    public static partial void SetSampleRate(nint builder, int rate);

    [LibraryImport(Lib, EntryPoint = "AAudioStreamBuilder_setSharingMode")]
    public static partial void SetSharingMode(nint builder, int mode);

    [LibraryImport(Lib, EntryPoint = "AAudioStreamBuilder_setPerformanceMode")]
    public static partial void SetPerformanceMode(nint builder, int mode);

    [LibraryImport(Lib, EntryPoint = "AAudioStreamBuilder_setFramesPerDataCallback")]
    public static partial void SetFramesPerDataCallback(nint builder, int frames);

    [LibraryImport(Lib, EntryPoint = "AAudioStreamBuilder_setBufferCapacityInFrames")]
    public static partial void SetBufferCapacity(nint builder, int frames);

    [LibraryImport(Lib, EntryPoint = "AAudioStreamBuilder_setDataCallback")]
    private static partial void SetDataCallback(nint builder, nint callback, nint userData);

    [LibraryImport(Lib, EntryPoint = "AAudioStreamBuilder_setErrorCallback")]
    private static partial void SetErrorCallback(nint builder, nint callback, nint userData);

    [LibraryImport(Lib, EntryPoint = "AAudioStreamBuilder_openStream")]
    public static partial int OpenStream(nint builder, out nint stream);

    [LibraryImport(Lib, EntryPoint = "AAudioStream_close")]
    public static partial int Close(nint stream);

    [LibraryImport(Lib, EntryPoint = "AAudioStream_requestStart")]
    public static partial int RequestStart(nint stream);

    [LibraryImport(Lib, EntryPoint = "AAudioStream_requestPause")]
    public static partial int RequestPause(nint stream);

    [LibraryImport(Lib, EntryPoint = "AAudioStream_requestStop")]
    public static partial int RequestStop(nint stream);

    [LibraryImport(Lib, EntryPoint = "AAudioStream_getState")]
    public static partial int GetState(nint stream);

    [LibraryImport(Lib, EntryPoint = "AAudioStream_waitForStateChange")]
    public static partial int WaitForStateChange(nint stream, int inputState, out int nextState, long timeoutNanoseconds);

    [LibraryImport(Lib, EntryPoint = "AAudioStream_getSampleRate")]
    public static partial int GetSampleRate(nint stream);

    [LibraryImport(Lib, EntryPoint = "AAudioStream_getChannelCount")]
    public static partial int GetChannelCount(nint stream);

    [LibraryImport(Lib, EntryPoint = "AAudioStream_getDeviceId")]
    public static partial int GetDeviceId(nint stream);

    [LibraryImport(Lib, EntryPoint = "AAudioStream_getFramesPerBurst")]
    public static partial int GetFramesPerBurst(nint stream);

    [LibraryImport(Lib, EntryPoint = "AAudioStream_getBufferSizeInFrames")]
    public static partial int GetBufferSize(nint stream);

    [LibraryImport(Lib, EntryPoint = "AAudioStream_getBufferCapacityInFrames")]
    public static partial int GetBufferCapacity(nint stream);

    [LibraryImport(Lib, EntryPoint = "AAudioStream_setBufferSizeInFrames")]
    public static partial int SetBufferSize(nint stream, int frames);

    [LibraryImport(Lib, EntryPoint = "AAudioStream_getFramesWritten")]
    public static partial long GetFramesWritten(nint stream);

    [LibraryImport(Lib, EntryPoint = "AAudioStream_getFramesRead")]
    public static partial long GetFramesRead(nint stream);

    [LibraryImport(Lib, EntryPoint = "AAudioStream_getXRunCount")]
    public static partial int GetXRunCount(nint stream);

    [LibraryImport(Lib, EntryPoint = "AAudioStream_getTimestamp")]
    public static partial int GetTimestamp(nint stream, int clockId, out long framePosition, out long timeNanoseconds);

    /// <summary>Installs the data and error callbacks on a builder; <paramref name="client"/> receives them until its handle is freed.</summary>
    public static void SetCallbacks(nint builder, GCHandle client)
    {
        nint user = GCHandle.ToIntPtr(client);
        SetDataCallback(builder, (nint)(delegate* unmanaged<nint, nint, nint, int, int>)&OnData, user);
        SetErrorCallback(builder, (nint)(delegate* unmanaged<nint, nint, int, void>)&OnError, user);
    }

    // The system calls this on its own high-priority thread. It must not allocate, block or throw; the client is the stream, whose
    // handlers keep to that.
    [UnmanagedCallersOnly]
    private static int OnData(nint stream, nint user, nint audio, int frames)
    {
        var client = (IAAudioClient)GCHandle.FromIntPtr(user).Target!;
        if (client.IsClosing || frames <= 0)
        {
            return CallbackStop;
        }

        client.Enter();
        int samples = frames * client.Channels;
        if (client.IsOutput)
        {
            client.OnOutput(new Span<float>((void*)audio, samples));
        }
        else
        {
            client.OnInput(new ReadOnlySpan<float>((void*)audio, samples));
        }

        client.Leave();
        return CallbackContinue;
    }

    [UnmanagedCallersOnly]
    private static void OnError(nint stream, nint user, int error)
    {
        ((IAAudioClient)GCHandle.FromIntPtr(user).Target!).OnError(error);
    }
}

/// <summary>What the native callbacks talk to: the stream. Kept as an interface so the unsafe trampolines stay in this folder.</summary>
internal interface IAAudioClient
{
    int Channels { get; }

    bool IsOutput { get; }

    bool IsClosing { get; }

    /// <summary>Marks a data callback as running, so pausing and closing can wait for it.</summary>
    void Enter();

    void Leave();

    void OnOutput(Span<float> buffer);

    void OnInput(ReadOnlySpan<float> buffer);

    void OnError(int error);
}
