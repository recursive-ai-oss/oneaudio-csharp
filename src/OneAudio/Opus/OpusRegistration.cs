using OneAudio.Opus;

namespace OneAudio;

/// <summary>Registration helper for Opus.</summary>
public static class OpusRegistryExtensions
{
    /// <summary>Registers the Opus decoder, which reads Ogg Opus streams (RFC 7845).</summary>
    /// <param name="registry">The registry.</param>
    /// <returns><paramref name="registry"/>, for chaining.</returns>
    public static AudioCodecRegistry AddOpus(this AudioCodecRegistry registry)
    {
        ArgumentNullException.ThrowIfNull(registry);
        return registry.Add(OpusCodec.Instance);
    }
}
