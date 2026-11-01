namespace OneAudio.Aac;

/// <summary>Time/frequency grid and envelope data of one SBR channel (ISO/IEC 14496-3 4.5.2.8.3 to 4.5.2.8.5).</summary>
internal sealed class SbrFrame
{
    internal const int MaxEnvelopes = 5, MaxNoiseEnvelopes = 2, MaxEnvelopeValues = MaxEnvelopes * SbrBands.MaxFreqCoeffs;

    public int FrameClass, NEnvelopes, NNoiseEnvelopes, TranEnv;
    public readonly int[] Borders = new int[MaxEnvelopes + 1];
    public readonly int[] FreqRes = new int[MaxEnvelopes];
    public readonly int[] BordersNoise = new int[MaxNoiseEnvelopes + 1];
    public readonly int[] DomainVec = new int[MaxEnvelopes];       // 0 = frequency direction, 1 = time direction
    public readonly int[] DomainVecNoise = new int[MaxNoiseEnvelopes];
    public readonly int[] InvfMode = new int[SbrBands.MaxNoiseCoeffs];
    public int Coupling;                                           // 0 off, 1 level (left), 2 balance (right)
    public int AmpResCurrent;
    public int NScaleFactors;
    public readonly byte[] AddHarmonics = new byte[SbrBands.MaxFreqCoeffs];
    public readonly float[] Envelope = new float[MaxEnvelopeValues];
    public readonly float[] NoiseFloor = new float[MaxNoiseEnvelopes * SbrBands.MaxNoiseCoeffs];

    public void CopyFrameInfoFrom(SbrFrame o)
    {
        FrameClass = o.FrameClass; NEnvelopes = o.NEnvelopes; NNoiseEnvelopes = o.NNoiseEnvelopes; TranEnv = o.TranEnv;
        o.Borders.CopyTo(Borders, 0); o.FreqRes.CopyTo(FreqRes, 0); o.BordersNoise.CopyTo(BordersNoise, 0);
    }
}

/// <summary>What the envelope decoder remembers of the previous frame of one channel (4.6.18.3.5, delta coding in time).</summary>
internal sealed class SbrPrev
{
    public readonly float[] SfbNrgPrev = new float[SbrBands.MaxFreqCoeffs];
    public readonly float[] PrevNoiseLevel = new float[SbrBands.MaxNoiseCoeffs];
    public int AmpRes, StopPos, Coupling;
    public readonly int[] InvfMode = new int[SbrBands.MaxNoiseCoeffs];

    public void Init(int timeSlots)
    {
        Array.Clear(SfbNrgPrev);
        Array.Clear(PrevNoiseLevel);
        Array.Clear(InvfMode);
        StopPos = timeSlots;
        Coupling = 0;
        AmpRes = 0;
    }
}

/// <summary>Reads the SBR payload syntax. Every count read from the stream is checked against the table sizes before use.</summary>
internal static class AacSbrSyntax
{
    private const int FixFix = 0, FixVar = 1, VarFix = 2, VarVar = 3;

    private static int Huff(ReadOnlySpan<sbyte> tree, ref AacBitReader br)
    {
        int index = 0;
        for (int guard = 0; guard < 64; guard++)
        {
            index = tree[index * 2 + (br.ReadBit() ? 1 : 0)];
            if (index <= 0) return index + 64;
            if (br.Overrun) return 0;
        }
        return 0;
    }

    /// <summary>4.5.2.8.1 sbr_header. Fields not sent take their defaults.</summary>
    public static void ReadHeader(ref AacBitReader br, SbrHeader h)
    {
        h.AmpResolution = (int)br.Read(1);
        h.StartFreq = (int)br.Read(4);
        h.StopFreq = (int)br.Read(4);
        h.XoverBand = (int)br.Read(3);
        br.Skip(2); // bs_reserved
        bool extra1 = br.ReadBit();
        bool extra2 = br.ReadBit();
        if (extra1)
        {
            h.FreqScale = (int)br.Read(2);
            h.AlterScale = (int)br.Read(1);
            h.NoiseBands = (int)br.Read(2);
        }
        else
        {
            h.FreqScale = 2; h.AlterScale = 1; h.NoiseBands = 2;
        }
        if (extra2)
        {
            h.LimiterBands = (int)br.Read(2);
            h.LimiterGains = (int)br.Read(2);
            h.InterpolFreq = (int)br.Read(1);
            h.SmoothingLength = (int)br.Read(1);
        }
        else
        {
            h.LimiterBands = 2; h.LimiterGains = 2; h.InterpolFreq = 1; h.SmoothingLength = 1;
        }
    }

    // 4.5.2.8.3 sbr_grid, with the border rules of 4.6.18.3.3
    private static bool ReadGrid(ref AacBitReader br, SbrFrame f, int numTimeSlots)
    {
        int cls = (int)br.Read(2);
        f.FrameClass = cls;
        int nEnv = 0, b = 0, n = 0, temp = 0, p, border, i;
        switch (cls)
        {
            case FixFix:
            {
                temp = (int)br.Read(2);
                bool staticFreqRes = br.ReadBit();
                nEnv = 1 << temp;
                if (nEnv > SbrFrame.MaxEnvelopes) return false;
                // evenly spaced borders; noise floor envelopes split at the middle border
                for (i = 0; i <= nEnv; i++) f.Borders[i] = numTimeSlots * i / nEnv;
                for (i = 0; i < nEnv; i++) f.FreqRes[i] = staticFreqRes ? 1 : 0;
                f.TranEnv = -1;
                f.BordersNoise[0] = 0;
                if (nEnv == 1) { f.BordersNoise[1] = numTimeSlots; }
                else { f.BordersNoise[1] = f.Borders[nEnv >> 1]; f.BordersNoise[2] = numTimeSlots; }
                break;
            }
            case FixVar:
            case VarFix:
                temp = (int)br.Read(2);
                n = (int)br.Read(2);
                nEnv = n + 1;
                b = nEnv + 1;
                break;
        }
        switch (cls)
        {
            case FixVar:
                f.Borders[0] = 0;
                border = temp + numTimeSlots;
                i = b - 1;
                f.Borders[i] = border;
                for (int k = 0; k < n; k++)
                {
                    temp = (int)br.Read(2);
                    border -= 2 * temp + 2;
                    if (i - 1 < 0) return false;
                    f.Borders[--i] = border;
                }
                p = (int)br.Read(PointerBits(n + 2));
                if (p > n + 1) return false;
                f.TranEnv = p != 0 ? n + 2 - p : -1;
                for (int k = n; k >= 0; k--) f.FreqRes[k] = (int)br.Read(1);
                f.BordersNoise[1] = (p == 0 || p == 1) ? f.Borders[n] : f.Borders[f.TranEnv];
                break;
            case VarFix:
                border = temp;
                f.Borders[0] = border;
                int kk;
                for (kk = 1; kk <= n; kk++)
                {
                    temp = (int)br.Read(2);
                    border += 2 * temp + 2;
                    f.Borders[kk] = border;
                }
                f.Borders[kk] = numTimeSlots;
                p = (int)br.Read(PointerBits(n + 2));
                if (p > n + 1) return false;
                f.TranEnv = (p == 0 || p == 1) ? -1 : p - 1;
                for (int k = 0; k <= n; k++) f.FreqRes[k] = (int)br.Read(1);
                f.BordersNoise[1] = p switch { 0 => f.Borders[1], 1 => f.Borders[n], _ => f.Borders[f.TranEnv] };
                break;
            case VarVar:
            {
                int aL = (int)br.Read(2);
                int aR = (int)br.Read(2) + numTimeSlots;
                int nL = (int)br.Read(2);
                int nR = (int)br.Read(2);
                nEnv = nL + nR + 1;
                if (nEnv > SbrFrame.MaxEnvelopes) return false;
                b = nEnv + 1;
                border = aL;
                f.Borders[0] = border;
                for (int k = 1; k <= nL; k++)
                {
                    temp = (int)br.Read(2);
                    border += 2 * temp + 2;
                    f.Borders[k] = border;
                }
                border = aR;
                i = nEnv;
                f.Borders[i] = border;
                for (int k = 0; k < nR; k++)
                {
                    temp = (int)br.Read(2);
                    border -= 2 * temp + 2;
                    if (i - 1 < 0) return false;
                    f.Borders[--i] = border;
                }
                p = (int)br.Read(PointerBits(nL + nR + 2));
                if (p > nL + nR + 1) return false;
                f.TranEnv = p != 0 ? b - p : -1;
                for (int k = 0; k < nEnv; k++) f.FreqRes[k] = (int)br.Read(1);
                f.BordersNoise[0] = aL;
                if (nEnv == 1) f.BordersNoise[1] = aR;
                else
                {
                    f.BordersNoise[1] = (p == 0 || p == 1) ? f.Borders[nEnv - 1] : f.Borders[f.TranEnv];
                    f.BordersNoise[2] = aR;
                }
                break;
            }
        }
        f.NEnvelopes = nEnv;
        f.NNoiseEnvelopes = nEnv == 1 ? 1 : 2;
        if (cls is VarFix or FixVar)
        {
            f.BordersNoise[0] = f.Borders[0];
            f.BordersNoise[f.NNoiseEnvelopes] = f.Borders[nEnv];
        }
        if (br.Overrun) return false; return CheckGrid(f, numTimeSlots);
    }

    // number of bits for the transient pointer: ceil(log2(n + 2)) where the argument is n + 2
    private static int PointerBits(int v) => v <= 2 ? 1 : v <= 4 ? 2 : 3;

    private static bool CheckGrid(SbrFrame f, int numTimeSlots)
    {
        int nEnv = f.NEnvelopes, nNoise = f.NNoiseEnvelopes;
        if (nEnv < 1 || nEnv > SbrFrame.MaxEnvelopes || nNoise > SbrFrame.MaxNoiseEnvelopes) return false;
        int startPos = f.Borders[0], stopPos = f.Borders[nEnv];
        const int maxPos = 19; // 4.6.18.3.3: envelopes may extend three time slots into the next frame
        if (startPos < 0 || startPos >= stopPos) return false;
        if (startPos > maxPos - numTimeSlots) return false;
        if (stopPos < numTimeSlots || stopPos > maxPos) return false;
        for (int i = 0; i < nEnv; i++) if (f.Borders[i] >= f.Borders[i + 1]) return false;
        if (f.TranEnv > nEnv) return false;
        if (nEnv == 1 && nNoise > 1) return false;
        if (startPos != f.BordersNoise[0] || stopPos != f.BordersNoise[nNoise]) return false;
        for (int i = 0; i < nNoise; i++) if (f.BordersNoise[i] >= f.BordersNoise[i + 1]) return false;
        for (int i = 0; i < nNoise; i++)
        {
            int s = f.BordersNoise[i];
            int j;
            for (j = 0; j < nEnv; j++) if (f.Borders[j] == s) break;
            if (j == nEnv) return false;
        }
        return true;
    }

    private static void ReadDirections(ref AacBitReader br, SbrFrame f)
    {
        for (int i = 0; i < f.NEnvelopes; i++) f.DomainVec[i] = (int)br.Read(1);
        for (int i = 0; i < f.NNoiseEnvelopes; i++) f.DomainVecNoise[i] = (int)br.Read(1);
    }

    // 4.5.2.8.4 sbr_envelope
    private static bool ReadEnvelope(ref AacBitReader br, SbrBands bands, SbrHeader h, SbrFrame f)
    {
        int coupling = f.Coupling;
        int ampRes = h.AmpResolution;
        int nEnv = f.NEnvelopes;
        f.NScaleFactors = 0;
        if (f.FrameClass == FixFix && nEnv == 1) ampRes = 0;
        f.AmpResCurrent = ampRes;
        int startBits = ampRes == 1 ? 6 : 7;
        int startBitsBalance = ampRes == 1 ? 5 : 6;
        Span<int> noBand = stackalloc int[SbrFrame.MaxEnvelopes];
        for (int i = 0; i < nEnv; i++)
        {
            noBand[i] = bands.NSfb[f.FreqRes[i]];
            f.NScaleFactors += noBand[i];
        }
        if (f.NScaleFactors > SbrFrame.MaxEnvelopeValues) return false;
        ReadOnlySpan<sbyte> hcbT, hcbF;
        int comp;
        if (coupling == 2)
        {
            comp = 1;
            if (ampRes == 0) { hcbT = AacSbrTables.EnvBalance10T; hcbF = AacSbrTables.EnvBalance10F; }
            else { hcbT = AacSbrTables.EnvBalance11T; hcbF = AacSbrTables.EnvBalance11F; }
        }
        else
        {
            comp = 0;
            if (ampRes == 0) { hcbT = AacSbrTables.EnvLevel10T; hcbF = AacSbrTables.EnvLevel10F; }
            else { hcbT = AacSbrTables.EnvLevel11T; hcbF = AacSbrTables.EnvLevel11F; }
        }
        int offset = 0;
        for (int j = 0; j < nEnv; j++)
        {
            int dom = f.DomainVec[j];
            if (dom == 0)
            {
                f.Envelope[offset] = coupling == 2 ? (int)br.Read(startBitsBalance) << comp : (int)br.Read(startBits);
            }
            for (int i = 1 - dom; i < noBand[j]; i++)
            {
                int delta = Huff(dom == 0 ? hcbF : hcbT, ref br);
                f.Envelope[offset + i] = delta << comp;
            }
            offset += noBand[j];
            if (br.Overrun) return false;
        }
        return true;
    }

    // 4.5.2.8.5 sbr_noise
    private static void ReadNoise(ref AacBitReader br, SbrBands bands, SbrFrame f)
    {
        int nNoise = bands.NNoise;
        int coupling = f.Coupling;
        ReadOnlySpan<sbyte> hcbNoise, hcbF;
        int comp;
        if (coupling == 2) { hcbNoise = AacSbrTables.NoiseBalance11T; hcbF = AacSbrTables.EnvBalance11F; comp = 1; }
        else { hcbNoise = AacSbrTables.NoiseLevel11T; hcbF = AacSbrTables.EnvLevel11F; comp = 0; }
        for (int i = 0; i < f.NNoiseEnvelopes; i++)
        {
            if (f.DomainVecNoise[i] == 0)
            {
                f.NoiseFloor[i * nNoise] = (int)br.Read(5) << comp;
                for (int j = 1; j < nNoise; j++) f.NoiseFloor[i * nNoise + j] = Huff(hcbF, ref br) << comp;
            }
            else
            {
                for (int j = 0; j < nNoise; j++) f.NoiseFloor[i * nNoise + j] = Huff(hcbNoise, ref br) << comp;
            }
        }
    }

    // 4.5.2.8.6 sbr_sinusoidal_coding
    private static void ReadSinusoidal(ref AacBitReader br, SbrBands bands, SbrFrame f)
    {
        Array.Clear(f.AddHarmonics);
        if (br.ReadBit())
            for (int i = 0; i < bands.NSfb[1]; i++) f.AddHarmonics[i] = (byte)br.Read(1);
    }

    /// <summary>4.5.2.8.2 sbr_single_channel_element (without the leading header flag). Parametric stereo data in the extension goes to <paramref name="ps"/>.</summary>
    public static bool ReadSingle(ref AacBitReader br, SbrBands bands, SbrHeader h, SbrFrame f, AacPs? ps, int numTimeSlots)
    {
        f.Coupling = 0;
        if (br.ReadBit()) br.Skip(4); // bs_data_extra: bs_reserved
        if (!ReadGrid(ref br, f, numTimeSlots)) return false;
        ReadDirections(ref br, f);
        for (int i = 0; i < bands.NNoise; i++) f.InvfMode[i] = (int)br.Read(2);
        if (!ReadEnvelope(ref br, bands, h, f) || br.Overrun) return false;
        ReadNoise(ref br, bands, f);
        ReadSinusoidal(ref br, bands, f);
        return ReadExtension(ref br, ps) && !br.Overrun;
    }

    /// <summary>4.5.2.8.2 sbr_channel_pair_element (without the leading header flag).</summary>
    public static bool ReadPair(ref AacBitReader br, SbrBands bands, SbrHeader h, SbrFrame l, SbrFrame r, int numTimeSlots)
    {
        if (br.ReadBit()) { br.Skip(4); br.Skip(4); }
        bool coupled = br.ReadBit();
        l.Coupling = coupled ? 1 : 0;
        r.Coupling = coupled ? 2 : 0;
        if (!ReadGrid(ref br, l, numTimeSlots)) return false;
        if (coupled) r.CopyFrameInfoFrom(l);
        else if (!ReadGrid(ref br, r, numTimeSlots)) return false;
        ReadDirections(ref br, l);
        ReadDirections(ref br, r);
        for (int i = 0; i < bands.NNoise; i++) l.InvfMode[i] = (int)br.Read(2);
        if (coupled)
        {
            for (int i = 0; i < bands.NNoise; i++) r.InvfMode[i] = l.InvfMode[i];
            if (!ReadEnvelope(ref br, bands, h, l)) return false;
            ReadNoise(ref br, bands, l);
            if (!ReadEnvelope(ref br, bands, h, r)) return false;
        }
        else
        {
            for (int i = 0; i < bands.NNoise; i++) r.InvfMode[i] = (int)br.Read(2);
            if (!ReadEnvelope(ref br, bands, h, l)) return false;
            if (!ReadEnvelope(ref br, bands, h, r)) return false;
            ReadNoise(ref br, bands, l);
        }
        ReadNoise(ref br, bands, r);
        ReadSinusoidal(ref br, bands, l);
        ReadSinusoidal(ref br, bands, r);
        return ReadExtension(ref br, null) && !br.Overrun;
    }

    // 4.5.2.8.7 sbr_extension: extension id 2 carries parametric stereo; unknown payloads are skipped
    private static bool ReadExtension(ref AacBitReader br, AacPs? ps)
    {
        if (!br.ReadBit()) return true;
        int cnt = (int)br.Read(4);
        if (cnt == 15) cnt += (int)br.Read(8);
        int bitsLeft = 8 * cnt;
        bool psRead = false;
        while (bitsLeft > 7)
        {
            int id = (int)br.Read(2);
            bitsLeft -= 2;
            if (id == 2 && !psRead && ps is not null)
            {
                int used = ps.Read(ref br, bitsLeft);
                if (used < 0) return false;
                bitsLeft -= used;
                psRead = true;
                ps.DataAvailable = true;
            }
            else
            {
                if (id == 2 && ps is null) { /* PS data in a channel pair or unsupported use: skipped */ }
                int bytes = bitsLeft >> 3;
                br.Skip(bytes * 8);
                bitsLeft -= bytes * 8;
            }
            if (br.Overrun) return false;
        }
        br.Skip(bitsLeft);
        return true;
    }
}
