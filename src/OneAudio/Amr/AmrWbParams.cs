// Copyright (c) Recursive AI, LLC. MIT OR Apache-2.0.
namespace OneAudio.Amr;

// LPC (ISF/ISP) decoding, excitation codebooks, gains, lag concealment and signal processing helpers of the AMR
// wideband decoder: TS 26.190 sections 5-6, TS 26.191 (error concealment); TS 26.173 sources named per function.
internal sealed partial class AmrWbCore
{
    // ---------------------------------------------------------------------------------------------------------
    // Math library (TS 26.173 math_op.c, log2.c, random.c)

    private short Random(ref short seed)
    {
        seed = ExtractL(LAdd(LShr(LMult(seed, 31821), 1), 13849));
        return seed;
    }

    private void Log2Norm(int lX, short exp, out short exponent, out short fraction)
    {
        if (lX <= 0)
        {
            exponent = 0;
            fraction = 0;
            return;
        }
        exponent = Sub(30, exp);
        lX = LShr(lX, 9);
        short i = ExtractH(lX);
        lX = LShr(lX, 1);
        short a = ExtractL(lX);
        a = (short)(a & 0x7fff);
        i = Sub(i, 32);
        short[] table = AmrWbTables.Log2;
        int lY = LDepositH(table[i]);
        short tmp = Sub(table[i], table[i + 1]);
        lY = LMsu(lY, tmp, a);
        fraction = ExtractH(lY);
    }

    private void Log2(int lX, out short exponent, out short fraction)
    {
        short exp = NormL(lX);
        Log2Norm(LShl(lX, exp), exp, out exponent, out fraction);
    }

    private int Pow2(short exponent, short fraction)
    {
        int lX = LMult(fraction, 32);
        short i = ExtractH(lX);
        lX = LShr(lX, 1);
        short a = ExtractL(lX);
        a = (short)(a & 0x7fff);
        short[] table = AmrWbTables.Pow2;
        lX = LDepositH(table[i]);
        short tmp = Sub(table[i], table[i + 1]);
        lX = LMsu(lX, tmp, a);
        short exp = Sub(30, exponent);
        return LShrR(lX, exp);
    }

    private void IsqrtN(ref int frac, ref short exp)
    {
        if (frac <= 0)
        {
            exp = 0;
            frac = 0x7fffffff;
            return;
        }
        if ((short)(exp & 1) == 1) frac = LShr(frac, 1);
        exp = Negate(Shr(Sub(exp, 1), 1));
        frac = LShr(frac, 9);
        short i = ExtractH(frac);
        frac = LShr(frac, 1);
        short a = ExtractL(frac);
        a = (short)(a & 0x7fff);
        i = Sub(i, 16);
        short[] table = AmrWbTables.Isqrt;
        frac = LDepositH(table[i]);
        short tmp = Sub(table[i], table[i + 1]);
        frac = LMsu(frac, tmp, a);
    }

    private int Isqrt(int lX)
    {
        short exp = NormL(lX);
        lX = LShl(lX, exp);
        exp = Sub(31, exp);
        IsqrtN(ref lX, ref exp);
        return LShl(lX, exp);
    }

    private int DotProduct12(short[] x, int xOff, short[] y, int yOff, int lg, out short exp)
    {
        int lSum = 1;
        for (int i = 0; i < lg; i++) lSum = LMac(lSum, x[xOff + i], y[yOff + i]);
        short sft = NormL(lSum);
        lSum = LShl(lSum, sft);
        exp = Sub(30, sft);
        return lSum;
    }

    private void ScaleSig(short[] x, int off, int lg, short exp)
    {
        for (int i = 0; i < lg; i++)
        {
            int lTmp = LDepositH(x[off + i]);
            lTmp = LShl(lTmp, exp);
            x[off + i] = Round(lTmp);
        }
    }

    private static short Median5(short[] x, int off)
    {
        // The median of five values is the third smallest; a sort gives the same value as the reference's
        // comparison network.
        Span<short> v = stackalloc short[5];
        for (int i = 0; i < 5; i++) v[i] = x[off - 2 + i];
        for (int i = 1; i < 5; i++)
        {
            short key = v[i];
            int j = i - 1;
            while (j >= 0 && v[j] > key)
            {
                v[j + 1] = v[j];
                j--;
            }
            v[j + 1] = key;
        }
        return v[2];
    }

    // ---------------------------------------------------------------------------------------------------------
    // ISF / ISP conversion and interpolation (isp_isf.c, isp_az.c, int_lpc.c, weight_a.c)

    private void IsfToIsp(short[] isf, short[] isp, int m)
    {
        short[] table = AmrWbTables.IspIsf;
        for (int i = 0; i < m - 1; i++) isp[i] = isf[i];
        isp[m - 1] = Shl(isf[m - 1], 1);
        for (int i = 0; i < m; i++)
        {
            // Garbage ISFs from a damaged stream can exceed the table; clamp so the lookup stays in range (valid
            // streams never reach the clamp).
            short ind = Math.Min(Shr(isp[i], 7), (short)(table.Length - 2));
            if (ind < 0) ind = 0;
            short offset = (short)(isp[i] & 0x007f);
            int lTmp = LMult(Sub(table[ind + 1], table[ind]), offset);
            isp[i] = Add(table[ind], ExtractL(LShr(lTmp, 8)));
        }
    }

    private void GetIspPol(short[] isp, int ispOff, int[] f, int n, bool k16)
    {
        int fi = 0;
        f[fi++] = LMult(4096, k16 ? 256 : 1024);
        f[fi++] = LMult(isp[ispOff], k16 ? -64 : -256);
        ispOff += 2;
        for (int i = 2; i <= n; i++)
        {
            f[fi] = f[fi - 2];
            for (int j = 1; j < i; j++, fi--)
            {
                LExtract(f[fi - 1], out short hi, out short lo);
                int t0 = Mpy3216(hi, lo, isp[ispOff]);
                t0 = LShl(t0, 1);
                f[fi] = LSub(f[fi], t0);
                f[fi] = LAdd(f[fi], f[fi - 2]);
            }
            f[fi] = LMsu(f[fi], isp[ispOff], k16 ? 64 : 256);
            fi += i;
            ispOff += 2;
        }
    }

    /// <summary>Isp_Az: converts ISPs to predictor coefficients A(z) in Q12 (or scaled when adaptive).</summary>
    private void IspAz(short[] isp, short[] a, int aOff, int m, int adaptiveScaling)
    {
        int[] f1 = _f1;
        int[] f2 = _f2;
        int nc = m >> 1;
        if (nc > 8)
        {
            GetIspPol(isp, 0, f1, nc, true);
            for (int i = 0; i <= nc; i++) f1[i] = LShl(f1[i], 2);
            GetIspPol(isp, 1, f2, nc - 1, true);
            for (int i = 0; i <= nc - 1; i++) f2[i] = LShl(f2[i], 2);
        }
        else
        {
            GetIspPol(isp, 0, f1, nc, false);
            GetIspPol(isp, 1, f2, nc - 1, false);
        }

        for (int i = nc - 1; i > 1; i--) f2[i] = LSub(f2[i], f2[i - 2]);
        for (int i = 0; i < nc; i++)
        {
            LExtract(f1[i], out short hi, out short lo);
            int t0 = Mpy3216(hi, lo, isp[m - 1]);
            f1[i] = LAdd(f1[i], t0);
            LExtract(f2[i], out hi, out lo);
            t0 = Mpy3216(hi, lo, isp[m - 1]);
            f2[i] = LSub(f2[i], t0);
        }

        a[aOff] = 4096;
        int tmax = 1;
        for (int i = 1, j = m - 1; i < nc; i++, j--)
        {
            int t0 = LAdd(f1[i], f2[i]);
            tmax |= LAbs(t0);
            a[aOff + i] = ExtractL(LShrR(t0, 12));
            t0 = LSub(f1[i], f2[i]);
            tmax |= LAbs(t0);
            a[aOff + j] = ExtractL(LShrR(t0, 12));
        }

        short q;
        if (adaptiveScaling == 1) q = Sub(4, NormL(tmax));
        else q = 0;

        short qSug;
        if (q > 0)
        {
            qSug = Add(12, q);
            for (int i = 1, j = m - 1; i < nc; i++, j--)
            {
                int t0 = LAdd(f1[i], f2[i]);
                a[aOff + i] = ExtractL(LShrR(t0, qSug));
                t0 = LSub(f1[i], f2[i]);
                a[aOff + j] = ExtractL(LShrR(t0, qSug));
            }
            a[aOff] = Shr(a[aOff], q);
        }
        else
        {
            qSug = 12;
            q = 0;
        }

        LExtract(f1[nc], out short h, out short l);
        int tt = Mpy3216(h, l, isp[m - 1]);
        tt = LAdd(f1[nc], tt);
        a[aOff + nc] = ExtractL(LShrR(tt, qSug));
        a[aOff + m] = ShrR(isp[m - 1], Add(3, q));
    }

    private void IntIsp(short[] ispOld, short[] ispNew, short[] frac, short[] az)
    {
        int o = 0;
        short[] isp = _intIsp;
        for (int k = 0; k < 3; k++)
        {
            short facNew = frac[k];
            short facOld = Add(Sub(32767, facNew), 1);
            for (int i = 0; i < M; i++)
            {
                int lTmp = LMult(ispOld[i], facOld);
                lTmp = LMac(lTmp, ispNew[i], facNew);
                isp[i] = Round(lTmp);
            }
            IspAz(isp, az, o, M, 0);
            o += M + 1;
        }
        IspAz(ispNew, az, o, M, 0);
    }

    private void WeightA(short[] a, short[] ap, short gamma, int m) => WeightA(a, 0, ap, gamma, m);

    private void WeightA(short[] a, int aOff, short[] ap, short gamma, int m)
    {
        ap[0] = a[aOff];
        short fac = gamma;
        for (int i = 1; i < m; i++)
        {
            ap[i] = Round(LMult(a[aOff + i], fac));
            fac = Round(LMult(fac, gamma));
        }
        ap[m] = Round(LMult(a[aOff + m], fac));
    }

    // ---------------------------------------------------------------------------------------------------------
    // ISF vector dequantization (qpisf_2s.c, qisf_ns.c; TS 26.190 section 6.1)

    private void ReorderIsf(short[] isf, short minDist, int n)
    {
        short isfMin = minDist;
        for (int i = 0; i < n - 1; i++)
        {
            if (isf[i] < isfMin) isf[i] = isfMin;
            isfMin = Add(isf[i], minDist);
        }
    }

    private void Dpisf2s46b(short[] indice, short[] isfQ, short[] pastIsfq, short[] isfold, short[] isfBuf, short bfi, bool encDec)
    {
        DpisfCommon(indice, isfQ, pastIsfq, isfold, isfBuf, bfi, encDec, false);
    }

    private void Dpisf2s36b(short[] indice, short[] isfQ, short[] pastIsfq, short[] isfold, short[] isfBuf, short bfi, bool encDec)
    {
        DpisfCommon(indice, isfQ, pastIsfq, isfold, isfBuf, bfi, encDec, true);
    }

    private readonly short[] _refIsf = new short[M];

    private void DpisfCommon(short[] indice, short[] isfQ, short[] pastIsfq, short[] isfold, short[] isfBuf, short bfi, bool encDec, bool is36)
    {
        const short mu = 10923;
        const short alpha = 29491;
        const short oneAlpha = 32768 - 29491;
        const short isfGap = 128;
        short[] meanIsf = AmrWbTables.MeanIsf;
        if (bfi == 0)
        {
            short[] d1 = AmrWbTables.Dico1Isf;
            short[] d2 = AmrWbTables.Dico2Isf;
            for (int i = 0; i < 9; i++) isfQ[i] = d1[indice[0] * 9 + i];
            for (int i = 0; i < 7; i++) isfQ[i + 9] = d2[indice[1] * 7 + i];
            if (!is36)
            {
                short[] d21 = AmrWbTables.Dico21Isf, d22 = AmrWbTables.Dico22Isf, d23 = AmrWbTables.Dico23Isf,
                    d24 = AmrWbTables.Dico24Isf, d25 = AmrWbTables.Dico25Isf;
                for (int i = 0; i < 3; i++) isfQ[i] = Add(isfQ[i], d21[indice[2] * 3 + i]);
                for (int i = 0; i < 3; i++) isfQ[i + 3] = Add(isfQ[i + 3], d22[indice[3] * 3 + i]);
                for (int i = 0; i < 3; i++) isfQ[i + 6] = Add(isfQ[i + 6], d23[indice[4] * 3 + i]);
                for (int i = 0; i < 3; i++) isfQ[i + 9] = Add(isfQ[i + 9], d24[indice[5] * 3 + i]);
                for (int i = 0; i < 4; i++) isfQ[i + 12] = Add(isfQ[i + 12], d25[indice[6] * 4 + i]);
            }
            else
            {
                short[] d21 = AmrWbTables.Dico21Isf36, d22 = AmrWbTables.Dico22Isf36, d23 = AmrWbTables.Dico23Isf36;
                for (int i = 0; i < 5; i++) isfQ[i] = Add(isfQ[i], d21[indice[2] * 5 + i]);
                for (int i = 0; i < 4; i++) isfQ[i + 5] = Add(isfQ[i + 5], d22[indice[3] * 4 + i]);
                for (int i = 0; i < 7; i++) isfQ[i + 9] = Add(isfQ[i + 9], d23[indice[4] * 7 + i]);
            }

            for (int i = 0; i < M; i++)
            {
                short tmp = isfQ[i];
                isfQ[i] = Add(tmp, meanIsf[i]);
                isfQ[i] = Add(isfQ[i], Mult(mu, pastIsfq[i]));
                pastIsfq[i] = tmp;
            }

            if (encDec)
            {
                for (int i = 0; i < M; i++)
                {
                    for (int j = LMeanBuf - 1; j > 0; j--) isfBuf[j * M + i] = isfBuf[(j - 1) * M + i];
                    isfBuf[i] = isfQ[i];
                }
            }
        }
        else
        {
            short[] refIsf = _refIsf;
            for (int i = 0; i < M; i++)
            {
                int lTmp = LMult(meanIsf[i], 8192);
                for (int j = 0; j < LMeanBuf; j++) lTmp = LMac(lTmp, isfBuf[j * M + i], 8192);
                refIsf[i] = Round(lTmp);
            }
            for (int i = 0; i < M; i++) isfQ[i] = Add(Mult(alpha, isfold[i]), Mult(oneAlpha, refIsf[i]));
            for (int i = 0; i < M; i++)
            {
                short tmp = Add(refIsf[i], Mult(pastIsfq[i], mu));
                pastIsfq[i] = Sub(isfQ[i], tmp);
                pastIsfq[i] = Shr(pastIsfq[i], 1);
            }
        }
        ReorderIsf(isfQ, isfGap, M);
    }

    private void DisfNs(short[] indice, short[] isfQ)
    {
        short[] mean = AmrWbTables.MeanIsfNoise;
        for (int i = 0; i < 2; i++) isfQ[i] = AmrWbTables.Dico1IsfNoise[indice[0] * 2 + i];
        for (int i = 0; i < 3; i++) isfQ[i + 2] = AmrWbTables.Dico2IsfNoise[indice[1] * 3 + i];
        for (int i = 0; i < 3; i++) isfQ[i + 5] = AmrWbTables.Dico3IsfNoise[indice[2] * 3 + i];
        for (int i = 0; i < 4; i++) isfQ[i + 8] = AmrWbTables.Dico4IsfNoise[indice[3] * 4 + i];
        for (int i = 0; i < 4; i++) isfQ[i + 12] = AmrWbTables.Dico5IsfNoise[indice[4] * 4 + i];
        for (int i = 0; i < M; i++) isfQ[i] = Add(isfQ[i], mean[i]);
        ReorderIsf(isfQ, 128, M);
    }

    // ---------------------------------------------------------------------------------------------------------
    // Excitation: algebraic codebook (d2t64fx.c, d4t64fx.c, q_pulse.c), adaptive codebook (pred_lt4.c)

    private void PredLt4(short[] exc, int basis, short t0, short frac, int lSubfr)
    {
        short[] inter = AmrWbTables.Inter4;
        int x = basis - t0;
        frac = Negate(frac);
        if (frac < 0)
        {
            frac = Add(frac, 4);
            x--;
        }
        x = x - 16 + 1;
        for (int j = 0; j < lSubfr; j++)
        {
            int lSum = 0;
            for (int i = 0, k = 4 - 1 - frac; i < 32; i++, k += 4) lSum = LMac(lSum, exc[x + i], inter[k]);
            lSum = LShl(lSum, 1);
            exc[basis + j] = Round(lSum);
            x++;
        }
    }

    private void DecAcelp2t64(short index, short[] code)
    {
        for (int i = 0; i < LSubfr; i++) code[i] = 0;
        short i0 = (short)(Shr(index, 5) & 0x003E);
        short i1 = Add(Shl((short)(index & 0x001F), 1), 1);
        if ((Shr(index, 6) & 32) == 0) code[i0] = 512;
        else code[i0] = -512;
        if ((index & 32) == 0) code[i1] = 512;
        else code[i1] = -512;
    }

    private void Dec1pN1(int index, short n, short offset, short[] pos, int po)
    {
        int mask = LDepositL(Sub(Shl(1, n), 1));
        short pos1 = Add(ExtractL(index & mask), offset);
        int i = LShr(index, n) & 1;
        if (i == 1) pos1 = Add(pos1, 16);
        pos[po] = pos1;
    }

    private void Dec2p2N1(int index, short n, short offset, short[] pos, int po)
    {
        int mask = LDepositL(Sub(Shl(1, n), 1));
        short pos1 = ExtractL(LAdd(LShr(index, n) & mask, LDepositL(offset)));
        short tmp = Shl(n, 1);
        int i = LShr(index, tmp) & 1;
        short pos2 = Add(ExtractL(index & mask), offset);
        if (pos2 < pos1)
        {
            if (i == 1) pos1 = Add(pos1, 16);
            else pos2 = Add(pos2, 16);
        }
        else if (i == 1)
        {
            pos1 = Add(pos1, 16);
            pos2 = Add(pos2, 16);
        }
        pos[po] = pos1;
        pos[po + 1] = pos2;
    }

    private void Dec3p3N1(int index, short n, short offset, short[] pos, int po)
    {
        short tmp = Sub(Shl(n, 1), 1);
        int mask = LSub(LShl(1, tmp), 1);
        int idx = index & mask;
        short j = offset;
        tmp = Sub(Shl(n, 1), 1);
        if ((LShr(index, tmp) & 1) != 0) j = Add(j, Shl(1, Sub(n, 1)));
        Dec2p2N1(idx, (short)(n - 1), j, pos, po);
        mask = Sub(Shl(1, Add(n, 1)), 1);
        tmp = Shl(n, 1);
        idx = LShr(index, tmp) & mask;
        Dec1pN1(idx, n, offset, pos, po + 2);
    }

    private void Dec4p4N1(int index, short n, short offset, short[] pos, int po)
    {
        short tmp = Sub(Shl(n, 1), 1);
        int mask = LSub(LShl(1, tmp), 1);
        int idx = index & mask;
        short j = offset;
        tmp = Sub(Shl(n, 1), 1);
        if ((LShr(index, tmp) & 1) != 0) j = Add(j, Shl(1, Sub(n, 1)));
        Dec2p2N1(idx, (short)(n - 1), j, pos, po);
        tmp = Add(Shl(n, 1), 1);
        mask = LSub(LShl(1, tmp), 1);
        idx = LShr(index, Shl(n, 1)) & mask;
        Dec2p2N1(idx, n, offset, pos, po + 2);
    }

    private void Dec4p4N(int index, short n, short offset, short[] pos, int po)
    {
        short n1 = (short)(n - 1);
        short j = Add(offset, Shl(1, n1));
        short tmp = Sub(Shl(n, 2), 2);
        switch (LShr(index, tmp) & 3)
        {
            case 0:
                tmp = Add(Shl(n1, 2), 1);
                if ((LShr(index, tmp) & 1) == 0) Dec4p4N1(index, n1, offset, pos, po);
                else Dec4p4N1(index, n1, j, pos, po);
                break;
            case 1:
                tmp = Add(ExtractL(LShr(LMult(3, n1), 1)), 1);
                Dec1pN1(LShr(index, tmp), n1, offset, pos, po);
                Dec3p3N1(index, n1, j, pos, po + 1);
                break;
            case 2:
                tmp = Add(Shl(n1, 1), 1);
                Dec2p2N1(LShr(index, tmp), n1, offset, pos, po);
                Dec2p2N1(index, n1, j, pos, po + 2);
                break;
            default:
                tmp = Add(n1, 1);
                Dec3p3N1(LShr(index, tmp), n1, offset, pos, po);
                Dec1pN1(index, n1, j, pos, po + 3);
                break;
        }
    }

    private void Dec5p5N(int index, short n, short offset, short[] pos, int po)
    {
        short n1 = (short)(n - 1);
        short j = Add(offset, Shl(1, n1));
        short tmp = Add(Shl(n, 1), 1);
        int idx = LShr(index, tmp);
        tmp = Sub(ExtractL(LShr(LMult(5, n), 1)), 1);
        if ((LShr(index, tmp) & 1) == 0)
        {
            Dec3p3N1(idx, n1, offset, pos, po);
            Dec2p2N1(index, n, offset, pos, po + 3);
        }
        else
        {
            Dec3p3N1(idx, n1, j, pos, po);
            Dec2p2N1(index, n, offset, pos, po + 3);
        }
    }

    private void Dec6p6N2(int index, short n, short offset, short[] pos, int po)
    {
        short n1 = (short)(n - 1);
        short j = Add(offset, Shl(1, n1));
        short offsetA = j, offsetB = j;
        if ((LShr(index, (short)(6 * n - 5)) & 1) == 0) offsetA = offset;
        else offsetB = offset;
        switch (LShr(index, (short)(6 * n - 4)) & 3)
        {
            case 0:
                Dec5p5N(LShr(index, n), n1, offsetA, pos, po);
                Dec1pN1(index, n1, offsetA, pos, po + 5);
                break;
            case 1:
                Dec5p5N(LShr(index, n), n1, offsetA, pos, po);
                Dec1pN1(index, n1, offsetB, pos, po + 5);
                break;
            case 2:
                Dec4p4N(LShr(index, (short)(2 * n1 + 1)), n1, offsetA, pos, po);
                Dec2p2N1(index, n1, offsetB, pos, po + 4);
                break;
            default:
                Dec3p3N1(LShr(index, (short)(3 * n1 + 1)), n1, offset, pos, po);
                Dec3p3N1(index, n1, j, pos, po + 3);
                break;
        }
    }

    private void AddPulses(short[] pos, int nbPulse, int track, short[] code)
    {
        for (int k = 0; k < nbPulse; k++)
        {
            short i = Add(Shl((short)(pos[k] & 15), 2), (short)track);
            if ((pos[k] & 16) == 0) code[i] = Add(code[i], 512);
            else code[i] = Sub(code[i], 512);
        }
    }

    private void DecAcelp4t64(short[] index, short nbbits, short[] code)
    {
        short[] pos = _pos;
        for (int i = 0; i < LSubfr; i++) code[i] = 0;
        if (nbbits == 20)
        {
            for (int k = 0; k < 4; k++)
            {
                Dec1pN1(index[k], 4, 0, pos, 0);
                AddPulses(pos, 1, k, code);
            }
        }
        else if (nbbits == 36)
        {
            for (int k = 0; k < 4; k++)
            {
                Dec2p2N1(index[k], 4, 0, pos, 0);
                AddPulses(pos, 2, k, code);
            }
        }
        else if (nbbits == 44)
        {
            for (int k = 0; k < 2; k++)
            {
                Dec3p3N1(index[k], 4, 0, pos, 0);
                AddPulses(pos, 3, k, code);
            }
            for (int k = 2; k < 4; k++)
            {
                Dec2p2N1(index[k], 4, 0, pos, 0);
                AddPulses(pos, 2, k, code);
            }
        }
        else if (nbbits == 52)
        {
            for (int k = 0; k < 4; k++)
            {
                Dec3p3N1(index[k], 4, 0, pos, 0);
                AddPulses(pos, 3, k, code);
            }
        }
        else if (nbbits == 64)
        {
            for (int k = 0; k < 4; k++)
            {
                int lIndex = LAdd(LShl(index[k], 14), index[k + 4]);
                Dec4p4N(lIndex, 4, 0, pos, 0);
                AddPulses(pos, 4, k, code);
            }
        }
        else if (nbbits == 72)
        {
            for (int k = 0; k < 2; k++)
            {
                int lIndex = LAdd(LShl(index[k], 10), index[k + 4]);
                Dec5p5N(lIndex, 4, 0, pos, 0);
                AddPulses(pos, 5, k, code);
            }
            for (int k = 2; k < 4; k++)
            {
                int lIndex = LAdd(LShl(index[k], 14), index[k + 4]);
                Dec4p4N(lIndex, 4, 0, pos, 0);
                AddPulses(pos, 4, k, code);
            }
        }
        else if (nbbits == 88)
        {
            for (int k = 0; k < 4; k++)
            {
                int lIndex = LAdd(LShl(index[k], 11), index[k + 4]);
                Dec6p6N2(lIndex, 4, 0, pos, 0);
                AddPulses(pos, 6, k, code);
            }
        }
    }

    private void Preemph(short[] x, short mu, int lg, ref short mem)
    {
        short temp = x[lg - 1];
        for (int i = lg - 1; i > 0; i--)
        {
            int lTmp = LDepositH(x[i]);
            lTmp = LMsu(lTmp, x[i - 1], mu);
            x[i] = Round(lTmp);
        }
        int l = LDepositH(x[0]);
        l = LMsu(l, mem, mu);
        x[0] = Round(l);
        mem = temp;
    }

    private void PreemphMem(short[] x, short mu, int lg, ref short mem) => Preemph(x, mu, lg, ref mem);

    private void PitShrp(short[] x, short pitLag, short sharp, int lSubfr)
    {
        for (int i = pitLag; i < lSubfr; i++)
        {
            int lTmp = LDepositH(x[i]);
            lTmp = LMac(lTmp, x[i - pitLag], sharp);
            x[i] = Round(lTmp);
        }
    }

    // ---------------------------------------------------------------------------------------------------------
    // Gains (d_gain2.c), voice factor, phase dispersion, AGC

    private void InitDGain2()
    {
        short[] mem = _decGain;
        mem[0] = -14336;
        mem[1] = -14336;
        mem[2] = -14336;
        mem[3] = -14336;
        for (int i = 4; i < 22; i++) mem[i] = 0;
        mem[22] = 21845;
    }

    private static readonly short[] Pred = [4096, 3277, 2458, 1638];

    private void DGain2(short index, short nbits, short[] code, int lSubfr, out short gainPit, out int gainCod,
        short bfi, short prevBfi, short state, short unusableFrame, short vadHist)
    {
        short[] mem = _decGain;
        const int pastQuaEn = 0, pastGainPit = 4, pastGainCode = 5, prevGc = 6, pbuf = 7, gbuf = 12, pbuf2 = 17;


        int lTmp = DotProduct12(code, 0, code, 0, lSubfr, out short exp);
        exp = Sub(exp, 18 + 6);
        IsqrtN(ref lTmp, ref exp);
        short gcodeInov = ExtractH(LShl(lTmp, Sub(exp, 3)));

        if (bfi != 0)
        {
            short tmp = Median5(mem, pbuf + 2);
            mem[pastGainPit] = tmp;
            if (mem[pastGainPit] > 15565) mem[pastGainPit] = 15565;
            if (unusableFrame != 0) gainPit = Mult(AmrWbTables.PdownUnusable[state], mem[pastGainPit]);
            else gainPit = Mult(AmrWbTables.PdownUsable[state], mem[pastGainPit]);

            tmp = Median5(mem, gbuf + 2);
            if (vadHist > 2)
            {
                mem[pastGainCode] = tmp;
            }
            else
            {
                if (unusableFrame != 0) mem[pastGainCode] = Mult(AmrWbTables.CdownUnusable[state], tmp);
                else mem[pastGainCode] = Mult(AmrWbTables.CdownUsable[state], tmp);
            }

            lTmp = LMult(mem[pastQuaEn], 8192);
            lTmp = LMac(lTmp, mem[pastQuaEn + 1], 8192);
            lTmp = LMac(lTmp, mem[pastQuaEn + 2], 8192);
            lTmp = LMac(lTmp, mem[pastQuaEn + 3], 8192);
            short quaEner = ExtractH(lTmp);
            quaEner = Sub(quaEner, 3072);
            if (quaEner < -14336) quaEner = -14336;

            mem[pastQuaEn + 3] = mem[pastQuaEn + 2];
            mem[pastQuaEn + 2] = mem[pastQuaEn + 1];
            mem[pastQuaEn + 1] = mem[pastQuaEn];
            mem[pastQuaEn] = quaEner;

            for (int i = 1; i < 5; i++) mem[gbuf + i - 1] = mem[gbuf + i];
            mem[gbuf + 4] = mem[pastGainCode];
            for (int i = 1; i < 5; i++) mem[pbuf + i - 1] = mem[pbuf + i];
            mem[pbuf + 4] = mem[pastGainPit];

            gainCod = LMult(mem[pastGainCode], gcodeInov);
            return;
        }

        lTmp = LDepositH(30); // MEAN_ENER
        lTmp = LShl(lTmp, 8);
        lTmp = LMac(lTmp, Pred[0], mem[pastQuaEn]);
        lTmp = LMac(lTmp, Pred[1], mem[pastQuaEn + 1]);
        lTmp = LMac(lTmp, Pred[2], mem[pastQuaEn + 2]);
        lTmp = LMac(lTmp, Pred[3], mem[pastQuaEn + 3]);
        short gcode0 = ExtractH(lTmp);

        lTmp = LMult(gcode0, 5443);
        lTmp = LShr(lTmp, 8);
        LExtract(lTmp, out short expGcode0, out short frac);
        gcode0 = ExtractL(Pow2(14, frac));
        expGcode0 = Sub(expGcode0, 14);

        int p;
        short[] tab;
        if (nbits == 6)
        {
            tab = AmrWbTables.QuaGain6b;
            p = index + index;
        }
        else
        {
            tab = AmrWbTables.QuaGain7b;
            p = index + index;
        }
        gainPit = tab[p];
        short gCode = tab[p + 1];

        lTmp = LMult(gCode, gcode0);
        lTmp = LShl(lTmp, Add(expGcode0, 4));
        gainCod = lTmp;

        if (prevBfi == 1)
        {
            lTmp = LMult(mem[prevGc], 5120);
            if (LSub(gainCod, lTmp) > 0 && LSub(gainCod, 6553600) > 0) gainCod = lTmp;
        }

        mem[pastGainCode] = Round(LShl(gainCod, 3));
        mem[pastGainPit] = gainPit;
        mem[prevGc] = mem[pastGainCode];

        for (int i = 1; i < 5; i++) mem[gbuf + i - 1] = mem[gbuf + i];
        mem[gbuf + 4] = mem[pastGainCode];
        for (int i = 1; i < 5; i++) mem[pbuf + i - 1] = mem[pbuf + i];
        mem[pbuf + 4] = mem[pastGainPit];
        for (int i = 1; i < 5; i++) mem[pbuf2 + i - 1] = mem[pbuf2 + i];
        mem[pbuf2 + 4] = mem[pastGainPit];

        LExtract(gainCod, out exp, out frac);
        lTmp = Mpy3216(exp, frac, gcodeInov);
        gainCod = LShl(lTmp, 3);

        lTmp = LDepositL(gCode);
        Log2(lTmp, out exp, out frac);
        exp = Sub(exp, 11);
        lTmp = Mpy3216(exp, frac, 24660);
        short qEner = ExtractL(LShr(lTmp, 3));

        mem[pastQuaEn + 3] = mem[pastQuaEn + 2];
        mem[pastQuaEn + 2] = mem[pastQuaEn + 1];
        mem[pastQuaEn + 1] = mem[pastQuaEn];
        mem[pastQuaEn] = qEner;
    }

    private short VoiceFactor(short[] exc, short qExc, short gainPit, short[] code, short gainCode, int lSubfr)
    {
        short ener1 = ExtractH(DotProduct12(exc, 0, exc, 0, lSubfr, out short exp1));
        exp1 = Sub(exp1, Add(qExc, qExc));
        int lTmp = LMult(gainPit, gainPit);
        short exp = NormL(lTmp);
        short tmp = ExtractH(LShl(lTmp, exp));
        ener1 = Mult(ener1, tmp);
        exp1 = Sub(Sub(exp1, exp), 10);

        short ener2 = ExtractH(DotProduct12(code, 0, code, 0, lSubfr, out short exp2));
        exp = NormS(gainCode);
        tmp = Shl(gainCode, exp);
        tmp = Mult(tmp, tmp);
        ener2 = Mult(ener2, tmp);
        exp2 = Sub(exp2, Add(exp, exp));

        short i = Sub(exp1, exp2);
        if (i >= 0)
        {
            ener1 = Shr(ener1, 1);
            ener2 = Shr(ener2, Add(i, 1));
        }
        else
        {
            ener1 = Shr(ener1, Sub(1, i));
            ener2 = Shr(ener2, 1);
        }

        tmp = Sub(ener1, ener2);
        ener1 = Add(Add(ener1, ener2), 1);
        if (tmp >= 0) tmp = DivS(tmp, ener1);
        else tmp = Negate(DivS(Negate(tmp), ener1));
        return tmp;
    }

    private void PhaseDispersion(short gainCode, short gainPit, short[] code, int mode)
    {
        const short pitch09 = 14746, pitch06 = 9830;
        // disp_mem: [0] previous state, [1] previous gain code, [2..7] gain pit history.
        short[] mem = _dispMem;
        Span<short> code2 = stackalloc short[2 * LSubfr];
        code2.Clear();

        short state;
        if (gainPit < pitch06) state = 0;
        else if (gainPit < pitch09) state = 1;
        else state = 2;

        for (int i = 5; i > 0; i--) mem[2 + i] = mem[2 + i - 1];
        mem[2] = gainPit;

        if (Sub(Sub(gainCode, mem[1]), Shl(mem[1], 1)) > 0)
        {
            if (state < 2) state = Add(state, 1);
        }
        else
        {
            short j = 0;
            for (int i = 0; i < 6; i++)
                if (mem[2 + i] < pitch06) j = Add(j, 1);
            if (j > 2) state = 0;
            if (Sub(state, mem[0]) > 1) state = Sub(state, 1);
        }

        mem[1] = gainCode;
        mem[0] = state;

        state = Add(state, (short)mode);

        if (state == 0)
        {
            short[] imp = AmrWbTables.PhImpLow;
            for (int i = 0; i < LSubfr; i++)
                if (code[i] != 0)
                    for (int j = 0; j < LSubfr; j++) code2[i + j] = Add(code2[i + j], MultR(code[i], imp[j]));
        }
        else if (state == 1)
        {
            short[] imp = AmrWbTables.PhImpMid;
            for (int i = 0; i < LSubfr; i++)
                if (code[i] != 0)
                    for (int j = 0; j < LSubfr; j++) code2[i + j] = Add(code2[i + j], MultR(code[i], imp[j]));
        }

        if (state < 2)
        {
            for (int i = 0; i < LSubfr; i++) code[i] = Add(code2[i], code2[i + LSubfr]);
        }
    }

    private void Agc2(short[] sigIn, short[] sigOut, int lTrm)
    {
        short temp = Shr(sigOut[0], 2);
        int s = LMult(temp, temp);
        for (int i = 1; i < lTrm; i++)
        {
            temp = Shr(sigOut[i], 2);
            s = LMac(s, temp, temp);
        }
        if (s == 0) return;
        short exp = Sub(NormL(s), 1);
        short gainOut = Round(LShl(s, exp));

        temp = Shr(sigIn[0], 2);
        s = LMult(temp, temp);
        for (int i = 1; i < lTrm; i++)
        {
            temp = Shr(sigIn[i], 2);
            s = LMac(s, temp, temp);
        }
        short g0;
        if (s == 0)
        {
            g0 = 0;
        }
        else
        {
            short i = NormL(s);
            short gainIn = Round(LShl(s, i));
            exp = Sub(exp, i);
            s = LDepositL(DivS(gainOut, gainIn));
            s = LShl(s, 7);
            s = LShr(s, exp);
            s = Isqrt(s);
            g0 = Round(LShl(s, 9));
        }
        for (int i = 0; i < lTrm; i++) sigOut[i] = ExtractH(LShl(LMult(sigOut[i], g0), 2));
    }

    // ---------------------------------------------------------------------------------------------------------
    // Lag concealment (lagconc.c; TS 26.191)

    private void Lagconc(ref short t0, short unusableFrame)
    {
        const int hist = 5;
        short[] gainHist = _decGain; // gain history lives at dec_gain[17..21]
        const int gh = 17;
        short[] lagHist = _lagHist;
        short lastGain = gainHist[gh + 4];
        short secLastGain = gainHist[gh + 3];
        short lastLag = lagHist[0];
        short minLag = lagHist[0];
        for (int i = 1; i < hist; i++)
            if (lagHist[i] < minLag) minLag = lagHist[i];
        short maxLag = lagHist[0];
        for (int i = 1; i < hist; i++)
            if (lagHist[i] > maxLag) maxLag = lagHist[i];
        short minGain = gainHist[gh];
        for (int i = 1; i < hist; i++)
            if (gainHist[gh + i] < minGain) minGain = gainHist[gh + i];
        short lagDif = Sub(maxLag, minLag);

        if (unusableFrame != 0)
        {
            if (minGain > 8192 && lagDif < 10) t0 = _oldT0;
            else if (lastGain > 8192 && secLastGain > 8192) t0 = lagHist[0];
            else t0 = RandomLag(lagHist);
            if (t0 > maxLag) t0 = maxLag;
            if (t0 < minLag) t0 = minLag;
        }
        else
        {
            short meanLag = 0;
            for (int i = 0; i < hist; i++) meanLag = Add(meanLag, lagHist[i]);
            meanLag = Mult(meanLag, 6554);
            short tmp = Sub(t0, maxLag);
            short tmp2 = Sub(t0, lastLag);

            if (lagDif < 10 && t0 > Sub(minLag, 5) && tmp < 5)
            {
                // keep T0
            }
            else if (lastGain > 8192 && secLastGain > 8192 && Add(tmp2, 10) > 0 && tmp2 - 10 < 0)
            {
                // keep T0
            }
            else if (minGain < 6554 && lastGain == minGain && t0 > minLag && t0 < maxLag)
            {
                // keep T0
            }
            else if (lagDif < 70 && t0 > minLag && t0 < maxLag)
            {
                // keep T0
            }
            else if (t0 > meanLag && t0 < maxLag)
            {
                // keep T0
            }
            else
            {
                if (minGain > 8192 && lagDif < 10) t0 = lagHist[0];
                else if (lastGain > 8192 && secLastGain > 8192) t0 = lagHist[0];
                else t0 = RandomLag(lagHist);
                if (t0 > maxLag) t0 = maxLag;
                if (t0 < minLag) t0 = minLag;
            }
        }
    }

    private short RandomLag(short[] lagHist)
    {
        Span<short> h = stackalloc short[5];
        for (int i = 0; i < 5; i++) h[i] = lagHist[i];
        for (int n = 1; n < 5; n++)
        {
            short x = h[n];
            int i = n - 1;
            for (; i >= 0; i--)
            {
                if (x < h[i]) h[i + 1] = h[i];
                else break;
            }
            h[i + 1] = x;
        }
        short lagDif = Sub(h[4], h[2]);
        if (lagDif > 40) lagDif = 40;
        short d = Random(ref _seed3);
        short tmp = Shr(lagDif, 1);
        short d2 = Mult(tmp, d);
        tmp = Add(Add(h[2], h[3]), h[4]);
        return Add(Mult(tmp, 10923), d2);
    }
}
