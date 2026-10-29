using OneAudio.Amr;

namespace OneAudio;

/// <summary>Registration of the AMR file container.</summary>
public static class AmrRegistryExtensions
{
    /// <summary>
    /// Registers AMR and AMR-WB storage-format files. Opening also needs an AMR codec; without one it throws
    /// <see cref="UnsupportedAudioFormatException"/>.
    /// </summary>
    /// <param name="registry">The registry to add to.</param>
    /// <returns><paramref name="registry"/>, for chaining.</returns>
    public static AudioCodecRegistry AddAmr(this AudioCodecRegistry registry)
    {
        ArgumentNullException.ThrowIfNull(registry);
        return registry.Add(AmrFormat.Instance);
    }
}
