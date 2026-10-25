namespace OneAudio.Flac;

/// <summary>The STREAMINFO metadata block (RFC 9639 section 8.2) after validation against <see cref="AudioLimits"/>.</summary>
internal sealed class FlacStreamInfo
{
    public int MinBlockSize { get; private init; }
    public int MaxBlockSize { get; private init; }
    public int MinFrameSize { get; private init; }
    public int MaxFrameSize { get; private init; }
    public int SampleRate { get; private init; }
    public int Channels { get; private init; }
    public int BitsPerSample { get; private init; }

    /// <summary>Total samples per channel, or 0 when the encoder did not record it.</summary>
    public long TotalSamples { get; private init; }

    public byte[] Md5 { get; private init; } = new byte[16];

    public bool HasMd5 => Md5.AsSpan().ContainsAnyExcept((byte)0);

    public const int Size = 34;

    /// <summary>Parses the 34-byte body. Throws for values RFC 9639 forbids and for limit violations.</summary>
    public static FlacStreamInfo Parse(ReadOnlySpan<byte> d, AudioLimits limits)
    {
        if (d.Length != Size) throw new AudioFormatException("FLAC STREAMINFO block must be 34 bytes.");
        int minBlock = (d[0] << 8) | d[1];
        int maxBlock = (d[2] << 8) | d[3];
        int minFrame = (d[4] << 16) | (d[5] << 8) | d[6];
        int maxFrame = (d[7] << 16) | (d[8] << 8) | d[9];
        int rate = (d[10] << 12) | (d[11] << 4) | (d[12] >> 4);
        int channels = ((d[12] >> 1) & 7) + 1;
        int bps = (((d[12] & 1) << 4) | (d[13] >> 4)) + 1;
        long total = ((long)(d[13] & 15) << 32) | ((long)d[14] << 24) | ((long)d[15] << 16) | ((long)d[16] << 8) | d[17];

        // RFC 9639 section 8.2: block sizes below 16 are invalid; a sample rate of 0 is invalid for audio.
        if (minBlock < 16 || maxBlock < 16) throw new AudioFormatException("FLAC STREAMINFO block size is below 16.");
        if (minBlock > maxBlock) throw new AudioFormatException("FLAC STREAMINFO minimum block size exceeds the maximum.");
        if (bps < 4) throw new AudioFormatException("FLAC STREAMINFO bit depth is below 4.");
        limits.CheckSampleRate(rate);
        limits.CheckChannels(channels);
        limits.CheckFrameSize(maxBlock);
        return new FlacStreamInfo
        {
            MinBlockSize = minBlock,
            MaxBlockSize = maxBlock,
            MinFrameSize = minFrame,
            MaxFrameSize = maxFrame,
            SampleRate = rate,
            Channels = channels,
            BitsPerSample = bps,
            TotalSamples = total,
            Md5 = d.Slice(18, 16).ToArray(),
        };
    }
}

/// <summary>A parsed and CRC-8-checked frame header (RFC 9639 section 9.1).</summary>
internal struct FlacFrameHeader
{
    public int BlockSize;
    public int SampleRate;
    public int BitsPerSample;
    public int Channels;

    /// <summary>0 to 7: independent channels (count minus one); 8 left/side, 9 side/right, 10 mid/side.</summary>
    public int ChannelAssignment;

    /// <summary>True when the coded number is a sample number (variable block size), false when it is a frame number.</summary>
    public bool Variable;
    public ulong Number;

    /// <summary>Header length in bytes including the CRC-8.</summary>
    public int Length;
}

internal enum FlacHeaderStatus { Ok, Invalid, NeedMore }

internal static class FlacFrameHeaderParser
{
    private static readonly int[] RateTable = [0, 88200, 176400, 192000, 8000, 16000, 22050, 24000, 32000, 44100, 48000, 96000];

    /// <summary>
    /// Parses a frame header at the start of <paramref name="d"/>. Never throws: bad input is <see cref="FlacHeaderStatus.Invalid"/>
    /// and a header cut short by the end of <paramref name="d"/> is <see cref="FlacHeaderStatus.NeedMore"/>.
    /// Zero codes (rate, depth) are returned as 0 for the caller to resolve from STREAMINFO.
    /// </summary>
    public static FlacHeaderStatus Parse(ReadOnlySpan<byte> d, out FlacFrameHeader h)
    {
        h = default;
        if (d.Length < 5) return FlacHeaderStatus.NeedMore;
        // 14-bit sync 0b11111111111110, then a reserved zero bit, then the blocking strategy bit.
        if (d[0] != 0xFF || (d[1] & 0xFE) != 0xF8) return FlacHeaderStatus.Invalid;
        h.Variable = (d[1] & 1) != 0;
        int bsCode = d[2] >> 4;
        int rateCode = d[2] & 15;
        int chCode = d[3] >> 4;
        int sizeCode = (d[3] >> 1) & 7;
        if ((d[3] & 1) != 0) return FlacHeaderStatus.Invalid; // reserved bit
        if (bsCode == 0 || rateCode == 15 || chCode > 10 || sizeCode == 3) return FlacHeaderStatus.Invalid;

        // UTF-8 style coded frame or sample number: up to 6 bytes (31 bits) for frame numbers, 7 (36 bits) for sample numbers.
        int pos = 4;
        int b0 = d[pos++];
        int extra;
        ulong num;
        if (b0 < 0x80) { extra = 0; num = (ulong)b0; }
        else if ((b0 & 0xE0) == 0xC0) { extra = 1; num = (ulong)(b0 & 0x1F); }
        else if ((b0 & 0xF0) == 0xE0) { extra = 2; num = (ulong)(b0 & 0x0F); }
        else if ((b0 & 0xF8) == 0xF0) { extra = 3; num = (ulong)(b0 & 0x07); }
        else if ((b0 & 0xFC) == 0xF8) { extra = 4; num = (ulong)(b0 & 0x03); }
        else if ((b0 & 0xFE) == 0xFC) { extra = 5; num = (ulong)(b0 & 0x01); }
        else if (b0 == 0xFE) { extra = 6; num = 0; }
        else return FlacHeaderStatus.Invalid;
        if (!h.Variable && extra > 5) return FlacHeaderStatus.Invalid;
        for (int i = 0; i < extra; i++)
        {
            if (pos >= d.Length) return FlacHeaderStatus.NeedMore;
            int c = d[pos++];
            if ((c & 0xC0) != 0x80) return FlacHeaderStatus.Invalid;
            num = (num << 6) | (uint)(c & 0x3F);
        }
        h.Number = num;

        int blockSize;
        if (bsCode == 1) blockSize = 192;
        else if (bsCode <= 5) blockSize = 576 << (bsCode - 2);
        else if (bsCode >= 8) blockSize = 256 << (bsCode - 8);
        else if (bsCode == 6)
        {
            if (pos >= d.Length) return FlacHeaderStatus.NeedMore;
            blockSize = d[pos++] + 1;
        }
        else
        {
            if (pos + 2 > d.Length) return FlacHeaderStatus.NeedMore;
            blockSize = ((d[pos] << 8) | d[pos + 1]) + 1;
            pos += 2;
        }

        int rate;
        if (rateCode <= 11) rate = RateTable[rateCode];
        else if (rateCode == 12)
        {
            if (pos >= d.Length) return FlacHeaderStatus.NeedMore;
            rate = d[pos++] * 1000;
        }
        else
        {
            if (pos + 2 > d.Length) return FlacHeaderStatus.NeedMore;
            rate = (d[pos] << 8) | d[pos + 1];
            if (rateCode == 14) rate *= 10;
            pos += 2;
        }

        if (pos >= d.Length) return FlacHeaderStatus.NeedMore;
        if (FlacCrc.Crc8(d[..pos]) != d[pos]) return FlacHeaderStatus.Invalid;
        pos++;

        h.BlockSize = blockSize;
        h.SampleRate = rate;
        h.BitsPerSample = sizeCode switch { 0 => 0, 1 => 8, 2 => 12, 4 => 16, 5 => 20, 6 => 24, _ => 32 };
        h.ChannelAssignment = chCode;
        h.Channels = chCode < 8 ? chCode + 1 : 2;
        h.Length = pos;
        return FlacHeaderStatus.Ok;
    }
}
