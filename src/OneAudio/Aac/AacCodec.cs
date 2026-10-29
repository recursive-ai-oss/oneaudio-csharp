namespace OneAudio.Aac;

/// <summary>The AAC-LC codec for the registry; see <see cref="AacDecoder"/> for what it decodes.</summary>
public sealed class AacCodec : IAudioCodec
{
    /// <inheritdoc />
    public CodecId Id => CodecId.Aac;

    /// <inheritdoc />
    public IAudioDecoder CreateDecoder(IPacketSource packets, AudioLimits limits)
    {
        ArgumentNullException.ThrowIfNull(packets);
        ArgumentNullException.ThrowIfNull(limits);
        return new AacDecoder(packets, limits);
    }
}
