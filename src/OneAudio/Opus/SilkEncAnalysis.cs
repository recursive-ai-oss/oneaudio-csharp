using static OneAudio.Opus.SilkMath;
using static OneAudio.Opus.SilkEncMath;

namespace OneAudio.Opus;

/// <summary>Per-frame analysis results shared between the encoder stages (silk_encoder_control_FLP).</summary>
internal sealed class SilkEncCtrl
{
    public readonly float[] Gains = new float[4];
    public readonly float[] PredCoef0 = new float[16];
    public readonly float[] PredCoef1 = new float[16];
    public readonly float[] LtpCoef = new float[20];
    public float LtpScale;
    public readonly int[] PitchL = new int[4];
    public readonly float[] AR1 = new float[64];
    public readonly float[] AR2 = new float[64];
    public readonly float[] LfMaShp = new float[4];
    public readonly float[] LfArShp = new float[4];
    public readonly float[] GainsPre = new float[4];
    public readonly float[] HarmBoost = new float[4];
    public readonly float[] Tilt = new float[4];
    public readonly float[] HarmShapeGain = new float[4];
    public float Lambda, InputQuality, CodingQuality, Sparseness, PredGain, LtpredCodGain;
    public readonly float[] ResNrg = new float[4];
    public readonly int[] GainsUnqQ16 = new int[4];
    public sbyte LastGainIndexPrev;
}

internal sealed partial class SilkEncState
{
    private const int SubFrameLengthMs = 5;
    private const int LaShapeMs = 5;

    private readonly float[] _xWindowed = new float[240];
    private readonly float[] _autoCorr = new float[17];

    private static float WarpedGain(ReadOnlySpan<float> coefs, float lambda, int order)
    {
        lambda = -lambda;
        float gain = coefs[order - 1];
        for (int i = order - 2; i >= 0; i--) gain = lambda * gain + coefs[i];
        return 1.0f / (1.0f - lambda * gain);
    }

    private static void WarpedTrue2MonicCoefs(Span<float> coefsSyn, Span<float> coefsAna, float lambda, float limit, int order)
    {
        int ind = 0;
        for (int i = order - 1; i > 0; i--)
        {
            coefsSyn[i - 1] -= lambda * coefsSyn[i];
            coefsAna[i - 1] -= lambda * coefsAna[i];
        }
        float gainSyn = (1.0f - lambda * lambda) / (1.0f + lambda * coefsSyn[0]);
        float gainAna = (1.0f - lambda * lambda) / (1.0f + lambda * coefsAna[0]);
        for (int i = 0; i < order; i++)
        {
            coefsSyn[i] *= gainSyn;
            coefsAna[i] *= gainAna;
        }
        for (int iter = 0; iter < 10; iter++)
        {
            float maxabs = -1.0f;
            for (int i = 0; i < order; i++)
            {
                float a = MathF.Abs(coefsSyn[i]), b = MathF.Abs(coefsAna[i]);
                float tmp = a > b ? a : b;
                if (tmp > maxabs)
                {
                    maxabs = tmp;
                    ind = i;
                }
            }
            if (maxabs <= limit) return;
            for (int i = 1; i < order; i++)
            {
                coefsSyn[i - 1] += lambda * coefsSyn[i];
                coefsAna[i - 1] += lambda * coefsAna[i];
            }
            gainSyn = 1.0f / gainSyn;
            gainAna = 1.0f / gainAna;
            for (int i = 0; i < order; i++)
            {
                coefsSyn[i] *= gainSyn;
                coefsAna[i] *= gainAna;
            }
            float chirp = 0.99f - (0.8f + 0.1f * iter) * (maxabs - limit) / (maxabs * (ind + 1));
            SilkFlp.BwExpander(coefsSyn, order, chirp);
            SilkFlp.BwExpander(coefsAna, order, chirp);
            for (int i = order - 1; i > 0; i--)
            {
                coefsSyn[i - 1] -= lambda * coefsSyn[i];
                coefsAna[i - 1] -= lambda * coefsAna[i];
            }
            gainSyn = (1.0f - lambda * lambda) / (1.0f + lambda * coefsSyn[0]);
            gainAna = (1.0f - lambda * lambda) / (1.0f + lambda * coefsAna[0]);
            for (int i = 0; i < order; i++)
            {
                coefsSyn[i] *= gainSyn;
                coefsAna[i] *= gainAna;
            }
        }
    }

    /// <summary>silk_noise_shape_analysis_FLP: noise-shaping filters, gains, tilt and harmonic shaping for the frame.</summary>
    private void NoiseShapeAnalysis(SilkEncCtrl c, ReadOnlySpan<float> pitchRes, int pitchResOff, int xOff)
    {
        var xb = XBuf;
        int xPtr = xOff - LaShape;
        float snrAdjDb = SnrDbQ7 * (1 / 128.0f);

        c.InputQuality = 0.5f * (InputQualityBandsQ15[0] + InputQualityBandsQ15[1]) * (1.0f / 32768.0f);
        c.CodingQuality = SilkFlp.Sigmoid(0.25f * (snrAdjDb - 20.0f));

        if (!UseCbr)
        {
            float bb = 1.0f - SpeechActivityQ8 * (1.0f / 256.0f);
            snrAdjDb -= 2.0f * c.CodingQuality * (0.5f + 0.5f * c.InputQuality) * bb * bb;
        }

        if (Indices.SignalType == TypeVoiced)
        {
            snrAdjDb += 2.0f * LtpCorr;
        }
        else
        {
            snrAdjDb += (-0.4f * SnrDbQ7 * (1 / 128.0f) + 6.0f) * (1.0f - c.InputQuality);
        }

        if (Indices.SignalType == TypeVoiced)
        {
            Indices.QuantOffsetType = 0;
            c.Sparseness = 0.0f;
        }
        else
        {
            int nSamples = 2 * FsKHz;
            float energyVariation = 0.0f;
            float logEnergyPrev = 0.0f;
            int pr = pitchResOff;
            for (int k = 0; k < SubFrameLengthMs * NbSubfr / 2; k++)
            {
                float nrg0 = nSamples + (float)SilkFlp.Energy(pitchRes.Slice(pr), nSamples);
                float logEnergy = SilkFlp.Log2(nrg0);
                if (k > 0) energyVariation += MathF.Abs(logEnergy - logEnergyPrev);
                logEnergyPrev = logEnergy;
                pr += nSamples;
            }
            c.Sparseness = SilkFlp.Sigmoid(0.4f * (energyVariation - 5.0f));
            Indices.QuantOffsetType = (sbyte)(c.Sparseness > 0.75f ? 0 : 1);
            snrAdjDb += 2.0f * (c.Sparseness - 0.5f);
        }

        float strength = 1e-3f * c.PredGain;
        float bwExp1, bwExp2;
        bwExp1 = bwExp2 = 0.95f / (1.0f + strength * strength);
        float delta = 0.01f * (1.0f - 0.75f * c.CodingQuality);
        bwExp1 -= delta;
        bwExp2 += delta;
        bwExp1 /= bwExp2;

        float warping = WarpingQ16 > 0 ? (float)WarpingQ16 / 65536.0f + 0.01f * c.CodingQuality : 0.0f;

        var xw = _xWindowed;
        var ac = _autoCorr;
        for (int k = 0; k < NbSubfr; k++)
        {
            int flatPart = FsKHz * 3;
            int slopePart = (ShapeWinLength - flatPart) / 2;
            SilkFlp.ApplySineWindow(xw, xb.AsSpan(xPtr), 1, slopePart);
            int shift = slopePart;
            xb.AsSpan(xPtr + shift, flatPart).CopyTo(xw.AsSpan(shift));
            shift += flatPart;
            SilkFlp.ApplySineWindow(xw.AsSpan(shift), xb.AsSpan(xPtr + shift), 2, slopePart);
            xPtr += SubfrLength;

            if (WarpingQ16 > 0) SilkFlp.WarpedAutocorrelation(ac, xw, warping, ShapeWinLength, ShapingLpcOrder);
            else SilkFlp.Autocorrelation(ac, xw, ShapeWinLength, ShapingLpcOrder + 1);

            ac[0] += ac[0] * 5e-5f;

            var ar2 = c.AR2.AsSpan(k * 16, 16);
            var ar1 = c.AR1.AsSpan(k * 16, 16);
            float nrg = SilkFlp.LevinsonDurbin(ar2, ac, ShapingLpcOrder);
            c.Gains[k] = (float)Math.Sqrt(nrg);
            if (WarpingQ16 > 0) c.Gains[k] *= WarpedGain(ar2, warping, ShapingLpcOrder);

            SilkFlp.BwExpander(ar2, ShapingLpcOrder, bwExp2);
            ar2.Slice(0, ShapingLpcOrder).CopyTo(ar1);
            SilkFlp.BwExpander(ar1, ShapingLpcOrder, bwExp1);

            float preNrg = SilkFlp.LpcInversePredGain(ar2, ShapingLpcOrder);
            nrg = SilkFlp.LpcInversePredGain(ar1, ShapingLpcOrder);
            c.GainsPre[k] = 1.0f - 0.7f * (1.0f - preNrg / nrg);

            WarpedTrue2MonicCoefs(ar2, ar1, warping, 3.999f, ShapingLpcOrder);
        }

        float gainMult = (float)Math.Pow(2.0f, -0.16f * snrAdjDb);
        float gainAdd = (float)Math.Pow(2.0f, 0.16f * 2);
        for (int k = 0; k < NbSubfr; k++)
        {
            c.Gains[k] *= gainMult;
            c.Gains[k] += gainAdd;
        }

        gainMult = 1.0f + 0.05f + c.CodingQuality * 0.1f;
        for (int k = 0; k < NbSubfr; k++) c.GainsPre[k] *= gainMult;

        strength = 4.0f * (1.0f + 0.5f * (InputQualityBandsQ15[0] * (1.0f / 32768.0f) - 1.0f));
        strength *= SpeechActivityQ8 * (1.0f / 256.0f);
        float tilt;
        if (Indices.SignalType == TypeVoiced)
        {
            for (int k = 0; k < NbSubfr; k++)
            {
                float b = 0.2f / FsKHz + 3.0f / c.PitchL[k];
                c.LfMaShp[k] = -1.0f + b;
                c.LfArShp[k] = 1.0f - b - b * strength;
            }
            tilt = -0.25f - (1 - 0.25f) * 0.35f * SpeechActivityQ8 * (1.0f / 256.0f);
        }
        else
        {
            float b = 1.3f / FsKHz;
            c.LfMaShp[0] = -1.0f + b;
            c.LfArShp[0] = 1.0f - b - b * strength * 0.6f;
            for (int k = 1; k < NbSubfr; k++)
            {
                c.LfMaShp[k] = c.LfMaShp[0];
                c.LfArShp[k] = c.LfArShp[0];
            }
            tilt = -0.25f;
        }

        float harmBoost = 0.1f * (1.0f - c.CodingQuality) * LtpCorr;
        harmBoost += 0.1f * (1.0f - c.InputQuality);

        float harmShapeGain;
        if (Indices.SignalType == TypeVoiced)
        {
            harmShapeGain = 0.3f;
            harmShapeGain += 0.2f * (1.0f - (1.0f - c.CodingQuality) * c.InputQuality);
            harmShapeGain *= (float)Math.Sqrt(LtpCorr);
        }
        else harmShapeGain = 0.0f;

        for (int k = 0; k < NbSubfr; k++)
        {
            ShapeHarmBoostSmth += 0.4f * (harmBoost - ShapeHarmBoostSmth);
            c.HarmBoost[k] = ShapeHarmBoostSmth;
            ShapeHarmShapeGainSmth += 0.4f * (harmShapeGain - ShapeHarmShapeGainSmth);
            c.HarmShapeGain[k] = ShapeHarmShapeGainSmth;
            ShapeTiltSmth += 0.4f * (tilt - ShapeTiltSmth);
            c.Tilt[k] = ShapeTiltSmth;
        }
    }

    private static void WarpedLpcAnalysisFilter(Span<float> state, Span<float> res, ReadOnlySpan<float> coef, ReadOnlySpan<float> input,
        float lambda, int length, int order)
    {
        for (int n = 0; n < length; n++)
        {
            float tmp2 = state[0] + lambda * state[1];
            state[0] = input[n];
            float tmp1 = state[1] + lambda * (state[2] - tmp2);
            state[1] = tmp2;
            float acc = coef[0] * tmp2;
            for (int i = 2; i < order; i += 2)
            {
                tmp2 = state[i] + lambda * (state[i + 1] - tmp1);
                state[i] = tmp1;
                acc += coef[i - 1] * tmp1;
                tmp1 = state[i + 1] + lambda * (state[i + 2] - tmp2);
                state[i + 1] = tmp2;
                acc += coef[i] * tmp2;
            }
            state[order] = tmp1;
            acc += coef[order - 1] * tmp1;
            res[n] = input[n] - acc;
        }
    }

    private readonly float[] _stRes = new float[MaxSubFrameLength + MaxLpcOrder];

    /// <summary>silk_prefilter_FLP: applies the noise-shaping analysis filter to the frame, producing the weighted signal.</summary>
    private void Prefilter(SilkEncCtrl c, Span<float> xw, int xOff)
    {
        Span<float> b = stackalloc float[2];
        Span<float> harmShapeFir = stackalloc float[3];
        var st = _stRes;
        int px = xOff, pxw = 0;
        int lag = PrefiltLagPrev;
        for (int k = 0; k < NbSubfr; k++)
        {
            if (Indices.SignalType == TypeVoiced) lag = c.PitchL[k];
            float harmShapeGain = c.HarmShapeGain[k] * (1.0f - c.HarmBoost[k]);
            harmShapeFir[0] = 0.25f * harmShapeGain;
            harmShapeFir[1] = 32767.0f / 65536.0f * harmShapeGain;
            harmShapeFir[2] = 0.25f * harmShapeGain;
            float tilt = c.Tilt[k];
            float lfMaShp = c.LfMaShp[k];
            float lfArShp = c.LfArShp[k];

            WarpedLpcAnalysisFilter(PrefiltSArShp, st, c.AR1.AsSpan(k * 16, 16), XBuf.AsSpan(px), (float)WarpingQ16 / 65536.0f,
                SubfrLength, ShapingLpcOrder);

            b[0] = c.GainsPre[k];
            b[1] = -c.GainsPre[k] * (c.HarmBoost[k] * harmShapeGain + 0.05f + c.CodingQuality * 0.1f);
            xw[pxw] = b[0] * st[0] + b[1] * PrefiltSHarmHp;
            for (int j = 1; j < SubfrLength; j++) xw[pxw + j] = b[0] * st[j] + b[1] * st[j - 1];
            PrefiltSHarmHp = st[SubfrLength - 1];

            Prefilt(xw.Slice(pxw), harmShapeFir, tilt, lfMaShp, lfArShp, lag, SubfrLength);
            px += SubfrLength;
            pxw += SubfrLength;
        }
        PrefiltLagPrev = c.PitchL[NbSubfr - 1];
    }

    private void Prefilt(Span<float> xw, ReadOnlySpan<float> harmShapeFir, float tilt, float lfMaShp, float lfArShp, int lag, int length)
    {
        const int ltpMask = LtpBufLength - 1;
        var buf = PrefiltSLtpShp;
        int bufIdx = PrefiltSLtpShpBufIdx;
        float sLfArShp = PrefiltSLfArShp;
        float sLfMaShp = PrefiltSLfMaShp;
        for (int i = 0; i < length; i++)
        {
            float nLtp;
            if (lag > 0)
            {
                int idx = lag + bufIdx;
                nLtp = buf[(idx - 3 / 2 - 1) & ltpMask] * harmShapeFir[0];
                nLtp += buf[(idx - 3 / 2) & ltpMask] * harmShapeFir[1];
                nLtp += buf[(idx - 3 / 2 + 1) & ltpMask] * harmShapeFir[2];
            }
            else nLtp = 0;
            float nTilt = sLfArShp * tilt;
            float nLf = sLfArShp * lfArShp + sLfMaShp * lfMaShp;
            sLfArShp = xw[i] - nTilt;
            sLfMaShp = sLfArShp - nLf;
            bufIdx = (bufIdx - 1) & ltpMask;
            buf[bufIdx] = sLfMaShp;
            xw[i] = sLfMaShp - nLtp;
        }
        PrefiltSLfArShp = sLfArShp;
        PrefiltSLfMaShp = sLfMaShp;
        PrefiltSLtpShpBufIdx = bufIdx;
    }

    /// <summary>silk_process_gains_FLP: scales and quantises the subframe gains and derives the rate-distortion trade-off.</summary>
    private void ProcessGains(SilkEncCtrl c, int condCoding)
    {
        Span<int> pGainsQ16 = stackalloc int[MaxNbSubfr];
        if (Indices.SignalType == TypeVoiced)
        {
            float s = 1.0f - 0.5f * SilkFlp.Sigmoid(0.25f * (c.LtpredCodGain - 12.0f));
            for (int k = 0; k < NbSubfr; k++) c.Gains[k] *= s;
        }

        float invMaxSqrVal = (float)(Math.Pow(2.0f, 0.33f * (21.0f - SnrDbQ7 * (1 / 128.0f))) / SubfrLength);
        for (int k = 0; k < NbSubfr; k++)
        {
            float gain = c.Gains[k];
            gain = (float)Math.Sqrt(gain * gain + c.ResNrg[k] * invMaxSqrVal);
            c.Gains[k] = gain < 32767.0f ? gain : 32767.0f;
        }

        for (int k = 0; k < NbSubfr; k++) pGainsQ16[k] = (int)(c.Gains[k] * 65536.0f);
        for (int k = 0; k < NbSubfr; k++) c.GainsUnqQ16[k] = pGainsQ16[k];
        c.LastGainIndexPrev = ShapeLastGainIndex;

        GainsQuant(Indices.GainsIndices, pGainsQ16, ref ShapeLastGainIndex, condCoding == CodeConditionally, NbSubfr);
        for (int k = 0; k < NbSubfr; k++) c.Gains[k] = pGainsQ16[k] / 65536.0f;

        if (Indices.SignalType == TypeVoiced)
        {
            Indices.QuantOffsetType = (sbyte)(c.LtpredCodGain + InputTiltQ15 * (1.0f / 32768.0f) > 1.0f ? 0 : 1);
        }

        float quantOffset = SilkTables.QuantizationOffsetsQ10[(Indices.SignalType >> 1) * 2 + Indices.QuantOffsetType] / 1024.0f;
        c.Lambda = 1.2f
            + -0.05f * NStatesDelayedDecision
            + -0.2f * SpeechActivityQ8 * (1.0f / 256.0f)
            + -0.1f * c.InputQuality
            + -0.2f * c.CodingQuality
            + 0.8f * quantOffset;
    }
}
