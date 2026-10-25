namespace OneAudio.Opus;

// Bit allocation with the encoder's skip, intensity and dual-stereo decisions (RFC 6716 reference, rate.c).
internal sealed partial class CeltEncoder
{
    private readonly int[] _bits1 = new int[NbEBands];
    private readonly int[] _bits2 = new int[NbEBands];
    private readonly int[] _thresh = new int[NbEBands];
    private readonly int[] _trimOffset = new int[NbEBands];

    private int ComputeAllocation(int start, int end, int[] offsets, int[] cap, int allocTrim, ref int intensity,
        ref int dualStereo, int total, out int balance, int[] pulses, int[] ebits, int[] finePriority, int c, int lm,
        ref RangeEncoder enc, int prev)
    {
        var eb = CeltTables.EBands5ms;
        var bits1 = _bits1;
        var bits2 = _bits2;
        var thresh = _thresh;
        var trimOffset = _trimOffset;
        var allocVectors = CeltTables.BandAllocation;
        const int len = NbEBands;

        total = Math.Max(total, 0);
        int skipStart = start;
        int skipRsv = total >= 1 << Bitres ? 1 << Bitres : 0;
        total -= skipRsv;
        int intensityRsv = 0, dualStereoRsv = 0;
        if (c == 2)
        {
            intensityRsv = CeltTables.Log2FracTable[end - start];
            if (intensityRsv > total) intensityRsv = 0;
            else
            {
                total -= intensityRsv;
                dualStereoRsv = total >= 1 << Bitres ? 1 << Bitres : 0;
                total -= dualStereoRsv;
            }
        }
        for (int j = start; j < end; j++)
        {
            int w = eb[j + 1] - eb[j];
            thresh[j] = Math.Max(c << Bitres, (3 * w << lm << Bitres) >> 4);
            trimOffset[j] = c * w * (allocTrim - 5 - lm) * (end - j - 1) * (1 << (lm + Bitres)) >> 6;
            // Single-coefficient bands get more benefit from having one coarse value per coefficient.
            if (w << lm == 1) trimOffset[j] -= c << Bitres;
        }

        int lo = 1, hi = CeltTables.NbAllocVectors - 1;
        do
        {
            bool done = false;
            int psum = 0;
            int mid = (lo + hi) >> 1;
            for (int j = end; j-- > start;)
            {
                int n = eb[j + 1] - eb[j];
                int bitsj = c * n * allocVectors[mid * len + j] << lm >> 2;
                if (bitsj > 0) bitsj = Math.Max(0, bitsj + trimOffset[j]);
                bitsj += offsets[j];
                if (bitsj >= thresh[j] || done)
                {
                    done = true;
                    psum += Math.Min(bitsj, cap[j]);
                }
                else if (bitsj >= c << Bitres)
                {
                    psum += c << Bitres;
                }
            }
            if (psum > total) hi = mid - 1;
            else lo = mid + 1;
        } while (lo <= hi);
        hi = lo--;

        for (int j = start; j < end; j++)
        {
            int n = eb[j + 1] - eb[j];
            int bits1j = c * n * allocVectors[lo * len + j] << lm >> 2;
            int bits2j = hi >= CeltTables.NbAllocVectors ? cap[j] : c * n * allocVectors[hi * len + j] << lm >> 2;
            if (bits1j > 0) bits1j = Math.Max(0, bits1j + trimOffset[j]);
            if (bits2j > 0) bits2j = Math.Max(0, bits2j + trimOffset[j]);
            if (lo > 0) bits1j += offsets[j];
            bits2j += offsets[j];
            if (offsets[j] > 0) skipStart = j;
            bits2j = Math.Max(0, bits2j - bits1j);
            bits1[j] = bits1j;
            bits2[j] = bits2j;
        }

        return InterpBits2Pulses(start, end, skipStart, bits1, bits2, thresh, cap, total, out balance, skipRsv,
            ref intensity, intensityRsv, ref dualStereo, dualStereoRsv, pulses, ebits, finePriority, c, lm, ref enc, prev);
    }

    private static int InterpBits2Pulses(int start, int end, int skipStart, int[] bits1, int[] bits2, int[] thresh,
        int[] cap, int total, out int outBalance, int skipRsv, ref int intensity, int intensityRsv, ref int dualStereo,
        int dualStereoRsv, int[] bits, int[] ebits, int[] finePriority, int c, int lm, ref RangeEncoder enc, int prev)
    {
        const int allocSteps = 6;
        var eb = CeltTables.EBands5ms;
        var logN = CeltTables.LogN;
        int allocFloor = c << Bitres;
        int stereo = c > 1 ? 1 : 0;
        int logM = lm << Bitres;
        int lo = 0, hi = 1 << allocSteps;
        for (int i = 0; i < allocSteps; i++)
        {
            int mid = (lo + hi) >> 1;
            int psum = 0;
            bool done = false;
            for (int j = end; j-- > start;)
            {
                int tmp = bits1[j] + (mid * bits2[j] >> allocSteps);
                if (tmp >= thresh[j] || done)
                {
                    done = true;
                    psum += Math.Min(tmp, cap[j]);
                }
                else if (tmp >= allocFloor)
                {
                    psum += allocFloor;
                }
            }
            if (psum > total) hi = mid;
            else lo = mid;
        }
        {
            int psum = 0;
            bool done = false;
            for (int j = end; j-- > start;)
            {
                int tmp = bits1[j] + (lo * bits2[j] >> allocSteps);
                if (tmp < thresh[j] && !done)
                {
                    tmp = tmp >= allocFloor ? allocFloor : 0;
                }
                else
                {
                    done = true;
                }
                tmp = Math.Min(tmp, cap[j]);
                bits[j] = tmp;
                psum += tmp;
            }

            int codedBands;
            for (codedBands = end; ; codedBands--)
            {
                int j = codedBands - 1;
                // Never skip the first band, nor a band boosted by dynamic allocation.
                if (j <= skipStart)
                {
                    total += skipRsv;
                    break;
                }
                // Left-over bits this band would get, including bits taken back from higher, skipped bands.
                int left = total - psum;
                int percoeff = left / (eb[codedBands] - eb[start]);
                left -= (eb[codedBands] - eb[start]) * percoeff;
                int rem = Math.Max(left - (eb[j] - eb[start]), 0);
                int bandWidth = eb[codedBands] - eb[j];
                int bandBits = bits[j] + percoeff * bandWidth + rem;
                // Only code a skip decision above the threshold for this band; otherwise it is force-skipped.
                if (bandBits >= Math.Max(thresh[j], allocFloor + (1 << Bitres)))
                {
                    // Hysteresis: bands that were coded last frame are kept unless clearly starved.
                    if (bandBits > ((j < prev ? 7 : 9) * bandWidth << lm << Bitres) >> 4)
                    {
                        enc.BitLogP(1, 1);
                        break;
                    }
                    enc.BitLogP(0, 1);
                    psum += 1 << Bitres;
                    bandBits -= 1 << Bitres;
                }
                psum -= bits[j] + intensityRsv;
                if (intensityRsv > 0) intensityRsv = CeltTables.Log2FracTable[j - start];
                psum += intensityRsv;
                if (bandBits >= allocFloor)
                {
                    psum += allocFloor;
                    bits[j] = allocFloor;
                }
                else
                {
                    bits[j] = 0;
                }
            }

            if (intensityRsv > 0)
            {
                intensity = Math.Min(intensity, codedBands);
                enc.EncodeUInt((uint)(intensity - start), (uint)(codedBands + 1 - start));
            }
            else intensity = 0;
            if (intensity <= start)
            {
                total += dualStereoRsv;
                dualStereoRsv = 0;
            }
            if (dualStereoRsv > 0) enc.BitLogP(dualStereo, 1);
            else dualStereo = 0;

            {
                int left = total - psum;
                int percoeff = left / (eb[codedBands] - eb[start]);
                left -= (eb[codedBands] - eb[start]) * percoeff;
                for (int j = start; j < codedBands; j++)
                    bits[j] += percoeff * (eb[j + 1] - eb[j]);
                for (int j = start; j < codedBands; j++)
                {
                    int tmp = Math.Min(left, eb[j + 1] - eb[j]);
                    bits[j] += tmp;
                    left -= tmp;
                }
            }

            int balance = 0;
            int jj;
            for (jj = start; jj < codedBands; jj++)
            {
                int n0 = eb[jj + 1] - eb[jj];
                int n = n0 << lm;
                bits[jj] += balance;
                int excess;
                if (n > 1)
                {
                    excess = Math.Max(bits[jj] - cap[jj], 0);
                    bits[jj] -= excess;
                    int den = c * n + ((c == 2 && n > 2 && dualStereo == 0 && jj < intensity) ? 1 : 0);
                    int nClogN = den * (logN[jj] + logM);
                    // Offset for the number of fine bits by log2(N)/2 + FINE_OFFSET relative to the fair share.
                    int offset = (nClogN >> 1) - den * 21;
                    if (n == 2) offset += den << Bitres >> 2;
                    // Changing the offset for allocating the second and third fine energy bit.
                    if (bits[jj] + offset < den * 2 << Bitres) offset += nClogN >> 2;
                    else if (bits[jj] + offset < den * 3 << Bitres) offset += nClogN >> 3;
                    ebits[jj] = Math.Max(0, (bits[jj] + offset + (den << (Bitres - 1))) / (den << Bitres));
                    if (c * ebits[jj] > (bits[jj] >> Bitres))
                        ebits[jj] = bits[jj] >> stereo >> Bitres;
                    ebits[jj] = Math.Min(ebits[jj], 8);
                    // If we rounded down or capped this band, make it a candidate for the final fine energy pass.
                    finePriority[jj] = ebits[jj] * (den << Bitres) >= bits[jj] + offset ? 1 : 0;
                    bits[jj] -= c * ebits[jj] << Bitres;
                }
                else
                {
                    excess = Math.Max(0, bits[jj] - (c << Bitres));
                    bits[jj] -= excess;
                    ebits[jj] = 0;
                    finePriority[jj] = 1;
                }
                // Fine energy cannot use the re-balancing in the band decoder, so do it here.
                if (excess > 0)
                {
                    int extraFine = Math.Min(excess >> (stereo + Bitres), 8 - ebits[jj]);
                    ebits[jj] += extraFine;
                    int extraBits = extraFine * c << Bitres;
                    finePriority[jj] = extraBits >= excess - balance ? 1 : 0;
                    excess -= extraBits;
                }
                balance = excess;
            }
            outBalance = balance;
            for (; jj < end; jj++)
            {
                ebits[jj] = bits[jj] >> stereo >> Bitres;
                bits[jj] = 0;
                finePriority[jj] = ebits[jj] < 1 ? 1 : 0;
            }
            return codedBands;
        }
    }
}
