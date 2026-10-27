using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;

namespace OneAudio.Devices.Interop;

/// <summary>What the endpoint notification object tells the rest of the backend. Called on an operating-system thread; must not throw.</summary>
internal interface IEndpointEvents
{
    /// <summary>The default endpoint of one data-flow direction (0 render, 1 capture) changed for a role (0 console, 1 multimedia, 2 communications).</summary>
    void DefaultDeviceChanged(int flow, int role);

    /// <summary>An endpoint was added, removed or changed state.</summary>
    void DevicesChanged();
}

/// <summary>
/// A hand-built native IMMNotificationClient: an object whose first field points at a v-table of unmanaged function pointers. Windows calls it
/// when endpoints come and go or the default changes. The object is reference counted by whoever holds it; the last Release frees it.
/// </summary>
internal static unsafe class EndpointNotificationClient
{
    // IMMNotificationClient: IUnknown (0..2), OnDeviceStateChanged 3, OnDeviceAdded 4, OnDeviceRemoved 5, OnDefaultDeviceChanged 6, OnPropertyValueChanged 7.
    internal const int VtableSlots = 8;
    internal const int SlotOnDefaultDeviceChanged = 6;

    private static readonly Guid s_iidUnknown = new("00000000-0000-0000-C000-000000000046");
    private static readonly Guid s_iidClient = new("7991EEC9-7E89-4D85-8390-6C703CEC60C0");
    private static nint* s_table;

    [StructLayout(LayoutKind.Sequential)]
    private struct Instance
    {
        public nint* Table;
        public int References;
        public nint Handle;
    }

    /// <summary>Creates the object with one reference held by the caller.</summary>
    public static nint Create(IEndpointEvents events)
    {
        Instance* o = (Instance*)NativeMemory.Alloc((nuint)sizeof(Instance));
        o->Table = Table();
        o->References = 1;
        o->Handle = GCHandle.ToIntPtr(GCHandle.Alloc(events));
        return (nint)o;
    }

    private static nint* Table()
    {
        if (s_table is null)
        {
            // Built once and kept for the life of the process.
            nint* t = (nint*)NativeMemory.Alloc((nuint)(VtableSlots * sizeof(nint)));
            t[0] = (nint)(delegate* unmanaged[Stdcall]<Instance*, Guid*, nint*, int>)&QueryInterface;
            t[1] = (nint)(delegate* unmanaged[Stdcall]<Instance*, uint>)&AddRef;
            t[2] = (nint)(delegate* unmanaged[Stdcall]<Instance*, uint>)&ReleaseRef;
            t[3] = (nint)(delegate* unmanaged[Stdcall]<Instance*, char*, uint, int>)&OnDeviceStateChanged;
            t[4] = (nint)(delegate* unmanaged[Stdcall]<Instance*, char*, int>)&OnDeviceAddedOrRemoved;
            t[5] = (nint)(delegate* unmanaged[Stdcall]<Instance*, char*, int>)&OnDeviceAddedOrRemoved;
            t[6] = (nint)(delegate* unmanaged[Stdcall]<Instance*, int, int, char*, int>)&OnDefaultDeviceChanged;
            t[7] = (nint)(delegate* unmanaged[Stdcall]<Instance*, char*, nint, int>)&OnPropertyValueChanged;
            s_table = t;
        }

        return s_table;
    }

    [UnmanagedCallersOnly(CallConvs = [typeof(CallConvStdcall)])]
    private static int QueryInterface(Instance* self, Guid* iid, nint* result)
    {
        if (result is null)
        {
            return unchecked((int)0x80004003); // E_POINTER
        }

        if (*iid == s_iidUnknown || *iid == s_iidClient)
        {
            _ = Interlocked.Increment(ref self->References);
            *result = (nint)self;
            return 0;
        }

        *result = 0;
        return unchecked((int)0x80004002); // E_NOINTERFACE
    }

    [UnmanagedCallersOnly(CallConvs = [typeof(CallConvStdcall)])]
    private static uint AddRef(Instance* self) => (uint)Interlocked.Increment(ref self->References);

    [UnmanagedCallersOnly(CallConvs = [typeof(CallConvStdcall)])]
    private static uint ReleaseRef(Instance* self)
    {
        int left = Interlocked.Decrement(ref self->References);
        if (left == 0)
        {
            GCHandle.FromIntPtr(self->Handle).Free();
            NativeMemory.Free(self);
        }

        return (uint)left;
    }

    // An exception must never cross into native code, so the handlers are the one place that swallows everything: the receivers are
    // simple flag setters and a failure has nobody to be reported to.
    [UnmanagedCallersOnly(CallConvs = [typeof(CallConvStdcall)])]
    private static int OnDeviceStateChanged(Instance* self, char* id, uint state)
    {
        Raise(self, flow: -1, role: 0);
        return 0;
    }

    [UnmanagedCallersOnly(CallConvs = [typeof(CallConvStdcall)])]
    private static int OnDeviceAddedOrRemoved(Instance* self, char* id)
    {
        Raise(self, flow: -1, role: 0);
        return 0;
    }

    [UnmanagedCallersOnly(CallConvs = [typeof(CallConvStdcall)])]
    private static int OnDefaultDeviceChanged(Instance* self, int flow, int role, char* id)
    {
        Raise(self, flow, role);
        return 0;
    }

    [UnmanagedCallersOnly(CallConvs = [typeof(CallConvStdcall)])]
    private static int OnPropertyValueChanged(Instance* self, char* id, nint key) => 0;

    private static void Raise(Instance* self, int flow, int role)
    {
        try
        {
            var events = (IEndpointEvents?)GCHandle.FromIntPtr(self->Handle).Target;
            if (events is null)
            {
                return;
            }

            if (flow < 0)
            {
                events.DevicesChanged();
            }
            else
            {
                events.DefaultDeviceChanged(flow, role);
            }
        }
        catch (Exception)
        {
            // See above.
        }
    }
}
