using OneAudio.Ogg;

namespace OneAudio;

/// <summary>Registration helper for Ogg.</summary>
public static class OggRegistryExtensions
{
    /// <summary>Registers Ogg container reading. Codecs for what it carries are registered separately (for example <c>AddVorbis</c>).</summary>
    /// <param name="registry">The registry.</param>
    /// <returns><paramref name="registry"/>, for chaining.</returns>
    public static AudioCodecRegistry AddOgg(this AudioCodecRegistry registry)
    {
        ArgumentNullException.ThrowIfNull(registry);
        return registry.Add(OggFormat.Instance);
    }
}
