namespace OneAudio;

/// <summary>
/// The sample representation a source stores natively. Decoders always offer interleaved <see cref="float"/>;
/// this says what the data was before conversion, and which sources can also deliver exact integers.
/// </summary>
public enum NativeSampleType
{
    /// <summary>The source is lossy or transform-coded; there is no integer representation to preserve.</summary>
    Lossy = 0,
    /// <summary>Unsigned 8-bit PCM (as stored in WAV). Integer reads return it re-centred to -128..127.</summary>
    PcmUnsigned8 = 1,
    /// <summary>Signed 16-bit PCM.</summary>
    PcmSigned16 = 2,
    /// <summary>Signed 24-bit PCM. Integer reads return values in -8388608..8388607.</summary>
    PcmSigned24 = 3,
    /// <summary>Signed 32-bit PCM.</summary>
    PcmSigned32 = 4,
    /// <summary>IEEE 754 binary32 samples.</summary>
    IeeeFloat32 = 5,
    /// <summary>IEEE 754 binary64 samples.</summary>
    IeeeFloat64 = 6,
}

/// <summary>
/// Speaker positions as a bit mask. Bit values match the Microsoft <c>WAVE_FORMAT_EXTENSIBLE</c> channel mask,
/// so masks from WAV files pass through unchanged.
/// </summary>
[Flags]
public enum ChannelMask : uint
{
    /// <summary>No layout is known; channels are just numbered.</summary>
    None = 0,
    /// <summary>Front left.</summary>
    FrontLeft = 0x1,
    /// <summary>Front right.</summary>
    FrontRight = 0x2,
    /// <summary>Front centre.</summary>
    FrontCenter = 0x4,
    /// <summary>Low-frequency effects.</summary>
    LowFrequency = 0x8,
    /// <summary>Back left.</summary>
    BackLeft = 0x10,
    /// <summary>Back right.</summary>
    BackRight = 0x20,
    /// <summary>Front left of centre.</summary>
    FrontLeftOfCenter = 0x40,
    /// <summary>Front right of centre.</summary>
    FrontRightOfCenter = 0x80,
    /// <summary>Back centre.</summary>
    BackCenter = 0x100,
    /// <summary>Side left.</summary>
    SideLeft = 0x200,
    /// <summary>Side right.</summary>
    SideRight = 0x400,
}

/// <summary>
/// Describes the interleaved audio a decoder produces: sample rate, channel count and layout, and native sample type.
/// A "frame" throughout the library is one sample for every channel; a buffer of N frames holds N * Channels samples.
/// </summary>
/// <param name="SampleRate">Frames per second, in Hz. Always positive.</param>
/// <param name="Channels">Channels per frame. Always positive.</param>
/// <param name="ChannelMask">Speaker positions, or <see cref="ChannelMask.None"/> when unknown. When non-zero, its bit count normally equals <paramref name="Channels"/>; channels are ordered by ascending bit.</param>
/// <param name="NativeType">How the source stores samples.</param>
/// <param name="BitsPerSample">Container width of one native sample in bits (0 for lossy sources).</param>
public readonly record struct AudioFormat(
    int SampleRate,
    int Channels,
    ChannelMask ChannelMask = ChannelMask.None,
    NativeSampleType NativeType = NativeSampleType.Lossy,
    int BitsPerSample = 0)
{
    /// <summary>Bytes in one native frame, or 0 for lossy sources.</summary>
    public int NativeBytesPerFrame => Channels * (BitsPerSample / 8);

    /// <summary>
    /// The conventional speaker layout for a channel count (1: centre, 2: left/right, 4: quad, 6: 5.1, 8: 7.1),
    /// or <see cref="ChannelMask.None"/> for counts without a convention.
    /// </summary>
    /// <param name="channels">Channel count.</param>
    public static ChannelMask DefaultMask(int channels) => channels switch
    {
        1 => ChannelMask.FrontCenter,
        2 => ChannelMask.FrontLeft | ChannelMask.FrontRight,
        3 => ChannelMask.FrontLeft | ChannelMask.FrontRight | ChannelMask.FrontCenter,
        4 => ChannelMask.FrontLeft | ChannelMask.FrontRight | ChannelMask.BackLeft | ChannelMask.BackRight,
        6 => ChannelMask.FrontLeft | ChannelMask.FrontRight | ChannelMask.FrontCenter | ChannelMask.LowFrequency
             | ChannelMask.BackLeft | ChannelMask.BackRight,
        8 => ChannelMask.FrontLeft | ChannelMask.FrontRight | ChannelMask.FrontCenter | ChannelMask.LowFrequency
             | ChannelMask.BackLeft | ChannelMask.BackRight | ChannelMask.SideLeft | ChannelMask.SideRight,
        _ => ChannelMask.None,
    };
}
