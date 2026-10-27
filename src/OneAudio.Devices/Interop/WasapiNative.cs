using System.Runtime.InteropServices;

namespace OneAudio.Devices.Interop;

/// <summary>WAVEFORMATEXTENSIBLE (mmreg.h): 40 bytes, packed.</summary>
[StructLayout(LayoutKind.Sequential, Pack = 1)]
internal struct WaveFormatExtensible
{
    public ushort FormatTag;
    public ushort Channels;
    public uint SamplesPerSec;
    public uint AvgBytesPerSec;
    public ushort BlockAlign;
    public ushort BitsPerSample;
    public ushort Size;
    public ushort ValidBitsPerSample;
    public uint ChannelMask;
    public Guid SubFormat;
}

/// <summary>
/// The Windows calls the WASAPI backend needs: a handful of source-generated P/Invokes and thin wrappers over COM interfaces. A COM object is a
/// pointer to a pointer to a table of function pointers; slots 0..2 are IUnknown, the interface's own methods follow in declaration order. The
/// slot numbers are written once, in <see cref="Slot"/>, from the interface definitions in the Windows SDK headers (mmdeviceapi.h, audioclient.h).
/// Every method returns the raw HRESULT; nothing here throws on a failed call.
/// </summary>
internal static unsafe partial class WasapiNative
{
    /// <summary>V-table slot of each COM method used (SDK header order).</summary>
    internal static class Slot
    {
        public const int Release = 2;

        // IMMDeviceEnumerator
        public const int EnumAudioEndpoints = 3;
        public const int GetDefaultAudioEndpoint = 4;
        public const int GetDevice = 5;
        public const int RegisterEndpointNotificationCallback = 6;
        public const int UnregisterEndpointNotificationCallback = 7;

        // IMMDeviceCollection
        public const int CollectionGetCount = 3;
        public const int CollectionItem = 4;

        // IMMDevice
        public const int Activate = 3;
        public const int OpenPropertyStore = 4;
        public const int GetId = 5;

        // IPropertyStore
        public const int GetValue = 5;

        // IAudioClient
        public const int Initialize = 3;
        public const int GetBufferSize = 4;
        public const int GetCurrentPadding = 6;
        public const int IsFormatSupported = 7;
        public const int GetMixFormat = 8;
        public const int GetDevicePeriod = 9;
        public const int Start = 10;
        public const int Stop = 11;
        public const int Reset = 12;
        public const int SetEventHandle = 13;
        public const int GetService = 14;

        // IAudioRenderClient / IAudioCaptureClient
        public const int RenderGetBuffer = 3;
        public const int RenderReleaseBuffer = 4;
        public const int CaptureGetBuffer = 3;
        public const int CaptureReleaseBuffer = 4;
        public const int CaptureGetNextPacketSize = 5;

        // IAudioClock
        public const int ClockGetFrequency = 3;
        public const int ClockGetPosition = 4;
    }

    public const uint CoinitMultithreaded = 0;
    public const uint ClsctxAll = 23;
    public const int RpcEChangedMode = unchecked((int)0x80010106);

    private static readonly Guid s_clsidEnumerator = new("BCDE0395-E52F-467C-8E3D-C4579291692E");
    private static readonly Guid s_iidEnumerator = new("A95664D2-9614-4F35-A746-DE8DB63617E6");
    private static readonly Guid s_iidAudioClient = new("1CB9AD4C-DBFA-4c32-B178-C2F568A703B2");
    private static readonly Guid s_iidRenderClient = new("F294ACFC-3146-4483-A7BF-ADDCA7C260E2");
    private static readonly Guid s_iidCaptureClient = new("C8ADBD64-E71E-48a0-A4DE-185C395CD317");
    private static readonly Guid s_iidClock = new("CD63314F-3FBA-4a1b-812C-EF96358728E7");

    // PKEY_Device_FriendlyName {a45c254e-df1c-4efd-8020-67d146a850e0}, 14
    private static readonly Guid s_friendlyNameFormat = new("A45C254E-DF1C-4EFD-8020-67D146A850E0");

    [ThreadStatic]
    private static bool t_comReady;

    [LibraryImport("ole32.dll")]
    private static partial int CoInitializeEx(nint reserved, uint flags);

    [LibraryImport("ole32.dll")]
    private static partial void CoUninitialize();

    [LibraryImport("ole32.dll")]
    private static partial int CoCreateInstance(Guid* clsid, nint outer, uint context, Guid* iid, out nint instance);

    [LibraryImport("ole32.dll")]
    public static partial void CoTaskMemFree(nint pointer);

    [LibraryImport("ole32.dll")]
    private static partial int PropVariantClear(void* variant);

    [LibraryImport("kernel32.dll")]
    private static partial nint CreateEventW(nint attributes, int manualReset, int initialState, nint name);

    [LibraryImport("kernel32.dll")]
    private static partial int SetEvent(nint handle);

    [LibraryImport("kernel32.dll")]
    private static partial int CloseHandle(nint handle);

    [LibraryImport("kernel32.dll")]
    private static partial uint WaitForMultipleObjects(uint count, nint* handles, int waitAll, uint milliseconds);

    [DefaultDllImportSearchPaths(DllImportSearchPath.System32)]
    [LibraryImport("avrt.dll", StringMarshalling = StringMarshalling.Utf16)]
    private static partial nint AvSetMmThreadCharacteristicsW(string task, ref uint index);

    [DefaultDllImportSearchPaths(DllImportSearchPath.System32)]
    [LibraryImport("avrt.dll")]
    private static partial int AvRevertMmThreadCharacteristics(nint handle);

    // ------------------------------------------------------------------ threads, events, MMCSS

    /// <summary>
    /// Makes COM usable on the calling thread, once. A thread already in another apartment (RPC_E_CHANGED_MODE) stays usable. Returns true
    /// when this call initialised COM and the thread should balance it with <see cref="LeaveCom"/> before it ends.
    /// </summary>
    public static bool EnterCom()
    {
        if (t_comReady)
        {
            return false;
        }

        t_comReady = true;
        return CoInitializeEx(0, CoinitMultithreaded) >= 0;
    }

    /// <summary>Balances a successful <see cref="EnterCom"/> at the end of a thread that created it.</summary>
    public static void LeaveCom()
    {
        t_comReady = false;
        CoUninitialize();
    }

    /// <summary>Creates an unnamed auto-reset event, or 0.</summary>
    public static nint CreateAutoResetEvent() => CreateEventW(0, 0, 0, 0);

    public static void Signal(nint handle) => _ = SetEvent(handle);

    public static void CloseEvent(nint handle)
    {
        if (handle != 0)
        {
            _ = CloseHandle(handle);
        }
    }

    /// <summary>Waits for either event. Returns 0 or 1 for the event that fired, -1 on timeout, -2 on failure.</summary>
    public static int WaitAny(nint first, nint second, int milliseconds)
    {
        nint* handles = stackalloc nint[2];
        handles[0] = first;
        handles[1] = second;
        uint r = WaitForMultipleObjects(2, handles, 0, (uint)milliseconds);
        return r switch
        {
            0 => 0,
            1 => 1,
            0x102 => -1, // WAIT_TIMEOUT
            _ => -2,
        };
    }

    /// <summary>Registers the calling thread with the multimedia class scheduler as "Pro Audio". Returns a handle for <see cref="LeaveProAudio"/>, or 0.</summary>
    public static nint EnterProAudio()
    {
        uint index = 0;
        return AvSetMmThreadCharacteristicsW("Pro Audio", ref index);
    }

    public static void LeaveProAudio(nint handle)
    {
        if (handle != 0)
        {
            _ = AvRevertMmThreadCharacteristics(handle);
        }
    }

    // ------------------------------------------------------------------ raw memory

    /// <summary>Copies samples into a device buffer that GetBuffer returned.</summary>
    public static void CopyToNative<T>(nint destination, ReadOnlySpan<T> source) where T : unmanaged =>
        source.CopyTo(new Span<T>((void*)destination, source.Length));

    /// <summary>Copies samples out of a device buffer that GetBuffer returned.</summary>
    public static void CopyFromNative<T>(nint source, Span<T> destination) where T : unmanaged =>
        new ReadOnlySpan<T>((void*)source, destination.Length).CopyTo(destination);

    /// <summary>Zeroes bytes of a device buffer.</summary>
    public static void ClearNative(nint destination, int bytes) => new Span<byte>((void*)destination, bytes).Clear();

    // ------------------------------------------------------------------ COM helpers

    private static nint* Table(nint self) => *(nint**)self;

    /// <summary>IUnknown::Release.</summary>
    public static void Release(nint self)
    {
        if (self != 0)
        {
            _ = ((delegate* unmanaged[Stdcall]<nint, uint>)Table(self)[Slot.Release])(self);
        }
    }

    // ------------------------------------------------------------------ IMMDeviceEnumerator

    public static int CreateEnumerator(out nint enumerator)
    {
        Guid clsid = s_clsidEnumerator;
        Guid iid = s_iidEnumerator;
        return CoCreateInstance(&clsid, 0, ClsctxAll, &iid, out enumerator);
    }

    public static int EnumAudioEndpoints(nint e, int flow, uint stateMask, out nint collection)
    {
        nint c = 0;
        int hr = ((delegate* unmanaged[Stdcall]<nint, int, uint, nint*, int>)Table(e)[Slot.EnumAudioEndpoints])(e, flow, stateMask, &c);
        collection = c;
        return hr;
    }

    public static int GetDefaultAudioEndpoint(nint e, int flow, int role, out nint device)
    {
        nint d = 0;
        int hr = ((delegate* unmanaged[Stdcall]<nint, int, int, nint*, int>)Table(e)[Slot.GetDefaultAudioEndpoint])(e, flow, role, &d);
        device = d;
        return hr;
    }

    public static int GetDevice(nint e, string id, out nint device)
    {
        nint d = 0;
        int hr;
        fixed (char* p = id)
        {
            hr = ((delegate* unmanaged[Stdcall]<nint, char*, nint*, int>)Table(e)[Slot.GetDevice])(e, p, &d);
        }

        device = d;
        return hr;
    }

    public static int RegisterEndpointNotificationCallback(nint e, nint client) =>
        ((delegate* unmanaged[Stdcall]<nint, nint, int>)Table(e)[Slot.RegisterEndpointNotificationCallback])(e, client);

    public static int UnregisterEndpointNotificationCallback(nint e, nint client) =>
        ((delegate* unmanaged[Stdcall]<nint, nint, int>)Table(e)[Slot.UnregisterEndpointNotificationCallback])(e, client);

    // ------------------------------------------------------------------ IMMDeviceCollection, IMMDevice, IPropertyStore

    public static int CollectionGetCount(nint c, out uint count)
    {
        uint n = 0;
        int hr = ((delegate* unmanaged[Stdcall]<nint, uint*, int>)Table(c)[Slot.CollectionGetCount])(c, &n);
        count = n;
        return hr;
    }

    public static int CollectionItem(nint c, uint index, out nint device)
    {
        nint d = 0;
        int hr = ((delegate* unmanaged[Stdcall]<nint, uint, nint*, int>)Table(c)[Slot.CollectionItem])(c, index, &d);
        device = d;
        return hr;
    }

    /// <summary>IMMDevice::GetId as a managed string (the CoTaskMem copy is freed here).</summary>
    public static int GetId(nint device, out string? id)
    {
        nint p = 0;
        int hr = ((delegate* unmanaged[Stdcall]<nint, nint*, int>)Table(device)[Slot.GetId])(device, &p);
        id = null;
        if (hr >= 0 && p != 0)
        {
            id = Marshal.PtrToStringUni(p);
            CoTaskMemFree(p);
        }

        return hr;
    }

    /// <summary>The device's friendly name (PKEY_Device_FriendlyName), or null.</summary>
    public static string? GetFriendlyName(nint device)
    {
        nint store = 0;
        if (((delegate* unmanaged[Stdcall]<nint, uint, nint*, int>)Table(device)[Slot.OpenPropertyStore])(device, 0, &store) < 0 || store == 0)
        {
            return null;
        }

        try
        {
            // PROPERTYKEY: a GUID and a 32-bit property id. PROPVARIANT: 16 bytes on x86, 24 on x64; the type is the first ushort
            // and the pointer value of a VT_LPWSTR (31) sits at offset 8 in both.
            byte* key = stackalloc byte[20];
            Guid format = s_friendlyNameFormat;
            *(Guid*)key = format;
            *(uint*)(key + 16) = 14;
            byte* variant = stackalloc byte[32];
            new Span<byte>(variant, 32).Clear();
            if (((delegate* unmanaged[Stdcall]<nint, byte*, byte*, int>)Table(store)[Slot.GetValue])(store, key, variant) < 0)
            {
                return null;
            }

            try
            {
                return *(ushort*)variant == 31 ? Marshal.PtrToStringUni(*(nint*)(variant + 8)) : null;
            }
            finally
            {
                _ = PropVariantClear(variant);
            }
        }
        finally
        {
            Release(store);
        }
    }

    // ------------------------------------------------------------------ IAudioClient

    public static int ActivateAudioClient(nint device, out nint client)
    {
        Guid iid = s_iidAudioClient;
        nint c = 0;
        int hr = ((delegate* unmanaged[Stdcall]<nint, Guid*, uint, nint, nint*, int>)Table(device)[Slot.Activate])(device, &iid, ClsctxAll, 0, &c);
        client = c;
        return hr;
    }

    public static int Initialize(nint client, int shareMode, uint flags, long bufferDuration, long periodicity, ref WaveFormatExtensible format)
    {
        fixed (WaveFormatExtensible* f = &format)
        {
            return ((delegate* unmanaged[Stdcall]<nint, int, uint, long, long, WaveFormatExtensible*, nint, int>)Table(client)[Slot.Initialize])(
                client, shareMode, flags, bufferDuration, periodicity, f, 0);
        }
    }

    public static int GetBufferSize(nint client, out uint frames)
    {
        uint n = 0;
        int hr = ((delegate* unmanaged[Stdcall]<nint, uint*, int>)Table(client)[Slot.GetBufferSize])(client, &n);
        frames = n;
        return hr;
    }

    public static int GetCurrentPadding(nint client, out uint frames)
    {
        uint n = 0;
        int hr = ((delegate* unmanaged[Stdcall]<nint, uint*, int>)Table(client)[Slot.GetCurrentPadding])(client, &n);
        frames = n;
        return hr;
    }

    /// <summary>IAudioClient::IsFormatSupported without a closest-match request (exclusive mode never returns one). S_OK means supported.</summary>
    public static int IsFormatSupported(nint client, int shareMode, ref WaveFormatExtensible format)
    {
        fixed (WaveFormatExtensible* f = &format)
        {
            return ((delegate* unmanaged[Stdcall]<nint, int, WaveFormatExtensible*, nint, int>)Table(client)[Slot.IsFormatSupported])(client, shareMode, f, 0);
        }
    }

    /// <summary>IAudioClient::GetMixFormat copied into a 40-byte struct (a plain WAVEFORMATEX leaves the extension zero).</summary>
    public static int GetMixFormat(nint client, out WaveFormatExtensible format)
    {
        nint p = 0;
        format = default;
        int hr = ((delegate* unmanaged[Stdcall]<nint, nint*, int>)Table(client)[Slot.GetMixFormat])(client, &p);
        if (hr >= 0 && p != 0)
        {
            int size = 18 + *(ushort*)((byte*)p + 16);
            WaveFormatExtensible copy = default;
            new ReadOnlySpan<byte>((void*)p, Math.Min(size, sizeof(WaveFormatExtensible))).CopyTo(new Span<byte>(&copy, sizeof(WaveFormatExtensible)));
            format = copy;
            CoTaskMemFree(p);
        }

        return hr;
    }

    /// <summary>Default and minimum device periods in 100-nanosecond units.</summary>
    public static int GetDevicePeriod(nint client, out long defaultPeriod, out long minimumPeriod)
    {
        long d = 0;
        long m = 0;
        int hr = ((delegate* unmanaged[Stdcall]<nint, long*, long*, int>)Table(client)[Slot.GetDevicePeriod])(client, &d, &m);
        defaultPeriod = d;
        minimumPeriod = m;
        return hr;
    }

    public static int Start(nint client) => ((delegate* unmanaged[Stdcall]<nint, int>)Table(client)[Slot.Start])(client);

    public static int Stop(nint client) => ((delegate* unmanaged[Stdcall]<nint, int>)Table(client)[Slot.Stop])(client);

    public static int SetEventHandle(nint client, nint handle) =>
        ((delegate* unmanaged[Stdcall]<nint, nint, int>)Table(client)[Slot.SetEventHandle])(client, handle);

    private static int GetService(nint client, Guid iid, out nint service)
    {
        nint s = 0;
        int hr = ((delegate* unmanaged[Stdcall]<nint, Guid*, nint*, int>)Table(client)[Slot.GetService])(client, &iid, &s);
        service = s;
        return hr;
    }

    public static int GetRenderClient(nint client, out nint service) => GetService(client, s_iidRenderClient, out service);

    public static int GetCaptureClient(nint client, out nint service) => GetService(client, s_iidCaptureClient, out service);

    public static int GetClock(nint client, out nint service) => GetService(client, s_iidClock, out service);

    // ------------------------------------------------------------------ IAudioRenderClient, IAudioCaptureClient, IAudioClock

    public static int RenderGetBuffer(nint render, uint frames, out nint data)
    {
        byte* d = null;
        int hr = ((delegate* unmanaged[Stdcall]<nint, uint, byte**, int>)Table(render)[Slot.RenderGetBuffer])(render, frames, &d);
        data = (nint)d;
        return hr;
    }

    public static int RenderReleaseBuffer(nint render, uint frames, uint flags) =>
        ((delegate* unmanaged[Stdcall]<nint, uint, uint, int>)Table(render)[Slot.RenderReleaseBuffer])(render, frames, flags);

    public static int CaptureGetBuffer(nint capture, out nint data, out uint frames, out uint flags)
    {
        byte* d = null;
        uint n = 0;
        uint f = 0;
        ulong devicePosition = 0;
        ulong counter = 0;
        int hr = ((delegate* unmanaged[Stdcall]<nint, byte**, uint*, uint*, ulong*, ulong*, int>)Table(capture)[Slot.CaptureGetBuffer])(
            capture, &d, &n, &f, &devicePosition, &counter);
        data = (nint)d;
        frames = n;
        flags = f;
        return hr;
    }

    public static int CaptureReleaseBuffer(nint capture, uint frames) =>
        ((delegate* unmanaged[Stdcall]<nint, uint, int>)Table(capture)[Slot.CaptureReleaseBuffer])(capture, frames);

    public static int CaptureGetNextPacketSize(nint capture, out uint frames)
    {
        uint n = 0;
        int hr = ((delegate* unmanaged[Stdcall]<nint, uint*, int>)Table(capture)[Slot.CaptureGetNextPacketSize])(capture, &n);
        frames = n;
        return hr;
    }

    public static int ClockGetFrequency(nint clock, out ulong frequency)
    {
        ulong f = 0;
        int hr = ((delegate* unmanaged[Stdcall]<nint, ulong*, int>)Table(clock)[Slot.ClockGetFrequency])(clock, &f);
        frequency = f;
        return hr;
    }

    public static int ClockGetPosition(nint clock, out ulong position)
    {
        ulong p = 0;
        ulong counter = 0;
        int hr = ((delegate* unmanaged[Stdcall]<nint, ulong*, ulong*, int>)Table(clock)[Slot.ClockGetPosition])(clock, &p, &counter);
        position = p;
        return hr;
    }
}
