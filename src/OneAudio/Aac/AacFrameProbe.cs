namespace OneAudio.Aac;

/// <summary>What <see cref="AacFrameProbe.Probe"/> found at the start of the data.</summary>
public enum AacProbeResult
{
    /// <summary>The data starts with a complete raw_data_block; its length is reported.</summary>
    Frame,
    /// <summary>The data is well-formed so far but ends before the block's end element: supply more bytes and probe again.</summary>
    Incomplete,
    /// <summary>The data does not start with a raw_data_block of this stream (a bad field before the data ran out, or a tool this library cannot parse).</summary>
    NotAac,
}

/// <summary>
/// Finds where one AAC raw_data_block (ISO/IEC 14496-3 4.4.2.1) ends, for a demuxer that has to frame an unframed AAC stream.
/// It runs the decoder's own syntax parser over the bytes up to the end element (<c>ID_END</c>), without dequantization or the
/// transform, so the answer is exact rather than guessed: the block is a self-delimiting bit string, followed by padding to a
/// byte boundary.
/// <para>Create one probe per stream from its AudioSpecificConfig (it builds the parser's tables once) and call
/// <see cref="Probe"/> as often as needed; after construction a call allocates nothing and never throws on bad data.
/// The scope is the decoder's: AAC-LC, mono or stereo or a program-config layout of front elements, 8-48 kHz, 1024-sample
/// frames. Not thread-safe.</para>
/// </summary>
public sealed class AacFrameProbe
{
    private readonly AacFrameDecoder _parser;

    /// <summary>Creates a probe for a stream with the given configuration.</summary>
    /// <param name="config">The stream's AudioSpecificConfig.</param>
    /// <exception cref="ArgumentNullException"><paramref name="config"/> is null.</exception>
    /// <exception cref="UnsupportedAudioFormatException">The configuration is outside what the decoder reads (<see cref="AacAudioConfig.CheckSupported"/>).</exception>
    public AacFrameProbe(AacAudioConfig config)
    {
        ArgumentNullException.ThrowIfNull(config);
        config.CheckSupported();
        _parser = new AacFrameDecoder(config);
    }

    /// <summary>Creates a probe from the bytes of an AudioSpecificConfig.</summary>
    /// <param name="audioSpecificConfig">The config exactly as a container stores it.</param>
    /// <exception cref="AudioFormatException">The config is invalid.</exception>
    /// <exception cref="UnsupportedAudioFormatException">The configuration is outside what the decoder reads.</exception>
    public AacFrameProbe(ReadOnlySpan<byte> audioSpecificConfig) : this(AacAudioConfig.Parse(audioSpecificConfig))
    {
    }

    /// <summary>Measures the raw_data_block at the start of <paramref name="data"/>.</summary>
    /// <param name="data">Raw AAC bytes starting at a block boundary (no ADTS header); may extend past the block.</param>
    /// <param name="length">The block's length in bytes, counting the padding after the end element, when the result is
    /// <see cref="AacProbeResult.Frame"/>; otherwise 0.</param>
    /// <returns><see cref="AacProbeResult.Frame"/>, <see cref="AacProbeResult.Incomplete"/> when more bytes are needed, or
    /// <see cref="AacProbeResult.NotAac"/>. Empty data is <see cref="AacProbeResult.Incomplete"/>.</returns>
    public AacProbeResult Probe(ReadOnlySpan<byte> data, out int length) => _parser.Probe(data, out length);
}
