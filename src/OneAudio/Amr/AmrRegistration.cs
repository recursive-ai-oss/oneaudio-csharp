using OneAudio.Amr;

namespace OneAudio;

/// <summary>Registration of the AMR file container.</summary>
public static class AmrRegistryExtensions
{
    /// <summary>
    /// Registers AMR and AMR-WB storage-format files together with the AMR and AMR-WB speech decoders (also
    /// reachable one by one through <see cref="AmrCodecRegistryExtensions"/>), so 3GP <c>samr</c>/<c>sawb</c> tracks
    /// decode when the MP4 container is registered too.
    /// </summary>
    /// <param name="registry">The registry to add to.</param>
    /// <returns><paramref name="registry"/>, for chaining.</returns>
    public static AudioCodecRegistry AddAmr(this AudioCodecRegistry registry)
    {
        ArgumentNullException.ThrowIfNull(registry);
        return registry.Add(AmrFormat.Instance).Add(AmrNbCodec.Instance).Add(AmrWbCodec.Instance);
    }
}
