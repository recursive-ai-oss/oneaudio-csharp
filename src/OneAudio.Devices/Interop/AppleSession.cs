using System.Runtime.InteropServices;

namespace OneAudio.Devices.Interop;

/// <summary>Whether the process may record from a microphone.</summary>
internal enum MicrophoneAccess
{
    /// <summary>The system could not say.</summary>
    Unknown,

    /// <summary>The user has not been asked yet; the first capture start asks.</summary>
    NotDetermined,

    /// <summary>Capture works.</summary>
    Granted,

    /// <summary>The user (or a policy) said no.</summary>
    Denied,
}

/// <summary>
/// The AVAudioSession side of iOS audio (choosing the category, activating the session, listening for interruption, route-change and
/// media-services-reset notifications) and the microphone-permission check on both Apple systems. One session exists per process, so this is
/// static; each open stream subscribes its engine to the notifications.
/// </summary>
internal static unsafe class AppleSession
{
    private const string AvAudio = "/System/Library/Frameworks/AVFAudio.framework/AVFAudio";
    private const string AvFoundation = "/System/Library/Frameworks/AVFoundation.framework/AVFoundation";

    private static readonly Lock s_gate = new();
    private static nint s_lib;
    private static nint s_observer;
    private static IDeviceEngine[] s_engines = [];

    private static nint Lib => s_lib != 0 ? s_lib : s_lib = NativeLibrary.Load(AvAudio);

    private static nint Session => ObjC.Send(ObjC.GetClass("AVAudioSession"), ObjC.Sel("sharedInstance"));

    /// <summary>
    /// Sets the category and activates the session; fails (and says why) while a call or another app holds the audio hardware. A capture
    /// stream upgrades any category to play-and-record, routed to the speaker, since recording needs it.
    /// </summary>
    public static bool TryActivate(AppleSessionCategory category, bool forInput, out string reason)
    {
        var pool = ObjC.PoolPush();
        try
        {
            if (forInput)
            {
                category = AppleSessionCategory.PlayAndRecord;
            }

            var session = Session;
            string symbol = category switch
            {
                AppleSessionCategory.Playback => "AVAudioSessionCategoryPlayback",
                AppleSessionCategory.SoloAmbient => "AVAudioSessionCategorySoloAmbient",
                AppleSessionCategory.PlayAndRecord => "AVAudioSessionCategoryPlayAndRecord",
                _ => "AVAudioSessionCategoryAmbient",
            };

            // Options: 0x4 allow Bluetooth hands-free input, 0x8 default to the speaker instead of the earpiece.
            nuint options = category == AppleSessionCategory.PlayAndRecord ? 0xCu : 0u;
            if (!ObjC.SendPtrUIntErr(session, ObjC.Sel("setCategory:withOptions:error:"), ObjC.ConstantString(Lib, symbol), options, out var error))
            {
                reason = "AVAudioSession refused the category: " + Describe(error);
                return false;
            }

            // A short hardware buffer keeps taps and sound close together; 10 ms is well inside what a phone sustains.
            ObjC.SendDoubleErr(session, ObjC.Sel("setPreferredIOBufferDuration:error:"), 0.010, out _);
            if (!ObjC.SendBoolErr(session, ObjC.Sel("setActive:error:"), true, out error))
            {
                reason = "AVAudioSession could not become active: " + Describe(error);
                return false;
            }

            reason = string.Empty;
            return true;
        }
        catch (Exception e) when (e is DllNotFoundException or EntryPointNotFoundException)
        {
            reason = "AVFAudio is not available: " + e.Message;
            return false;
        }
        finally
        {
            ObjC.PoolPop(pool);
        }
    }

    /// <summary>The hardware buffer duration the session settled on, in seconds (0.01 when unknown).</summary>
    public static double IoBufferSeconds()
    {
        var pool = ObjC.PoolPush();
        try
        {
            double seconds = ObjC.SendDouble(Session, ObjC.Sel("IOBufferDuration"));
            return seconds is > 0.0005 and < 0.1 ? seconds : 0.01;
        }
        catch (Exception e) when (e is DllNotFoundException or EntryPointNotFoundException)
        {
            return 0.01;
        }
        finally
        {
            ObjC.PoolPop(pool);
        }
    }

    /// <summary>The session's hardware sample rate, or 0.</summary>
    public static int SampleRate()
    {
        var pool = ObjC.PoolPush();
        try
        {
            return (int)ObjC.SendDouble(Session, ObjC.Sel("sampleRate"));
        }
        catch (Exception e) when (e is DllNotFoundException or EntryPointNotFoundException)
        {
            return 0;
        }
        finally
        {
            ObjC.PoolPop(pool);
        }
    }

    /// <summary>The channel count of the current output or input route, or 0.</summary>
    public static int RouteChannels(bool input)
    {
        var pool = ObjC.PoolPush();
        try
        {
            return (int)ObjC.SendUInt(Session, ObjC.Sel(input ? "inputNumberOfChannels" : "outputNumberOfChannels"));
        }
        catch (Exception e) when (e is DllNotFoundException or EntryPointNotFoundException)
        {
            return 0;
        }
        finally
        {
            ObjC.PoolPop(pool);
        }
    }

    /// <summary>The first port of the current route, such as <c>Speaker "Speaker"</c>, for names and diagnostics.</summary>
    public static string Route(bool input)
    {
        var pool = ObjC.PoolPush();
        try
        {
            var ports = ObjC.Send(ObjC.Send(Session, ObjC.Sel("currentRoute")), ObjC.Sel(input ? "inputs" : "outputs"));
            var first = ObjC.Send(ports, ObjC.Sel("firstObject"));
            return first == 0 ? "none" : ObjC.ToManaged(ObjC.Send(first, ObjC.Sel("portType"))) + " \"" + ObjC.ToManaged(ObjC.Send(first, ObjC.Sel("portName"))) + "\"";
        }
        catch (Exception e) when (e is DllNotFoundException or EntryPointNotFoundException)
        {
            return "unknown";
        }
        finally
        {
            ObjC.PoolPop(pool);
        }
    }

    /// <summary>The name of the first port of the current route, or <paramref name="fallback"/>.</summary>
    public static string RouteName(bool input, string fallback)
    {
        var pool = ObjC.PoolPush();
        try
        {
            var ports = ObjC.Send(ObjC.Send(Session, ObjC.Sel("currentRoute")), ObjC.Sel(input ? "inputs" : "outputs"));
            var first = ObjC.Send(ports, ObjC.Sel("firstObject"));
            return first == 0 ? fallback : ObjC.ToManaged(ObjC.Send(first, ObjC.Sel("portName"))) ?? fallback;
        }
        catch (Exception e) when (e is DllNotFoundException or EntryPointNotFoundException)
        {
            return fallback;
        }
        finally
        {
            ObjC.PoolPop(pool);
        }
    }

    /// <summary>The microphone permission of this process. On iOS from the audio session, on macOS from the capture-device authorization.</summary>
    public static MicrophoneAccess Microphone()
    {
        var pool = ObjC.PoolPush();
        try
        {
            if (OperatingSystem.IsIOS())
            {
                _ = Lib;

                // AVAudioSessionRecordPermission is a four-character code: 'undt', 'deny', 'grnt'.
                nuint value = ObjC.SendUInt(Session, ObjC.Sel("recordPermission"));
                return value switch
                {
                    0x756E6474 => MicrophoneAccess.NotDetermined,
                    0x64656E79 => MicrophoneAccess.Denied,
                    0x67726E74 => MicrophoneAccess.Granted,
                    _ => MicrophoneAccess.Unknown,
                };
            }

            _ = NativeLibrary.Load(AvFoundation);
            var cls = ObjC.GetClass("AVCaptureDevice");
            if (cls == 0)
            {
                return MicrophoneAccess.Unknown;
            }

            // AVAuthorizationStatus: 0 not determined, 1 restricted, 2 denied, 3 authorized. AVMediaTypeAudio is the string "soun".
            nuint status = (nuint)ObjC.SendPtr(cls, ObjC.Sel("authorizationStatusForMediaType:"), ObjC.ToNative("soun"));
            return status switch
            {
                0 => MicrophoneAccess.NotDetermined,
                1 or 2 => MicrophoneAccess.Denied,
                3 => MicrophoneAccess.Granted,
                _ => MicrophoneAccess.Unknown,
            };
        }
        catch (Exception e) when (e is DllNotFoundException or EntryPointNotFoundException)
        {
            return MicrophoneAccess.Unknown;
        }
        finally
        {
            ObjC.PoolPop(pool);
        }
    }

    private static string Describe(nint error) =>
        error == 0 ? "unknown error" : ObjC.ToManaged(ObjC.Send(error, ObjC.Sel("localizedDescription"))) ?? "unknown error";

    /// <summary>Routes the session's notifications to an engine until <see cref="Unsubscribe"/>.</summary>
    public static void Subscribe(IDeviceEngine engine)
    {
        lock (s_gate)
        {
            s_engines = [.. s_engines, engine];
            if (s_observer != 0)
            {
                return;
            }

            var pool = ObjC.PoolPush();
            try
            {
                var cls = ObjC.GetClass("OneAudioSessionObserver");
                if (cls == 0)
                {
                    cls = ObjC.AllocateClassPair(ObjC.GetClass("NSObject"), "OneAudioSessionObserver", 0);
                    ObjC.AddMethod(cls, ObjC.Sel("onInterruption:"), (nint)(delegate* unmanaged<nint, nint, nint, void>)&OnInterruptionNotification, "v@:@");
                    ObjC.AddMethod(cls, ObjC.Sel("onRouteChange:"), (nint)(delegate* unmanaged<nint, nint, nint, void>)&OnRouteChangeNotification, "v@:@");
                    ObjC.AddMethod(cls, ObjC.Sel("onReset:"), (nint)(delegate* unmanaged<nint, nint, nint, void>)&OnResetNotification, "v@:@");
                    ObjC.RegisterClassPair(cls);
                }

                var observer = ObjC.Send(ObjC.Send(cls, ObjC.Sel("alloc")), ObjC.Sel("init"));
                var center = Center();
                var add = ObjC.Sel("addObserver:selector:name:object:");
                ObjC.SendObserver(center, add, observer, ObjC.Sel("onInterruption:"), ObjC.ConstantString(Lib, "AVAudioSessionInterruptionNotification"), 0);
                ObjC.SendObserver(center, add, observer, ObjC.Sel("onRouteChange:"), ObjC.ConstantString(Lib, "AVAudioSessionRouteChangeNotification"), 0);
                ObjC.SendObserver(center, add, observer, ObjC.Sel("onReset:"), ObjC.ConstantString(Lib, "AVAudioSessionMediaServicesWereResetNotification"), 0);
                s_observer = observer;
            }
            finally
            {
                ObjC.PoolPop(pool);
            }
        }
    }

    /// <summary>Stops routing notifications to the engine (the observer stays registered and ignores them when nothing is subscribed).</summary>
    public static void Unsubscribe(IDeviceEngine engine)
    {
        lock (s_gate)
        {
            s_engines = [.. s_engines.Where(e => !ReferenceEquals(e, engine))];
        }
    }

    private static nint Center() => ObjC.Send(ObjC.GetClass("NSNotificationCenter"), ObjC.Sel("defaultCenter"));

    /// <summary>Posts an interruption-began or -ended notification the way the system would, so the whole path runs without a phone call. iOS only.</summary>
    public static bool PostInterruption(bool began)
    {
        if (!OperatingSystem.IsIOS())
        {
            return false;
        }

        var pool = ObjC.PoolPush();
        try
        {
            var number = ObjC.SendUIntArg(ObjC.GetClass("NSNumber"), ObjC.Sel("numberWithUnsignedInteger:"), began ? 1u : 2u);
            var info = ObjC.SendDictionary(ObjC.GetClass("NSDictionary"), ObjC.Sel("dictionaryWithObject:forKey:"), number, ObjC.ConstantString(Lib, "AVAudioSessionInterruptionTypeKey"));
            ObjC.SendPost(Center(), ObjC.Sel("postNotificationName:object:userInfo:"), ObjC.ConstantString(Lib, "AVAudioSessionInterruptionNotification"), Session, info);
            return true;
        }
        catch (Exception e) when (e is DllNotFoundException or EntryPointNotFoundException)
        {
            return false;
        }
        finally
        {
            ObjC.PoolPop(pool);
        }
    }

    // Notification handlers run on whichever thread posted. The engine's notifications never throw, so nothing unwinds into the system.
    [UnmanagedCallersOnly]
    private static void OnInterruptionNotification(nint self, nint command, nint notification)
    {
        var pool = ObjC.PoolPush();
        try
        {
            var info = ObjC.Send(notification, ObjC.Sel("userInfo"));
            var number = ObjC.SendPtr(info, ObjC.Sel("objectForKey:"), ObjC.ConstantString(Lib, "AVAudioSessionInterruptionTypeKey"));
            if (number != 0)
            {
                // AVAudioSessionInterruptionTypeBegan = 1, Ended = 2.
                bool began = ObjC.SendUInt(number, ObjC.Sel("unsignedIntegerValue")) == 1;
                foreach (var engine in Volatile.Read(ref s_engines))
                {
                    engine.NotifyInterruption(began);
                }
            }
        }
        finally
        {
            ObjC.PoolPop(pool);
        }
    }

    [UnmanagedCallersOnly]
    private static void OnRouteChangeNotification(nint self, nint command, nint notification)
    {
        foreach (var engine in Volatile.Read(ref s_engines))
        {
            engine.NotifyDefaultChanged();
        }
    }

    [UnmanagedCallersOnly]
    private static void OnResetNotification(nint self, nint command, nint notification)
    {
        foreach (var engine in Volatile.Read(ref s_engines))
        {
            engine.NotifyLost("the audio services were reset");
        }
    }
}
