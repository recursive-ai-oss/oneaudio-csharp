using System.Runtime.InteropServices;

namespace OneAudio.Devices.Interop;

/// <summary>What the audio unit's real-time callbacks hand to the stream. Both run on the system's audio thread and must not allocate, lock or throw.</summary>
internal interface IAudioUnitClient
{
    /// <summary>Output: fill <paramref name="buffer"/> (<paramref name="frames"/> interleaved frames at the unit's channel count) for the time <paramref name="sampleTime"/> on the device clock.</summary>
    void RenderOutput(Span<float> buffer, int frames, double sampleTime);

    /// <summary>Input: a captured block at the unit's channel count and the device's rate.</summary>
    void DeliverInput(ReadOnlySpan<float> buffer, int frames);
}

/// <summary>
/// One Audio Unit instance: the HAL output unit on macOS (bound to a chosen device, for either direction) or the remote I/O unit on iOS. The
/// client side of the unit runs in 32-bit float; for output the unit converts from the client rate to the hardware rate itself, the client
/// channel count is the hardware's, and the stream above maps channels. For input the client format is the hardware's.
/// </summary>
internal sealed unsafe class AudioUnitHost : IDisposable
{
    private const int MaxSliceFrames = 4096;
    private const int MaxChannels = 64;

    private static double s_nanosPerTick;

    private readonly bool _output;
    private readonly uint _device;
    private readonly IAudioUnitClient _client;
    private nint _unit;
    private GCHandle _self;
    private nint _inputBuffer;
    private bool _initialized;
    private long _stampSequence;
    private double _stampSample;
    private ulong _stampHost;

    private AudioUnitHost(bool output, uint device, IAudioUnitClient client)
    {
        _output = output;
        _device = device;
        _client = client;
    }

    /// <summary>Channels of the unit's client format (the hardware's channel count).</summary>
    public int HwChannels { get; private set; }

    /// <summary>The hardware (device clock) sample rate.</summary>
    public int HwRate { get; private set; }

    /// <summary>The device's I/O period in frames at the hardware rate.</summary>
    public int HwPeriodFrames { get; private set; }

    /// <summary>The device's safety offset in frames at the hardware rate (0 where the platform does not say).</summary>
    public int HwSafetyFrames { get; private set; }

    /// <summary>
    /// Creates, configures and initializes a unit without starting it.
    /// </summary>
    /// <param name="output">True for playback, false for capture.</param>
    /// <param name="device">The Core Audio device (macOS); ignored on iOS.</param>
    /// <param name="clientRate">Output only: the rate the client side runs at.</param>
    /// <param name="periodHint">The period in frames at the hardware rate to ask the device for, or 0 to leave it.</param>
    /// <param name="client">Receives the real-time callbacks.</param>
    /// <param name="reason">Why creation failed.</param>
    public static AudioUnitHost? TryCreate(bool output, uint device, int clientRate, int periodHint, IAudioUnitClient client, out string reason)
    {
        var host = new AudioUnitHost(output, device, client);
        if (host.Setup(clientRate, periodHint, out reason))
        {
            return host;
        }

        host.Dispose();
        return null;
    }

    private bool Setup(int clientRate, int periodHint, out string reason)
    {
        bool mac = OperatingSystem.IsMacOS();
        var description = new AppleNative.ComponentDescription
        {
            Type = AppleNative.TypeOutput,
            SubType = mac ? AppleNative.SubTypeHalOutput : AppleNative.SubTypeRemoteIo,
            Manufacturer = AppleNative.ManufacturerApple,
        };
        nint component = AppleNative.ComponentFindNext(0, &description);
        if (component == 0)
        {
            reason = "this system has no audio output unit";
            return false;
        }

        nint unit;
        int status = AppleNative.InstanceNew(component, &unit);
        if (status != 0)
        {
            reason = $"the audio unit could not be created ({status})";
            return false;
        }

        _unit = unit;
        _self = GCHandle.Alloc(this);

        // Direction: the HAL unit has output on bus 0 and input on bus 1, each switched on or off before a device is chosen.
        if (mac || !_output)
        {
            status = SetU32(AppleNative.PropertyEnableIo, AppleNative.UnitScopeInput, 1, _output ? 0u : 1u);
            if (status == 0)
            {
                status = SetU32(AppleNative.PropertyEnableIo, AppleNative.UnitScopeOutput, 0, _output ? 1u : 0u);
            }

            if (status != 0)
            {
                reason = $"the audio unit would not switch to {(_output ? "output" : "input")} ({status})";
                return false;
            }
        }

        if (mac)
        {
            uint dev = _device;
            status = AppleNative.UnitSetProperty(_unit, AppleNative.PropertyCurrentDevice, AppleNative.UnitScopeGlobal, 0, &dev, sizeof(uint));
            if (status != 0)
            {
                reason = $"the audio unit could not use the device ({status})";
                return false;
            }
        }

        // The hardware side: output bus 0's output scope, or input bus 1's input scope.
        var hw = default(AppleNative.StreamDescription);
        uint size = (uint)sizeof(AppleNative.StreamDescription);
        status = _output
            ? AppleNative.UnitGetProperty(_unit, AppleNative.PropertyStreamFormat, AppleNative.UnitScopeOutput, 0, &hw, &size)
            : AppleNative.UnitGetProperty(_unit, AppleNative.PropertyStreamFormat, AppleNative.UnitScopeInput, 1, &hw, &size);
        if (!mac && status == 0)
        {
            // The remote I/O unit may not know the hardware rate before it is initialized; the audio session does. The channel count the
            // unit reports is the one its converter expects, so the session fills in only what the unit leaves blank.
            if (hw.SampleRate < 1000)
            {
                hw.SampleRate = AppleSession.SampleRate();
            }

            if (hw.ChannelsPerFrame == 0)
            {
                hw.ChannelsPerFrame = (uint)AppleSession.RouteChannels(!_output);
            }
        }

        if (status != 0 || hw.ChannelsPerFrame is 0 or > MaxChannels || hw.SampleRate < 1000)
        {
            reason = status != 0
                ? $"the device format could not be read ({status})"
                : $"the device reports no usable audio format ({hw.SampleRate:0} Hz x {hw.ChannelsPerFrame})";
            return false;
        }

        HwChannels = (int)hw.ChannelsPerFrame;
        HwRate = (int)Math.Round(hw.SampleRate);

        var client = new AppleNative.StreamDescription
        {
            SampleRate = _output ? clientRate : hw.SampleRate,
            FormatId = AppleNative.FormatLinearPcm,
            FormatFlags = AppleNative.FlagIsFloat | AppleNative.FlagIsPacked,
            BytesPerPacket = 4 * hw.ChannelsPerFrame,
            FramesPerPacket = 1,
            BytesPerFrame = 4 * hw.ChannelsPerFrame,
            ChannelsPerFrame = hw.ChannelsPerFrame,
            BitsPerChannel = 32,
        };
        status = _output
            ? AppleNative.UnitSetProperty(_unit, AppleNative.PropertyStreamFormat, AppleNative.UnitScopeInput, 0, &client, (uint)sizeof(AppleNative.StreamDescription))
            : AppleNative.UnitSetProperty(_unit, AppleNative.PropertyStreamFormat, AppleNative.UnitScopeOutput, 1, &client, (uint)sizeof(AppleNative.StreamDescription));
        if (status != 0)
        {
            reason = $"the {(_output ? "output" : "input")} unit refused {client.SampleRate:0} Hz x {HwChannels} float ({status})";
            return false;
        }

        _ = SetU32(AppleNative.PropertyMaximumFramesPerSlice, AppleNative.UnitScopeGlobal, 0, MaxSliceFrames);

        if (mac)
        {
            if (periodHint > 0)
            {
                Hal.SetFrameSize(_device, periodHint);
            }

            HwPeriodFrames = Hal.FrameSize(_device);
            HwSafetyFrames = Hal.SafetyOffset(_device, _output);
        }
        else
        {
            HwPeriodFrames = Math.Max(64, (int)(AppleSession.IoBufferSeconds() * HwRate));
        }

        if (HwPeriodFrames <= 0)
        {
            HwPeriodFrames = 512;
        }

        var callback = new AppleNative.RenderCallbackStruct { RefCon = GCHandle.ToIntPtr(_self) };
        if (_output)
        {
            callback.Proc = (nint)(delegate* unmanaged<nint, uint*, AppleNative.AudioTimeStamp*, uint, uint, AppleNative.BufferList*, int>)&OutputProc;
            status = AppleNative.UnitSetProperty(_unit, AppleNative.PropertySetRenderCallback, AppleNative.UnitScopeInput, 0, &callback, (uint)sizeof(AppleNative.RenderCallbackStruct));
        }
        else
        {
            _inputBuffer = (nint)NativeMemory.AllocZeroed((nuint)(MaxSliceFrames * HwChannels * sizeof(float)));
            callback.Proc = (nint)(delegate* unmanaged<nint, uint*, AppleNative.AudioTimeStamp*, uint, uint, AppleNative.BufferList*, int>)&InputProc;
            status = AppleNative.UnitSetProperty(_unit, AppleNative.PropertySetInputCallback, AppleNative.UnitScopeGlobal, 0, &callback, (uint)sizeof(AppleNative.RenderCallbackStruct));
        }

        if (status != 0)
        {
            reason = $"the audio unit would not take its callback ({status})";
            return false;
        }

        status = AppleNative.UnitInitialize(_unit);
        _initialized = status == 0;
        if (status != 0)
        {
            reason = $"the audio unit could not be initialized at {HwRate} Hz x{HwChannels} ({status})" + (_output ? string.Empty : "; there may be no microphone to record from");
            return false;
        }

        reason = string.Empty;
        return true;
    }

    private int SetU32(uint property, uint scope, uint element, uint value) =>
        AppleNative.UnitSetProperty(_unit, property, scope, element, &value, sizeof(uint));

    /// <summary>Starts the unit; returns the system status (0 on success).</summary>
    public int Start() => AppleNative.OutputUnitStart(_unit);

    /// <summary>Stops the unit; when it returns the system has finished any callback in flight.</summary>
    public int Stop() => AppleNative.OutputUnitStop(_unit);

    /// <summary>
    /// The device clock's current sample time (at the hardware rate), or NaN when it cannot be told. On macOS it is the device's own current
    /// time; on iOS, which has no such call, it is extended from the last render time stamp with the host clock.
    /// </summary>
    public double DeviceSampleTimeNow()
    {
        if (OperatingSystem.IsMacOS())
        {
            var ts = default(AppleNative.AudioTimeStamp);
            return AppleNative.DeviceGetCurrentTime(_device, &ts) == 0 && (ts.Flags & AppleNative.TimeSampleTimeValid) != 0 ? ts.SampleTime : double.NaN;
        }

        for (int attempt = 0; attempt < 4; attempt++)
        {
            long before = Volatile.Read(ref _stampSequence);
            if (before == 0 || (before & 1) != 0)
            {
                continue;
            }

            double sample = _stampSample;
            ulong host = _stampHost;
            if (Volatile.Read(ref _stampSequence) != before)
            {
                continue;
            }

            if (s_nanosPerTick == 0)
            {
                var info = default(AppleNative.MachTimebase);
                _ = AppleNative.MachTimebaseInfo(&info);
                s_nanosPerTick = info.Denominator == 0 ? 1 : (double)info.Numerator / info.Denominator;
            }

            ulong now = AppleNative.MachAbsoluteTime();
            double seconds = now > host ? (now - host) * s_nanosPerTick * 1e-9 : 0;
            return sample + (seconds * HwRate);
        }

        return double.NaN;
    }

    // Records the (sample time, host time) pair of the latest cycle; the render thread is the only writer.
    private void Stamp(AppleNative.AudioTimeStamp* ts)
    {
        if ((ts->Flags & AppleNative.TimeSampleTimeValid) == 0)
        {
            return;
        }

        long sequence = _stampSequence;
        Volatile.Write(ref _stampSequence, sequence + 1);
        _stampSample = ts->SampleTime;
        _stampHost = ts->HostTime;
        Volatile.Write(ref _stampSequence, sequence + 2);
    }

    [UnmanagedCallersOnly]
    private static int OutputProc(nint refCon, uint* flags, AppleNative.AudioTimeStamp* ts, uint bus, uint frames, AppleNative.BufferList* io)
    {
        var host = (AudioUnitHost?)GCHandle.FromIntPtr(refCon).Target;
        if (host is null || io->BufferCount == 0)
        {
            return 0;
        }

        host.Stamp(ts);
        int channels = host.HwChannels;
        uint capacity = io->ByteSize / (uint)(4 * channels);
        int n = (int)Math.Min(frames, capacity);
        host._client.RenderOutput(new Span<float>((void*)io->Data, n * channels), n, ts->SampleTime);
        return 0;
    }

    [UnmanagedCallersOnly]
    private static int InputProc(nint refCon, uint* flags, AppleNative.AudioTimeStamp* ts, uint bus, uint frames, AppleNative.BufferList* unused)
    {
        var host = (AudioUnitHost?)GCHandle.FromIntPtr(refCon).Target;
        if (host is null || frames == 0 || frames > MaxSliceFrames)
        {
            return 0;
        }

        host.Stamp(ts);
        int channels = host.HwChannels;
        var list = new AppleNative.BufferList
        {
            BufferCount = 1,
            Channels = (uint)channels,
            ByteSize = frames * (uint)(4 * channels),
            Data = host._inputBuffer,
        };
        if (AppleNative.UnitRender(host._unit, flags, ts, bus, frames, &list) == 0)
        {
            host._client.DeliverInput(new ReadOnlySpan<float>((void*)list.Data, (int)frames * channels), (int)frames);
        }

        return 0;
    }

    public void Dispose()
    {
        if (_unit != 0)
        {
            _ = AppleNative.OutputUnitStop(_unit);
            if (_initialized)
            {
                _ = AppleNative.UnitUninitialize(_unit);
            }

            _ = AppleNative.InstanceDispose(_unit);
            _unit = 0;
        }

        if (_self.IsAllocated)
        {
            _self.Free();
        }

        if (_inputBuffer != 0)
        {
            NativeMemory.Free((void*)_inputBuffer);
            _inputBuffer = 0;
        }
    }
}
