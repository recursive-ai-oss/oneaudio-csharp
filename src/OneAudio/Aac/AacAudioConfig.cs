namespace OneAudio.Aac;

/// <summary>
/// The MPEG-4 AudioSpecificConfig (ISO/IEC 14496-3 1.6.2.1) parsed in full, including the GASpecificConfig and the
/// backward-compatible SBR / PS signalling after it. It describes what the stream is; <see cref="CheckSupported"/> says
/// whether <see cref="AacDecoder"/> can decode it.
/// </summary>
public sealed class AacAudioConfig
{
    private static readonly int[] Rates = [96000, 88200, 64000, 48000, 44100, 32000, 24000, 22050, 16000, 12000, 11025, 8000, 7350];

    private AacAudioConfig() { }

    /// <summary>Audio object type of the core coder: 2 is AAC-LC, 1 Main, 3 SSR, 4 LTP, 5 SBR, 29 PS, 17+ error-resilient types.</summary>
    public int ObjectType { get; private set; }

    /// <summary>Sampling rate in Hz of the core coder (the rate of the decoded output; SBR, when present, would double it).</summary>
    public int SampleRate { get; private set; }

    /// <summary>The 4-bit sampling frequency index; 15 when the rate was written out explicitly.</summary>
    public int SampleRateIndex { get; private set; }

    /// <summary>The 4-bit channelConfiguration: 0 means the layout is in a program config element, 1 mono, 2 stereo, up to 7.</summary>
    public int ChannelConfiguration { get; private set; }

    /// <summary>
    /// Channel count implied by <see cref="ChannelConfiguration"/>, or counted from the program config element when the
    /// configuration is 0. 0 when unknown.
    /// </summary>
    public int Channels { get; private set; }

    /// <summary>True when the program config element lists anything beyond front channel elements (side, back, LFE, data or coupling elements).</summary>
    public bool ProgramConfigIsComplex { get; private set; }

    /// <summary>True when frameLengthFlag selected 960-sample frames instead of 1024.</summary>
    public bool FrameLength960 { get; private set; }

    /// <summary>The dependsOnCoreCoder flag (scalable streams).</summary>
    public bool DependsOnCoreCoder { get; private set; }

    /// <summary>The coreCoderDelay field, 0 unless <see cref="DependsOnCoreCoder"/>.</summary>
    public int CoreCoderDelay { get; private set; }

    /// <summary>The GASpecificConfig extensionFlag.</summary>
    public bool ExtensionFlag { get; private set; }

    /// <summary>True when the config announces spectral band replication, either with object type 5 / 29 or a 0x2b7 sync extension that sets it.</summary>
    public bool SbrSignalled { get; private set; }

    /// <summary>True when the config announces parametric stereo (object type 29 or a 0x548 sync extension that sets it).</summary>
    public bool PsSignalled { get; private set; }

    /// <summary>The output sampling rate announced together with SBR, or 0.</summary>
    public int ExtensionSampleRate { get; private set; }

    /// <summary>True when the channel layout is carried in a program config element inside the config (channelConfiguration 0).</summary>
    public bool HasProgramConfigElement => ChannelConfiguration == 0;

    /// <summary>Parses the config bytes exactly as a container stores them.</summary>
    /// <param name="config">The AudioSpecificConfig.</param>
    /// <returns>The parsed fields.</returns>
    /// <exception cref="AudioFormatException">The bytes are too short or use a reserved value.</exception>
    public static AacAudioConfig Parse(ReadOnlySpan<byte> config)
    {
        if (!TryParse(config, out var c)) throw new AudioFormatException("The AAC AudioSpecificConfig is truncated or invalid.");
        return c!;
    }

    /// <summary>Parses the config bytes without throwing.</summary>
    /// <param name="config">The AudioSpecificConfig.</param>
    /// <param name="result">The parsed fields when this returns true.</param>
    /// <returns>False when the bytes are too short or use a reserved value.</returns>
    public static bool TryParse(ReadOnlySpan<byte> config, out AacAudioConfig? result)
    {
        result = null;
        var br = new AacBitReader(config);
        var c = new AacAudioConfig();
        if (!ReadObjectType(ref br, out int aot)) return false;
        if (!ReadRate(ref br, out int rate, out int index)) return false;
        if (br.BitsLeft < 4) return false;
        c.ChannelConfiguration = (int)br.Read(4);
        c.Channels = c.ChannelConfiguration switch { 7 => 8, <= 6 => c.ChannelConfiguration, _ => 0 };
        c.SampleRate = rate;
        c.SampleRateIndex = index;
        int coreAot = aot;
        if (aot is 5 or 29)
        {
            c.SbrSignalled = true;
            c.PsSignalled = aot == 29;
            if (!ReadRate(ref br, out int extRate, out _)) return false;
            c.ExtensionSampleRate = extRate;
            if (!ReadObjectType(ref br, out coreAot)) return false;
            if (coreAot == 22 && br.BitsLeft >= 4) br.Skip(4);
        }
        c.ObjectType = aot is 5 or 29 ? coreAot : aot;
        if (c.ObjectType is 1 or 2 or 3 or 4 or 6 or 7 or 17 or 19 or 20 or 21 or 22 or 23)
        {
            if (br.BitsLeft < 3) return false;
            c.FrameLength960 = br.ReadBit();
            c.DependsOnCoreCoder = br.ReadBit();
            if (c.DependsOnCoreCoder)
            {
                if (br.BitsLeft < 14) return false;
                c.CoreCoderDelay = (int)br.Read(14);
            }
            c.ExtensionFlag = br.ReadBit();
            if (c.ChannelConfiguration == 0)
            {
                if (!c.ReadProgramConfig(ref br)) return false;
            }
            if (c.ObjectType is 6 or 20)
            {
                if (br.BitsLeft < 3) return false;
                br.Skip(3);
            }
            if (c.ExtensionFlag)
            {
                if (c.ObjectType == 22)
                {
                    if (br.BitsLeft < 16) return false;
                    br.Skip(16);
                }
                if (c.ObjectType is 17 or 19 or 20 or 23)
                {
                    if (br.BitsLeft < 3) return false;
                    br.Skip(3);
                }
                if (br.BitsLeft < 1) return false;
                br.Skip(1); // extensionFlag3
            }
        }
        // Backward-compatible extension signalling (14496-3 1.6.2.1): sync word 0x2b7 announces SBR, then 0x548 PS.
        if (aot is not (5 or 29) && br.BitsLeft >= 16 && br.Peek(11) == 0x2b7)
        {
            br.Skip(11);
            if (ReadObjectType(ref br, out int ext) && ext == 5 && br.BitsLeft >= 1)
            {
                if (br.ReadBit())
                {
                    c.SbrSignalled = true;
                    if (ReadRate(ref br, out int extRate, out _)) c.ExtensionSampleRate = extRate;
                    if (br.BitsLeft >= 12 && br.Peek(11) == 0x548)
                    {
                        br.Skip(11);
                        if (br.ReadBit()) c.PsSignalled = true;
                    }
                }
            }
        }
        else if (aot is not (5 or 29) && br.BitsLeft >= 12 && br.Peek(11) == 0x548)
        {
            br.Skip(11);
            if (br.ReadBit()) c.PsSignalled = true;
        }
        result = c;
        return true;
    }

    /// <summary>
    /// Throws when this config is outside what <see cref="AacDecoder"/> decodes: AAC-LC (object type 2), 1024-sample frames,
    /// one or two channels in channelConfiguration 1 or 2, a core rate of 8-48 kHz from the standard table, no
    /// explicit SBR or PS signalling and no scalable core coder.
    /// </summary>
    /// <exception cref="UnsupportedAudioFormatException">The stream needs a feature that is not implemented.</exception>
    public void CheckSupported()
    {
        if (SbrSignalled || PsSignalled || ObjectType is 5 or 29)
            throw new UnsupportedAudioFormatException("AAC with explicit SBR or parametric stereo signalling (HE-AAC) is not supported.");
        if (ObjectType != 2)
            throw new UnsupportedAudioFormatException($"AAC audio object type {ObjectType} is not supported; only AAC-LC (2) is.");
        if (ProgramConfigIsComplex)
            throw new UnsupportedAudioFormatException("AAC program config elements with side, back, LFE, data or coupling channels are not supported.");
        if (Channels is < 1 or > 2)
            throw new UnsupportedAudioFormatException(ChannelConfiguration == 0
                ? $"AAC with {Channels} channels in its program config element is not supported; mono and stereo are."
                : $"AAC with channel configuration {ChannelConfiguration} (more than two channels) is not supported.");
        if (FrameLength960)
            throw new UnsupportedAudioFormatException("AAC with 960-sample frames is not supported.");
        if (DependsOnCoreCoder)
            throw new UnsupportedAudioFormatException("Scalable AAC (core coder dependency) is not supported.");
        if (SampleRateIndex == 15 || SampleRateIndex >= 12 || SampleRate > 48000)
            throw new UnsupportedAudioFormatException($"AAC at {SampleRate} Hz is not supported; the core rates 8000-48000 Hz are.");
    }

    // 14496-3 4.4.1.1 program_config_element: only the channel count matters here. Anything but front elements marks the layout complex.
    private bool ReadProgramConfig(ref AacBitReader br)
    {
        if (br.BitsLeft < 4 + 2 + 4 + 4 + 4 + 4 + 2 + 3 + 4 + 3) return false;
        br.Skip(4 + 2 + 4); // tag, object type, sampling frequency index
        int front = (int)br.Read(4), side = (int)br.Read(4), back = (int)br.Read(4), lfe = (int)br.Read(2), assoc = (int)br.Read(3), cc = (int)br.Read(4);
        if (br.ReadBit()) br.Skip(4);
        if (br.ReadBit()) br.Skip(4);
        if (br.ReadBit()) br.Skip(3);
        if (br.BitsLeft < front * 5 + side * 5 + back * 5 + lfe * 4 + assoc * 4 + cc * 5) return false;
        int channels = 0;
        for (int i = 0; i < front; i++)
        {
            channels += br.ReadBit() ? 2 : 1; // is_cpe
            br.Skip(4);                       // element_instance_tag
        }
        br.Skip(side * 5 + back * 5 + lfe * 4 + assoc * 4 + cc * 5);
        br.ByteAlign();
        if (br.BitsLeft < 8) return false;
        int comment = (int)br.Read(8);
        if (br.BitsLeft < comment * 8) return false;
        br.Skip(comment * 8);
        ProgramConfigIsComplex = side + back + lfe + assoc + cc != 0;
        Channels = channels;
        return true;
    }

    internal static int RateIndexToSupportedTable(int rateIndex) => rateIndex switch
    {
        3 => 0, 4 => 1, 5 => 2, 6 => 3, 7 => 4, 8 => 5, 9 => 6, 10 => 7, 11 => 8, _ => -1,
    };

    private static bool ReadObjectType(ref AacBitReader br, out int aot)
    {
        aot = 0;
        if (br.BitsLeft < 5) return false;
        aot = (int)br.Read(5);
        if (aot == 31)
        {
            if (br.BitsLeft < 6) return false;
            aot = 32 + (int)br.Read(6);
        }
        return aot != 0;
    }

    private static bool ReadRate(ref AacBitReader br, out int rate, out int index)
    {
        rate = 0;
        index = 0;
        if (br.BitsLeft < 4) return false;
        index = (int)br.Read(4);
        if (index == 15)
        {
            if (br.BitsLeft < 24) return false;
            rate = (int)br.Read(24);
            return rate > 0;
        }
        if (index >= Rates.Length) return false;
        rate = Rates[index];
        return true;
    }
}
