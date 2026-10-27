using System.Runtime.InteropServices;

namespace OneAudio.Devices.Interop;

/// <summary>A Core Audio hardware device as the enumeration sees it.</summary>
internal readonly record struct HalDevice(uint Object, string Uid, string Name, int InputChannels, int OutputChannels, double Rate, int MinFrames);

/// <summary>A registered Core Audio property listener; disposing removes it.</summary>
internal sealed unsafe class HalListener : IDisposable
{
    private readonly uint _object;
    private readonly AppleNative.ObjectPropertyAddress _address;
    private readonly Action _action;
    private GCHandle _self;

    private HalListener(uint obj, AppleNative.ObjectPropertyAddress address, Action action)
    {
        _object = obj;
        _address = address;
        _action = action;
    }

    /// <summary>Calls <paramref name="action"/> on a Core Audio thread whenever the property changes. The action must not throw or block.</summary>
    public static HalListener? Add(uint obj, uint selector, uint scope, Action action)
    {
        var listener = new HalListener(obj, new AppleNative.ObjectPropertyAddress { Selector = selector, Scope = scope, Element = AppleNative.ElementMain }, action);
        listener._self = GCHandle.Alloc(listener);
        var address = listener._address;
        int status = AppleNative.ObjectAddPropertyListener(obj, &address, (nint)(delegate* unmanaged<uint, uint, AppleNative.ObjectPropertyAddress*, nint, int>)&Proc, GCHandle.ToIntPtr(listener._self));
        if (status != 0)
        {
            listener._self.Free();
            return null;
        }

        return listener;
    }

    [UnmanagedCallersOnly]
    private static int Proc(uint obj, uint count, AppleNative.ObjectPropertyAddress* addresses, nint clientData)
    {
        (GCHandle.FromIntPtr(clientData).Target as HalListener)?._action();
        return 0;
    }

    public void Dispose()
    {
        if (_self.IsAllocated)
        {
            var address = _address;
            _ = AppleNative.ObjectRemovePropertyListener(_object, &address, (nint)(delegate* unmanaged<uint, uint, AppleNative.ObjectPropertyAddress*, nint, int>)&Proc, GCHandle.ToIntPtr(_self));
            _self.Free();
        }
    }
}

/// <summary>Core Audio hardware-object queries for macOS: device list, names, channel counts, default devices, buffer sizes and liveness.</summary>
internal static unsafe class Hal
{
    private static int s_runLoopSet;

    private static AppleNative.ObjectPropertyAddress Address(uint selector, uint scope) =>
        new() { Selector = selector, Scope = scope, Element = AppleNative.ElementMain };

    /// <summary>
    /// Tells the hardware layer to run its own notification thread instead of waiting for a run loop this process does not have. Without it
    /// property listeners may never fire in a console program. Done once.
    /// </summary>
    public static void EnsureOwnThread()
    {
        if (Interlocked.Exchange(ref s_runLoopSet, 1) != 0)
        {
            return;
        }

        var address = Address(AppleNative.HardwareRunLoop, AppleNative.ScopeGlobal);
        nint none = 0;
        _ = AppleNative.ObjectSetPropertyData(AppleNative.SystemObject, &address, 0, null, (uint)sizeof(nint), &none);
    }

    /// <summary>All devices, with their channel counts per direction.</summary>
    public static HalDevice[] Devices()
    {
        EnsureOwnThread();
        var address = Address(AppleNative.HardwareDevices, AppleNative.ScopeGlobal);
        uint size;
        if (AppleNative.ObjectGetPropertyDataSize(AppleNative.SystemObject, &address, 0, null, &size) != 0 || size < sizeof(uint))
        {
            return [];
        }

        var ids = new uint[size / sizeof(uint)];
        fixed (uint* p = ids)
        {
            if (AppleNative.ObjectGetPropertyData(AppleNative.SystemObject, &address, 0, null, &size, p) != 0)
            {
                return [];
            }
        }

        var result = new List<HalDevice>(ids.Length);
        for (int i = 0; i < size / sizeof(uint); i++)
        {
            if (Describe(ids[i]) is { } device)
            {
                result.Add(device);
            }
        }

        return [.. result];
    }

    /// <summary>Reads one device's identity and format, or null when it has no identifier (it vanished).</summary>
    public static HalDevice? Describe(uint id)
    {
        string? uid = String(id, AppleNative.DeviceUid);
        return uid is null ? null : new HalDevice(id, uid, String(id, AppleNative.ObjectName) ?? uid, Channels(id, false), Channels(id, true), NominalRate(id), MinFrames(id));
    }

    /// <summary>The system default device of a direction, or 0.</summary>
    public static uint DefaultDevice(bool output)
    {
        var address = Address(output ? AppleNative.HardwareDefaultOutput : AppleNative.HardwareDefaultInput, AppleNative.ScopeGlobal);
        uint device = 0;
        uint size = sizeof(uint);
        return AppleNative.ObjectGetPropertyData(AppleNative.SystemObject, &address, 0, null, &size, &device) == 0 && device != 0 ? device : 0;
    }

    /// <summary>Makes a device the system default of a direction. Used by the tests that exercise default-device following.</summary>
    public static bool SetDefaultDevice(bool output, uint device)
    {
        var address = Address(output ? AppleNative.HardwareDefaultOutput : AppleNative.HardwareDefaultInput, AppleNative.ScopeGlobal);
        return AppleNative.ObjectSetPropertyData(AppleNative.SystemObject, &address, 0, null, sizeof(uint), &device) == 0;
    }

    /// <summary>Listens for the system default of a direction changing.</summary>
    public static HalListener? ListenDefault(bool output, Action action) =>
        HalListener.Add(AppleNative.SystemObject, output ? AppleNative.HardwareDefaultOutput : AppleNative.HardwareDefaultInput, AppleNative.ScopeGlobal, action);

    /// <summary>Listens for devices appearing or disappearing.</summary>
    public static HalListener? ListenDevices(Action action) =>
        HalListener.Add(AppleNative.SystemObject, AppleNative.HardwareDevices, AppleNative.ScopeGlobal, action);

    /// <summary>Listens for a device's liveness changing (it is unplugged or comes back).</summary>
    public static HalListener? ListenAlive(uint device, Action action) =>
        HalListener.Add(device, AppleNative.DeviceIsAlive, AppleNative.ScopeGlobal, action);

    /// <summary>Whether the device is still present.</summary>
    public static bool IsAlive(uint device)
    {
        var address = Address(AppleNative.DeviceIsAlive, AppleNative.ScopeGlobal);
        uint alive = 0;
        uint size = sizeof(uint);
        return AppleNative.ObjectGetPropertyData(device, &address, 0, null, &size, &alive) == 0 && alive != 0;
    }

    /// <summary>Total channels across the device's streams in one direction.</summary>
    public static int Channels(uint device, bool output)
    {
        var address = Address(AppleNative.DeviceStreamConfiguration, output ? AppleNative.ScopeOutput : AppleNative.ScopeInput);
        uint size;
        if (AppleNative.ObjectGetPropertyDataSize(device, &address, 0, null, &size) != 0 || size < 8)
        {
            return 0;
        }

        byte* list = (byte*)NativeMemory.Alloc(size);
        try
        {
            if (AppleNative.ObjectGetPropertyData(device, &address, 0, null, &size, list) != 0 || size < 8)
            {
                return 0;
            }

            // AudioBufferList: a count, four bytes of padding, then 16-byte buffers whose first field is the channel count.
            uint count = *(uint*)list;
            int total = 0;
            for (uint i = 0; i < count && 8 + ((i + 1) * 16) <= size; i++)
            {
                total += (int)*(uint*)(list + 8 + (i * 16));
            }

            return total;
        }
        finally
        {
            NativeMemory.Free(list);
        }
    }

    /// <summary>The device's current nominal sample rate, or 0.</summary>
    public static double NominalRate(uint device)
    {
        var address = Address(AppleNative.DeviceNominalRate, AppleNative.ScopeGlobal);
        double rate = 0;
        uint size = sizeof(double);
        return AppleNative.ObjectGetPropertyData(device, &address, 0, null, &size, &rate) == 0 ? rate : 0;
    }

    /// <summary>The device's current I/O buffer size in frames, or 0.</summary>
    public static int FrameSize(uint device)
    {
        var address = Address(AppleNative.DeviceFrameSize, AppleNative.ScopeGlobal);
        uint frames = 0;
        uint size = sizeof(uint);
        return AppleNative.ObjectGetPropertyData(device, &address, 0, null, &size, &frames) == 0 ? (int)frames : 0;
    }

    /// <summary>The smallest I/O buffer size the device accepts, or 0.</summary>
    public static int MinFrames(uint device)
    {
        var address = Address(AppleNative.DeviceFrameSizeRange, AppleNative.ScopeGlobal);
        double* range = stackalloc double[2];
        uint size = 2 * sizeof(double);
        return AppleNative.ObjectGetPropertyData(device, &address, 0, null, &size, range) == 0 ? (int)range[0] : 0;
    }

    /// <summary>Asks the device for an I/O buffer size, clamped to what it accepts. This is a device-wide setting that other programs also see.</summary>
    public static void SetFrameSize(uint device, int frames)
    {
        var address = Address(AppleNative.DeviceFrameSizeRange, AppleNative.ScopeGlobal);
        double* range = stackalloc double[2];
        uint size = 2 * sizeof(double);
        if (AppleNative.ObjectGetPropertyData(device, &address, 0, null, &size, range) == 0)
        {
            frames = (int)Math.Clamp(frames, range[0], range[1]);
        }

        uint value = (uint)frames;
        var set = Address(AppleNative.DeviceFrameSize, AppleNative.ScopeGlobal);
        _ = AppleNative.ObjectSetPropertyData(device, &set, 0, null, sizeof(uint), &value);
    }

    /// <summary>The device's safety offset in frames for one direction, or 0.</summary>
    public static int SafetyOffset(uint device, bool output)
    {
        var address = Address(AppleNative.DeviceSafetyOffset, output ? AppleNative.ScopeOutput : AppleNative.ScopeInput);
        uint frames = 0;
        uint size = sizeof(uint);
        return AppleNative.ObjectGetPropertyData(device, &address, 0, null, &size, &frames) == 0 ? (int)frames : 0;
    }

    private static string? String(uint obj, uint selector)
    {
        var address = Address(selector, AppleNative.ScopeGlobal);
        nint text = 0;
        uint size = (uint)sizeof(nint);
        return AppleNative.ObjectGetPropertyData(obj, &address, 0, null, &size, &text) == 0 ? AppleNative.TakeString(text) : null;
    }
}
