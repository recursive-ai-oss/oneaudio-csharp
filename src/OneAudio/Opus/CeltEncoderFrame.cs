namespace OneAudio.Opus;

// Frame encoding of the CELT layer (celt_encode_with_ec in the RFC 6716 reference encoder).
internal sealed partial class CeltEncoder
{
    /// <summary>
    /// Encodes one frame. <paramref name="pcm"/> is interleaved float in [-1, 1] with the encoder's channel count.
    /// When <paramref name="haveEnc"/> is set the frame is appended to the caller's range coder (hybrid mode);
    /// otherwise a coder over <paramref name="compressed"/> is used.
    /// </summary>
    /// <returns>Bytes used, or a negative value on error.</returns>
    public int Encode(ReadOnlySpan<float> pcm, int frameSize, Span<byte> compressed, int nbCompressedBytes,
        ref RangeEncoder encArg, bool haveEnc)
    {
        int cc = _cc;
        int c = _streamChannels;
        if (nbCompressedBytes < 2) return -1;
        int lm;
        for (lm = 0; lm <= CeltTables.MaxLM; lm++)
            if (ShortMdct << lm == frameSize) break;
        if (lm > CeltTables.MaxLM) return -1;
        int m = 1 << lm;
        int n = m * ShortMdct;
        var eb = CeltTables.EBands5ms;

        ref RangeEncoder enc = ref encArg;
        int tell, nbFilledBytes;
        if (!haveEnc)
        {
            tell = 1;
            nbFilledBytes = 0;
        }
        else
        {
            tell = enc.Tell();
            nbFilledBytes = (tell + 4) >> 3;
        }

        nbCompressedBytes = Math.Min(nbCompressedBytes, 1275);
        int nbAvailableBytes = nbCompressedBytes - nbFilledBytes;
        int vbrRate, effectiveBytes;
        if (Vbr && Bitrate != -1)
        {
            int den = 48000 >> Bitres;
            vbrRate = (Bitrate * frameSize + (den >> 1)) / den;
            effectiveBytes = vbrRate >> (3 + Bitres);
        }
        else
        {
            vbrRate = 0;
            int tmp = Bitrate * frameSize;
            if (tell > 1) tmp += tell;
            if (Bitrate != -1)
                nbCompressedBytes = Math.Max(2, Math.Min(nbCompressedBytes, (tmp + 4 * 48000) / (8 * 48000)));
            effectiveBytes = nbCompressedBytes;
        }
        if (!haveEnc)
        {
            enc = new RangeEncoder(compressed, nbCompressedBytes);
        }

        if (vbrRate > 0 && ConstrainedVbr)
        {
            // The maximum rate allowed in VBR mode, so the target rate and buffering are not violated.
            int vbrBound = vbrRate;
            int maxAllowed = Math.Min(Math.Max(tell == 1 ? 2 : 0, (vbrRate + vbrBound - _vbrReservoir) >> (Bitres + 3)),
                nbAvailableBytes);
            if (maxAllowed < nbAvailableBytes)
            {
                nbCompressedBytes = nbFilledBytes + maxAllowed;
                nbAvailableBytes = maxAllowed;
                enc.Shrink(nbCompressedBytes);
            }
        }
        int totalBits = nbCompressedBytes * 8;
        int effEnd = Math.Min(_end, NbEBands);

        // Pre-emphasis; the input is scaled to the 16-bit range the codec's thresholds are tuned for.
        int inStride = n + Overlap;
        int preStride = n + CombMaxPeriod;
        _preStride = preStride;
        var inp = _in;
        var pre = _pre;
        bool silence = true;
        for (int ch = 0; ch < cc; ch++)
        {
            int io = ch * inStride + Overlap;
            for (int i = 0; i < n; i++)
            {
                float x = pcm[ch + i * cc] * 32768f;
                if (float.IsNaN(x)) x = 0;
                if (Clip) x = Math.Max(-65536f, Math.Min(65536f, x));
                float tmp = 1f * x;
                float v = tmp + _preemphMemE[ch];
                inp[io + i] = v;
                _preemphMemE[ch] = 0f * v - 0.85000610f * tmp;
                silence = silence && v == 0;
            }
            Array.Copy(_prefilterMem, ch * CombMaxPeriod, pre, ch * preStride, CombMaxPeriod);
            Array.Copy(inp, io, pre, ch * preStride + CombMaxPeriod, n);
        }
        if (tell == 1) enc.BitLogP(silence ? 1 : 0, 15);
        else silence = false;
        if (silence)
        {
            if (vbrRate > 0)
            {
                effectiveBytes = nbCompressedBytes = Math.Min(nbCompressedBytes, nbFilledBytes + 2);
                totalBits = nbCompressedBytes * 8;
                nbAvailableBytes = 2;
                enc.Shrink(nbCompressedBytes);
            }
            // Pretend all the remaining bits were filled with zeros, which is what the initialiser did anyway.
            tell = nbCompressedBytes * 8;
            enc.AddTotalBits(tell - enc.Tell());
        }

        // Pitch pre-filter (comb filter) analysis, RFC 6716 section 5.3.1.
        int pitchIndex = CombMinPeriod;
        float gain1 = 0;
        int prefilterTapset = 0;
        bool pfOn;
        if (nbAvailableBytes > 12 * c && _start == 0 && !silence && !DisablePrefilter && Complexity >= 5)
        {
            PitchDownsample(CombMaxPeriod + n, cc);
            pitchIndex = PitchSearch(_pitchBuf, CombMaxPeriod >> 1, _pitchBuf, n, CombMaxPeriod - CombMinPeriod);
            pitchIndex = CombMaxPeriod - pitchIndex;
            gain1 = RemoveDoubling(_pitchBuf, CombMaxPeriod, CombMinPeriod, n, ref pitchIndex, _prefilterPeriod,
                _prefilterGain);
            if (pitchIndex > CombMaxPeriod - 2) pitchIndex = CombMaxPeriod - 2;
            gain1 = .7f * gain1;
            if (LossRate > 2) gain1 = .5f * gain1;
            if (LossRate > 4) gain1 = .5f * gain1;
            if (LossRate > 8) gain1 = 0;
            prefilterTapset = _tapsetDecision;
        }
        else gain1 = 0;
        float pfThreshold = .2f;
        if (Math.Abs(pitchIndex - _prefilterPeriod) * 10 > pitchIndex) pfThreshold += .2f;
        if (nbAvailableBytes < 25) pfThreshold += .1f;
        if (nbAvailableBytes < 35) pfThreshold += .1f;
        if (_prefilterGain > .4f) pfThreshold -= .1f;
        if (_prefilterGain > .55f) pfThreshold -= .1f;
        pfThreshold = Math.Max(pfThreshold, .2f);
        if (gain1 < pfThreshold)
        {
            if (_start == 0 && tell + 16 <= totalBits) enc.BitLogP(0, 1);
            gain1 = 0;
            pfOn = false;
        }
        else
        {
            if (Math.Abs(gain1 - _prefilterGain) < .1f) gain1 = _prefilterGain;
            int qg = (int)Math.Floor(.5f + gain1 * 32 / 3) - 1;
            qg = Math.Max(0, Math.Min(7, qg));
            enc.BitLogP(1, 1);
            pitchIndex += 1;
            int octave = CeltDecoder.EcIlog((uint)pitchIndex) - 5;
            enc.EncodeUInt((uint)octave, 6);
            enc.Bits((uint)(pitchIndex - (16 << octave)), 4 + octave);
            pitchIndex -= 1;
            enc.Bits((uint)qg, 3);
            if (enc.Tell() + 2 <= totalBits) enc.ICdf(prefilterTapset, CeltTables.TapsetICdf, 2);
            else prefilterTapset = 0;
            gain1 = 0.09375f * (qg + 1);
            pfOn = true;
        }
        var window = CeltTables.Window;
        for (int ch = 0; ch < cc; ch++)
        {
            int offset = ShortMdct - Overlap;
            _prefilterPeriod = Math.Max(_prefilterPeriod, CombMinPeriod);
            Array.Copy(_inMem, ch * Overlap, inp, ch * inStride, Overlap);
            if (offset != 0)
                CeltDecoder.CombFilter(inp, ch * inStride + Overlap, pre, ch * preStride + CombMaxPeriod,
                    _prefilterPeriod, _prefilterPeriod, offset, -_prefilterGain, -_prefilterGain, _prefilterTapset,
                    _prefilterTapset, default, 0);
            CeltDecoder.CombFilter(inp, ch * inStride + Overlap + offset, pre, ch * preStride + CombMaxPeriod + offset,
                _prefilterPeriod, pitchIndex, n - offset, -_prefilterGain, -gain1, _prefilterTapset, prefilterTapset,
                window, Overlap);
            Array.Copy(inp, ch * inStride + n, _inMem, ch * Overlap, Overlap);
            if (n > CombMaxPeriod)
                Array.Copy(pre, ch * preStride + n, _prefilterMem, ch * CombMaxPeriod, CombMaxPeriod);
            else
            {
                Array.Copy(_prefilterMem, ch * CombMaxPeriod + n, _prefilterMem, ch * CombMaxPeriod, CombMaxPeriod - n);
                Array.Copy(pre, ch * preStride + CombMaxPeriod, _prefilterMem, ch * CombMaxPeriod + CombMaxPeriod - n, n);
            }
        }

        bool isTransient = false;
        int shortBlocks = 0;
        if (lm > 0 && enc.Tell() + 3 <= totalBits)
        {
            if (Complexity > 1)
            {
                isTransient = TransientAnalysis(inp, n + Overlap, cc, Overlap, _tmp, _bins);
                if (isTransient) shortBlocks = m;
            }
            enc.BitLogP(isTransient ? 1 : 0, 3);
        }

        ComputeMdcts(shortBlocks, cc, lm);
        if (cc == 2 && c == 1)
            for (int i = 0; i < n; i++)
                _freq[i] = .5f * _freq[i] + .5f * _freq[n + i];
        ComputeBandEnergies(effEnd, c, m);
        Amp2Log2(effEnd, _end, c);
        NormaliseBands(effEnd, c, m);

        int tfSelect = TfAnalysis(effEnd, c, isTransient, _tfRes, effectiveBytes, n, lm, out int tfSum);
        for (int i = effEnd; i < _end; i++) _tfRes[i] = _tfRes[effEnd - 1];

        QuantCoarseEnergy(_start, _end, effEnd, (uint)totalBits, ref enc, c, lm, nbAvailableBytes, Complexity >= 4);
        TfEncode(_start, _end, isTransient ? 1 : 0, _tfRes, lm, tfSelect, ref enc);

        _spreadDecision = SpreadNormal;
        if (enc.Tell() + 4 <= totalBits)
        {
            if (shortBlocks != 0 || Complexity < 3 || nbAvailableBytes < 10 * c)
            {
                if (Complexity == 0) _spreadDecision = SpreadNone;
            }
            else
                _spreadDecision = SpreadingDecision(_spreadDecision, pfOn && shortBlocks == 0, effEnd, c, m);
            enc.ICdf(_spreadDecision, CeltTables.SpreadICdf, 5);
        }

        var cap = _cap;
        var offsets = _offsets;
        InitCaps(cap, lm, c);
        Array.Clear(offsets);
        if (effectiveBytes > 50 && lm >= 1)
        {
            int t1, t2;
            if (lm <= 1) { t1 = 3; t2 = 5; }
            else { t1 = 2; t2 = 4; }
            var bl = _bandLogE;
            for (int i = _start + 1; i < _end - 1; i++)
            {
                float d2 = 2 * bl[i] - bl[i - 1] - bl[i + 1];
                if (c == 2)
                    d2 = .5f * (d2 + 2 * bl[i + NbEBands] - bl[i - 1 + NbEBands] - bl[i + 1 + NbEBands]);
                if (d2 > t1) offsets[i] += 1;
                if (d2 > t2) offsets[i] += 1;
            }
        }
        int dynallocLogp = 6;
        totalBits <<= Bitres;
        int totalBoost = 0;
        tell = enc.TellFrac();
        for (int i = _start; i < _end; i++)
        {
            int width = c * (eb[i + 1] - eb[i]) << lm;
            // quanta is 6 bits, but no more than 1 bit/sample and no less than 1/8 bit/sample.
            int quanta = Math.Min(width << Bitres, Math.Max(6 << Bitres, width));
            int loopLogp = dynallocLogp;
            int boost = 0;
            int j;
            for (j = 0; tell + (loopLogp << Bitres) < totalBits - totalBoost && boost < cap[i]; j++)
            {
                int flag = j < offsets[i] ? 1 : 0;
                enc.BitLogP(flag, loopLogp);
                tell = enc.TellFrac();
                if (flag == 0) break;
                boost += quanta;
                totalBoost += quanta;
                loopLogp = 1;
            }
            if (j != 0) dynallocLogp = Math.Max(2, dynallocLogp - 1);
            offsets[i] = boost;
        }
        int allocTrim = 5;
        if (tell + (6 << Bitres) <= totalBits - totalBoost)
        {
            allocTrim = AllocTrimAnalysis(_end, lm, c, n);
            enc.ICdf(allocTrim, CeltTables.TrimICdf, 7);
            tell = enc.TellFrac();
        }

        if (vbrRate > 0)
        {
            int lmDiff = CeltTables.MaxLM - lm;
            int target = vbrRate + (_vbrOffset >> lmDiff) - ((40 * c + 20) << Bitres);
            // Short blocks get a large boost in bitrate, but since they are uncommon long blocks are not greatly affected.
            if (shortBlocks != 0 || tfSum < -2 * (_end - _start)) target = 7 * target / 4;
            else if (tfSum < -(_end - _start)) target = 3 * target / 2;
            else if (m > 1) target -= (target + 14) / 28;
            target += tell;
            // The frame must not shrink so far that the encoder runs out of bits; the 2-byte margin keeps the
            // decoder's bust-prevention logic from triggering.
            int minAllowed = ((tell + totalBoost + (1 << (Bitres + 3)) - 1) >> (Bitres + 3)) + 2 - nbFilledBytes;
            nbAvailableBytes = (target + (1 << (Bitres + 2))) >> (Bitres + 3);
            nbAvailableBytes = Math.Max(minAllowed, nbAvailableBytes);
            nbAvailableBytes = Math.Min(nbCompressedBytes, nbAvailableBytes + nbFilledBytes) - nbFilledBytes;
            int delta = target - vbrRate;
            target = nbAvailableBytes << (Bitres + 3);
            // A silent frame does not adjust the drift, but lets the reservoir refill.
            if (silence)
            {
                nbAvailableBytes = 2;
                target = 2 * 8 << Bitres;
                delta = 0;
            }
            float alpha;
            if (_vbrCount < 970)
            {
                _vbrCount++;
                alpha = 1f / (_vbrCount + 20);
            }
            else alpha = .001f;
            if (ConstrainedVbr) _vbrReservoir += target - vbrRate;
            _vbrDrift += (int)(alpha * ((delta * (1 << lmDiff)) - _vbrOffset - _vbrDrift));
            _vbrOffset = -_vbrDrift;
            if (ConstrainedVbr && _vbrReservoir < 0)
            {
                int adjust = -_vbrReservoir / (8 << Bitres);
                nbAvailableBytes += silence ? 0 : adjust;
                _vbrReservoir = 0;
            }
            nbCompressedBytes = Math.Min(nbCompressedBytes, nbAvailableBytes + nbFilledBytes);
            enc.Shrink(nbCompressedBytes);
        }

        int intensity = 0;
        int dualStereo = 0;
        if (c == 2)
        {
            if (lm != 0) dualStereo = StereoAnalysis(lm, n) ? 1 : 0;
            int effectiveRate = (8 * effectiveBytes - 80) >> lm;
            effectiveRate = 2 * effectiveRate / 5;
            if (effectiveRate < 35) intensity = 8;
            else if (effectiveRate < 50) intensity = 12;
            else if (effectiveRate < 68) intensity = 16;
            else if (effectiveRate < 84) intensity = 18;
            else if (effectiveRate < 102) intensity = 19;
            else if (effectiveRate < 130) intensity = 20;
            else intensity = 100;
            intensity = Math.Min(_end, Math.Max(_start, intensity));
        }

        int bits = (nbCompressedBytes * 8 << Bitres) - enc.TellFrac() - 1;
        int antiCollapseRsv = isTransient && lm >= 2 && bits >= (lm + 2) << Bitres ? 1 << Bitres : 0;
        bits -= antiCollapseRsv;
        int codedBands = ComputeAllocation(_start, _end, offsets, cap, allocTrim, ref intensity, ref dualStereo, bits,
            out int balance, _pulses, _fineQuant, _finePriority, c, lm, ref enc, _lastCodedBands);
        _lastCodedBands = codedBands;
        QuantFineEnergy(_start, _end, ref enc, c);

        QuantAllBands(_start, _end, c, n, dualStereo, intensity, shortBlocks != 0, _spreadDecision,
            nbCompressedBytes * (8 << Bitres) - antiCollapseRsv, balance, ref enc, lm, codedBands);
        bool antiCollapseOn = false;
        if (antiCollapseRsv > 0)
        {
            antiCollapseOn = _consecTransient < 2;
            enc.Bits(antiCollapseOn ? 1u : 0u, 1);
        }
        QuantEnergyFinalise(_start, _end, nbCompressedBytes * 8 - enc.Tell(), ref enc, c);
        if (silence)
            for (int i = 0; i < c * NbEBands; i++) _oldBandE[i] = -28f;

        _prefilterPeriod = pitchIndex;
        _prefilterGain = gain1;
        _prefilterTapset = prefilterTapset;
        if (cc == 2 && c == 1)
            for (int i = 0; i < NbEBands; i++) _oldBandE[NbEBands + i] = _oldBandE[i];
        if (!isTransient)
        {
            for (int i = 0; i < cc * NbEBands; i++) _oldLogE2[i] = _oldLogE[i];
            for (int i = 0; i < cc * NbEBands; i++) _oldLogE[i] = _oldBandE[i];
        }
        else
        {
            for (int i = 0; i < cc * NbEBands; i++) _oldLogE[i] = Math.Min(_oldLogE[i], _oldBandE[i]);
        }
        for (int ch = 0; ch < cc; ch++)
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
        if (isTransient) _consecTransient++;
        else _consecTransient = 0;
        _rng = enc.Range;
        // Any room left (only at very high rates) is already zero.
        enc.Done();
        if (enc.Error) return -1;
        return nbCompressedBytes;
    }
}
