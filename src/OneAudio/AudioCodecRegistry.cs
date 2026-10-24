namespace OneAudio;

/// <summary>
/// The set of container formats and codecs an application opts into. Nothing is registered by default and nothing
/// is found by reflection, so a published application only carries the formats it names. Build the registry once at
/// start-up (for example <c>new AudioCodecRegistry().AddWav()</c>) and share it; it is safe for concurrent use
/// once nobody is adding to it.
/// </summary>
public sealed class AudioCodecRegistry
{
    private readonly List<IContainerFormat> _containers = [];
    private readonly List<IAudioCodec> _codecs = [];

    /// <summary>How many leading bytes <see cref="IContainerFormat.Matches"/> may inspect. 512.</summary>
    public const int SniffBytes = 512;

    /// <summary>Registers a container format. Earlier registrations are tried first.</summary>
    /// <param name="format">The format.</param>
    /// <returns>This registry, for chaining.</returns>
    public AudioCodecRegistry Add(IContainerFormat format)
    {
        ArgumentNullException.ThrowIfNull(format);
        _containers.Add(format);
        return this;
    }

    /// <summary>Registers a codec. A second codec with the same id replaces the first.</summary>
    /// <param name="codec">The codec.</param>
    /// <returns>This registry, for chaining.</returns>
    public AudioCodecRegistry Add(IAudioCodec codec)
    {
        ArgumentNullException.ThrowIfNull(codec);
        _codecs.RemoveAll(c => c.Id == codec.Id);
        _codecs.Add(codec);
        return this;
    }

    /// <summary>Finds the registered codec for an id.</summary>
    /// <param name="id">Codec id from a container.</param>
    /// <returns>The codec.</returns>
    /// <exception cref="UnsupportedAudioFormatException">No such codec is registered.</exception>
    public IAudioCodec GetCodec(CodecId id)
    {
        foreach (var c in _codecs)
            if (c.Id == id) return c;
        throw new UnsupportedAudioFormatException($"No codec registered for '{id}'.");
    }

    /// <summary>Finds the first registered container whose <see cref="IContainerFormat.Matches"/> accepts the bytes, or null.</summary>
    /// <param name="head">Leading bytes of a stream.</param>
    public IContainerFormat? Detect(ReadOnlySpan<byte> head)
    {
        foreach (var c in _containers)
            if (c.Matches(head)) return c;
        return null;
    }
}
