using System.Numerics;
using static OneAudio.Opus.SilkMath;

namespace OneAudio.Opus;

/// <summary>Fixed-point helpers used only by the SILK encoder (the decoder-shared ones live in <see cref="SilkMath"/>).</summary>
internal static class SilkEncMath
{
    /// <summary>silk_lin2log: approximation of 128 * log2(x).</summary>
    public static int Lin2Log(int inLin)
    {
        int lz = BitOperations.LeadingZeroCount((uint)inLin);
        int frac = (int)BitOperations.RotateRight((uint)inLin, 24 - lz) & 0x7f;
        return ((31 - lz) << 7) + SMlaWB(frac, frac * (128 - frac), 179);
    }

    private static readonly int[] SigmSlopeQ10 = [237, 153, 73, 30, 12, 7];
    private static readonly int[] SigmPosQ15 = [16384, 23955, 28861, 31213, 32178, 32548];
    private static readonly int[] SigmNegQ15 = [16384, 8812, 3906, 1554, 589, 219];

    /// <summary>silk_sigm_Q15: sigmoid by table interpolation, input in Q5.</summary>
    public static int SigmQ15(int inQ5)
    {
        if (inQ5 < 0)
        {
            inQ5 = -inQ5;
            if (inQ5 >= 6 * 32) return 0;
            int ind = inQ5 >> 5;
            return SigmNegQ15[ind] - SMulBB(SigmSlopeQ10[ind], inQ5 & 0x1F);
        }
        else
        {
            if (inQ5 >= 6 * 32) return 32767;
            int ind = inQ5 >> 5;
            return SigmPosQ15[ind] + SMulBB(SigmSlopeQ10[ind], inQ5 & 0x1F);
        }
    }

    public static int AddPosSat32(int a, int b) => (((uint)a + (uint)b) & 0x80000000u) != 0 ? int.MaxValue : a + b;

    public static int Add32(int a, int b) => unchecked(a + b);

    /// <summary>silk_SMULWT: 32-bit times the top 16 bits.</summary>
    public static int SMulWT(int a, int b) => (int)(((long)a * (b >> 16)) >> 16);

    public static int SMlaWT(int a, int b, int c) => a + SMulWT(b, c);

    public static int SMulTT(int a, int b) => (a >> 16) * (b >> 16);

    public static int SMlaTT(int a, int b, int c) => a + (b >> 16) * (c >> 16);

    public static int Mla(int a, int b, int c) => unchecked(a + b * c);

    public static int SMlal(int a, int b, int c) => a + (int)(((long)b * c) >> 32);

    public static int LimitInt(int a, int lo, int hi) => lo > hi ? (a > lo ? lo : a < hi ? hi : a) : (a > hi ? hi : a < lo ? lo : a);

    /// <summary>silk_float2int as built by the reference: (int)floor(.5 + x) in double precision.</summary>
    public static int Float2Int(float x) => (int)Math.Floor(0.5 + x);

    public static short Float2Short(float x)
    {
        int i = Float2Int(x);
        return (short)(i > short.MaxValue ? short.MaxValue : i < short.MinValue ? short.MinValue : i);
    }

    /// <summary>silk_CLZ_FRAC.</summary>
    public static void ClzFrac(int input, out int lz, out int fracQ7)
    {
        lz = BitOperations.LeadingZeroCount((uint)input);
        fracQ7 = (int)BitOperations.RotateRight((uint)input, 24 - lz) & 0x7f;
    }

}
