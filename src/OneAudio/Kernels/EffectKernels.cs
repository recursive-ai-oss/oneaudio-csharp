using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using System.Runtime.Intrinsics;

namespace OneAudio.Kernels;

/// <summary>Coefficients of the trapezoidal state-variable filter for one cutoff and damping (Zavalishin, "The Art of VA Filter Design"; Simper, "Solving the continuous SVF equations using trapezoidal integration").</summary>
internal readonly struct SvfCoefficients
{
    public readonly float A1, A2, A3, K;

    public SvfCoefficients(double cutoffHz, double q, double sampleRate)
    {
        // Bilinear transform with the cutoff pre-warped so the -3 dB (Q = 1/sqrt 2) point lands exactly on cutoffHz.
        double g = Math.Tan(Math.PI * cutoffHz / sampleRate);
        double k = 1.0 / q;
        double a1 = 1.0 / (1.0 + g * (g + k));
        A1 = (float)a1;
        A2 = (float)(g * a1);
        A3 = (float)(g * g * a1);
        K = (float)k;
    }
}

/// <summary>
/// Inner loops of the mixer's effects over planar buffers (one contiguous run per channel, <c>stride</c> floats apart).
/// Each has a <c>*Scalar</c> reference. The vector paths run the same operations in the same order per element and never
/// fuse a multiply with an add, so they match the references bit for bit. Callers have validated sizes.
/// </summary>
internal static class EffectKernels
{
    private static void CheckLayout(int length, int channels, int stride, int start, int frames)
    {
        if (channels < 0 || frames < 0 || start < 0 || stride < start + frames) throw new ArgumentException("Invalid planar layout.");
        if (channels > 0 && (long)(channels - 1) * stride + start + frames > length) throw new ArgumentException("Planar buffer too short.");
    }

    // ------------------------------------------------------------------ state-variable filter

    /// <summary>
    /// One channel of the filter, in place. The two integrator states are updated in place. The
    /// trapezoidal form stays stable however the coefficients move between calls.
    /// </summary>
    private static void SvfRow(ref float x, int frames, in SvfCoefficients c, ref float ic1Ref, ref float ic2Ref, bool highPass)
    {
        float a1 = c.A1, a2 = c.A2, a3 = c.A3, k = c.K;
        float ic1 = ic1Ref, ic2 = ic2Ref;
        for (int f = 0; f < frames; f++)
        {
            ref float p = ref Unsafe.Add(ref x, f);
            float s = p;
            float v3 = s - ic2;
            float v1 = a1 * ic1 + a2 * v3;
            float v2 = ic2 + a2 * ic1 + a3 * v3;
            ic1 = 2f * v1 - ic1;
            ic2 = 2f * v2 - ic2;
            p = highPass ? s - k * v1 - v2 : v2;
        }
        ic1Ref = ic1;
        ic2Ref = ic2;
    }

    /// <summary>Scalar reference of <see cref="Svf"/>.</summary>
    public static void SvfScalar(Span<float> data, int stride, int channels, int start, int frames, in SvfCoefficients c, Span<float> ic1, Span<float> ic2, bool highPass)
    {
        CheckLayout(data.Length, channels, stride, start, frames);
        if (ic1.Length < channels || ic2.Length < channels) throw new ArgumentException("State too short.");
        if (frames == 0) return;
        for (int ch = 0; ch < channels; ch++)
            SvfRow(ref data[ch * stride + start], frames, c, ref ic1[ch], ref ic2[ch], highPass);
    }

    /// <summary>
    /// Filters frames <c>[start, start + frames)</c> of every channel in place as a low-pass or high-pass. Channels are processed
    /// eight or four at a time, one per vector lane, since each channel's recursion is serial.
    /// </summary>
    public static void Svf(Span<float> data, int stride, int channels, int start, int frames, in SvfCoefficients c, Span<float> ic1, Span<float> ic2, bool highPass)
    {
        CheckLayout(data.Length, channels, stride, start, frames);
        if (ic1.Length < channels || ic2.Length < channels) throw new ArgumentException("State too short.");
        if (frames == 0) return;
        ref float d0 = ref MemoryMarshal.GetReference(data);
        int ch = 0;
        if (frames >= 4 && Vector256.IsHardwareAccelerated)
            for (; ch + 8 <= channels; ch += 8) Svf8(ref d0, stride, ch, start, frames, c, ic1, ic2, highPass);
        if (frames >= 4 && Vector128.IsHardwareAccelerated)
            for (; ch + 4 <= channels; ch += 4) Svf4(ref d0, stride, ch, start, frames, c, ic1, ic2, highPass);
        for (; ch < channels; ch++)
            SvfRow(ref Unsafe.Add(ref d0, ch * stride + start), frames, c, ref ic1[ch], ref ic2[ch], highPass);
    }

    private static void Svf4(ref float d0, int stride, int ch, int start, int frames, in SvfCoefficients c, Span<float> ic1s, Span<float> ic2s, bool highPass)
    {
        ref float r0 = ref Unsafe.Add(ref d0, ch * stride + start);
        ref float r1 = ref Unsafe.Add(ref r0, stride);
        ref float r2 = ref Unsafe.Add(ref r1, stride);
        ref float r3 = ref Unsafe.Add(ref r2, stride);
        var a1 = Vector128.Create(c.A1); var a2 = Vector128.Create(c.A2); var a3 = Vector128.Create(c.A3); var k = Vector128.Create(c.K);
        var two = Vector128.Create(2f);
        var ic1 = Vector128.Create(ic1s[ch], ic1s[ch + 1], ic1s[ch + 2], ic1s[ch + 3]);
        var ic2 = Vector128.Create(ic2s[ch], ic2s[ch + 1], ic2s[ch + 2], ic2s[ch + 3]);
        for (int f = 0; f < frames; f++)
        {
            var s = Vector128.Create(Unsafe.Add(ref r0, f), Unsafe.Add(ref r1, f), Unsafe.Add(ref r2, f), Unsafe.Add(ref r3, f));
            var v3 = s - ic2;
            var v1 = a1 * ic1 + a2 * v3;
            var v2 = ic2 + a2 * ic1 + a3 * v3;
            ic1 = two * v1 - ic1;
            ic2 = two * v2 - ic2;
            var o = highPass ? s - k * v1 - v2 : v2;
            Unsafe.Add(ref r0, f) = o.GetElement(0);
            Unsafe.Add(ref r1, f) = o.GetElement(1);
            Unsafe.Add(ref r2, f) = o.GetElement(2);
            Unsafe.Add(ref r3, f) = o.GetElement(3);
        }
        for (int i = 0; i < 4; i++) { ic1s[ch + i] = ic1.GetElement(i); ic2s[ch + i] = ic2.GetElement(i); }
    }

    private static void Svf8(ref float d0, int stride, int ch, int start, int frames, in SvfCoefficients c, Span<float> ic1s, Span<float> ic2s, bool highPass)
    {
        ref float r0 = ref Unsafe.Add(ref d0, ch * stride + start);
        var a1 = Vector256.Create(c.A1); var a2 = Vector256.Create(c.A2); var a3 = Vector256.Create(c.A3); var k = Vector256.Create(c.K);
        var two = Vector256.Create(2f);
        var ic1 = Vector256.Create(ic1s[ch], ic1s[ch + 1], ic1s[ch + 2], ic1s[ch + 3], ic1s[ch + 4], ic1s[ch + 5], ic1s[ch + 6], ic1s[ch + 7]);
        var ic2 = Vector256.Create(ic2s[ch], ic2s[ch + 1], ic2s[ch + 2], ic2s[ch + 3], ic2s[ch + 4], ic2s[ch + 5], ic2s[ch + 6], ic2s[ch + 7]);
        for (int f = 0; f < frames; f++)
        {
            var s = Vector256.Create(
                Unsafe.Add(ref r0, f), Unsafe.Add(ref r0, stride + f), Unsafe.Add(ref r0, 2 * stride + f), Unsafe.Add(ref r0, 3 * stride + f),
                Unsafe.Add(ref r0, 4 * stride + f), Unsafe.Add(ref r0, 5 * stride + f), Unsafe.Add(ref r0, 6 * stride + f), Unsafe.Add(ref r0, 7 * stride + f));
            var v3 = s - ic2;
            var v1 = a1 * ic1 + a2 * v3;
            var v2 = ic2 + a2 * ic1 + a3 * v3;
            ic1 = two * v1 - ic1;
            ic2 = two * v2 - ic2;
            var o = highPass ? s - k * v1 - v2 : v2;
            for (int i = 0; i < 8; i++) Unsafe.Add(ref r0, i * stride + f) = o.GetElement(i);
        }
        for (int i = 0; i < 8; i++) { ic1s[ch + i] = ic1.GetElement(i); ic2s[ch + i] = ic2.GetElement(i); }
    }

    // ------------------------------------------------------------------ limiter pieces

    /// <summary>Scalar reference of <see cref="PeakAcross"/>.</summary>
    public static void PeakAcrossScalar(ReadOnlySpan<float> data, int stride, int channels, int frames, Span<float> peak)
    {
        CheckLayout(data.Length, channels, stride, 0, frames);
        if (peak.Length < frames) throw new ArgumentException("Output too short.");
        for (int f = 0; f < frames; f++)
        {
            float m = 0f;
            for (int c = 0; c < channels; c++) m = Math.Max(m, Math.Abs(data[c * stride + f]));
            peak[f] = m;
        }
    }

    /// <summary>Writes, for every frame, the largest sample magnitude over all channels.</summary>
    public static void PeakAcross(ReadOnlySpan<float> data, int stride, int channels, int frames, Span<float> peak)
    {
        CheckLayout(data.Length, channels, stride, 0, frames);
        if (peak.Length < frames) throw new ArgumentException("Output too short.");
        ref float d = ref MemoryMarshal.GetReference(data);
        ref float p = ref MemoryMarshal.GetReference(peak);
        int f = 0;
        if (Vector256.IsHardwareAccelerated)
            for (; f <= frames - 8; f += 8)
            {
                var m = Vector256<float>.Zero;
                for (int c = 0; c < channels; c++) m = Vector256.Max(m, Vector256.Abs(Vector256.LoadUnsafe(ref d, (nuint)(c * stride + f))));
                m.StoreUnsafe(ref p, (nuint)f);
            }
        if (Vector128.IsHardwareAccelerated)
            for (; f <= frames - 4; f += 4)
            {
                var m = Vector128<float>.Zero;
                for (int c = 0; c < channels; c++) m = Vector128.Max(m, Vector128.Abs(Vector128.LoadUnsafe(ref d, (nuint)(c * stride + f))));
                m.StoreUnsafe(ref p, (nuint)f);
            }
        for (; f < frames; f++)
        {
            float m = 0f;
            for (int c = 0; c < channels; c++) m = Math.Max(m, Math.Abs(data[c * stride + f]));
            peak[f] = m;
        }
    }

    /// <summary>Scalar reference of <see cref="SwapScaleClamp"/>.</summary>
    public static void SwapScaleClampScalar(Span<float> block, Span<float> ring, ReadOnlySpan<float> gain, float ceiling)
    {
        int n = block.Length;
        if (ring.Length < n || gain.Length < n) throw new ArgumentException("Buffer shorter than the frame count.");
        for (int i = 0; i < n; i++)
        {
            float delayed = ring[i];
            ring[i] = block[i];
            block[i] = Math.Min(Math.Max(delayed * gain[i], -ceiling), ceiling);
        }
    }

    /// <summary>
    /// Delay line step and gain apply of the limiter: for each element the old ring content leaves (scaled by <paramref name="gain"/>
    /// and clamped to ±<paramref name="ceiling"/>) while the incoming block sample takes its place in the ring.
    /// </summary>
    public static void SwapScaleClamp(Span<float> block, Span<float> ring, ReadOnlySpan<float> gain, float ceiling)
    {
        int n = block.Length;
        if (ring.Length < n || gain.Length < n) throw new ArgumentException("Buffer shorter than the frame count.");
        ref float b = ref MemoryMarshal.GetReference(block);
        ref float r = ref MemoryMarshal.GetReference(ring);
        ref float g = ref MemoryMarshal.GetReference(gain);
        int i = 0;
        if (Vector512.IsHardwareAccelerated)
        {
            var hi = Vector512.Create(ceiling); var lo = Vector512.Create(-ceiling);
            for (; i <= n - 16; i += 16)
            {
                var delayed = Vector512.LoadUnsafe(ref r, (nuint)i);
                Vector512.LoadUnsafe(ref b, (nuint)i).StoreUnsafe(ref r, (nuint)i);
                Vector512.Min(Vector512.Max(delayed * Vector512.LoadUnsafe(ref g, (nuint)i), lo), hi).StoreUnsafe(ref b, (nuint)i);
            }
        }
        if (Vector256.IsHardwareAccelerated)
        {
            var hi = Vector256.Create(ceiling); var lo = Vector256.Create(-ceiling);
            for (; i <= n - 8; i += 8)
            {
                var delayed = Vector256.LoadUnsafe(ref r, (nuint)i);
                Vector256.LoadUnsafe(ref b, (nuint)i).StoreUnsafe(ref r, (nuint)i);
                Vector256.Min(Vector256.Max(delayed * Vector256.LoadUnsafe(ref g, (nuint)i), lo), hi).StoreUnsafe(ref b, (nuint)i);
            }
        }
        if (Vector128.IsHardwareAccelerated)
        {
            var hi = Vector128.Create(ceiling); var lo = Vector128.Create(-ceiling);
            for (; i <= n - 4; i += 4)
            {
                var delayed = Vector128.LoadUnsafe(ref r, (nuint)i);
                Vector128.LoadUnsafe(ref b, (nuint)i).StoreUnsafe(ref r, (nuint)i);
                Vector128.Min(Vector128.Max(delayed * Vector128.LoadUnsafe(ref g, (nuint)i), lo), hi).StoreUnsafe(ref b, (nuint)i);
            }
        }
        for (; i < n; i++)
        {
            float delayed = ring[i];
            ring[i] = block[i];
            block[i] = Math.Min(Math.Max(delayed * gain[i], -ceiling), ceiling);
        }
    }
}
