namespace OneAudio.Opus;

/// <summary>What the encoder is tuned for. The values follow the Opus application constants (RFC 6716 section 2.1).</summary>
public enum OpusApplication
{
    /// <summary>General audio (music, mixed content). Default.</summary>
    Audio = 2049,

    /// <summary>Speech: applies a high-pass filter and favours low-rate speech modes.</summary>
    Voip = 2048,

    /// <summary>Lowest delay: no look-ahead, always the CELT mode.</summary>
    RestrictedLowDelay = 2051,
}

/// <summary>Audio bandwidth coded by a packet (RFC 6716 table 1). The value is the audio bandwidth's upper edge.</summary>
public enum OpusBandwidth
{
    /// <summary>Chosen by the encoder from the bitrate.</summary>
    Auto = -1000,

    /// <summary>4 kHz.</summary>
    Narrowband = 1101,

    /// <summary>6 kHz.</summary>
    Mediumband = 1102,

    /// <summary>8 kHz.</summary>
    Wideband = 1103,

    /// <summary>12 kHz.</summary>
    SuperWideband = 1104,

    /// <summary>20 kHz.</summary>
    Fullband = 1105,
}

/// <summary>Hint about the signal type, steering mode and bandwidth choice.</summary>
public enum OpusSignal
{
    /// <summary>Detected from the application setting.</summary>
    Auto = -1000,

    /// <summary>Speech.</summary>
    Voice = 3001,

    /// <summary>Music.</summary>
    Music = 3002,
}

/// <summary>How the encoder trades packet size against quality.</summary>
public enum OpusBitrateMode
{
    /// <summary>Variable bitrate: packet sizes follow the signal and average to the target. Default.</summary>
    Vbr,

    /// <summary>Variable bitrate with a bit reservoir: instantaneous rate stays near the target.</summary>
    ConstrainedVbr,

    /// <summary>Constant bitrate: every packet has the same size (padded where needed).</summary>
    Cbr,
}

/// <summary>Settings of an <see cref="OpusEncoder"/>.</summary>
public sealed record OpusEncoderSettings
{
    /// <summary>Use for <see cref="BitrateBps"/> to let the encoder choose from the frame size and channel count.</summary>
    public const int AutoBitrate = 0;

    /// <summary>Use for <see cref="BitrateBps"/> to let packets grow to the output buffer size.</summary>
    public const int MaxBitrate = -1;

    /// <summary>Input sample rate in Hz. Only 48000 is accepted by the packet-level encoder; resample first otherwise.</summary>
    public int SampleRate { get; init; } = 48000;

    /// <summary>Input channels, 1 or 2.</summary>
    public int Channels { get; init; } = 2;

    /// <summary>Tuning target. Default <see cref="OpusApplication.Audio"/>.</summary>
    public OpusApplication Application { get; init; } = OpusApplication.Audio;

    /// <summary>
    /// Target bitrate in bits per second for all channels: <see cref="AutoBitrate"/> (the default), <see cref="MaxBitrate"/>,
    /// or 500 to 300000 per channel.
    /// </summary>
    public int BitrateBps { get; init; } = AutoBitrate;

    /// <summary>Rate control. Default <see cref="OpusBitrateMode.Vbr"/>.</summary>
    public OpusBitrateMode BitrateMode { get; init; } = OpusBitrateMode.Vbr;

    /// <summary>Effort from 0 (fastest) to 10 (best). Default 10.</summary>
    public int Complexity { get; init; } = 10;

    /// <summary>Bandwidth to code, or <see cref="OpusBandwidth.Auto"/> (the default).</summary>
    public OpusBandwidth Bandwidth { get; init; } = OpusBandwidth.Auto;

    /// <summary>Highest bandwidth the automatic choice may use. Default <see cref="OpusBandwidth.Fullband"/>.</summary>
    public OpusBandwidth MaxBandwidth { get; init; } = OpusBandwidth.Fullband;

    /// <summary>Signal type hint. Default <see cref="OpusSignal.Auto"/>.</summary>
    public OpusSignal Signal { get; init; } = OpusSignal.Auto;

    /// <summary>Forces a channel count in the packets (1 codes a stereo input as mono), or 0 for automatic. Default 0.</summary>
    public int ForceChannels { get; init; }

    /// <summary>Expected packet loss in percent (0 to 100), which makes the encoder more robust. Default 0.</summary>
    public int PacketLossPercent { get; init; }
}
