namespace OneAudio.Opus;

internal sealed partial class CeltDecoder
{
    private const int SpreadNone = 0;
    private const int SpreadAggressive = 3;

    private readonly float[] _norm = new float[2 * 8 * 100];
    private readonly float[] _lowbandScratch = new float[8 * 22];
    private readonly float[] _hadamardTmp = new float[8 * 22];
    private readonly int[] _iy = new int[8 * 22 + 16];
    private readonly uint[] _cwrsU = new uint[132 + 2];

    private static uint LcgRand(uint seed) => unchecked(1664525 * seed + 1013904223);

    internal static int EcIlog(uint v) => 32 - System.Numerics.BitOperations.LeadingZeroCount(v);

    internal static int FracMul16(int a, int b) => (16384 + (short)a * (short)b) >> 15;

    // A cos() approximation that is bit-exact on every platform; it affects the bit allocation (RFC 6716 4.3.4.3).
    internal static int BitexactCos(int x)
    {
        int tmp = (4096 + x * x) >> 13;
        int x2 = (short)tmp;
        x2 = (short)((32767 - x2) + FracMul16(x2, -7651 + FracMul16(x2, 8277 + FracMul16(-626, x2))));
        return 1 + x2;
    }

    internal static int BitexactLog2Tan(int isin, int icos)
    {
        int lc = EcIlog((uint)icos);
        int ls = EcIlog((uint)isin);
        icos <<= 15 - lc;
        isin <<= 15 - ls;
        return (ls - lc) * (1 << 11)
            + FracMul16(isin, FracMul16(isin, -2597) + 7932)
            - FracMul16(icos, FracMul16(icos, -2597) + 7932);
    }

    internal static uint Isqrt32(uint val)
    {
        uint g = 0;
        int bshift = (EcIlog(val) - 1) >> 1;
        uint b = 1u << bshift;
        do
        {
            uint t = ((g << 1) + b) << bshift;
            if (t <= val)
            {
                g += b;
                val -= t;
            }
            b >>= 1;
            bshift--;
        } while (bshift >= 0);
        return g;
    }

    private static void RenormaliseVector(Span<float> x, float gain)
    {
        float e = 1e-15f;
        for (int i = 0; i < x.Length; i++) e += x[i] * x[i];
        float g = 1f / (float)Math.Sqrt(e) * gain;
        for (int i = 0; i < x.Length; i++) x[i] *= g;
    }

    // ------------------------------------------------------------------------------------------------
    // Anti-collapse (RFC 6716 section 4.3.5)
    // ------------------------------------------------------------------------------------------------

    private static void AntiCollapse(float[] x, byte[] collapseMasks, int lm, int c, int size, int start, int end,
        float[] logE, float[] prev1LogE, float[] prev2LogE, int[] pulses, uint seed)
    {
        var eb = CeltTables.EBands5ms;
        for (int i = start; i < end; i++)
        {
            int n0 = eb[i + 1] - eb[i];
            int depth = (1 + pulses[i]) / ((eb[i + 1] - eb[i]) << lm);
            float thresh = .5f * Exp2(-.125f * depth);
            float sqrt1 = 1f / (float)Math.Sqrt(n0 << lm);
            for (int ch = 0; ch < c; ch++)
            {
                float prev1 = prev1LogE[ch * NbEBands + i];
                float prev2 = prev2LogE[ch * NbEBands + i];
                if (c == 1)
                {
                    prev1 = Math.Max(prev1, prev1LogE[NbEBands + i]);
                    prev2 = Math.Max(prev2, prev2LogE[NbEBands + i]);
                }
                float ediff = logE[ch * NbEBands + i] - Math.Min(prev1, prev2);
                ediff = Math.Max(0, ediff);
                // r is scaled by 2 or 2*sqrt(2) because short blocks do not have the same energy as long ones.
                float r = 2f * Exp2(-ediff);
                if (lm == 3) r *= 1.41421356f;
                r = Math.Min(thresh, r);
                r *= sqrt1;
                int xo = ch * size + (eb[i] << lm);
                bool renormalize = false;
                for (int k = 0; k < 1 << lm; k++)
                {
                    if ((collapseMasks[i * c + ch] & 1 << k) == 0)
                    {
                        for (int j = 0; j < n0; j++)
                        {
                            seed = LcgRand(seed);
                            x[xo + (j << lm) + k] = (seed & 0x8000) != 0 ? r : -r;
                        }
                        renormalize = true;
                    }
                }
                if (renormalize) RenormaliseVector(x.AsSpan(xo, n0 << lm), 1f);
            }
        }
    }

    // ------------------------------------------------------------------------------------------------
    // PVQ shape decoding (RFC 6716 sections 4.3.4.1 - 4.3.4.4)
    // ------------------------------------------------------------------------------------------------

    internal static void ExpRotation1(Span<float> x, int len, int stride, float c, float s)
    {
        int p = 0;
        for (int i = 0; i < len - stride; i++)
        {
            float x1 = x[p];
            float x2 = x[p + stride];
            x[p + stride] = c * x2 + s * x1;
            x[p++] = c * x1 - s * x2;
        }
        p = len - 2 * stride - 1;
        for (int i = len - 2 * stride - 1; i >= 0; i--)
        {
            float x1 = x[p];
            float x2 = x[p + stride];
            x[p + stride] = c * x2 + s * x1;
            x[p--] = c * x1 - s * x2;
        }
    }

    internal static float CosNorm(float x) => (float)Math.Cos((double)((.5f * Pi) * x));

    private static readonly int[] SpreadFactor = [15, 10, 5];

    internal static void ExpRotation(Span<float> x, int len, int dir, int stride, int k, int spread)
    {
        if (2 * k >= len || spread == SpreadNone) return;
        int factor = SpreadFactor[spread - 1];
        float gain = (float)len / (len + factor * k);
        float theta = .5f * (gain * gain);
        float c = CosNorm(theta);
        float s = CosNorm(1f - theta);
        int stride2 = 0;
        if (len >= 8 * stride)
        {
            // A simple way of computing sqrt(len/stride) with rounding.
            stride2 = 1;
            while ((stride2 * stride2 + stride2) * stride + (stride >> 2) < len) stride2++;
        }
        len /= stride;
        for (int i = 0; i < stride; i++)
        {
            var sub = x.Slice(i * len);
            if (dir < 0)
            {
                if (stride2 != 0) ExpRotation1(sub, len, stride2, s, c);
                ExpRotation1(sub, len, 1, c, s);
            }
            else
            {
                ExpRotation1(sub, len, 1, c, -s);
                if (stride2 != 0) ExpRotation1(sub, len, stride2, s, -c);
            }
        }
    }

    // Combinatorial recurrence u[i][j] = u[i-1][j] + u[i][j-1] + u[i-1][j-1] (RFC 6716 section 4.3.4.2),
    // applied to ui[off .. off + len).
    internal static void Unext(uint[] ui, int off, int len, uint ui0)
    {
        int j = 1;
        do
        {
            uint ui1 = unchecked(ui[off + j] + ui[off + j - 1] + ui0);
            ui[off + j - 1] = ui0;
            ui0 = ui1;
        } while (++j < len);
        ui[off + j - 1] = ui0;
    }

    internal static void Uprev(uint[] ui, int n, uint ui0)
    {
        int j = 1;
        do
        {
            uint ui1 = unchecked(ui[j] - ui[j - 1] - ui0);
            ui[j - 1] = ui0;
            ui0 = ui1;
        } while (++j < n);
        ui[j - 1] = ui0;
    }

    // Returns V(n, k) and fills u[0..k+1] with row n of U. Requires n >= 2 and k >= 1.
    internal static uint NcwrsUrow(int n, int k, uint[] u)
    {
        int len = k + 2;
        u[0] = 0;
        u[1] = 1;
        for (int kk = 2; kk < len; kk++) u[kk] = (uint)((kk << 1) - 1);
        for (int kk = 2; kk < n; kk++) Unext(u, 1, k + 1, 1);
        return unchecked(u[k] + u[k + 1]);
    }

    // Turns a combination index into a pulse vector (cwrsi).
    internal static void Cwrsi(int n, int k, uint i, Span<int> y, uint[] u)
    {
        int j = 0;
        do
        {
            uint p = u[k + 1];
            int s = i >= p ? -1 : 0;
            i -= p & (uint)s;
            int yj = k;
            p = u[k];
            while (p > i) p = u[--k];
            i -= p;
            yj -= k;
            y[j] = (yj + s) ^ s;
            Uprev(u, k + 2, 0);
        } while (++j < n);
    }

    /// <summary>Decodes the K-pulse shape of an N-sample band and scales it to <paramref name="gain"/> (alg_unquant).</summary>
    private uint AlgUnquant(Span<float> x, int n, int k, int spread, int b, ref RangeDecoder dec, float gain)
    {
        var iy = _iy.AsSpan(0, n);
        var u = _cwrsU;
        uint nc = NcwrsUrow(n, k, u);
        Cwrsi(n, k, dec.DecodeUInt(nc), iy, u);
        float ryy = 0;
        for (int i = 0; i < n; i++) ryy += (float)iy[i] * iy[i];
        float g = 1f / (float)Math.Sqrt(ryy) * gain;
        for (int i = 0; i < n; i++) x[i] = g * iy[i];
        ExpRotation(x, n, -1, b, k, spread);
        // Collapse mask: which of the B short blocks received a pulse.
        if (b <= 1) return 1;
        int n0 = n / b;
        uint mask = 0;
        for (int i = 0; i < b; i++)
            for (int j = 0; j < n0; j++)
                mask |= (iy[i * n0 + j] != 0 ? 1u : 0u) << i;
        return mask;
    }
}
