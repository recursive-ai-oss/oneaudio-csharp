namespace OneAudio.Aac;

/// <summary>
/// The envelope adjuster of spectral band replication (ISO/IEC 14496-3 4.6.18.7, "HF adjustment"): scales the patched
/// high band so each scale-factor band reaches its transmitted energy, adds the noise floor and the signalled
/// sinusoids, limits the gains per limiter band and smooths them over time at the start of an envelope.
/// </summary>
internal sealed class SbrAdjuster
{
    private const float GainCap = 1.0e10f;            // largest limiter gain (4.6.18.7.4)
    private const float BoostCap = 2.51188643f;       // +8 dB: largest compensation boost (4.6.18.7.4)
    private const float Tiny = 1e-12f;                // keeps a zero denominator away
    private const int NeverStarts = SbrFrame.MaxEnvelopes;

    private static readonly float[] LimiterGainTable = [0.5011932025f, 1.0f, 1.9952692516f, 1.0e20f]; // -6, 0, +6 dB, infinite
    // Smoothing filter h_smooth (4.6.18.7.3): the four weights applied to the previous envelope's gains.
    private static readonly float[] Smooth = [0.66666666666666f, 0.36516383427084f, 0.14699433520835f, 0.03183050093751f];
    private static readonly float[] Reciprocal = BuildReciprocals();

    // sinusoid bookkeeping (4.6.18.7.5)
    private readonly bool[] _prevHarmonic = new bool[SbrBands.MaxFreqCoeffs];
    private readonly int[] _sineStart = new int[SbrBands.MaxFreqCoeffs];   // first envelope in which a subband carries a sinusoid
    private int _sinePhase;                                                 // position in the 4-step sinusoid cycle
    private int _noisePhase;                                                // position in the noise table
    private int _prevTransient = -1;
    private bool _first = true;

    // per-subband values of the envelope being processed, then those of the previous one
    private readonly float[] _gainPrev = new float[SbrBands.MaxFreqCoeffs];
    private readonly float[] _noisePrev = new float[SbrBands.MaxFreqCoeffs];
    private readonly float[] _target = new float[SbrBands.MaxFreqCoeffs];   // transmitted energy per subband
    private readonly float[] _measured = new float[SbrBands.MaxFreqCoeffs]; // energy of the patched signal
    private readonly float[] _noiseEnergy = new float[SbrBands.MaxFreqCoeffs];
    private readonly float[] _sineEnergy = new float[SbrBands.MaxFreqCoeffs];
    private readonly float[] _gain = new float[SbrBands.MaxFreqCoeffs];

    public SbrAdjuster()
    {
        // The static tables above are created when first touched. Touch them here, at construction, so that the first frame
        // with SBR data (which can arrive thousands of frames into a stream) allocates nothing.
        _ = Reciprocal.Length + Smooth.Length + LimiterGainTable.Length;
    }

    private static float[] BuildReciprocals()
    {
        var t = new float[64];
        t[0] = float.MaxValue;
        for (int i = 1; i < t.Length; i++) t[i] = 1.0f / i;
        return t;
    }

    public void Reset()
    {
        Array.Clear(_prevHarmonic);
        Array.Clear(_gainPrev);
        Array.Clear(_noisePrev);
        _sinePhase = _noisePhase = 0;
        _prevTransient = -1;
        _first = true;
    }

    /// <summary>Restarts the noise sequence and gain smoothing after a header with a new band layout.</summary>
    public void Restart()
    {
        _noisePhase = 0;
        _first = true;
    }

    // Marks the first envelope in which each subband carries a sinusoid: the transient envelope if the sinusoid is new,
    // envelope 0 if the same band had one in the previous frame (4.6.18.7.5).
    private void PlanSinusoids(SbrBands b, SbrFrame f)
    {
        var edges = b.Table[1];
        int nBands = b.NSfb[1];
        Array.Fill(_sineStart, NeverStarts);
        for (int i = 0; i < nBands; i++)
        {
            bool now = f.AddHarmonics[i] != 0;
            if (now)
            {
                int mid = (edges[i] + edges[i + 1] - 2 * edges[0]) >> 1;
                if ((uint)mid < (uint)_sineStart.Length) _sineStart[mid] = _prevHarmonic[i] ? 0 : f.TranEnv;
            }
            _prevHarmonic[i] = now;
        }
    }

    /// <summary>Adjusts the high band held in <paramref name="re"/>/<paramref name="im"/> for one frame.</summary>
    public void Run(float[][] re, float[][] im, SbrBands b, SbrHeader h, SbrFrame f)
    {
        int nEnv = f.NEnvelopes;
        int low = b.LowSubband, count = b.HighSubband - b.LowSubband;
        float limiterGain = LimiterGainTable[h.LimiterGains];
        PlanSinusoids(b, f);

        int sfbIndex = 0;       // running index into the transmitted envelope values
        int noiseEnv = 0, noiseOffset = 0;
        for (int env = 0; env < nEnv; env++)
        {
            int start = 2 * f.Borders[env], stop = 2 * f.Borders[env + 1];
            int res = f.FreqRes[env];
            if (f.Borders[env] == f.BordersNoise[noiseEnv + 1])
            {
                noiseOffset += b.NNoise;
                noiseEnv++;
            }
            // Transient envelopes (and the one after) get no added noise and no smoothing.
            bool transient = env == f.TranEnv || env == _prevTransient;
            int smoothLength = transient ? 0 : (1 - h.SmoothingLength) << 2;

            MeasureEnergy(re, im, b, h, res, start, stop);
            ComputeGains(b, f, res, env, transient, sfbIndex, noiseOffset);
            sfbIndex += b.NSfb[res];
            LimitGains(b, limiterGain, transient, count);

            for (int k = 0; k < count; k++)
            {
                _gain[k] = MathF.Sqrt(_gain[k]);
                _noiseEnergy[k] = MathF.Sqrt(_noiseEnergy[k]);
                _sineEnergy[k] = MathF.Sqrt(_sineEnergy[k]);
            }
            if (_first)
            {
                Array.Copy(_gain, _gainPrev, count);
                Array.Copy(_noiseEnergy, _noisePrev, count);
                _first = false;
            }
            for (int t = start; t < stop; t++)
                ApplySlot(re[t], im[t], low, count, t - start < smoothLength ? Smooth[t - start] : 0f, transient);
            Array.Copy(_gain, _gainPrev, count);
            Array.Copy(_noiseEnergy, _noisePrev, count);
        }
        _prevTransient = f.TranEnv == nEnv ? 0 : -1;
    }

    // Mean energy of the patched signal over the envelope, per subband (4.6.18.7.2). With interpolated frequency
    // resolution each subband stands alone; otherwise the energy is shared across its scale-factor band.
    private void MeasureEnergy(float[][] re, float[][] im, SbrBands b, SbrHeader h, int res, int start, int stop)
    {
        float perSlot = Reciprocal[stop - start];
        if (h.InterpolFreq != 0)
        {
            for (int k = b.LowSubband; k < b.HighSubband; k++)
                _measured[k - b.LowSubband] = perSlot * SumSquares(re, im, k, k + 1, start, stop);
            return;
        }
        var edges = b.Table[res];
        int o = 0;
        for (int j = 0; j < b.NSfb[res]; j++)
        {
            int lo = edges[j], hi = edges[j + 1];
            float e = SumSquares(re, im, lo, hi, start, stop) * perSlot * Reciprocal[hi - lo];
            for (int k = lo; k < hi; k++) _measured[o++] = e;
        }
    }

    private static float SumSquares(float[][] re, float[][] im, int kLo, int kHi, int start, int stop)
    {
        float acc = 0;
        for (int k = kLo; k < kHi; k++)
            for (int t = start; t < stop; t++)
            {
                float a = re[t][k], c = im[t][k];
                acc += a * a;
                acc += c * c;
            }
        return acc;
    }

    // Gain, noise level and sinusoid level per subband from the transmitted energy, noise floor and sinusoid flags
    // (4.6.18.7.4, 4.6.18.7.5). Energies, not amplitudes, until the square root at the end.
    private void ComputeGains(SbrBands b, SbrFrame f, int res, int env, bool transient, int sfbIndex, int noiseOffset)
    {
        var edges = b.Table[res];
        int baseBand = edges[0];
        int noiseBand = 0;
        int noiseTop = b.Noise[1];
        for (int j = 0; j < b.NSfb[res]; j++)
        {
            int lo = edges[j], hi = edges[j + 1];
            // a sinusoid anywhere in the scale-factor band changes how the whole band is treated
            bool sineInBand = false;
            for (int k = lo; k < hi; k++) sineInBand |= env >= _sineStart[k - baseBand];
            float target = f.Envelope[sfbIndex + j];
            for (int k = lo; k < hi; k++)
            {
                if (k >= noiseTop)
                {
                    noiseBand++;
                    noiseTop = b.Noise[noiseBand + 1];
                }
                int c = k - baseBand;
                float q = f.NoiseFloor[noiseOffset + noiseBand];
                float measured = _measured[c] + 1.0f;
                float noise = target * q / (1.0f + q);
                _target[c] = target;
                _noiseEnergy[c] = noise;
                _sineEnergy[c] = 0;
                if (sineInBand)
                {
                    _gain[c] = noise / measured;
                    if (env >= _sineStart[c]) _sineEnergy[c] = target / (1.0f + q);
                }
                else
                {
                    _gain[c] = transient ? target / measured : target / ((1.0f + q) * measured);
                }
            }
        }
    }

    // Limits the gain inside each limiter band to the ratio of transmitted to measured energy times the limiter gain,
    // then rescales so the band keeps its transmitted energy (boost capped at +8 dB) (4.6.18.7.4).
    private void LimitGains(SbrBands b, float limiterGain, bool transient, int count)
    {
        for (int band = 0; band < b.NLimiter; band++)
        {
            int lo = b.Limiter[band], hi = b.Limiter[band + 1];
            float sumTarget = 1.0f, sumMeasured = 1.0f;
            for (int k = lo; k < hi; k++) { sumTarget += _target[k]; sumMeasured += _measured[k]; }
            float maxGain = Math.Min(sumTarget / sumMeasured * limiterGain, GainCap);
            for (int k = lo; k < hi; k++)
            {
                if (_gain[k] > maxGain)
                {
                    _noiseEnergy[k] *= maxGain / _gain[k];
                    _gain[k] = maxGain;
                }
            }
            float total = 0;
            for (int k = lo; k < hi; k++)
            {
                total += _gain[k] * _measured[k];
                if (_sineEnergy[k] != 0.0f) total += _sineEnergy[k];
                else if (!transient) total += _noiseEnergy[k];
            }
            if (total == 0) total = Tiny;
            float boost = Math.Min(sumTarget / total, BoostCap);
            for (int k = lo; k < hi; k++)
            {
                _gain[k] *= boost;
                _sineEnergy[k] *= boost;
                _noiseEnergy[k] *= boost;
            }
        }
    }

    // Applies gain (smoothed against the previous envelope near its start), then adds either a sinusoid or noise (4.6.18.7.5).
    private void ApplySlot(float[] re, float[] im, int low, int count, float smooth, bool noNoise)
    {
        float direct = 1.0f - smooth;
        int noisePhase = _noisePhase;
        int sinePhase = _sinePhase;
        bool oddBand = (low & 1) != 0;
        var noiseTable = AacSbrTables.RandomPhase;
        for (int k = 0; k < count; k++)
        {
            float g = _gain[k], nl = _noiseEnergy[k];
            if (smooth > 0)
            {
                g = smooth * _gainPrev[k] + direct * g;
                nl = smooth * _noisePrev[k] + direct * nl;
            }
            int at = low + k;
            float r = re[at] * g, i = im[at] * g;
            noisePhase = (noisePhase + 1) & 511;
            float s = _sineEnergy[k];
            if (s != 0.0f)
            {
                // The sinusoid rotates by a quarter turn per slot: +real, +imag, -real, -imag; the imaginary
                // direction alternates with the subband parity.
                switch (sinePhase)
                {
                    case 0: r += s; break;
                    case 2: r -= s; break;
                    case 1: i += oddBand ? -s : s; break;
                    default: i += oddBand ? s : -s; break;
                }
            }
            else if (!noNoise)
            {
                r += noiseTable[noisePhase * 2] * nl;
                i += noiseTable[noisePhase * 2 + 1] * nl;
            }
            re[at] = r;
            im[at] = i;
            oddBand = !oddBand;
        }
        _sinePhase = (sinePhase + 1) & 3;
        _noisePhase = noisePhase;
    }
}
