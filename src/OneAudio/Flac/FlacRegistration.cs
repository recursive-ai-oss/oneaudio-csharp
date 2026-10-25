using OneAudio.Flac;

namespace OneAudio;

/// <summary>Registration helper for FLAC.</summary>
public static class FlacRegistryExtensions
{
    /// <summary>
    /// Registers native <c>.flac</c> files and the FLAC codec for Ogg FLAC. Ogg itself is registered separately with
    /// <c>AddOgg</c>.
    /// </summary>
    /// <param name="registry">The registry.</param>
    /// <returns><paramref name="registry"/>, for chaining.</returns>
    public static AudioCodecRegistry AddFlac(this AudioCodecRegistry registry)
    {
        ArgumentNullException.ThrowIfNull(registry);
        return registry.Add(FlacFormat.Instance).Add(FlacCodec.Instance);
    }
}
