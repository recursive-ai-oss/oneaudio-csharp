// Copyright (c) Recursive AI, LLC. MIT OR Apache-2.0.
using System.Runtime.CompilerServices;

namespace OneAudio.Amr;

/// <summary>
/// The saturating 16/32-bit fixed-point operations that the AMR and AMR-WB specifications define their arithmetic
/// with (TS 26.073 and TS 26.173, "basic operators"). Both codecs are specified bit-exactly in terms of these
/// operators, including where they saturate and what they leave in the overflow flag, so every codec step is
/// written with them rather than with plain integer arithmetic. Word16 is <see cref="short"/>, Word32 is
/// <see cref="int"/>. <see cref="Overflow"/> is the sticky flag the decoders read after a synthesis filter pass.
/// </summary>
internal abstract class AmrFixed
{
    protected const int Max32 = int.MaxValue;
    protected const int Min32 = int.MinValue;
    protected const short Max16 = short.MaxValue;
    protected const short Min16 = short.MinValue;

    /// <summary>Set by any operator that saturates; cleared by the caller.</summary>
    protected bool Overflow;

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    protected short Saturate(int v)
    {
        if (v > short.MaxValue) { Overflow = true; return short.MaxValue; }
        if (v < short.MinValue) { Overflow = true; return short.MinValue; }
        return (short)v;
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    protected short Add(int a, int b) => Saturate(a + b);

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    protected short Sub(int a, int b) => Saturate(a - b);

    protected static short AbsS(short a) => a == short.MinValue ? short.MaxValue : (a < 0 ? (short)-a : a);

    protected static short Negate(short a) => a == short.MinValue ? short.MaxValue : (short)-a;

    protected short Shl(short a, int n)
    {
        if (n < 0)
        {
            if (n < -16) n = -16;
            return Shr(a, -n);
        }
        if (n > 15)
        {
            if (a == 0) return 0;
            Overflow = true;
            return a > 0 ? short.MaxValue : short.MinValue;
        }
        int r = a << n;
        if (r != (short)r)
        {
            Overflow = true;
            return a > 0 ? short.MaxValue : short.MinValue;
        }
        return (short)r;
    }

    protected short Shr(short a, int n)
    {
        if (n < 0)
        {
            if (n < -16) n = -16;
            return Shl(a, -n);
        }
        if (n >= 15) return (short)(a < 0 ? -1 : 0);
        return (short)(a >> n);
    }

    protected short ShrR(short a, int n)
    {
        if (n > 15) return 0;
        short r = Shr(a, n);
        if (n > 0 && (a & (1 << (n - 1))) != 0) r++;
        return r;
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    protected short Mult(int a, int b) => Saturate((a * b) >> 15);

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    protected short MultR(int a, int b) => Saturate((a * b + 0x4000) >> 15);

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    protected int LMult(int a, int b)
    {
        int p = a * b;
        if (p != 0x40000000) return p * 2;
        Overflow = true;
        return Max32;
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    protected int LAdd(int a, int b)
    {
        int r = unchecked(a + b);
        if (((a ^ b) & Min32) == 0 && ((r ^ a) & Min32) != 0)
        {
            Overflow = true;
            return a < 0 ? Min32 : Max32;
        }
        return r;
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    protected int LSub(int a, int b)
    {
        int r = unchecked(a - b);
        if (((a ^ b) & Min32) != 0 && ((r ^ a) & Min32) != 0)
        {
            Overflow = true;
            return a < 0 ? Min32 : Max32;
        }
        return r;
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    protected int LMac(int acc, int a, int b) => LAdd(acc, LMult(a, b));

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    protected int LMsu(int acc, int a, int b) => LSub(acc, LMult(a, b));

    protected static int LNegate(int a) => a == Min32 ? Max32 : -a;

    protected static int LAbs(int a) => a == Min32 ? Max32 : (a < 0 ? -a : a);

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    protected static short ExtractH(int a) => (short)(a >> 16);

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    protected static short ExtractL(int a) => (short)a;

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    protected short Round(int a) => ExtractH(LAdd(a, 0x8000));

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    protected static int LDepositH(short a) => a << 16;

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    protected static int LDepositL(short a) => a;

    protected short MacR(int acc, int a, int b) => ExtractH(LAdd(LMac(acc, a, b), 0x8000));

    protected short MsuR(int acc, int a, int b) => ExtractH(LAdd(LMsu(acc, a, b), 0x8000));

    protected int LShl(int a, int n)
    {
        if (n <= 0)
        {
            if (n < -32) n = -32;
            return LShr(a, -n);
        }
        int r = 0;
        for (; n > 0; n--)
        {
            if (a > 0x3fffffff) { Overflow = true; return Max32; }
            if (a < unchecked((int)0xc0000000)) { Overflow = true; return Min32; }
            a *= 2;
            r = a;
        }
        return r;
    }

    protected int LShr(int a, int n)
    {
        if (n < 0)
        {
            if (n < -32) n = -32;
            return LShl(a, -n);
        }
        if (n >= 31) return a < 0 ? -1 : 0;
        return a >> n;
    }

    protected int LShrR(int a, int n)
    {
        if (n > 31) return 0;
        int r = LShr(a, n);
        if (n > 0 && (a & (1 << (n - 1))) != 0) r++;
        return r;
    }

    protected static short NormS(short a)
    {
        if (a == 0) return 0;
        if (a == -1) return 15;
        if (a < 0) a = (short)~a;
        short n = 0;
        while (a < 0x4000) { a <<= 1; n++; }
        return n;
    }

    protected static short NormL(int a)
    {
        if (a == 0) return 0;
        if (a == -1) return 31;
        if (a < 0) a = ~a;
        short n = 0;
        while (a < 0x40000000) { a <<= 1; n++; }
        return n;
    }

    /// <summary>Fractional division of two non-negative Word16 values with var1 &lt;= var2, result in Q15.</summary>
    protected short DivS(short var1, short var2)
    {
        // The specification aborts on var1 > var2, negative operands and a zero denominator; a decoder fed
        // arbitrary bits must not, so those cases return 0 (they cannot occur for any valid parameter set).
        if (var1 > var2 || var1 < 0 || var2 <= 0) return 0;
        if (var1 == 0) return 0;
        if (var1 == var2) return short.MaxValue;
        int num = var1;
        int den = var2;
        short r = 0;
        for (int i = 0; i < 15; i++)
        {
            r <<= 1;
            num <<= 1;
            if (num >= den)
            {
                num = LSub(num, den);
                r = Add(r, 1);
            }
        }
        return r;
    }

    // ---- Double-precision helpers (TS 26.073 oper_32b.c / TS 26.173 oper_32b.c) ----

    protected void LExtract(int l32, out short hi, out short lo)
    {
        hi = ExtractH(l32);
        lo = ExtractL(LMsu(LShr(l32, 1), hi, 16384));
    }

    protected int LComp(short hi, short lo) => LMac(LDepositH(hi), lo, 1);

    protected int Mpy3232(short hi1, short lo1, short hi2, short lo2)
    {
        int l = LMult(hi1, hi2);
        l = LMac(l, Mult(hi1, lo2), 1);
        l = LMac(l, Mult(lo1, hi2), 1);
        return l;
    }

    protected int Mpy3216(short hi, short lo, short n)
    {
        int l = LMult(hi, n);
        l = LMac(l, Mult(lo, n), 1);
        return l;
    }

    protected int Div32(int num, short denomHi, short denomLo)
    {
        short approx = DivS(0x3fff, denomHi);
        int l = Mpy3216(denomHi, denomLo, approx);
        l = LSub(0x7fffffff, l);
        LExtract(l, out short hi, out short lo);
        l = Mpy3216(hi, lo, approx);
        LExtract(l, out hi, out lo);
        LExtract(num, out short nHi, out short nLo);
        l = Mpy3232(nHi, nLo, hi, lo);
        return LShl(l, 2);
    }
}
