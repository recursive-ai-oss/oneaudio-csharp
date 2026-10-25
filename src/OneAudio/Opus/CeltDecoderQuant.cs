namespace OneAudio.Opus;

internal sealed partial class CeltDecoder
{
    // ------------------------------------------------------------------------------------------------
    // Band shape decoding (quant_all_bands / quant_band, decode path)
    // ------------------------------------------------------------------------------------------------

    private void QuantAllBands(int start, int end, float[] xAll, bool stereo, byte[] collapseMasks, int[] pulses,
        int shortBlocks, int spread, bool dualStereo, int intensity, int[] tfRes, int totalBits, int balance,
        ref RangeDecoder dec, int lm, int codedBands, ref uint seed, int n)
    {
        var eb = CeltTables.EBands5ms;
        int m = 1 << lm;
        int b = shortBlocks != 0 ? m : 1;
        int c = stereo ? 2 : 1;
        var norm = _norm;
        int norm2 = m * eb[NbEBands];
        Array.Clear(norm, 0, c * norm2);
        var lowbandScratch = _lowbandScratch;
        int lowbandOffset = 0;
        bool updateLowband = true;
        int remainingBits;

        for (int i = start; i < end; i++)
        {
            int xo = m * eb[i];
            int bandN = m * eb[i + 1] - m * eb[i];
            int tell = (int)dec.TellFrac();
            if (i != start) balance -= tell;
            remainingBits = totalBits - tell - 1;
            int bandBits;
            if (i <= codedBands - 1)
            {
                int currBalance = balance / Math.Min(3, codedBands - i);
                bandBits = Math.Max(0, Math.Min(16383, Math.Min(remainingBits + 1, pulses[i] + currBalance)));
            }
            else
            {
                bandBits = 0;
            }

            if ((m * eb[i] - bandN >= m * eb[start] || i == start + 1) && (updateLowband || lowbandOffset == 0))
                lowbandOffset = i;

            if (i == start + 1)
            {
                // Duplicate enough of the first band's folding data to fold the second band; no data for CELT-only.
                int n1 = m * (eb[start + 1] - eb[start]);
                int n2 = m * (eb[start + 2] - eb[start + 1]);
                int offset = m * eb[start];
                Array.Copy(norm, offset + 2 * n1 - n2, norm, offset + n1, n2 - n1);
                if (c == 2) Array.Copy(norm, norm2 + offset + 2 * n1 - n2, norm, norm2 + offset + n1, n2 - n1);
            }

            int tfChange = tfRes[i];
            Span<float> xs = xAll.AsSpan(xo, bandN);
            Span<float> ys = stereo ? xAll.AsSpan(n + xo, bandN) : default;

            int effectiveLowband = -1;
            uint xCm, yCm;
            // A conservative estimate of the collapse masks of the bands we are going to fold from.
            if (lowbandOffset != 0 && (spread != SpreadAggressive || b > 1 || tfChange < 0))
            {
                effectiveLowband = Math.Max(m * eb[start], m * eb[lowbandOffset] - bandN);
                int foldStart = lowbandOffset;
                while (m * eb[--foldStart] > effectiveLowband) { }
                int foldEnd = lowbandOffset - 1;
                while (++foldEnd < i && m * eb[foldEnd] < effectiveLowband + bandN) { }
                xCm = yCm = 0;
                int foldI = foldStart;
                do
                {
                    xCm |= collapseMasks[foldI * c + 0];
                    yCm |= collapseMasks[foldI * c + c - 1];
                } while (++foldI < foldEnd);
            }
            else
            {
                // Otherwise we fold with the LCG, so all blocks will (almost always) be non-zero.
                xCm = yCm = (1u << b) - 1;
            }

            if (dualStereo && i == intensity)
            {
                dualStereo = false;
                for (int j = m * eb[start]; j < m * eb[i]; j++)
                    norm[j] = 0.5f * (norm[j] + norm[norm2 + j]);
            }

            Span<float> lowband0 = effectiveLowband != -1 ? norm.AsSpan(effectiveLowband) : default;
            if (dualStereo)
            {
                xCm = QuantBand(ref dec, i, xs, default, bandN, bandBits / 2, spread, b, intensity, tfChange, lowband0,
                    ref remainingBits, lm, norm.AsSpan(m * eb[i]), 0, ref seed, 1f, lowbandScratch, (int)xCm);
                Span<float> lowband1 = effectiveLowband != -1 ? norm.AsSpan(norm2 + effectiveLowband) : default;
                yCm = QuantBand(ref dec, i, ys, default, bandN, bandBits / 2, spread, b, intensity, tfChange, lowband1,
                    ref remainingBits, lm, norm.AsSpan(norm2 + m * eb[i]), 0, ref seed, 1f, lowbandScratch, (int)yCm);
            }
            else
            {
                xCm = QuantBand(ref dec, i, xs, ys, bandN, bandBits, spread, b, intensity, tfChange, lowband0,
                    ref remainingBits, lm, norm.AsSpan(m * eb[i]), 0, ref seed, 1f, lowbandScratch, (int)(xCm | yCm));
                yCm = xCm;
            }
            collapseMasks[i * c + 0] = (byte)xCm;
            collapseMasks[i * c + c - 1] = (byte)yCm;
            balance += pulses[i] + tell;
            updateLowband = bandBits > (bandN << Bitres);
        }
    }

    internal static int ComputeQn(int n, int b, int offset, int pulseCap, bool stereo)
    {
        int n2 = 2 * n - 1;
        if (stereo && n == 2) n2--;
        // The upper limit ensures that in a stereo split with itheta==16384 there are enough bits left for a pulse.
        int qb = Math.Min(b - pulseCap - (4 << Bitres), (b + n2 * offset) / n2);
        qb = Math.Min(8 << Bitres, qb);
        if (qb < (1 << Bitres >> 1)) return 1;
        int qn = CeltTables.Exp2Table8[qb & 0x7] >> (14 - (qb >> Bitres));
        return (qn + 1) >> 1 << 1;
    }

    private void DeinterleaveHadamard(Span<float> x, int n0, int stride, bool hadamard)
    {
        int n = n0 * stride;
        var tmp = _hadamardTmp;
        if (hadamard)
        {
            int ord = stride - 2;
            for (int i = 0; i < stride; i++)
                for (int j = 0; j < n0; j++)
                    tmp[CeltTables.OrderyTable[ord + i] * n0 + j] = x[j * stride + i];
        }
        else
        {
            for (int i = 0; i < stride; i++)
                for (int j = 0; j < n0; j++)
                    tmp[i * n0 + j] = x[j * stride + i];
        }
        for (int j = 0; j < n; j++) x[j] = tmp[j];
    }

    private void InterleaveHadamard(Span<float> x, int n0, int stride, bool hadamard)
    {
        int n = n0 * stride;
        var tmp = _hadamardTmp;
        if (hadamard)
        {
            int ord = stride - 2;
            for (int i = 0; i < stride; i++)
                for (int j = 0; j < n0; j++)
                    tmp[j * stride + i] = x[CeltTables.OrderyTable[ord + i] * n0 + j];
        }
        else
        {
            for (int i = 0; i < stride; i++)
                for (int j = 0; j < n0; j++)
                    tmp[j * stride + i] = x[i * n0 + j];
        }
        for (int j = 0; j < n; j++) x[j] = tmp[j];
    }

    internal static void Haar1(Span<float> x, int n0, int stride)
    {
        n0 >>= 1;
        for (int i = 0; i < stride; i++)
        {
            for (int j = 0; j < n0; j++)
            {
                float tmp1 = .70710678f * x[stride * 2 * j + i];
                float tmp2 = .70710678f * x[stride * (2 * j + 1) + i];
                x[stride * 2 * j + i] = tmp1 + tmp2;
                x[stride * (2 * j + 1) + i] = tmp1 - tmp2;
            }
        }
    }

    internal static void StereoMerge(Span<float> x, Span<float> y, float mid, int n)
    {
        float xp = 0, side = 0;
        for (int j = 0; j < n; j++)
        {
            xp += x[j] * y[j];
            side += y[j] * y[j];
        }
        xp = mid * xp;
        float mid2 = mid;
        float el = mid2 * mid2 + side - 2 * xp;
        float er = mid2 * mid2 + side + 2 * xp;
        if (er < 6e-4f || el < 6e-4f)
        {
            for (int j = 0; j < n; j++) y[j] = x[j];
            return;
        }
        float lgain = 1f / (float)Math.Sqrt(el);
        float rgain = 1f / (float)Math.Sqrt(er);
        for (int j = 0; j < n; j++)
        {
            float l = mid * x[j];
            float r = y[j];
            x[j] = lgain * (l - r);
            y[j] = rgain * (l + r);
        }
    }

    /// <summary>
    /// Decodes one band, splitting it in two (and recursing) when the allocation calls for a mid/side or time split.
    /// Recursion depth is bounded by the frame size (at most 3 splits), not by the input.
    /// </summary>
    private uint QuantBand(ref RangeDecoder dec, int i, Span<float> x, Span<float> y, int n, int b, int spread, int bBlocks,
        int intensity, int tfChange, Span<float> lowband, ref int remainingBits, int lm, Span<float> lowbandOut, int level,
        ref uint seed, float gain, Span<float> lowbandScratch, int fill)
    {
        int n0 = n;
        int nB = n;
        int b0 = bBlocks;
        int timeDivide = 0;
        int recombine = 0;
        int inv = 0;
        float mid = 0, side = 0;
        uint cm = 0;
        bool longBlocks = b0 == 1;
        int blocks = bBlocks;
        nB /= blocks;
        int nB0 = nB;
        bool stereo = !y.IsEmpty;
        bool split = stereo;

        if (n == 1)
        {
            for (int ch = 0; ch < (stereo ? 2 : 1); ch++)
            {
                var xv = ch == 0 ? x : y;
                int sign = 0;
                if (remainingBits >= 1 << Bitres)
                {
                    sign = (int)dec.Bits(1);
                    remainingBits -= 1 << Bitres;
                    b -= 1 << Bitres;
                }
                xv[0] = sign != 0 ? -1f : 1f;
            }
            if (!lowbandOut.IsEmpty) lowbandOut[0] = x[0];
            return 1;
        }

        if (!stereo && level == 0)
        {
            if (tfChange > 0) recombine = tfChange;
            // Band recombining to increase frequency resolution
            if (!lowband.IsEmpty && (recombine != 0 || ((nB & 1) == 0 && tfChange < 0) || b0 > 1))
            {
                lowband.Slice(0, n).CopyTo(lowbandScratch);
                lowband = lowbandScratch;
            }
            for (int k = 0; k < recombine; k++)
            {
                if (!lowband.IsEmpty) Haar1(lowband, n >> k, 1 << k);
                fill = CeltTables.BitInterleaveTable[fill & 0xF] | CeltTables.BitInterleaveTable[(fill >> 4) & 0xF] << 2;
            }
            blocks >>= recombine;
            nB <<= recombine;

            // Increasing the time resolution
            while ((nB & 1) == 0 && tfChange < 0)
            {
                if (!lowband.IsEmpty) Haar1(lowband, nB, blocks);
                fill |= fill << blocks;
                blocks <<= 1;
                nB >>= 1;
                timeDivide++;
                tfChange++;
            }
            b0 = blocks;
            nB0 = nB;

            // Reorganize the samples in time order instead of frequency order
            if (b0 > 1 && !lowband.IsEmpty)
                DeinterleaveHadamard(lowband, nB >> recombine, b0 << recombine, longBlocks);
        }

        int cacheOff = CeltTables.CacheIndex[(lm + 1) * NbEBands + i];
        // If we need 1.5 more bit than we can produce, split the band in two.
        if (!stereo && lm != -1 && b > CeltTables.CacheBits[cacheOff + CeltTables.CacheBits[cacheOff]] + 12 && n > 2)
        {
            n >>= 1;
            y = x.Slice(n);
            split = true;
            lm -= 1;
            if (blocks == 1) fill = (fill & 1) | (fill << 1);
            blocks = (blocks + 1) >> 1;
        }

        if (split)
        {
            int itheta = 0;
            int pulseCap = CeltTables.LogN[i] + lm * (1 << Bitres);
            int offset = (pulseCap >> 1) - (stereo && n == 2 ? 16 : 4);
            int qn = ComputeQn(n, b, offset, pulseCap, stereo);
            if (stereo && i >= intensity) qn = 1;
            int tell = (int)dec.TellFrac();
            if (qn != 1)
            {
                // Entropy coding of the angle: a step PDF for stereo, uniform for the time split, triangular otherwise.
                if (stereo && n > 2)
                {
                    int p0 = 3;
                    int x0 = qn / 2;
                    int ft = p0 * (x0 + 1) + x0;
                    int fs = (int)dec.Decode((uint)ft);
                    int xx;
                    if (fs < (x0 + 1) * p0) xx = fs / p0;
                    else xx = x0 + 1 + (fs - (x0 + 1) * p0);
                    dec.Update(xx <= x0 ? (uint)(p0 * xx) : (uint)((xx - 1 - x0) + (x0 + 1) * p0),
                        xx <= x0 ? (uint)(p0 * (xx + 1)) : (uint)((xx - x0) + (x0 + 1) * p0), (uint)ft);
                    itheta = xx;
                }
                else if (b0 > 1 || stereo)
                {
                    itheta = (int)dec.DecodeUInt((uint)(qn + 1));
                }
                else
                {
                    int ft = ((qn >> 1) + 1) * ((qn >> 1) + 1);
                    int fm = (int)dec.Decode((uint)ft);
                    int fs, fl;
                    if (fm < ((qn >> 1) * ((qn >> 1) + 1) >> 1))
                    {
                        itheta = (int)((Isqrt32(8 * (uint)fm + 1) - 1) >> 1);
                        fs = itheta + 1;
                        fl = itheta * (itheta + 1) >> 1;
                    }
                    else
                    {
                        itheta = (int)((2 * (qn + 1) - Isqrt32(8 * (uint)(ft - fm - 1) + 1)) >> 1);
                        fs = qn + 1 - itheta;
                        fl = ft - ((qn + 1 - itheta) * (qn + 2 - itheta) >> 1);
                    }
                    dec.Update((uint)fl, (uint)(fl + fs), (uint)ft);
                }
                itheta = itheta * 16384 / qn;
            }
            else if (stereo)
            {
                if (b > 2 << Bitres && remainingBits > 2 << Bitres) inv = dec.BitLogP(2);
                else inv = 0;
                // RFC 8251 section 10: optionally skip the 180 degree phase shift of intensity stereo.
                if (DisablePhaseInversion) inv = 0;
                itheta = 0;
            }
            int qalloc = (int)dec.TellFrac() - tell;
            b -= qalloc;
            int origFill = fill;
            int imid, iside, delta;
            if (itheta == 0)
            {
                imid = 32767;
                iside = 0;
                fill &= (1 << blocks) - 1;
                delta = -16384;
            }
            else if (itheta == 16384)
            {
                imid = 0;
                iside = 32767;
                fill &= ((1 << blocks) - 1) << blocks;
                delta = 16384;
            }
            else
            {
                imid = BitexactCos(itheta);
                iside = BitexactCos(16384 - itheta);
                // The mid versus side allocation that minimizes squared error in that band.
                delta = FracMul16((n - 1) << 7, BitexactLog2Tan(iside, imid));
            }
            mid = (1f / 32768) * imid;
            side = (1f / 32768) * iside;

            int mbits, sbits;
            if (n == 2 && stereo)
            {
                // Special case for N=2: mid and side are orthogonal, so the side needs just one bit.
                int sign = 0;
                mbits = b;
                sbits = 0;
                if (itheta != 0 && itheta != 16384) sbits = 1 << Bitres;
                mbits -= sbits;
                int cc = itheta > 8192 ? 1 : 0;
                remainingBits -= qalloc + sbits;
                var x2 = cc != 0 ? y : x;
                var y2 = cc != 0 ? x : y;
                if (sbits != 0) sign = (int)dec.Bits(1);
                sign = 1 - 2 * sign;
                // orig_fill is used so the side can be folded even when itheta==16384 cleared the low bits of fill.
                cm = QuantBand(ref dec, i, x2, default, n, mbits, spread, blocks, intensity, tfChange, lowband,
                    ref remainingBits, lm, lowbandOut, level, ref seed, gain, lowbandScratch, origFill);
                y2[0] = -sign * x2[1];
                y2[1] = sign * x2[0];
                x[0] = mid * x[0];
                x[1] = mid * x[1];
                y[0] = side * y[0];
                y[1] = side * y[1];
                float tmp = x[0];
                x[0] = tmp - y[0];
                y[0] = tmp + y[0];
                tmp = x[1];
                x[1] = tmp - y[1];
                y[1] = tmp + y[1];
            }
            else
            {
                Span<float> nextLowband2 = default;
                Span<float> nextLowbandOut1 = default;
                int nextLevel = 0;
                if (b0 > 1 && !stereo && (itheta & 0x3fff) != 0)
                {
                    if (itheta > 8192) delta -= delta >> (4 - lm);
                    else delta = Math.Min(0, delta + (n << Bitres >> (5 - lm)));
                }
                mbits = Math.Max(0, Math.Min(b, (b - delta) / 2));
                sbits = b - mbits;
                remainingBits -= qalloc;
                if (!lowband.IsEmpty && !stereo) nextLowband2 = lowband.Slice(n);
                if (stereo) nextLowbandOut1 = lowbandOut;
                else nextLevel = level + 1;
                int rebalance = remainingBits;
                int shift = (b0 >> 1) & (stereo ? 0 : -1);
                if (mbits >= sbits)
                {
                    // In stereo the mid is not scaled, because the normalized mid is needed for folding.
                    cm = QuantBand(ref dec, i, x, default, n, mbits, spread, blocks, intensity, tfChange, lowband,
                        ref remainingBits, lm, nextLowbandOut1, nextLevel, ref seed, stereo ? 1f : gain * mid,
                        lowbandScratch, fill);
                    rebalance = mbits - (rebalance - remainingBits);
                    if (rebalance > 3 << Bitres && itheta != 0) sbits += rebalance - (3 << Bitres);
                    // For a stereo split the high bits of fill are always zero, so the side is never folded.
                    cm |= QuantBand(ref dec, i, y, default, n, sbits, spread, blocks, intensity, tfChange, nextLowband2,
                        ref remainingBits, lm, default, nextLevel, ref seed, gain * side, default, fill >> blocks) << shift;
                }
                else
                {
                    cm = QuantBand(ref dec, i, y, default, n, sbits, spread, blocks, intensity, tfChange, nextLowband2,
                        ref remainingBits, lm, default, nextLevel, ref seed, gain * side, default, fill >> blocks) << shift;
                    rebalance = sbits - (rebalance - remainingBits);
                    if (rebalance > 3 << Bitres && itheta != 16384) mbits += rebalance - (3 << Bitres);
                    cm |= QuantBand(ref dec, i, x, default, n, mbits, spread, blocks, intensity, tfChange, lowband,
                        ref remainingBits, lm, nextLowbandOut1, nextLevel, ref seed, stereo ? 1f : gain * mid,
                        lowbandScratch, fill);
                }
            }
        }
        else
        {
            // This is the base case: a single PVQ codebook.
            int q = Bits2Pulses(i, lm, b);
            int currBits = Pulses2Bits(i, lm, q);
            remainingBits -= currBits;
            // Stay within the bit budget by lowering the pulse count if needed.
            while (remainingBits < 0 && q > 0)
            {
                remainingBits += currBits;
                q--;
                currBits = Pulses2Bits(i, lm, q);
                remainingBits -= currBits;
            }
            if (q != 0)
            {
                int k = GetPulses(q);
                cm = AlgUnquant(x, n, k, spread, blocks, ref dec, gain);
            }
            else
            {
                // No pulses: fill the band with noise or folded content.
                uint cmMask = (1u << blocks) - 1;
                fill &= (int)cmMask;
                if (fill == 0)
                {
                    for (int j = 0; j < n; j++) x[j] = 0;
                }
                else
                {
                    if (lowband.IsEmpty)
                    {
                        for (int j = 0; j < n; j++)
                        {
                            seed = LcgRand(seed);
                            x[j] = (int)seed >> 20;
                        }
                        cm = cmMask;
                    }
                    else
                    {
                        for (int j = 0; j < n; j++)
                        {
                            seed = LcgRand(seed);
                            float tmp = 1f / 256;
                            tmp = (seed & 0x8000) != 0 ? tmp : -tmp;
                            x[j] = lowband[j] + tmp;
                        }
                        cm = (uint)fill;
                    }
                    RenormaliseVector(x.Slice(0, n), gain);
                }
            }
        }

        if (stereo)
        {
            if (n != 2) StereoMerge(x, y, mid, n);
            if (inv != 0)
                for (int j = 0; j < n; j++) y[j] = -y[j];
        }
        else if (level == 0)
        {
            if (b0 > 1) InterleaveHadamard(x, nB >> recombine, b0 << recombine, longBlocks);
            nB = nB0;
            blocks = b0;
            for (int k = 0; k < timeDivide; k++)
            {
                blocks >>= 1;
                nB <<= 1;
                cm |= cm >> blocks;
                Haar1(x, nB, blocks);
            }
            for (int k = 0; k < recombine; k++)
            {
                cm = CeltTables.BitDeinterleaveTable[cm & 0xF];
                Haar1(x, n0 >> k, 1 << k);
            }
            blocks <<= recombine;
            // Scale output for later folding
            if (!lowbandOut.IsEmpty)
            {
                float nn = (float)Math.Sqrt(n0);
                for (int j = 0; j < n0; j++) lowbandOut[j] = nn * x[j];
            }
            cm &= (1u << blocks) - 1;
        }
        return cm;
    }
}
