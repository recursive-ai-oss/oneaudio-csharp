using System.Runtime.InteropServices;

namespace OneAudio.Devices.Interop;

/// <summary>
/// The ALSA PCM calls the Linux backend uses (alsa-lib reference manual, PCM interface and hardware/software parameter containers).
/// The library is loaded lazily, so a machine without libasound just has no devices. Buffers go in as spans, which the generated
/// stubs pin; the only pointer work is installing the error handler.
/// </summary>
internal static unsafe partial class AlsaNative
{
    private const string Lib = "libasound.so.2";

    public const int StreamPlayback = 0;
    public const int StreamCapture = 1;
    public const int FormatS16Le = 2;
    public const int FormatFloatLe = 14;
    public const int AccessRwInterleaved = 3;

    public const int Eintr = 4;
    public const int Epipe = 32;
    public const int Estrpipe = 86;

    public const int StateXrun = 4;
    public const int StateDisconnected = 8;

    private static int s_quiet;

    /// <summary>Whether libasound can be loaded here. Checked once.</summary>
    public static bool IsPresent { get; } = NativeLibrary.TryLoad(Lib, out var handle) && Release(handle);

    private static bool Release(nint handle)
    {
        NativeLibrary.Free(handle);
        return true;
    }

    /// <summary>Installs a handler that does nothing, which silences alsa-lib's own stderr chatter; failures are reported through return codes.</summary>
    public static void Quiet()
    {
        if (Interlocked.Exchange(ref s_quiet, 1) == 0)
        {
            _ = SetErrorHandler((nint)(delegate* unmanaged[Cdecl]<byte*, int, byte*, int, byte*, void>)&Silence);
        }
    }

    // alsa-lib's handler takes (file, line, function, err, format, ...); a callee that ignores its arguments is safe for the variadic tail.
    [UnmanagedCallersOnly(CallConvs = [typeof(System.Runtime.CompilerServices.CallConvCdecl)])]
    private static void Silence(byte* file, int line, byte* function, int err, byte* format)
    {
    }

    [LibraryImport(Lib, EntryPoint = "snd_lib_error_set_handler")]
    private static partial int SetErrorHandler(nint handler);

    [LibraryImport(Lib, EntryPoint = "snd_pcm_open", StringMarshalling = StringMarshalling.Utf8)]
    public static partial int Open(out nint pcm, string name, int stream, int mode);

    [LibraryImport(Lib, EntryPoint = "snd_pcm_close")]
    public static partial int Close(nint pcm);

    // One call picks the hardware parameters; soft_resample lets the plug-in layer convert rate when the hardware can't.
    [LibraryImport(Lib, EntryPoint = "snd_pcm_set_params")]
    public static partial int SetParams(nint pcm, int format, int access, uint channels, uint rate, int softResample, uint latencyMicroseconds);

    [LibraryImport(Lib, EntryPoint = "snd_pcm_get_params")]
    public static partial int GetParams(nint pcm, out nuint bufferSize, out nuint periodSize);

    [LibraryImport(Lib, EntryPoint = "snd_pcm_writei")]
    public static partial nint WriteI(nint pcm, ReadOnlySpan<float> buffer, nuint frames);

    [LibraryImport(Lib, EntryPoint = "snd_pcm_writei")]
    public static partial nint WriteI(nint pcm, ReadOnlySpan<short> buffer, nuint frames);

    [LibraryImport(Lib, EntryPoint = "snd_pcm_readi")]
    public static partial nint ReadI(nint pcm, Span<float> buffer, nuint frames);

    [LibraryImport(Lib, EntryPoint = "snd_pcm_readi")]
    public static partial nint ReadI(nint pcm, Span<short> buffer, nuint frames);

    [LibraryImport(Lib, EntryPoint = "snd_pcm_recover")]
    public static partial int Recover(nint pcm, int error, int silent);

    [LibraryImport(Lib, EntryPoint = "snd_pcm_delay")]
    public static partial int Delay(nint pcm, out nint frames);

    [LibraryImport(Lib, EntryPoint = "snd_pcm_state")]
    public static partial int State(nint pcm);

    [LibraryImport(Lib, EntryPoint = "snd_pcm_prepare")]
    public static partial int Prepare(nint pcm);

    [LibraryImport(Lib, EntryPoint = "snd_pcm_start")]
    public static partial int Start(nint pcm);

    [LibraryImport(Lib, EntryPoint = "snd_pcm_drain")]
    public static partial int Drain(nint pcm);

    [LibraryImport(Lib, EntryPoint = "snd_pcm_drop")]
    public static partial int Drop(nint pcm);

    [LibraryImport(Lib, EntryPoint = "snd_pcm_hw_params_malloc")]
    public static partial int HwMalloc(out nint parameters);

    [LibraryImport(Lib, EntryPoint = "snd_pcm_hw_params_free")]
    public static partial void HwFree(nint parameters);

    [LibraryImport(Lib, EntryPoint = "snd_pcm_hw_params_any")]
    public static partial int HwAny(nint pcm, nint parameters);

    [LibraryImport(Lib, EntryPoint = "snd_pcm_hw_params_set_access")]
    public static partial int HwSetAccess(nint pcm, nint parameters, int access);

    [LibraryImport(Lib, EntryPoint = "snd_pcm_hw_params_set_format")]
    public static partial int HwSetFormat(nint pcm, nint parameters, int format);

    [LibraryImport(Lib, EntryPoint = "snd_pcm_hw_params_set_channels")]
    public static partial int HwSetChannels(nint pcm, nint parameters, uint channels);

    [LibraryImport(Lib, EntryPoint = "snd_pcm_hw_params_set_rate_resample")]
    public static partial int HwSetRateResample(nint pcm, nint parameters, uint enable);

    [LibraryImport(Lib, EntryPoint = "snd_pcm_hw_params_set_rate_near")]
    public static partial int HwSetRateNear(nint pcm, nint parameters, ref uint rate, ref int direction);

    [LibraryImport(Lib, EntryPoint = "snd_pcm_hw_params_set_buffer_size_near")]
    public static partial int HwSetBufferSizeNear(nint pcm, nint parameters, ref nuint frames);

    [LibraryImport(Lib, EntryPoint = "snd_pcm_hw_params_set_period_size_near")]
    public static partial int HwSetPeriodSizeNear(nint pcm, nint parameters, ref nuint frames, ref int direction);

    [LibraryImport(Lib, EntryPoint = "snd_pcm_hw_params")]
    public static partial int HwApply(nint pcm, nint parameters);

    [LibraryImport(Lib, EntryPoint = "snd_pcm_sw_params_malloc")]
    public static partial int SwMalloc(out nint parameters);

    [LibraryImport(Lib, EntryPoint = "snd_pcm_sw_params_free")]
    public static partial void SwFree(nint parameters);

    [LibraryImport(Lib, EntryPoint = "snd_pcm_sw_params_current")]
    public static partial int SwCurrent(nint pcm, nint parameters);

    [LibraryImport(Lib, EntryPoint = "snd_pcm_sw_params_set_start_threshold")]
    public static partial int SwSetStartThreshold(nint pcm, nint parameters, nuint frames);

    [LibraryImport(Lib, EntryPoint = "snd_pcm_sw_params")]
    public static partial int SwApply(nint pcm, nint parameters);

    [LibraryImport(Lib, EntryPoint = "snd_device_name_hint", StringMarshalling = StringMarshalling.Utf8)]
    public static partial int NameHint(int card, string iface, out nint hints);

    [LibraryImport(Lib, EntryPoint = "snd_device_name_get_hint", StringMarshalling = StringMarshalling.Utf8)]
    public static partial nint NameGetHint(nint hint, string id);

    [LibraryImport(Lib, EntryPoint = "snd_device_name_free_hint")]
    public static partial int NameFreeHint(nint hints);
}
