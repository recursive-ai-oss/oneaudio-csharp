using System.Runtime.InteropServices;

namespace OneAudio.Devices.Interop;

/// <summary>
/// The few Objective-C runtime calls the audio-session and permission code needs: classes and selectors by name, typed <c>objc_msgSend</c>
/// entry points, and one class made at run time to receive notifications. Apple platforms only.
/// </summary>
internal static unsafe partial class ObjC
{
    private const string Runtime = "/usr/lib/libobjc.A.dylib";

    [LibraryImport(Runtime, EntryPoint = "objc_getClass", StringMarshalling = StringMarshalling.Utf8)]
    public static partial nint GetClass(string name);

    [LibraryImport(Runtime, EntryPoint = "sel_registerName", StringMarshalling = StringMarshalling.Utf8)]
    public static partial nint Sel(string name);

    [LibraryImport(Runtime, EntryPoint = "objc_allocateClassPair", StringMarshalling = StringMarshalling.Utf8)]
    public static partial nint AllocateClassPair(nint superclass, string name, nuint extraBytes);

    [LibraryImport(Runtime, EntryPoint = "objc_registerClassPair")]
    public static partial void RegisterClassPair(nint cls);

    [LibraryImport(Runtime, EntryPoint = "class_addMethod", StringMarshalling = StringMarshalling.Utf8)]
    [return: MarshalAs(UnmanagedType.U1)]
    public static partial bool AddMethod(nint cls, nint selector, nint implementation, string types);

    [LibraryImport(Runtime, EntryPoint = "objc_msgSend")]
    public static partial nint Send(nint receiver, nint selector);

    [LibraryImport(Runtime, EntryPoint = "objc_msgSend")]
    public static partial nint SendPtr(nint receiver, nint selector, nint a);

    [LibraryImport(Runtime, EntryPoint = "objc_msgSend")]
    public static partial nuint SendUInt(nint receiver, nint selector);

    [LibraryImport(Runtime, EntryPoint = "objc_msgSend")]
    public static partial nint SendUIntArg(nint receiver, nint selector, nuint a);

    [LibraryImport(Runtime, EntryPoint = "objc_msgSend")]
    [return: MarshalAs(UnmanagedType.U1)]
    public static partial bool SendBoolErr(nint receiver, nint selector, [MarshalAs(UnmanagedType.U1)] bool a, out nint error);

    [LibraryImport(Runtime, EntryPoint = "objc_msgSend")]
    [return: MarshalAs(UnmanagedType.U1)]
    public static partial bool SendPtrUIntErr(nint receiver, nint selector, nint a, nuint b, out nint error);

    [LibraryImport(Runtime, EntryPoint = "objc_msgSend")]
    [return: MarshalAs(UnmanagedType.U1)]
    public static partial bool SendDoubleErr(nint receiver, nint selector, double a, out nint error);

    [LibraryImport(Runtime, EntryPoint = "objc_msgSend")]
    public static partial double SendDouble(nint receiver, nint selector);

    [LibraryImport(Runtime, EntryPoint = "objc_msgSend")]
    public static partial void SendObserver(nint receiver, nint selector, nint observer, nint action, nint name, nint obj);

    [LibraryImport(Runtime, EntryPoint = "objc_msgSend")]
    public static partial void SendPost(nint receiver, nint selector, nint name, nint obj, nint userInfo);

    [LibraryImport(Runtime, EntryPoint = "objc_msgSend")]
    public static partial nint SendDictionary(nint receiver, nint selector, nint value, nint key);

    [LibraryImport(Runtime, EntryPoint = "objc_autoreleasePoolPush")]
    public static partial nint PoolPush();

    [LibraryImport(Runtime, EntryPoint = "objc_autoreleasePoolPop")]
    public static partial void PoolPop(nint pool);

    /// <summary>Reads an <c>NSString*</c> constant a framework exports (a global variable), such as a notification name.</summary>
    public static nint ConstantString(nint library, string symbol) => *(nint*)NativeLibrary.GetExport(library, symbol);

    /// <summary>Copies an <c>NSString</c> to a managed string.</summary>
    public static string? ToManaged(nint nsString)
    {
        if (nsString == 0)
        {
            return null;
        }

        var utf8 = Send(nsString, Sel("UTF8String"));
        return utf8 == 0 ? null : Marshal.PtrToStringUTF8(utf8);
    }

    /// <summary>Makes an autoreleased <c>NSString</c> from a managed string.</summary>
    public static nint ToNative(string text)
    {
        var bytes = System.Text.Encoding.UTF8.GetBytes(text + "\0");
        fixed (byte* p = bytes)
        {
            return SendPtr(GetClass("NSString"), Sel("stringWithUTF8String:"), (nint)p);
        }
    }
}
