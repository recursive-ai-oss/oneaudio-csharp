// Copyright (c) Recursive AI, LLC. MIT OR Apache-2.0.
using OneAudio.Amr;

namespace OneAudio;

/// <summary>Registration helpers for the AMR speech codecs.</summary>
public static class AmrCodecRegistryExtensions
{
    /// <summary>
    /// Registers the AMR (narrowband, 8 kHz) decoder, which decodes packets of <see cref="CodecId.AmrNb"/> from any
    /// container that supplies them.
    /// </summary>
    /// <param name="registry">The registry.</param>
    /// <returns><paramref name="registry"/>, for chaining.</returns>
    public static AudioCodecRegistry AddAmrNb(this AudioCodecRegistry registry)
    {
        ArgumentNullException.ThrowIfNull(registry);
        return registry.Add(AmrNbCodec.Instance);
    }

    /// <summary>
    /// Registers the AMR-WB (wideband, 16 kHz) decoder, which decodes packets of <see cref="CodecId.AmrWb"/> from any
    /// container that supplies them.
    /// </summary>
    /// <param name="registry">The registry.</param>
    /// <returns><paramref name="registry"/>, for chaining.</returns>
    public static AudioCodecRegistry AddAmrWb(this AudioCodecRegistry registry)
    {
        ArgumentNullException.ThrowIfNull(registry);
        return registry.Add(AmrWbCodec.Instance);
    }
}
