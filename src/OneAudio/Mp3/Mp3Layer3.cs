using OneAudio.Kernels;

namespace OneAudio.Mp3;

/// <summary>
/// Constant tables derived once at start-up: the requantization power law, the IMDCT basis, the transform windows,
/// the polyphase matrixing basis and synthesis window, and the Huffman code tables.
/// </summary>
internal static class Mp3Constants
{
    /// <summary>|is|^(4/3) for |is| up to 15 + 8191 (the largest escape value).</summary>
    public static readonly float[] Pow43 = BuildPow43();

    /// <summary>Quarter-step gain: <c>2^(q/4)</c> at index <c>q + GainBias</c>.</summary>
    public static readonly float[] Gain = BuildGain();
    public const int GainBias = 512;

    /// <summary>Preemphasis added to scalefactors when preflag is set (long blocks, bands 0 to 21).</summary>
    public static readonly int[] Pretab = [0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 1, 1, 1, 1, 2, 2, 3, 3, 3, 2, 0];

    /// <summary>Row length of the 36-point IMDCT basis, padded so every vector width divides it.</summary>
    public const int Imdct36Stride = 40;

    /// <summary>IMDCT basis for long blocks: row k holds <c>cos(pi/72 * (2i + 1 + 18) * (2k + 1))</c> for output i.</summary>
    public static readonly float[] Imdct36 = BuildImdct(36, Imdct36Stride);

    /// <summary>IMDCT basis for short blocks: row k holds <c>cos(pi/24 * (2i + 1 + 6) * (2k + 1))</c> for output i (12 outputs).</summary>
    public static readonly float[] Imdct12 = BuildImdct(12, 12);

    /// <summary>Block windows: 0 normal, 1 start, 2 short (12 taps), 3 stop. Long windows have 36 taps.</summary>
    public static readonly float[][] Windows = BuildWindows();

    /// <summary>Alias reduction butterflies cs[i], ca[i] from the coefficients c[i].</summary>
    public static readonly float[] AliasCs = BuildAlias(cs: true);
    public static readonly float[] AliasCa = BuildAlias(cs: false);

    /// <summary>Polyphase matrixing basis: row k holds <c>cos((16 + i)(2k + 1) pi / 64)</c> for i in 0..63.</summary>
    public static readonly float[] Matrixing = BuildMatrixing();

    /// <summary>Synthesis window D[i] (a multiple of 2^-16 at every tap).</summary>
    public static readonly float[] SynthWindow = BuildSynthWindow();

    public static readonly Mp3HuffmanTable?[] BigTables = BuildBigTables();
    public static readonly Mp3HuffmanTable QuadA = new(Mp3Tables.HuffA, 0, 0);
    public static readonly Mp3HuffmanTable QuadB = new(Mp3Tables.HuffB, 0, 0);

    private static float[] BuildPow43()
    {
        var t = new float[8207];
        for (int i = 0; i < t.Length; i++) t[i] = (float)Math.Pow(i, 4.0 / 3.0);
        return t;
    }

    private static float[] BuildGain()
    {
        var t = new float[GainBias + 64];
        for (int i = 0; i < t.Length; i++) t[i] = (float)Math.Pow(2.0, (i - GainBias) / 4.0);
        return t;
    }

    private static float[] BuildImdct(int n, int stride)
    {
        int half = n / 2;
        var t = new float[half * stride];
        for (int k = 0; k < half; k++)
            for (int i = 0; i < n; i++)
                t[k * stride + i] = (float)Math.Cos(Math.PI / (2 * n) * (2 * i + 1 + half) * (2 * k + 1));
        return t;
    }

    private static float[][] BuildWindows()
    {
        var normal = new float[36];
        var start = new float[36];
        var stop = new float[36];
        var shortW = new float[12];
        for (int i = 0; i < 36; i++) normal[i] = (float)Math.Sin(Math.PI / 36 * (i + 0.5));
        for (int i = 0; i < 12; i++) shortW[i] = (float)Math.Sin(Math.PI / 12 * (i + 0.5));
        for (int i = 0; i < 18; i++) start[i] = normal[i];
        for (int i = 18; i < 24; i++) start[i] = 1f;
        for (int i = 24; i < 30; i++) start[i] = (float)Math.Sin(Math.PI / 12 * (i - 18 + 0.5));
        for (int i = 6; i < 12; i++) stop[i] = (float)Math.Sin(Math.PI / 12 * (i - 6 + 0.5));
        for (int i = 12; i < 18; i++) stop[i] = 1f;
        for (int i = 18; i < 36; i++) stop[i] = normal[i];
        return [normal, start, shortW, stop];
    }

    private static float[] BuildAlias(bool cs)
    {
        var t = new float[8];
        for (int i = 0; i < 8; i++)
        {
            double c = Mp3Tables.AliasC[i];
            double d = Math.Sqrt(1.0 + c * c);
            t[i] = (float)(cs ? 1.0 / d : c / d);
        }
        return t;
    }

    private static float[] BuildMatrixing()
    {
        var t = new float[32 * 64];
        for (int k = 0; k < 32; k++)
            for (int i = 0; i < 64; i++)
                t[k * 64 + i] = (float)Math.Cos((16 + i) * (2 * k + 1) * Math.PI / 64);
        return t;
    }

    private static float[] BuildSynthWindow()
    {
        var t = new float[512];
        for (int i = 0; i < 512; i++) t[i] = Mp3Tables.SynthesisWindow65536[i] / 65536f;
        return t;
    }

    private static Mp3HuffmanTable?[] BuildBigTables()
    {
        var t = new Mp3HuffmanTable?[32];
        t[1] = new(Mp3Tables.Huff1, 2, 0);
        t[2] = new(Mp3Tables.Huff2, 3, 0);
        t[3] = new(Mp3Tables.Huff3, 3, 0);
        t[5] = new(Mp3Tables.Huff5, 4, 0);
        t[6] = new(Mp3Tables.Huff6, 4, 0);
        t[7] = new(Mp3Tables.Huff7, 6, 0);
        t[8] = new(Mp3Tables.Huff8, 6, 0);
        t[9] = new(Mp3Tables.Huff9, 6, 0);
        t[10] = new(Mp3Tables.Huff10, 8, 0);
        t[11] = new(Mp3Tables.Huff11, 8, 0);
        t[12] = new(Mp3Tables.Huff12, 8, 0);
        t[13] = new(Mp3Tables.Huff13, 16, 0);
        t[15] = new(Mp3Tables.Huff15, 16, 0);
        int[] lin16 = [1, 2, 3, 4, 6, 8, 10, 13];
        int[] lin24 = [4, 5, 6, 7, 8, 9, 11, 13];
        for (int i = 0; i < 8; i++)
        {
            t[16 + i] = new(Mp3Tables.Huff16, 16, lin16[i]);
            t[24 + i] = new(Mp3Tables.Huff24, 16, lin24[i]);
        }
        return t;
    }
}

/// <summary>Per granule and channel side information (Table 4 of the format description), plus decoded scalefactors.</summary>
internal sealed class Mp3GranuleChannel
{
    public int Part23Length, BigValues, GlobalGain, ScalefacCompress;
    public bool WindowSwitching, Mixed, Preflag;
    public int BlockType, ScalefacScale, Count1Table, Region0Count, Region1Count, IntensityScale;
    public readonly int[] TableSelect = new int[3];
    public readonly int[] SubblockGain = new int[3];

    /// <summary>Long-block scalefactors, bands 0..21 (band 21 is always zero).</summary>
    public readonly int[] SfL = new int[23];
    /// <summary>Short-block scalefactors at index <c>band * 3 + window</c>, bands 0..12 (band 12 is always zero).</summary>
    public readonly int[] SfS = new int[39];
    /// <summary>Largest legal value (<c>2^slen - 1</c>) of each scalefactor in reading order; used to spot illegal intensity positions in MPEG-2.</summary>
    public readonly int[] SfMaxL = new int[23];
    public readonly int[] SfMaxS = new int[39];

    public bool IsShort => WindowSwitching && BlockType == 2;
}

/// <summary>
/// Layer III frame decoder: side information, bit reservoir, scalefactors, Huffman decoding, requantization, stereo
/// processing, alias reduction, IMDCT with overlap-add, and the polyphase synthesis filterbank, for MPEG-1, MPEG-2 and
/// MPEG-2.5. The steps and tables follow the public Layer III description; the requantization and filterbank
/// equations are the standard's published mathematics. All working memory is allocated at construction.
/// A granule that cannot be decoded (reservoir underflow after a seek or a damaged frame) decodes as silence
/// into the filterbank state, which then rings out naturally.
/// </summary>
internal sealed class Mp3Layer3
{
    private const int MaxHistory = 511;      // largest main_data_begin (9 bits)
    private const float InvSqrt2 = 0.70710678118654752f;

    private readonly byte[] _res;            // reservoir: kept history followed by the current frame's payload
    private int _resLen;

    private readonly Mp3GranuleChannel[,] _gc = new Mp3GranuleChannel[2, 2];
    private readonly int[,] _scfsi = new int[2, 4];
    private readonly int[][] _is = [new int[576 + 4], new int[576 + 4]];
    private readonly float[][] _xr = [new float[576], new float[576]];     // requantized, reading order
    private readonly float[][] _spec = [new float[576], new float[576]];   // after reorder and alias reduction
    private readonly float[][] _overlap = [new float[576], new float[576]];
    private readonly float[][] _sub = [new float[18 * 32], new float[18 * 32]]; // subband samples, [t * 32 + sb]
    private readonly float[] _kl = new float[576];
    private readonly float[] _kr = new float[576];
    private readonly byte[] _lineKind = new byte[576]; // 0 plain or mid/side, 1 intensity
    private readonly int[] _entryEnd = new int[3 * 13 + 24];
    private readonly int[] _partLen = new int[4];
    private readonly int[] _partCount = new int[4];

    // Polyphase state per channel: ring of the last 16 matrixed vectors.
    private readonly float[][] _v = [new float[1024], new float[1024]];
    private readonly int[] _vOff = new int[2];
    private readonly float[] _v64 = new float[64];
    private readonly float[] _out32 = new float[32];
    private readonly float[] _acc40 = new float[Mp3Constants.Imdct36Stride];
    private readonly float[] _acc12 = new float[12];
    private readonly float[] _tmp36 = new float[36];

    public Mp3Layer3(int maxPayloadBytes)
    {
        _res = new byte[MaxHistory + maxPayloadBytes + 16];
        for (int g = 0; g < 2; g++)
            for (int c = 0; c < 2; c++) _gc[g, c] = new Mp3GranuleChannel();
        Reset();
    }

    /// <summary>Forgets the reservoir and all filterbank memory, as at the start of a stream.</summary>
    public void Reset()
    {
        _resLen = 0;
        for (int c = 0; c < 2; c++)
        {
            Array.Clear(_overlap[c]);
            Array.Clear(_v[c]);
            _vOff[c] = 0;
        }
    }

    /// <summary>
    /// Decodes one frame. <paramref name="frame"/> is the whole frame including its header. Writes
    /// <c>SamplesPerFrame * channels</c> interleaved floats to <paramref name="pcm"/>.
    /// </summary>
    public void DecodeFrame(in Mp3Header h, ReadOnlySpan<byte> frame, Span<float> pcm)
    {
        int ch = h.Channels;
        int granules = h.GranulesPerFrame;
        int sideStart = h.HeaderBytes;
        int sideLen = h.SideInfoBytes;
        bool sideOk = frame.Length >= sideStart + sideLen;

        int mainBegin = 0;
        if (sideOk) sideOk = ParseSideInfo(h, frame.Slice(sideStart, sideLen), out mainBegin);

        // Append this frame's payload to the reservoir, then work out where its main data starts.
        bool haveData = false;
        long dataBit = 0;
        if (sideOk)
        {
            var payload = frame[(sideStart + sideLen)..];
            if (_resLen + payload.Length > _res.Length) _resLen = Math.Min(_resLen, MaxHistory);
            int copy = Math.Min(payload.Length, _res.Length - _resLen);
            if (mainBegin <= _resLen)
            {
                haveData = true;
                dataBit = (long)(_resLen - mainBegin) * 8;
            }
            payload[..copy].CopyTo(_res.AsSpan(_resLen));
            _resLen += copy;
        }

        var main = _res.AsSpan(0, _resLen);
        long cursor = dataBit;
        int samplesPerGranule = 576;
        long reservoirBits = (long)_resLen * 8;
        for (int gr = 0; gr < granules; gr++)
        {
            bool good = haveData;
            for (int c = 0; c < ch; c++)
            {
                var gc = _gc[gr, c];
                long start = cursor;
                long end = start + gc.Part23Length;
                cursor = end;
                if (!good) continue;
                if (end > reservoirBits) good = false;
                else good = DecodeGranuleChannel(h, gr, c, gc, main, start, end);
            }
            if (good)
            {
                if (ch == 2) Stereo(h, gr);
                for (int c = 0; c < ch; c++) Reorder(h, _gc[gr, c], c);
            }
            for (int c = 0; c < ch; c++)
            {
                Transform(h, good ? _gc[gr, c] : null, c);
                Synthesize(c, pcm.Slice(gr * samplesPerGranule * ch), ch);
            }
        }

        // Keep only what a later frame's main_data_begin can reach.
        if (_resLen > MaxHistory)
        {
            Buffer.BlockCopy(_res, _resLen - MaxHistory, _res, 0, MaxHistory);
            _resLen = MaxHistory;
        }
    }

    // ------------------------------------------------------------------ side information

    private bool ParseSideInfo(in Mp3Header h, ReadOnlySpan<byte> side, out int mainBegin)
    {
        var br = new Mp3BitReader(side);
        bool mpeg1 = h.Version == MpegVersion.Mpeg1;
        int ch = h.Channels;
        mainBegin = (int)br.Read(mpeg1 ? 9 : 8);
        br.Read(mpeg1 ? (ch == 1 ? 5 : 3) : (ch == 1 ? 1 : 2)); // private bits
        if (mpeg1)
            for (int c = 0; c < ch; c++)
                for (int b = 0; b < 4; b++) _scfsi[c, b] = (int)br.Read(1);
        else
            for (int c = 0; c < 2; c++)
                for (int b = 0; b < 4; b++) _scfsi[c, b] = 0;

        int granules = h.GranulesPerFrame;
        bool valid = true;
        for (int gr = 0; gr < granules; gr++)
            for (int c = 0; c < ch; c++)
            {
                var g = _gc[gr, c];
                g.Part23Length = (int)br.Read(12);
                g.BigValues = (int)br.Read(9);
                g.GlobalGain = (int)br.Read(8);
                g.ScalefacCompress = (int)br.Read(mpeg1 ? 4 : 9);
                g.WindowSwitching = br.Read(1) == 1;
                g.BlockType = 0;
                g.Mixed = false;
                if (g.WindowSwitching)
                {
                    g.BlockType = (int)br.Read(2);
                    g.Mixed = br.Read(1) == 1;
                    g.TableSelect[0] = (int)br.Read(5);
                    g.TableSelect[1] = (int)br.Read(5);
                    g.TableSelect[2] = 0;
                    for (int w = 0; w < 3; w++) g.SubblockGain[w] = (int)br.Read(3);
                    if (g.BlockType == 0) valid = false; // forbidden when window switching is on
                    // Region sizes are implied (see Huffman region setup).
                    g.Region0Count = g.BlockType == 2 && !g.Mixed ? 8 : 7;
                    g.Region1Count = 36;
                }
                else
                {
                    for (int r = 0; r < 3; r++) g.TableSelect[r] = (int)br.Read(5);
                    g.Region0Count = (int)br.Read(4);
                    g.Region1Count = (int)br.Read(3);
                    for (int w = 0; w < 3; w++) g.SubblockGain[w] = 0;
                }
                g.Preflag = mpeg1 && br.Read(1) == 1;
                g.ScalefacScale = (int)br.Read(1);
                g.Count1Table = (int)br.Read(1);
                if (g.BigValues > 288) valid = false;
            }
        return valid;
    }

    // ------------------------------------------------------------------ one granule of one channel

    private bool DecodeGranuleChannel(in Mp3Header h, int gr, int ch, Mp3GranuleChannel g, ReadOnlySpan<byte> main, long start, long end)
    {
        var br = new Mp3BitReader(main, start);
        if (h.Version == MpegVersion.Mpeg1) ReadScalefactorsMpeg1(gr, ch, g, ref br);
        else ReadScalefactorsMpeg2(h, g, ch, ref br);
        if (br.Position > end) return false;
        if (!ReadHuffman(h, g, ref br, end, _is[ch])) return false;
        Requantize(h, g, _is[ch], _xr[ch]);
        return true;
    }

    private static readonly int[] GroupStart = [0, 6, 11, 16, 21];
    private static readonly byte[] Slen1Mpeg1 = [0, 0, 0, 0, 3, 1, 1, 1, 2, 2, 2, 3, 3, 3, 4, 4];
    private static readonly byte[] Slen2Mpeg1 = [0, 1, 2, 3, 0, 1, 2, 3, 1, 2, 3, 1, 2, 3, 2, 3];

    private void ReadScalefactorsMpeg1(int gr, int ch, Mp3GranuleChannel g, ref Mp3BitReader br)
    {
        int slen1 = Slen1Mpeg1[g.ScalefacCompress], slen2 = Slen2Mpeg1[g.ScalefacCompress];
        if (g.IsShort)
        {
            int longBands = g.Mixed ? 8 : 0;
            for (int sfb = 0; sfb < longBands; sfb++) g.SfL[sfb] = (int)br.Read(slen1);
            int firstShort = g.Mixed ? 3 : 0;
            for (int sfb = firstShort; sfb < 12; sfb++)
            {
                int len = sfb < 6 ? slen1 : slen2;
                for (int w = 0; w < 3; w++) g.SfS[sfb * 3 + w] = (int)br.Read(len);
            }
            g.SfS[36] = g.SfS[37] = g.SfS[38] = 0;
            return;
        }
        // Long blocks: four groups of bands, each optionally shared with granule 0 (scfsi).
        var prev = _gc[0, ch];
        for (int grp = 0; grp < 4; grp++)
        {
            int len = grp < 2 ? slen1 : slen2;
            bool reuse = gr == 1 && _scfsi[ch, grp] == 1;
            for (int sfb = GroupStart[grp]; sfb < GroupStart[grp + 1]; sfb++)
                g.SfL[sfb] = reuse ? prev.SfL[sfb] : (int)br.Read(len);
        }
        g.SfL[21] = 0;
    }

    private void ReadScalefactorsMpeg2(in Mp3Header h, Mp3GranuleChannel g, int ch, ref Mp3BitReader br)
    {
        int sc = g.ScalefacCompress;
        bool intensityChannel = ch == 1 && (h.ModeBits == 1) && (h.ModeExtension & 1) != 0;
        int kind = g.IsShort ? (g.Mixed ? 2 : 1) : 0; // 0 long, 1 short, 2 mixed
        Span<int> slen = stackalloc int[4];
        Span<int> nr = stackalloc int[4];
        g.Preflag = false;
        if (!intensityChannel)
        {
            g.IntensityScale = 0;
            if (sc < 400)
            {
                slen[0] = (sc >> 4) / 5; slen[1] = (sc >> 4) % 5; slen[2] = (sc % 16) >> 2; slen[3] = sc % 4;
                Counts(kind, nr, [6, 5, 5, 5], [9, 9, 9, 9], [6, 9, 9, 9]);
            }
            else if (sc < 500)
            {
                int s = sc - 400;
                slen[0] = (s >> 2) / 5; slen[1] = (s >> 2) % 5; slen[2] = s % 4; slen[3] = 0;
                Counts(kind, nr, [6, 5, 7, 3], [9, 9, 12, 6], [6, 9, 12, 6]);
            }
            else
            {
                int s = sc - 500;
                slen[0] = s / 3; slen[1] = s % 3; slen[2] = 0; slen[3] = 0;
                g.Preflag = true;
                Counts(kind, nr, [11, 10, 0, 0], [18, 18, 0, 0], [15, 18, 0, 0]);
            }
        }
        else
        {
            g.IntensityScale = sc & 1;
            int isc = sc >> 1;
            if (isc < 180)
            {
                slen[0] = isc / 36; slen[1] = (isc % 36) / 6; slen[2] = (isc % 36) % 6; slen[3] = 0;
                Counts(kind, nr, [7, 7, 7, 0], [12, 12, 12, 0], [6, 15, 12, 0]);
            }
            else if (isc < 244)
            {
                int s = isc - 180;
                slen[0] = (s % 64) >> 4; slen[1] = (s % 16) >> 2; slen[2] = s % 4; slen[3] = 0;
                Counts(kind, nr, [6, 6, 6, 3], [12, 9, 9, 6], [6, 12, 9, 6]);
            }
            else
            {
                int s = isc - 244;
                slen[0] = s / 3; slen[1] = s % 3; slen[2] = 0; slen[3] = 0;
                Counts(kind, nr, [8, 8, 5, 0], [15, 12, 9, 0], [6, 18, 9, 0]);
            }
        }
        if (g.IsShort && g.Preflag) g.Preflag = false; // preemphasis is never applied to short blocks

        Array.Clear(g.SfL); Array.Clear(g.SfS); Array.Clear(g.SfMaxL); Array.Clear(g.SfMaxS);
        int index = 0;
        int longValues = kind == 0 ? 21 : kind == 2 ? 6 : 0;
        for (int p = 0; p < 4; p++)
        {
            int max = slen[p] == 0 ? -1 : (1 << slen[p]) - 1;
            for (int i = 0; i < nr[p]; i++, index++)
            {
                int v = (int)br.Read(slen[p]);
                if (index < longValues) { g.SfL[index] = v; g.SfMaxL[index] = max; }
                else
                {
                    int s = index - longValues;
                    int sfb = (kind == 2 ? 3 : 0) + s / 3;
                    if (sfb < 12) { g.SfS[sfb * 3 + s % 3] = v; g.SfMaxS[sfb * 3 + s % 3] = max; }
                }
            }
        }
    }

    private static void Counts(int kind, Span<int> nr, ReadOnlySpan<int> longC, ReadOnlySpan<int> shortC, ReadOnlySpan<int> mixedC)
    {
        var src = kind == 0 ? longC : kind == 1 ? shortC : mixedC;
        for (int i = 0; i < 4; i++) nr[i] = src[i];
    }

    // ------------------------------------------------------------------ Huffman

    private static bool ReadHuffman(in Mp3Header h, Mp3GranuleChannel g, ref Mp3BitReader br, long end, int[] isv)
    {
        int bandIdx = h.BandTableIndex;
        int[] longB = Mp3Tables.LongBands[bandIdx];
        int[] shortB = Mp3Tables.ShortBands[bandIdx];
        int bigEnd = Math.Min(g.BigValues * 2, 576);

        // Region boundaries in lines. Short blocks count every band three times (once per window), in reading order.
        int r0, r1;
        if (g.WindowSwitching)
        {
            if (g.BlockType == 2 && !g.Mixed) r0 = 3 * shortB[3];
            else if (g.BlockType == 2 && h.Version != MpegVersion.Mpeg1) r0 = longB[6] + 2 * (shortB[4] - shortB[3]);
            else r0 = longB[8];
            r1 = bigEnd;
        }
        else
        {
            r0 = longB[Math.Min(g.Region0Count + 1, 22)];
            r1 = longB[Math.Min(g.Region0Count + g.Region1Count + 2, 22)];
        }
        r0 = Math.Min(r0, bigEnd);
        r1 = Math.Min(Math.Max(r1, r0), bigEnd);

        int line = 0;
        for (int region = 0; region < 3; region++)
        {
            int regionEnd = region == 0 ? r0 : region == 1 ? r1 : bigEnd;
            int ts = g.TableSelect[region];
            if (ts == 0)
            {
                for (; line < regionEnd; line++) isv[line] = 0;
                continue;
            }
            var table = Mp3Constants.BigTables[ts];
            if (table is null) return false; // tables 4 and 14 do not exist
            int lin = table.LinBits;
            for (; line < regionEnd; line += 2)
            {
                int sym = table.Decode(ref br);
                if (sym < 0) return false;
                int x = sym >> 4, y = sym & 15;
                if (x == 15 && lin > 0) x += (int)br.Read(lin);
                if (x != 0 && br.ReadBit() != 0) x = -x;
                if (y == 15 && lin > 0) y += (int)br.Read(lin);
                if (y != 0 && br.ReadBit() != 0) y = -y;
                isv[line] = x;
                isv[line + 1] = y;
            }
            if (br.Position > end) return false;
        }

        // count1 region: quadruples of -1, 0, 1 until the declared bits run out.
        var quad = g.Count1Table == 0 ? Mp3Constants.QuadA : Mp3Constants.QuadB;
        while (line <= 572 && br.Position < end)
        {
            int sym = quad.Decode(ref br);
            if (sym < 0) return false;
            int v = (sym >> 3) & 1, w = (sym >> 2) & 1, x = (sym >> 1) & 1, y = sym & 1;
            if (v != 0 && br.ReadBit() != 0) v = -v;
            if (w != 0 && br.ReadBit() != 0) w = -w;
            if (x != 0 && br.ReadBit() != 0) x = -x;
            if (y != 0 && br.ReadBit() != 0) y = -y;
            if (br.Position > end) break; // the last quadruple would overrun the granule: treat it as padding
            isv[line] = v; isv[line + 1] = w; isv[line + 2] = x; isv[line + 3] = y;
            line += 4;
        }
        for (int i = line; i < 576; i++) isv[i] = 0;
        return true;
    }

    // ------------------------------------------------------------------ requantization

    private static void Requantize(in Mp3Header h, Mp3GranuleChannel g, int[] isv, float[] xr)
    {
        int bandIdx = h.BandTableIndex;
        int[] longB = Mp3Tables.LongBands[bandIdx];
        int[] shortB = Mp3Tables.ShortBands[bandIdx];
        float[] gain = Mp3Constants.Gain;
        float[] pow43 = Mp3Constants.Pow43;
        int q0 = g.GlobalGain - 210;
        int stepMul = 2 * (1 + g.ScalefacScale);

        int longBandsToDo = g.IsShort ? (g.Mixed ? (h.Version == MpegVersion.Mpeg1 ? 8 : 6) : 0) : 22;
        int line = 0;
        for (int sfb = 0; sfb < longBandsToDo; sfb++)
        {
            int sf = g.SfL[sfb] + (g.Preflag ? Mp3Constants.Pretab[sfb] : 0);
            float gn = GainAt(gain, q0 - stepMul * sf);
            for (int end = longB[sfb + 1]; line < end; line++)
            {
                int v = isv[line];
                xr[line] = v == 0 ? 0f : v > 0 ? pow43[v] * gn : -pow43[-v] * gn;
            }
        }
        if (g.IsShort)
        {
            int first = g.Mixed ? 3 : 0;
            for (int sfb = first; sfb < 13; sfb++)
            {
                int width = shortB[sfb + 1] - shortB[sfb];
                for (int w = 0; w < 3; w++)
                {
                    int q = q0 - 8 * g.SubblockGain[w] - stepMul * g.SfS[sfb * 3 + w];
                    float gn = GainAt(gain, q);
                    for (int i = 0; i < width; i++, line++)
                    {
                        int v = isv[line];
                        xr[line] = v == 0 ? 0f : v > 0 ? pow43[v] * gn : -pow43[-v] * gn;
                    }
                }
            }
        }
    }

    private static float GainAt(float[] gain, int q)
    {
        int i = q + Mp3Constants.GainBias;
        return (uint)i < (uint)gain.Length ? gain[i] : (i < 0 ? 0f : gain[^1]);
    }

    // ------------------------------------------------------------------ stereo

    private void Stereo(in Mp3Header h, int gr)
    {
        if (h.ModeBits != 1) return;
        bool intensity = (h.ModeExtension & 1) != 0, ms = (h.ModeExtension & 2) != 0;
        if (!intensity && !ms) return;
        var g0 = _gc[gr, 0];
        var g1 = _gc[gr, 1];
        if (g0.BlockType != g1.BlockType || g0.WindowSwitching != g1.WindowSwitching || g0.Mixed != g1.Mixed) return;

        float[] l = _xr[0], r = _xr[1];
        Array.Clear(_lineKind);
        if (intensity) MarkIntensity(h, g1, _is[1]);

        for (int i = 0; i < 576; i++)
        {
            if (_lineKind[i] == 1)
            {
                float m = l[i];
                l[i] = m * _kl[i];
                r[i] = m * _kr[i];
            }
            else if (ms)
            {
                float a = l[i], b = r[i];
                l[i] = (a + b) * InvSqrt2;
                r[i] = (a - b) * InvSqrt2;
            }
        }
    }

    /// <summary>
    /// Finds the lines coded as intensity stereo: everything above the last scalefactor band in which the right channel
    /// has a nonzero value (per window for short blocks), unless the band's position is the illegal value, in which
    /// case the band is decoded as mid/side or left/right instead.
    /// </summary>
    private void MarkIntensity(in Mp3Header h, Mp3GranuleChannel right, int[] isRight)
    {
        bool mpeg1 = h.Version == MpegVersion.Mpeg1;
        int[] longB = Mp3Tables.LongBands[h.BandTableIndex];
        int[] shortB = Mp3Tables.ShortBands[h.BandTableIndex];
        if (!right.IsShort)
        {
            int last = -1;
            for (int b = 0; b < 22; b++)
                for (int i = longB[b]; i < longB[b + 1]; i++)
                    if (isRight[i] != 0) { last = b; break; }
            for (int b = last + 1; b < 22; b++)
            {
                int sb = Math.Min(b, 20);
                int pos = right.SfL[sb];
                int max = right.SfMaxL[sb];
                SetIntensity(h, mpeg1, pos, max, right.IntensityScale, longB[b], longB[b + 1]);
            }
            return;
        }
        int firstShort = right.Mixed ? 3 : 0;
        bool anyShortData = false;
        if (right.Mixed)
        {
            // The long part of a mixed block is shared by all three windows. It only reaches into intensity coding when
            // the right channel has nothing at all in the short part.
            for (int w = 0; w < 3 && !anyShortData; w++)
                for (int b = 3; b < 13 && !anyShortData; b++)
                {
                    int width = shortB[b + 1] - shortB[b];
                    int basePos = 3 * shortB[b] + w * width;
                    for (int i = 0; i < width; i++)
                        if (isRight[basePos + i] != 0) { anyShortData = true; break; }
                }
            if (!anyShortData)
            {
                int nLong = mpeg1 ? 8 : 6;
                int lastLong = -1;
                for (int b = 0; b < nLong; b++)
                    for (int i = longB[b]; i < longB[b + 1]; i++)
                        if (isRight[i] != 0) { lastLong = b; break; }
                for (int b = lastLong + 1; b < nLong; b++)
                    SetIntensity(h, mpeg1, right.SfL[b], right.SfMaxL[b], right.IntensityScale, longB[b], longB[b + 1]);
            }
        }
        for (int w = 0; w < 3; w++)
        {
            int last = firstShort - 1;
            for (int b = firstShort; b < 13; b++)
            {
                int width = shortB[b + 1] - shortB[b];
                int basePos = 3 * shortB[b] + w * width;
                for (int i = 0; i < width; i++)
                    if (isRight[basePos + i] != 0) { last = b; break; }
            }
            for (int b = last + 1; b < 13; b++)
            {
                int width = shortB[b + 1] - shortB[b];
                int sb = Math.Min(b, 11);
                int pos = right.SfS[sb * 3 + w];
                int max = right.SfMaxS[sb * 3 + w];
                SetIntensity(h, mpeg1, pos, max, right.IntensityScale, 3 * shortB[b] + w * width, 3 * shortB[b] + w * width + width);
            }
        }
    }

    private void SetIntensity(in Mp3Header h, bool mpeg1, int pos, int max, int scale, int from, int to)
    {
        float kl, kr;
        if (mpeg1)
        {
            if (pos >= 7) return; // illegal position: leave the band to mid/side or left/right
            double ratio = Math.Tan(pos * Math.PI / 12);
            kl = (float)(ratio / (1 + ratio));
            kr = (float)(1 / (1 + ratio));
        }
        else
        {
            if (max >= 0 && pos == max) return; // illegal position
            double x = scale == 1 ? 1 : 2;
            if (pos == 0) { kl = 1f; kr = 1f; }
            else if ((pos & 1) == 1) { kl = (float)Math.Pow(2, -((pos + 1) / (4 * x))); kr = 1f; }
            else { kl = 1f; kr = (float)Math.Pow(2, -(pos / (4 * x))); }
        }
        for (int i = from; i < to; i++)
        {
            _lineKind[i] = 1;
            _kl[i] = kl;
            _kr[i] = kr;
        }
    }

    // ------------------------------------------------------------------ reorder, alias reduction, IMDCT

    private void Reorder(in Mp3Header h, Mp3GranuleChannel g, int ch)
    {
        float[] xr = _xr[ch];
        float[] spec = _spec[ch];
        if (!g.IsShort)
        {
            Array.Copy(xr, spec, 576);
            // Alias reduction between adjacent subbands (all 31 boundaries for long blocks).
            AliasReduce(spec, 32);
            return;
        }
        int[] shortB = Mp3Tables.ShortBands[h.BandTableIndex];
        int[] longB = Mp3Tables.LongBands[h.BandTableIndex];
        int line = 0;
        int firstShort = 0;
        if (g.Mixed)
        {
            int longLines = h.Version == MpegVersion.Mpeg1 ? longB[8] : longB[6];
            Array.Copy(xr, spec, longLines);
            line = longLines;
            firstShort = 3;
            Array.Clear(spec, longLines, 576 - longLines);
            // The ISO/IEC 11172-4 stream he_mode (frames 91 to 109: mixed blocks with a tone in the second long subband) matches
            // its reference decode to one 16-bit step only when no butterfly runs between the long subbands of a mixed
            // block; with the single 0/1 butterfly the error is up to 11 steps. So the loop stops one subband short of the
            // long part (sb_amount - 1): nothing at the usual two long subbands, two boundaries for the four of 8 kHz MPEG-2.5
            // (that last case has no reference stream to check against).
            AliasReduce(spec, (h.SampleRate == 8000 && h.Version == MpegVersion.Mpeg25 ? 4 : 2) - 1);
        }
        else Array.Clear(spec);
        for (int sfb = firstShort; sfb < 13; sfb++)
        {
            int width = shortB[sfb + 1] - shortB[sfb];
            for (int w = 0; w < 3; w++)
                for (int i = 0; i < width; i++)
                {
                    int f = shortB[sfb] + i;
                    spec[(f / 6) * 18 + w * 6 + f % 6] = xr[line++];
                }
        }
    }

    private static void AliasReduce(float[] x, int subbandAmount)
    {
        float[] cs = Mp3Constants.AliasCs, ca = Mp3Constants.AliasCa;
        for (int sb = 1; sb < subbandAmount; sb++)
            for (int i = 0; i < 8; i++)
            {
                int lo = 18 * sb - 1 - i, hi = 18 * sb + i;
                float a = x[lo], b = x[hi];
                x[lo] = a * cs[i] - b * ca[i];
                x[hi] = b * cs[i] + a * ca[i];
            }
    }

    /// <summary>
    /// IMDCT, windowing and overlap-add for all 32 subbands of one channel, then the frequency-inversion fix-up.
    /// A null granule means "decode as silence".
    /// </summary>
    private void Transform(in Mp3Header h, Mp3GranuleChannel? g, int ch)
    {
        float[] spec = _spec[ch];
        float[] overlap = _overlap[ch];
        float[] sub = _sub[ch];
        int mixedLongSubbands = h.Version == MpegVersion.Mpeg25 && h.SampleRate == 8000 ? 4 : 2;
        float[][] windows = Mp3Constants.Windows;
        var acc = _acc40;
        var tmp = _tmp36;

        for (int sb = 0; sb < 32; sb++)
        {
            var x = spec.AsSpan(sb * 18, 18);
            var ov = overlap.AsSpan(sb * 18, 18);
            bool allZero = true;
            for (int k = 0; k < 18; k++) if (x[k] != 0f) { allZero = false; break; }

            if (g is null || allZero)
            {
                // Output is just the stored tail; the new tail is zero.
                for (int t = 0; t < 18; t++)
                {
                    sub[t * 32 + sb] = ov[t];
                    ov[t] = 0f;
                }
            }
            else
            {
                bool shortBlock = g.IsShort && !(g.Mixed && sb < mixedLongSubbands);
                if (shortBlock)
                {
                    Array.Clear(tmp);
                    float[] win = windows[2];
                    for (int w = 0; w < 3; w++)
                    {
                        Array.Clear(_acc12);
                        for (int k = 0; k < 6; k++)
                        {
                            float c = x[w * 6 + k];
                            if (c != 0f) Mp3Kernels.MulAdd(_acc12, Mp3Constants.Imdct12.AsSpan(k * 12, 12), c);
                        }
                        for (int i = 0; i < 12; i++) tmp[6 + 6 * w + i] += _acc12[i] * win[i];
                    }
                }
                else
                {
                    Array.Clear(acc);
                    for (int k = 0; k < 18; k++)
                    {
                        float c = x[k];
                        if (c != 0f) Mp3Kernels.MulAdd(acc, Mp3Constants.Imdct36.AsSpan(k * Mp3Constants.Imdct36Stride, Mp3Constants.Imdct36Stride), c);
                    }
                    // With mixed_block_flag set, the lowest subbands always use the normal window, whatever the block type.
                    int type = g.WindowSwitching && !(g.Mixed && sb < mixedLongSubbands) ? g.BlockType : 0;
                    float[] win = windows[type == 1 ? 1 : type == 3 ? 3 : 0];
                    for (int i = 0; i < 36; i++) tmp[i] = acc[i] * win[i];
                }
                for (int t = 0; t < 18; t++)
                {
                    sub[t * 32 + sb] = tmp[t] + ov[t];
                    ov[t] = tmp[18 + t];
                }
            }
            if ((sb & 1) == 1)
                for (int t = 1; t < 18; t += 2) sub[t * 32 + sb] = -sub[t * 32 + sb];
        }
    }

    // ------------------------------------------------------------------ polyphase synthesis

    private void Synthesize(int ch, Span<float> pcm, int channels)
    {
        float[] sub = _sub[ch];
        float[] v = _v[ch];
        float[] d = Mp3Constants.SynthWindow;
        var v64 = _v64;
        var o = _out32;
        int off = _vOff[ch];
        for (int t = 0; t < 18; t++)
        {
            Array.Clear(v64);
            for (int k = 0; k < 32; k++)
            {
                float s = sub[t * 32 + k];
                if (s != 0f) Mp3Kernels.MulAdd(v64, Mp3Constants.Matrixing.AsSpan(k * 64, 64), s);
            }
            off = (off - 64) & 1023;
            v64.CopyTo(v.AsSpan(off, 64));

            Array.Clear(o);
            for (int m = 0; m < 8; m++)
            {
                Mp3Kernels.MulAddPairs(o, v.AsSpan((off + 128 * m) & 1023, 32), d.AsSpan(64 * m, 32));
                Mp3Kernels.MulAddPairs(o, v.AsSpan((off + 128 * m + 96) & 1023, 32), d.AsSpan(64 * m + 32, 32));
            }
            int baseIdx = t * 32 * channels + ch;
            for (int j = 0; j < 32; j++) pcm[baseIdx + j * channels] = o[j];
        }
        _vOff[ch] = off;
    }
}
