namespace OneAudio.Flac;

/// <summary>
/// The two checksums of a FLAC frame (RFC 9639 sections 9.1.8 and 9.3): CRC-8 with polynomial x^8+x^2+x+1 over the
/// frame header, and CRC-16 with polynomial x^16+x^15+x^2+1 over the whole frame. Both start at zero, are not
/// reflected and have no final XOR. The 16-bit sum uses slicing-by-4 tables.
/// </summary>
internal static class FlacCrc
{
    private static readonly byte[] Table8 = BuildTable8();
    private static readonly ushort[] Table16 = BuildTable16();

    private static byte[] BuildTable8()
    {
        var t = new byte[256];
        for (int i = 0; i < 256; i++)
        {
            int c = i;
            for (int b = 0; b < 8; b++) c = (c & 0x80) != 0 ? (c << 1) ^ 0x07 : c << 1;
            t[i] = (byte)c;
        }
        return t;
    }

    // Four tables: Table16[k * 256 + b] is the CRC contribution of byte b followed by k zero bytes.
    private static ushort[] BuildTable16()
    {
        var t = new ushort[4 * 256];
        for (int i = 0; i < 256; i++)
        {
            int c = i << 8;
            for (int b = 0; b < 8; b++) c = (c & 0x8000) != 0 ? (c << 1) ^ 0x8005 : c << 1;
            t[i] = (ushort)c;
        }
        for (int i = 0; i < 256; i++)
            for (int k = 1; k < 4; k++)
            {
                int prev = t[((k - 1) << 8) + i];
                t[(k << 8) + i] = (ushort)((prev << 8) ^ t[prev >> 8]);
            }
        return t;
    }

    public static byte Crc8(ReadOnlySpan<byte> data)
    {
        int c = 0;
        foreach (byte b in data) c = Table8[c ^ b];
        return (byte)c;
    }

    public static ushort Crc16(ReadOnlySpan<byte> data)
    {
        var t = Table16;
        int c = 0;
        int i = 0;
        for (; i + 4 <= data.Length; i += 4)
        {
            int x = c ^ ((data[i] << 8) | data[i + 1]);
            c = t[768 + (x >> 8)] ^ t[512 + (x & 0xFF)] ^ t[256 + data[i + 2]] ^ t[data[i + 3]];
        }
        for (; i < data.Length; i++) c = ((c << 8) & 0xFFFF) ^ t[(c >> 8) ^ data[i]];
        return (ushort)c;
    }
}
