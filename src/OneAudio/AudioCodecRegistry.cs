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

    /// <summary>
    /// Finds the registered container that recognises the bytes. Formats identified by a signature at the start of the file (RIFF/WAVE,
    /// OggS, fLaC, an ISO BMFF box, the AMR magic, an ADTS header, with or without an ID3v2 tag in front) are asked first, in registration
    /// order; formats that only find a frame pattern somewhere in the bytes (see <see cref="IContainerFormat.HasWeakSignature"/>, which
    /// raw MP3 sets) are asked afterwards, also in registration order. So registration order never lets a pattern match steal a file
    /// that has a real signature, and only registered formats can be returned.
    /// </summary>
    /// <param name="head">Leading bytes of a stream.</param>
    /// <returns>The format, or null when none accepts the bytes.</returns>
    public IContainerFormat? Detect(ReadOnlySpan<byte> head)
    {
        foreach (var c in _containers)
            if (!c.HasWeakSignature && c.Matches(head)) return c;
        foreach (var c in _containers)
            if (c.HasWeakSignature && c.Matches(head)) return c;
        return null;
    }

    /// <summary>
    /// Finds the registered container format that lists <paramref name="extension"/> among its <see cref="IContainerFormat.Extensions"/>.
    /// Content sniffing with <see cref="Detect"/> is more reliable; this is the fallback for files whose start does not identify them.
    /// </summary>
    /// <param name="extension">A file extension with or without the leading dot, compared ignoring case.</param>
    /// <returns>The first matching format, or null.</returns>
    public IContainerFormat? DetectByExtension(string? extension)
    {
        if (string.IsNullOrEmpty(extension)) return null;
        ReadOnlySpan<char> ext = extension.AsSpan().TrimStart('.');
        if (ext.IsEmpty) return null;
        foreach (var c in _containers)
            foreach (var e in c.Extensions)
                if (e.AsSpan().TrimStart('.').Equals(ext, StringComparison.OrdinalIgnoreCase)) return c;
        return null;
    }
}
