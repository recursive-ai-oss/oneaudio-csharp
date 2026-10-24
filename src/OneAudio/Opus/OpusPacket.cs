namespace OneAudio.Opus;

/// <summary>Opus packet framing (RFC 6716 section 3): the table-of-contents byte and frame lengths.</summary>
public static class OpusPacket
{
    /// <summary>Most frames a packet can carry (6 bits of count, limited by the 120 ms duration cap).</summary>
    internal const int MaxFrames = 48;

    /// <summary>Longest packet duration in samples at 48 kHz (120 ms).</summary>
    public const int MaxSamplesPer48k = 5760;

    /// <summary>
    /// Number of samples per channel in one frame of the packet, at <paramref name="sampleRate"/>.
    /// </summary>
    /// <param name="packet">Packet bytes; only the first byte (the table of contents) is read.</param>
    /// <param name="sampleRate">Output rate in Hz; a multiple of 400.</param>
    /// <returns>Samples per frame, e.g. 960 for a 20 ms frame at 48 kHz.</returns>
    /// <exception cref="ArgumentException"><paramref name="packet"/> is empty.</exception>
    public static int GetSamplesPerFrame(ReadOnlySpan<byte> packet, int sampleRate)
    {
        if (packet.IsEmpty) throw new ArgumentException("Empty packet.", nameof(packet));
        return SamplesPerFrame(packet[0], sampleRate);
    }

    internal static int SamplesPerFrame(byte toc, int fs)
    {
        if ((toc & 0x80) != 0)
            return (fs << ((toc >> 3) & 3)) / 400;
        if ((toc & 0x60) == 0x60)
            return (toc & 0x08) != 0 ? fs / 50 : fs / 100;
        int size = (toc >> 3) & 3;
        return size == 3 ? fs * 60 / 1000 : (fs << size) / 100;
    }

    /// <summary>Number of channels (1 or 2) the packet's frames are coded with.</summary>
    /// <param name="packet">Packet bytes; only the first byte is read.</param>
    /// <exception cref="ArgumentException"><paramref name="packet"/> is empty.</exception>
    public static int GetChannels(ReadOnlySpan<byte> packet)
    {
        if (packet.IsEmpty) throw new ArgumentException("Empty packet.", nameof(packet));
        return (packet[0] & 4) != 0 ? 2 : 1;
    }

    /// <summary>
    /// Total samples per channel the packet decodes to at <paramref name="sampleRate"/>, or -1 when the packet is malformed.
    /// </summary>
    /// <param name="packet">The packet.</param>
    /// <param name="sampleRate">Output rate in Hz; a multiple of 400.</param>
    public static int GetSampleCount(ReadOnlySpan<byte> packet, int sampleRate)
    {
        Span<int> offsets = stackalloc int[MaxFrames];
        Span<int> sizes = stackalloc int[MaxFrames];
        int count = Parse(packet, false, offsets, sizes, out _, out _);
        if (count < 0) return -1;
        return count * SamplesPerFrame(packet[0], sampleRate);
    }

    /// <summary>
    /// Splits a packet into its frames (RFC 6716 section 3.2). Returns the frame count, or -1 when the packet is
    /// invalid. All offsets are bounds-checked against <paramref name="data"/>.
    /// </summary>
    /// <param name="data">The packet; for self-delimited framing, the bytes starting at this stream's packet.</param>
    /// <param name="selfDelimited">True for the self-delimiting framing used between streams of a multistream packet.</param>
    /// <param name="offsets">Receives each frame's start offset in <paramref name="data"/>.</param>
    /// <param name="sizes">Receives each frame's length in bytes.</param>
    /// <param name="toc">The table-of-contents byte.</param>
    /// <param name="consumed">Bytes the packet occupies (needed for self-delimited framing).</param>
    internal static int Parse(ReadOnlySpan<byte> data, bool selfDelimited, Span<int> offsets, Span<int> sizes,
        out int toc, out int consumed)
    {
        toc = 0;
        consumed = 0;
        if (data.Length < 1) return -1;
        int framesize = SamplesPerFrame(data[0], 48000);
        toc = data[0];
        int pos = 1;
        int len = data.Length - 1;
        int lastSize = len;
        bool cbr = false;
        int count;
        switch (toc & 3)
        {
            case 0:
                count = 1;
                break;
            case 1:
                count = 2;
                cbr = true;
                if (!selfDelimited)
                {
                    if ((len & 1) != 0) return -1;
                    sizes[0] = lastSize = len / 2;
                }
                break;
            case 2:
            {
                count = 2;
                int bytes = ParseSize(data.Slice(pos), out sizes[0]);
                if (bytes < 0) return -1;
                len -= bytes;
                if (sizes[0] < 0 || sizes[0] > len) return -1;
                pos += bytes;
                lastSize = len - sizes[0];
                break;
            }
            default:
            {
                if (len < 1) return -1;
                int ch = data[pos++];
                count = ch & 0x3F;
                if (count <= 0 || framesize * count > MaxSamplesPer48k) return -1;
                len--;
                if ((ch & 0x40) != 0)
                {
                    // Padding length: each 255 byte adds 254 bytes of padding (RFC 8251 section 4).
                    int p;
                    do
                    {
                        if (len <= 0) return -1;
                        p = data[pos++];
                        len--;
                        len -= p == 255 ? 254 : p;
                    } while (p == 255);
                }
                if (len < 0) return -1;
                cbr = (ch & 0x80) == 0;
                if (!cbr)
                {
                    lastSize = len;
                    for (int i = 0; i < count - 1; i++)
                    {
                        int bytes = ParseSize(data.Slice(pos), out sizes[i]);
                        if (bytes < 0) return -1;
                        len -= bytes;
                        if (sizes[i] < 0 || sizes[i] > len) return -1;
                        pos += bytes;
                        lastSize -= bytes + sizes[i];
                    }
                    if (lastSize < 0) return -1;
                }
                else if (!selfDelimited)
                {
                    lastSize = len / count;
                    if (lastSize * count != len) return -1;
                    for (int i = 0; i < count - 1; i++) sizes[i] = lastSize;
                }
                break;
            }
        }

        if (selfDelimited)
        {
            int bytes = ParseSize(data.Slice(pos), out sizes[count - 1]);
            if (bytes < 0) return -1;
            len -= bytes;
            if (sizes[count - 1] < 0 || sizes[count - 1] > len) return -1;
            pos += bytes;
            if (cbr)
            {
                if (sizes[count - 1] * count > len) return -1;
                for (int i = 0; i < count - 1; i++) sizes[i] = sizes[count - 1];
            }
            else if (sizes[count - 1] > lastSize)
            {
                return -1;
            }
        }
        else
        {
            // The last frame's size is implicit; it cannot exceed 1275 bytes.
            if (lastSize > 1275) return -1;
            sizes[count - 1] = lastSize;
        }

        for (int i = 0; i < count; i++)
        {
            offsets[i] = pos;
            pos += sizes[i];
        }
        if (pos > data.Length) return -1;
        consumed = pos;
        return count;
    }

    private static int ParseSize(ReadOnlySpan<byte> data, out int size)
    {
        if (data.Length < 1)
        {
            size = -1;
            return -1;
        }
        if (data[0] < 252)
        {
            size = data[0];
            return 1;
        }
        if (data.Length < 2)
        {
            size = -1;
            return -1;
        }
        size = 4 * data[1] + data[0];
        return 2;
    }
}
