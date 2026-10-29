using OneAudio.Aac;

namespace OneAudio;

/// <summary>Registration of the AAC-LC decoder.</summary>
public static class AacRegistryExtensions
{
    /// <summary>
    /// Registers the AAC-LC decoder (mono and stereo, 8-48 kHz). It decodes the packets that
    /// <see cref="Mp4RegistryExtensions.AddMp4"/> and <see cref="AdtsRegistryExtensions.AddAdts"/> deliver; those containers
    /// must be registered too to open files.
    /// </summary>
    /// <param name="registry">The registry to add to.</param>
    /// <returns><paramref name="registry"/>, for chaining.</returns>
    public static AudioCodecRegistry AddAac(this AudioCodecRegistry registry)
    {
        ArgumentNullException.ThrowIfNull(registry);
        return registry.Add(new AacCodec());
    }
}
