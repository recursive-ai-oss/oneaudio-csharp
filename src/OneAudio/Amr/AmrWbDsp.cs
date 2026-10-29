// Copyright (c) Recursive AI, LLC. MIT OR Apache-2.0.
namespace OneAudio.Amr;

// Synthesis filters, de-emphasis, band filters, 12.8 -> 16 kHz oversampling and ISF extrapolation of the AMR wideband
// decoder (TS 26.190 sections 6.2-6.3; TS 26.173 syn_filt.c, deemph.c, hp50.c, hp400.c, decim54.c, hp6k.c, hp7k.c,
// isfextrp.c).
internal sealed partial class AmrWbCore
{
    private const int FirLen = 31;

    /// <summary>Syn_filt_32: 1/A(z) in extended precision. Output is split into high and low 16-bit parts.</summary>
    private void SynFilt32(short[] a, int aOff, int m, short[] exc, int excOff, short qNew,
        short[] hi, int hiOff, short[] lo, int loOff, int lg)
    {
        short s = Sub(NormS(a[aOff]), 2);
        short a0 = Shr(a[aOff], Add(4, qNew));
        for (int i = 0; i < lg; i++)
        {
            int lTmp = 0;
            for (int j = 1; j <= m; j++) lTmp = LMsu(lTmp, lo[loOff + i - j], a[aOff + j]);
            lTmp = LShr(lTmp, 16 - 4);
            lTmp = LMac(lTmp, exc[excOff + i], a0);
            for (int j = 1; j <= m; j++) lTmp = LMsu(lTmp, hi[hiOff + i - j], a[aOff + j]);
            lTmp = LShl(lTmp, Add(3, s));
            hi[hiOff + i] = ExtractH(lTmp);
            lTmp = LShr(lTmp, 4);
            lo[loOff + i] = ExtractL(LMsu(lTmp, hi[hiOff + i], 2048));
        }
    }

    /// <summary>Syn_filt: 1/A(z) on 16-bit data with the filter memory in <paramref name="mem"/> at <paramref name="memOff"/>.</summary>
    private void SynFilt(short[] a, int m, short[] x, short[] y, int lg, short[] mem, int memOff, bool update)
    {
        short[] yy = _synBuf;
        for (int i = 0; i < m; i++) yy[i] = mem[memOff + i];
        short s = Sub(NormS(a[0]), 2);
        short a0 = Shr(a[0], 1);
        for (int i = 0; i < lg; i++)
        {
            int lTmp = LMult(x[i], a0);
            for (int j = 1; j <= m; j++) lTmp = LMsu(lTmp, a[j], yy[m + i - j]);
            lTmp = LShl(lTmp, Add(3, s));
            short r = Round(lTmp);
            y[i] = r;
            yy[m + i] = r;
        }
        if (update)
        {
            for (int i = 0; i < m; i++) mem[memOff + i] = yy[lg + i];
        }
    }

    /// <summary>Deemph_32: first-order de-emphasis 1/(1 - mu z^-1) of the split signal, state in <c>_memDeemph</c>.</summary>
    private void Deemph32(short[] hi, int hiOff, short[] lo, int loOff, short[] y, short mu, int len)
    {
        short fac = Shr(mu, 1);
        int lTmp = LDepositH(hi[hiOff]);
        lTmp = LMac(lTmp, lo[loOff], 8);
        lTmp = LShl(lTmp, 3);
        lTmp = LMac(lTmp, _memDeemph, fac);
        lTmp = LShl(lTmp, 1);
        y[0] = Round(lTmp);
        for (int i = 1; i < len; i++)
        {
            lTmp = LDepositH(hi[hiOff + i]);
            lTmp = LMac(lTmp, lo[loOff + i], 8);
            lTmp = LShl(lTmp, 3);
            lTmp = LMac(lTmp, y[i - 1], fac);
            lTmp = LShl(lTmp, 1);
            y[i] = Round(lTmp);
        }
        _memDeemph = y[len - 1];
    }

    /// <summary>Second-order IIR in double precision shared by the two high-pass filters (hp50.c, hp400.c).</summary>
    private void HighPass(short[] signal, int lg, short[] mem, short b0, short b1, short b2, short a1, short a2, int outShift, bool extraShift)
    {
        short y2Hi = mem[0], y2Lo = mem[1], y1Hi = mem[2], y1Lo = mem[3], x0 = mem[4], x1 = mem[5];
        for (int i = 0; i < lg; i++)
        {
            short x2 = x1;
            x1 = x0;
            x0 = signal[i];
            int lTmp = 16384;
            lTmp = LMac(lTmp, y1Lo, a1);
            lTmp = LMac(lTmp, y2Lo, a2);
            lTmp = LShr(lTmp, 15);
            lTmp = LMac(lTmp, y1Hi, a1);
            lTmp = LMac(lTmp, y2Hi, a2);
            lTmp = LMac(lTmp, x0, b0);
            lTmp = LMac(lTmp, x1, b1);
            lTmp = LMac(lTmp, x2, b2);
            lTmp = LShl(lTmp, (short)outShift);
            y2Hi = y1Hi;
            y2Lo = y1Lo;
            LExtract(lTmp, out y1Hi, out y1Lo);
            if (extraShift) lTmp = LShl(lTmp, 1);
            signal[i] = Round(lTmp);
        }
        mem[0] = y2Hi;
        mem[1] = y2Lo;
        mem[2] = y1Hi;
        mem[3] = y1Lo;
        mem[4] = x0;
        mem[5] = x1;
    }

    /// <summary>HP50_12k8: 50 Hz high-pass on the 12.8 kHz signal (TS 26.190 section 6.2).</summary>
    private void Hp50(short[] signal, int lg) =>
        HighPass(signal, lg, _memSigOut, 4053, -8106, 4053, 16211, -8021, 2, true);

    /// <summary>HP400_12k8: 400 Hz high-pass used to derive the high-band gain from the low band.</summary>
    private void Hp400(short[] signal, int lg) =>
        HighPass(signal, lg, _memHp400, 915, -1830, 915, 29280, -14160, 1, false);

    /// <summary>Oversamp_16k: 12.8 kHz to 16 kHz by 5/4 polyphase interpolation (TS 26.190 section 6.2).</summary>
    private void Oversamp16k(short[] sig12k8, int lg, short[] sig16k, int outOff)
    {
        const int NbCoefUp = 12;
        short[] signal = _sig;
        Array.Copy(_memOversamp, signal, 2 * NbCoefUp);
        Array.Copy(sig12k8, 0, signal, 2 * NbCoefUp, lg);
        int lgUp = Shl(Mult(lg, 20480), 1);
        short[] fir = AmrWbTables.FirUp;
        int pos = 0;
        for (int j = 0; j < lgUp; j++)
        {
            int i = Mult(pos, 6554);
            int frac = pos - (i * 5);
            // Interpol: 24 taps centred on sample i + NbCoefUp of the buffer, phase selected by frac.
            int xBase = NbCoefUp + i - NbCoefUp + 1;
            int k = 5 - 1 - frac;
            int sum = 0;
            for (int t = 0; t < 2 * NbCoefUp; t++, k += 5) sum = LMac(sum, signal[xBase + t], fir[k]);
            sum = LShl(sum, 1);
            sig16k[outOff + j] = Round(sum);
            pos += 4;
        }
        Array.Copy(signal, lg, _memOversamp, 0, 2 * NbCoefUp);
    }

    private void FirFilter(short[] signal, int lg, short[] mem, short[] fir, bool shiftInput)
    {
        short[] x = _sig;
        Array.Copy(mem, x, FirLen - 1);
        for (int i = 0; i < lg; i++) x[i + FirLen - 1] = shiftInput ? Shr(signal[i], 2) : signal[i];
        for (int i = 0; i < lg; i++)
        {
            int lTmp = 0;
            for (int j = 0; j < FirLen; j++) lTmp = LMac(lTmp, x[i + j], fir[j]);
            signal[i] = Round(lTmp);
        }
        Array.Copy(x, lg, mem, 0, FirLen - 1);
    }

    /// <summary>Filt_6k_7k: band-pass 6-7 kHz applied to the synthesized high-band noise.</summary>
    private void Filt6k7k(short[] signal, int lg) => FirFilter(signal, lg, _memHf, AmrWbTables.Fir6k7k, true);

    /// <summary>Filt_7k: 7 kHz low-pass used at the highest mode.</summary>
    private void Filt7k(short[] signal, int lg) => FirFilter(signal, lg, _memHf3, AmrWbTables.Fir7k, false);

    /// <summary>Isf_Extrapolation (TS 26.190 section 6.3.2): extends the 16 low-band ISFs to 20 for the 6.6 kbit/s high band.</summary>
    private void IsfExtrapolation(short[] hfIsf)
    {
        Span<short> isfDiff = stackalloc short[M - 2];
        Span<int> isfCorr = stackalloc int[3];
        hfIsf[M16k - 1] = hfIsf[M - 1];
        for (int i = 1; i < M - 1; i++) isfDiff[i - 1] = Sub(hfIsf[i], hfIsf[i - 1]);
        int lTmp = 0;
        for (int i = 3; i < M - 1; i++) lTmp = LMac(lTmp, isfDiff[i - 1], 2731);
        short mean = Round(lTmp);
        isfCorr.Clear();
        short tmp = 0;
        for (int i = 0; i < M - 2; i++)
            if (Sub(isfDiff[i], tmp) > 0) tmp = isfDiff[i];
        short exp = NormS(tmp);
        for (int i = 0; i < M - 2; i++) isfDiff[i] = Shl(isfDiff[i], exp);
        mean = Shl(mean, exp);
        for (int lag = 2; lag <= 4; lag++)
        {
            for (int i = 7; i < M - 2; i++)
            {
                short tmp2 = Sub(isfDiff[i], mean);
                short tmp3 = Sub(isfDiff[i - lag], mean);
                lTmp = LMult(tmp2, tmp3);
                LExtract(lTmp, out short hi, out short lo);
                lTmp = Mpy3232(hi, lo, hi, lo);
                isfCorr[lag - 2] = LAdd(isfCorr[lag - 2], lTmp);
            }
        }
        int maxCorr = LSub(isfCorr[0], isfCorr[1]) > 0 ? 0 : 1;
        if (LSub(isfCorr[2], isfCorr[maxCorr]) > 0) maxCorr = 2;
        maxCorr++;
        for (int i = M - 1; i < M16k - 1; i++)
        {
            tmp = Sub(hfIsf[i - 1 - maxCorr], hfIsf[i - 2 - maxCorr]);
            hfIsf[i] = Add(hfIsf[i - 1], tmp);
        }
        tmp = Add(hfIsf[4], hfIsf[3]);
        tmp = Sub(hfIsf[2], tmp);
        tmp = Mult(tmp, 5461);
        tmp = Add(tmp, 20390);
        if (Sub(tmp, 19456) > 0) tmp = 19456;
        tmp = Sub(tmp, hfIsf[M - 2]);
        short tmp2b = Sub(hfIsf[M16k - 2], hfIsf[M - 2]);
        short exp2 = NormS(tmp2b);
        exp = NormS(tmp);
        exp = Sub(exp, 1);
        tmp = Shl(tmp, exp);
        tmp2b = Shl(tmp2b, exp2);
        short coeff = DivS(tmp, tmp2b);
        exp = Sub(exp2, exp);
        for (int i = M - 1; i < M16k - 1; i++)
        {
            tmp = Mult(Sub(hfIsf[i], hfIsf[i - 1]), coeff);
            isfDiff[i - (M - 1)] = Shl(tmp, exp);
        }
        for (int i = M; i < M16k - 1; i++)
        {
            tmp = Sub(Add(isfDiff[i - (M - 1)], isfDiff[i - M]), 1280);
            if (tmp < 0)
            {
                if (Sub(isfDiff[i - (M - 1)], isfDiff[i - M]) > 0) isfDiff[i - M] = Sub(1280, isfDiff[i - (M - 1)]);
                else isfDiff[i - (M - 1)] = Sub(1280, isfDiff[i - M]);
            }
        }
        for (int i = M - 1; i < M16k - 1; i++) hfIsf[i] = Add(hfIsf[i - 1], isfDiff[i - (M - 1)]);
        for (int i = 0; i < M16k - 1; i++) hfIsf[i] = Mult(hfIsf[i], 26214);
        IsfToIsp(hfIsf, hfIsf, M16k);
    }
}
