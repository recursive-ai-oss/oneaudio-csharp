using System.Runtime.InteropServices;

namespace OneAudio.Devices.Interop;

/// <summary>
/// The Core Audio, Audio Toolbox and Core Foundation calls the Apple backend uses, bound by framework path (they are in the dyld shared cache on
/// current systems). Declarations follow the public headers (<c>AudioHardware.h</c>, <c>AudioUnitProperties.h</c>, <c>AUComponent.h</c>,
/// <c>CoreAudioTypes.h</c>). The hardware-object calls exist on macOS only; on iOS they are never reached.
/// </summary>
internal static unsafe partial class AppleNative
{
    private const string AudioToolboxLib = "/System/Library/Frameworks/AudioToolbox.framework/AudioToolbox";
    private const string CoreAudioLib = "/System/Library/Frameworks/CoreAudio.framework/CoreAudio";
    private const string CoreFoundationLib = "/System/Library/Frameworks/CoreFoundation.framework/CoreFoundation";
    private const string SystemLib = "/usr/lib/libSystem.B.dylib";

    // Four-character codes are big-endian in the headers.
    public static uint Code(string four) => ((uint)four[0] << 24) | ((uint)four[1] << 16) | ((uint)four[2] << 8) | four[3];

    // Object model
    public const uint SystemObject = 1;
    public static readonly uint ScopeGlobal = Code("glob");
    public static readonly uint ScopeInput = Code("inpt");
    public static readonly uint ScopeOutput = Code("outp");
    public const uint ElementMain = 0;
    public static readonly uint HardwareDevices = Code("dev#");
    public static readonly uint HardwareDefaultOutput = Code("dOut");
    public static readonly uint HardwareDefaultInput = Code("dIn ");
    public static readonly uint HardwareRunLoop = Code("rnlp");
    public static readonly uint DeviceUid = Code("uid ");
    public static readonly uint ObjectName = Code("lnam");
    public static readonly uint DeviceStreamConfiguration = Code("slay");
    public static readonly uint DeviceNominalRate = Code("nsrt");
    public static readonly uint DeviceFrameSize = Code("fsiz");
    public static readonly uint DeviceFrameSizeRange = Code("fsz#");
    public static readonly uint DeviceIsAlive = Code("livn");
    public static readonly uint DeviceSafetyOffset = Code("saft");

    // Audio units
    public static readonly uint TypeOutput = Code("auou");
    public static readonly uint SubTypeHalOutput = Code("ahal");
    public static readonly uint SubTypeRemoteIo = Code("rioc");
    public static readonly uint ManufacturerApple = Code("appl");
    public static readonly uint FormatLinearPcm = Code("lpcm");
    public const uint FlagIsFloat = 1;
    public const uint FlagIsPacked = 8;
    public const uint PropertyStreamFormat = 8;
    public const uint PropertyMaximumFramesPerSlice = 14;
    public const uint PropertySetRenderCallback = 23;
    public const uint PropertyCurrentDevice = 2000;
    public const uint PropertyEnableIo = 2003;
    public const uint PropertySetInputCallback = 2005;
    public const uint UnitScopeGlobal = 0;
    public const uint UnitScopeInput = 1;
    public const uint UnitScopeOutput = 2;
    public const uint TimeSampleTimeValid = 1;

    [StructLayout(LayoutKind.Sequential)]
    public struct ObjectPropertyAddress
    {
        public uint Selector;
        public uint Scope;
        public uint Element;
    }

    [StructLayout(LayoutKind.Sequential)]
    public struct ComponentDescription
    {
        public uint Type;
        public uint SubType;
        public uint Manufacturer;
        public uint Flags;
        public uint FlagsMask;
    }

    [StructLayout(LayoutKind.Sequential)]
    public struct StreamDescription
    {
        public double SampleRate;
        public uint FormatId;
        public uint FormatFlags;
        public uint BytesPerPacket;
        public uint FramesPerPacket;
        public uint BytesPerFrame;
        public uint ChannelsPerFrame;
        public uint BitsPerChannel;
        public uint Reserved;
    }

    /// <summary><c>AudioTimeStamp</c>; the SMPTE block is carried as three opaque words (24 bytes).</summary>
    [StructLayout(LayoutKind.Sequential)]
    public struct AudioTimeStamp
    {
        public double SampleTime;
        public ulong HostTime;
        public double RateScalar;
        public ulong WordClockTime;
        public ulong Smpte0;
        public ulong Smpte1;
        public ulong Smpte2;
        public uint Flags;
        public uint Reserved;
    }

    [StructLayout(LayoutKind.Sequential)]
    public struct RenderCallbackStruct
    {
        public nint Proc;
        public nint RefCon;
    }

    /// <summary><c>AudioBufferList</c> with its first buffer inline; interleaved audio uses exactly one. The buffer holds a pointer, so it starts on an 8-byte boundary.</summary>
    [StructLayout(LayoutKind.Sequential)]
    public struct BufferList
    {
        public uint BufferCount;
        public uint Padding;
        public uint Channels;
        public uint ByteSize;
        public nint Data;
    }

    [StructLayout(LayoutKind.Sequential)]
    public struct MachTimebase
    {
        public uint Numerator;
        public uint Denominator;
    }

    // ---- Core Audio hardware objects (macOS)

    [LibraryImport(CoreAudioLib, EntryPoint = "AudioObjectGetPropertyDataSize")]
    public static partial int ObjectGetPropertyDataSize(uint obj, ObjectPropertyAddress* address, uint qualifierSize, void* qualifier, uint* size);

    [LibraryImport(CoreAudioLib, EntryPoint = "AudioObjectGetPropertyData")]
    public static partial int ObjectGetPropertyData(uint obj, ObjectPropertyAddress* address, uint qualifierSize, void* qualifier, uint* size, void* data);

    [LibraryImport(CoreAudioLib, EntryPoint = "AudioObjectSetPropertyData")]
    public static partial int ObjectSetPropertyData(uint obj, ObjectPropertyAddress* address, uint qualifierSize, void* qualifier, uint size, void* data);

    [LibraryImport(CoreAudioLib, EntryPoint = "AudioObjectAddPropertyListener")]
    public static partial int ObjectAddPropertyListener(uint obj, ObjectPropertyAddress* address, nint listener, nint clientData);

    [LibraryImport(CoreAudioLib, EntryPoint = "AudioObjectRemovePropertyListener")]
    public static partial int ObjectRemovePropertyListener(uint obj, ObjectPropertyAddress* address, nint listener, nint clientData);

    [LibraryImport(CoreAudioLib, EntryPoint = "AudioDeviceGetCurrentTime")]
    public static partial int DeviceGetCurrentTime(uint device, AudioTimeStamp* time);

    // ---- Audio units

    [LibraryImport(AudioToolboxLib, EntryPoint = "AudioComponentFindNext")]
    public static partial nint ComponentFindNext(nint after, ComponentDescription* description);

    [LibraryImport(AudioToolboxLib, EntryPoint = "AudioComponentInstanceNew")]
    public static partial int InstanceNew(nint component, nint* unit);

    [LibraryImport(AudioToolboxLib, EntryPoint = "AudioComponentInstanceDispose")]
    public static partial int InstanceDispose(nint unit);

    [LibraryImport(AudioToolboxLib, EntryPoint = "AudioUnitSetProperty")]
    public static partial int UnitSetProperty(nint unit, uint property, uint scope, uint element, void* data, uint size);

    [LibraryImport(AudioToolboxLib, EntryPoint = "AudioUnitGetProperty")]
    public static partial int UnitGetProperty(nint unit, uint property, uint scope, uint element, void* data, uint* size);

    [LibraryImport(AudioToolboxLib, EntryPoint = "AudioUnitInitialize")]
    public static partial int UnitInitialize(nint unit);

    [LibraryImport(AudioToolboxLib, EntryPoint = "AudioUnitUninitialize")]
    public static partial int UnitUninitialize(nint unit);

    [LibraryImport(AudioToolboxLib, EntryPoint = "AudioOutputUnitStart")]
    public static partial int OutputUnitStart(nint unit);

    [LibraryImport(AudioToolboxLib, EntryPoint = "AudioOutputUnitStop")]
    public static partial int OutputUnitStop(nint unit);

    [LibraryImport(AudioToolboxLib, EntryPoint = "AudioUnitRender")]
    public static partial int UnitRender(nint unit, uint* actionFlags, AudioTimeStamp* time, uint bus, uint frames, BufferList* data);

    // ---- Core Foundation and the host clock

    [LibraryImport(CoreFoundationLib, EntryPoint = "CFStringGetCString")]
    public static partial byte CFStringGetCString(nint text, byte* buffer, nint size, uint encoding);

    [LibraryImport(CoreFoundationLib, EntryPoint = "CFRelease")]
    public static partial void CFRelease(nint obj);

    [LibraryImport(SystemLib, EntryPoint = "mach_absolute_time")]
    public static partial ulong MachAbsoluteTime();

    [LibraryImport(SystemLib, EntryPoint = "mach_timebase_info")]
    public static partial int MachTimebaseInfo(MachTimebase* info);

    private static int s_present;

    /// <summary>Whether Audio Toolbox can be loaded here (it can on every Apple system; false elsewhere). Never throws.</summary>
    public static bool IsPresent
    {
        get
        {
            if (s_present == 0)
            {
                s_present = (OperatingSystem.IsMacOS() || OperatingSystem.IsIOS()) && NativeLibrary.TryLoad(AudioToolboxLib, out _) ? 1 : 2;
            }

            return s_present == 1;
        }
    }

    /// <summary>Copies a retained <c>CFString</c> to a managed string and releases it.</summary>
    public static string? TakeString(nint cfString)
    {
        if (cfString == 0)
        {
            return null;
        }

        try
        {
            byte* buffer = stackalloc byte[1024];
            return CFStringGetCString(cfString, buffer, 1024, 0x08000100) != 0 ? Marshal.PtrToStringUTF8((nint)buffer) : null;
        }
        finally
        {
            CFRelease(cfString);
        }
    }
}
