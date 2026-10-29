using OneAudio.Mp4;

namespace OneAudio;

/// <summary>Registration of the MP4 / M4A / 3GP container.</summary>
public static class Mp4RegistryExtensions
{
    /// <summary>
    /// Registers the MP4, M4A and 3GP container. It only frames packets; opening a file also needs a codec registered for what the
    /// track holds (AAC, AMR), otherwise opening throws <see cref="UnsupportedAudioFormatException"/>.
    /// </summary>
    /// <param name="registry">The registry to add to.</param>
    /// <returns><paramref name="registry"/>, for chaining.</returns>
    public static AudioCodecRegistry AddMp4(this AudioCodecRegistry registry)
    {
        ArgumentNullException.ThrowIfNull(registry);
        return registry.Add(Mp4Format.Instance);
    }
}
