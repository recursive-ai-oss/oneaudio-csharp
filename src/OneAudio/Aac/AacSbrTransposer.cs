namespace OneAudio.Aac;

/// <summary>A complex number in single precision, for the covariance arithmetic of the transposer.</summary>
internal readonly struct Cx(float re, float im)
{
    public readonly float Re = re, Im = im;

    public static Cx operator +(Cx a, Cx b) => new(a.Re + b.Re, a.Im + b.Im);
    public static Cx operator -(Cx a, Cx b) => new(a.Re - b.Re, a.Im - b.Im);
    public static Cx operator *(Cx a, Cx b) => new(a.Re * b.Re - a.Im * b.Im, a.Re * b.Im + a.Im * b.Re);
    public static Cx operator *(float s, Cx a) => new(s * a.Re, s * a.Im);
    public Cx Conjugate() => new(Re, -Im);
    public float NormSquared => Re * Re + Im * Im;
}

/// <summary>
/// The patching stage of spectral band replication (ISO/IEC 14496-3 4.6.18.6, "HF generator"): copies low-band QMF
/// samples into the high band after predicting each low subband from its two preceding samples (second-order
/// complex linear prediction along time, inverse filtering with the transmitted chirp strength).
/// </summary>
internal sealed class SbrTransposer
{
    // The two samples that precede the current buffer, per low subband: index 0 is x(-2), index 1 is x(-1).
    private readonly float[][] _histRe = [new float[32], new float[32]];
    private readonly float[][] _histIm = [new float[32], new float[32]];
    private readonly float[] _chirpPrev = new float[SbrBands.MaxNoiseCoeffs];
    private readonly float[] _chirp = new float[SbrBands.MaxNoiseCoeffs];
    private readonly int[] _chirpBandOf = new int[SbrChannel.Bands];

    // one low subband laid out with its two history samples in front: element n + 2 is x(n)
    private readonly float[] _xr = new float[SbrChannel.Slots + 2];
    private readonly float[] _xi = new float[SbrChannel.Slots + 2];

    public void Reset()
    {
        for (int i = 0; i < 2; i++) { Array.Clear(_histRe[i]); Array.Clear(_histIm[i]); }
        Array.Clear(_chirpPrev);
    }

    /// <summary>Forgets the prediction history of low subbands in [<paramref name="from"/>, <paramref name="to"/>).</summary>
    public void ClearHistory(int from, int to)
    {
        for (int k = from; k < to && k < 32; k++)
        {
            _histRe[0][k] = _histRe[1][k] = 0;
            _histIm[0][k] = _histIm[1][k] = 0;
        }
    }

    /// <summary>Remembers the two slots before the next buffer. Needed in frames where nothing is patched too.</summary>
    public void SaveHistory(float[][] re, float[][] im, int lowSubbands)
    {
        for (int i = 0; i < 2; i++)
            for (int k = 0; k < lowSubbands; k++)
            {
                _histRe[i][k] = re[SbrChannel.Cols - 2 + i][k];
                _histIm[i][k] = im[SbrChannel.Cols - 2 + i][k];
            }
    }

    /// <summary>The chirp (bandwidth expansion) factor for an inverse-filtering mode, 4.6.18.6.3 Table 4.157.</summary>
    private static float ChirpTarget(int mode, int previousMode) => mode switch
    {
        1 => previousMode == 0 ? 0.6f : 0.75f,   // low: a stronger step when coming from "off"
        2 => 0.90f,                              // intermediate
        3 => 0.98f,                              // strong
        _ => previousMode == 1 ? 0.6f : 0.0f,    // off
    };

    // Covariance phi(i, j) = sum over n of x(n - i) * conj(x(n - j)) across the whole buffer (4.6.18.6.2).
    private Cx Covariance(int i, int j)
    {
        float sr = 0, si = 0;
        for (int n = 0; n < SbrChannel.Slots; n++)
        {
            float ar = _xr[n + 2 - i], ai = _xi[n + 2 - i], br = _xr[n + 2 - j], bi = _xi[n + 2 - j];
            sr += ar * br + ai * bi;
            si += ai * br - ar * bi;
        }
        return new Cx(sr, si);
    }

    /// <summary>
    /// Generates the high band into <paramref name="re"/>/<paramref name="im"/> for the time range of the frame's envelopes.
    /// </summary>
    public void Run(float[][] re, float[][] im, SbrBands bands, SbrFrame frame, SbrPrev prev)
    {
        int nNoise = bands.NNoise;
        int first = frame.Borders[0] * 2;
        int end = SbrChannel.Cols + (frame.Borders[frame.NEnvelopes] - 16) * 2;

        // Smooth the chirp factors over time: faster attack when the strength falls (4.6.18.6.3).
        for (int i = 0; i < nNoise; i++)
        {
            float target = ChirpTarget(frame.InvfMode[i], prev.InvfMode[i]);
            float wNew = target < _chirpPrev[i] ? 0.75f : 0.90625f;
            float v = wNew * target + (1 - wNew) * _chirpPrev[i];
            if (v < 0.015625f) v = 0;
            _chirp[i] = Math.Min(v, 0.99609375f);
        }
        // the chirp band of every high-band subband: the noise band whose upper edge lies above it
        for (int hi = 0; hi < SbrChannel.Bands; hi++)
        {
            int idx = 0;
            while (idx < nNoise - 1 && hi >= bands.BwBorders[idx]) idx++;
            _chirpBandOf[hi] = idx;
        }

        // above the last patch nothing is generated: make sure no stale data remains in the range
        ref var lastPatch = ref bands.Patches[bands.NPatches - 1];
        int patchedEnd = Math.Min(SbrChannel.Bands, lastPatch.TargetStartBand + lastPatch.NumBandsInPatch);
        for (int n = first; n < end; n++)
        {
            Array.Clear(re[n], patchedEnd, SbrChannel.Bands - patchedEnd);
            Array.Clear(im[n], patchedEnd, SbrChannel.Bands - patchedEnd);
        }

        for (int k = bands.LbStart; k < bands.LbStop; k++)
        {
            _xr[0] = _histRe[0][k]; _xr[1] = _histRe[1][k];
            _xi[0] = _histIm[0][k]; _xi[1] = _histIm[1][k];
            for (int n = 0; n < SbrChannel.Slots; n++) { _xr[n + 2] = re[n][k]; _xi[n + 2] = im[n][k]; }

            Predictor(out Cx alpha0, out Cx alpha1);

            for (int p = 0; p < bands.NPatches; p++)
            {
                ref var patch = ref bands.Patches[p];
                if (k < patch.SourceStartBand || k >= patch.SourceStopBand) continue;
                int target = k + patch.TargetBandOffs;
                if (target >= SbrChannel.Bands) continue;
                float bw = _chirp[_chirpBandOf[target]];
                float bw2 = bw * bw;
                Cx c0 = bw * alpha0, c1 = bw2 * alpha1;
                for (int n = first; n < end; n++)
                {
                    float xr = _xr[n + 2], xi = _xi[n + 2];
                    if (bw2 > 0)
                    {
                        Cx p1 = c0 * new Cx(_xr[n + 1], _xi[n + 1]);
                        Cx p2 = c1 * new Cx(_xr[n], _xi[n]);
                        xr += p1.Re + p2.Re;
                        xi += p1.Im + p2.Im;
                    }
                    re[n][target] = xr;
                    im[n][target] = xi;
                }
            }
        }

        SaveHistory(re, im, bands.Patches[0].TargetStartBand);
        for (int i = 0; i < nNoise; i++) _chirpPrev[i] = _chirp[i];
    }

    // Solves the normal equations of the second-order predictor from the covariance of the loaded subband (4.6.18.6.2).
    private void Predictor(out Cx alpha0, out Cx alpha1)
    {
        Cx p11 = Covariance(1, 1), p22 = Covariance(2, 2), p01 = Covariance(0, 1), p12 = Covariance(1, 2), p02 = Covariance(0, 2);
        float det = p11.Re * p22.Re - p12.NormSquared;
        alpha1 = default;
        if (det != 0.0f) alpha1 = (1.0f / det) * (p01 * p12 - p11.Re * p02);
        alpha0 = default;
        if (p11.Re != 0.0f) alpha0 = (-1.0f / p11.Re) * (p01 + alpha1 * p12.Conjugate());
        // an unstable predictor is switched off
        if (alpha0.NormSquared >= 16.0f || alpha1.NormSquared >= 16.0f)
        {
            alpha0 = default;
            alpha1 = default;
        }
    }
}
