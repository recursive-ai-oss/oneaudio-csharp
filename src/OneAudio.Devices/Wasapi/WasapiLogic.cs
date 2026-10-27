using System.Buffers.Binary;
using OneAudio.Devices.Interop;

namespace OneAudio.Devices;

/// <summary>The sample layouts a WASAPI stream can run in. Shared mode always uses <see cref="Float32"/>; exclusive mode takes what the hardware accepts.</summary>
internal enum WasapiSampleKind
{
    /// <summary>32-bit IEEE float.</summary>
    Float32,

    /// <summary>32-bit integer, all 32 bits valid.</summary>
    Int32,

    /// <summary>24-bit integer in a 32-bit container (the 24 valid bits are the high ones).</summary>
    Int24In32,

    /// <summary>24-bit integer packed in three bytes.</summary>
    Int24,

    /// <summary>16-bit integer.</summary>
    Int16,
}

/// <summary>What happens to a stream after a failed call.</summary>
internal enum WasapiFault
{
    /// <summary>Not a failure.</summary>
    None,

    /// <summary>The endpoint is gone or unusable (unplugged, disabled, format changed, service restarted); the engine reopens.</summary>
    Lost,

    /// <summary>The call asked for more than the buffer holds or came at the wrong moment; skip this cycle.</summary>
    Skip,

    /// <summary>Any other failure: the call is refused.</summary>
    Failed,
}

/// <summary>
/// The parts of the WASAPI backend that are arithmetic, byte layout and decisions, kept free of COM so they run (and are tested) on every
/// operating system.
/// </summary>
internal static class WasapiLogic
{
    public const int AudclntEDeviceInvalidated = unchecked((int)0x88890004);
    public const int AudclntEBufferTooLarge = unchecked((int)0x88890006);
    public const int AudclntEOutOfOrder = unchecked((int)0x88890007);
    public const int AudclntEUnsupportedFormat = unchecked((int)0x88890008);
    public const int AudclntEDeviceInUse = unchecked((int)0x8889000A);
    public const int AudclntEExclusiveModeNotAllowed = unchecked((int)0x8889000E);
    public const int AudclntEEndpointCreateFailed = unchecked((int)0x8889000F);
    public const int AudclntEServiceNotRunning = unchecked((int)0x88890010);
    public const int AudclntEExclusiveModeOnly = unchecked((int)0x88890012);
    public const int AudclntEBufferError = unchecked((int)0x88890018);
    public const int AudclntEBufferSizeNotAligned = unchecked((int)0x88890019);
    public const int AudclntEInvalidDevicePeriod = unchecked((int)0x88890020);
    public const int AudclntEResourcesInvalidated = unchecked((int)0x88890026);
    public const int AudclntSBufferEmpty = 0x08890001;
    public const int EAccessDenied = unchecked((int)0x80070005);
    public const int ENotFound = unchecked((int)0x80070490);

    public const uint StreamFlagAutoConvertPcm = 0x80000000u;
    public const uint StreamFlagSrcDefaultQuality = 0x08000000u;
    public const uint StreamFlagEventCallback = 0x00040000u;
    public const uint BufferFlagDataDiscontinuity = 0x1;
    public const uint BufferFlagSilent = 0x2;

    /// <summary>IMMDeviceEnumerator data-flow: eRender.</summary>
    public const int FlowRender = 0;

    /// <summary>IMMDeviceEnumerator data-flow: eCapture.</summary>
    public const int FlowCapture = 1;

    /// <summary>The role the system default is read for: eConsole.</summary>
    public const int RoleConsole = 0;

    public const uint DeviceStateActive = 0x1;

    /// <summary>The WASAPI data-flow value of a direction.</summary>
    public static int Flow(DeviceDirection direction) => direction == DeviceDirection.Output ? FlowRender : FlowCapture;

    /// <summary>The direction of a WASAPI data-flow value, or null for eAll and anything else.</summary>
    public static DeviceDirection? DirectionOf(int flow) => flow switch
    {
        FlowRender => DeviceDirection.Output,
        FlowCapture => DeviceDirection.Input,
        _ => null,
    };

    /// <summary>100-nanosecond units, which is how the API states durations.</summary>
    public static long ToReferenceTime(int milliseconds) => milliseconds * 10_000L;

    /// <summary>A frame count as a duration in 100-nanosecond units, rounded to nearest (the rounding the alignment rule in the documentation uses).</summary>
    public static long FramesToReferenceTime(int frames, int sampleRate) =>
        (long)((10_000_000.0 / sampleRate * frames) + 0.5);

    /// <summary>A duration in 100-nanosecond units as frames, rounded to nearest.</summary>
    public static int ReferenceTimeToFrames(long duration, int sampleRate) =>
        (int)Math.Min(int.MaxValue, ((duration * sampleRate) + 5_000_000L) / 10_000_000L);

    /// <summary>Sorts an HRESULT from a call on a running stream.</summary>
    public static WasapiFault Classify(int hresult)
    {
        if (hresult >= 0)
        {
            return WasapiFault.None;
        }

        return hresult switch
        {
            AudclntEDeviceInvalidated or AudclntEResourcesInvalidated or AudclntEServiceNotRunning
                or AudclntEEndpointCreateFailed or AudclntEBufferError or AudclntEOutOfOrder => WasapiFault.Lost,
            AudclntEBufferTooLarge => WasapiFault.Skip,
            _ => WasapiFault.Failed,
        };
    }

    /// <summary>Whether an HRESULT from a running stream means the endpoint is gone.</summary>
    public static bool IsDeviceLost(int hresult) => Classify(hresult) == WasapiFault.Lost;

    /// <summary>A short constant text for the engine's lost-device event.</summary>
    public static string LostReason(int hresult) => hresult switch
    {
        AudclntEDeviceInvalidated => "the audio device was removed or disabled",
        AudclntEResourcesInvalidated => "the audio device's resources were taken away",
        AudclntEServiceNotRunning => "the Windows audio service stopped",
        AudclntEEndpointCreateFailed => "the audio endpoint could not be used",
        _ => "the audio device failed",
    };

    /// <summary>The reason text of a refused open: a plain sentence and the HRESULT.</summary>
    public static string Describe(string what, int hresult)
    {
        string why = hresult switch
        {
            AudclntEDeviceInUse => " (the device is in use by another application in exclusive mode)",
            AudclntEExclusiveModeNotAllowed => " (the device does not allow exclusive mode: see the Advanced tab of its sound properties)",
            AudclntEExclusiveModeOnly => " (the device only works in exclusive mode)",
            AudclntEUnsupportedFormat => " (format not supported)",
            AudclntEDeviceInvalidated => " (the device was removed)",
            AudclntEServiceNotRunning => " (the Windows audio service is not running)",
            EAccessDenied => " (access denied: Windows privacy settings may block this application from the microphone)",
            ENotFound => " (no such device)",
            _ => string.Empty,
        };
        return $"{what}: 0x{hresult:X8}{why}";
    }

    /// <summary>The speaker-position bits for a channel count (ksmedia.h SPEAKER_*), 0 when unspecified.</summary>
    public static uint ChannelMask(int channels) => channels switch
    {
        1 => 0x4,   // front center
        2 => 0x3,   // front left, front right
        3 => 0x7,   // + front center
        4 => 0x33,  // front pair + back pair
        5 => 0x37,
        6 => 0x3F,  // 5.1
        7 => 0x13F, // 6.1
        8 => 0x63F, // 7.1
        _ => 0,
    };

    /// <summary>Bytes per sample of a layout.</summary>
    public static int BytesPerSample(WasapiSampleKind kind) => kind switch
    {
        WasapiSampleKind.Int16 => 2,
        WasapiSampleKind.Int24 => 3,
        _ => 4,
    };

    private static void Layout(WasapiSampleKind kind, out ushort container, out ushort valid, out bool isFloat)
    {
        isFloat = kind == WasapiSampleKind.Float32;
        container = (ushort)(BytesPerSample(kind) * 8);
        valid = kind switch
        {
            WasapiSampleKind.Int24 or WasapiSampleKind.Int24In32 => 24,
            _ => container,
        };
    }

    /// <summary>Fills a WAVE_FORMAT_EXTENSIBLE for interleaved PCM of the given layout.</summary>
    public static WaveFormatExtensible MakeFormat(WasapiSampleKind kind, int sampleRate, int channels)
    {
        Layout(kind, out ushort container, out ushort valid, out bool isFloat);
        ushort blockAlign = (ushort)(channels * container / 8);
        return new WaveFormatExtensible
        {
            FormatTag = 0xFFFE, // WAVE_FORMAT_EXTENSIBLE
            Channels = (ushort)channels,
            SamplesPerSec = (uint)sampleRate,
            AvgBytesPerSec = (uint)(sampleRate * blockAlign),
            BlockAlign = blockAlign,
            BitsPerSample = container,
            Size = 22,
            ValidBitsPerSample = valid,
            ChannelMask = ChannelMask(channels),
            // KSDATAFORMAT_SUBTYPE_IEEE_FLOAT {00000003-...} or KSDATAFORMAT_SUBTYPE_PCM {00000001-...}, both -0000-0010-8000-00AA00389B71
            SubFormat = new Guid(isFloat ? 3u : 1u, 0, 0x10, 0x80, 0, 0, 0xAA, 0, 0x38, 0x9B, 0x71),
        };
    }

    /// <summary>The layouts tried in exclusive mode, best first. Rate and channel count are never converted by the hardware path, so only the layout varies.</summary>
    public static ReadOnlySpan<WasapiSampleKind> ExclusiveCandidates =>
        [WasapiSampleKind.Float32, WasapiSampleKind.Int32, WasapiSampleKind.Int24In32, WasapiSampleKind.Int24, WasapiSampleKind.Int16];

    /// <summary>
    /// Exclusive-mode format negotiation: the first layout the device accepts at exactly the requested rate and channels, or false when none is.
    /// </summary>
    /// <param name="isSupported">Asks the device whether a layout works (IAudioClient::IsFormatSupported in exclusive mode returning S_OK).</param>
    /// <param name="kind">The layout to use.</param>
    public static bool NegotiateExclusive(Func<WasapiSampleKind, bool> isSupported, out WasapiSampleKind kind)
    {
        foreach (var candidate in ExclusiveCandidates)
        {
            if (isSupported(candidate))
            {
                kind = candidate;
                return true;
            }
        }

        kind = WasapiSampleKind.Float32;
        return false;
    }

    /// <summary>The wording of a refused exclusive negotiation.</summary>
    public static string ExclusiveRefusal(int sampleRate, int channels) =>
        $"exclusive mode needs a format the device takes as is, and it accepts none of float32, 32-, 24- or 16-bit integer at {sampleRate} Hz x {channels}";

    /// <summary>
    /// The buffer duration asked for in shared mode: <see cref="DeviceStreamOptions.BufferFrames"/> when set, else <see cref="DeviceStreamOptions.LatencyMilliseconds"/>.
    /// The audio engine raises it to its own minimum.
    /// </summary>
    public static long SharedDuration(DeviceStreamOptions options) =>
        options.BufferFrames > 0
            ? FramesToReferenceTime(options.BufferFrames, options.SampleRate)
            : ToReferenceTime(Math.Max(1, options.LatencyMilliseconds));

    /// <summary>
    /// The duration asked for in exclusive event-driven mode, where buffer and period must be equal: the period if set, else the buffer if set,
    /// else the latency, never below the device's minimum period.
    /// </summary>
    public static long ExclusiveDuration(DeviceStreamOptions options, long minimumPeriod)
    {
        long wanted = options.PeriodFrames > 0
            ? FramesToReferenceTime(options.PeriodFrames, options.SampleRate)
            : options.BufferFrames > 0
                ? FramesToReferenceTime(options.BufferFrames, options.SampleRate)
                : ToReferenceTime(Math.Max(1, options.LatencyMilliseconds));
        return Math.Max(wanted, minimumPeriod);
    }

    /// <summary>
    /// The aligned duration to retry with after AUDCLNT_E_BUFFER_SIZE_NOT_ALIGNED: the buffer size the device reported, as a duration
    /// (Microsoft's documented recipe for exclusive mode).
    /// </summary>
    public static long AlignedDuration(int alignedFrames, int sampleRate) => FramesToReferenceTime(alignedFrames, sampleRate);

    /// <summary>How many frames may be written now in shared mode: the free part of the buffer.</summary>
    public static int WritableFrames(int bufferFrames, int paddingFrames) => Math.Max(0, bufferFrames - paddingFrames);

    /// <summary>
    /// Turns an IAudioClock position into frames: position / frequency is seconds. Computed in 128 bits so a long-running stream cannot overflow.
    /// </summary>
    public static long PositionToFrames(ulong position, ulong frequency, int sampleRate)
    {
        if (frequency == 0)
        {
            return 0;
        }

        UInt128 frames = (UInt128)position * (ulong)sampleRate / frequency;
        return frames > long.MaxValue ? long.MaxValue : (long)frames;
    }

    /// <summary>Converts interleaved float samples to a device layout with round-to-nearest, clamping to full scale. Not-a-number becomes silence.</summary>
    /// <param name="source">Samples in -1..1.</param>
    /// <param name="kind">The layout to write.</param>
    /// <param name="destination">Little-endian bytes; must hold <c>source.Length * BytesPerSample(kind)</c>.</param>
    public static void FromFloat(ReadOnlySpan<float> source, WasapiSampleKind kind, Span<byte> destination)
    {
        int width = BytesPerSample(kind);
        destination = destination[..(source.Length * width)];
        switch (kind)
        {
            case WasapiSampleKind.Float32:
                for (int i = 0; i < source.Length; i++)
                {
                    BinaryPrimitives.WriteSingleLittleEndian(destination[(i * 4)..], source[i]);
                }

                break;
            case WasapiSampleKind.Int32:
                for (int i = 0; i < source.Length; i++)
                {
                    BinaryPrimitives.WriteInt32LittleEndian(destination[(i * 4)..], (int)Quantize(source[i], 2147483647.0));
                }

                break;
            case WasapiSampleKind.Int24In32:
                for (int i = 0; i < source.Length; i++)
                {
                    BinaryPrimitives.WriteInt32LittleEndian(destination[(i * 4)..], (int)Quantize(source[i], 8388607.0) << 8);
                }

                break;
            case WasapiSampleKind.Int24:
                for (int i = 0; i < source.Length; i++)
                {
                    int v = (int)Quantize(source[i], 8388607.0);
                    int o = i * 3;
                    destination[o] = (byte)v;
                    destination[o + 1] = (byte)(v >> 8);
                    destination[o + 2] = (byte)(v >> 16);
                }

                break;
            default:
                for (int i = 0; i < source.Length; i++)
                {
                    BinaryPrimitives.WriteInt16LittleEndian(destination[(i * 2)..], (short)Quantize(source[i], 32767.0));
                }

                break;
        }
    }

    /// <summary>Converts a device layout to interleaved float; integers are divided by their full-scale magnitude plus one, so -32768 becomes -1.</summary>
    /// <param name="source">Little-endian bytes of whole samples.</param>
    /// <param name="kind">The layout read.</param>
    /// <param name="destination">Receives <c>source.Length / BytesPerSample(kind)</c> samples.</param>
    public static void ToFloat(ReadOnlySpan<byte> source, WasapiSampleKind kind, Span<float> destination)
    {
        int width = BytesPerSample(kind);
        int count = source.Length / width;
        for (int i = 0; i < count; i++)
        {
            destination[i] = kind switch
            {
                WasapiSampleKind.Float32 => BinaryPrimitives.ReadSingleLittleEndian(source[(i * 4)..]),
                WasapiSampleKind.Int32 => BinaryPrimitives.ReadInt32LittleEndian(source[(i * 4)..]) * (1f / 2147483648f),
                WasapiSampleKind.Int24In32 => BinaryPrimitives.ReadInt32LittleEndian(source[(i * 4)..]) * (1f / 2147483648f),
                WasapiSampleKind.Int24 => ((source[(i * 3) + 2] << 24) | (source[(i * 3) + 1] << 16) | (source[i * 3] << 8)) * (1f / 2147483648f),
                _ => BinaryPrimitives.ReadInt16LittleEndian(source[(i * 2)..]) * (1f / 32768f),
            };
        }
    }

    private static double Quantize(float sample, double fullScale)
    {
        if (float.IsNaN(sample))
        {
            return 0;
        }

        return Math.Clamp(Math.Floor((sample * fullScale) + 0.5), -fullScale - 1, fullScale);
    }
}
