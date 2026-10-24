namespace OneAudio.Opus;

/// <summary>
/// The CELT layer decoder (RFC 6716 section 4.3) for the 48 kHz mode, including the pitch-based loss concealment.
/// The signal path is single-precision floating point, in the scale of 16-bit samples; output is divided by
/// 32768 to land in [-1, 1]. Derived from the reference decoder in RFC 6716 (Simplified BSD licence, see
/// THIRD-PARTY-NOTICES.md).
/// </summary>
internal sealed partial class CeltDecoder
{
    private const int DecodeBufferSize = 2048;
    private const int MaxPeriod = 1024;
    private const int LpcOrder = 24;
    private const int CombMinPeriod = 15;
    private const int NbEBands = CeltTables.NbEBands;
    private const int Overlap = CeltTables.Overlap;
    private const int ShortMdct = CeltTables.ShortMdctSize;
    private const int MaxFrame = 960;
    private const int Bitres = 3;
    private const float Pi = 3.141592653f;

    private readonly int _channels;      // output channels (CC)
    private int _streamChannels;         // channels in the stream (C)
    private readonly int _downsample;
    private int _start, _end;

    private uint _rng;
    private int _lastPitchIndex;
    private int _lossCount;
    private int _postfilterPeriod, _postfilterPeriodOld;
    private float _postfilterGain, _postfilterGainOld;
    private int _postfilterTapset, _postfilterTapsetOld;
    private readonly float[] _preemphMemD = new float[2];

    private readonly float[][] _decodeMem;
    private readonly float[] _lpc = new float[2 * LpcOrder];
    private readonly float[] _oldBandE = new float[2 * NbEBands];
    private readonly float[] _oldLogE = new float[2 * NbEBands];
    private readonly float[] _oldLogE2 = new float[2 * NbEBands];
    private readonly float[] _backgroundLogE = new float[2 * NbEBands];

    // Scratch
    private readonly float[] _freq = new float[2 * MaxFrame];
    private readonly float[] _x = new float[2 * MaxFrame];
    private readonly float[] _bandE = new float[2 * NbEBands];
    private readonly int[] _fineQuant = new int[NbEBands];
    private readonly int[] _pulses = new int[NbEBands];
    private readonly int[] _cap = new int[NbEBands];
    private readonly int[] _offsets = new int[NbEBands];
    private readonly int[] _finePriority = new int[NbEBands];
    private readonly int[] _tfRes = new int[NbEBands];
    private readonly byte[] _collapseMasks = new byte[2 * NbEBands];
    private readonly float[] _mdctIn = new float[MaxFrame + Overlap];
    private readonly float[] _mdctF = new float[960];
    private readonly float[] _mdctF2 = new float[960];
    private readonly float[] _pitchBuf = new float[DecodeBufferSize >> 1];
    private readonly float[] _exc = new float[MaxPeriod];
    private readonly float[] _e = new float[MaxPeriod + 2 * Overlap];
    private readonly float[] _ac = new float[LpcOrder + 1];
    private readonly float[] _mem = new float[LpcOrder];
    private readonly float[] _xcorr = new float[DecodeBufferSize >> 2];
    private readonly float[] _x4 = new float[DecodeBufferSize >> 3];
    private readonly float[] _y4 = new float[DecodeBufferSize >> 3];
    private readonly float[] _xx = new float[MaxPeriod];

    public CeltDecoder(int channels, int sampleRate)
    {
        _channels = channels;
        _streamChannels = channels;
        _downsample = sampleRate switch
        {
            48000 => 1,
            24000 => 2,
            16000 => 3,
            12000 => 4,
            8000 => 6,
            _ => throw new ArgumentOutOfRangeException(nameof(sampleRate)),
        };
        _start = 0;
        _end = NbEBands;
        _decodeMem = new float[channels][];
        for (int c = 0; c < channels; c++) _decodeMem[c] = new float[DecodeBufferSize + Overlap];
        Reset();
    }

    /// <summary>First coded band (0 for CELT-only, 17 for hybrid).</summary>
    public int Start { get => _start; set => _start = value; }

    /// <summary>One past the last coded band, set from the packet bandwidth.</summary>
    public int End { get => _end; set => _end = value; }

    /// <summary>Number of channels in the packet being decoded (1 or 2).</summary>
    public int StreamChannels { get => _streamChannels; set => _streamChannels = value; }

    /// <summary>Final range coder state of the last decoded frame (for conformance checks).</summary>
    public uint FinalRange => _rng;

    /// <summary>Clears all signal history, as for a new stream.</summary>
    public void Reset()
    {
        _rng = 0;
        _lastPitchIndex = 0;
        _lossCount = 0;
        _postfilterPeriod = _postfilterPeriodOld = 0;
        _postfilterGain = _postfilterGainOld = 0;
        _postfilterTapset = _postfilterTapsetOld = 0;
        Array.Clear(_preemphMemD);
        foreach (var m in _decodeMem) Array.Clear(m);
        Array.Clear(_lpc);
        Array.Clear(_oldBandE);
        Array.Clear(_backgroundLogE);
        Array.Fill(_oldLogE, -28f);
        Array.Fill(_oldLogE2, -28f);
    }

    // ------------------------------------------------------------------------------------------------
    // Frame decoding (celt_decode_with_ec)
    // ------------------------------------------------------------------------------------------------

    /// <summary>Decodes one frame from its own bytes.</summary>
    /// <returns>Samples per channel written, at the output rate.</returns>
    public int Decode(ReadOnlySpan<byte> data, Span<float> pcm, int frameSize)
    {
        var rd = new RangeDecoder(data);
        return DecodeFrame(ref rd, data.Length, pcm, frameSize);
    }

    /// <summary>Conceals a lost frame.</summary>
    public int DecodeLost(Span<float> pcm, int frameSize)
    {
        frameSize *= _downsample;
        int lm = FindLM(frameSize);
        DecodeLostFrame(pcm, frameSize, lm);
        return frameSize / _downsample;
    }

    private static int FindLM(int frameSize)
    {
        for (int lm = 0; lm <= CeltTables.MaxLM; lm++)
            if (ShortMdct << lm == frameSize) return lm;
        throw new ArgumentException("Unsupported CELT frame size.", nameof(frameSize));
    }

    /// <summary>
    /// Decodes a frame whose range decoder is already positioned (hybrid mode shares one range decoder with SILK).
    /// </summary>
    /// <param name="dec">The range decoder.</param>
    /// <param name="len">Bytes of CELT data, used for the bit budget.</param>
    /// <param name="pcm">Interleaved output.</param>
    /// <param name="frameSize">Samples per channel at the output rate.</param>
    public int DecodeFrame(ref RangeDecoder dec, int len, Span<float> pcm, int frameSize)
    {
        int cc = _channels;
        int c = _streamChannels;
        frameSize *= _downsample;
        int lm = FindLM(frameSize);
        int m = 1 << lm;
        if (len < 0 || len > 1275) throw new ArgumentOutOfRangeException(nameof(len));
        int n = m * ShortMdct;
        int effEnd = Math.Min(_end, NbEBands);
        var eb = CeltTables.EBands5ms;

        var freq = _freq;
        var x = _x;
        var bandE = _bandE;
        for (int ch = 0; ch < c; ch++)
        {
            for (int i = 0; i < m * eb[_start]; i++) x[ch * n + i] = 0;
            for (int i = m * eb[effEnd]; i < n; i++) x[ch * n + i] = 0;
        }

        if (len <= 1)
        {
            DecodeLostFrame(pcm, n, lm);
            return frameSize / _downsample;
        }

        if (c == 1)
        {
            for (int i = 0; i < NbEBands; i++)
                _oldBandE[i] = Math.Max(_oldBandE[i], _oldBandE[NbEBands + i]);
        }

        int totalBits = len * 8;
        int tell = dec.Tell();
        int silence;
        if (tell >= totalBits) silence = 1;
        else if (tell == 1) silence = dec.BitLogP(15);
        else silence = 0;
        if (silence != 0)
        {
            tell = len * 8;
            dec.AddTotalBits(tell - dec.Tell());
        }

        float postfilterGain = 0;
        int postfilterPitch = 0;
        int postfilterTapset = 0;
        if (_start == 0 && tell + 16 <= totalBits)
        {
            if (dec.BitLogP(1) != 0)
            {
                int octave = (int)dec.DecodeUInt(6);
                postfilterPitch = (16 << octave) + (int)dec.Bits(4 + octave) - 1;
                int qg = (int)dec.Bits(3);
                if (dec.Tell() + 2 <= totalBits)
                    postfilterTapset = dec.ICdf(CeltTables.TapsetICdf, 2);
                postfilterGain = 0.09375f * (qg + 1);
            }
            tell = dec.Tell();
        }

        int isTransient;
        if (lm > 0 && tell + 3 <= totalBits)
        {
            isTransient = dec.BitLogP(3);
            tell = dec.Tell();
        }
        else
        {
            isTransient = 0;
        }
        int shortBlocks = isTransient != 0 ? m : 0;

        int intraEner = tell + 3 <= totalBits ? dec.BitLogP(3) : 0;
        UnquantCoarseEnergy(_start, _end, _oldBandE, intraEner, ref dec, c, lm);

        TfDecode(_start, _end, isTransient, _tfRes, lm, ref dec);
        tell = dec.Tell();
        int spreadDecision = 2; // SPREAD_NORMAL
        if (tell + 4 <= totalBits)
            spreadDecision = dec.ICdf(CeltTables.SpreadICdf, 5);

        var pulses = _pulses;
        var cap = _cap;
        var offsets = _offsets;
        var finePriority = _finePriority;
        for (int i = 0; i < NbEBands; i++)
        {
            int nn = (eb[i + 1] - eb[i]) << lm;
            cap[i] = (CeltTables.CacheCaps[NbEBands * (2 * lm + c - 1) + i] + 64) * c * nn >> 2;
        }
        int dynallocLogp = 6;
        totalBits <<= Bitres;
        tell = (int)dec.TellFrac();
        for (int i = _start; i < _end; i++)
        {
            int width = c * (eb[i + 1] - eb[i]) << lm;
            // quanta is 6 bits, but no more than 1 bit/sample and no less than 1/8 bit/sample
            int quanta = Math.Min(width << Bitres, Math.Max(6 << Bitres, width));
            int loopLogp = dynallocLogp;
            int boost = 0;
            while (tell + (loopLogp << Bitres) < totalBits && boost < cap[i])
            {
                int flag = dec.BitLogP(loopLogp);
                tell = (int)dec.TellFrac();
                if (flag == 0) break;
                boost += quanta;
                totalBits -= quanta;
                loopLogp = 1;
            }
            offsets[i] = boost;
            if (boost > 0) dynallocLogp = Math.Max(2, dynallocLogp - 1);
        }

        int allocTrim = tell + (6 << Bitres) <= totalBits ? dec.ICdf(CeltTables.TrimICdf, 7) : 5;
        int bits = ((len * 8) << Bitres) - (int)dec.TellFrac() - 1;
        int antiCollapseRsv = isTransient != 0 && lm >= 2 && bits >= ((lm + 2) << Bitres) ? 1 << Bitres : 0;
        bits -= antiCollapseRsv;
        int codedBands = ComputeAllocation(_start, _end, offsets, cap, allocTrim, out int intensity, out int dualStereo,
            bits, out int balance, pulses, _fineQuant, finePriority, c, lm, ref dec);
        UnquantFineEnergy(_start, _end, _oldBandE, _fineQuant, ref dec, c);

        QuantAllBands(_start, _end, x, c == 2, _collapseMasks, pulses, shortBlocks, spreadDecision, dualStereo != 0,
            intensity, _tfRes, len * (8 << Bitres) - antiCollapseRsv, balance, ref dec, lm, codedBands, ref _rng, n);

        int antiCollapseOn = 0;
        if (antiCollapseRsv > 0) antiCollapseOn = (int)dec.Bits(1);
        UnquantEnergyFinalise(_start, _end, _oldBandE, _fineQuant, finePriority, len * 8 - dec.Tell(), ref dec, c);
        if (antiCollapseOn != 0)
            AntiCollapse(x, _collapseMasks, lm, c, n, _start, _end, _oldBandE, _oldLogE, _oldLogE2, pulses, _rng);

        Log2Amp(_start, _end, bandE, _oldBandE, c);
        if (silence != 0)
        {
            for (int i = 0; i < c * NbEBands; i++)
            {
                bandE[i] = 0;
                _oldBandE[i] = -28f;
            }
        }
        DenormaliseBands(x, freq, bandE, effEnd, c, m);

        for (int ch = 0; ch < cc; ch++)
            Array.Copy(_decodeMem[ch], n, _decodeMem[ch], 0, DecodeBufferSize - n);

        for (int ch = 0; ch < c; ch++)
        {
            for (int i = 0; i < m * eb[_start]; i++) freq[ch * n + i] = 0;
            int bound = m * eb[effEnd];
            if (_downsample != 1) bound = Math.Min(bound, n / _downsample);
            for (int i = bound; i < n; i++) freq[ch * n + i] = 0;
        }

        if (cc == 2 && c == 1)
        {
            for (int i = 0; i < n; i++) freq[n + i] = freq[i];
        }
        if (cc == 1 && c == 2)
        {
            for (int i = 0; i < n; i++) freq[i] = 0.5f * (freq[i] + freq[n + i]);
        }
        ComputeInvMdcts(shortBlocks, freq, lm, n);

        for (int ch = 0; ch < cc; ch++)
        {
            _postfilterPeriod = Math.Max(_postfilterPeriod, CombMinPeriod);
            _postfilterPeriodOld = Math.Max(_postfilterPeriodOld, CombMinPeriod);
            int syn = DecodeBufferSize - n;
            var mem = _decodeMem[ch];
            CombFilter(mem, syn, mem, syn, _postfilterPeriodOld, _postfilterPeriod, ShortMdct,
                _postfilterGainOld, _postfilterGain, _postfilterTapsetOld, _postfilterTapset, CeltTables.Window, Overlap);
            if (lm != 0)
                CombFilter(mem, syn + ShortMdct, mem, syn + ShortMdct, _postfilterPeriod, postfilterPitch, n - ShortMdct,
                    _postfilterGain, postfilterGain, _postfilterTapset, postfilterTapset, CeltTables.Window, Overlap);
        }
        _postfilterPeriodOld = _postfilterPeriod;
        _postfilterGainOld = _postfilterGain;
        _postfilterTapsetOld = _postfilterTapset;
        _postfilterPeriod = postfilterPitch;
        _postfilterGain = postfilterGain;
        _postfilterTapset = postfilterTapset;
        if (lm != 0)
        {
            _postfilterPeriodOld = _postfilterPeriod;
            _postfilterGainOld = _postfilterGain;
            _postfilterTapsetOld = _postfilterTapset;
        }

        if (c == 1)
        {
            for (int i = 0; i < NbEBands; i++) _oldBandE[NbEBands + i] = _oldBandE[i];
        }

        if (isTransient == 0)
        {
            Array.Copy(_oldLogE, _oldLogE2, 2 * NbEBands);
            Array.Copy(_oldBandE, _oldLogE, 2 * NbEBands);
            for (int i = 0; i < 2 * NbEBands; i++)
                _backgroundLogE[i] = Math.Min(_backgroundLogE[i] + m * 0.001f, _oldBandE[i]);
        }
        else
        {
            for (int i = 0; i < 2 * NbEBands; i++)
                _oldLogE[i] = Math.Min(_oldLogE[i], _oldBandE[i]);
        }
        for (int ch = 0; ch < 2; ch++)
        {
            for (int i = 0; i < _start; i++)
            {
                _oldBandE[ch * NbEBands + i] = 0;
                _oldLogE[ch * NbEBands + i] = _oldLogE2[ch * NbEBands + i] = -28f;
            }
            for (int i = _end; i < NbEBands; i++)
            {
                _oldBandE[ch * NbEBands + i] = 0;
                _oldLogE[ch * NbEBands + i] = _oldLogE2[ch * NbEBands + i] = -28f;
            }
        }
        _rng = dec.Range;
        Deemphasis(pcm, n, cc);
        _lossCount = 0;
        return frameSize / _downsample;
    }

    // ------------------------------------------------------------------------------------------------
    // Synthesis helpers
    // ------------------------------------------------------------------------------------------------

    private void ComputeInvMdcts(int shortBlocks, float[] freq, int lm, int n)
    {
        var mdct = CeltMdct.Instance;
        var window = CeltTables.Window;
        var x = _mdctIn;
        for (int c = 0; c < _channels; c++)
        {
            int n2 = n, b = 1;
            if (shortBlocks != 0)
            {
                n2 = ShortMdct;
                b = shortBlocks;
            }
            Array.Clear(x, 0, Overlap);
            int shift = shortBlocks != 0 ? CeltTables.MaxLM : CeltTables.MaxLM - lm;
            for (int blk = 0; blk < b; blk++)
                mdct.Backward(freq, blk + c * n2 * b, x, n2 * blk, window, Overlap, shift, b, _mdctF, _mdctF2);
            var mem = _decodeMem[c];
            int syn = DecodeBufferSize - n;
            int ovl = DecodeBufferSize;
            for (int j = 0; j < Overlap; j++) mem[syn + j] = x[j] + mem[ovl + j];
            for (int j = Overlap; j < n; j++) mem[syn + j] = x[j];
            for (int j = 0; j < Overlap; j++) mem[ovl + j] = x[n + j];
        }
    }

    private void Deemphasis(Span<float> pcm, int n, int cc)
    {
        const float coef0 = 0.85000610f;
        for (int c = 0; c < cc; c++)
        {
            var mem = _decodeMem[c];
            int syn = DecodeBufferSize - n;
            float m = _preemphMemD[c];
            int count = 0;
            int y = c;
            for (int j = 0; j < n; j++)
            {
                float xj = mem[syn + j];
                float tmp = xj + m;
                m = coef0 * tmp;
                if (count == 0) pcm[y] = tmp * (1f / 32768f);
                if (++count == _downsample)
                {
                    y += cc;
                    count = 0;
                }
            }
            _preemphMemD[c] = m;
        }
    }

    /// <summary>Pitch-adaptive comb post-filter (RFC 6716 section 4.3.7.1). x and y may be the same array.</summary>
    private static void CombFilter(float[] y, int yo, float[] x, int xo, int t0, int t1, int n,
        float g0, float g1, int tapset0, int tapset1, ReadOnlySpan<float> window, int overlap)
    {
        var gains = CeltTables.CombGains;
        float g00 = g0 * gains[tapset0 * 3], g01 = g0 * gains[tapset0 * 3 + 1], g02 = g0 * gains[tapset0 * 3 + 2];
        float g10 = g1 * gains[tapset1 * 3], g11 = g1 * gains[tapset1 * 3 + 1], g12 = g1 * gains[tapset1 * 3 + 2];
        int i;
        for (i = 0; i < overlap; i++)
        {
            float f = window[i] * window[i];
            float a = 1f - f;
            y[yo + i] = x[xo + i]
                + a * g00 * x[xo + i - t0]
                + a * g01 * x[xo + i - t0 - 1]
                + a * g01 * x[xo + i - t0 + 1]
                + a * g02 * x[xo + i - t0 - 2]
                + a * g02 * x[xo + i - t0 + 2]
                + f * g10 * x[xo + i - t1]
                + f * g11 * x[xo + i - t1 - 1]
                + f * g11 * x[xo + i - t1 + 1]
                + f * g12 * x[xo + i - t1 - 2]
                + f * g12 * x[xo + i - t1 + 2];
        }
        for (; i < n; i++)
        {
            y[yo + i] = x[xo + i]
                + g10 * x[xo + i - t1]
                + g11 * x[xo + i - t1 - 1]
                + g11 * x[xo + i - t1 + 1]
                + g12 * x[xo + i - t1 - 2]
                + g12 * x[xo + i - t1 + 2];
        }
    }

    // ------------------------------------------------------------------------------------------------
    // Side information
    // ------------------------------------------------------------------------------------------------

    private static void TfDecode(int start, int end, int isTransient, int[] tfRes, int lm, ref RangeDecoder dec)
    {
        int budget = dec.Storage * 8;
        int tell = dec.Tell();
        int logp = isTransient != 0 ? 2 : 4;
        int tfSelectRsv = lm > 0 && tell + logp + 1 <= budget ? 1 : 0;
        budget -= tfSelectRsv;
        int tfChanged = 0, curr = 0;
        for (int i = start; i < end; i++)
        {
            if (tell + logp <= budget)
            {
                curr ^= dec.BitLogP(logp);
                tell = dec.Tell();
                tfChanged |= curr;
            }
            tfRes[i] = curr;
            logp = isTransient != 0 ? 4 : 5;
        }
        int tfSelect = 0;
        var table = CeltTables.TfSelectTable;
        if (tfSelectRsv != 0 &&
            table[lm * 8 + 4 * isTransient + 0 + tfChanged] != table[lm * 8 + 4 * isTransient + 2 + tfChanged])
        {
            tfSelect = dec.BitLogP(1);
        }
        for (int i = start; i < end; i++)
            tfRes[i] = table[lm * 8 + 4 * isTransient + 2 * tfSelect + tfRes[i]];
    }

    private static void UnquantCoarseEnergy(int start, int end, float[] oldEBands, int intra, ref RangeDecoder dec, int c, int lm)
    {
        var probModel = CeltTables.EProbModel;
        int pmBase = (lm * 2 + intra) * 42;
        float coef, beta;
        if (intra != 0)
        {
            coef = 0;
            beta = CeltTables.BetaIntra;
        }
        else
        {
            beta = CeltTables.BetaCoef[lm];
            coef = CeltTables.PredCoef[lm];
        }
        float prev0 = 0, prev1 = 0;
        int budget = dec.Storage * 8;
        for (int i = start; i < end; i++)
        {
            for (int ch = 0; ch < c; ch++)
            {
                int qi;
                int tell = dec.Tell();
                if (budget - tell >= 15)
                {
                    int pi = 2 * Math.Min(i, 20);
                    qi = dec.LaplaceDecode((uint)probModel[pmBase + pi] << 7, probModel[pmBase + pi + 1] << 6);
                }
                else if (budget - tell >= 2)
                {
                    qi = dec.ICdf(CeltTables.SmallEnergyICdf, 2);
                    qi = (qi >> 1) ^ -(qi & 1);
                }
                else if (budget - tell >= 1)
                {
                    qi = -dec.BitLogP(1);
                }
                else
                {
                    qi = -1;
                }
                float q = qi;
                int idx = i + ch * NbEBands;
                oldEBands[idx] = Math.Max(-9f, oldEBands[idx]);
                float prev = ch == 0 ? prev0 : prev1;
                float tmp = coef * oldEBands[idx] + prev + q;
                oldEBands[idx] = tmp;
                prev = prev + q - beta * q;
                if (ch == 0) prev0 = prev; else prev1 = prev;
            }
        }
    }

    private static void UnquantFineEnergy(int start, int end, float[] oldEBands, int[] fineQuant, ref RangeDecoder dec, int c)
    {
        for (int i = start; i < end; i++)
        {
            if (fineQuant[i] <= 0) continue;
            for (int ch = 0; ch < c; ch++)
            {
                int q2 = (int)dec.Bits(fineQuant[i]);
                float offset = (q2 + .5f) * (1 << (14 - fineQuant[i])) * (1f / 16384) - .5f;
                oldEBands[i + ch * NbEBands] += offset;
            }
        }
    }

    private static void UnquantEnergyFinalise(int start, int end, float[] oldEBands, int[] fineQuant, int[] finePriority,
        int bitsLeft, ref RangeDecoder dec, int c)
    {
        for (int prio = 0; prio < 2; prio++)
        {
            for (int i = start; i < end && bitsLeft >= c; i++)
            {
                if (fineQuant[i] >= 8 || finePriority[i] != prio) continue;
                for (int ch = 0; ch < c; ch++)
                {
                    int q2 = (int)dec.Bits(1);
                    float offset = (q2 - .5f) * (1 << (14 - fineQuant[i] - 1)) * (1f / 16384);
                    oldEBands[i + ch * NbEBands] += offset;
                    bitsLeft--;
                }
            }
        }
    }

    private static float Exp2(float x) => (float)Math.Exp(0.6931471805599453094 * x);

    private static void Log2Amp(int start, int end, float[] eBands, float[] oldEBands, int c)
    {
        for (int ch = 0; ch < c; ch++)
        {
            int i;
            for (i = 0; i < start; i++) eBands[i + ch * NbEBands] = 0;
            for (; i < end; i++)
            {
                float lg = oldEBands[i + ch * NbEBands] + CeltTables.EMeans[i];
                lg = Math.Min(32f, lg);
                eBands[i + ch * NbEBands] = Exp2(lg);
            }
            for (; i < NbEBands; i++) eBands[i + ch * NbEBands] = 0;
        }
    }

    private static void DenormaliseBands(float[] x, float[] freq, float[] bandE, int end, int c, int m)
    {
        var eb = CeltTables.EBands5ms;
        int n = m * ShortMdct;
        for (int ch = 0; ch < c; ch++)
        {
            int f = ch * n;
            int xi = ch * n;
            for (int i = 0; i < end; i++)
            {
                float g = bandE[i + ch * NbEBands];
                int j = m * eb[i];
                int bandEnd = m * eb[i + 1];
                do
                {
                    freq[f++] = x[xi++] * g;
                } while (++j < bandEnd);
            }
            for (int i = m * eb[end]; i < n; i++) freq[f++] = 0;
        }
    }
}
