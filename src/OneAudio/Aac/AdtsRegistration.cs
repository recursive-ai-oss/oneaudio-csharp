using OneAudio.Aac;

namespace OneAudio;

/// <summary>Registration of the ADTS container.</summary>
public static class AdtsRegistryExtensions
{
    /// <summary>
    /// Registers raw AAC (ADTS) files. Register it before MP3 so a tagged AAC file is not taken for MP3. Opening also needs an AAC
    /// codec; without one it throws <see cref="UnsupportedAudioFormatException"/>.
    /// </summary>
    /// <param name="registry">The registry to add to.</param>
    /// <returns><paramref name="registry"/>, for chaining.</returns>
    public static AudioCodecRegistry AddAdts(this AudioCodecRegistry registry)
    {
        ArgumentNullException.ThrowIfNull(registry);
        return registry.Add(AdtsFormat.Instance);
    }
}
