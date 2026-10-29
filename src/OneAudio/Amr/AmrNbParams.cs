// Copyright (c) Recursive AI, LLC. MIT OR Apache-2.0.
namespace OneAudio.Amr;

// LPC parameter decoding and interpolation, excitation codebooks, gains and error concealment of the AMR
// narrowband decoder: TS 26.090 sections 5-6, TS 26.091 (error concealment).
internal sealed partial class AmrNbCore
{
    // ---------------------------------------------------------------------------------------------------------
    // LSF -> LSP -> A(z)

    /// <summary>Lsf_lsp: LSF in [0,0.5) scaled to 16 bits to LSP cosines through a 64-segment table (TS 26.090 section 5.2.x).</summary>
    private void LsfToLsp(short[] lsf, short[] lsp, int m)
    {
        short[] table = AmrNbTables.LspTable;
        for (int i = 0; i < m; i++)
        {
            short ind = Shr(lsf[i], 8);
            short offset = (short)(lsf[i] & 0x00ff);
            int lTmp = LMult(Sub(table[ind + 1], table[ind]), offset);
            lsp[i] = Add(table[ind], ExtractL(LShr(lTmp, 9)));
        }
    }

    private void LspToLsf(short[] lsp, short[] lsf, int m)
    {
        short[] table = AmrNbTables.LspTable;
        short[] slope = AmrNbTables.LspSlope;
        int ind = 63;
        for (int i = m - 1; i >= 0; i--)
        {
            while (Sub(table[ind], lsp[i]) < 0) ind--;
            int lTmp = LMult(Sub(lsp[i], table[ind]), slope[ind]);
            lsf[i] = Round(LShl(lTmp, 3));
            lsf[i] = Add(lsf[i], Shl((short)ind, 8));
        }
    }

    private void ReorderLsf(short[] lsf, short minDist, int n)
    {
        short lsfMin = minDist;
        for (int i = 0; i < n; i++)
        {
            if (lsf[i] < lsfMin) lsf[i] = lsfMin;
            lsfMin = Add(lsf[i], minDist);
        }
    }

    private void GetLspPol(short[] lsp, int lspOff, int[] f)
    {
        int fi = 0;
        f[fi++] = LMult(4096, 2048);
        f[fi++] = LMsu(0, lsp[lspOff], 512);
        lspOff += 2;
        for (int i = 2; i <= 5; i++)
        {
            f[fi] = f[fi - 2];
            for (int j = 1; j < i; j++, fi--)
            {
                LExtract(f[fi - 1], out short hi, out short lo);
                int t0 = Mpy3216(hi, lo, lsp[lspOff]);
                t0 = LShl(t0, 1);
                f[fi] = LAdd(f[fi], f[fi - 2]);
                f[fi] = LSub(f[fi], t0);
            }
            f[fi] = LMsu(f[fi], lsp[lspOff], 512);
            fi += i;
            lspOff += 2;
        }
    }

    /// <summary>Lsp_Az: converts 10 LSPs to the 11 coefficients of A(z) in Q12 (TS 26.090 section 5.2.x).</summary>
    private void LspAz(short[] lsp, int lspOff, short[] a, int aOff)
    {
        GetLspPol(lsp, lspOff, _f1);
        GetLspPol(lsp, lspOff + 1, _f2);
        for (int i = 5; i > 0; i--)
        {
            _f1[i] = LAdd(_f1[i], _f1[i - 1]);
            _f2[i] = LSub(_f2[i], _f2[i - 1]);
        }
        a[aOff] = 4096;
        for (int i = 1, j = 10; i <= 5; i++, j--)
        {
            int t0 = LAdd(_f1[i], _f2[i]);
            a[aOff + i] = ExtractL(LShrR(t0, 13));
            t0 = LSub(_f1[i], _f2[i]);
            a[aOff + j] = ExtractL(LShrR(t0, 13));
        }
    }

    private void IntLpc1And3(short[] lspOld, short[] lspMid, short[] lspNew, short[] az)
    {
        short[] lsp = _lspTmp;
        int o = 0;
        for (int i = 0; i < M; i++) lsp[i] = Add(Shr(lspMid[i], 1), Shr(lspOld[i], 1));
        LspAz(lsp, 0, az, o);
        o += Mp1;
        LspAz(lspMid, 0, az, o);
        o += Mp1;
        for (int i = 0; i < M; i++) lsp[i] = Add(Shr(lspMid[i], 1), Shr(lspNew[i], 1));
        LspAz(lsp, 0, az, o);
        o += Mp1;
        LspAz(lspNew, 0, az, o);
    }

    private void IntLpc1To3(short[] lspOld, short[] lspNew, short[] az)
    {
        short[] lsp = _lspTmp;
        int o = 0;
        for (int i = 0; i < M; i++) lsp[i] = Add(Shr(lspNew[i], 2), Sub(lspOld[i], Shr(lspOld[i], 2)));
        LspAz(lsp, 0, az, o);
        o += Mp1;
        for (int i = 0; i < M; i++) lsp[i] = Add(Shr(lspOld[i], 1), Shr(lspNew[i], 1));
        LspAz(lsp, 0, az, o);
        o += Mp1;
        for (int i = 0; i < M; i++) lsp[i] = Add(Shr(lspOld[i], 2), Sub(lspNew[i], Shr(lspNew[i], 2)));
        LspAz(lsp, 0, az, o);
        o += Mp1;
        LspAz(lspNew, 0, az, o);
    }

    private void IntLsf(short[] lsfOld, short[] lsfNew, int iSubfr, short[] lsfOut)
    {
        if (iSubfr == 0)
        {
            for (int i = 0; i < M; i++) lsfOut[i] = Add(Sub(lsfOld[i], Shr(lsfOld[i], 2)), Shr(lsfNew[i], 2));
        }
        else if (iSubfr == 40)
        {
            for (int i = 0; i < M; i++) lsfOut[i] = Add(Shr(lsfOld[i], 1), Shr(lsfNew[i], 1));
        }
        else if (iSubfr == 80)
        {
            for (int i = 0; i < M; i++) lsfOut[i] = Add(Shr(lsfOld[i], 2), Sub(lsfNew[i], Shr(lsfNew[i], 2)));
        }
        else if (iSubfr == 120)
        {
            for (int i = 0; i < M; i++) lsfOut[i] = lsfNew[i];
        }
    }

    // ---------------------------------------------------------------------------------------------------------
    // LSF vector dequantization (TS 26.090 section 6.1 / TS 26.073 d_plsf_3.c, d_plsf_5.c)

    private void DPlsf3(int mode, short bfi, short[] indice, int ip, short[] lsp1Q)
    {
        short[] lsf1R = _lsfTmp1;
        short[] lsf1Q = _lsfTmp2;
        short[] meanLsf = AmrNbTables.MeanLsf3;
        short[] predFac = AmrNbTables.PredFac;

        if (bfi != 0)
        {
            for (int i = 0; i < M; i++)
                lsf1Q[i] = Add(Mult(_pastLsfQ[i], 29491), Mult(meanLsf[i], 3277));
            if (mode != MrDtx)
            {
                for (int i = 0; i < M; i++)
                {
                    short temp = Add(meanLsf[i], Mult(_pastRQ[i], predFac[i]));
                    _pastRQ[i] = Sub(lsf1Q[i], temp);
                }
            }
            else
            {
                for (int i = 0; i < M; i++)
                {
                    short temp = Add(meanLsf[i], _pastRQ[i]);
                    _pastRQ[i] = Sub(lsf1Q[i], temp);
                }
            }
        }
        else
        {
            short[] cb1, cb2, cb3;
            if (mode == Mr475 || mode == Mr515)
            {
                cb1 = AmrNbTables.Dico1Lsf3;
                cb2 = AmrNbTables.Dico2Lsf3;
                cb3 = AmrNbTables.Mr515Lsf3;
            }
            else if (mode == Mr795)
            {
                cb1 = AmrNbTables.Mr795Lsf1;
                cb2 = AmrNbTables.Dico2Lsf3;
                cb3 = AmrNbTables.Dico3Lsf3;
            }
            else
            {
                cb1 = AmrNbTables.Dico1Lsf3;
                cb2 = AmrNbTables.Dico2Lsf3;
                cb3 = AmrNbTables.Dico3Lsf3;
            }

            short index = indice[ip++];
            int p = index * 3;
            lsf1R[0] = cb1[p++];
            lsf1R[1] = cb1[p++];
            lsf1R[2] = cb1[p];

            index = indice[ip++];
            if (mode == Mr475 || mode == Mr515) index = Shl(index, 1);
            p = index * 3;
            lsf1R[3] = cb2[p++];
            lsf1R[4] = cb2[p++];
            lsf1R[5] = cb2[p];

            index = indice[ip];
            p = index << 2;
            lsf1R[6] = cb3[p++];
            lsf1R[7] = cb3[p++];
            lsf1R[8] = cb3[p++];
            lsf1R[9] = cb3[p];

            if (mode != MrDtx)
            {
                for (int i = 0; i < M; i++)
                {
                    short temp = Add(meanLsf[i], Mult(_pastRQ[i], predFac[i]));
                    lsf1Q[i] = Add(lsf1R[i], temp);
                    _pastRQ[i] = lsf1R[i];
                }
            }
            else
            {
                for (int i = 0; i < M; i++)
                {
                    short temp = Add(meanLsf[i], _pastRQ[i]);
                    lsf1Q[i] = Add(lsf1R[i], temp);
                    _pastRQ[i] = lsf1R[i];
                }
            }
        }

        ReorderLsf(lsf1Q, LsfGap, M);
        Array.Copy(lsf1Q, _pastLsfQ, M);
        LsfToLsp(lsf1Q, lsp1Q, M);
    }

    private void DPlsf5(short bfi, short[] indice, int ip, short[] lsp1Q, short[] lsp2Q)
    {
        short[] lsf1R = _lsfTmp1;
        short[] lsf1Q = _lsfTmp2;
        short[] meanLsf = AmrNbTables.MeanLsf5;
        short[] lsf2Rs = _lsf2R;
        short[] lsf2Q = _lsf2Q;

        if (bfi != 0)
        {
            for (int i = 0; i < M; i++)
            {
                lsf1Q[i] = Add(Mult(_pastLsfQ[i], 31128), Mult(meanLsf[i], 1639));
                lsf2Q[i] = lsf1Q[i];
            }
            for (int i = 0; i < M; i++)
            {
                short temp = Add(meanLsf[i], Mult(_pastRQ[i], 21299));
                _pastRQ[i] = Sub(lsf2Q[i], temp);
            }
        }
        else
        {
            short[] d1 = AmrNbTables.Dico1Lsf5;
            short[] d2 = AmrNbTables.Dico2Lsf5;
            short[] d3 = AmrNbTables.Dico3Lsf5;
            short[] d4 = AmrNbTables.Dico4Lsf5;
            short[] d5 = AmrNbTables.Dico5Lsf5;

            int p = indice[ip] << 2;
            lsf1R[0] = d1[p++];
            lsf1R[1] = d1[p++];
            lsf2Rs[0] = d1[p++];
            lsf2Rs[1] = d1[p];

            p = indice[ip + 1] << 2;
            lsf1R[2] = d2[p++];
            lsf1R[3] = d2[p++];
            lsf2Rs[2] = d2[p++];
            lsf2Rs[3] = d2[p];

            short sign = (short)(indice[ip + 2] & 1);
            short idx = Shr(indice[ip + 2], 1);
            p = idx << 2;
            if (sign == 0)
            {
                lsf1R[4] = d3[p++];
                lsf1R[5] = d3[p++];
                lsf2Rs[4] = d3[p++];
                lsf2Rs[5] = d3[p];
            }
            else
            {
                lsf1R[4] = Negate(d3[p++]);
                lsf1R[5] = Negate(d3[p++]);
                lsf2Rs[4] = Negate(d3[p++]);
                lsf2Rs[5] = Negate(d3[p]);
            }

            p = indice[ip + 3] << 2;
            lsf1R[6] = d4[p++];
            lsf1R[7] = d4[p++];
            lsf2Rs[6] = d4[p++];
            lsf2Rs[7] = d4[p];

            p = indice[ip + 4] << 2;
            lsf1R[8] = d5[p++];
            lsf1R[9] = d5[p++];
            lsf2Rs[8] = d5[p++];
            lsf2Rs[9] = d5[p];

            for (int i = 0; i < M; i++)
            {
                short temp = Add(meanLsf[i], Mult(_pastRQ[i], 21299));
                lsf1Q[i] = Add(lsf1R[i], temp);
                lsf2Q[i] = Add(lsf2Rs[i], temp);
                _pastRQ[i] = lsf2Rs[i];
            }
        }

        ReorderLsf(lsf1Q, LsfGap, M);
        ReorderLsf(lsf2Q, LsfGap, M);
        Array.Copy(lsf2Q, _pastLsfQ, M);
        LsfToLsp(lsf1Q, lsp1Q, M);
        LsfToLsp(lsf2Q, lsp2Q, M);
    }

    private readonly short[] _cbTmp = new short[M];
    private readonly short[] _lsf2R = new short[M];
    private readonly short[] _lsf2Q = new short[M];

    private void LspAvg(short[] lsp)
    {
        for (int i = 0; i < M; i++)
        {
            int lTmp = LDepositH(_lspMeanSave[i]);
            lTmp = LMsu(lTmp, 5243, _lspMeanSave[i]);
            lTmp = LMac(lTmp, 5243, lsp[i]);
            _lspMeanSave[i] = Round(lTmp);
        }
    }

    // ---------------------------------------------------------------------------------------------------------
    // Pitch lag decoding (TS 26.090 section 6.1 / dec_lag3.c, dec_lag6.c)

    private void DecLag3(short index, short t0Min, short t0Max, short iSubfr, short t0Prev, out short t0, out short t0Frac, bool flag4)
    {
        short i;
        if (iSubfr == 0)
        {
            if (index < 197)
            {
                t0 = Add(Mult(Add(index, 2), 10923), 19);
                i = Add(Add(t0, t0), t0);
                t0Frac = Add(Sub(index, i), 58);
            }
            else
            {
                t0 = Sub(index, 112);
                t0Frac = 0;
            }
        }
        else if (!flag4)
        {
            i = Sub(Mult(Add(index, 2), 10923), 1);
            t0 = Add(i, t0Min);
            i = Add(Add(i, i), i);
            t0Frac = Sub(Sub(index, 2), i);
        }
        else
        {
            short tmpLag = t0Prev;
            if (Sub(Sub(tmpLag, t0Min), 5) > 0) tmpLag = Add(t0Min, 5);
            if (Sub(Sub(t0Max, tmpLag), 4) > 0) tmpLag = Sub(t0Max, 4);
            if (index < 4)
            {
                i = Sub(tmpLag, 5);
                t0 = Add(i, index);
                t0Frac = 0;
            }
            else if (index < 12)
            {
                i = Sub(Mult(Sub(index, 5), 10923), 1);
                t0 = Add(i, tmpLag);
                i = Add(Add(i, i), i);
                t0Frac = Sub(Sub(index, 9), i);
            }
            else
            {
                i = Add(Sub(index, 12), tmpLag);
                t0 = Add(i, 1);
                t0Frac = 0;
            }
        }
    }

    private void DecLag6(short index, short pitMin, short pitMax, short iSubfr, ref short t0, out short t0Frac)
    {
        short i;
        if (iSubfr == 0)
        {
            if (index < 463)
            {
                t0 = Add(Mult(Add(index, 5), 5462), 17);
                i = Add(Add(t0, t0), t0);
                t0Frac = Add(Sub(index, Add(i, i)), 105);
            }
            else
            {
                t0 = Sub(index, 368);
                t0Frac = 0;
            }
        }
        else
        {
            short t0Min = Sub(t0, 5);
            if (t0Min < pitMin) t0Min = pitMin;
            short t0Max = Add(t0Min, 9);
            if (t0Max > pitMax)
            {
                t0Max = pitMax;
                t0Min = Sub(t0Max, 9);
            }
            i = Sub(Mult(Add(index, 5), 5462), 1);
            t0 = Add(i, t0Min);
            i = Add(Add(i, i), i);
            t0Frac = Sub(Sub(index, 3), Add(i, i));
        }
    }

    // ---------------------------------------------------------------------------------------------------------
    // Algebraic codebooks (TS 26.090 section 6.1; TS 26.073 d*pf.c)

    private void Dec10i40Bits35(short[] index, int ip, short[] cod)
    {
        short[] dgray = AmrNbTables.Dgray;
        for (int i = 0; i < LSubfr; i++) cod[i] = 0;
        for (int j = 0; j < 5; j++)
        {
            short tmp = index[ip + j];
            short i = (short)(tmp & 7);
            i = dgray[i];
            i = ExtractL(LShr(LMult(i, 5), 1));
            short pos1 = Add(i, j);
            i = (short)(Shr(tmp, 3) & 1);
            short sign = i == 0 ? (short)4096 : (short)-4096;
            cod[pos1] = sign;

            i = (short)(index[ip + Add(j, 5)] & 7);
            i = dgray[i];
            i = ExtractL(LShr(LMult(i, 5), 1));
            short pos2 = Add(i, j);
            if (pos2 < pos1) sign = Negate(sign);
            cod[pos2] = Add(cod[pos2], sign);
        }
    }

    private static void SetPulses(short[] cod, short sign, short p0, short p1)
    {
        for (int i = 0; i < LSubfr; i++) cod[i] = 0;
        cod[p0] = (sign & 1) != 0 ? (short)8191 : (short)-8192;
        sign >>= 1;
        cod[p1] = (sign & 1) != 0 ? (short)8191 : (short)-8192;
    }

    private void Decode2i40Bits11(short sign, short index, short[] cod)
    {
        short j = (short)(index & 1);
        index = Shr(index, 1);
        short i = (short)(index & 7);
        i = Add(i, Shl(i, 2));
        i = Add(i, 1);
        j = Shl(j, 1);
        short pos0 = Add(i, j);

        index = Shr(index, 3);
        j = (short)(index & 3);
        index = Shr(index, 2);
        i = (short)(index & 7);
        short pos1;
        if (j == 3)
        {
            i = Add(i, Shl(i, 2));
            pos1 = Add(i, 4);
        }
        else
        {
            i = Add(i, Shl(i, 2));
            pos1 = Add(i, j);
        }
        SetPulses(cod, sign, pos0, pos1);
    }

    private void DecodeI40Bits9(short subNr, short sign, short index, short[] cod)
    {
        short[] startPos = AmrNbTables.StartPos9;
        short j = Shr((short)(index & 64), 6);
        short i = (short)(index & 7);
        i = Add(i, Shl(i, 2));
        short k = startPos[Add(Shl(j, 3), Shl(subNr, 1))];
        short pos0 = Add(i, k);

        index = Shr(index, 3);
        i = (short)(index & 7);
        i = Add(i, Shl(i, 2));
        k = startPos[Add(Add(Shl(j, 3), Shl(subNr, 1)), 1)];
        short pos1 = Add(i, k);
        SetPulses(cod, sign, pos0, pos1);
    }

    private void Decode3i40Bits14(short sign, short index, short[] cod)
    {
        short i = (short)(index & 7);
        short p0 = Add(i, Shl(i, 2));

        index = Shr(index, 3);
        short j = (short)(index & 1);
        index = Shr(index, 1);
        i = (short)(index & 7);
        i = Add(i, Shl(i, 2));
        i = Add(i, 1);
        j = Shl(j, 1);
        short p1 = Add(i, j);

        index = Shr(index, 3);
        j = (short)(index & 1);
        index = Shr(index, 1);
        i = (short)(index & 7);
        i = Add(i, Shl(i, 2));
        i = Add(i, 2);
        j = Shl(j, 1);
        short p2 = Add(i, j);

        for (int n = 0; n < LSubfr; n++) cod[n] = 0;
        cod[p0] = (sign & 1) != 0 ? (short)8191 : (short)-8192;
        sign = Shr(sign, 1);
        cod[p1] = (sign & 1) != 0 ? (short)8191 : (short)-8192;
        sign = Shr(sign, 1);
        cod[p2] = (sign & 1) != 0 ? (short)8191 : (short)-8192;
    }

    private void Decode4i40Bits17(short sign, short index, short[] cod)
    {
        short[] dgray = AmrNbTables.Dgray;
        short i = (short)(index & 7);
        i = dgray[i];
        short p0 = Add(i, Shl(i, 2));

        index = Shr(index, 3);
        i = (short)(index & 7);
        i = dgray[i];
        i = Add(i, Shl(i, 2));
        short p1 = Add(i, 1);

        index = Shr(index, 3);
        i = (short)(index & 7);
        i = dgray[i];
        i = Add(i, Shl(i, 2));
        short p2 = Add(i, 2);

        index = Shr(index, 3);
        short j = (short)(index & 1);
        index = Shr(index, 1);
        i = (short)(index & 7);
        i = dgray[i];
        i = Add(i, Shl(i, 2));
        i = Add(i, 3);
        short p3 = Add(i, j);

        for (int n = 0; n < LSubfr; n++) cod[n] = 0;
        cod[p0] = (sign & 1) != 0 ? (short)8191 : (short)-8192;
        sign = Shr(sign, 1);
        cod[p1] = (sign & 1) != 0 ? (short)8191 : (short)-8192;
        sign = Shr(sign, 1);
        cod[p2] = (sign & 1) != 0 ? (short)8191 : (short)-8192;
        sign = Shr(sign, 1);
        cod[p3] = (sign & 1) != 0 ? (short)8191 : (short)-8192;
    }

    private void Decompress10(short msbs, short lsbs, int index1, int index2, int index3, short[] posIndx)
    {
        if (msbs > 124) msbs = 124;
        short ia = Mult(msbs, 1311);
        ia = Sub(msbs, ExtractL(LShr(LMult(ia, 25), 1)));
        short ib = Shl(Sub(ia, ExtractL(LShr(LMult(Mult(ia, 6554), 5), 1))), 1);
        short ic = Shl(Shr(lsbs, 2), 2);
        ic = Sub(lsbs, ic);
        posIndx[index1] = Add(ib, (short)(ic & 1));
        ib = Shl(Mult(ia, 6554), 1);
        posIndx[index2] = Add(ib, Shr(ic, 1));
        posIndx[index3] = Add(Shl(Mult(msbs, 1311), 1), Shr(lsbs, 2));
    }

    private void Dec8i40Bits31(short[] index, int ip, short[] cod)
    {
        for (int i = 0; i < LSubfr; i++) cod[i] = 0;

        // decompress_code
        short[] signIndx = _signIndx;
        short[] posIndx = _posIndx;
        for (int i = 0; i < 4; i++) signIndx[i] = index[ip + i];
        short msbs = Shr(index[ip + 4], 3);
        short lsbs = (short)(index[ip + 4] & 7);
        Decompress10(msbs, lsbs, 0, 4, 1, posIndx);
        msbs = Shr(index[ip + 5], 3);
        lsbs = (short)(index[ip + 5] & 7);
        Decompress10(msbs, lsbs, 2, 6, 5, posIndx);
        msbs = Shr(index[ip + 6], 2);
        lsbs = (short)(index[ip + 6] & 3);
        short msbs024 = Shr(Add(ExtractL(LShr(LMult(msbs, 25), 1)), 12), 5);
        short ia = (short)(Mult(msbs024, 6554) & 1);
        short ib = Sub(msbs024, ExtractL(LShr(LMult(Mult(msbs024, 6554), 5), 1)));
        if (ia == 1) ib = Sub(4, ib);
        posIndx[3] = Add(Shl(ib, 1), (short)(lsbs & 1));
        ia = Shl(Mult(msbs024, 6554), 1);
        posIndx[7] = Add(ia, Shr(lsbs, 1));

        for (int j = 0; j < 4; j++)
        {
            short i = posIndx[j];
            i = ExtractL(LShr(LMult(i, 4), 1));
            short pos1 = Add(i, j);
            short sign = signIndx[j] == 0 ? (short)8191 : (short)-8191;
            cod[pos1] = sign;

            i = posIndx[Add(j, 4)];
            i = ExtractL(LShr(LMult(i, 4), 1));
            short pos2 = Add(i, j);
            if (pos2 < pos1) sign = Negate(sign);
            cod[pos2] = Add(cod[pos2], sign);
        }
    }

    // ---------------------------------------------------------------------------------------------------------
    // Gains (TS 26.090 section 6.1; d_gain_p.c, d_gain_c.c, dec_gain.c, gc_pred.c)

    private short DGainPitch(int mode, short index)
    {
        if (mode == Mr122) return Shl(Shr(AmrNbTables.QuaGainPitch[index], 2), 2);
        return AmrNbTables.QuaGainPitch[index];
    }

    private void GcPred(int mode, short[] code, out short expGcode0, out short fracGcode0, out short expEn, out short fracEn)
    {
        expEn = 0;
        fracEn = 0;
        int enerCode = LMac(0, code[0], code[0]);
        for (int i = 1; i < LSubfr; i++) enerCode = LMac(enerCode, code[i], code[i]);

        if (mode == Mr122)
        {
            enerCode = LMult(Round(enerCode), 26214);
            Log2(enerCode, out short exp, out short frac);
            enerCode = LComp(Sub(exp, 30), frac);
            int ener = 783741; // MEAN_ENER_MR122
            ReadOnlySpan<short> predMr122 = [44, 37, 22, 12];
            for (int i = 0; i < 4; i++) ener = LMac(ener, _pastQuaEnMr122[i], predMr122[i]);
            ener = LShr(LSub(ener, enerCode), 1);
            LExtract(ener, out expGcode0, out fracGcode0);
        }
        else
        {
            short expCode = NormL(enerCode);
            enerCode = LShl(enerCode, expCode);
            Log2Norm(enerCode, expCode, out short exp, out short frac);
            int lTmp = Mpy3216(exp, frac, -24660);
            if (mode == Mr102)
            {
                lTmp = LMac(lTmp, 16678, 64);
            }
            else if (mode == Mr795)
            {
                fracEn = ExtractH(enerCode);
                expEn = Sub(-11, expCode);
                lTmp = LMac(lTmp, 17062, 64);
            }
            else if (mode == Mr74)
            {
                lTmp = LMac(lTmp, 32588, 32);
            }
            else if (mode == Mr67)
            {
                lTmp = LMac(lTmp, 32268, 32);
            }
            else
            {
                lTmp = LMac(lTmp, 16678, 64);
            }
            lTmp = LShl(lTmp, 10);
            ReadOnlySpan<short> pred = [5571, 4751, 2785, 1556];
            for (int i = 0; i < 4; i++) lTmp = LMac(lTmp, pred[i], _pastQuaEn[i]);
            short gcode0 = ExtractH(lTmp);
            if (mode == Mr74) lTmp = LMult(gcode0, 5439);
            else lTmp = LMult(gcode0, 5443);
            lTmp = LShr(lTmp, 8);
            LExtract(lTmp, out expGcode0, out fracGcode0);
        }
    }

    private void GcPredUpdate(short quaEnerMr122, short quaEner)
    {
        for (int i = 3; i > 0; i--)
        {
            _pastQuaEn[i] = _pastQuaEn[i - 1];
            _pastQuaEnMr122[i] = _pastQuaEnMr122[i - 1];
        }
        _pastQuaEnMr122[0] = quaEnerMr122;
        _pastQuaEn[0] = quaEner;
    }

    private void GcPredAverageLimited(out short enerAvgMr122, out short enerAvg)
    {
        short avPredEn = 0;
        for (int i = 0; i < 4; i++) avPredEn = Add(avPredEn, _pastQuaEnMr122[i]);
        avPredEn = Mult(avPredEn, 8192);
        if (avPredEn < -2381) avPredEn = -2381;
        enerAvgMr122 = avPredEn;

        avPredEn = 0;
        for (int i = 0; i < 4; i++) avPredEn = Add(avPredEn, _pastQuaEn[i]);
        avPredEn = Mult(avPredEn, 8192);
        if (avPredEn < -14336) avPredEn = -14336;
        enerAvg = avPredEn;
    }

    private short DGainCode(int mode, short index, short[] code)
    {
        GcPred(mode, code, out short exp, out short frac, out _, out _);
        short[] qua = AmrNbTables.QuaGainCode;
        int p = index * 3;
        short gainCode;
        short gcode0;
        if (mode == Mr122)
        {
            gcode0 = ExtractL(Pow2(exp, frac));
            gcode0 = Shl(gcode0, 4);
            gainCode = Shl(Mult(gcode0, qua[p++]), 1);
        }
        else
        {
            gcode0 = ExtractL(Pow2(14, frac));
            int lTmp = LMult(qua[p++], gcode0);
            lTmp = LShr(lTmp, Sub(9, exp));
            gainCode = ExtractH(lTmp);
        }
        short quaEnerMr122 = qua[p++];
        short quaEner = qua[p];
        GcPredUpdate(quaEnerMr122, quaEner);
        return gainCode;
    }

    private void DecGain(int mode, short index, short[] code, bool evenSubfr, out short gainPit, out short gainCod)
    {
        short[] table;
        short quaEnerMr122, quaEner, gCode;
        int p;
        index = Shl(index, 2);
        if (mode == Mr102 || mode == Mr74 || mode == Mr67)
        {
            table = AmrNbTables.GainHighRates;
            p = index;
            gainPit = table[p++];
            gCode = table[p++];
            quaEnerMr122 = table[p++];
            quaEner = table[p];
        }
        else if (mode == Mr475)
        {
            index = Add(index, Shl(Sub(1, evenSubfr ? 1 : 0), 1));
            table = AmrNbTables.GainMr475;
            p = index;
            gainPit = table[p++];
            gCode = table[p];
            Log2(LDepositL(gCode), out short e, out short f);
            e = Sub(e, 12);
            quaEnerMr122 = Add(ShrR(f, 5), Shl(e, 10));
            int lt = Mpy3216(e, f, 24660);
            quaEner = Round(LShl(lt, 13));
        }
        else
        {
            table = AmrNbTables.GainLowRates;
            p = index;
            gainPit = table[p++];
            gCode = table[p++];
            quaEnerMr122 = table[p++];
            quaEner = table[p];
        }

        GcPred(mode, code, out short exp, out short frac, out _, out _);
        short gcode0 = ExtractL(Pow2(14, frac));
        int lTmp = LMult(gCode, gcode0);
        lTmp = LShr(lTmp, Sub(10, exp));
        gainCod = ExtractH(lTmp);
        GcPredUpdate(quaEnerMr122, quaEner);
    }

    // Error concealment of the gains (TS 26.091 section 6.x; ec_gains.c)

    private short EcGainPitch(short state)
    {
        ReadOnlySpan<short> pdown = [32767, 32112, 32112, 26214, 9830, 6553, 6553];
        short tmp = GmedN(_pbuf, 0, 5);
        if (tmp > _pastGainPit) tmp = _pastGainPit;
        return Mult(tmp, pdown[state]);
    }

    private short EcGainPitchUpdate(short bfi, short prevBf, short gainPitch)
    {
        if (bfi == 0)
        {
            if (prevBf != 0)
            {
                if (gainPitch > _prevGp) gainPitch = _prevGp;
            }
            _prevGp = gainPitch;
        }
        _pastGainPit = gainPitch;
        if (_pastGainPit > 16384) _pastGainPit = 16384;
        for (int i = 1; i < 5; i++) _pbuf[i - 1] = _pbuf[i];
        _pbuf[4] = _pastGainPit;
        return gainPitch;
    }

    private short EcGainCode(short state)
    {
        ReadOnlySpan<short> cdown = [32767, 32112, 32112, 32112, 32112, 32112, 22937];
        short tmp = GmedN(_gbuf, 0, 5);
        if (tmp > _pastGainCode) tmp = _pastGainCode;
        tmp = Mult(tmp, cdown[state]);
        GcPredAverageLimited(out short quaEnerMr122, out short quaEner);
        GcPredUpdate(quaEnerMr122, quaEner);
        return tmp;
    }

    private short EcGainCodeUpdate(short bfi, short prevBf, short gainCode)
    {
        if (bfi == 0)
        {
            if (prevBf != 0)
            {
                if (gainCode > _prevGc) gainCode = _prevGc;
            }
            _prevGc = gainCode;
        }
        _pastGainCode = gainCode;
        for (int i = 1; i < 5; i++) _gbuf[i - 1] = _gbuf[i];
        _gbuf[4] = gainCode;
        return gainCode;
    }

    // ---------------------------------------------------------------------------------------------------------
    // Background noise handling of TS 26.091 (c_g_aver.c, bgnscd.c, ex_ctrl.c, ph_disp.c)

    private short CbGainAverage(int mode, short gainCode, short[] lsp, short[] lspAver, short bfi, short prevBf,
        short pdfi, short prevPdf, short inBackgroundNoise, short voicedHangover)
    {
        const int hist = 7;
        short cbGainMix = gainCode;
        for (int i = 0; i < hist - 1; i++) _cbGainHistory[i] = _cbGainHistory[i + 1];
        _cbGainHistory[hist - 1] = gainCode;

        short[] tmp = _cbTmp;
        for (int i = 0; i < M; i++)
        {
            short tmp1 = AbsS(Sub(lspAver[i], lsp[i]));
            short shift1 = Sub(NormS(tmp1), 1);
            tmp1 = Shl(tmp1, shift1);
            short shift2 = NormS(lspAver[i]);
            short tmp2 = Shl(lspAver[i], shift2);
            tmp[i] = DivS(tmp1, tmp2);
            short shift = Sub(Add(2, shift1), shift2);
            if (shift >= 0) tmp[i] = Shr(tmp[i], shift);
            else tmp[i] = Shl(tmp[i], Negate(shift));
        }
        short diff = tmp[0];
        for (int i = 1; i < M; i++) diff = Add(diff, tmp[i]);

        if (diff > 5325) _hangVar = Add(_hangVar, 1);
        else _hangVar = 0;
        if (_hangVar > 10) _hangCount = 0;

        if (mode <= Mr67 || mode == Mr102)
        {
            short bgMix = 8192;
            short tmp1;
            if (((pdfi != 0 && prevPdf != 0) || bfi != 0 || prevBf != 0) && voicedHangover > 1 &&
                inBackgroundNoise != 0 && (mode == Mr475 || mode == Mr515 || mode == Mr59))
            {
                short tmpDiff = Sub(diff, 4506);
                tmp1 = tmpDiff > 0 ? tmpDiff : (short)0;
                bgMix = 2048 < tmp1 ? (short)8192 : Shl(tmp1, 2);
            }
            else
            {
                short tmpDiff = Sub(diff, 3277);
                tmp1 = tmpDiff > 0 ? tmpDiff : (short)0;
                bgMix = 2048 < tmp1 ? (short)8192 : Shl(tmp1, 2);
            }
            if (_hangCount < 40 || diff > 5325) bgMix = 8192;

            int lSum = LMult(6554, _cbGainHistory[2]);
            for (int i = 3; i < hist; i++) lSum = LMac(lSum, 6554, _cbGainHistory[i]);
            short cbGainMean = Round(lSum);

            if ((bfi != 0 || prevBf != 0) && inBackgroundNoise != 0 && (mode == Mr475 || mode == Mr515 || mode == Mr59))
            {
                lSum = LMult(4681, _cbGainHistory[0]);
                for (int i = 1; i < hist; i++) lSum = LMac(lSum, 4681, _cbGainHistory[i]);
                cbGainMean = Round(lSum);
            }

            lSum = LMult(bgMix, cbGainMix);
            lSum = LMac(lSum, 8192, cbGainMean);
            lSum = LMsu(lSum, bgMix, cbGainMean);
            cbGainMix = Round(LShl(lSum, 2));
        }
        _hangCount = Add(_hangCount, 1);
        return cbGainMix;
    }

    private short BgnScd(short[] ltpGainHist, short[] speech)
    {
        const int hist = 60;
        int s = 0;
        for (int i = 0; i < LFrame; i++) s = LMac(s, speech[i], speech[i]);
        s = LShl(s, 2);
        short currEnergy = ExtractH(s);

        short frameEnergyMin = 32767;
        for (int i = 0; i < hist; i++)
            if (_frameEnergyHist[i] < frameEnergyMin) frameEnergyMin = _frameEnergyHist[i];
        short noiseFloor = Shl(frameEnergyMin, 4);

        short maxEnergy = _frameEnergyHist[0];
        for (int i = 1; i < hist - 4; i++)
            if (maxEnergy < _frameEnergyHist[i]) maxEnergy = _frameEnergyHist[i];

        short maxEnergyLastPart = _frameEnergyHist[2 * hist / 3];
        for (int i = 2 * hist / 3 + 1; i < hist; i++)
            if (maxEnergyLastPart < _frameEnergyHist[i]) maxEnergyLastPart = _frameEnergyHist[i];

        short inbgNoise = 0;
        if (maxEnergy > 20 && currEnergy < 17578 && currEnergy > 20 &&
            (currEnergy < noiseFloor || maxEnergyLastPart < 1953))
        {
            if (Add(_bgHangover, 1) > 30) _bgHangover = 30;
            else _bgHangover = Add(_bgHangover, 1);
        }
        else
        {
            _bgHangover = 0;
        }
        if (_bgHangover > 1) inbgNoise = 1;

        for (int i = 0; i < hist - 1; i++) _frameEnergyHist[i] = _frameEnergyHist[i + 1];
        _frameEnergyHist[hist - 1] = currEnergy;

        short ltpLimit = 13926;
        if (_bgHangover > 8) ltpLimit = 15565;
        if (_bgHangover > 15) ltpLimit = 16383;

        bool prevVoiced = false;
        if (GmedN(ltpGainHist, 4, 5) > ltpLimit) prevVoiced = true;
        if (_bgHangover > 20)
        {
            prevVoiced = GmedN(ltpGainHist, 0, 9) > ltpLimit;
        }

        if (prevVoiced)
        {
            _voicedHangover = 0;
        }
        else
        {
            short temp = Add(_voicedHangover, 1);
            _voicedHangover = temp > 10 ? (short)10 : temp;
        }
        return inbgNoise;
    }

    private void ExCtrl(short[] excitation, short excEnergy, short[] exEnergyHist, short voicedHangover, short prevBfi, short carefulFlag)
    {
        short avgEnergy = GmedN(exEnergyHist, 0, 9);
        short prevEnergy = Shr(Add(exEnergyHist[7], exEnergyHist[8]), 1);
        if (exEnergyHist[8] < prevEnergy) prevEnergy = exEnergyHist[8];

        if (excEnergy < avgEnergy && excEnergy > 5)
        {
            short testEnergy = Shl(prevEnergy, 2);
            if (voicedHangover < 7 || prevBfi != 0) testEnergy = Sub(testEnergy, prevEnergy);
            if (avgEnergy > testEnergy) avgEnergy = testEnergy;

            short exp = NormS(excEnergy);
            excEnergy = Shl(excEnergy, exp);
            excEnergy = DivS(16383, excEnergy);
            int t0 = LMult(avgEnergy, excEnergy);
            t0 = LShr(t0, Sub(20, exp));
            if (LSub(t0, 32767) > 0) t0 = 32767;
            short scaleFactor = ExtractL(t0);
            if (carefulFlag != 0 && scaleFactor > 3072) scaleFactor = 3072;
            for (int i = 0; i < LSubfr; i++)
            {
                t0 = LMult(scaleFactor, excitation[i]);
                t0 = LShr(t0, 11);
                excitation[i] = ExtractL(t0);
            }
        }
    }

    private void PhDisp(int mode, short[] x, short cbGain, short ltpGain, short[] inno, short pitchFac, int tmpShift)
    {
        const short thr1 = 9830, thr2 = 14746;
        for (int i = 4; i > 0; i--) _phGainMem[i] = _phGainMem[i - 1];
        _phGainMem[0] = ltpGain;

        short impNr;
        if (ltpGain < thr2) impNr = ltpGain > thr1 ? (short)1 : (short)0;
        else impNr = 2;

        short tmp1 = Round(LShl(LMult(_phPrevCbGain, 16384), 2));
        if (cbGain > tmp1)
        {
            _phOnset = 2;
        }
        else if (_phOnset > 0)
        {
            _phOnset = Sub(_phOnset, 1);
        }

        if (_phOnset == 0)
        {
            short i1 = 0;
            for (int i = 0; i < 5; i++)
                if (_phGainMem[i] < thr1) i1 = Add(i1, 1);
            if (i1 > 2) impNr = 0;
        }

        if (impNr > Add(_phPrevState, 1) && _phOnset == 0) impNr = Sub(impNr, 1);
        if (impNr < 2 && _phOnset > 0) impNr = Add(impNr, 1);
        if (cbGain < 10) impNr = 2;
        if (_phLockFull == 1) impNr = 0;

        _phPrevState = impNr;
        _phPrevCbGain = cbGain;

        if (mode != Mr122 && mode != Mr102 && mode != Mr74 && impNr < 2)
        {
            short nze = 0;
            for (int i = 0; i < LSubfr; i++)
            {
                if (inno[i] != 0)
                {
                    _phPsPoss[nze] = (short)i;
                    nze = Add(nze, 1);
                }
                _phInnoSav[i] = inno[i];
                inno[i] = 0;
            }

            short[] phImp;
            if (mode == Mr795) phImp = impNr == 0 ? AmrNbTables.PhImpLowMr795 : AmrNbTables.PhImpMidMr795;
            else phImp = impNr == 0 ? AmrNbTables.PhImpLow : AmrNbTables.PhImpMid;

            for (int nPulse = 0; nPulse < nze; nPulse++)
            {
                short ppos = _phPsPoss[nPulse];
                int j = 0;
                for (int i = ppos; i < LSubfr; i++)
                {
                    tmp1 = Mult(_phInnoSav[ppos], phImp[j++]);
                    inno[i] = Add(inno[i], tmp1);
                }
                for (int i = 0; i < ppos; i++)
                {
                    tmp1 = Mult(_phInnoSav[ppos], phImp[j++]);
                    inno[i] = Add(inno[i], tmp1);
                }
            }
        }

        for (int i = 0; i < LSubfr; i++)
        {
            int lTemp = LMult(x[i], pitchFac);
            lTemp = LMac(lTemp, inno[i], cbGain);
            lTemp = LShl(lTemp, tmpShift);
            x[i] = Round(lTemp);
        }
    }
}
