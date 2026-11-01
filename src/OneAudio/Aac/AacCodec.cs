namespace OneAudio.Aac;

/// <summary>The AAC-LC codec for the registry; see <see cref="AacDecoder"/> for what it decodes.</summary>
public sealed class AacCodec : IAudioCodec
{
    /// <summary>
    /// Whether decoders look into the first access units for SBR and parametric stereo that the config does not announce;
    /// see <see cref="AacDecoder(IPacketSource, AudioLimits?, bool)"/>. Default true.
    /// </summary>
    public bool ProbeAtOpen { get; init; } = true;

    /// <inheritdoc />
    public CodecId Id => CodecId.Aac;

    /// <inheritdoc />
    public IAudioDecoder CreateDecoder(IPacketSource packets, AudioLimits limits)
    {
        ArgumentNullException.ThrowIfNull(packets);
        ArgumentNullException.ThrowIfNull(limits);
        return new AacDecoder(packets, limits, ProbeAtOpen);
    }
}
