namespace OneAudio.Aac;

internal sealed partial class AacEncCore
{
    // ---- window switching --------------------------------------------------------------------------------------------------

    // True when the signal gains a sudden burst of high-frequency energy inside the short-window zone of the block that begins at
    // hist[blockStart]: 128-sample segments (after a first-difference high-pass) are compared with the mean of the four before.
    private bool DetectAttack(int blockStart)
    {
        for (int ch = 0; ch < _ch; ch++)
        {
            var x = _hist[ch];
            // Segments 0..3 are history, 4..11 the zone (block samples 512 to 1536).
            int origin = blockStart;
            for (int i = 0; i < 12; i++)
            {
                int a = origin + i * 128;
                double e = 0;
                float prev = a > 0 ? x[a - 1] : 0f;
                for (int n = 0; n < 128; n++)
                {
                    float cur = x[a + n];
                    float y = cur - 0.95f * prev;
                    e += y * y;
                    prev = cur;
                }
                _seg[i] = (float)e;
            }
            for (int i = 4; i < 12; i++)
            {
                float before = 0.25f * (_seg[i - 1] + _seg[i - 2] + _seg[i - 3] + _seg[i - 4]);
                // 128 samples at about 40 LSB rms after the high-pass is the floor below which nothing counts as an attack.
                if (_seg[i] > 8f * before && _seg[i] > 128f * 1600f) return true;
            }
        }
        return false;
    }

    private int ChooseSequence()
    {
        if (_first) _detectCur = _s.ShortBlocks && DetectAttack(0);
        bool next = _s.ShortBlocks && DetectAttack(M);
        bool effShort = _detectCur || (_prevEffShort && next);
        int seq = effShort ? 2 : next ? 1 : _prevEffShort ? 3 : 0;
        _prevEffShort = effShort;
        _detectCur = next;
        _first = false;
        return seq;
    }

    // ---- transform ---------------------------------------------------------------------------------------------------------

    private void Transform(int seq)
    {
        for (int ch = 0; ch < _ch; ch++)
        {
            var x = _hist[ch].AsSpan(0, 2 * M);
            var spec = _c[ch].Spec;
            if (seq != 2)
            {
                var w = _winLong[seq];
                for (int n = 0; n < 2 * M; n++) _tmp[n] = x[n] * w[n];
                _mdctLong.Forward(_tmp, spec);
                for (int k = _long.LowpassLine; k < M; k++) spec[k] = 0;
            }
            else
            {
                for (int win = 0; win < 8; win++)
                {
                    int off = 448 + win * S;
                    for (int n = 0; n < 2 * S; n++) _tmp[n] = x[off + n] * _winShort[n];
                    _mdctShort.Forward(_tmp.AsSpan(0, 2 * S), spec.AsSpan(win * S, S));
                    for (int k = _short.LowpassLine; k < S; k++) spec[win * S + k] = 0;
                }
            }
        }
    }

    // ---- psychoacoustics and bitstream order ------------------------------------------------------------------------------

    private void Analyze(int seq)
    {
        _isShort = seq == 2;
        var pc = _isShort ? _short : _long;
        _pc = pc;
        int nb = pc.BandCount;
        _active = pc.Active;

        for (int ch = 0; ch < _ch; ch++)
        {
            var c = _c[ch];
            if (!_isShort)
            {
                for (int b = 0; b < nb; b++) c.E[b] = BandEnergy(c.Spec, pc.Offsets[b], pc.Offsets[b + 1]);
                pc.Thresholds(c.E.AsSpan(0, nb), c.T.AsSpan(0, nb), c.PrevLong, ignorePrevious: seq == 3);
                if (seq == 1) c.PrevLong.AsSpan(0, nb).Fill(float.MaxValue);
            }
            else
            {
                bool wasShort = _prevShortBlock;
                for (int win = 0; win < 8; win++)
                {
                    var spec = c.Spec.AsSpan(win * S, S);
                    for (int b = 0; b < nb; b++) c.E[win * 16 + b] = BandEnergy(spec, pc.Offsets[b], pc.Offsets[b + 1]);
                    pc.Thresholds(c.E.AsSpan(win * 16, nb), c.T.AsSpan(win * 16, nb), c.PrevShort, ignorePrevious: win == 0 && !wasShort);
                }
            }
        }
        _prevShortBlock = _isShort;

        // Window grouping: neighbouring short windows of similar loudness share scalefactors.
        if (!_isShort)
        {
            _numGroups = 1;
            _groupLen[0] = 1;
        }
        else
        {
            _numGroups = 0;
            double prev = -1;
            for (int win = 0; win < 8; win++)
            {
                double e = 0;
                for (int ch = 0; ch < _ch; ch++)
                    for (int b = 0; b < _active; b++) e += _c[ch].E[win * 16 + b];
                bool join = win > 0 && e < 3.0 * prev + 1e3 && prev < 3.0 * e + 1e3;
                if (join) _groupLen[_numGroups - 1]++;
                else _groupLen[_numGroups++] = 1;
                prev = e;
            }
        }

        BuildEntries(pc);
    }

    private bool _prevShortBlock;
    private AacEncPsyConfig _pc = null!;

    private static float BandEnergy(ReadOnlySpan<float> x, int from, int to)
    {
        float e = 0;
        for (int i = from; i < to; i++) e += x[i] * x[i];
        return e;
    }

    // Lays the spectrum out in bitstream order and sets each entry's energy, threshold and noise cap.
    private void BuildEntries(AacEncPsyConfig pc)
    {
        int a = _active;
        for (int ch = 0; ch < _ch; ch++)
        {
            var c = _c[ch];
            int pos = 0, win0 = 0;
            for (int g = 0; g < _numGroups; g++)
            {
                int nw = _isShort ? _groupLen[g] : 1;
                for (int b = 0; b < a; b++)
                {
                    int e = g * a + b;
                    int w = pc.Offsets[b + 1] - pc.Offsets[b];
                    c.Start[e] = pos;
                    c.Len[e] = w * nw;
                    float t = float.MaxValue;
                    for (int k = 0; k < nw; k++)
                    {
                        int win = win0 + k;
                        int from = _isShort ? win * S + pc.Offsets[b] : pc.Offsets[b];
                        c.Spec.AsSpan(from, w).CopyTo(c.Co.AsSpan(pos, w));
                        pos += w;
                        t = Math.Min(t, c.T[_isShort ? win * 16 + b : b]);
                    }
                    c.EntT[e] = t * nw;
                }
                win0 += nw;
            }
            RecomputeEntryEnergies(c, g_: _numGroups * a);
        }

        // Mid/side per band when the estimated bits for mid+side undercut left+right (14496-3 4.6.8.1).
        Array.Clear(_ms);
        _anyMs = false;
        if (_ch == 2 && _s.MidSide)
        {
            var l = _c[0];
            var r = _c[1];
            int total = _numGroups * a;
            for (int e = 0; e < total; e++)
            {
                int st = l.Start[e], n = l.Len[e];
                float em = 0, es = 0;
                for (int i = st; i < st + n; i++)
                {
                    float m = 0.5f * (l.Co[i] + r.Co[i]), d = 0.5f * (l.Co[i] - r.Co[i]);
                    em += m * m;
                    es += d * d;
                }
                float tms = 0.5f * Math.Min(l.EntT[e], r.EntT[e]);
                float lr = Estimate(l.EntE[e], l.EntT[e], n) + Estimate(r.EntE[e], r.EntT[e], n);
                float msb = Estimate(em, tms, n) + Estimate(es, tms, n);
                if (msb < 0.95f * lr)
                {
                    _ms[e] = true;
                    _anyMs = true;
                    for (int i = st; i < st + n; i++)
                    {
                        float x = l.Co[i], y = r.Co[i];
                        l.Co[i] = 0.5f * (x + y);
                        r.Co[i] = 0.5f * (x - y);
                    }
                    l.EntT[e] = r.EntT[e] = tms;
                    l.EntE[e] = em;
                    r.EntE[e] = es;
                }
            }
        }

        _allMs = false;
        if (_anyMs)
        {
            _allMs = true;
            for (int e = 0; e < _numGroups * a; e++) if (!_ms[e]) { _allMs = false; break; }
        }

        _pe = 0;
        for (int ch = 0; ch < _ch; ch++)
        {
            var c = _c[ch];
            int total = _numGroups * a;
            for (int e = 0; e < total; e++)
            {
                int b = e % a;
                c.Cap[e] = pc.MinSnr[b] * c.EntE[e];
                float mx = 0;
                int st = c.Start[e];
                for (int i = st; i < st + c.Len[e]; i++)
                {
                    float v = Math.Abs(c.Co[i]);
                    c.Abs[i] = v;
                    c.Abs34[i] = MathF.Sqrt(v * MathF.Sqrt(v));
                    mx = Math.Max(mx, c.Abs34[i]);
                }
                c.Max34[e] = mx;
                _pe += Estimate(c.EntE[e], c.EntT[e], c.Len[e]);
            }
        }
    }

    private bool _anyMs, _allMs;
    private double _pe;

    private static void RecomputeEntryEnergies(AacEncChannel c, int g_)
    {
        for (int e = 0; e < g_; e++)
        {
            float en = 0;
            int st = c.Start[e];
            for (int i = st; i < st + c.Len[e]; i++) en += c.Co[i] * c.Co[i];
            c.EntE[e] = en;
        }
    }

    // Rough bits for coding a band of n lines with energy e down to noise t: half a bit per line per 6 dB of signal over noise.
    private static float Estimate(float e, float t, int n)
        => e > t && t > 0 ? 0.5f * n * MathF.Log2(e / t) : 0f;
}
