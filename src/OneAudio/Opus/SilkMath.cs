using System.Numerics;
using System.Runtime.CompilerServices;

namespace OneAudio.Opus;

/// <summary>
/// The fixed-point primitives of the SILK layer (RFC 6716 section 4.2). The decoder is specified bit-exactly in
/// 32-bit integer arithmetic with wrap-around, so these keep the exact rounding of the reference macros.
/// Derived from the reference decoder in RFC 6716 (Simplified BSD licence, see THIRD-PARTY-NOTICES.md).
/// </summary>
internal static class SilkMath
{
    /// <summary>32x16 bit multiply keeping the top 32 bits (silk_SMULWB).</summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static int SMulWB(int a, int b) => (int)(((long)a * (short)b) >> 16);

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static int SMlaWB(int a, int b, int c) => a + (int)(((long)b * (short)c) >> 16);

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static int SMulBB(int a, int b) => (short)a * (short)b;

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static int SMlaBB(int a, int b, int c) => a + (short)b * (short)c;

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static int SMulWW(int a, int b) => SMulWB(a, b) + a * RShiftRound(b, 16);

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static int SMlaWW(int a, int b, int c) => SMlaWB(a, b, c) + b * RShiftRound(c, 16);

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static int SMMul(int a, int b) => (int)(((long)a * b) >> 32);

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static int RShiftRound(int a, int shift) => shift == 1 ? (a >> 1) + (a & 1) : ((a >> (shift - 1)) + 1) >> 1;

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static long RShiftRound64(long a, int shift) => shift == 1 ? (a >> 1) + (a & 1) : ((a >> (shift - 1)) + 1) >> 1;

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static int Sat16(int a) => a > short.MaxValue ? short.MaxValue : a < short.MinValue ? short.MinValue : a;

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static int Limit(int a, int limit1, int limit2) =>
        limit1 > limit2 ? (a > limit1 ? limit1 : a < limit2 ? limit2 : a) : (a > limit2 ? limit2 : a < limit1 ? limit1 : a);

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static int Clz32(int a) => a == 0 ? 32 : BitOperations.LeadingZeroCount((uint)a);

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static int Abs(int a) => a > 0 ? a : -a;

    /// <summary>Linear congruential generator for dithering and noise substitution (silk_RAND).</summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static int Rand(int seed) => unchecked(907633515 + seed * 196314165);

    public static int SubSat32(int a, int b)
    {
        int s = unchecked(a - b);
        return ((a ^ b) & int.MinValue) == 0 ? s : ((s ^ a) & int.MinValue) == 0 ? s : (a < 0 ? int.MinValue : int.MaxValue);
    }

    public static int LShiftSat32(int a, int shift) => Limit(a, int.MinValue >> shift, int.MaxValue >> shift) << shift;

    /// <summary>Approximate square root in Q0.5 precision used by the loss-concealment gain match.</summary>
    public static int SqrtApprox(int x)
    {
        if (x <= 0) return 0;
        int lz = Clz32(x);
        int frac = (int)BitOperations.RotateRight((uint)x, 24 - lz) & 0x7f;
        int y = (lz & 1) != 0 ? 32768 : 46214;
        y >>= lz >> 1;
        return SMlaWB(y, y, SMulBB(213, frac));
    }

    public static int Div32VarQ(int a32, int b32, int qRes)
    {
        int aHeadrm = Clz32(Abs(a32)) - 1;
        int a32Nrm = a32 << aHeadrm;
        int bHeadrm = Clz32(Abs(b32)) - 1;
        int b32Nrm = b32 << bHeadrm;
        int b32Inv = (int.MaxValue >> 2) / (b32Nrm >> 16);
        int result = SMulWB(a32Nrm, b32Inv);
        a32Nrm = unchecked(a32Nrm - (SMMul(b32Nrm, result) << 3));
        result = SMlaWB(result, a32Nrm, b32Inv);
        int lshift = 29 + aHeadrm - bHeadrm - qRes;
        if (lshift < 0) return LShiftSat32(result, -lshift);
        return lshift < 32 ? result >> lshift : 0;
    }

    public static int Inverse32VarQ(int b32, int qRes)
    {
        int bHeadrm = Clz32(Abs(b32)) - 1;
        int b32Nrm = b32 << bHeadrm;
        int b32Inv = (int.MaxValue >> 2) / (b32Nrm >> 16);
        int result = b32Inv << 16;
        int errQ32 = ((1 << 29) - SMulWB(b32Nrm, b32Inv)) << 3;
        result = SMlaWW(result, errQ32, b32Inv);
        int lshift = 61 - bHeadrm - qRes;
        if (lshift <= 0) return LShiftSat32(result, -lshift);
        return lshift < 32 ? result >> lshift : 0;
    }

    /// <summary>Converts a Q7 log2 gain to linear (silk_log2lin).</summary>
    public static int Log2Lin(int inLogQ7)
    {
        if (inLogQ7 < 0) return 0;
        int output = 1 << (inLogQ7 >> 7);
        int fracQ7 = inLogQ7 & 0x7F;
        int t = SMlaWB(fracQ7, SMulBB(fracQ7, 128 - fracQ7), -174);
        if (inLogQ7 < 2048) output += (output * t) >> 7;
        else output += (output >> 7) * t;
        return output;
    }

    /// <summary>Bandwidth expansion of a 16-bit LPC filter (silk_bwexpander).</summary>
    public static void BwExpander(Span<short> ar, int d, int chirpQ16)
    {
        int chirpMinusOne = chirpQ16 - 65536;
        for (int i = 0; i < d - 1; i++)
        {
            ar[i] = (short)RShiftRound(chirpQ16 * ar[i], 16);
            chirpQ16 += RShiftRound(chirpQ16 * chirpMinusOne, 16);
        }
        ar[d - 1] = (short)RShiftRound(chirpQ16 * ar[d - 1], 16);
    }

    /// <summary>Bandwidth expansion of a 32-bit LPC filter (silk_bwexpander_32).</summary>
    public static void BwExpander32(Span<int> ar, int d, int chirpQ16)
    {
        int chirpMinusOne = chirpQ16 - 65536;
        for (int i = 0; i < d - 1; i++)
        {
            ar[i] = SMulWW(chirpQ16, ar[i]);
            chirpQ16 += RShiftRound(chirpQ16 * chirpMinusOne, 16);
        }
        ar[d - 1] = SMulWW(chirpQ16, ar[d - 1]);
    }

    /// <summary>Energy of a block with a right shift that keeps the sum inside 30 bits (silk_sum_sqr_shift).</summary>
    public static void SumSqrShift(out int energy, out int shift, ReadOnlySpan<short> x, int len)
    {
        int nrg = 0, shft = 0, i = 0;
        len--;
        for (; i < len; i += 2)
        {
            nrg = unchecked(nrg + x[i] * x[i]);
            nrg = unchecked(nrg + x[i + 1] * x[i + 1]);
            if (nrg < 0)
            {
                nrg = (int)((uint)nrg >> 2);
                shft = 2;
                break;
            }
        }
        for (; i < len; i += 2)
        {
            int tmp = x[i] * x[i];
            tmp = unchecked(tmp + x[i + 1] * x[i + 1]);
            nrg = unchecked((int)((uint)nrg + ((uint)tmp >> shft)));
            if (nrg < 0)
            {
                nrg = (int)((uint)nrg >> 2);
                shft += 2;
            }
        }
        if (i == len)
        {
            int tmp = x[i] * x[i];
            nrg = unchecked((int)((uint)nrg + ((uint)tmp >> shft)));
        }
        if ((nrg & 0xC0000000) != 0)
        {
            nrg = (int)((uint)nrg >> 2);
            shft += 2;
        }
        shift = shft;
        energy = nrg;
    }

    /// <summary>Inverse prediction gain of an LPC filter in Q30, or 0 when the filter is unstable (silk_LPC_inverse_pred_gain).</summary>
    public static int LpcInversePredGain(ReadOnlySpan<short> aQ12, int order, Span<int> scratch)
    {
        const int QA = 24;
        const int ALimit = 16773022; // 0.99975 in Q24
        Span<int> a0 = scratch.Slice(0, 16), a1 = scratch.Slice(16, 16);
        Span<int> anew = (order & 1) != 0 ? a1 : a0;
        int dc = 0;
        for (int k = 0; k < order; k++)
        {
            dc += aQ12[k];
            anew[k] = aQ12[k] << (QA - 12);
        }
        if (dc >= 4096) return 0;

        int invGainQ30 = 1 << 30;
        for (int k = order - 1; k > 0; k--)
        {
            if (anew[k] > ALimit || anew[k] < -ALimit) return 0;
            int rcQ31 = -(anew[k] << (31 - QA));
            int rcMult1Q30 = (1 << 30) - SMMul(rcQ31, rcQ31);
            int mult2Q = 32 - Clz32(Abs(rcMult1Q30));
            int rcMult2 = Inverse32VarQ(rcMult1Q30, mult2Q + 30);
            invGainQ30 = SMMul(invGainQ30, rcMult1Q30) << 2;
            Span<int> aold = anew;
            anew = (k & 1) != 0 ? a1 : a0;
            for (int n = 0; n < k; n++)
            {
                int tmpQA = SubSat32(aold[n], (int)RShiftRound64((long)aold[k - n - 1] * rcQ31, 31));
                long tmp64 = RShiftRound64((long)tmpQA * rcMult2, mult2Q);
                if (tmp64 > int.MaxValue || tmp64 < int.MinValue) return 0;
                anew[n] = (int)tmp64;
            }
        }
        if (anew[0] > ALimit || anew[0] < -ALimit) return 0;
        int rc = -(anew[0] << (31 - QA));
        int mult1 = (1 << 30) - SMMul(rc, rc);
        return SMMul(invGainQ30, mult1) << 2;
    }
}
