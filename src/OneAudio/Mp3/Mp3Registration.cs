using OneAudio.Mp3;

namespace OneAudio;

/// <summary>Registration helper for MP3.</summary>
public static class Mp3RegistryExtensions
{
    /// <summary>Registers MP3 (MPEG-1, MPEG-2 and MPEG-2.5 Layer III) decoding.</summary>
    /// <param name="registry">The registry.</param>
    /// <returns><paramref name="registry"/>, for chaining.</returns>
    public static AudioCodecRegistry AddMp3(this AudioCodecRegistry registry)
    {
        ArgumentNullException.ThrowIfNull(registry);
        return registry.Add(Mp3Format.Instance);
    }
}
