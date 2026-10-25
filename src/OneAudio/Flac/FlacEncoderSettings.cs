namespace OneAudio.Flac;

/// <summary>
/// Options for <see cref="FlacEncoder"/>. Every value is validated when the encoder is created. The defaults give the
/// compression of level 5 with the streamable subset, a 4096-frame block and 16-bit output for float or lossy input.
/// </summary>
public sealed record FlacEncoderSettings
{
    /// <summary>
    /// Bits per sample of the FLAC stream, 4 to 32. 0 (the default) takes the container width of the format's integer PCM
    /// type, or 16 when the format is floating-point or lossy. Float input is rounded to this depth (see <see cref="Dither"/>).
    /// </summary>
    public int BitsPerSample { get; init; }

    /// <summary>
    /// Speed against size, 0 (fastest) to 8 (smallest). Levels 0 to 2 use fixed predictors only with 1152-frame blocks; 3 to 8 add
    /// linear prediction on 4096-frame blocks (maximum order 6, 8, 8, 8, 12, 12 for levels 3 to 8). Stereo input is decorrelated from
    /// level 1 (left/side, right/side or mid/side per frame, from an estimate at levels 1 and 4, by encoding every mode at the others).
    /// Levels 6 and up analyse several windows, level 7 and 8 try the best two and three orders per window, and level 8 adds more
    /// windows. Default 5.
    /// </summary>
    public int CompressionLevel { get; init; } = 5;

    /// <summary>Frames per block, 16 to 65535. 0 (default) uses the level's size. The last block of a stream may be shorter.</summary>
    public int BlockSize { get; init; }

    /// <summary>Largest LPC order, 0 to 32 (0 means fixed predictors only). -1 (default) uses the level's order.</summary>
    public int MaxLpcOrder { get; init; } = -1;

    /// <summary>Largest Rice partition order, 0 to 15. -1 (default) uses the level's order.</summary>
    public int MaxPartitionOrder { get; init; } = -1;

    /// <summary>Quantized LPC coefficient precision in bits, 5 to 15. 0 (default) picks it from the block size.</summary>
    public int QlpPrecision { get; init; }

    /// <summary>
    /// Keep the settings inside the streamable subset of RFC 9639 section 7 (default true): blocks of at most 16384 frames (4608 up to
    /// 48 kHz), LPC order at most 12 up to 48 kHz, Rice partition order at most 8. Settings that break it throw when true. A bit
    /// depth or sample rate that has no frame-header code (such as 4 or 17 bits) is written as a reference to STREAMINFO, as the format
    /// requires, and a channel layout that is not one of the format's own gets a channel-mask comment; neither can be avoided, and
    /// both put the stream outside the subset whatever this setting says.
    /// </summary>
    public bool Subset { get; init; } = true;

    /// <summary>Add triangular (TPDF) dither of one LSB when float input is rounded to <see cref="BitsPerSample"/>. Default false.</summary>
    public bool Dither { get; init; }

    /// <summary>Seed of the dither generator; must not be 0. Default 1. The same seed and input give the same output.</summary>
    public uint DitherSeed { get; init; } = 1;

    /// <summary>
    /// Total frames the stream will hold, or -1 if unknown (default). A seekable output is patched with the true count at
    /// <see cref="FlacEncoder.Finish"/>, so this only matters for non-seekable outputs, where STREAMINFO is written once
    /// at the start: it carries this count, and <see cref="FlacEncoder.Finish"/> throws if fewer or more frames were written.
    /// </summary>
    public long TotalFrames { get; init; } = -1;

    /// <summary>Slots reserved for a SEEKTABLE when the output is seekable, 0 to 1024 (default 64; 0 writes none). Non-seekable outputs get none.</summary>
    public int SeekPoints { get; init; } = 64;

    /// <summary>VORBIS_COMMENT entries as <c>NAME=value</c> strings (the name uses printable ASCII other than '='). Null or empty writes an empty comment block.</summary>
    public IReadOnlyList<string>? Comments { get; init; }

    /// <summary>The vendor string of the comment block. Null (default) uses "RecursiveAI.OneAudio".</summary>
    public string? Vendor { get; init; }
}
