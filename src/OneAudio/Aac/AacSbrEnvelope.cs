namespace OneAudio.Aac;

/// <summary>
/// Turns the raw envelope and noise floor codes of an SBR frame into linear energies (ISO/IEC 14496-3 4.6.18.3.5 and
/// 4.6.18.3.6): delta decoding in time or frequency direction, range limits, the switch from logarithmic to linear
/// domain, the unmapping of coupled channel pairs, and a simple concealment for frames that cannot be used.
/// </summary>
internal static class SbrEnvelope
{
    private const float MaxLevel = 35f;       // largest meaningful envelope code at 3 dB resolution (doubled at 1.5 dB)
    private const float NoiseOffset = 6f;      // noise floor energy is 2^(6 - code)
    private const float PanOffset = 12f;       // coupled balance codes are centred on 12

    // The envelope of the previous frame is kept at high resolution; a low resolution envelope covers one or two
    // high resolution bands (the low table is every second border of the high one, 4.6.18.3.2).
    private static int LoToHi(int offset, int band) => band < offset ? band : 2 * band - offset;

    private static void StorePrev(float[] prev, int offset, int band, int res, float value)
    {
        if (res == 0 && band >= offset)
        {
            int at = 2 * band - offset;
            prev[at] = value;
            prev[at + 1] = value;
        }
        else
        {
            prev[band] = value;
        }
    }

    /// <summary>
    /// Delta-decodes the envelope of <paramref name="f"/> against <paramref name="prev"/> and converts it to linear energy.
    /// Returns false (leaving <paramref name="prev"/> as it was) when a decoded value is out of range.
    /// </summary>
    public static bool DecodeEnvelope(SbrBands b, SbrFrame f, SbrPrev prev, float[] saved)
    {
        Array.Copy(prev.SfbNrgPrev, saved, saved.Length);
        var env = f.Envelope;
        var np = prev.SfbNrgPrev;
        int offset = 2 * b.NSfb[0] - b.NSfb[1];
        int at = 0;
        for (int i = 0; i < f.NEnvelopes; i++)
        {
            int res = f.FreqRes[i];
            int n = b.NSfb[res];
            if (f.DomainVec[i] == 0)
            {
                StorePrev(np, offset, 0, res, env[at]);
                for (int k = 1; k < n; k++)
                {
                    env[at + k] += env[at + k - 1];
                    StorePrev(np, offset, k, res, env[at + k]);
                }
            }
            else
            {
                for (int k = 0; k < n; k++)
                {
                    env[at + k] += np[res == 0 ? LoToHi(offset, k) : k];
                    StorePrev(np, offset, k, res, env[at + k]);
                }
            }
            at += n;
        }
        // range check
        float max = f.AmpResCurrent == 1 ? MaxLevel : 2 * MaxLevel;
        bool bad = false;
        for (int i = 0; i < f.NScaleFactors; i++)
        {
            if (env[i] > max) bad = true;
            if (env[i] < 0) env[i] = 0;
        }
        for (int i = 0; i < b.NSfb[1]; i++)
        {
            if (np[i] < 0) np[i] = 0;
            else if (np[i] > max) np[i] = max;
        }
        if (bad)
        {
            Array.Copy(saved, prev.SfbNrgPrev, saved.Length);
            return false;
        }
        return true;
    }

    /// <summary>Restores the delta history after a failed decode.</summary>
    public static void RestorePrev(SbrPrev prev, float[] saved) => Array.Copy(saved, prev.SfbNrgPrev, saved.Length);

    /// <summary>Envelope codes to linear: 2^(code / a + 4) with a = 2 at 1.5 dB resolution and 1 at 3 dB (4.6.18.3.5).</summary>
    public static void Requantize(SbrFrame f)
    {
        int shift = 1 - f.AmpResCurrent;
        for (int i = 0; i < f.NScaleFactors; i++)
        {
            int e = (int)f.Envelope[i];
            float mant = (e & shift) != 0 ? 0.707106781186548f : 0.5f;
            f.Envelope[i] = (float)(mant * Math.Pow(2.0, (e >> shift) + 5));
        }
    }

    /// <summary>Delta-decodes the noise floor of <paramref name="f"/> (4.6.18.3.6) and keeps the last envelope for the next frame.</summary>
    public static void DecodeNoise(SbrBands b, SbrFrame f, SbrPrev prev)
    {
        int n = b.NNoise;
        var nf = f.NoiseFloor;
        if (f.DomainVecNoise[0] == 0)
        {
            for (int i = 1; i < n; i++) nf[i] += nf[i - 1];
        }
        else
        {
            for (int i = 0; i < n; i++) nf[i] += prev.PrevNoiseLevel[i];
        }
        if (f.NNoiseEnvelopes > 1)
        {
            if (f.DomainVecNoise[1] == 0)
            {
                for (int i = n + 1; i < 2 * n; i++) nf[i] += nf[i - 1];
            }
            else
            {
                for (int i = 0; i < n; i++) nf[i + n] += nf[i];
            }
        }
        int total = n * f.NNoiseEnvelopes;
        for (int i = 0; i < total; i++) nf[i] = Math.Clamp(nf[i], 0f, MaxLevel);
        for (int i = 0; i < n; i++) prev.PrevNoiseLevel[i] = nf[i + n * (f.NNoiseEnvelopes - 1)];
        if (f.Coupling == 0)
            for (int i = 0; i < total; i++) nf[i] = (float)Math.Pow(2.0, (int)(NoiseOffset - (long)nf[i]));
    }

    /// <summary>
    /// Splits a coupled pair into left and right energies (4.6.18.3.6): the left channel carries the level and the right the
    /// balance, both as codes already turned to linear by the decode steps above.
    /// </summary>
    public static void Unmap(SbrBands b, SbrFrame l, SbrFrame r)
    {
        const float scale = 1.0f / 65536f;
        for (int i = 0; i < l.NScaleFactors; i++)
        {
            float rr = r.Envelope[i] * scale;
            float lev = l.Envelope[i];
            float newR = 2.0f * lev / (rr + 1.0f);
            l.Envelope[i] = rr * newR;
            r.Envelope[i] = newR;
        }
        int total = b.NNoise * l.NNoiseEnvelopes;
        for (int i = 0; i < total; i++)
        {
            float lev = (float)Math.Pow(2.0, NoiseOffset - l.NoiseFloor[i]);
            float bal = (float)Math.Pow(2.0, r.NoiseFloor[i] - PanOffset);
            float newR = 2.0f * lev / (1.0f + bal);
            l.NoiseFloor[i] = bal * newR;
            r.NoiseFloor[i] = newR;
        }
    }

    /// <summary>
    /// Replaces a frame that could not be decoded by one flat envelope that slowly follows the previous energies: the
    /// transmitted codes are replaced by a one-step drift towards the target, no noise and no sinusoids.
    /// </summary>
    public static void Conceal(SbrBands b, SbrFrame f, SbrPrev prev, int numTimeSlots)
    {
        int start = prev.StopPos - numTimeSlots;
        f.AmpResCurrent = prev.AmpRes;
        f.Coupling = prev.Coupling;
        for (int i = 0; i < SbrBands.MaxNoiseCoeffs; i++) f.InvfMode[i] = prev.InvfMode[i];
        f.NEnvelopes = 1;
        f.Borders[0] = start;
        f.Borders[1] = numTimeSlots;
        f.FreqRes[0] = 1;
        f.TranEnv = -1;
        f.NNoiseEnvelopes = 1;
        f.BordersNoise[0] = start;
        f.BordersNoise[1] = numTimeSlots;
        f.NScaleFactors = b.NSfb[1];
        f.DomainVec[0] = 1;
        float target = f.Coupling == 2 ? PanOffset : 0f;
        float step = 1f;
        if (f.AmpResCurrent == 0) { target *= 2; step *= 2; }
        for (int i = 0; i < f.NScaleFactors; i++) f.Envelope[i] = prev.SfbNrgPrev[i] > target ? -step : step;
        f.DomainVecNoise[0] = 1;
        for (int i = 0; i < b.NNoise; i++) f.NoiseFloor[i] = 0f;
        Array.Clear(f.AddHarmonics);
    }
}
