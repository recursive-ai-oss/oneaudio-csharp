using OneAudio.Vorbis;

namespace OneAudio;

/// <summary>Registration helper for Vorbis.</summary>
public static class VorbisRegistryExtensions
{
    /// <summary>Registers the Vorbis I decoder.</summary>
    /// <param name="registry">The registry.</param>
    /// <returns><paramref name="registry"/>, for chaining.</returns>
    public static AudioCodecRegistry AddVorbis(this AudioCodecRegistry registry)
    {
        ArgumentNullException.ThrowIfNull(registry);
        return registry.Add(VorbisCodec.Instance);
    }
}
