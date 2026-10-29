namespace OneAudio.Mp4;

/// <summary>
/// The fields of an MPEG-4 AudioSpecificConfig (ISO/IEC 14496-3 1.6.2.1) that identify the stream. Only these leading fields
/// are read; everything after them belongs to the AAC decoder.
/// </summary>
/// <param name="ObjectType">Audio object type, for example 2 for AAC-LC, 5 for SBR, 29 for PS, 1 for AAC Main.</param>
/// <param name="SampleRate">Sampling rate in Hz of the core codec.</param>
/// <param name="Channels">Channels from channelConfiguration (1-6, 7 means 8); 0 when the layout is in an in-band program config element.</param>
/// <param name="ExtensionSampleRate">The explicitly signalled output rate when the config announces SBR (object type 5 or 29), else 0.</param>
public readonly record struct AacConfigInfo(int ObjectType, int SampleRate, int Channels, int ExtensionSampleRate)
{
    private static readonly int[] Rates = [96000, 88200, 64000, 48000, 44100, 32000, 24000, 22050, 16000, 12000, 11025, 8000, 7350];

    internal static int RateFromIndex(int index) => Rates[index];

    /// <summary>Reads the leading fields of an AudioSpecificConfig.</summary>
    /// <param name="config">The config bytes exactly as stored in the container.</param>
    /// <param name="info">The fields.</param>
    /// <returns>False when the bytes are too short or name a reserved value.</returns>
    public static bool TryParse(ReadOnlySpan<byte> config, out AacConfigInfo info)
    {
        info = default;
        int pos = 0;
        if (!TryObjectType(config, ref pos, out int aot)) return false;
        if (!TryRate(config, ref pos, out int rate)) return false;
        if (!TryBits(config, ref pos, 4, out int chan)) return false;
        int ext = 0;
        if (aot is 5 or 29)
        {
            if (!TryRate(config, ref pos, out ext)) return false;
            if (!TryObjectType(config, ref pos, out _)) return false;
        }
        info = new AacConfigInfo(aot, rate, chan == 7 ? 8 : chan <= 6 ? chan : 0, ext);
        return true;
    }

    private static bool TryBits(ReadOnlySpan<byte> d, ref int pos, int count, out int value)
    {
        value = 0;
        if (pos + count > d.Length * 8) return false;
        for (int i = 0; i < count; i++, pos++)
            value = (value << 1) | ((d[pos >> 3] >> (7 - (pos & 7))) & 1);
        return true;
    }

    private static bool TryObjectType(ReadOnlySpan<byte> d, ref int pos, out int aot)
    {
        if (!TryBits(d, ref pos, 5, out aot)) return false;
        if (aot != 31) return aot != 0;
        if (!TryBits(d, ref pos, 6, out int ext)) return false;
        aot = 32 + ext;
        return true;
    }

    private static bool TryRate(ReadOnlySpan<byte> d, ref int pos, out int rate)
    {
        rate = 0;
        if (!TryBits(d, ref pos, 4, out int index)) return false;
        if (index == 15) return TryBits(d, ref pos, 24, out rate) && rate > 0;
        if (index >= Rates.Length) return false;
        rate = Rates[index];
        return true;
    }
}
