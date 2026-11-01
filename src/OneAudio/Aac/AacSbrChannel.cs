namespace OneAudio.Aac;

/// <summary>
/// High-frequency generation and adjustment for one SBR channel on complex QMF time slots (ISO/IEC 14496-3 4.6.18.6
/// and 4.6.18.7). The slot buffers hold six slots carried over from the previous frame followed by the 32 slots of the
/// current one; both stages work on the whole buffer, as the standard's delay model requires.
/// </summary>
internal sealed class SbrChannel
{
    internal const int Cols = 32, Overlap = 6, Slots = Cols + Overlap, Bands = 64;

    public readonly float[][] Re = NewBuffer();
    public readonly float[][] Im = NewBuffer();
    public readonly SbrQmfAnalysis Analysis = new();
    public readonly SbrQmfSynthesis Synthesis;

    // transposer state (4.6.18.6.2)
    private readonly float[][] _lpcRe = [new float[32], new float[32]];
    private readonly float[][] _lpcIm = [new float[32], new float[32]];
    private readonly float[] _bwOld = new float[SbrBands.MaxNoiseCoeffs];
    private readonly float[] _bw = new float[SbrBands.MaxNoiseCoeffs];
    private readonly float[] _lowRe = new float[Slots + 2];
    private readonly float[] _lowIm = new float[Slots + 2];

    // envelope adjuster state (4.6.18.7)
    private readonly int[] _harmFlagsPrev = new int[3];
    private int _harmIndex, _phaseIndex, _prevTranEnv = -1;
    private bool _startUp = true;
    private readonly float[] _filtGain = new float[SbrBands.MaxFreqCoeffs];
    private readonly float[] _filtNoise = new float[SbrBands.MaxFreqCoeffs];
    private readonly float[] _nrgRef = new float[SbrBands.MaxFreqCoeffs];
    private readonly float[] _nrgEst = new float[SbrBands.MaxFreqCoeffs];
    private readonly float[] _noiseLevel = new float[SbrBands.MaxFreqCoeffs];
    private readonly float[] _nrgGain = new float[SbrBands.MaxFreqCoeffs];
    private readonly float[] _nrgSine = new float[SbrBands.MaxFreqCoeffs];
    private readonly int[] _sineMapped = new int[SbrBands.MaxFreqCoeffs];

    private static readonly float[] LimGains = [0.5011932025f, 1.0f, 1.9952692516f, 1.0e20f];
    private static readonly float[] SmoothFilter = [0.66666666666666f, 0.36516383427084f, 0.14699433520835f, 0.03183050093751f];
    private static readonly float[] InvInt = BuildInvInt();
    private const float Eps = 1e-12f;

    private static float[] BuildInvInt()
    {
        var t = new float[64];
        t[0] = float.MaxValue;
        for (int i = 1; i < t.Length; i++) t[i] = 1.0f / i;
        return t;
    }

    public SbrChannel(int synthBands) => Synthesis = new SbrQmfSynthesis(synthBands);

    private static float[][] NewBuffer()
    {
        var b = new float[Slots][];
        for (int i = 0; i < Slots; i++) b[i] = new float[Bands];
        return b;
    }

    public void Reset()
    {
        Analysis.Reset();
        Synthesis.Reset();
        foreach (var r in Re) Array.Clear(r);
        foreach (var r in Im) Array.Clear(r);
        Array.Clear(_lpcRe[0]); Array.Clear(_lpcRe[1]); Array.Clear(_lpcIm[0]); Array.Clear(_lpcIm[1]);
        Array.Clear(_bwOld);
        Array.Clear(_harmFlagsPrev);
        _harmIndex = _phaseIndex = 0;
        _prevTranEnv = -1;
        _startUp = true;
        Array.Clear(_filtGain);
        Array.Clear(_filtNoise);
    }

    /// <summary>Restarts the envelope adjuster after a header with a new band layout (resetSbrEnvelopeCalc).</summary>
    public void ResetEnvelope()
    {
        _phaseIndex = 0;
        _startUp = true;
    }

    /// <summary>
    /// Clears carried-over slots and transposer history above the old crossover when the band layout changes
    /// (4.6.18.2.1 header reset): the old lower edge is <paramref name="oldLsb"/>, the new one <paramref name="newLsb"/>.
    /// </summary>
    public void ResetQmfRange(int oldLsb, int newLsb, int prevStopPos, bool first)
    {
        int startSlot = 2 * (prevStopPos - 16);
        for (int l = Math.Max(0, startSlot); l < Overlap; l++)
            for (int k = oldLsb; k < newLsb; k++) { Re[l][k] = 0; Im[l][k] = 0; }
        if (first)
        {
            int a = Math.Min(oldLsb, newLsb), b = Math.Max(oldLsb, newLsb);
            for (int l = a; l < b && l < 32; l++)
            {
                _lpcRe[0][l] = _lpcRe[1][l] = 0;
                _lpcIm[0][l] = _lpcIm[1][l] = 0;
            }
        }
    }

    public void ClearLpcState()
    {
        Array.Clear(_lpcRe[0]); Array.Clear(_lpcRe[1]); Array.Clear(_lpcIm[0]); Array.Clear(_lpcIm[1]);
    }

    /// <summary>Carries the last six slots of the buffer to its start (the QMF overlap).</summary>
    public void ShiftOverlap()
    {
        for (int i = 0; i < Overlap; i++)
        {
            Array.Copy(Re[i + Cols], Re[i], Bands);
            Array.Copy(Im[i + Cols], Im[i], Bands);
        }
    }

    // --- transposer ---------------------------------------------------------------------------------------------------

    private static float MapInvf(int mode, int prev) => mode switch
    {
        1 => prev == 0 ? 0.6f : 0.75f,
        2 => 0.90f,
        3 => 0.98f,
        _ => prev == 1 ? 0.6f : 0.0f,
    };

    /// <summary>4.6.18.6: patches low-band subband samples into the high band with second-order inverse filtering.</summary>
    public void Transpose(SbrBands b, SbrFrame f, SbrPrev prev)
    {
        int nInvf = b.NNoise;
        int startSample = f.Borders[0] * 2;
        int stopSample = Cols + (f.Borders[f.NEnvelopes] - 16) * 2;
        var bw = _bw;
        for (int i = 0; i < nInvf; i++)
        {
            bw[i] = MapInvf(f.InvfMode[i], prev.InvfMode[i]);
            float w1, w2;
            if (bw[i] < _bwOld[i]) { w1 = 0.75f; w2 = 0.25f; }
            else { w1 = 0.90625f; w2 = 0.09375f; }
            float accu = w1 * bw[i] + w2 * _bwOld[i];
            if (accu < 0.015625f) accu = 0;
            bw[i] = Math.Min(accu, 0.99609375f);
        }
        ref var last = ref b.Patches[b.NPatches - 1];
        int targetStopBand = last.TargetStartBand + last.NumBandsInPatch;
        for (int i = startSample; i < stopSample; i++)
            for (int j = targetStopBand; j < Bands; j++) { Re[i][j] = 0; Im[i][j] = 0; }

        Span<int> bwIndex = stackalloc int[SbrBands.MaxPatches];
        bwIndex.Clear();
        var lr = _lowRe;
        var li = _lowIm;
        for (int loBand = b.LbStart; loBand < b.LbStop; loBand++)
        {
            lr[0] = _lpcRe[0][loBand]; lr[1] = _lpcRe[1][loBand];
            li[0] = _lpcIm[0][loBand]; li[1] = _lpcIm[1][loBand];
            for (int i = 0; i < Slots; i++) { lr[2 + i] = Re[i][loBand]; li[2 + i] = Im[i][loBand]; }
            AutoCorrelate(lr, li, out float r11, out float r22, out float r01r, out float r01i, out float r12r, out float r12i,
                out float r02r, out float r02i, out float det);
            float a1r = 0, a1i = 0;
            if (det != 0.0f)
            {
                float fac = 1.0f / det;
                a1r = (r01r * r12r - r01i * r12i - r02r * r11) * fac;
                a1i = (r01i * r12r + r01r * r12i - r02i * r11) * fac;
            }
            float a0r = 0, a0i = 0;
            if (r11 != 0.0f)
            {
                float fac = 1.0f / r11;
                a0r = -(r01r + a1r * r12r + a1i * r12i) * fac;
                a0i = -(r01i + a1i * r12r - a1r * r12i) * fac;
            }
            if (a0r * a0r + a0i * a0i >= 16.0f || a1r * a1r + a1i * a1i >= 16.0f)
            {
                a0r = a1r = a0i = a1i = 0;
            }
            for (int patch = 0; patch < b.NPatches; patch++)
            {
                ref var pp = ref b.Patches[patch];
                int hiBand = loBand + pp.TargetBandOffs;
                if (loBand < pp.SourceStartBand || loBand >= pp.SourceStopBand) continue;
                if (hiBand >= Bands) continue;
                while (bwIndex[patch] < b.NNoise - 1 && hiBand >= b.BwBorders[bwIndex[patch]]) bwIndex[patch]++;
                float g = bw[bwIndex[patch]];
                float c0r = g * a0r, c0i = g * a0i;
                g *= g;
                float c1r = g * a1r, c1i = g * a1i;
                for (int i = startSample; i < stopSample; i++)
                {
                    float xr = lr[2 + i], xi = li[2 + i];
                    if (g > 0)
                    {
                        xr += c0r * lr[2 + i - 1] - c0i * li[2 + i - 1] + c1r * lr[2 + i - 2] - c1i * li[2 + i - 2];
                        xi += c0i * lr[2 + i - 1] + c0r * li[2 + i - 1] + c1i * lr[2 + i - 2] + c1r * li[2 + i - 2];
                    }
                    Re[i][hiBand] = xr;
                    Im[i][hiBand] = xi;
                }
            }
        }
        int tsb = b.Patches[0].TargetStartBand;
        for (int i = 0; i < 2; i++)
            for (int loBand = 0; loBand < tsb; loBand++)
            {
                _lpcRe[i][loBand] = Re[Cols - 2 + i][loBand];
                _lpcIm[i][loBand] = Im[Cols - 2 + i][loBand];
            }
        for (int i = 0; i < nInvf; i++) _bwOld[i] = bw[i];
    }

    // x points two entries into the arrays so x[-1] and x[-2] are the carried-over samples
    private static void AutoCorrelate(float[] re, float[] im, out float r11, out float r22, out float r01r, out float r01i,
        out float r12r, out float r12i, out float r02r, out float r02i, out float det)
    {
        const int o = 2;
        int len = Slots;
        float accu1 = 0, accu2;
        int j;
        for (j = 0; j < len - 1; j++) accu1 += re[o + j - 1] * re[o + j - 1] + im[o + j - 1] * im[o + j - 1];
        accu2 = re[o - 2] * re[o - 2] + im[o - 2] * im[o - 2];
        accu2 += accu1;
        accu1 += re[o + j - 1] * re[o + j - 1] + im[o + j - 1] * im[o + j - 1];
        r11 = accu1;
        r22 = accu2;
        accu1 = 0;
        for (j = 0; j < len - 1; j++) accu1 += re[o + j] * re[o + j - 1] + im[o + j] * im[o + j - 1];
        accu2 = re[o - 1] * re[o - 2] + im[o - 1] * im[o - 2];
        accu2 += accu1;
        accu1 += re[o + j] * re[o + j - 1] + im[o + j] * im[o + j - 1];
        r01r = accu1;
        r12r = accu2;
        accu1 = 0;
        for (j = 0; j < len - 1; j++) accu1 += im[o + j] * re[o + j - 1] - re[o + j] * im[o + j - 1];
        accu2 = im[o - 1] * re[o - 2] - re[o - 1] * im[o - 2];
        accu2 += accu1;
        accu1 += im[o + j] * re[o + j - 1] - re[o + j] * im[o + j - 1];
        r01i = accu1;
        r12i = accu2;
        accu1 = accu2 = 0;
        for (j = 0; j < len; j++)
        {
            accu1 += re[o + j] * re[o + j - 2] + im[o + j] * im[o + j - 2];
            accu2 += im[o + j] * re[o + j - 2] - re[o + j] * im[o + j - 2];
        }
        r02r = accu1;
        r02i = accu2;
        det = r11 * r22 - (r12r * r12r + r12i * r12i);
    }

    // --- envelope adjustment ------------------------------------------------------------------------------------------

    private void MapSineFlags(SbrBands b, SbrFrame f)
    {
        var hi = b.Table[1];
        int nSfb = b.NSfb[1];
        int lowSubband2 = 2 * hi[0];
        for (int i = 0; i < _sineMapped.Length; i++) _sineMapped[i] = SbrFrame.MaxEnvelopes;
        int bitcount = 0, oldflags = 0, newflags = 0, pos = 0;
        for (int i = nSfb - 1; i >= 0; i--)
        {
            int li = hi[i], ui = hi[i + 1];
            int mask = 1 << bitcount;
            if (bitcount == 0)
            {
                oldflags = _harmFlagsPrev[pos];
                newflags = 0;
            }
            if (f.AddHarmonics[i] != 0)
            {
                newflags |= mask;
                int idx = (ui + li - lowSubband2) >> 1;
                if ((uint)idx < (uint)_sineMapped.Length) _sineMapped[idx] = (oldflags & mask) != 0 ? 0 : f.TranEnv;
            }
            if (++bitcount == 16 || i == 0)
            {
                bitcount = 0;
                _harmFlagsPrev[pos++] = newflags;
            }
        }
    }

    /// <summary>4.6.18.7: scales the generated high band to the transmitted envelope and adds noise and sinusoids.</summary>
    public void Adjust(SbrBands b, SbrHeader h, SbrFrame f)
    {
        int nEnv = f.NEnvelopes;
        var nrgRef = _nrgRef; var nrgEst = _nrgEst; var noiseLevel = _noiseLevel; var nrgGain = _nrgGain; var nrgSine = _nrgSine;
        int lowSubband = b.LowSubband, highSubband = b.HighSubband, noSubbands = highSubband - lowSubband;
        int noNoiseBands = b.NNoise;
        float limiterGain = LimGains[h.LimiterGains];
        MapSineFlags(b, f);
        int m = 0, envNoise = 0, noiseOffset = 0;
        for (int i = 0; i < nEnv; i++)
        {
            int startPos = 2 * f.Borders[i], stopPos = 2 * f.Borders[i + 1];
            int freqRes = f.FreqRes[i];
            if (f.Borders[i] == f.BordersNoise[envNoise + 1])
            {
                noiseOffset += noNoiseBands;
                envNoise++;
            }
            bool noNoise;
            int smoothLength;
            if (i == f.TranEnv || i == _prevTranEnv) { noNoise = true; smoothLength = 0; }
            else { noNoise = false; smoothLength = (1 - h.SmoothingLength) << 2; }

            if (h.InterpolFreq != 0) EnergyPerSubband(lowSubband, highSubband, startPos, stopPos, nrgEst);
            else EnergyPerSfb(b.NSfb[freqRes], b.Table[freqRes], startPos, stopPos, nrgEst);

            {
                var table = b.Table[freqRes];
                int uiNoise = b.Noise[1];
                int noiseBandIndex = 0;
                int c = 0, cc = 0;
                for (int j = 0; j < b.NSfb[freqRes]; j++)
                {
                    int li = table[j], ui = table[j + 1];
                    bool sinePresent = false;
                    for (int k = li; k < ui; k++)
                    {
                        if (i >= _sineMapped[cc]) sinePresent = true;
                        cc++;
                    }
                    for (int k = li; k < ui; k++)
                    {
                        if (k >= uiNoise)
                        {
                            noiseBandIndex++;
                            uiNoise = b.Noise[noiseBandIndex + 1];
                        }
                        nrgRef[c] = f.Envelope[m];
                        nrgSine[c] = 0;
                        SubbandGain(nrgRef[c], nrgEst[c], f.NoiseFloor[noiseOffset + noiseBandIndex], out nrgGain[c], out noiseLevel[c], ref nrgSine[c],
                            sinePresent, i >= _sineMapped[c], noNoise);
                        c++;
                    }
                    m++;
                }
            }

            for (int c = 0; c < b.NLimiter; c++)
            {
                int lo = b.Limiter[c], hi = b.Limiter[c + 1];
                float sumRef = 1.0f, sumEst = 1.0f;
                for (int k = lo; k < hi; k++) { sumRef += nrgRef[k]; sumEst += nrgEst[k]; }
                float maxGain = sumRef / sumEst;
                maxGain *= limiterGain;
                if (maxGain > 1.0e10f) maxGain = 1.0e10f;
                for (int k = lo; k < hi; k++)
                {
                    if (nrgGain[k] > maxGain)
                    {
                        noiseLevel[k] *= maxGain / nrgGain[k];
                        nrgGain[k] = maxGain;
                    }
                }
                float accu = 0.0f;
                for (int k = lo; k < hi; k++)
                {
                    accu += nrgGain[k] * nrgEst[k];
                    if (nrgSine[k] != 0.0f) accu += nrgSine[k];
                    else if (!noNoise) accu += noiseLevel[k];
                }
                if (accu == 0) accu = Eps;
                float boost = sumRef / accu;
                if (boost > 2.51188643f) boost = 2.51188643f;
                for (int k = lo; k < hi; k++)
                {
                    nrgGain[k] *= boost;
                    nrgSine[k] *= boost;
                    noiseLevel[k] *= boost;
                }
            }
            for (int k = 0; k < noSubbands; k++)
            {
                nrgSine[k] = MathF.Sqrt(nrgSine[k]);
                nrgGain[k] = MathF.Sqrt(nrgGain[k]);
                noiseLevel[k] = MathF.Sqrt(noiseLevel[k]);
            }
            if (_startUp)
            {
                for (int k = 0; k < noSubbands; k++) { _filtNoise[k] = noiseLevel[k]; _filtGain[k] = nrgGain[k]; }
                _startUp = false;
            }
            for (int l = startPos; l < stopPos; l++)
            {
                float smoothRatio = l - startPos < smoothLength ? SmoothFilter[l - startPos] : 0;
                AdjustSlot(Re[l], Im[l], lowSubband, noSubbands, smoothRatio, noNoise);
            }
            for (int k = 0; k < noSubbands; k++) { _filtGain[k] = nrgGain[k]; _filtNoise[k] = noiseLevel[k]; }
        }
        _prevTranEnv = f.TranEnv == nEnv ? 0 : -1;
    }

    private void EnergyPerSubband(int lowSubband, int highSubband, int startPos, int nextPos, float[] nrgEst)
    {
        float invWidth = InvInt[nextPos - startPos];
        int o = 0;
        for (int k = lowSubband; k < highSubband; k++)
        {
            float accu = 0;
            for (int l = startPos; l < nextPos; l++)
            {
                float t = Re[l][k];
                accu += t * t;
                t = Im[l][k];
                accu += t * t;
            }
            nrgEst[o++] = accu * invWidth;
        }
    }

    private void EnergyPerSfb(int nSfb, byte[] table, int startPos, int nextPos, float[] nrgEst)
    {
        float invWidth = InvInt[nextPos - startPos];
        int o = 0;
        for (int j = 0; j < nSfb; j++)
        {
            int li = table[j], ui = table[j + 1];
            float accu = 0;
            for (int k = li; k < ui; k++)
                for (int l = startPos; l < nextPos; l++)
                {
                    float t = Re[l][k];
                    accu += t * t;
                    t = Im[l][k];
                    accu += t * t;
                }
            accu *= invWidth;
            accu *= InvInt[ui - li];
            for (int k = li; k < ui; k++) nrgEst[o++] = accu;
        }
    }

    private static void SubbandGain(float nrgRef, float nrgEst, float tmpNoise, out float gain, out float noiseLevel, ref float nrgSine,
        bool sinePresent, bool sineMapped, bool noNoise)
    {
        nrgEst += 1.0f;
        float a = nrgRef * tmpNoise;
        float bb = 1.0f + tmpNoise;
        noiseLevel = a / bb;
        if (sinePresent)
        {
            float c = bb * nrgEst;
            gain = a / c;
            if (sineMapped) nrgSine = nrgRef / bb;
        }
        else
        {
            bb = noNoise ? nrgEst : bb * nrgEst;
            gain = nrgRef / bb;
        }
    }

    private void AdjustSlot(float[] re, float[] im, int lowSubband, int noSubbands, float smoothRatio, bool noNoise)
    {
        float directRatio = 1.0f - smoothRatio;
        int index = _phaseIndex;
        int harmIndex = _harmIndex;
        bool freqInv = (lowSubband & 1) != 0;
        var rp = AacSbrTables.RandomPhase;
        for (int k = 0; k < noSubbands; k++)
        {
            float smoothedGain, smoothedNoise;
            if (smoothRatio > 0)
            {
                smoothedGain = smoothRatio * _filtGain[k] + directRatio * _nrgGain[k];
                smoothedNoise = smoothRatio * _filtNoise[k] + directRatio * _noiseLevel[k];
            }
            else
            {
                smoothedGain = _nrgGain[k];
                smoothedNoise = _noiseLevel[k];
            }
            int at = lowSubband + k;
            float signalReal = re[at] * smoothedGain;
            float signalImag = im[at] * smoothedGain;
            index = (index + 1) & 511;
            if (_nrgSine[k] != 0.0f)
            {
                float sine = _nrgSine[k];
                switch (harmIndex)
                {
                    case 0: re[at] = signalReal + sine; im[at] = signalImag; break;
                    case 2: re[at] = signalReal - sine; im[at] = signalImag; break;
                    case 1: re[at] = signalReal; im[at] = freqInv ? signalImag - sine : signalImag + sine; break;
                    default: re[at] = signalReal; im[at] = freqInv ? signalImag + sine : signalImag - sine; break;
                }
            }
            else if (noNoise)
            {
                re[at] = signalReal;
                im[at] = signalImag;
            }
            else
            {
                re[at] = signalReal + rp[index * 2] * smoothedNoise;
                im[at] = signalImag + rp[index * 2 + 1] * smoothedNoise;
            }
            freqInv = !freqInv;
        }
        _harmIndex = (harmIndex + 1) & 3;
        _phaseIndex = index;
    }
}
