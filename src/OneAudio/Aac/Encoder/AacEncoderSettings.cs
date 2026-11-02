namespace OneAudio.Aac;

/// <summary>How <see cref="AacEncoder"/> spends bits.</summary>
public enum AacRateControl
{
    /// <summary>
    /// Constant bit rate: every block is sized from the nominal rate and a bit reservoir (blocks that need more may borrow up to
    /// the standard's 6144 bits per channel). The long-term average equals <see cref="AacEncoderSettings.Bitrate"/> from below:
    /// when the reservoir is full, padding is written as fill elements.
    /// </summary>
    Cbr = 0,

    /// <summary>
    /// Average bit rate: block sizes follow the material's perceptual entropy, steered by a reservoir of about eight blocks that is
    /// not bounded by the standard's per-block limit (the stream makes no constant-rate promise), so sizes vary much more than in
    /// <see cref="Cbr"/> while the file still averages <see cref="AacEncoderSettings.Bitrate"/> (within a few percent on any
    /// material; never above it by more than the last block, below it only by what the reservoir holds at the end).
    /// </summary>
    Abr = 1,

    /// <summary>
    /// Variable bit rate: each block gets bits in proportion to its perceptual entropy relative to the running mean, scaled by
    /// <see cref="AacEncoderSettings.VbrQuality"/>. <see cref="AacEncoderSettings.Bitrate"/> is the rate quality 0.5 aims at on typical
    /// music; it also sizes the bandwidth and the limits. The average depends on the material.
    /// </summary>
    Vbr = 2,
}

/// <summary>Settings of an <see cref="AacEncoder"/>. All properties are init-only; the defaults give constant bit rate at 64 kbit/s per channel with an automatic bandwidth.</summary>
public sealed record AacEncoderSettings
{
    /// <summary>
    /// Target bits per second for the whole stream (all channels). Default 0: 64000 per channel, lowered at low sampling rates to half
    /// of what the format allows there (for example 48000 for stereo at 8000 Hz). For <see cref="AacRateControl.Vbr"/> it is the
    /// nominal rate that sets the bandwidth and the floor and ceiling of block sizes. Must give at least 8000 bit/s per channel and
    /// no more than 90 % of what 6144 bits per channel per block allows at the sampling rate.
    /// </summary>
    public int Bitrate { get; init; }

    /// <summary>Rate control mode. Default <see cref="AacRateControl.Cbr"/>.</summary>
    public AacRateControl RateControl { get; init; } = AacRateControl.Cbr;

    /// <summary>For <see cref="AacRateControl.Vbr"/>: quality from 0 to 1; each 0.5 step doubles the bits of a block. Default 0.5.</summary>
    public double VbrQuality { get; init; } = 0.5;

    /// <summary>Low-pass frequency in Hz; 0 (default) picks one from the bit rate per channel (about 14 kHz at 64 kbit/s stereo, 16.5 kHz at 96, 18 kHz at 128, 20 kHz at 192). Limited to the Nyquist frequency.</summary>
    public int Bandwidth { get; init; }

    /// <summary>Allow mid/side coding per band in stereo (default true).</summary>
    public bool MidSide { get; init; } = true;

    /// <summary>
    /// Replace bands above 4 kHz that would otherwise decode as silence by noise of the same energy (perceptual noise substitution,
    /// ISO/IEC 14496-3 4.5.2.3.4; default true). It keeps the spectrum free of holes at low bit rates. Decoders must support PNS, as
    /// every AAC-LC decoder does.
    /// </summary>
    public bool NoiseSubstitution { get; init; } = true;

    /// <summary>Allow eight-window short blocks at transients (default true). Off, attacks smear (pre-echo) but every block is long.</summary>
    public bool ShortBlocks { get; init; } = true;

    /// <summary>
    /// Prefix every access unit with a 7-byte ADTS header (ISO/IEC 14496-3 1.A.2, MPEG-4 ID, no CRC, buffer fullness 0x7FF) so the
    /// packets concatenate into a playable <c>.aac</c> file. Default false: raw access units for MP4.
    /// </summary>
    public bool Adts { get; init; }
}

/// <summary>One AAC access unit (a single raw_data_block, or ADTS-framed when <see cref="AacEncoderSettings.Adts"/> is set).</summary>
/// <param name="Data">The bytes. Valid until the next call that encodes on the encoder that produced it.</param>
/// <param name="Index">Zero-based access unit number.</param>
/// <param name="Timestamp">Decode time of the unit's first output sample in sampling-rate ticks on the media timeline: <c>Index * 1024</c>. The first <see cref="AacEncoder.PrimingFrames"/> output samples are priming.</param>
/// <param name="Duration">Output samples the unit covers: 1024.</param>
public readonly record struct AacPacket(ReadOnlyMemory<byte> Data, long Index, long Timestamp, int Duration);
