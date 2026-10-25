namespace OneAudio.Opus;

// Energy and band quantisation of the CELT encoder (RFC 6716 sections 5.3.2, 5.3.5 - 5.3.8). Ported from the
// reference encoder in RFC 6716 (Simplified BSD licence, see THIRD-PARTY-NOTICES.md). The encoder never resynthesises,
// so the folding, collapse-mask and gain bookkeeping that only feeds the decoder is not carried here.
internal sealed partial class CeltEncoder
{
    // ------------------------------------------------------------------------------------------------
    // Energy
    // ------------------------------------------------------------------------------------------------

    private static float LossDistortion(float[] eBands, float[] oldEBands, int start, int end, int len, int c)
    {
        float dist = 0;
        for (int cc = 0; cc < c; cc++)
            for (int i = start; i < end; i++)
            {
                float d = eBands[i + cc * len] - oldEBands[i + cc * len];
                dist += d * d;
            }
        return Math.Min(200f, dist);
    }

    private static int QuantCoarseEnergyImpl(int start, int end, float[] eBands, float[] oldEBands, int budget, int tell,
        int probBase, float[] error, ref RangeEncoder enc, int c, int lm, int intra, float maxDecay)
    {
        int badness = 0;
        Span<float> prev = stackalloc float[2];
        float coef, beta;
        if (tell + 3 <= budget) enc.BitLogP(intra, 3);
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
        var probModel = CeltTables.EProbModel;
        for (int i = start; i < end; i++)
        {
            for (int cc = 0; cc < c; cc++)
            {
                float x = eBands[i + cc * NbEBands];
                float oldE = Math.Max(-9f, oldEBands[i + cc * NbEBands]);
                float f = x - coef * oldE - prev[cc];
                int qi = (int)Math.Floor(.5f + f);
                float decayBound = Math.Max(-28f, oldEBands[i + cc * NbEBands]) - maxDecay;
                // Prevent the energy from going down too quickly (e.g. for bands that have just one bin).
                if (qi < 0 && x < decayBound)
                {
                    qi += (int)(decayBound - x);
                    if (qi > 0) qi = 0;
                }
                int qi0 = qi;
                // If there are not enough bits to code all the energies, assume something safe.
                tell = enc.Tell();
                int bitsLeft = budget - tell - 3 * c * (end - i);
                if (i != start && bitsLeft < 30)
                {
                    if (bitsLeft < 24) qi = Math.Min(1, qi);
                    if (bitsLeft < 16) qi = Math.Max(-1, qi);
                }
                if (budget - tell >= 15)
                {
                    int pi = 2 * Math.Min(i, 20);
                    qi = enc.LaplaceEncode(qi, (uint)probModel[probBase + pi] << 7, probModel[probBase + pi + 1] << 6);
                }
                else if (budget - tell >= 2)
                {
                    qi = Math.Max(-1, Math.Min(qi, 1));
                    enc.ICdf(2 * qi ^ -(qi < 0 ? 1 : 0), CeltTables.SmallEnergyICdf, 2);
                }
                else if (budget - tell >= 1)
                {
                    qi = Math.Min(0, qi);
                    enc.BitLogP(-qi, 1);
                }
                else qi = -1;
                error[i + cc * NbEBands] = f - qi;
                badness += Math.Abs(qi0 - qi);
                float q = qi;
                float tmp = coef * oldE + prev[cc] + q;
                oldEBands[i + cc * NbEBands] = tmp;
                prev[cc] = prev[cc] + q - beta * q;
            }
        }
        return badness;
    }

    private void QuantCoarseEnergy(int start, int end, int effEnd, uint budget, ref RangeEncoder enc, int c, int lm,
        int nbAvailableBytes, bool twoPass)
    {
        var eBands = _bandLogE;
        var oldEBands = _oldBandE;
        var error = _error;
        bool intra = ForceIntra || (!twoPass && _delayedIntra > 2 * c * (end - start) && nbAvailableBytes > (end - start) * c);
        int intraBias = (int)(budget * _delayedIntra * LossRate / (c * 512));
        float newDistortion = LossDistortion(eBands, oldEBands, start, effEnd, NbEBands, c);
        uint tell = (uint)enc.Tell();
        if (tell + 3 > budget) twoPass = intra = false;
        float maxDecay = Math.Min(16f, .125f * nbAvailableBytes);
        var encStart = enc;
        var oldIntra = _oldEBandsIntra;
        var errIntra = _errorIntra;
        Array.Copy(oldEBands, oldIntra, c * NbEBands);
        int badness1 = 0;
        if (twoPass || intra)
            badness1 = QuantCoarseEnergyImpl(start, end, eBands, oldIntra, (int)budget, (int)tell, (lm * 2 + 1) * 42,
                errIntra, ref enc, c, lm, 1, maxDecay);
        if (!intra)
        {
            int tellIntra = enc.TellFrac();
            var encIntra = enc;
            int nstart = encStart.Offs;
            int nintra = encIntra.Offs;
            var buf = encIntra.Buffer;
            buf.Slice(nstart, nintra - nstart).CopyTo(_intraBits);
            enc = encStart;
            int badness2 = QuantCoarseEnergyImpl(start, end, eBands, oldEBands, (int)budget, (int)tell, (lm * 2) * 42,
                error, ref enc, c, lm, 0, maxDecay);
            if (twoPass && (badness1 < badness2 || (badness1 == badness2 && enc.TellFrac() + intraBias > tellIntra)))
            {
                enc = encIntra;
                _intraBits.AsSpan(0, nintra - nstart).CopyTo(buf.Slice(nstart, nintra - nstart));
                Array.Copy(oldIntra, oldEBands, c * NbEBands);
                Array.Copy(errIntra, error, c * NbEBands);
                intra = true;
            }
        }
        else
        {
            Array.Copy(oldIntra, oldEBands, c * NbEBands);
            Array.Copy(errIntra, error, c * NbEBands);
        }
        if (intra) _delayedIntra = newDistortion;
        else _delayedIntra = CeltTables.PredCoef[lm] * CeltTables.PredCoef[lm] * _delayedIntra + newDistortion;
    }

    private void QuantFineEnergy(int start, int end, ref RangeEncoder enc, int c)
    {
        for (int i = start; i < end; i++)
        {
            int fq = _fineQuant[i];
            int frac = 1 << fq;
            if (fq <= 0) continue;
            for (int cc = 0; cc < c; cc++)
            {
                int q2 = (int)Math.Floor((_error[i + cc * NbEBands] + .5f) * frac);
                if (q2 > frac - 1) q2 = frac - 1;
                if (q2 < 0) q2 = 0;
                enc.Bits((uint)q2, fq);
                float offset = (q2 + .5f) * (1 << (14 - fq)) * (1f / 16384) - .5f;
                _oldBandE[i + cc * NbEBands] += offset;
                _error[i + cc * NbEBands] -= offset;
            }
        }
    }

    private void QuantEnergyFinalise(int start, int end, int bitsLeft, ref RangeEncoder enc, int c)
    {
        for (int prio = 0; prio < 2; prio++)
        {
            for (int i = start; i < end && bitsLeft >= c; i++)
            {
                if (_fineQuant[i] >= 8 || _finePriority[i] != prio) continue;
                for (int cc = 0; cc < c; cc++)
                {
                    int q2 = _error[i + cc * NbEBands] < 0 ? 0 : 1;
                    enc.Bits((uint)q2, 1);
                    float offset = (q2 - .5f) * (1 << (14 - _fineQuant[i] - 1)) * (1f / 16384);
                    _oldBandE[i + cc * NbEBands] += offset;
                    bitsLeft--;
                }
            }
        }
    }

    // ------------------------------------------------------------------------------------------------
    // Shape quantisation
    // ------------------------------------------------------------------------------------------------

    private void QuantAllBands(int start, int end, int c, int n0, int dualStereo, int intensity, bool shortBlocks,
        int spread, int totalBits, int balance, ref RangeEncoder enc, int lm, int codedBands)
    {
        var eb = CeltTables.EBands5ms;
        int m = 1 << lm;
        int b = shortBlocks ? m : 1;
        for (int i = start; i < end; i++)
        {
            int n = m * eb[i + 1] - m * eb[i];
            int tell = enc.TellFrac();
            if (i != start) balance -= tell;
            int remainingBits = totalBits - tell - 1;
            int bits;
            if (i <= codedBands - 1)
            {
                int currBalance = balance / Math.Min(3, codedBands - i);
                bits = Math.Max(0, Math.Min(16383, Math.Min(remainingBits + 1, _pulses[i] + currBalance)));
            }
            else bits = 0;
            int tfChange = _tfRes[i];
            if (dualStereo != 0 && i == intensity) dualStereo = 0;
            var xs = _x.AsSpan(m * eb[i], n);
            if (dualStereo != 0)
            {
                QuantBand(i, xs, default, n, bits / 2, spread, b, intensity, tfChange, ref remainingBits, lm, 0, ref enc);
                QuantBand(i, _x.AsSpan(n0 + m * eb[i], n), default, n, bits / 2, spread, b, intensity, tfChange,
                    ref remainingBits, lm, 0, ref enc);
            }
            else
            {
                var ys = c == 2 ? _x.AsSpan(n0 + m * eb[i], n) : default;
                QuantBand(i, xs, ys, n, bits, spread, b, intensity, tfChange, ref remainingBits, lm, 0, ref enc);
            }
            balance += _pulses[i] + tell;
        }
    }

    private static int StereoItheta(ReadOnlySpan<float> x, ReadOnlySpan<float> y, bool stereo, int n)
    {
        float emid = 1e-15f, eside = 1e-15f;
        if (stereo)
        {
            for (int i = 0; i < n; i++)
            {
                float mm = x[i] + y[i];
                float s = x[i] - y[i];
                emid += mm * mm;
                eside += s * s;
            }
        }
        else
        {
            for (int i = 0; i < n; i++)
            {
                emid += x[i] * x[i];
                eside += y[i] * y[i];
            }
        }
        float mid = MathF.Sqrt(emid);
        float side = MathF.Sqrt(eside);
        return (int)Math.Floor(.5f + (double)(16384 * 0.63662f) * Math.Atan2(side, mid));
    }

    private void IntensityStereo(Span<float> x, ReadOnlySpan<float> y, int bandId, int n)
    {
        float left = _bandE[bandId], right = _bandE[bandId + NbEBands];
        float norm = 1e-15f + MathF.Sqrt(1e-15f + left * left + right * right);
        float a1 = left / norm, a2 = right / norm;
        for (int j = 0; j < n; j++)
        {
            float l = x[j], r = y[j];
            x[j] = a1 * l + a2 * r;
        }
    }

    private static void StereoSplit(Span<float> x, Span<float> y, int n)
    {
        for (int j = 0; j < n; j++)
        {
            float l = .70710678f * x[j];
            float r = .70710678f * y[j];
            x[j] = l + r;
            y[j] = r - l;
        }
    }

    private void DeinterleaveHadamard(Span<float> x, int n0, int stride, bool hadamard)
    {
        int n = n0 * stride;
        var tmp = _hadTmp;
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

    // Encodes one band, splitting it in two (and recursing) where the bit budget allows. Recursion depth is bounded
    // by the number of halvings of a band (at most 3), not by input data.
    private void QuantBand(int i, Span<float> x, Span<float> y, int n, int b, int spread, int bb, int intensity,
        int tfChange, ref int remainingBits, int lm, int level, ref RangeEncoder enc)
    {
        int n0 = n;
        int nB = n;
        int b0 = bb;
        int timeDivide = 0;
        int recombine = 0;
        int inv = 0;
        bool longBlocks = b0 == 1;
        nB /= bb;
        bool stereo = !y.IsEmpty;
        bool split = stereo;

        if (n == 1)
        {
            var cur = x;
            for (int c = 0; c < 1 + (stereo ? 1 : 0); c++)
            {
                int sign = 0;
                if (remainingBits >= 1 << Bitres)
                {
                    sign = cur[0] < 0 ? 1 : 0;
                    enc.Bits((uint)sign, 1);
                    remainingBits -= 1 << Bitres;
                    b -= 1 << Bitres;
                }
                cur = y;
            }
            return;
        }

        if (!stereo && level == 0)
        {
            if (tfChange > 0) recombine = tfChange;
            for (int k = 0; k < recombine; k++)
                CeltDecoder.Haar1(x, n >> k, 1 << k);
            bb >>= recombine;
            nB <<= recombine;
            // Increasing the time resolution
            while ((nB & 1) == 0 && tfChange < 0)
            {
                CeltDecoder.Haar1(x, nB, bb);
                bb <<= 1;
                nB >>= 1;
                timeDivide++;
                tfChange++;
            }
            b0 = bb;
            if (b0 > 1)
                DeinterleaveHadamard(x, nB >> recombine, b0 << recombine, longBlocks);
        }

        int cacheOff = CeltTables.CacheIndex[(lm + 1) * NbEBands + i];
        if (!stereo && lm != -1 && b > CeltTables.CacheBits[cacheOff + CeltTables.CacheBits[cacheOff]] + 12 && n > 2)
        {
            n >>= 1;
            y = x.Slice(n);
            split = true;
            lm -= 1;
            bb = (bb + 1) >> 1;
        }

        if (split)
        {
            int itheta;
            int pulseCap = CeltTables.LogN[i] + lm * (1 << Bitres);
            int offset = (pulseCap >> 1) - (stereo && n == 2 ? 16 : 4);
            int qn = CeltDecoder.ComputeQn(n, b, offset, pulseCap, stereo);
            if (stereo && i >= intensity) qn = 1;
            // theta is the atan() of the ratio between the (normalised) side and mid.
            itheta = StereoItheta(x, y, stereo, n);
            int tell = enc.TellFrac();
            if (qn != 1)
            {
                itheta = (itheta * qn + 8192) >> 14;
                if (stereo && n > 2)
                {
                    int p0 = 3;
                    int xx = itheta;
                    int x0 = qn / 2;
                    int ft = p0 * (x0 + 1) + x0;
                    enc.Encode((uint)(xx <= x0 ? p0 * xx : (xx - 1 - x0) + (x0 + 1) * p0),
                        (uint)(xx <= x0 ? p0 * (xx + 1) : (xx - x0) + (x0 + 1) * p0), (uint)ft);
                }
                else if (b0 > 1 || stereo)
                {
                    enc.EncodeUInt((uint)itheta, (uint)(qn + 1));
                }
                else
                {
                    int ft = ((qn >> 1) + 1) * ((qn >> 1) + 1);
                    int fs = itheta <= (qn >> 1) ? itheta + 1 : qn + 1 - itheta;
                    int fl = itheta <= (qn >> 1) ? itheta * (itheta + 1) >> 1
                        : ft - ((qn + 1 - itheta) * (qn + 2 - itheta) >> 1);
                    enc.Encode((uint)fl, (uint)(fl + fs), (uint)ft);
                }
                itheta = itheta * 16384 / qn;
                if (stereo)
                {
                    if (itheta == 0) IntensityStereo(x, y, i, n);
                    else StereoSplit(x, y, n);
                }
            }
            else if (stereo)
            {
                inv = itheta > 8192 ? 1 : 0;
                if (inv != 0)
                    for (int j = 0; j < n; j++) y[j] = -y[j];
                IntensityStereo(x, y, i, n);
                if (b > 2 << Bitres && remainingBits > 2 << Bitres) enc.BitLogP(inv, 2);
                else inv = 0;
                itheta = 0;
            }
            int qalloc = enc.TellFrac() - tell;
            b -= qalloc;
            int delta;
            if (itheta == 0) delta = -16384;
            else if (itheta == 16384) delta = 16384;
            else
            {
                int imid = CeltDecoder.BitexactCos(itheta);
                int iside = CeltDecoder.BitexactCos(16384 - itheta);
                // The mid vs side allocation that minimises squared error in the band.
                delta = CeltDecoder.FracMul16((n - 1) << 7, CeltDecoder.BitexactLog2Tan(iside, imid));
            }

            int mbits, sbits;
            if (n == 2 && stereo)
            {
                // With N=2 mid and side are orthogonal, so the side needs only one sign bit.
                mbits = b;
                sbits = 0;
                if (itheta != 0 && itheta != 16384) sbits = 1 << Bitres;
                mbits -= sbits;
                bool c = itheta > 8192;
                remainingBits -= qalloc + sbits;
                var x2 = c ? y : x;
                var y2 = c ? x : y;
                if (sbits != 0)
                {
                    int sign = x2[0] * y2[1] - x2[1] * y2[0] < 0 ? 1 : 0;
                    enc.Bits((uint)sign, 1);
                }
                QuantBand(i, x2, default, n, mbits, spread, bb, intensity, tfChange, ref remainingBits, lm, level, ref enc);
            }
            else
            {
                if (b0 > 1 && !stereo && (itheta & 0x3fff) != 0)
                {
                    if (itheta > 8192) delta -= delta >> (4 - lm);
                    else delta = Math.Min(0, delta + (n << Bitres >> (5 - lm)));
                }
                mbits = Math.Max(0, Math.Min(b, (b - delta) / 2));
                sbits = b - mbits;
                remainingBits -= qalloc;
                int nextLevel = stereo ? 0 : level + 1;
                int rebalance = remainingBits;
                if (mbits >= sbits)
                {
                    QuantBand(i, x, default, n, mbits, spread, bb, intensity, tfChange, ref remainingBits, lm, nextLevel, ref enc);
                    rebalance = mbits - (rebalance - remainingBits);
                    if (rebalance > 3 << Bitres && itheta != 0) sbits += rebalance - (3 << Bitres);
                    QuantBand(i, y, default, n, sbits, spread, bb, intensity, tfChange, ref remainingBits, lm, nextLevel, ref enc);
                }
                else
                {
                    QuantBand(i, y, default, n, sbits, spread, bb, intensity, tfChange, ref remainingBits, lm, nextLevel, ref enc);
                    rebalance = sbits - (rebalance - remainingBits);
                    if (rebalance > 3 << Bitres && itheta != 16384) mbits += rebalance - (3 << Bitres);
                    QuantBand(i, x, default, n, mbits, spread, bb, intensity, tfChange, ref remainingBits, lm, nextLevel, ref enc);
                }
            }
        }
        else
        {
            int q = CeltDecoder.Bits2Pulses(i, lm, b);
            int currBits = CeltDecoder.Pulses2Bits(i, lm, q);
            remainingBits -= currBits;
            while (remainingBits < 0 && q > 0)
            {
                remainingBits += currBits;
                q--;
                currBits = CeltDecoder.Pulses2Bits(i, lm, q);
                remainingBits -= currBits;
            }
            if (q != 0)
            {
                int k = CeltDecoder.GetPulses(q);
                AlgQuant(x, n, k, spread, bb, ref enc);
            }
        }
    }

    private void AlgQuant(Span<float> x, int n, int k, int spread, int bb, ref RangeEncoder enc)
    {
        var y = _qY;
        var iy = _qIy;
        var signx = _qSign;
        CeltDecoder.ExpRotation(x, n, 1, bb, k, spread);
        float sum = 0;
        for (int j = 0; j < n; j++)
        {
            if (x[j] > 0) signx[j] = 1;
            else
            {
                signx[j] = -1;
                x[j] = -x[j];
            }
            iy[j] = 0;
            y[j] = 0;
        }
        float xy = 0, yy = 0;
        int pulsesLeft = k;
        if (k > (n >> 1))
        {
            for (int j = 0; j < n; j++) sum += x[j];
            // Prevents infinities and NaNs from allocating too many pulses.
            if (!(sum > 1e-15f && sum < 64))
            {
                x[0] = 1f;
                for (int j = 1; j < n; j++) x[j] = 0;
                sum = 1f;
            }
            float rcp = (k - 1) * (1f / sum);
            for (int j = 0; j < n; j++)
            {
                iy[j] = (int)Math.Floor(rcp * x[j]);
                y[j] = iy[j];
                yy += y[j] * y[j];
                xy += x[j] * y[j];
                y[j] *= 2;
                pulsesLeft -= iy[j];
            }
        }
        // This should never happen, but just in case (e.g. on silence) put the pulses in the first bin.
        if (pulsesLeft > n + 3)
        {
            float tmp = pulsesLeft;
            yy += tmp * tmp;
            yy += tmp * y[0];
            iy[0] += pulsesLeft;
            pulsesLeft = 0;
        }
        float s = 1;
        for (int i = 0; i < pulsesLeft; i++)
        {
            int bestId = 0;
            float bestNum = -1e15f;
            float bestDen = 0;
            // The squared magnitude term gets added anyway, so add it outside the loop.
            yy = yy + 1;
            for (int j = 0; j < n; j++)
            {
                float rxy = xy + x[j];
                float ryy = yy + y[j];
                // Approximate score: maximise Rxy / sqrt(Ryy); Rxy is positive because the sign is pre-computed.
                rxy = rxy * rxy;
                if (bestDen * rxy > ryy * bestNum)
                {
                    bestDen = ryy;
                    bestNum = rxy;
                    bestId = j;
                }
            }
            xy = xy + x[bestId];
            yy = yy + y[bestId];
            y[bestId] += 2 * s;
            iy[bestId]++;
        }
        for (int j = 0; j < n; j++)
        {
            x[j] = signx[j] * x[j];
            if (signx[j] < 0) iy[j] = -iy[j];
        }
        EncodePulses(iy, n, k, ref enc);
    }

    // Index of a pulse vector among all vectors of N dimensions and K pulses (RFC 6716 section 4.3.4.2).
    private void EncodePulses(int[] y, int n, int k, ref RangeEncoder enc)
    {
        var u = _cwrsU;
        u[0] = 0;
        for (int kk = 1; kk <= k + 1; kk++) u[kk] = (uint)((kk << 1) - 1);
        int j = n - 1;
        int kc = Math.Abs(y[j]);
        uint idx = y[j] < 0 ? 1u : 0u;
        j = n - 2;
        idx += u[kc];
        kc += Math.Abs(y[j]);
        if (y[j] < 0) idx += u[kc + 1];
        while (j-- > 0)
        {
            CeltDecoder.Unext(u, 0, k + 2, 0);
            idx += u[kc];
            kc += Math.Abs(y[j]);
            if (y[j] < 0) idx += u[kc + 1];
        }
        uint nc = u[kc] + u[kc + 1];
        enc.EncodeUInt(idx, nc);
    }
}
