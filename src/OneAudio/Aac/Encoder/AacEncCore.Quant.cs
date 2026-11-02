namespace OneAudio.Aac;

internal sealed partial class AacEncCore
{
    // 2^((sf - 100) / 4): the quantizer step for scalefactor sf (14496-3 4.6.2).
    private static readonly float[] StepTab = BuildTab(sf => Math.Pow(2, (sf - 100) / 4.0));
    // Step^(-3/4): maps |x|^(3/4) to the quantizer's input.
    private static readonly float[] InvTab = BuildTab(sf => Math.Pow(2, -(sf - 100) * 3.0 / 16.0));

    private static float[] BuildTab(Func<int, double> f)
    {
        var t = new float[256];
        for (int i = 0; i < 256; i++) t[i] = (float)f(i);
        return t;
    }

    // Quantizer rounding offset. The decoder-side rule is x = q^(4/3) * step; rounding |x|^(3/4) up at 0.3 instead of 0.5 widens the
    // dead zone, which spends fewer bits on values that sit just above a threshold and measures better at equal rate.
    private const float Rounding = 0.30f;

    // Squared quantization error of a band at scalefactor sf.
    private static float Noise(AacEncChannel c, int start, int len, int sf)
    {
        float inv = InvTab[sf], step = StepTab[sf], err = 0;
        var abs = c.Abs.AsSpan(start, len);
        var a34 = c.Abs34.AsSpan(start, len);
        var p43 = AacEncTables.Pow43;
        for (int i = 0; i < abs.Length; i++)
        {
            int q = (int)(a34[i] * inv + Rounding);
            if (q > AacEncTables.MaxQuant) q = AacEncTables.MaxQuant;
            float d = abs[i] - p43[q] * step;
            err += d * d;
        }
        return err;
    }

    private static bool Quantize(AacEncChannel c, int start, int len, int sf)
    {
        float inv = InvTab[sf];
        bool any = false;
        for (int i = start; i < start + len; i++)
        {
            int q = (int)(c.Abs34[i] * inv + Rounding);
            if (q > AacEncTables.MaxQuant) q = AacEncTables.MaxQuant;
            if (q != 0) any = true;
            c.Q[i] = c.Co[i] < 0 ? -q : q;
        }
        return any;
    }

    /// <summary>
    /// Quantizes every channel for threshold shift <paramref name="r"/> (added to the fourth root of each band threshold, so quiet
    /// bands keep their noise near their threshold while loud ones give way first), fixes the scalefactors and codebooks and returns
    /// the size in bits.
    /// </summary>
    private long Trial(float r, bool avoidHoles)
    {
        int total = _numGroups * _active;

        for (int ch = 0; ch < _ch; ch++)
        {
            var c = _c[ch];
            for (int e = 0; e < total; e++)
            {
                c.Zero[e] = true;
                c.Noise[e] = false;
                float en = c.EntE[e], t0 = c.EntT[e];
                if (en <= t0 || en <= 0) { MarkNoise(c, e); continue; }
                float t4 = MathF.Sqrt(MathF.Sqrt(t0)) + r;
                float n = t4 * t4;
                n *= n;
                if (avoidHoles) n = Math.Min(n, Math.Max(c.Cap[e], t0));
                if (en <= n) { MarkNoise(c, e); continue; }
                int len = c.Len[e], st = c.Start[e];
                // Starting point: uniform quantization noise is step^2 / 12 per line.
                float delta = MathF.Sqrt(12f * n / len);
                int sf = 100 + (int)MathF.Round(4f * MathF.Log2(Math.Max(delta, 1e-3f)));
                // Smallest scalefactor that keeps every magnitude within the escape range.
                int sfMin = c.Max34[e] > 0 ? (int)MathF.Ceiling(100f - 16f / 3f * MathF.Log2(8190f / c.Max34[e])) : 0;
                sfMin = Math.Clamp(sfMin, 0, 255);
                sf = Math.Clamp(sf, sfMin, 255);
                if (Noise(c, st, len, sf) <= n)
                {
                    for (int k = 0; k < 8 && sf < 255 && Noise(c, st, len, sf + 1) <= n; k++) sf++;
                }
                else
                {
                    while (sf > sfMin)
                    {
                        sf--;
                        if (Noise(c, st, len, sf) <= n) break;
                    }
                }
                c.Sf[e] = sf;
                c.Zero[e] = false;
            }

            // Scalefactor deltas are limited to +-60 between coded neighbours (4.6.2.3): lower the finer-than-neighbour limit.
            int prev = -1000;
            for (int g = 0; g < _numGroups; g++)
                for (int b = 0; b < _active; b++)
                {
                    int e = g * _active + b;
                    if (c.Zero[e]) continue;
                    if (prev > -1000) c.Sf[e] = Math.Min(c.Sf[e], prev + 60);
                    prev = c.Sf[e];
                }
            prev = -1000;
            for (int g = _numGroups - 1; g >= 0; g--)
                for (int b = _active - 1; b >= 0; b--)
                {
                    int e = g * _active + b;
                    if (c.Zero[e]) continue;
                    if (prev > -1000) c.Sf[e] = Math.Min(c.Sf[e], prev + 60);
                    prev = c.Sf[e];
                }
            for (int e = 0; e < total; e++)
            {
                if (c.Zero[e]) { c.Q.AsSpan(c.Start[e], c.Len[e]).Clear(); continue; }
                if (!Quantize(c, c.Start[e], c.Len[e], c.Sf[e])) { c.Zero[e] = true; MarkNoise(c, e); }

            }
        }

        // max_sfb is shared by the channels of a common window: the highest coded band of any group.
        int maxSfb = 0;
        for (int ch = 0; ch < _ch; ch++)
            for (int e = 0; e < total; e++)
                if (!_c[ch].Zero[e] || _c[ch].Noise[e]) maxSfb = Math.Max(maxSfb, e % _active + 1);
        _maxSfb = maxSfb;

        for (int ch = 0; ch < _ch; ch++) ChooseCodebooks(_c[ch]);

        var counter = new AacBitCounter();
        WriteBlock(ref counter);
        return counter.Bits;
    }

    // A band that gets no spectrum but is above the threshold in quiet and lies in the upper spectrum is sent as noise with its
    // energy (PNS), so the decoded spectrum has no hole there. M/S bands are left alone: the decoder would take a noise/noise pair as
    // correlated and give both channels one noise vector.
    private void MarkNoise(AacEncChannel c, int e)
    {
        if (!_s.NoiseSubstitution || _ms[e]) return;
        int b = e % _active;
        var pc = _pc;
        if (pc.Offsets[b] * (_rate / 2) / pc.Lines < PnsMinHz) return;
        int nw = _isShort ? c.Len[e] / (pc.Offsets[b + 1] - pc.Offsets[b]) : 1;
        float perWindow = c.EntE[e] / nw;
        if (perWindow < 16f * (pc.Offsets[b + 1] - pc.Offsets[b])) return;
        c.Noise[e] = true;
        // The decoder scales the noise of one window to energy 2^((nrg - 60) / 2) in full-scale units; this encoder's spectrum is 32768 times larger.
        c.NoiseNrg[e] = (int)MathF.Round(2f * MathF.Log2(perWindow));
    }

    private const int PnsMinHz = 4000;

    // Cheapest section partition per window group: dynamic programming over (band, codebook) with the section header cost.
    private readonly int[] _dpCost = new int[64 * 14];
    private readonly int[] _dpBest = new int[64 * 14];
    private readonly sbyte[] _dpFrom = new sbyte[64 * 14];

    private void ChooseCodebooks(AacEncChannel c)
    {
        int hdr = (_isShort ? 3 : 5) + 4;
        for (int g = 0; g < _numGroups; g++)
        {
            int baseE = g * _active;
            int n = _maxSfb;
            if (n == 0) continue;
            for (int b = 0; b < n; b++)
            {
                int e = baseE + b;
                for (int k = 0; k < 14; k++) _dpCost[b * 14 + k] = int.MaxValue;
                if (c.Noise[e]) { _dpCost[b * 14 + 13] = 0; continue; }
                if (c.Zero[e]) { _dpCost[b * 14] = 0; continue; }
                int st = c.Start[e], len = c.Len[e];
                int mx = 0;
                for (int i = st; i < st + len; i++) mx = Math.Max(mx, Math.Abs(c.Q[i]));
                int tried = 0;
                for (int book = 1; book <= 11 && tried < 4; book++)
                {
                    if (book < 11 && mx > AacEncTables.BookMax[book]) continue;
                    _dpCost[b * 14 + book] = BandBits(c.Q.AsSpan(st, len), book);
                    tried++;
                }
            }
            // forward pass
            for (int k = 0; k < 14; k++)
            {
                _dpBest[k] = _dpCost[k] == int.MaxValue ? int.MaxValue : _dpCost[k] + hdr;
                _dpFrom[k] = -1;
            }
            for (int b = 1; b < n; b++)
            {
                int bestAny = int.MaxValue, bestAnyBook = -1;
                for (int k = 0; k < 14; k++)
                    if (_dpBest[(b - 1) * 14 + k] < bestAny) { bestAny = _dpBest[(b - 1) * 14 + k]; bestAnyBook = k; }
                for (int k = 0; k < 14; k++)
                {
                    int cost = _dpCost[b * 14 + k];
                    int idx = b * 14 + k;
                    if (cost == int.MaxValue) { _dpBest[idx] = int.MaxValue; continue; }
                    int same = _dpBest[(b - 1) * 14 + k];
                    long viaSame = same == int.MaxValue ? long.MaxValue : same;
                    long viaNew = bestAny == int.MaxValue ? long.MaxValue : (long)bestAny + hdr;
                    if (viaSame <= viaNew) { _dpBest[idx] = (int)(viaSame + cost); _dpFrom[idx] = (sbyte)k; }
                    else { _dpBest[idx] = (int)(viaNew + cost); _dpFrom[idx] = (sbyte)bestAnyBook; }
                }
            }
            int last = (n - 1) * 14, bk = 0, bv = int.MaxValue;
            for (int k = 0; k < 14; k++)
                if (_dpBest[last + k] < bv) { bv = _dpBest[last + k]; bk = k; }
            for (int b = n - 1; b >= 0; b--)
            {
                c.Cb[baseE + b] = bk;
                bk = b > 0 ? _dpFrom[b * 14 + bk] : 0;
            }
        }
    }

    // Bits to code a band with a spectral codebook: codewords, sign bits and escape sequences (4.4.2.7).
    private static int BandBits(ReadOnlySpan<int> q, int book)
    {
        var len = AacEncTables.Len[book];
        int bits = 0;
        if (book <= 4)
        {
            bool signed_ = book <= 2;
            for (int i = 0; i < q.Length; i += 4)
            {
                int sym;
                if (signed_) sym = (q[i] + 1) | (q[i + 1] + 1) << 2 | (q[i + 2] + 1) << 4 | (q[i + 3] + 1) << 6;
                else
                {
                    int a0 = Math.Abs(q[i]), a1 = Math.Abs(q[i + 1]), a2 = Math.Abs(q[i + 2]), a3 = Math.Abs(q[i + 3]);
                    sym = a0 | a1 << 2 | a2 << 4 | a3 << 6;
                    bits += (a0 != 0 ? 1 : 0) + (a1 != 0 ? 1 : 0) + (a2 != 0 ? 1 : 0) + (a3 != 0 ? 1 : 0);
                }
                bits += len[sym];
            }
            return bits;
        }
        if (book <= 6)
        {
            for (int i = 0; i < q.Length; i += 2) bits += len[(q[i] + 4) | (q[i + 1] + 4) << 4];
            return bits;
        }
        if (book <= 10)
        {
            for (int i = 0; i < q.Length; i += 2)
            {
                int a0 = Math.Abs(q[i]), a1 = Math.Abs(q[i + 1]);
                bits += len[a0 | a1 << 4] + (a0 != 0 ? 1 : 0) + (a1 != 0 ? 1 : 0);
            }
            return bits;
        }
        for (int i = 0; i < q.Length; i += 2)
        {
            int a0 = Math.Abs(q[i]), a1 = Math.Abs(q[i + 1]);
            bits += len[Math.Min(a0, 16) | Math.Min(a1, 16) << 5] + (a0 != 0 ? 1 : 0) + (a1 != 0 ? 1 : 0);
            if (a0 >= 16) bits += EscapeBits(a0);
            if (a1 >= 16) bits += EscapeBits(a1);
        }
        return bits;
    }

    private static int EscapeBits(int a)
    {
        int n = 31 - System.Numerics.BitOperations.LeadingZeroCount((uint)a); // floor(log2 a), at least 4
        return (n - 4) + 1 + n;
    }
}
