namespace OneAudio.Opus;

internal sealed partial class CeltDecoder
{
    // ------------------------------------------------------------------------------------------------
    // Bit allocation (RFC 6716 section 4.3.3)
    // ------------------------------------------------------------------------------------------------

    private readonly int[] _bits1 = new int[NbEBands];
    private readonly int[] _bits2 = new int[NbEBands];
    private readonly int[] _thresh = new int[NbEBands];
    private readonly int[] _trimOffset = new int[NbEBands];

    private int ComputeAllocation(int start, int end, int[] offsets, int[] cap, int allocTrim, out int intensity,
        out int dualStereo, int total, out int balance, int[] pulses, int[] ebits, int[] finePriority, int c, int lm,
        ref RangeDecoder dec)
    {
        var eb = CeltTables.EBands5ms;
        var bits1 = _bits1;
        var bits2 = _bits2;
        var thresh = _thresh;
        var trimOffset = _trimOffset;
        var allocVectors = CeltTables.BandAllocation;
        const int len = NbEBands;

        total = Math.Max(total, 0);
        int skipStart = start;
        int skipRsv = total >= 1 << Bitres ? 1 << Bitres : 0;
        total -= skipRsv;
        int intensityRsv = 0, dualStereoRsv = 0;
        if (c == 2)
        {
            intensityRsv = CeltTables.Log2FracTable[end - start];
            if (intensityRsv > total) intensityRsv = 0;
            else
            {
                total -= intensityRsv;
                dualStereoRsv = total >= 1 << Bitres ? 1 << Bitres : 0;
                total -= dualStereoRsv;
            }
        }
        for (int j = start; j < end; j++)
        {
            int w = eb[j + 1] - eb[j];
            thresh[j] = Math.Max(c << Bitres, (3 * w << lm << Bitres) >> 4);
            trimOffset[j] = c * w * (allocTrim - 5 - lm) * (end - j - 1) * (1 << (lm + Bitres)) >> 6;
            // Single-coefficient bands get more benefit from having one coarse value per coefficient.
            if (w << lm == 1) trimOffset[j] -= c << Bitres;
        }

        int lo = 1, hi = CeltTables.NbAllocVectors - 1;
        do
        {
            bool done = false;
            int psum = 0;
            int mid = (lo + hi) >> 1;
            for (int j = end; j-- > start;)
            {
                int n = eb[j + 1] - eb[j];
                int bitsj = c * n * allocVectors[mid * len + j] << lm >> 2;
                if (bitsj > 0) bitsj = Math.Max(0, bitsj + trimOffset[j]);
                bitsj += offsets[j];
                if (bitsj >= thresh[j] || done)
                {
                    done = true;
                    psum += Math.Min(bitsj, cap[j]);
                }
                else if (bitsj >= c << Bitres)
                {
                    psum += c << Bitres;
                }
            }
            if (psum > total) hi = mid - 1;
            else lo = mid + 1;
        } while (lo <= hi);
        hi = lo--;

        for (int j = start; j < end; j++)
        {
            int n = eb[j + 1] - eb[j];
            int bits1j = c * n * allocVectors[lo * len + j] << lm >> 2;
            int bits2j = hi >= CeltTables.NbAllocVectors ? cap[j] : c * n * allocVectors[hi * len + j] << lm >> 2;
            if (bits1j > 0) bits1j = Math.Max(0, bits1j + trimOffset[j]);
            if (bits2j > 0) bits2j = Math.Max(0, bits2j + trimOffset[j]);
            if (lo > 0) bits1j += offsets[j];
            bits2j += offsets[j];
            if (offsets[j] > 0) skipStart = j;
            bits2j = Math.Max(0, bits2j - bits1j);
            bits1[j] = bits1j;
            bits2[j] = bits2j;
        }

        return InterpBits2Pulses(start, end, skipStart, bits1, bits2, thresh, cap, total, out balance, skipRsv,
            out intensity, intensityRsv, out dualStereo, dualStereoRsv, pulses, ebits, finePriority, c, lm, ref dec);
    }

    private static int InterpBits2Pulses(int start, int end, int skipStart, int[] bits1, int[] bits2, int[] thresh,
        int[] cap, int total, out int outBalance, int skipRsv, out int intensity, int intensityRsv, out int dualStereo,
        int dualStereoRsv, int[] bits, int[] ebits, int[] finePriority, int c, int lm, ref RangeDecoder dec)
    {
        const int allocSteps = 6;
        var eb = CeltTables.EBands5ms;
        var logN = CeltTables.LogN;
        int allocFloor = c << Bitres;
        int stereo = c > 1 ? 1 : 0;
        int logM = lm << Bitres;
        int lo = 0, hi = 1 << allocSteps;
        for (int i = 0; i < allocSteps; i++)
        {
            int mid = (lo + hi) >> 1;
            int psum = 0;
            bool done = false;
            for (int j = end; j-- > start;)
            {
                int tmp = bits1[j] + (mid * bits2[j] >> allocSteps);
                if (tmp >= thresh[j] || done)
                {
                    done = true;
                    psum += Math.Min(tmp, cap[j]);
                }
                else if (tmp >= allocFloor)
                {
                    psum += allocFloor;
                }
            }
            if (psum > total) hi = mid;
            else lo = mid;
        }
        {
            int psum = 0;
            bool done = false;
            for (int j = end; j-- > start;)
            {
                int tmp = bits1[j] + (lo * bits2[j] >> allocSteps);
                if (tmp < thresh[j] && !done)
                {
                    tmp = tmp >= allocFloor ? allocFloor : 0;
                }
                else
                {
                    done = true;
                }
                tmp = Math.Min(tmp, cap[j]);
                bits[j] = tmp;
                psum += tmp;
            }

            int codedBands;
            for (codedBands = end; ; codedBands--)
            {
                int j = codedBands - 1;
                // Never skip the first band, nor a band boosted by dynamic allocation.
                if (j <= skipStart)
                {
                    total += skipRsv;
                    break;
                }
                // Left-over bits this band would get, including bits taken back from higher, skipped bands.
                int left = total - psum;
                int percoeff = left / (eb[codedBands] - eb[start]);
                left -= (eb[codedBands] - eb[start]) * percoeff;
                int rem = Math.Max(left - (eb[j] - eb[start]), 0);
                int bandWidth = eb[codedBands] - eb[j];
                int bandBits = bits[j] + percoeff * bandWidth + rem;
                // Only code a skip decision above the threshold for this band; otherwise it is force-skipped.
                if (bandBits >= Math.Max(thresh[j], allocFloor + (1 << Bitres)))
                {
                    if (dec.BitLogP(1) != 0) break;
                    psum += 1 << Bitres;
                    bandBits -= 1 << Bitres;
                }
                psum -= bits[j] + intensityRsv;
                if (intensityRsv > 0) intensityRsv = CeltTables.Log2FracTable[j - start];
                psum += intensityRsv;
                if (bandBits >= allocFloor)
                {
                    psum += allocFloor;
                    bits[j] = allocFloor;
                }
                else
                {
                    bits[j] = 0;
                }
            }

            if (intensityRsv > 0) intensity = start + (int)dec.DecodeUInt((uint)(codedBands + 1 - start));
            else intensity = 0;
            if (intensity <= start)
            {
                total += dualStereoRsv;
                dualStereoRsv = 0;
            }
            dualStereo = dualStereoRsv > 0 ? dec.BitLogP(1) : 0;

            {
                int left = total - psum;
                int percoeff = left / (eb[codedBands] - eb[start]);
                left -= (eb[codedBands] - eb[start]) * percoeff;
                for (int j = start; j < codedBands; j++)
                    bits[j] += percoeff * (eb[j + 1] - eb[j]);
                for (int j = start; j < codedBands; j++)
                {
                    int tmp = Math.Min(left, eb[j + 1] - eb[j]);
                    bits[j] += tmp;
                    left -= tmp;
                }
            }

            int balance = 0;
            int jj;
            for (jj = start; jj < codedBands; jj++)
            {
                int n0 = eb[jj + 1] - eb[jj];
                int n = n0 << lm;
                bits[jj] += balance;
                int excess;
                if (n > 1)
                {
                    excess = Math.Max(bits[jj] - cap[jj], 0);
                    bits[jj] -= excess;
                    int den = c * n + ((c == 2 && n > 2 && dualStereo == 0 && jj < intensity) ? 1 : 0);
                    int nClogN = den * (logN[jj] + logM);
                    // Offset for the number of fine bits by log2(N)/2 + FINE_OFFSET relative to the fair share.
                    int offset = (nClogN >> 1) - den * 21;
                    if (n == 2) offset += den << Bitres >> 2;
                    // Changing the offset for allocating the second and third fine energy bit.
                    if (bits[jj] + offset < den * 2 << Bitres) offset += nClogN >> 2;
                    else if (bits[jj] + offset < den * 3 << Bitres) offset += nClogN >> 3;
                    ebits[jj] = Math.Max(0, (bits[jj] + offset + (den << (Bitres - 1))) / (den << Bitres));
                    if (c * ebits[jj] > (bits[jj] >> Bitres))
                        ebits[jj] = bits[jj] >> stereo >> Bitres;
                    ebits[jj] = Math.Min(ebits[jj], 8);
                    // If we rounded down or capped this band, make it a candidate for the final fine energy pass.
                    finePriority[jj] = ebits[jj] * (den << Bitres) >= bits[jj] + offset ? 1 : 0;
                    bits[jj] -= c * ebits[jj] << Bitres;
                }
                else
                {
                    excess = Math.Max(0, bits[jj] - (c << Bitres));
                    bits[jj] -= excess;
                    ebits[jj] = 0;
                    finePriority[jj] = 1;
                }
                // Fine energy cannot use the re-balancing in the band decoder, so do it here.
                if (excess > 0)
                {
                    int extraFine = Math.Min(excess >> (stereo + Bitres), 8 - ebits[jj]);
                    ebits[jj] += extraFine;
                    int extraBits = extraFine * c << Bitres;
                    finePriority[jj] = extraBits >= excess - balance ? 1 : 0;
                    excess -= extraBits;
                }
                balance = excess;
            }
            outBalance = balance;
            for (; jj < end; jj++)
            {
                ebits[jj] = bits[jj] >> stereo >> Bitres;
                bits[jj] = 0;
                finePriority[jj] = ebits[jj] < 1 ? 1 : 0;
            }
            return codedBands;
        }
    }

    internal static int GetPulses(int i) => i < 8 ? i : (8 + (i & 7)) << ((i >> 3) - 1);

    internal static int Bits2Pulses(int band, int lm, int bits)
    {
        lm++;
        int cacheOff = CeltTables.CacheIndex[lm * NbEBands + band];
        var cache = CeltTables.CacheBits;
        int lo = 0;
        int hi = cache[cacheOff];
        bits--;
        for (int i = 0; i < 6; i++)
        {
            int mid = (lo + hi + 1) >> 1;
            if (cache[cacheOff + mid] >= bits) hi = mid;
            else lo = mid;
        }
        if (bits - (lo == 0 ? -1 : cache[cacheOff + lo]) <= cache[cacheOff + hi] - bits) return lo;
        return hi;
    }

    internal static int Pulses2Bits(int band, int lm, int pulses)
    {
        lm++;
        int cacheOff = CeltTables.CacheIndex[lm * NbEBands + band];
        return pulses == 0 ? 0 : CeltTables.CacheBits[cacheOff + pulses] + 1;
    }

    // ------------------------------------------------------------------------------------------------
    // Loss concealment (celt_decode_lost)
    // ------------------------------------------------------------------------------------------------

    private void DecodeLostFrame(Span<float> pcm, int n, int lm)
    {
        int cc = _channels;
        int syn = DecodeBufferSize - n;
        var eb = CeltTables.EBands5ms;
        int len = n + Overlap;

        if (_lossCount >= 5 || _start != 0)
        {
            // Noise substitution at the decayed (or background) band energies.
            int effEnd = Math.Min(_end, NbEBands);
            var freq = _freq;
            var x = _x;
            var bandE = _bandE;
            if (_lossCount >= 5)
            {
                Log2Amp(_start, _end, bandE, _backgroundLogE, cc);
            }
            else
            {
                float decay = _lossCount == 0 ? 1.5f : .5f;
                for (int c = 0; c < cc; c++)
                    for (int i = _start; i < _end; i++)
                        _oldBandE[c * NbEBands + i] -= decay;
                Log2Amp(_start, _end, bandE, _oldBandE, cc);
            }
            uint seed = _rng;
            for (int c = 0; c < cc; c++)
            {
                for (int i = 0; i < eb[_start] << lm; i++) x[c * n + i] = 0;
                for (int i = _start; i < NbEBands; i++)
                {
                    int boffs = n * c + (eb[i] << lm);
                    int blen = (eb[i + 1] - eb[i]) << lm;
                    for (int j = 0; j < blen; j++)
                    {
                        seed = LcgRand(seed);
                        x[boffs + j] = (int)seed >> 20;
                    }
                    RenormaliseVector(x.AsSpan(boffs, blen), 1f);
                }
                for (int i = eb[_end] << lm; i < n; i++) x[c * n + i] = 0;
            }
            _rng = seed;
            DenormaliseBands(x, freq, bandE, NbEBands, cc, 1 << lm);
            for (int c = 0; c < cc; c++)
            {
                for (int i = 0; i < eb[_start] << lm; i++) freq[c * n + i] = 0;
                int bound = eb[effEnd] << lm;
                if (_downsample != 1) bound = Math.Min(bound, n / _downsample);
                for (int i = bound; i < n; i++) freq[c * n + i] = 0;
            }
            // Keep the synthesis history aligned with the new frame (the reference decoder omits this shift).
            for (int c = 0; c < cc; c++)
                Array.Copy(_decodeMem[c], n, _decodeMem[c], 0, DecodeBufferSize - n);
            ComputeInvMdcts(0, freq, lm, n);
        }
        else
        {
            int pitchIndex;
            float fade = 1f;
            if (_lossCount == 0)
            {
                // Corresponds to a min pitch of 67 Hz.
                const int poffset = 720;
                PitchDownsample(_pitchBuf, DecodeBufferSize, cc);
                pitchIndex = PitchSearch(_pitchBuf, poffset >> 1, _pitchBuf, DecodeBufferSize - poffset, poffset - 100);
                pitchIndex = poffset - pitchIndex;
                _lastPitchIndex = pitchIndex;
            }
            else
            {
                pitchIndex = _lastPitchIndex;
                fade = .8f;
            }
            var window = CeltTables.Window;
            for (int c = 0; c < cc; c++)
            {
                var mem = _decodeMem[c];
                const int outMem = DecodeBufferSize - MaxPeriod;
                var exc = _exc;
                var e = _e;
                float decay = 1f;
                float s1 = 0;
                var lpcMem = _mem;
                Array.Clear(lpcMem);
                int offset = MaxPeriod - pitchIndex;
                for (int i = 0; i < MaxPeriod; i++) exc[i] = mem[outMem + i];
                if (_lossCount == 0)
                {
                    Autocorr(exc, _ac, window, Overlap, LpcOrder, MaxPeriod);
                    _ac[0] *= 1.0001f;
                    for (int i = 1; i <= LpcOrder; i++)
                        _ac[i] -= _ac[i] * (.008f * i) * (.008f * i);
                    CeltLpc(_lpc, c * LpcOrder, _ac, LpcOrder);
                }
                for (int i = 0; i < LpcOrder; i++) lpcMem[i] = mem[outMem + MaxPeriod - 1 - i];
                CeltFir(exc, _lpc, c * LpcOrder, exc, MaxPeriod, LpcOrder, lpcMem);
                {
                    float e1 = 1, e2 = 1;
                    int period = pitchIndex <= MaxPeriod / 2 ? pitchIndex : MaxPeriod / 2;
                    for (int i = 0; i < period; i++)
                    {
                        float a = exc[MaxPeriod - period + i];
                        float b = exc[MaxPeriod - 2 * period + i];
                        e1 += a * a;
                        e2 += b * b;
                    }
                    if (e1 > e2) e1 = e2;
                    decay = (float)Math.Sqrt((double)e1 / e2);
                }
                for (int i = 0; i < len + Overlap; i++)
                {
                    if (offset + i >= MaxPeriod)
                    {
                        offset -= pitchIndex;
                        decay = decay * decay;
                    }
                    e[i] = decay * exc[offset + i];
                    float tmp = mem[outMem + offset + i];
                    s1 += tmp * tmp;
                }
                for (int i = 0; i < LpcOrder; i++) lpcMem[i] = mem[outMem + MaxPeriod - 1 - i];
                for (int i = 0; i < len + Overlap; i++) e[i] = fade * e[i];
                CeltIir(e, _lpc, c * LpcOrder, e, len + Overlap, LpcOrder, lpcMem);
                {
                    float s2 = 0;
                    for (int i = 0; i < len + Overlap; i++) s2 += e[i] * e[i];
                    if (!(s1 > 0.2f * s2))
                    {
                        for (int i = 0; i < len + Overlap; i++) e[i] = 0;
                    }
                    else if (s1 < s2)
                    {
                        float ratio = (float)Math.Sqrt((double)(s1 + 1) / (s2 + 1));
                        for (int i = 0; i < len + Overlap; i++) e[i] = ratio * e[i];
                    }
                }
                CombFilter(mem, outMem + MaxPeriod, mem, outMem + MaxPeriod, Math.Max(_postfilterPeriod, CombMinPeriod), Math.Max(_postfilterPeriod, CombMinPeriod), Overlap,
                    _postfilterGain, _postfilterGain, _postfilterTapset, _postfilterTapset, ReadOnlySpan<float>.Empty, 0);
                for (int i = 0; i < MaxPeriod + Overlap - n; i++)
                    mem[outMem + i] = mem[outMem + n + i];
                // Apply TDAC to the concealed audio so that it blends with the previous and next frames.
                for (int i = 0; i < Overlap / 2; i++)
                {
                    float tmp = window[i] * e[n + Overlap - 1 - i] + window[Overlap - i - 1] * e[n + i];
                    mem[outMem + MaxPeriod + i] = window[Overlap - i - 1] * tmp;
                    mem[outMem + MaxPeriod + Overlap - i - 1] = window[i] * tmp;
                }
                for (int i = 0; i < n; i++) mem[outMem + MaxPeriod - n + i] = e[i];
                CombFilter(e, 0, mem, outMem + MaxPeriod, Math.Max(_postfilterPeriod, CombMinPeriod), Math.Max(_postfilterPeriod, CombMinPeriod), Overlap,
                    -_postfilterGain, -_postfilterGain, _postfilterTapset, _postfilterTapset, ReadOnlySpan<float>.Empty, 0);
                for (int i = 0; i < Overlap; i++) mem[outMem + MaxPeriod + i] = e[i];
            }
        }
        Deemphasis(pcm, n, cc);
        _lossCount++;
    }

    // ------------------------------------------------------------------------------------------------
    // Pitch search and linear prediction used by concealment
    // ------------------------------------------------------------------------------------------------

    private void PitchDownsample(float[] xLp, int len, int c)
    {
        var m0 = _decodeMem[0];
        int half = len >> 1;
        for (int i = 1; i < half; i++)
            xLp[i] = 0.5f * (0.5f * (m0[2 * i - 1] + m0[2 * i + 1]) + m0[2 * i]);
        xLp[0] = 0.5f * (0.5f * m0[1] + m0[0]);
        if (c == 2)
        {
            var m1 = _decodeMem[1];
            for (int i = 1; i < half; i++)
                xLp[i] += 0.5f * (0.5f * (m1[2 * i - 1] + m1[2 * i + 1]) + m1[2 * i]);
            xLp[0] += 0.5f * (0.5f * m1[1] + m1[0]);
        }
        Span<float> ac = stackalloc float[5];
        AutocorrNoWindow(xLp, ac, 4, half);
        ac[0] *= 1.0001f;
        for (int i = 1; i <= 4; i++)
            ac[i] -= ac[i] * (.008f * i) * (.008f * i);
        var lpc = _lpc4;
        CeltLpc(lpc, 0, ac, 4);
        float tmp = 1f;
        for (int i = 0; i < 4; i++)
        {
            tmp = .9f * tmp;
            lpc[i] *= tmp;
        }
        var mem = _mem4;
        Array.Clear(mem);
        CeltFir(xLp, lpc, 0, xLp, half, 4, mem);
        mem[0] = 0;
        lpc[0] = .8f;
        CeltFir(xLp, lpc, 0, xLp, half, 1, mem);
    }

    private readonly float[] _lpc4 = new float[4];
    private readonly float[] _mem4 = new float[4];

    internal static void FindBestPitch(float[] xcorr, float[] y, int yOff, int len, int maxPitch, Span<int> bestPitch)
    {
        float syy = 1;
        Span<float> bestNum = stackalloc float[2];
        Span<float> bestDen = stackalloc float[2];
        bestNum[0] = -1;
        bestNum[1] = -1;
        bestDen[0] = 0;
        bestDen[1] = 0;
        bestPitch[0] = 0;
        bestPitch[1] = 1;
        for (int j = 0; j < len; j++) syy += y[yOff + j] * y[yOff + j];
        for (int i = 0; i < maxPitch; i++)
        {
            if (xcorr[i] > 0)
            {
                float num = xcorr[i] * xcorr[i];
                if (num * bestDen[1] > bestNum[1] * syy)
                {
                    if (num * bestDen[0] > bestNum[0] * syy)
                    {
                        bestNum[1] = bestNum[0];
                        bestDen[1] = bestDen[0];
                        bestPitch[1] = bestPitch[0];
                        bestNum[0] = num;
                        bestDen[0] = syy;
                        bestPitch[0] = i;
                    }
                    else
                    {
                        bestNum[1] = num;
                        bestDen[1] = syy;
                        bestPitch[1] = i;
                    }
                }
            }
            syy += y[yOff + i + len] * y[yOff + i + len] - y[yOff + i] * y[yOff + i];
            syy = Math.Max(1, syy);
        }
    }

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
        FindBestPitch(xcorr, yLp4, 0, len >> 2, maxPitch >> 2, bestPitch);
        for (int i = 0; i < maxPitch >> 1; i++)
        {
            float sum = 0;
            xcorr[i] = 0;
            if (Math.Abs(i - 2 * bestPitch[0]) > 2 && Math.Abs(i - 2 * bestPitch[1]) > 2) continue;
            for (int j = 0; j < len >> 1; j++) sum += xLp[xOff + j] * y[i + j];
            xcorr[i] = Math.Max(-1, sum);
        }
        FindBestPitch(xcorr, y, 0, len >> 1, maxPitch >> 1, bestPitch);
        int offset;
        if (bestPitch[0] > 0 && bestPitch[0] < (maxPitch >> 1) - 1)
        {
            float a = xcorr[bestPitch[0] - 1], b = xcorr[bestPitch[0]], c = xcorr[bestPitch[0] + 1];
            if (c - a > .7f * (b - a)) offset = 1;
            else if (a - c > .7f * (b - c)) offset = -1;
            else offset = 0;
        }
        else
        {
            offset = 0;
        }
        return 2 * bestPitch[0] - offset;
    }

    internal static void CeltLpc(float[] lpc, int off, ReadOnlySpan<float> ac, int p)
    {
        float error = ac[0];
        for (int i = 0; i < p; i++) lpc[off + i] = 0;
        if (ac[0] != 0)
        {
            for (int i = 0; i < p; i++)
            {
                float rr = 0;
                for (int j = 0; j < i; j++) rr += lpc[off + j] * ac[i - j];
                rr += ac[i + 1];
                float r = -(rr / error);
                lpc[off + i] = r;
                for (int j = 0; j < (i + 1) >> 1; j++)
                {
                    float tmp1 = lpc[off + j];
                    float tmp2 = lpc[off + i - 1 - j];
                    lpc[off + j] = tmp1 + r * tmp2;
                    lpc[off + i - 1 - j] = tmp2 + r * tmp1;
                }
                error = error - r * r * error;
                if (error < .001f * ac[0]) break;
            }
        }
    }

    internal static void CeltFir(float[] x, float[] num, int numOff, float[] y, int n, int ord, float[] mem)
    {
        for (int i = 0; i < n; i++)
        {
            float sum = x[i];
            for (int j = 0; j < ord; j++) sum += num[numOff + j] * mem[j];
            for (int j = ord - 1; j >= 1; j--) mem[j] = mem[j - 1];
            mem[0] = x[i];
            y[i] = sum;
        }
    }

    private static void CeltIir(float[] x, float[] den, int denOff, float[] y, int n, int ord, float[] mem)
    {
        for (int i = 0; i < n; i++)
        {
            float sum = x[i];
            for (int j = 0; j < ord; j++) sum -= den[denOff + j] * mem[j];
            for (int j = ord - 1; j >= 1; j--) mem[j] = mem[j - 1];
            mem[0] = sum;
            y[i] = sum;
        }
    }

    private void Autocorr(float[] x, float[] ac, ReadOnlySpan<float> window, int overlap, int lag, int n)
    {
        var xx = _xx;
        for (int i = 0; i < n; i++) xx[i] = x[i];
        for (int i = 0; i < overlap; i++)
        {
            xx[i] = x[i] * window[i];
            xx[n - i - 1] = x[n - i - 1] * window[i];
        }
        while (lag >= 0)
        {
            float d = 0;
            for (int i = lag; i < n; i++) d += xx[i] * xx[i - lag];
            ac[lag] = d;
            lag--;
        }
        ac[0] += 10;
    }

    private static void AutocorrNoWindow(float[] x, Span<float> ac, int lag, int n)
    {
        while (lag >= 0)
        {
            float d = 0;
            for (int i = lag; i < n; i++) d += x[i] * x[i - lag];
            ac[lag] = d;
            lag--;
        }
        ac[0] += 10;
    }
}
