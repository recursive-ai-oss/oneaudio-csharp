using static OneAudio.Opus.SilkMath;
using static OneAudio.Opus.SilkEncMath;

namespace OneAudio.Opus;

internal sealed partial class SilkEncState
{
    private static readonly int[] TiltWeights = [30000, 6000, -12000, -12000];
    private readonly short[] _vadX = new short[4 * (MaxFrameLength / 2)];

    private void VadInit()
    {
        for (int b = 0; b < VadNBands; b++)
            VadNoiseLevelBias[b] = Math.Max(50 / (b + 1), 1);
        for (int b = 0; b < VadNBands; b++)
        {
            VadNl[b] = 100 * VadNoiseLevelBias[b];
            VadInvNl[b] = int.MaxValue / VadNl[b];
        }
        VadCounter = 15;
        for (int b = 0; b < VadNBands; b++) VadNrgRatioSmthQ8[b] = 100 * 256;
    }

    /// <summary>silk_ana_filt_bank_1: two-band split by a pair of all-pass sections.</summary>
    private static void AnaFiltBank1(Span<short> inp, int inOff, Span<int> s, Span<short> outL, int lOff, Span<short> outH, int hOff, int n)
    {
        const int a20 = 5394 << 1;
        const int a21 = -24290;
        int n2 = n >> 1;
        for (int k = 0; k < n2; k++)
        {
            int in32 = inp[inOff + 2 * k] << 10;
            int y = in32 - s[0];
            int x = SMlaWB(y, y, a21);
            int out1 = s[0] + x;
            s[0] = in32 + x;

            in32 = inp[inOff + 2 * k + 1] << 10;
            y = in32 - s[1];
            x = SMulWB(y, a20);
            int out2 = s[1] + x;
            s[1] = in32 + x;

            outL[lOff + k] = (short)Sat16(RShiftRound(out2 + out1, 11));
            outH[hOff + k] = (short)Sat16(RShiftRound(out2 - out1, 11));
        }
    }

    /// <summary>silk_VAD_GetSA_Q8: speech activity and band qualities for the current frame (RFC 6716 reference encoder).</summary>
    private void VadGetSa(ReadOnlySpan<short> pIn)
    {
        const int h = MaxFrameLength / 2;
        var x = _vadX;
        Span<int> xnrg = stackalloc int[4];
        Span<int> nrgToNoiseRatioQ8 = stackalloc int[4];

        // The filter bank works in place on x, so the input is first copied (the C code reads pIn directly for the first stage).
        var tmpIn = _vadIn;
        pIn.Slice(0, FrameLength).CopyTo(tmpIn);
        AnaFiltBank1(tmpIn, 0, VadAnaState, x, 0, x, 3 * h, FrameLength);
        AnaFiltBank1(x, 0, VadAnaState1, x, 0, x, 2 * h, FrameLength >> 1);
        AnaFiltBank1(x, 0, VadAnaState2, x, 0, x, 1 * h, FrameLength >> 2);

        int decimatedFramelength = FrameLength >> 3;
        x[decimatedFramelength - 1] = (short)(x[decimatedFramelength - 1] >> 1);
        short hpStateTmp = x[decimatedFramelength - 1];
        for (int i = decimatedFramelength - 1; i > 0; i--)
        {
            x[i - 1] = (short)(x[i - 1] >> 1);
            x[i] -= x[i - 1];
        }
        x[0] -= VadHpState;
        VadHpState = hpStateTmp;

        int sumSquared = 0;
        for (int b = 0; b < VadNBands; b++)
        {
            decimatedFramelength = FrameLength >> Math.Min(VadNBands - b, VadNBands - 1);
            int decSubframeLength = decimatedFramelength >> 2;
            int decSubframeOffset = 0;
            xnrg[b] = VadXnrgSubfr[b];
            for (int s = 0; s < 4; s++)
            {
                sumSquared = 0;
                for (int i = 0; i < decSubframeLength; i++)
                {
                    int xTmp = x[b * h + i + decSubframeOffset] >> 3;
                    sumSquared = SMlaBB(sumSquared, xTmp, xTmp);
                }
                if (s < 3) xnrg[b] = AddPosSat32(xnrg[b], sumSquared);
                else xnrg[b] = AddPosSat32(xnrg[b], sumSquared >> 1);
                decSubframeOffset += decSubframeLength;
            }
            VadXnrgSubfr[b] = sumSquared;
        }

        VadGetNoiseLevels(xnrg);

        sumSquared = 0;
        int inputTilt = 0;
        for (int b = 0; b < VadNBands; b++)
        {
            int speechNrg = xnrg[b] - VadNl[b];
            if (speechNrg > 0)
            {
                if ((xnrg[b] & unchecked((int)0xFF800000)) == 0)
                    nrgToNoiseRatioQ8[b] = (xnrg[b] << 8) / (VadNl[b] + 1);
                else
                    nrgToNoiseRatioQ8[b] = xnrg[b] / ((VadNl[b] >> 8) + 1);

                int snrQ7 = Lin2Log(nrgToNoiseRatioQ8[b]) - 8 * 128;
                sumSquared = SMlaBB(sumSquared, snrQ7, snrQ7);
                if (speechNrg < (1 << 20))
                    snrQ7 = SMulWB(SqrtApprox(speechNrg) << 6, snrQ7);
                inputTilt = SMlaWB(inputTilt, TiltWeights[b], snrQ7);
            }
            else
            {
                nrgToNoiseRatioQ8[b] = 256;
            }
        }

        sumSquared /= VadNBands;
        int pSnrDbQ7 = (short)(3 * SqrtApprox(sumSquared));
        int saQ15 = SigmQ15(SMulWB(45000, pSnrDbQ7) - 128);
        InputTiltQ15 = (SigmQ15(inputTilt) - 16384) << 1;

        int speechNrgSum = 0;
        for (int b = 0; b < VadNBands; b++)
            speechNrgSum += (b + 1) * ((xnrg[b] - VadNl[b]) >> 4);
        if (speechNrgSum <= 0)
        {
            saQ15 >>= 1;
        }
        else if (speechNrgSum < 32768)
        {
            speechNrgSum = FrameLength == 10 * FsKHz ? LShiftSat32(speechNrgSum, 16) : LShiftSat32(speechNrgSum, 15);
            speechNrgSum = SqrtApprox(speechNrgSum);
            saQ15 = SMulWB(32768 + speechNrgSum, saQ15);
        }

        SpeechActivityQ8 = Math.Min(saQ15 >> 7, 255);
        int smoothCoefQ16 = SMulWB(4096, SMulWB(saQ15, saQ15));
        if (FrameLength == 10 * FsKHz) smoothCoefQ16 >>= 1;

        for (int b = 0; b < VadNBands; b++)
        {
            VadNrgRatioSmthQ8[b] = SMlaWB(VadNrgRatioSmthQ8[b], nrgToNoiseRatioQ8[b] - VadNrgRatioSmthQ8[b], smoothCoefQ16);
            int snrQ7 = 3 * (Lin2Log(VadNrgRatioSmthQ8[b]) - 8 * 128);
            InputQualityBandsQ15[b] = SigmQ15((snrQ7 - 16 * 128) >> 4);
        }
    }

    private readonly short[] _vadIn = new short[MaxFrameLength];

    private void VadGetNoiseLevels(ReadOnlySpan<int> pX)
    {
        int minCoef = VadCounter < 1000 ? short.MaxValue / ((VadCounter >> 4) + 1) : 0;
        for (int k = 0; k < VadNBands; k++)
        {
            int nl = VadNl[k];
            int nrg = AddPosSat32(pX[k], VadNoiseLevelBias[k]);
            int invNrg = int.MaxValue / nrg;
            int coef;
            if (nrg > (nl << 3)) coef = 1024 >> 3;
            else if (nrg < nl) coef = 1024;
            else coef = SMulWB(SMulWW(invNrg, nl), 1024 << 1);
            coef = Math.Max(coef, minCoef);
            VadInvNl[k] = SMlaWB(VadInvNl[k], invNrg - VadInvNl[k], coef);
            nl = int.MaxValue / VadInvNl[k];
            nl = Math.Min(nl, 0x00FFFFFF);
            VadNl[k] = nl;
        }
        VadCounter++;
    }
}
