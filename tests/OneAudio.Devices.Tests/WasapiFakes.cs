using System.Runtime.InteropServices;
using OneAudio.Devices.Interop;

namespace OneAudio.Devices.Tests;

/// <summary>
/// A COM object built by hand in native memory: a pointer to a v-table whose slots are unmanaged function pointers, followed by a handle to a
/// managed state object. The v-table is filled with a function that fails for every slot except the ones a test installs at the slot numbers
/// the SDK headers document, so a wrapper that calls the wrong slot gets an error instead of passing by accident.
/// </summary>
internal sealed unsafe class FakeCom : IDisposable
{
    public const int Wrong = unchecked((int)0x8000FFFF);
    private const int Slots = 32;
    private readonly nint* _table;
    private readonly nint* _object;
    private GCHandle _handle;

    public FakeCom(FakeState state, params (int Slot, nint Function)[] functions)
    {
        _table = (nint*)NativeMemory.Alloc(Slots * (nuint)sizeof(nint));
        for (int i = 0; i < Slots; i++)
        {
            _table[i] = (nint)(delegate* unmanaged<nint, int>)&WrongSlot;
        }

        _table[2] = (nint)(delegate* unmanaged<nint, uint>)&ReleaseFn;
        foreach (var (slot, function) in functions)
        {
            _table[slot] = function;
        }

        _handle = GCHandle.Alloc(state);
        _object = (nint*)NativeMemory.Alloc((nuint)(2 * sizeof(nint)));
        _object[0] = (nint)_table;
        _object[1] = GCHandle.ToIntPtr(_handle);
    }

    public nint Pointer => (nint)_object;

    public static T State<T>(nint self) where T : FakeState => (T)GCHandle.FromIntPtr(((nint*)self)[1]).Target!;

    public void Dispose()
    {
        _handle.Free();
        NativeMemory.Free(_object);
        NativeMemory.Free(_table);
    }

    [UnmanagedCallersOnly]
    private static int WrongSlot(nint self) => Wrong;

    [UnmanagedCallersOnly]
    private static uint ReleaseFn(nint self)
    {
        return (uint)Interlocked.Increment(ref State<FakeState>(self).Releases);
    }
}

internal class FakeState
{
    public int Releases;
}

internal sealed class ClientState : FakeState
{
    public uint Buffer = 480;
    public uint Padding;
    public int PaddingHr;
    public int Starts;
    public int Stops;
    public int StartHr;
    public int InitShare = -1;
    public uint InitFlags;
    public long InitDuration;
    public long InitPeriod;
    public WaveFormatExtensible InitFormat;
    public nint EventHandle;
    public Guid ServiceRequested;
    public nint ServicePointer;
    public int SupportedBits = 16;
    public long DefaultPeriod = 100_000;
    public long MinimumPeriod = 30_000;
}

internal sealed class RenderState : FakeState
{
    public nint Buffer;
    public int GetBufferHr;
    public uint RequestedFrames;
    public uint ReleasedFrames;
    public uint ReleasedFlags;
    public int Releases2;
}

internal sealed class CaptureState : FakeState
{
    public readonly Queue<(byte[] Data, uint Frames, uint Flags)> Packets = new();
    public nint Current;
    public int ReleasedFrames;
    public int GetBufferHr;
}

internal sealed class ClockState : FakeState
{
    public ulong Frequency = 10_000_000;
    public ulong Position;
}

internal sealed class EnumeratorState : FakeState
{
    public int Flow = -1;
    public int Role = -1;
    public uint StateMask;
    public nint RegisteredClient;
    public nint UnregisteredClient;
    public string? RequestedId;
    public nint Collection = 0x1000;
    public nint Device = 0x2000;
}

/// <summary>The functions behind the fakes. Slot numbers are written out here from the SDK headers, independently of the code under test.</summary>
internal static unsafe class FakeFunctions
{
    // IAudioClient
    [UnmanagedCallersOnly]
    public static int Initialize(nint self, int share, uint flags, long duration, long period, WaveFormatExtensible* format, nint session)
    {
        var s = FakeCom.State<ClientState>(self);
        s.InitShare = share;
        s.InitFlags = flags;
        s.InitDuration = duration;
        s.InitPeriod = period;
        s.InitFormat = *format;
        return 0;
    }

    [UnmanagedCallersOnly]
    public static int GetBufferSize(nint self, uint* frames)
    {
        *frames = FakeCom.State<ClientState>(self).Buffer;
        return 0;
    }

    [UnmanagedCallersOnly]
    public static int GetCurrentPadding(nint self, uint* frames)
    {
        var s = FakeCom.State<ClientState>(self);
        *frames = s.Padding;
        return s.PaddingHr;
    }

    [UnmanagedCallersOnly]
    public static int IsFormatSupported(nint self, int share, WaveFormatExtensible* format, nint closest)
    {
        var s = FakeCom.State<ClientState>(self);
        return share == 1 && closest == 0 && format->FormatTag == 0xFFFE && format->BitsPerSample == s.SupportedBits ? 0 : unchecked((int)0x88890008);
    }

    [UnmanagedCallersOnly]
    public static int GetDevicePeriod(nint self, long* defaultPeriod, long* minimumPeriod)
    {
        var s = FakeCom.State<ClientState>(self);
        *defaultPeriod = s.DefaultPeriod;
        *minimumPeriod = s.MinimumPeriod;
        return 0;
    }

    [UnmanagedCallersOnly]
    public static int Start(nint self)
    {
        var s = FakeCom.State<ClientState>(self);
        s.Starts++;
        return s.StartHr;
    }

    [UnmanagedCallersOnly]
    public static int Stop(nint self)
    {
        FakeCom.State<ClientState>(self).Stops++;
        return 0;
    }

    [UnmanagedCallersOnly]
    public static int SetEventHandle(nint self, nint handle)
    {
        FakeCom.State<ClientState>(self).EventHandle = handle;
        return 0;
    }

    [UnmanagedCallersOnly]
    public static int GetService(nint self, Guid* iid, nint* service)
    {
        var s = FakeCom.State<ClientState>(self);
        s.ServiceRequested = *iid;
        *service = s.ServicePointer;
        return 0;
    }

    // IAudioRenderClient
    [UnmanagedCallersOnly]
    public static int RenderGetBuffer(nint self, uint frames, byte** data)
    {
        var s = FakeCom.State<RenderState>(self);
        s.RequestedFrames = frames;
        if (s.GetBufferHr < 0)
        {
            return s.GetBufferHr;
        }

        *data = (byte*)s.Buffer;
        return 0;
    }

    [UnmanagedCallersOnly]
    public static int RenderReleaseBuffer(nint self, uint frames, uint flags)
    {
        var s = FakeCom.State<RenderState>(self);
        s.ReleasedFrames = frames;
        s.ReleasedFlags = flags;
        s.Releases2++;
        return 0;
    }

    // IAudioCaptureClient
    [UnmanagedCallersOnly]
    public static int CaptureGetBuffer(nint self, byte** data, uint* frames, uint* flags, ulong* devicePosition, ulong* counter)
    {
        var s = FakeCom.State<CaptureState>(self);
        if (s.GetBufferHr != 0)
        {
            return s.GetBufferHr;
        }

        var packet = s.Packets.Peek();
        *data = (byte*)s.Current;
        *frames = packet.Frames;
        *flags = packet.Flags;
        *devicePosition = 77;
        *counter = 88;
        return 0;
    }

    [UnmanagedCallersOnly]
    public static int CaptureReleaseBuffer(nint self, uint frames)
    {
        var s = FakeCom.State<CaptureState>(self);
        s.ReleasedFrames += (int)frames;
        _ = s.Packets.Dequeue();
        return 0;
    }

    [UnmanagedCallersOnly]
    public static int CaptureGetNextPacketSize(nint self, uint* frames)
    {
        var s = FakeCom.State<CaptureState>(self);
        *frames = s.Packets.Count == 0 ? 0 : s.Packets.Peek().Frames;
        return 0;
    }

    // IAudioClock
    [UnmanagedCallersOnly]
    public static int ClockGetFrequency(nint self, ulong* frequency)
    {
        *frequency = FakeCom.State<ClockState>(self).Frequency;
        return 0;
    }

    [UnmanagedCallersOnly]
    public static int ClockGetPosition(nint self, ulong* position, ulong* counter)
    {
        *position = FakeCom.State<ClockState>(self).Position;
        *counter = 5;
        return 0;
    }

    // IMMDeviceEnumerator, IMMDeviceCollection, IMMDevice
    [UnmanagedCallersOnly]
    public static int EnumAudioEndpoints(nint self, int flow, uint mask, nint* collection)
    {
        var s = FakeCom.State<EnumeratorState>(self);
        s.Flow = flow;
        s.StateMask = mask;
        *collection = s.Collection;
        return 0;
    }

    [UnmanagedCallersOnly]
    public static int GetDefaultAudioEndpoint(nint self, int flow, int role, nint* device)
    {
        var s = FakeCom.State<EnumeratorState>(self);
        s.Flow = flow;
        s.Role = role;
        *device = s.Device;
        return 0;
    }

    [UnmanagedCallersOnly]
    public static int GetDevice(nint self, char* id, nint* device)
    {
        var s = FakeCom.State<EnumeratorState>(self);
        s.RequestedId = new string(id);
        *device = s.Device;
        return 0;
    }

    [UnmanagedCallersOnly]
    public static int Register(nint self, nint client)
    {
        FakeCom.State<EnumeratorState>(self).RegisteredClient = client;
        return 0;
    }

    [UnmanagedCallersOnly]
    public static int Unregister(nint self, nint client)
    {
        FakeCom.State<EnumeratorState>(self).UnregisteredClient = client;
        return 0;
    }

    [UnmanagedCallersOnly]
    public static int CollectionGetCount(nint self, uint* count)
    {
        *count = 3;
        return 0;
    }

    [UnmanagedCallersOnly]
    public static int CollectionItem(nint self, uint index, nint* device)
    {
        *device = (nint)(0x3000 + index);
        return 0;
    }

    [UnmanagedCallersOnly]
    public static int Activate(nint self, Guid* iid, uint context, nint parameters, nint* result)
    {
        var s = FakeCom.State<ClientState>(self);
        s.ServiceRequested = *iid;
        *result = s.ServicePointer;
        return context == 23 && parameters == 0 ? 0 : FakeCom.Wrong;
    }
}
