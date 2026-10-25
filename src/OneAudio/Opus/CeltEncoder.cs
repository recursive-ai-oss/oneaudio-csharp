using System.Runtime.CompilerServices;

namespace OneAudio.Opus;

/// <summary>
/// The CELT layer of the Opus encoder for the 48 kHz mode (RFC 6716 section 5.3). Ported from the reference encoder in
/// RFC 6716 (Simplified BSD licence, see THIRD-PARTY-NOTICES.md), floating-point build. All state and scratch
/// buffers are allocated up front; encoding a frame allocates nothing.
/// </summary>
internal sealed partial class CeltEncoder
{
    private const int NbEBands = CeltTables.NbEBands;
    private const int Overlap = CeltTables.Overlap;
    private const int ShortMdct = CeltTables.ShortMdctSize;
    private const int MaxFrame = 960;
    private const int Bitres = 3;
    private const int CombMaxPeriod = 1024;
    private const int CombMinPeriod = 15;
    private const float Pi = 3.141592653f;

    private const int SpreadNone = 0, SpreadLight = 1, SpreadNormal = 2, SpreadAggressive = 3;

    private readonly int _cc;          // channels of the input
    private int _streamChannels;       // channels coded
    private int _start, _end = NbEBands;

    // Settings
    public bool ForceIntra;
    public bool Clip = true;
    public bool DisablePrefilter;
    public int Complexity = 5;
    public int Bitrate = -1;           // bits per second, -1 = unlimited
    public bool Vbr;
    public bool ConstrainedVbr = true;
    public int LossRate;

    // State cleared by Reset
    private uint _rng;
    private int _spreadDecision;
    private float _delayedIntra;
    private int _tonalAverage;
    private int _lastCodedBands;
    private int _hfAverage;
    private int _tapsetDecision;
    private int _prefilterPeriod;
    private float _prefilterGain;
    private int _prefilterTapset;
    private int _consecTransient;
    private readonly float[] _preemphMemE = new float[2];
    private int _vbrReservoir, _vbrDrift, _vbrOffset, _vbrCount;
    private readonly float[] _inMem;
    private readonly float[] _prefilterMem;
    private readonly float[] _oldBandE, _oldLogE, _oldLogE2;

    // Scratch
    private readonly float[] _in, _pre, _pitchBuf, _freq, _x, _bandE, _bandLogE, _error;
    private readonly float[] _tmp, _bins, _mdctF, _mdctF2, _scratchTf;
    private readonly int[] _metric, _path0, _path1;
    private readonly int[] _fineQuant = new int[NbEBands], _pulses = new int[NbEBands], _cap = new int[NbEBands];
    private readonly int[] _offsets = new int[NbEBands], _finePriority = new int[NbEBands], _tfRes = new int[NbEBands];
    private readonly float[] _oldEBandsIntra, _errorIntra;
    private readonly byte[] _intraBits = new byte[1280];
    private readonly float[] _xcorr = new float[CombMaxPeriod], _x4 = new float[MaxFrame / 4 + 8], _y4 = new float[(MaxFrame + CombMaxPeriod) / 4 + 8];
    private readonly float[] _lpc4 = new float[4], _mem4 = new float[4];
    private readonly float[] _qFloat = new float[512], _qY = new float[512], _qSign = new float[512];
    private readonly int[] _qIy = new int[512];
    private readonly uint[] _cwrsU = new uint[640];
    private readonly float[] _hadTmp = new float[1024];

    public CeltEncoder(int channels)
    {
        _cc = channels;
        _streamChannels = channels;
        _inMem = new float[channels * Overlap];
        _prefilterMem = new float[channels * CombMaxPeriod];
        _oldBandE = new float[channels * NbEBands];
        _oldLogE = new float[channels * NbEBands];
        _oldLogE2 = new float[channels * NbEBands];
        _in = new float[channels * (MaxFrame + Overlap)];
        _pre = new float[channels * (MaxFrame + CombMaxPeriod)];
        _pitchBuf = new float[(CombMaxPeriod + MaxFrame) >> 1];
        _freq = new float[channels * MaxFrame];
        _x = new float[channels * MaxFrame];
        _bandE = new float[channels * NbEBands];
        _bandLogE = new float[channels * NbEBands];
        _error = new float[channels * NbEBands];
        _tmp = new float[MaxFrame + Overlap];
        _bins = new float[(MaxFrame + Overlap) / (Overlap / 2) + 1];
        _mdctF = new float[MaxFrame];
        _mdctF2 = new float[MaxFrame];
        _scratchTf = new float[256];
        _metric = new int[NbEBands];
        _path0 = new int[NbEBands];
        _path1 = new int[NbEBands];
        _oldEBandsIntra = new float[channels * NbEBands];
        _errorIntra = new float[channels * NbEBands];
        Reset();
    }

    public int Start { get => _start; set => _start = value; }
    public int End { get => _end; set => _end = value; }
    public int StreamChannels { get => _streamChannels; set => _streamChannels = value; }
    public uint FinalRange => _rng;

    public void Reset()
    {
        _rng = 0;
        _spreadDecision = SpreadNormal;
        _delayedIntra = 1;
        _tonalAverage = 256;
        _lastCodedBands = 0;
        _hfAverage = 0;
        _tapsetDecision = 0;
        _prefilterPeriod = 0;
        _prefilterGain = 0;
        _prefilterTapset = 0;
        _consecTransient = 0;
        Array.Clear(_preemphMemE);
        _vbrReservoir = _vbrDrift = _vbrOffset = _vbrCount = 0;
        Array.Clear(_inMem);
        Array.Clear(_prefilterMem);
        Array.Clear(_oldBandE);
        Array.Fill(_oldLogE, -28f);
        Array.Fill(_oldLogE2, -28f);
    }

    private static float CeltLog2(float x) => (float)(1.442695040888963387 * Math.Log(x));

    // ------------------------------------------------------------------------------------------------

    private static bool TransientAnalysis(float[] inp, int len, int c, int overlap, float[] tmp, float[] bins)
    {
        float mem0 = 0, mem1 = 0;
        bool isTransient = false;
        int block = overlap / 2;
        int n = len / block;
        if (c == 1) for (int i = 0; i < len; i++) tmp[i] = inp[i];
        else for (int i = 0; i < len; i++) tmp[i] = inp[i] + inp[i + len];
        for (int i = 0; i < len; i++)
        {
            float x = tmp[i];
            float y = mem0 + x;
            mem0 = mem1 + y - 2 * x;
            mem1 = x - .5f * y;
            tmp[i] = y;
        }
        for (int i = 0; i < 12; i++) tmp[i] = 0;
        for (int i = 0; i < n; i++)
        {
            float maxAbs = 0;
            for (int j = 0; j < block; j++)
            {
                float a = Math.Abs(tmp[i * block + j]);
                if (a > maxAbs) maxAbs = a;
            }
            bins[i] = maxAbs;
        }
        for (int i = 0; i < n; i++)
        {
            int conseq = 0;
            float t1 = .15f * bins[i], t2 = .4f * bins[i], t3 = .15f * bins[i];
            for (int j = 0; j < i; j++)
            {
                if (bins[j] < t1) conseq++;
                if (bins[j] < t2) conseq++;
                else conseq = 0;
            }
            if (conseq >= 3) isTransient = true;
            conseq = 0;
            for (int j = i + 1; j < n; j++)
            {
                if (bins[j] < t3) conseq++;
                else conseq = 0;
            }
            if (conseq >= 7) isTransient = true;
        }
        return isTransient;
    }

    private void ComputeMdcts(int shortBlocks, int cc, int lm)
    {
        var mdct = CeltMdct.Instance;
        var window = CeltTables.Window;
        if (cc == 1 && shortBlocks == 0)
        {
            mdct.Forward(_in, 0, _freq, 0, window, Overlap, CeltTables.MaxLM - lm, 1, _mdctF, _mdctF2);
            return;
        }
        int n = ShortMdct << lm;
        int b = 1;
        if (shortBlocks != 0)
        {
            n = ShortMdct;
            b = shortBlocks;
        }
        for (int c = 0; c < cc; c++)
            for (int bb = 0; bb < b; bb++)
                mdct.Forward(_in, c * (b * n + Overlap) + bb * n, _freq, bb + c * n * b, window, Overlap,
                    shortBlocks != 0 ? CeltTables.MaxLM : CeltTables.MaxLM - lm, b, _mdctF, _mdctF2);
    }

    private void ComputeBandEnergies(int end, int c, int m)
    {
        var eb = CeltTables.EBands5ms;
        int n = m * ShortMdct;
        for (int cc = 0; cc < c; cc++)
            for (int i = 0; i < end; i++)
            {
                float sum = 1e-27f;
                for (int j = m * eb[i]; j < m * eb[i + 1]; j++)
                    sum += _freq[j + cc * n] * _freq[j + cc * n];
                _bandE[i + cc * NbEBands] = MathF.Sqrt(sum);
            }
    }

    private void NormaliseBands(int end, int c, int m)
    {
        var eb = CeltTables.EBands5ms;
        int n = m * ShortMdct;
        for (int cc = 0; cc < c; cc++)
            for (int i = 0; i < end; i++)
            {
                float g = 1f / (1e-27f + _bandE[i + cc * NbEBands]);
                for (int j = m * eb[i]; j < m * eb[i + 1]; j++)
                    _x[j + cc * n] = _freq[j + cc * n] * g;
            }
    }

    private void Amp2Log2(int effEnd, int end, int c)
    {
        for (int cc = 0; cc < c; cc++)
        {
            for (int i = 0; i < effEnd; i++)
                _bandLogE[i + cc * NbEBands] = CeltLog2(_bandE[i + cc * NbEBands]) - CeltTables.EMeans[i];
            for (int i = effEnd; i < end; i++)
                _bandLogE[cc * NbEBands + i] = -14f;
        }
    }

    // ------------------------------------------------------------------------------------------------
    // Time/frequency resolution
    // ------------------------------------------------------------------------------------------------

    private static readonly float[] SqrtM1 = [1f, .70710678f, .5f, .35355339f];

    private static float L1Metric(float[] tmp, int n, int lm, int width)
    {
        float l1 = 0;
        for (int i = 0; i < 1 << lm; i++)
        {
            float l2 = 0;
            for (int j = 0; j < n >> lm; j++)
                l2 += tmp[(j << lm) + i] * tmp[(j << lm) + i];
            l1 += MathF.Sqrt(l2);
        }
        l1 = SqrtM1[lm] * l1;
        float bias;
        if (width == 1) bias = .12f * lm;
        else if (width == 2) bias = .05f * lm;
        else bias = .02f * lm;
        l1 = l1 + bias * l1;
        return l1;
    }

    private int TfAnalysis(int len, int c, bool isTransient, int[] tfRes, int nbCompressedBytes, int n0, int lm,
        out int tfSum)
    {
        var eb = CeltTables.EBands5ms;
        int t = isTransient ? 1 : 0;
        if (nbCompressedBytes < 15 * c)
        {
            tfSum = 0;
            for (int i = 0; i < len; i++) tfRes[i] = t;
            return 0;
        }
        int lambda;
        if (nbCompressedBytes < 40) lambda = 12;
        else if (nbCompressedBytes < 60) lambda = 6;
        else if (nbCompressedBytes < 100) lambda = 4;
        else lambda = 3;
        var tmp = _scratchTf;
        var metric = _metric;
        tfSum = 0;
        for (int i = 0; i < len; i++)
        {
            int n = (eb[i + 1] - eb[i]) << lm;
            int bestLevel = 0;
            for (int j = 0; j < n; j++) tmp[j] = _x[j + (eb[i] << lm)];
            if (c == 2)
                for (int j = 0; j < n; j++) tmp[j] = tmp[j] + _x[n0 + j + (eb[i] << lm)];
            float l1 = L1Metric(tmp, n, isTransient ? lm : 0, n >> lm);
            float bestL1 = l1;
            for (int k = 0; k < lm; k++)
            {
                int b = isTransient ? lm - k - 1 : k + 1;
                if (isTransient) CeltDecoder.Haar1(tmp, n >> (lm - k), 1 << (lm - k));
                else CeltDecoder.Haar1(tmp, n >> k, 1 << k);
                l1 = L1Metric(tmp, n, b, n >> lm);
                if (l1 < bestL1)
                {
                    bestL1 = l1;
                    bestLevel = k + 1;
                }
            }
            metric[i] = isTransient ? bestLevel : -bestLevel;
            tfSum += metric[i];
        }
        int tfSelect = 0;
        var table = CeltTables.TfSelectTable;
        int cost0 = 0;
        int cost1 = isTransient ? 0 : lambda;
        var path0 = _path0;
        var path1 = _path1;
        for (int i = 1; i < len; i++)
        {
            int curr0, curr1;
            int from0 = cost0;
            int from1 = cost1 + lambda;
            if (from0 < from1) { curr0 = from0; path0[i] = 0; }
            else { curr0 = from1; path0[i] = 1; }
            from0 = cost0 + lambda;
            from1 = cost1;
            if (from0 < from1) { curr1 = from0; path1[i] = 0; }
            else { curr1 = from1; path1[i] = 1; }
            cost0 = curr0 + Math.Abs(metric[i] - table[lm * 8 + 4 * t + 2 * tfSelect + 0]);
            cost1 = curr1 + Math.Abs(metric[i] - table[lm * 8 + 4 * t + 2 * tfSelect + 1]);
        }
        tfRes[len - 1] = cost0 < cost1 ? 0 : 1;
        for (int i = len - 2; i >= 0; i--)
            tfRes[i] = tfRes[i + 1] == 1 ? path1[i + 1] : path0[i + 1];
        return tfSelect;
    }

    private static void TfEncode(int start, int end, int isTransient, int[] tfRes, int lm, int tfSelect, ref RangeEncoder enc)
    {
        var table = CeltTables.TfSelectTable;
        int budget = enc.Storage * 8;
        int tell = enc.Tell();
        int logp = isTransient != 0 ? 2 : 4;
        int tfSelectRsv = lm > 0 && tell + logp + 1 <= budget ? 1 : 0;
        budget -= tfSelectRsv;
        int curr = 0, tfChanged = 0;
        for (int i = start; i < end; i++)
        {
            if (tell + logp <= budget)
            {
                enc.BitLogP(tfRes[i] ^ curr, logp);
                tell = enc.Tell();
                curr = tfRes[i];
                tfChanged |= curr;
            }
            else tfRes[i] = curr;
            logp = isTransient != 0 ? 4 : 5;
        }
        if (tfSelectRsv != 0 &&
            table[lm * 8 + 4 * isTransient + 0 + tfChanged] != table[lm * 8 + 4 * isTransient + 2 + tfChanged])
            enc.BitLogP(tfSelect, 1);
        else
            tfSelect = 0;
        for (int i = start; i < end; i++)
            tfRes[i] = table[lm * 8 + 4 * isTransient + 2 * tfSelect + tfRes[i]];
    }

    // ------------------------------------------------------------------------------------------------
    // Analysis helpers
    // ------------------------------------------------------------------------------------------------

    private static void InitCaps(int[] cap, int lm, int c)
    {
        var eb = CeltTables.EBands5ms;
        for (int i = 0; i < NbEBands; i++)
        {
            int n = (eb[i + 1] - eb[i]) << lm;
            cap[i] = (CeltTables.CacheCaps[NbEBands * (2 * lm + c - 1) + i] + 64) * c * n >> 2;
        }
    }

    private int AllocTrimAnalysis(int end, int lm, int c, int n0)
    {
        var eb = CeltTables.EBands5ms;
        float diff = 0;
        int trimIndex = 5;
        if (c == 2)
        {
            float sum = 0;
            for (int i = 0; i < 8; i++)
            {
                float partial = 0;
                for (int j = eb[i] << lm; j < eb[i + 1] << lm; j++)
                    partial += _x[j] * _x[n0 + j];
                sum = sum + partial;
            }
            sum = .125f * sum;
            if (sum > .995f) trimIndex -= 4;
            else if (sum > .92f) trimIndex -= 3;
            else if (sum > .85f) trimIndex -= 2;
            else if (sum > .8f) trimIndex -= 1;
        }
        for (int cc = 0; cc < c; cc++)
            for (int i = 0; i < end - 1; i++)
                diff += _bandLogE[i + cc * NbEBands] * (2 + 2 * i - NbEBands);
        diff /= 2 * c * (end - 1);
        if (diff > 2f) trimIndex--;
        if (diff > 8f) trimIndex--;
        if (diff < -4f) trimIndex++;
        if (diff < -10f) trimIndex++;
        return Math.Clamp(trimIndex, 0, 10);
    }

    private bool StereoAnalysis(int lm, int n0)
    {
        var eb = CeltTables.EBands5ms;
        float sumLR = 1e-15f, sumMS = 1e-15f;
        for (int i = 0; i < 13; i++)
            for (int j = eb[i] << lm; j < eb[i + 1] << lm; j++)
            {
                float l = _x[j], r = _x[n0 + j];
                float m = l + r, s = l - r;
                sumLR += Math.Abs(l) + Math.Abs(r);
                sumMS += Math.Abs(m) + Math.Abs(s);
            }
        sumMS = .707107f * sumMS;
        int thetas = 13;
        if (lm <= 1) thetas -= 8;
        return ((eb[13] << (lm + 1)) + thetas) * sumMS > (eb[13] << (lm + 1)) * sumLR;
    }

    private int SpreadingDecision(int lastDecision, bool updateHf, int end, int c, int m)
    {
        var eb = CeltTables.EBands5ms;
        int sum = 0, nbBands = 0, hfSum = 0;
        int n0 = m * ShortMdct;
        if (m * (eb[end] - eb[end - 1]) <= 8) return SpreadNone;
        Span<int> tcount = stackalloc int[3];
        for (int cc = 0; cc < c; cc++)
        {
            for (int i = 0; i < end; i++)
            {
                int n = m * (eb[i + 1] - eb[i]);
                if (n <= 8) continue;
                int xo = m * eb[i] + cc * n0;
                tcount[0] = tcount[1] = tcount[2] = 0;
                for (int j = 0; j < n; j++)
                {
                    float x2n = _x[xo + j] * _x[xo + j] * n;
                    if (x2n < .25f) tcount[0]++;
                    if (x2n < .0625f) tcount[1]++;
                    if (x2n < .015625f) tcount[2]++;
                }
                if (i > NbEBands - 4) hfSum += 32 * (tcount[1] + tcount[0]) / n;
                int tmp = (2 * tcount[2] >= n ? 1 : 0) + (2 * tcount[1] >= n ? 1 : 0) + (2 * tcount[0] >= n ? 1 : 0);
                sum += tmp * 256;
                nbBands++;
            }
        }
        if (updateHf)
        {
            if (hfSum != 0) hfSum /= c * (4 - NbEBands + end);
            _hfAverage = (_hfAverage + hfSum) >> 1;
            hfSum = _hfAverage;
            if (_tapsetDecision == 2) hfSum += 4;
            else if (_tapsetDecision == 0) hfSum -= 4;
            if (hfSum > 22) _tapsetDecision = 2;
            else if (hfSum > 18) _tapsetDecision = 1;
            else _tapsetDecision = 0;
        }
        sum /= nbBands;
        sum = (sum + _tonalAverage) >> 1;
        _tonalAverage = sum;
        sum = (3 * sum + (((3 - lastDecision) << 7) + 64) + 2) >> 2;
        if (sum < 80) return SpreadAggressive;
        if (sum < 256) return SpreadNormal;
        if (sum < 384) return SpreadLight;
        return SpreadNone;
    }

    // ------------------------------------------------------------------------------------------------
    // Pitch pre-filter analysis
    // ------------------------------------------------------------------------------------------------

    private void PitchDownsample(int len, int c)
    {
        var xLp = _pitchBuf;
        var p = _pre;
        int o1 = MaxFrame + CombMaxPeriod; // channel stride of the pre-filter buffer is set by the caller
        o1 = _preStride;
        int half = len >> 1;
        for (int i = 1; i < half; i++)
            xLp[i] = .5f * (.5f * (p[2 * i - 1] + p[2 * i + 1]) + p[2 * i]);
        xLp[0] = .5f * (.5f * p[1] + p[0]);
        if (c == 2)
        {
            for (int i = 1; i < half; i++)
                xLp[i] += .5f * (.5f * (p[o1 + 2 * i - 1] + p[o1 + 2 * i + 1]) + p[o1 + 2 * i]);
            xLp[0] += .5f * (.5f * p[o1 + 1] + p[o1]);
        }
        Span<float> ac = stackalloc float[5];
        for (int lag = 4; lag >= 0; lag--)
        {
            float d = 0;
            for (int i = lag; i < half; i++) d += xLp[i] * xLp[i - lag];
            ac[lag] = d;
        }
        ac[0] += 10;
        ac[0] *= 1.0001f;
        for (int i = 1; i <= 4; i++)
            ac[i] -= ac[i] * (.008f * i) * (.008f * i);
        var lpc = _lpc4;
        CeltDecoder.CeltLpc(lpc, 0, ac, 4);
        float tmp = 1f;
        for (int i = 0; i < 4; i++)
        {
            tmp = .9f * tmp;
            lpc[i] = lpc[i] * tmp;
        }
        var mem = _mem4;
        Array.Clear(mem);
        CeltDecoder.CeltFir(xLp, lpc, 0, xLp, half, 4, mem);
        mem[0] = 0;
        lpc[0] = .8f;
        CeltDecoder.CeltFir(xLp, lpc, 0, xLp, half, 1, mem);
    }

    private int _preStride;

    private int PitchSearch(float[] xLp, int xOff, float[] y, int len, int maxPitch)
    {
        int lag = len + maxPitch;
        var xLp4 = _x4;
        var yLp4 = _y4;
        var xcorr = _xcorr;
        Span<int> bestPitch = stackalloc int[2];
        for (int j = 0; j < len >> 2; j++) xLp4[j] = xLp[xOff + 2 * j];
        for (int j = 0; j < lag >> 2; j++) yLp4[j] = y[2 * j];
        for (int i = 0; i < maxPitch >> 2; i++)
        {
            float sum = 0;
            for (int j = 0; j < len >> 2; j++) sum += xLp4[j] * yLp4[i + j];
            xcorr[i] = Math.Max(-1, sum);
        }
        CeltDecoder.FindBestPitch(xcorr, yLp4, 0, len >> 2, maxPitch >> 2, bestPitch);
        for (int i = 0; i < maxPitch >> 1; i++)
        {
            float sum = 0;
            xcorr[i] = 0;
            if (Math.Abs(i - 2 * bestPitch[0]) > 2 && Math.Abs(i - 2 * bestPitch[1]) > 2) continue;
            for (int j = 0; j < len >> 1; j++) sum += xLp[xOff + j] * y[i + j];
            xcorr[i] = Math.Max(-1, sum);
        }
        CeltDecoder.FindBestPitch(xcorr, y, 0, len >> 1, maxPitch >> 1, bestPitch);
        int offset;
        if (bestPitch[0] > 0 && bestPitch[0] < (maxPitch >> 1) - 1)
        {
            float a = xcorr[bestPitch[0] - 1], b = xcorr[bestPitch[0]], c = xcorr[bestPitch[0] + 1];
            if (c - a > .7f * (b - a)) offset = 1;
            else if (a - c > .7f * (b - c)) offset = -1;
            else offset = 0;
        }
        else offset = 0;
        return 2 * bestPitch[0] - offset;
    }

    private static readonly int[] SecondCheck = [0, 0, 3, 2, 3, 2, 5, 2, 3, 2, 3, 2, 5, 2, 3, 2];

    private static float RemoveDoubling(float[] xa, int maxPeriod, int minPeriod, int n, ref int t0Ref, int prevPeriod,
        float prevGain)
    {
        int minPeriod0 = minPeriod;
        maxPeriod /= 2;
        minPeriod /= 2;
        t0Ref /= 2;
        prevPeriod /= 2;
        n /= 2;
        int xo = maxPeriod;
        if (t0Ref >= maxPeriod) t0Ref = maxPeriod - 1;
        int t0 = t0Ref, t = t0;
        float xx = 0, xy = 0, yy = 0;
        for (int i = 0; i < n; i++)
        {
            xy += xa[xo + i] * xa[xo + i - t0];
            xx += xa[xo + i] * xa[xo + i];
            yy += xa[xo + i - t0] * xa[xo + i - t0];
        }
        float bestXy = xy, bestYy = yy;
        float g, g0;
        g = g0 = xy / MathF.Sqrt(1 + xx * yy);
        for (int k = 2; k <= 15; k++)
        {
            int t1 = (2 * t0 + k) / (2 * k);
            if (t1 < minPeriod) break;
            int t1b;
            if (k == 2)
            {
                if (t1 + t0 > maxPeriod) t1b = t0;
                else t1b = t0 + t1;
            }
            else t1b = (2 * SecondCheck[k] * t0 + k) / (2 * k);
            xy = yy = 0;
            for (int i = 0; i < n; i++)
            {
                xy += xa[xo + i] * xa[xo + i - t1];
                yy += xa[xo + i - t1] * xa[xo + i - t1];
                xy += xa[xo + i] * xa[xo + i - t1b];
                yy += xa[xo + i - t1b] * xa[xo + i - t1b];
            }
            float g1 = xy / MathF.Sqrt(1 + 2f * xx * 1f * yy);
            float cont;
            if (Math.Abs(t1 - prevPeriod) <= 1) cont = prevGain;
            else if (Math.Abs(t1 - prevPeriod) <= 2 && 5 * k * k < t0) cont = .5f * prevGain;
            else cont = 0;
            if (g1 > .3f + .4f * g0 - cont)
            {
                bestXy = xy;
                bestYy = yy;
                t = t1;
                g = g1;
            }
        }
        float pg;
        if (bestYy <= bestXy) pg = 1f;
        else pg = bestXy / (bestYy + 1);
        Span<float> xcorr = stackalloc float[3];
        for (int k = 0; k < 3; k++)
        {
            int t1 = t + k - 1;
            xy = 0;
            for (int i = 0; i < n; i++) xy += xa[xo + i] * xa[xo + i - t1];
            xcorr[k] = xy;
        }
        int offset;
        if (xcorr[2] - xcorr[0] > .7f * (xcorr[1] - xcorr[0])) offset = 1;
        else if (xcorr[0] - xcorr[2] > .7f * (xcorr[1] - xcorr[2])) offset = -1;
        else offset = 0;
        if (pg > g) pg = g;
        t0Ref = 2 * t + offset;
        if (t0Ref < minPeriod0) t0Ref = minPeriod0;
        return pg;
    }
}
