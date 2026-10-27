using System.Runtime.InteropServices;
using OneAudio.Devices.Interop;
using Xunit;

namespace OneAudio.Devices.Tests;

/// <summary>
/// The WASAPI backend's parts that do not need Windows: struct layouts, v-table slot numbers (checked by calling the real wrappers on
/// hand-built COM objects), HRESULT mapping, position arithmetic, format negotiation, sample conversion, notification mapping and the stream's
/// per-cycle logic. The checks that need a real endpoint are in <see cref="WasapiWindowsTests"/>.
/// </summary>
public unsafe class WasapiTests
{
    // ------------------------------------------------------------------ layouts

    [Fact]
    public void WaveFormatExtensible_MatchesTheDocumentedLayout()
    {
        Assert.Equal(40, sizeof(WaveFormatExtensible));
        Assert.Equal(0, (int)Marshal.OffsetOf<WaveFormatExtensible>(nameof(WaveFormatExtensible.FormatTag)));
        Assert.Equal(2, (int)Marshal.OffsetOf<WaveFormatExtensible>(nameof(WaveFormatExtensible.Channels)));
        Assert.Equal(4, (int)Marshal.OffsetOf<WaveFormatExtensible>(nameof(WaveFormatExtensible.SamplesPerSec)));
        Assert.Equal(8, (int)Marshal.OffsetOf<WaveFormatExtensible>(nameof(WaveFormatExtensible.AvgBytesPerSec)));
        Assert.Equal(12, (int)Marshal.OffsetOf<WaveFormatExtensible>(nameof(WaveFormatExtensible.BlockAlign)));
        Assert.Equal(14, (int)Marshal.OffsetOf<WaveFormatExtensible>(nameof(WaveFormatExtensible.BitsPerSample)));
        Assert.Equal(16, (int)Marshal.OffsetOf<WaveFormatExtensible>(nameof(WaveFormatExtensible.Size)));
        Assert.Equal(18, (int)Marshal.OffsetOf<WaveFormatExtensible>(nameof(WaveFormatExtensible.ValidBitsPerSample)));
        Assert.Equal(20, (int)Marshal.OffsetOf<WaveFormatExtensible>(nameof(WaveFormatExtensible.ChannelMask)));
        Assert.Equal(24, (int)Marshal.OffsetOf<WaveFormatExtensible>(nameof(WaveFormatExtensible.SubFormat)));
    }

    [Fact]
    public void MakeFormat_FloatStereo_HasTheDocumentedFields()
    {
        var f = WasapiLogic.MakeFormat(WasapiSampleKind.Float32, 48_000, 2);
        Assert.Equal(0xFFFE, f.FormatTag);
        Assert.Equal(2, f.Channels);
        Assert.Equal(48_000u, f.SamplesPerSec);
        Assert.Equal(8u, f.BlockAlign);
        Assert.Equal(384_000u, f.AvgBytesPerSec);
        Assert.Equal(32, f.BitsPerSample);
        Assert.Equal(32, f.ValidBitsPerSample);
        Assert.Equal(22, f.Size);
        Assert.Equal(0x3u, f.ChannelMask);
        Assert.Equal(new Guid("00000003-0000-0010-8000-00AA00389B71"), f.SubFormat);
    }

    [Theory]
    [InlineData((int)WasapiSampleKind.Int32, 32, 32, 4)]
    [InlineData((int)WasapiSampleKind.Int24In32, 32, 24, 4)]
    [InlineData((int)WasapiSampleKind.Int24, 24, 24, 3)]
    [InlineData((int)WasapiSampleKind.Int16, 16, 16, 2)]
    public void MakeFormat_Integer_UsesPcmSubtypeAndTheContainerWidth(int kindValue, int container, int valid, int bytes)
    {
        var kind = (WasapiSampleKind)kindValue;
        var f = WasapiLogic.MakeFormat(kind, 44_100, 6);
        Assert.Equal(new Guid("00000001-0000-0010-8000-00AA00389B71"), f.SubFormat);
        Assert.Equal(container, f.BitsPerSample);
        Assert.Equal(valid, f.ValidBitsPerSample);
        Assert.Equal(6 * bytes, f.BlockAlign);
        Assert.Equal(44_100u * f.BlockAlign, f.AvgBytesPerSec);
        Assert.Equal(0x3Fu, f.ChannelMask);
    }

    // ------------------------------------------------------------------ HRESULTs

    [Theory]
    [InlineData(unchecked((int)0x88890004), (int)WasapiFault.Lost)] // AUDCLNT_E_DEVICE_INVALIDATED
    [InlineData(unchecked((int)0x88890026), (int)WasapiFault.Lost)] // AUDCLNT_E_RESOURCES_INVALIDATED
    [InlineData(unchecked((int)0x88890010), (int)WasapiFault.Lost)] // AUDCLNT_E_SERVICE_NOT_RUNNING
    [InlineData(unchecked((int)0x8889000F), (int)WasapiFault.Lost)] // AUDCLNT_E_ENDPOINT_CREATE_FAILED
    [InlineData(unchecked((int)0x88890018), (int)WasapiFault.Lost)] // AUDCLNT_E_BUFFER_ERROR
    [InlineData(unchecked((int)0x88890006), (int)WasapiFault.Skip)] // AUDCLNT_E_BUFFER_TOO_LARGE
    [InlineData(unchecked((int)0x80004005), (int)WasapiFault.Failed)] // E_FAIL
    [InlineData(0, (int)WasapiFault.None)]
    [InlineData(1, (int)WasapiFault.None)]                           // S_FALSE
    [InlineData(0x08890001, (int)WasapiFault.None)]                  // AUDCLNT_S_BUFFER_EMPTY
    public void Classify_MapsHresultsToTheirConsequence(int hresult, int expectedValue)
    {
        var expected = (WasapiFault)expectedValue;
        Assert.Equal(expected, WasapiLogic.Classify(hresult));
        Assert.Equal(expected == WasapiFault.Lost, WasapiLogic.IsDeviceLost(hresult));
    }

    [Fact]
    public void Describe_GivesRefusalsAReadableReason()
    {
        string inUse = WasapiLogic.Describe("the device refused exclusive mode", unchecked((int)0x8889000A));
        Assert.Contains("in use by another application", inUse);
        Assert.Contains("0x8889000A", inUse);
        Assert.Contains("microphone", WasapiLogic.Describe("open", unchecked((int)0x80070005)));
        Assert.Contains("0x80004005", WasapiLogic.Describe("open", unchecked((int)0x80004005)));
        Assert.Contains("exclusive", WasapiLogic.Describe("open", unchecked((int)0x8889000E)));
    }

    // ------------------------------------------------------------------ arithmetic

    [Theory]
    [InlineData(10_000_000UL, 10_000_000UL, 48_000, 48_000L)]
    [InlineData(5_000_000UL, 10_000_000UL, 48_000, 24_000L)]
    [InlineData(48_000UL, 48_000UL, 44_100, 44_100L)]     // a clock counting in frames
    [InlineData(0UL, 10_000_000UL, 48_000, 0L)]
    [InlineData(1UL, 0UL, 48_000, 0L)]                    // no frequency known
    public void PositionToFrames_IsPositionOverFrequencyTimesRate(ulong position, ulong frequency, int rate, long expected) =>
        Assert.Equal(expected, WasapiLogic.PositionToFrames(position, frequency, rate));

    [Fact]
    public void PositionToFrames_DoesNotOverflowOnVeryLongStreams()
    {
        // 10 years of 10 MHz ticks at 384 kHz would overflow a 64-bit product.
        ulong ticks = 10UL * 365 * 24 * 3600 * 10_000_000UL;
        long frames = WasapiLogic.PositionToFrames(ticks, 10_000_000UL, 384_000);
        Assert.Equal(10L * 365 * 24 * 3600 * 384_000, frames);
        Assert.Equal(long.MaxValue, WasapiLogic.PositionToFrames(ulong.MaxValue, 1, int.MaxValue));
    }

    [Fact]
    public void Durations_RoundTripAndAlign()
    {
        Assert.Equal(200_000, WasapiLogic.ToReferenceTime(20));
        Assert.Equal(100_000, WasapiLogic.FramesToReferenceTime(480, 48_000));
        Assert.Equal(480, WasapiLogic.ReferenceTimeToFrames(100_000, 48_000));
        // The documented alignment recipe: the aligned frame count as a duration, rounded to nearest.
        Assert.Equal(WasapiLogic.FramesToReferenceTime(528, 44_100), WasapiLogic.AlignedDuration(528, 44_100));
        Assert.Equal(119_728, WasapiLogic.AlignedDuration(528, 44_100));
        Assert.Equal(0, WasapiLogic.WritableFrames(480, 480));
        Assert.Equal(0, WasapiLogic.WritableFrames(480, 500));
        Assert.Equal(200, WasapiLogic.WritableFrames(480, 280));
    }

    [Fact]
    public void SharedAndExclusiveDurations_FollowTheOptions()
    {
        var latency = new DeviceStreamOptions { LatencyMilliseconds = 30 };
        Assert.Equal(300_000, WasapiLogic.SharedDuration(latency));
        Assert.Equal(100_000, WasapiLogic.SharedDuration(new DeviceStreamOptions { BufferFrames = 480 }));

        Assert.Equal(300_000, WasapiLogic.ExclusiveDuration(latency, 30_000));
        Assert.Equal(100_000, WasapiLogic.ExclusiveDuration(new DeviceStreamOptions { BufferFrames = 480, LatencyMilliseconds = 99 }, 30_000));
        Assert.Equal(50_000, WasapiLogic.ExclusiveDuration(new DeviceStreamOptions { PeriodFrames = 240, BufferFrames = 480 }, 30_000));
        // Never below the device's minimum period.
        Assert.Equal(30_000, WasapiLogic.ExclusiveDuration(new DeviceStreamOptions { PeriodFrames = 48 }, 30_000));
    }

    // ------------------------------------------------------------------ exclusive negotiation

    [Fact]
    public void NegotiateExclusive_PicksTheBestLayoutTheDeviceAccepts()
    {
        Assert.True(WasapiLogic.NegotiateExclusive(k => true, out var kind));
        Assert.Equal(WasapiSampleKind.Float32, kind);

        Assert.True(WasapiLogic.NegotiateExclusive(k => k is WasapiSampleKind.Int24 or WasapiSampleKind.Int16, out kind));
        Assert.Equal(WasapiSampleKind.Int24, kind);

        var asked = new List<WasapiSampleKind>();
        Assert.True(WasapiLogic.NegotiateExclusive(k => { asked.Add(k); return k == WasapiSampleKind.Int16; }, out kind));
        Assert.Equal(WasapiSampleKind.Int16, kind);
        Assert.Equal(
            [WasapiSampleKind.Float32, WasapiSampleKind.Int32, WasapiSampleKind.Int24In32, WasapiSampleKind.Int24, WasapiSampleKind.Int16],
            asked);
    }

    [Fact]
    public void NegotiateExclusive_RefusesWithAReasonWhenNothingFits()
    {
        Assert.False(WasapiLogic.NegotiateExclusive(k => false, out _));
        string reason = WasapiLogic.ExclusiveRefusal(96_000, 6);
        Assert.Contains("96000 Hz x 6", reason);
        Assert.Contains("exclusive", reason);
    }

    [Fact]
    public void IsFormatSupported_WrapperAsksInExclusiveModeWithTheFormat()
    {
        var state = new ClientState { SupportedBits = 24 };
        using var com = new FakeCom(state, (7, (nint)(delegate* unmanaged<nint, int, WaveFormatExtensible*, nint, int>)&FakeFunctions.IsFormatSupported));
        var yes = WasapiLogic.MakeFormat(WasapiSampleKind.Int24, 48_000, 2);
        var no = WasapiLogic.MakeFormat(WasapiSampleKind.Int16, 48_000, 2);
        Assert.Equal(0, WasapiNative.IsFormatSupported(com.Pointer, 1, ref yes));
        Assert.NotEqual(0, WasapiNative.IsFormatSupported(com.Pointer, 1, ref no));
        Assert.True(WasapiLogic.NegotiateExclusive(
            k => { var f = WasapiLogic.MakeFormat(k, 48_000, 2); return WasapiNative.IsFormatSupported(com.Pointer, 1, ref f) == 0; }, out var kind));
        Assert.Equal(WasapiSampleKind.Int24, kind);
    }

    // ------------------------------------------------------------------ sample conversion

    [Fact]
    public void FromFloat_RoundsToNearestAndClamps()
    {
        float[] samples = [0f, 1f, -1f, 0.5f, 2f, -2f, float.NaN, 1f / 32767f / 2.001f];
        var bytes = new byte[samples.Length * 2];
        WasapiLogic.FromFloat(samples, WasapiSampleKind.Int16, bytes);
        short[] got = new short[samples.Length];
        Buffer.BlockCopy(bytes, 0, got, 0, bytes.Length);
        Assert.Equal(new short[] { 0, 32767, -32767, 16384, 32767, -32768, 0, 0 }, got);
    }

    [Theory]
    [InlineData((int)WasapiSampleKind.Float32)]
    [InlineData((int)WasapiSampleKind.Int32)]
    [InlineData((int)WasapiSampleKind.Int24In32)]
    [InlineData((int)WasapiSampleKind.Int24)]
    [InlineData((int)WasapiSampleKind.Int16)]
    public void Conversion_RoundTripsWithinOneStep(int kindValue)
    {
        var kind = (WasapiSampleKind)kindValue;
        var source = new float[257];
        for (int i = 0; i < source.Length; i++)
        {
            source[i] = (MathF.Sin(i * 0.37f) * 0.99f) + 0.0001f;
        }

        var bytes = new byte[source.Length * WasapiLogic.BytesPerSample(kind)];
        WasapiLogic.FromFloat(source, kind, bytes);
        var back = new float[source.Length];
        WasapiLogic.ToFloat(bytes, kind, back);
        float step = kind switch { WasapiSampleKind.Float32 => 0f, WasapiSampleKind.Int16 => 1f / 32768f, WasapiSampleKind.Int32 => 1e-7f, _ => 1f / 8388608f };
        for (int i = 0; i < source.Length; i++)
        {
            // Encoding scales by full scale, decoding by full scale + 1: at most one step of scale error plus half a step of rounding.
            Assert.InRange(MathF.Abs(back[i] - source[i]), 0f, step * 2f);
        }
    }

    [Fact]
    public void Int24_IsLittleEndianPacked()
    {
        var bytes = new byte[3];
        WasapiLogic.FromFloat([-1f], WasapiSampleKind.Int24, bytes);
        Assert.Equal(new byte[] { 0x01, 0x00, 0x80 }, bytes); // -8388607
        WasapiLogic.FromFloat([0.5f], WasapiSampleKind.Int24, bytes);
        Assert.Equal(new byte[] { 0x00, 0x00, 0x40 }, bytes); // 4194304 (round half up of 4194303.5)
        var f = new float[1];
        WasapiLogic.ToFloat([0x00, 0x00, 0x80], WasapiSampleKind.Int24, f);
        Assert.Equal(-1f, f[0]);
    }

    // ------------------------------------------------------------------ v-table slots (real wrappers over hand-built COM objects)

    [Fact]
    public void AudioClientWrappers_CallTheDocumentedSlots()
    {
        var state = new ClientState { Buffer = 960, Padding = 123, ServicePointer = 0x4242 };
        using var com = new FakeCom(
            state,
            (3, (nint)(delegate* unmanaged<nint, int, uint, long, long, WaveFormatExtensible*, nint, int>)&FakeFunctions.Initialize),
            (4, (nint)(delegate* unmanaged<nint, uint*, int>)&FakeFunctions.GetBufferSize),
            (6, (nint)(delegate* unmanaged<nint, uint*, int>)&FakeFunctions.GetCurrentPadding),
            (9, (nint)(delegate* unmanaged<nint, long*, long*, int>)&FakeFunctions.GetDevicePeriod),
            (10, (nint)(delegate* unmanaged<nint, int>)&FakeFunctions.Start),
            (11, (nint)(delegate* unmanaged<nint, int>)&FakeFunctions.Stop),
            (13, (nint)(delegate* unmanaged<nint, nint, int>)&FakeFunctions.SetEventHandle),
            (14, (nint)(delegate* unmanaged<nint, Guid*, nint*, int>)&FakeFunctions.GetService));
        nint c = com.Pointer;

        var format = WasapiLogic.MakeFormat(WasapiSampleKind.Float32, 48_000, 2);
        Assert.Equal(0, WasapiNative.Initialize(c, 1, 0x40000, 100_000, 100_000, ref format));
        Assert.Equal(1, state.InitShare);
        Assert.Equal(0x40000u, state.InitFlags);
        Assert.Equal(100_000, state.InitDuration);
        Assert.Equal(100_000, state.InitPeriod);
        Assert.Equal(format.SubFormat, state.InitFormat.SubFormat);
        Assert.Equal(48_000u, state.InitFormat.SamplesPerSec);

        Assert.Equal(0, WasapiNative.GetBufferSize(c, out uint size));
        Assert.Equal(960u, size);
        Assert.Equal(0, WasapiNative.GetCurrentPadding(c, out uint padding));
        Assert.Equal(123u, padding);
        Assert.Equal(0, WasapiNative.GetDevicePeriod(c, out long d, out long m));
        Assert.Equal((100_000L, 30_000L), (d, m));
        Assert.Equal(0, WasapiNative.Start(c));
        Assert.Equal(0, WasapiNative.Stop(c));
        Assert.Equal((1, 1), (state.Starts, state.Stops));
        Assert.Equal(0, WasapiNative.SetEventHandle(c, 0x77));
        Assert.Equal(0x77, state.EventHandle);

        Assert.Equal(0, WasapiNative.GetRenderClient(c, out nint r));
        Assert.Equal(new Guid("F294ACFC-3146-4483-A7BF-ADDCA7C260E2"), state.ServiceRequested);
        Assert.Equal(0x4242, r);
        Assert.Equal(0, WasapiNative.GetCaptureClient(c, out _));
        Assert.Equal(new Guid("C8ADBD64-E71E-48a0-A4DE-185C395CD317"), state.ServiceRequested);
        Assert.Equal(0, WasapiNative.GetClock(c, out _));
        Assert.Equal(new Guid("CD63314F-3FBA-4a1b-812C-EF96358728E7"), state.ServiceRequested);

        WasapiNative.Release(c);
        Assert.Equal(1, state.Releases);
    }

    [Fact]
    public void EnumeratorWrappers_CallTheDocumentedSlots()
    {
        var state = new EnumeratorState();
        using var com = new FakeCom(
            state,
            (3, (nint)(delegate* unmanaged<nint, int, uint, nint*, int>)&FakeFunctions.EnumAudioEndpoints),
            (4, (nint)(delegate* unmanaged<nint, int, int, nint*, int>)&FakeFunctions.GetDefaultAudioEndpoint),
            (5, (nint)(delegate* unmanaged<nint, char*, nint*, int>)&FakeFunctions.GetDevice),
            (6, (nint)(delegate* unmanaged<nint, nint, int>)&FakeFunctions.Register),
            (7, (nint)(delegate* unmanaged<nint, nint, int>)&FakeFunctions.Unregister));
        nint e = com.Pointer;

        Assert.Equal(0, WasapiNative.EnumAudioEndpoints(e, 1, 1, out nint collection));
        Assert.Equal((1, 1u, state.Collection), (state.Flow, state.StateMask, collection));
        Assert.Equal(0, WasapiNative.GetDefaultAudioEndpoint(e, 0, 0, out nint device));
        Assert.Equal((0, 0, state.Device), (state.Flow, state.Role, device));
        Assert.Equal(0, WasapiNative.GetDevice(e, "{0.0.0.00000000}.{abc}", out device));
        Assert.Equal("{0.0.0.00000000}.{abc}", state.RequestedId);
        Assert.Equal(0, WasapiNative.RegisterEndpointNotificationCallback(e, 0x11));
        Assert.Equal(0, WasapiNative.UnregisterEndpointNotificationCallback(e, 0x22));
        Assert.Equal((0x11, 0x22), (state.RegisteredClient, state.UnregisteredClient));
    }

    [Fact]
    public void CollectionAndDeviceWrappers_CallTheDocumentedSlots()
    {
        var collection = new EnumeratorState();
        using var comCollection = new FakeCom(
            collection,
            (3, (nint)(delegate* unmanaged<nint, uint*, int>)&FakeFunctions.CollectionGetCount),
            (4, (nint)(delegate* unmanaged<nint, uint, nint*, int>)&FakeFunctions.CollectionItem));
        Assert.Equal(0, WasapiNative.CollectionGetCount(comCollection.Pointer, out uint count));
        Assert.Equal(3u, count);
        Assert.Equal(0, WasapiNative.CollectionItem(comCollection.Pointer, 2, out nint item));
        Assert.Equal(0x3002, item);

        var client = new ClientState { ServicePointer = 0x5151 };
        using var comDevice = new FakeCom(client, (3, (nint)(delegate* unmanaged<nint, Guid*, uint, nint, nint*, int>)&FakeFunctions.Activate));
        Assert.Equal(0, WasapiNative.ActivateAudioClient(comDevice.Pointer, out nint audioClient));
        Assert.Equal(0x5151, audioClient);
        Assert.Equal(new Guid("1CB9AD4C-DBFA-4c32-B178-C2F568A703B2"), client.ServiceRequested);
    }

    [Fact]
    public void ClockAndBufferWrappers_CallTheDocumentedSlots()
    {
        var clock = new ClockState { Frequency = 48_000, Position = 96_000 };
        using var comClock = new FakeCom(
            clock,
            (3, (nint)(delegate* unmanaged<nint, ulong*, int>)&FakeFunctions.ClockGetFrequency),
            (4, (nint)(delegate* unmanaged<nint, ulong*, ulong*, int>)&FakeFunctions.ClockGetPosition));
        Assert.Equal(0, WasapiNative.ClockGetFrequency(comClock.Pointer, out ulong freq));
        Assert.Equal(0, WasapiNative.ClockGetPosition(comClock.Pointer, out ulong pos));
        Assert.Equal((48_000UL, 96_000UL), (freq, pos));

        var render = new RenderState { Buffer = Marshal.AllocHGlobal(64) };
        try
        {
            using var comRender = new FakeCom(
                render,
                (3, (nint)(delegate* unmanaged<nint, uint, byte**, int>)&FakeFunctions.RenderGetBuffer),
                (4, (nint)(delegate* unmanaged<nint, uint, uint, int>)&FakeFunctions.RenderReleaseBuffer));
            Assert.Equal(0, WasapiNative.RenderGetBuffer(comRender.Pointer, 7, out nint data));
            Assert.Equal((7u, render.Buffer), (render.RequestedFrames, data));
            Assert.Equal(0, WasapiNative.RenderReleaseBuffer(comRender.Pointer, 7, 2));
            Assert.Equal((7u, 2u), (render.ReleasedFrames, render.ReleasedFlags));
        }
        finally
        {
            Marshal.FreeHGlobal(render.Buffer);
        }

        var capture = new CaptureState { Current = Marshal.AllocHGlobal(64) };
        try
        {
            capture.Packets.Enqueue((new byte[8], 2, 1));
            using var comCapture = new FakeCom(
                capture,
                (3, (nint)(delegate* unmanaged<nint, byte**, uint*, uint*, ulong*, ulong*, int>)&FakeFunctions.CaptureGetBuffer),
                (4, (nint)(delegate* unmanaged<nint, uint, int>)&FakeFunctions.CaptureReleaseBuffer),
                (5, (nint)(delegate* unmanaged<nint, uint*, int>)&FakeFunctions.CaptureGetNextPacketSize));
            Assert.Equal(0, WasapiNative.CaptureGetNextPacketSize(comCapture.Pointer, out uint next));
            Assert.Equal(2u, next);
            Assert.Equal(0, WasapiNative.CaptureGetBuffer(comCapture.Pointer, out nint cdata, out uint frames, out uint flags));
            Assert.Equal((capture.Current, 2u, 1u), (cdata, frames, flags));
            Assert.Equal(0, WasapiNative.CaptureReleaseBuffer(comCapture.Pointer, 2));
            Assert.Equal(2, capture.ReleasedFrames);
        }
        finally
        {
            Marshal.FreeHGlobal(capture.Current);
        }
    }

    // ------------------------------------------------------------------ notifications

    private sealed class RecordingEvents : IEndpointEvents
    {
        public readonly List<(int Flow, int Role)> Defaults = [];
        public int Devices;

        public void DefaultDeviceChanged(int flow, int role) => Defaults.Add((flow, role));

        public void DevicesChanged() => Devices++;
    }

    [Fact]
    public void NotificationObject_ImplementsIUnknownAndForwardsTheCallbacks()
    {
        var events = new RecordingEvents();
        nint obj = EndpointNotificationClient.Create(events);
        nint* table = *(nint**)obj;

        Guid unknown = new("00000000-0000-0000-C000-000000000046");
        Guid client = new("7991EEC9-7E89-4D85-8390-6C703CEC60C0");
        Guid other = new("A95664D2-9614-4F35-A746-DE8DB63617E6");
        nint result = 0;
        Assert.Equal(0, ((delegate* unmanaged<nint, Guid*, nint*, int>)table[0])(obj, &unknown, &result));
        Assert.Equal(obj, result);
        Assert.Equal(0, ((delegate* unmanaged<nint, Guid*, nint*, int>)table[0])(obj, &client, &result));
        Assert.Equal(unchecked((int)0x80004002), ((delegate* unmanaged<nint, Guid*, nint*, int>)table[0])(obj, &other, &result));
        Assert.Equal(0, result);

        // QueryInterface added two references to the one we hold.
        Assert.Equal(4u, ((delegate* unmanaged<nint, uint>)table[1])(obj));
        Assert.Equal(3u, ((delegate* unmanaged<nint, uint>)table[2])(obj));

        fixed (char* id = "{0.0.0.00000000}.{x}")
        {
            Assert.Equal(0, ((delegate* unmanaged<nint, char*, uint, int>)table[3])(obj, id, 1));
            Assert.Equal(0, ((delegate* unmanaged<nint, char*, int>)table[4])(obj, id));
            Assert.Equal(0, ((delegate* unmanaged<nint, char*, int>)table[5])(obj, id));
            Assert.Equal(0, ((delegate* unmanaged<nint, int, int, char*, int>)table[6])(obj, 1, 0, id));
            Assert.Equal(0, ((delegate* unmanaged<nint, char*, nint, int>)table[7])(obj, id, 0));
        }

        Assert.Equal(3, events.Devices);
        Assert.Equal([(1, 0)], events.Defaults);

        for (int i = 0; i < 3; i++)
        {
            _ = ((delegate* unmanaged<nint, uint>)table[2])(obj);
        }

        // The last Release freed the object; the vtable of the type outlives it.
        Assert.Equal(EndpointNotificationClient.VtableSlots, 8);
    }

    private sealed class RecordingEngine : IDeviceEngine
    {
        public int Defaults;
        public int Devices;
        public int LostCount;
        public string? LostReason;
        public int Xruns;
        public float RenderBase = 0.125f;
        public readonly List<int> RenderLengths = [];
        public readonly List<float[]> Captured = [];

        public void Render(Span<float> interleaved)
        {
            RenderLengths.Add(interleaved.Length);
            for (int i = 0; i < interleaved.Length; i++)
            {
                interleaved[i] = RenderBase + (i * 0.001f);
            }
        }

        public void Capture(ReadOnlySpan<float> interleaved) => Captured.Add(interleaved.ToArray());

        public void NotifyLost(string? reason)
        {
            LostCount++;
            LostReason = reason;
        }

        public void NotifyXrun(int frames) => Xruns++;

        public void NotifyDefaultChanged() => Defaults++;

        public void NotifyDevicesChanged() => Devices++;

        public void NotifyInterruption(bool began)
        {
        }
    }

    [Fact]
    public void Notifications_DefaultChangeReachesOnlyEnginesOfThatDirectionAndRole()
    {
        var notifications = new WasapiNotifications();
        var output = new RecordingEngine();
        var input = new RecordingEngine();
        notifications.Add(DeviceDirection.Output, output);
        notifications.Add(DeviceDirection.Input, input);

        notifications.DefaultDeviceChanged(flow: 0, role: 0);
        Assert.Equal((1, 0), (output.Defaults, input.Defaults));
        notifications.DefaultDeviceChanged(flow: 1, role: 0);
        Assert.Equal((1, 1), (output.Defaults, input.Defaults));
        notifications.DefaultDeviceChanged(flow: 0, role: 1); // multimedia role: ignored
        notifications.DefaultDeviceChanged(flow: 0, role: 2); // communications role: ignored
        notifications.DefaultDeviceChanged(flow: 2, role: 0); // eAll: ignored
        Assert.Equal((1, 1), (output.Defaults, input.Defaults));

        notifications.DevicesChanged();
        Assert.Equal(1, output.Devices + input.Devices);

        notifications.Remove(output);
        notifications.DefaultDeviceChanged(flow: 0, role: 0);
        Assert.Equal(1, output.Defaults);
    }

    [Fact]
    public void Notifications_DevicesChangedWithoutStreamsRaisesTheStaticEvent()
    {
        int raised = 0;
        void Handler() => Interlocked.Increment(ref raised);
        AudioDevices.DevicesChanged += Handler;
        try
        {
            new WasapiNotifications().DevicesChanged();
        }
        finally
        {
            AudioDevices.DevicesChanged -= Handler;
        }

        Assert.True(raised >= 1);
    }

    // ------------------------------------------------------------------ the stream's cycle, against fake COM objects

    private sealed class Rig : IDisposable
    {
        public readonly ClientState Client = new();
        public readonly RenderState Render = new();
        public readonly CaptureState Capture = new();
        public readonly ClockState Clock = new();
        public readonly RecordingEngine Engine = new();
        public readonly WasapiStream Stream;
        private readonly List<FakeCom> _coms = [];
        private readonly nint _renderMemory = Marshal.AllocHGlobal(1 << 16);
        private readonly nint _captureMemory = Marshal.AllocHGlobal(1 << 16);

        public Rig(DeviceDirection direction, bool exclusive, WasapiSampleKind kind, int bufferFrames, int channels = 2, WasapiNotifications? notifications = null)
        {
            Render.Buffer = _renderMemory;
            Capture.Current = _captureMemory;
            nint client = Add(new FakeCom(
                Client,
                (6, (nint)(delegate* unmanaged<nint, uint*, int>)&FakeFunctions.GetCurrentPadding),
                (10, (nint)(delegate* unmanaged<nint, int>)&FakeFunctions.Start),
                (11, (nint)(delegate* unmanaged<nint, int>)&FakeFunctions.Stop))).Pointer;
            nint render = direction == DeviceDirection.Output
                ? Add(new FakeCom(
                    Render,
                    (3, (nint)(delegate* unmanaged<nint, uint, byte**, int>)&FakeFunctions.RenderGetBuffer),
                    (4, (nint)(delegate* unmanaged<nint, uint, uint, int>)&FakeFunctions.RenderReleaseBuffer))).Pointer
                : 0;
            nint capture = direction == DeviceDirection.Input
                ? Add(new FakeCom(
                    Capture,
                    (3, (nint)(delegate* unmanaged<nint, byte**, uint*, uint*, ulong*, ulong*, int>)&FakeFunctions.CaptureGetBuffer),
                    (4, (nint)(delegate* unmanaged<nint, uint, int>)&FakeFunctions.CaptureReleaseBuffer),
                    (5, (nint)(delegate* unmanaged<nint, uint*, int>)&FakeFunctions.CaptureGetNextPacketSize))).Pointer
                : 0;
            nint clock = Add(new FakeCom(
                Clock,
                (3, (nint)(delegate* unmanaged<nint, ulong*, int>)&FakeFunctions.ClockGetFrequency),
                (4, (nint)(delegate* unmanaged<nint, ulong*, ulong*, int>)&FakeFunctions.ClockGetPosition))).Pointer;
            Stream = new WasapiStream(
                new WasapiParts(client, render, capture, clock, 0, 0),
                direction,
                Engine,
                new AudioDeviceFormat(48_000, channels),
                null,
                exclusive,
                kind,
                bufferFrames / 2,
                bufferFrames,
                notifications);
        }

        private FakeCom Add(FakeCom com)
        {
            _coms.Add(com);
            return com;
        }

        public void Dispose()
        {
            Stream.Dispose();
            foreach (var c in _coms)
            {
                c.Dispose();
            }

            Marshal.FreeHGlobal(_renderMemory);
            Marshal.FreeHGlobal(_captureMemory);
        }
    }

    private static float[] ReadFloats(nint memory, int count)
    {
        var floats = new float[count];
        Marshal.Copy(memory, floats, 0, count);
        return floats;
    }

    [Fact]
    public void SharedRender_PrimesTheFreeBufferThenStartsTheClient()
    {
        using var rig = new Rig(DeviceDirection.Output, exclusive: false, WasapiSampleKind.Float32, bufferFrames: 480);
        rig.Client.Padding = 100; // 100 frames already queued
        Assert.True(rig.Stream.BeginRunning(out string reason), reason);
        Assert.Equal(1, rig.Client.Starts);
        Assert.Equal(380u, rig.Render.RequestedFrames);
        Assert.Equal(380u, rig.Render.ReleasedFrames);
        Assert.Equal(0u, rig.Render.ReleasedFlags);
        Assert.Equal([380 * 2], rig.Engine.RenderLengths);
        // What the engine wrote is what the device buffer holds, in order.
        var got = ReadFloats(rig.Render.Buffer, 760);
        Assert.Equal(0.125f, got[0]);
        Assert.Equal(0.125f + (759 * 0.001f), got[759]);
        Assert.Equal(0, rig.Engine.Xruns);
        Assert.Equal(480, rig.Stream.QueuedFrames);
    }

    [Fact]
    public void SharedRender_WritesOnlyWhatIsFree_AndCountsADrainedBufferAsAnUnderrun()
    {
        using var rig = new Rig(DeviceDirection.Output, exclusive: false, WasapiSampleKind.Float32, bufferFrames: 480);
        Assert.True(rig.Stream.BeginRunning(out _));
        Assert.Equal(480u, rig.Render.RequestedFrames); // empty buffer at the start: all of it is filled

        rig.Client.Padding = 240;
        rig.Stream.Step();
        Assert.Equal(240u, rig.Render.RequestedFrames);
        Assert.Equal(0, rig.Engine.Xruns);

        rig.Client.Padding = 480; // full: nothing to do
        int before = rig.Engine.RenderLengths.Count;
        rig.Stream.Step();
        Assert.Equal(before, rig.Engine.RenderLengths.Count);

        rig.Client.Padding = 0; // the device ate everything before we came back
        rig.Stream.Step();
        Assert.Equal(1, rig.Engine.Xruns);
        Assert.Equal(480u, rig.Render.RequestedFrames);
        Assert.Equal(0, rig.Engine.LostCount);
    }

    [Fact]
    public void ExclusiveRender_WritesTheWholeBufferInTheNegotiatedLayout()
    {
        using var rig = new Rig(DeviceDirection.Output, exclusive: true, WasapiSampleKind.Int16, bufferFrames: 64);
        Assert.True(rig.Stream.BeginRunning(out _));
        Assert.Equal(64u, rig.Render.RequestedFrames);
        var expected = new byte[64 * 2 * 2];
        var source = new float[64 * 2];
        for (int i = 0; i < source.Length; i++)
        {
            source[i] = 0.125f + (i * 0.001f);
        }

        WasapiLogic.FromFloat(source, WasapiSampleKind.Int16, expected);
        var got = new byte[expected.Length];
        Marshal.Copy(rig.Render.Buffer, got, 0, got.Length);
        Assert.Equal(expected, got);

        rig.Client.Padding = 12345; // exclusive mode never consults the padding
        rig.Stream.Step();
        Assert.Equal(64u, rig.Render.RequestedFrames);
        Assert.Equal(0, rig.Engine.Xruns);
        Assert.Equal(64, rig.Stream.QueuedFrames);
        Assert.Contains("exclusive", rig.Stream.Detail);
    }

    [Fact]
    public void Render_FramesPlayedComesFromTheClock_AndNeverGoesBackwards()
    {
        using var rig = new Rig(DeviceDirection.Output, exclusive: false, WasapiSampleKind.Float32, bufferFrames: 480);
        Assert.Equal(0, rig.Stream.FramesPlayed);
        Assert.True(rig.Stream.BeginRunning(out _));
        rig.Clock.Position = 5_000_000; // half a second at 10 MHz
        rig.Client.Padding = 100;
        rig.Stream.Step();
        Assert.Equal(24_000, rig.Stream.FramesPlayed);
        rig.Clock.Position = 4_000_000; // a clock hiccup
        rig.Stream.Step();
        Assert.Equal(24_000, rig.Stream.FramesPlayed);
        rig.Clock.Position = 10_000_000;
        rig.Stream.EndRunning();
        Assert.Equal(48_000, rig.Stream.FramesPlayed); // read again when stopped
        Assert.Equal(1, rig.Client.Stops);
    }

    [Fact]
    public void Render_DeviceInvalidatedBecomesALostNotification()
    {
        using var rig = new Rig(DeviceDirection.Output, exclusive: false, WasapiSampleKind.Float32, bufferFrames: 480);
        Assert.True(rig.Stream.BeginRunning(out _));
        rig.Client.PaddingHr = unchecked((int)0x88890004);
        rig.Stream.Step();
        Assert.True(rig.Stream.IsLost);
        Assert.Equal(1, rig.Engine.LostCount);
        Assert.Contains("removed", rig.Engine.LostReason);
        rig.Stream.Step();
        Assert.Equal(1, rig.Engine.LostCount); // reported once
        Assert.False(rig.Stream.Start(out string reason));
        Assert.Contains("lost", reason);
    }

    [Fact]
    public void Render_BufferTooLargeSkipsTheCycle_OtherBufferErrorsLoseTheDevice()
    {
        using var rig = new Rig(DeviceDirection.Output, exclusive: false, WasapiSampleKind.Float32, bufferFrames: 480);
        Assert.True(rig.Stream.BeginRunning(out _));
        rig.Render.GetBufferHr = unchecked((int)0x88890006);
        rig.Client.Padding = 100;
        rig.Stream.Step();
        Assert.False(rig.Stream.IsLost);
        Assert.Equal(0, rig.Engine.LostCount);
        rig.Render.GetBufferHr = unchecked((int)0x88890026);
        rig.Stream.Step();
        Assert.True(rig.Stream.IsLost);
        Assert.Equal(1, rig.Engine.LostCount);
    }

    [Fact]
    public void Start_FailureIsReportedByTheCallNotAsALostNotification()
    {
        using var rig = new Rig(DeviceDirection.Output, exclusive: false, WasapiSampleKind.Float32, bufferFrames: 480);
        rig.Client.PaddingHr = unchecked((int)0x88890004); // fails while priming
        Assert.False(rig.Stream.BeginRunning(out string reason));
        Assert.NotEmpty(reason);
        Assert.Equal(0, rig.Engine.LostCount);
        Assert.Equal(0, rig.Client.Starts);

        using var refused = new Rig(DeviceDirection.Output, exclusive: false, WasapiSampleKind.Float32, bufferFrames: 480);
        refused.Client.StartHr = unchecked((int)0x8889000A);
        Assert.False(refused.Stream.BeginRunning(out reason));
        Assert.Contains("in use", reason);
    }

    [Fact]
    public void Capture_DeliversEveryPacket_ZeroesSilentOnes_AndCountsDiscontinuities()
    {
        using var rig = new Rig(DeviceDirection.Input, exclusive: false, WasapiSampleKind.Float32, bufferFrames: 480);
        Assert.True(rig.Stream.BeginRunning(out _));
        Assert.Equal(1, rig.Client.Starts);
        Assert.Empty(rig.Engine.RenderLengths); // capture never renders

        float[] first = [0.1f, 0.2f, 0.3f, 0.4f];
        Marshal.Copy(first, 0, rig.Capture.Current, 4);
        rig.Capture.Packets.Enqueue((Array.Empty<byte>(), 2, 1));      // data discontinuity
        rig.Capture.Packets.Enqueue((Array.Empty<byte>(), 3, 2));      // silent
        rig.Stream.Step();
        Assert.Equal(2, rig.Engine.Captured.Count);
        Assert.Equal(first, rig.Engine.Captured[0]);
        Assert.Equal(new float[6], rig.Engine.Captured[1]);
        Assert.Equal(1, rig.Engine.Xruns);
        Assert.Equal(5, rig.Stream.FramesPlayed);
        Assert.Equal(5, rig.Capture.ReleasedFrames);
        Assert.Empty(rig.Capture.Packets);
    }

    [Fact]
    public void Capture_ConvertsIntegerLayoutsAndSplitsPacketsLargerThanTheBuffer()
    {
        using var rig = new Rig(DeviceDirection.Input, exclusive: true, WasapiSampleKind.Int16, bufferFrames: 4, channels: 1);
        Assert.True(rig.Stream.BeginRunning(out _));
        short[] pcm = [0, 16384, -16384, -32768, 8192, 4096];
        Marshal.Copy(pcm, 0, rig.Capture.Current, pcm.Length);
        rig.Capture.Packets.Enqueue((Array.Empty<byte>(), 6, 0));
        rig.Stream.Step();
        Assert.Equal(2, rig.Engine.Captured.Count);
        Assert.Equal(new[] { 0f, 0.5f, -0.5f, -1f }, rig.Engine.Captured[0]);
        Assert.Equal(new[] { 0.25f, 0.125f }, rig.Engine.Captured[1]);
        Assert.Equal(6, rig.Stream.FramesPlayed);
    }

    [Fact]
    public void Capture_ErrorsLoseTheDevice()
    {
        using var rig = new Rig(DeviceDirection.Input, exclusive: false, WasapiSampleKind.Float32, bufferFrames: 480);
        Assert.True(rig.Stream.BeginRunning(out _));
        rig.Capture.Packets.Enqueue((Array.Empty<byte>(), 2, 0));
        rig.Capture.GetBufferHr = unchecked((int)0x88890004);
        rig.Stream.Step();
        Assert.True(rig.Stream.IsLost);
        Assert.Equal(1, rig.Engine.LostCount);

        using var empty = new Rig(DeviceDirection.Input, exclusive: false, WasapiSampleKind.Float32, bufferFrames: 480);
        Assert.True(empty.Stream.BeginRunning(out _));
        empty.Capture.Packets.Enqueue((Array.Empty<byte>(), 2, 0));
        empty.Capture.GetBufferHr = 0x08890001; // AUDCLNT_S_BUFFER_EMPTY: not an error
        empty.Stream.Step();
        Assert.False(empty.Stream.IsLost);
    }

    [Fact]
    public void Probe_AfterATimeout_LosesAnInvalidatedDeviceOnly()
    {
        using var rig = new Rig(DeviceDirection.Output, exclusive: false, WasapiSampleKind.Float32, bufferFrames: 480);
        Assert.True(rig.Stream.BeginRunning(out _));
        rig.Stream.Probe();
        Assert.False(rig.Stream.IsLost);
        rig.Client.PaddingHr = unchecked((int)0x88890010);
        rig.Stream.Probe();
        Assert.True(rig.Stream.IsLost);
        Assert.Contains("service", rig.Engine.LostReason);
    }

    [Fact]
    public void Dispose_ReleasesEveryComObjectOnce_AndLeavesTheNotificationList()
    {
        var notifications = new WasapiNotifications();
        var rig = new Rig(DeviceDirection.Output, exclusive: false, WasapiSampleKind.Float32, bufferFrames: 480, notifications: notifications);
        notifications.Add(DeviceDirection.Output, rig.Engine);
        rig.Stream.Dispose();
        rig.Stream.Dispose();
        Assert.Equal((1, 1, 1), (rig.Client.Releases, rig.Render.Releases, rig.Clock.Releases));
        notifications.DefaultDeviceChanged(0, 0);
        Assert.Equal(0, rig.Engine.Defaults);
        rig.Dispose();
    }
}
