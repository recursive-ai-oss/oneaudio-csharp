namespace OneAudio.Ogg;

/// <summary>
/// The Ogg page checksum (RFC 3533 section 6): CRC-32 with polynomial 0x04C11DB7, initial value 0, no bit reflection
/// and no final XOR, computed over the whole page with the checksum field itself set to zero.
/// </summary>
internal static class OggCrc
{
    private static readonly uint[] Table = BuildTable();

    private static uint[] BuildTable()
    {
        var t = new uint[256];
        for (uint i = 0; i < 256; i++)
        {
            uint r = i << 24;
            for (int k = 0; k < 8; k++) r = (r & 0x80000000) != 0 ? (r << 1) ^ 0x04C11DB7 : r << 1;
            t[i] = r;
        }
        return t;
    }

    /// <summary>Continues a checksum over <paramref name="data"/>.</summary>
    public static uint Update(uint crc, ReadOnlySpan<byte> data)
    {
        var table = Table;
        foreach (byte b in data) crc = (crc << 8) ^ table[(byte)(crc >> 24) ^ b];
        return crc;
    }
}
