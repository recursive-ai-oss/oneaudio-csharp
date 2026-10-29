namespace OneAudio.Aac;

/// <summary>
/// Decodes AAC-LC raw_data_blocks (ISO/IEC 14496-3 subpart 4, with 3GPP TS 26.410 as the reference for the behaviour of
/// each tool) to float PCM at the core rate. One instance holds the state of one stream (overlap buffers, previous window
/// shape) and allocates nothing after construction. Not thread-safe.
/// </summary>
internal sealed partial class AacFrameDecoder
{
    internal const int FrameSize = 1024;
    internal const int ShortSize = 128;
    internal const int OnlyLong = 0, LongStart = 1, EightShort = 2, LongStop = 3;
    private const int ZeroBook = 0, EscBook = 11, ReservedBook = 12, NoiseBook = 13, IntensityBook2 = 14, IntensityBook = 15;

    private static readonly float[] Pow43 = BuildPow43();
    private static readonly float[] QuarterSteps = [1f, 1.18920711500272f, 1.4142135623731f, 1.68179283050743f];

    private readonly int _channels;
    private readonly int[]? _frontTags;
    private readonly bool[]? _frontIsCpe;
    private readonly bool[] _filled;
    private readonly short[] _longBands;
    private readonly short[] _shortBands;
    private readonly int _longBandCount;
    private readonly int _shortBandCount;
    private readonly int _tnsMaxLong;
    private readonly AacChannelState[] _ch;
    private readonly AacImdct _imdctLong = new(FrameSize);
    private readonly AacImdct _imdctShort = new(ShortSize);
    private readonly float[] _imdctOut = new float[2 * FrameSize];
    private readonly float[] _windowed = new float[2 * FrameSize];
    private readonly byte[] _msUsed = new byte[AacChannelState.MaxBands];
    private readonly bool[] _correlated = new bool[AacChannelState.MaxBands];
    private readonly uint[] _noiseSeed = new uint[AacChannelState.MaxBands * 8];
    private uint _noiseState;

    /// <summary>True once any frame carried an SBR fill extension (HE-AAC data riding on the core stream), which is skipped.</summary>
    internal bool SbrDataSeen { get; private set; }

    /// <summary>Tools seen in the frames decoded so far.</summary>
    internal AacTools Tools { get; private set; }

    internal AacFrameDecoder(AacAudioConfig config)
    {
        _channels = config.Channels;
        _frontTags = config.FrontTags;
        _frontIsCpe = config.FrontIsCpe;
        _filled = new bool[_channels];
        switch (AacAudioConfig.RateIndexToSupportedTable(config.SampleRateIndex))
        {
            case 0: case 1: // 48000, 44100
                _longBands = AacTables.SfbLong48.ToArray(); _shortBands = AacTables.SfbShort48.ToArray(); _tnsMaxLong = config.SampleRateIndex == 3 ? 40 : 42; break;
            case 2:
                _longBands = AacTables.SfbLong32.ToArray(); _shortBands = AacTables.SfbShort48.ToArray(); _tnsMaxLong = 51; break;
            case 3: case 4:
                _longBands = AacTables.SfbLong24.ToArray(); _shortBands = AacTables.SfbShort24.ToArray(); _tnsMaxLong = 46; break;
            case 5: case 6: case 7:
                _longBands = AacTables.SfbLong16.ToArray(); _shortBands = AacTables.SfbShort16.ToArray(); _tnsMaxLong = 42; break;
            case 8:
                _longBands = AacTables.SfbLong8.ToArray(); _shortBands = AacTables.SfbShort8.ToArray(); _tnsMaxLong = 39; break;
            default:
                throw new UnsupportedAudioFormatException($"AAC at {config.SampleRate} Hz is not supported.");
        }
        _longBandCount = _longBands.Length - 1;
        _shortBandCount = _shortBands.Length - 1;
        _ch = new AacChannelState[_channels];
        for (int i = 0; i < _channels; i++) _ch[i] = new AacChannelState();
        _ = AacHuffman.Books; // build the codebooks now, not on the first frame
    }

    internal int Channels => _channels;

    /// <summary>Forgets all history (overlap, previous window shape, concealment state), as at the start of a stream.</summary>
    internal void Reset()
    {
        foreach (var c in _ch)
        {
            Array.Clear(c.Overlap);
            c.PrevShape = 0;
            c.LastSequence = OnlyLong;
            c.LastSpecUsable = false;
            c.ConcealRun = 0;
        }
    }

    // --- one access unit ---------------------------------------------------------------------------------------------

    /// <summary>
    /// Decodes one access unit into interleaved PCM (1024 frames of <see cref="Channels"/> samples).
    /// <paramref name="frameIndex"/> seeds the noise generator so that decoding from a seek lands on the same noise as a linear decode.
    /// </summary>
    /// <exception cref="AudioFormatException">The unit is malformed.</exception>
    /// <exception cref="UnsupportedAudioFormatException">The unit uses a coupling channel, LFE, program config or gain control element.</exception>
    internal void Decode(ReadOnlySpan<byte> unit, Span<float> pcm, long frameIndex)
    {
        _noiseState = (uint)frameIndex * 2654435761u + 0x9E3779B9u;
        var br = new AacBitReader(unit);
        int done = 0;
        bool end = false;
        Array.Clear(_filled);
        while (!end)
        {
            if (br.BitsLeft < 3) throw new AudioFormatException("An AAC access unit ends before its end element.");
            int id = (int)br.Read(3);
            switch (id)
            {
                case 0: // SCE
                {
                    int slot = Slot((int)br.Read(4), false, done, 1);
                    ReadChannelStream(ref br, _ch[slot], commonWindow: false);
                    CheckOverrun(in br);
                    ApplyPns(_ch[slot], null, 0);
                    ApplyTns(_ch[slot]);
                    done += 1;
                    break;
                }
                case 1: // CPE
                {
                    int slot = Slot((int)br.Read(4), true, done, 2);
                    ReadPair(ref br, _ch[slot], _ch[slot + 1]);
                    done += 2;
                    break;
                }
                case 2:
                    throw new UnsupportedAudioFormatException("AAC coupling channel elements are not supported.");
                case 3:
                    throw new UnsupportedAudioFormatException("AAC LFE elements are not supported.");
                case 4: // DSE
                    SkipDataStream(ref br);
                    break;
                case 5: // in-band program config element: tolerated when it describes at most front elements, as the config did
                    SkipProgramConfig(ref br);
                    break;
                case 6: // FIL
                    SkipFill(ref br);
                    break;
                default: // 7 = END
                    end = true;
                    break;
            }
            CheckOverrun(in br);
        }
        if (done != _channels) throw new AudioFormatException("An AAC access unit has fewer channels than the stream configuration.");
        for (int c = 0; c < _channels; c++)
        {
            Synthesize(_ch[c]);
            var t = _ch[c].Time;
            for (int i = 0; i < FrameSize; i++) pcm[i * _channels + c] = t[i];
        }
    }

    private void Note(AacChannelState c)
    {
        if (c.PulsePresent) Tools |= AacTools.Pulse;
        if (c.TnsPresent) Tools |= AacTools.Tns;
        if (c.WindowShape == 1) Tools |= AacTools.KbdWindow;
        if (c.IsShort) { Tools |= AacTools.ShortBlocks; if (c.NumGroups < 8) Tools |= AacTools.WindowGrouping; }
        for (int g = 0; g < c.NumGroups; g++)
            for (int b = 0; b < c.MaxSfb; b++)
                switch (c.Codebook[g * 16 + b])
                {
                    case NoiseBook: Tools |= AacTools.Pns; break;
                    case IntensityBook: case IntensityBook2: Tools |= AacTools.Intensity; break;
                }
    }

    // Finds the output channel of an element: by its instance tag when the config listed the front elements, otherwise in order.
    private int Slot(int tag, bool pair, int done, int width)
    {
        int slot = done;
        if (_frontTags is { } tags)
        {
            slot = -1;
            int at = 0;
            for (int i = 0; i < tags.Length; i++)
            {
                int w = _frontIsCpe![i] ? 2 : 1;
                if (tags[i] == tag && _frontIsCpe[i] == pair) { slot = at; break; }
                at += w;
            }
            if (slot < 0) throw new AudioFormatException("An AAC element has an instance tag that the program config element does not list.");
        }
        if (slot + width > _channels) throw new AudioFormatException("An AAC access unit has more channels than the stream configuration.");
        for (int i = 0; i < width; i++)
        {
            if (_filled[slot + i]) throw new AudioFormatException("An AAC access unit has two elements for the same channel.");
            _filled[slot + i] = true;
        }
        return slot;
    }

    private static void CheckOverrun(in AacBitReader br)
    {
        if (br.Overrun) throw new AudioFormatException("An AAC access unit is truncated.");
    }

    private static void SkipProgramConfig(ref AacBitReader br)
    {
        br.Skip(4 + 2 + 4);
        int front = (int)br.Read(4), side = (int)br.Read(4), back = (int)br.Read(4), lfe = (int)br.Read(2), assoc = (int)br.Read(3), cc = (int)br.Read(4);
        if (br.ReadBit()) br.Skip(4);
        if (br.ReadBit()) br.Skip(4);
        if (br.ReadBit()) br.Skip(3);
        br.Skip(front * 5 + side * 5 + back * 5 + lfe * 4 + assoc * 4 + cc * 5);
        br.ByteAlign();
        br.Skip((int)br.Read(8) * 8);
    }

    // 14496-3 4.4.2.6 data_stream_element
    private static void SkipDataStream(ref AacBitReader br)
    {
        br.Skip(4);
        bool align = br.ReadBit();
        int count = (int)br.Read(8);
        if (count == 255) count += (int)br.Read(8);
        if (align) br.ByteAlign();
        br.Skip(count * 8);
    }

    // 14496-3 4.4.2.7 fill_element; an extension_type of 0xD / 0xE is SBR data that a core-only decoder skips.
    private void SkipFill(ref AacBitReader br)
    {
        int count = (int)br.Read(4);
        if (count == 15) count += (int)br.Read(8) - 1;
        if (count > 0 && br.BitsLeft >= 4)
        {
            uint ext = br.Peek(4);
            if (ext is 0xD or 0xE) { SbrDataSeen = true; Tools |= AacTools.SbrFill; }
        }
        br.Skip(count * 8);
    }

    // --- channel elements --------------------------------------------------------------------------------------------

    private void ReadPair(ref AacBitReader br, AacChannelState l, AacChannelState r)
    {
        bool common = br.ReadBit();
        Array.Clear(_msUsed);
        Array.Clear(_correlated);
        bool msPresent = false;
        if (common)
        {
            ReadIcsInfo(ref br, l);
            r.CopyIcsFrom(l);
            int mask = (int)br.Read(2);
            if (mask == 3) throw new AudioFormatException("An AAC channel pair has a reserved ms_mask_present value.");
            if (mask == 1)
            {
                for (int g = 0; g < l.NumGroups; g++)
                    for (int b = 0; b < l.MaxSfb; b++)
                        _msUsed[g * 16 + b] = br.ReadBit() ? (byte)1 : (byte)0;
                msPresent = true;
            }
            else if (mask == 2)
            {
                for (int g = 0; g < l.NumGroups; g++)
                    for (int b = 0; b < l.MaxSfb; b++)
                        _msUsed[g * 16 + b] = 1;
                msPresent = true;
            }
        }
        ReadChannelStream(ref br, l, common);
        CheckOverrun(in br);
        ReadChannelStream(ref br, r, common);
        CheckOverrun(in br);

        if (common && msPresent)
        {
            // 14496-3 4.6.13: a band that is noise in both channels with ms_used set shares one noise vector and is not
            // mid/side transformed.
            for (int g = 0; g < l.NumGroups; g++)
                for (int b = 0; b < l.MaxSfb; b++)
                {
                    int i = g * 16 + b;
                    if (_msUsed[i] != 0)
                    {
                        _correlated[i] = true;
                        if (l.Codebook[i] == NoiseBook && r.Codebook[i] == NoiseBook) _msUsed[i] = 0;
                    }
                }
            for (int i = 0; i < _msUsed.Length; i++) if (_msUsed[i] != 0) { Tools |= AacTools.MidSide; break; }
            ApplyMidSide(l, r);
        }
        ApplyIntensity(l, r, common);
        ApplyPns(l, _correlated, 0);
        ApplyPns(r, _correlated, 1);
        ApplyTns(l);
        ApplyTns(r);
    }

    // 14496-3 4.4.2.1 individual_channel_stream
    private void ReadChannelStream(ref AacBitReader br, AacChannelState c, bool commonWindow)
    {
        int globalGain = (int)br.Read(8);
        if (!commonWindow) ReadIcsInfo(ref br, c);
        ReadSectionData(ref br, c);
        ReadScaleFactors(ref br, c, globalGain);
        c.PulsePresent = br.ReadBit();
        if (c.PulsePresent)
        {
            if (c.IsShort) throw new AudioFormatException("An AAC short-block channel carries pulse data.");
            c.PulseCount = (int)br.Read(2) + 1;
            c.PulseStartBand = (int)br.Read(6);
            for (int i = 0; i < c.PulseCount; i++)
            {
                c.PulseOffset[i] = (int)br.Read(5);
                c.PulseAmp[i] = (int)br.Read(4);
            }
        }
        c.TnsPresent = br.ReadBit();
        if (c.TnsPresent) ReadTns(ref br, c);
        if (br.ReadBit()) throw new UnsupportedAudioFormatException("AAC gain control (SSR) data is not supported.");
        CheckOverrun(in br);
        ReadSpectralData(ref br, c);
        Dequantize(c);
        Note(c);
    }

    // 14496-3 4.4.2.1 ics_info
    private void ReadIcsInfo(ref AacBitReader br, AacChannelState c)
    {
        br.Skip(1); // ics_reserved_bit
        c.WindowSequence = (int)br.Read(2);
        c.WindowShape = (int)br.Read(1);
        if (c.WindowSequence != EightShort)
        {
            c.TotalSfb = _longBandCount;
            c.MaxSfb = (int)br.Read(6);
            if (br.ReadBit()) throw new AudioFormatException("An AAC-LC channel carries prediction data (predictor_data_present).");
            c.NumGroups = 1;
            c.GroupLength[0] = 1;
        }
        else
        {
            c.TotalSfb = _shortBandCount;
            c.MaxSfb = (int)br.Read(4);
            int grouping = (int)br.Read(7);
            c.NumGroups = 1;
            c.GroupLength[0] = 1;
            for (int i = 0; i < 7; i++)
            {
                if ((grouping & (1 << (6 - i))) != 0) c.GroupLength[c.NumGroups - 1]++;
                else c.GroupLength[c.NumGroups++] = 1;
            }
        }
        if (c.MaxSfb > c.TotalSfb) throw new AudioFormatException("An AAC channel has max_sfb beyond the scalefactor bands of its sampling rate.");
    }

    // 14496-3 4.4.2.1 section_data
    private static void ReadSectionData(ref AacBitReader br, AacChannelState c)
    {
        int lenBits = c.IsShort ? 3 : 5;
        int escape = (1 << lenBits) - 1;
        Array.Clear(c.Codebook);
        for (int g = 0; g < c.NumGroups; g++)
        {
            int band = 0;
            while (band < c.MaxSfb)
            {
                int cb = (int)br.Read(4);
                int len = 0;
                int incr = (int)br.Read(lenBits);
                while (incr == escape)
                {
                    len += escape;
                    if (br.Overrun) throw new AudioFormatException("An AAC access unit is truncated.");
                    incr = (int)br.Read(lenBits);
                }
                len += incr;
                if (cb == ReservedBook) throw new AudioFormatException("An AAC section uses the reserved codebook 12.");
                if (len == 0 || band + len > c.MaxSfb) throw new AudioFormatException("An AAC section runs past max_sfb.");
                for (int i = 0; i < len; i++) c.Codebook[g * 16 + band + i] = (byte)cb;
                band += len;
            }
        }
    }

    // 14496-3 4.4.2.5 scale_factor_data and 4.6.2.3 decoding of scalefactors
    private static void ReadScaleFactors(ref AacBitReader br, AacChannelState c, int globalGain)
    {
        var huff = AacHuffman.Books[12]!;
        int factor = globalGain;
        int position = 0;
        int noise = 0;
        bool noiseStarted = false;
        Array.Clear(c.Scale);
        for (int g = 0; g < c.NumGroups; g++)
        {
            for (int b = 0; b < c.MaxSfb; b++)
            {
                int i = g * 16 + b;
                switch (c.Codebook[i])
                {
                    case ZeroBook:
                        break;
                    case NoiseBook:
                        if (!noiseStarted)
                        {
                            // 4.6.13.2: the first noise energy is a 9-bit value relative to global_gain - 90.
                            noise = globalGain - 90 + (int)br.Read(9) - 256;
                            noiseStarted = true;
                        }
                        else
                        {
                            noise += huff.Decode(ref br) - 60;
                        }
                        c.Scale[i] = (short)noise;
                        break;
                    case IntensityBook:
                    case IntensityBook2:
                        position += huff.Decode(ref br) - 60;
                        if (position is < -1000 or > 1000) throw new AudioFormatException("An AAC intensity position is out of range.");
                        c.Scale[i] = (short)position;
                        break;
                    default:
                        factor += huff.Decode(ref br) - 60;
                        if (factor is < 0 or > 255) throw new AudioFormatException("An AAC scalefactor is out of range.");
                        c.Scale[i] = (short)factor;
                        break;
                }
            }
            if (br.Overrun) throw new AudioFormatException("An AAC access unit is truncated.");
        }
    }

    // 14496-3 4.4.2.2 / 4.6.9 tns_data
    private static void ReadTns(ref AacBitReader br, AacChannelState c)
    {
        bool isShort = c.IsShort;
        int windows = c.NumWindows;
        for (int w = 0; w < windows; w++)
        {
            int filters = (int)br.Read(isShort ? 1 : 2);
            c.TnsFilterCount[w] = filters;
            if (filters == 0) continue;
            int coefRes = (int)br.Read(1);
            int next = c.TotalSfb;
            for (int f = 0; f < filters; f++)
            {
                int slot = w * 3 + f;
                int length = (int)br.Read(isShort ? 4 : 6);
                int order = (int)br.Read(isShort ? 3 : 5);
                c.TnsStop[slot] = next;
                c.TnsStart[slot] = Math.Max(0, next - length);
                next = c.TnsStart[slot];
                c.TnsOrder[slot] = order;
                c.TnsDirection[slot] = 1;
                if (order == 0) continue;
                c.TnsDirection[slot] = br.ReadBit() ? -1 : 1;
                int compress = (int)br.Read(1);
                int bits = coefRes + 3;
                int stored = bits - compress;
                // 4.6.9.2: the index q is a signed stored-width value; q >= 0 maps with the half step above, q < 0 with the half step below.
                double posScale = (Math.PI / 2) / ((1 << (bits - 1)) - 0.5);
                double negScale = (Math.PI / 2) / ((1 << (bits - 1)) + 0.5);
                for (int i = 0; i < order; i++)
                {
                    int q = (int)br.Read(stored);
                    if ((q & (1 << (stored - 1))) != 0) q -= 1 << stored;
                    c.TnsCoef[slot * 32 + i] = (float)Math.Sin(q * (q >= 0 ? posScale : negScale));
                }
            }
        }
    }

    private static float[] BuildPow43()
    {
        var t = new float[8192 + 32];
        for (int i = 0; i < t.Length; i++) t[i] = (float)Math.Pow(i, 4.0 / 3.0);
        return t;
    }

    private static float PowQuarter(int e) => MathF.ScaleB(QuarterSteps[e & 3], e >> 2);
}
