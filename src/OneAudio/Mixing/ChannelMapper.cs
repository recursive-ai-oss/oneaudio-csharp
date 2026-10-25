using System.Numerics;

namespace OneAudio.Mixing;

/// <summary>
/// Builds the matrix that maps a source's channels onto the mixer's output channels, with pan applied.
/// <para>
/// A matrix is stored row-major by output channel: <c>matrix[output * inputChannels + input]</c>.
/// </para>
/// <para><b>Rules</b> (layouts come from <see cref="AudioFormat.ChannelMask"/>; a missing mask means the conventional
/// layout for the channel count, and counts without a convention are mapped by index):</para>
/// <list type="bullet">
/// <item><description>Mono source: a point source panned across the output's front left/right pair with a constant-power law
/// (centre = -3 dB per side). A mono output gets it at unity. Outputs without a front pair use channels 0 and 1.</description></item>
/// <item><description>A speaker that exists in the output goes straight to it at unity, including LFE.</description></item>
/// <item><description>A missing speaker is folded: centre to front left/right at -3 dB; back and side speakers to each other
/// (-3 dB when the source also has the target pair), else to the front pair at -3 dB; back centre to the back pair, else the
/// side pair, else the front pair at -3 dB; front-of-centre pair to the front pair. LFE is dropped when the output has no LFE.
/// A mono output takes front left/right at -6 dB each, centre at unity, everything else at -9 dB.</description></item>
/// <item><description>Upmixing never invents content: a stereo source on a larger layout plays on front left/right only.</description></item>
/// <item><description>Pan on a non-mono source is a balance: left-side speakers are attenuated linearly for a positive pan, right-side
/// ones for a negative pan; centre, LFE and back centre are unaffected.</description></item>
/// </list>
/// Down-mix sums are not normalized, so loud multichannel material can exceed full scale when folded; the mixer does not clip.
/// </summary>
internal static class ChannelMapper
{
    private const float Minus3Db = 0.70710678f;

    private const uint Left = (uint)(ChannelMask.FrontLeft | ChannelMask.BackLeft | ChannelMask.SideLeft | ChannelMask.FrontLeftOfCenter);
    private const uint Right = (uint)(ChannelMask.FrontRight | ChannelMask.BackRight | ChannelMask.SideRight | ChannelMask.FrontRightOfCenter);

    /// <summary>
    /// Returns the mask to use for <paramref name="channels"/> channels: the given mask when its bit count matches, else the
    /// conventional layout, else <see cref="ChannelMask.None"/> (map by index).
    /// </summary>
    public static ChannelMask Resolve(ChannelMask mask, int channels)
    {
        if (mask != ChannelMask.None && BitOperations.PopCount((uint)mask) == channels) return mask;
        return AudioFormat.DefaultMask(channels);
    }

    /// <summary>Fills <paramref name="matrix"/> (at least <c>outChannels * inChannels</c> long); the rest is untouched.</summary>
    public static void Build(Span<float> matrix, int inChannels, ChannelMask inMask, int outChannels, ChannelMask outMask, float pan)
    {
        matrix = matrix[..(outChannels * inChannels)];
        matrix.Clear();
        uint outBits = (uint)Resolve(outMask, outChannels);
        uint inBits = (uint)Resolve(inMask, inChannels);

        if (inChannels == 1)
        {
            if (outChannels == 1) { matrix[0] = 1f; return; }
            int l = 0, r = 1;
            if (outBits != 0 && Has(outBits, ChannelMask.FrontLeft) && Has(outBits, ChannelMask.FrontRight))
            {
                l = Index(outBits, ChannelMask.FrontLeft);
                r = Index(outBits, ChannelMask.FrontRight);
            }
            double a = (pan + 1.0) * (Math.PI / 4.0);
            matrix[l * inChannels] = (float)Math.Cos(a);
            matrix[r * inChannels] = (float)Math.Sin(a);
            return;
        }

        if (inBits == 0 || outBits == 0)
        {
            // No known layout on one side: channel i feeds channel i; extras are dropped and missing channels stay silent.
            int n = Math.Min(inChannels, outChannels);
            for (int i = 0; i < n; i++)
                matrix[i * inChannels + i] = i == 0 ? LeftWeight(pan) : i == 1 ? RightWeight(pan) : 1f;
            return;
        }

        int idx = 0;
        for (uint remaining = inBits; remaining != 0; remaining &= remaining - 1, idx++)
        {
            uint bit = 1u << BitOperations.TrailingZeroCount(remaining);
            float w = (bit & Left) != 0 ? LeftWeight(pan) : (bit & Right) != 0 ? RightWeight(pan) : 1f;
            Route(matrix, idx, inChannels, (ChannelMask)bit, inBits, outBits, w);
        }
    }

    private static float LeftWeight(float pan) => pan > 0f ? 1f - pan : 1f;

    private static float RightWeight(float pan) => pan < 0f ? 1f + pan : 1f;

    private static bool Has(uint mask, ChannelMask bit) => (mask & (uint)bit) != 0;

    private static bool HasAll(uint mask, ChannelMask bits) => (mask & (uint)bits) == (uint)bits;

    private static int Index(uint mask, ChannelMask bit) => BitOperations.PopCount(mask & ((uint)bit - 1));

    private static void Put(Span<float> m, int input, int inCh, uint outBits, ChannelMask target, float coef)
        => m[Index(outBits, target) * inCh + input] += coef;

    private static void Route(Span<float> m, int input, int inCh, ChannelMask speaker, uint inBits, uint outBits, float w)
    {
        const ChannelMask FL = ChannelMask.FrontLeft, FR = ChannelMask.FrontRight, FC = ChannelMask.FrontCenter;
        const ChannelMask BL = ChannelMask.BackLeft, BR = ChannelMask.BackRight, SL = ChannelMask.SideLeft, SR = ChannelMask.SideRight;

        if (Has(outBits, speaker)) { Put(m, input, inCh, outBits, speaker, w); return; }

        bool monoOut = outBits == (uint)FC;
        bool left = (speaker & (ChannelMask)Left) != 0;
        ChannelMask front = left ? FL : FR;
        bool frontPair = HasAll(outBits, FL | FR);

        switch (speaker)
        {
            case FL or FR:
                if (Has(outBits, FC)) Put(m, input, inCh, outBits, FC, 0.5f * w);
                break;
            case FC:
                if (frontPair) { Put(m, input, inCh, outBits, FL, Minus3Db * w); Put(m, input, inCh, outBits, FR, Minus3Db * w); }
                break;
            case ChannelMask.LowFrequency:
                break;
            case BL or BR:
            {
                ChannelMask side = left ? SL : SR;
                if (Has(outBits, side)) Put(m, input, inCh, outBits, side, Has(inBits, side) ? Minus3Db * w : w);
                else if (Has(outBits, front)) Put(m, input, inCh, outBits, front, Minus3Db * w);
                else if (monoOut) Put(m, input, inCh, outBits, FC, 0.35355339f * w);
                break;
            }
            case SL or SR:
            {
                ChannelMask back = left ? BL : BR;
                if (Has(outBits, back)) Put(m, input, inCh, outBits, back, Has(inBits, back) ? Minus3Db * w : w);
                else if (Has(outBits, front)) Put(m, input, inCh, outBits, front, Minus3Db * w);
                else if (monoOut) Put(m, input, inCh, outBits, FC, 0.35355339f * w);
                break;
            }
            case ChannelMask.BackCenter:
                if (HasAll(outBits, BL | BR)) { Put(m, input, inCh, outBits, BL, Minus3Db * w); Put(m, input, inCh, outBits, BR, Minus3Db * w); }
                else if (HasAll(outBits, SL | SR)) { Put(m, input, inCh, outBits, SL, Minus3Db * w); Put(m, input, inCh, outBits, SR, Minus3Db * w); }
                else if (frontPair) { Put(m, input, inCh, outBits, FL, Minus3Db * w); Put(m, input, inCh, outBits, FR, Minus3Db * w); }
                else if (monoOut) Put(m, input, inCh, outBits, FC, 0.35355339f * w);
                break;
            case ChannelMask.FrontLeftOfCenter or ChannelMask.FrontRightOfCenter:
                if (Has(outBits, front)) Put(m, input, inCh, outBits, front, w);
                else if (Has(outBits, FC)) Put(m, input, inCh, outBits, FC, 0.5f * w);
                break;
        }
    }
}
