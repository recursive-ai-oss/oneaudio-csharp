using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using System.Runtime.Intrinsics;

namespace OneAudio.Kernels;

/// <summary>
/// Float to integer sample conversion for the encoders. A sample is scaled by 2^(bits-1) (the inverse of the decoders'
/// division), rounded to the nearest integer with ties to even, and clipped to [-2^(bits-1), 2^(bits-1)-1]. NaN becomes 0
/// and infinities clip. The optional TPDF dither adds, before rounding, the sum of two independent uniform values in
/// [-0.5, 0.5) LSB drawn from a xorshift32 generator whose state the caller carries between calls, so a stream
/// dithers identically however its writes are split.
/// </summary>
internal static class SampleQuantize
{
    /// <summary>Converts without dither. Uses vectors for up to 24 bits, where every bound is exactly representable as a float.</summary>
    public static void ToInteger(ReadOnlySpan<float> source, Span<int> destination, int bits)
    {
        if (destination.Length < source.Length) throw new ArgumentException("Destination is too short.", nameof(destination));
        if (bits <= 24 && Vector256.IsHardwareAccelerated) ToInteger256(source, destination, bits);
        else if (bits <= 24 && Vector128.IsHardwareAccelerated) ToInteger128(source, destination, bits);
        else ToIntegerScalar(source, destination, bits);
    }

    /// <summary>The reference loop every vector path must match exactly.</summary>
    public static void ToIntegerScalar(ReadOnlySpan<float> source, Span<int> destination, int bits)
    {
        float scale = (float)(1L << (bits - 1));
        for (int i = 0; i < source.Length; i++) destination[i] = Quantize(source[i] * scale, scale, bits);
    }

    /// <summary>Converts with TPDF dither (scalar). <paramref name="state"/> must be non-zero.</summary>
    public static void ToIntegerDithered(ReadOnlySpan<float> source, Span<int> destination, int bits, ref uint state)
    {
        if (destination.Length < source.Length) throw new ArgumentException("Destination is too short.", nameof(destination));
        float scale = (float)(1L << (bits - 1));
        uint s = state;
        for (int i = 0; i < source.Length; i++)
        {
            s ^= s << 13; s ^= s >> 17; s ^= s << 5;
            float u1 = (s >> 8) * (1f / 16777216f);
            s ^= s << 13; s ^= s >> 17; s ^= s << 5;
            float u2 = (s >> 8) * (1f / 16777216f);
            destination[i] = Quantize(source[i] * scale + (u1 + u2 - 1f), scale, bits);
        }
        state = s;
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static int Quantize(float x, float scale, int bits)
    {
        if (float.IsNaN(x)) return 0;
        float r = MathF.Round(x);
        if (r >= scale) return (int)((1L << (bits - 1)) - 1);
        if (r <= -scale) return (int)(-(1L << (bits - 1)));
        return (int)r;
    }

    private static void ToInteger256(ReadOnlySpan<float> source, Span<int> destination, int bits)
    {
        float scale = (float)(1L << (bits - 1));
        var vScale = Vector256.Create(scale);
        var vMin = Vector256.Create(-scale);
        var vMax = Vector256.Create(scale - 1f);
        ref float s = ref MemoryMarshal.GetReference(source);
        ref int d = ref MemoryMarshal.GetReference(destination);
        int i = 0;
        for (; i + 8 <= source.Length; i += 8)
        {
            var v = Vector256.LoadUnsafe(ref s, (nuint)i) * vScale;
            v = Vector256.ConditionalSelect(Vector256.Equals(v, v), v, Vector256<float>.Zero);
            v = Vector256.Min(Vector256.Max(Vector256.Round(v), vMin), vMax);
            Vector256.ConvertToInt32(v).StoreUnsafe(ref d, (nuint)i);
        }
        ToIntegerScalar(source[i..], destination[i..], bits);
    }

    private static void ToInteger128(ReadOnlySpan<float> source, Span<int> destination, int bits)
    {
        float scale = (float)(1L << (bits - 1));
        var vScale = Vector128.Create(scale);
        var vMin = Vector128.Create(-scale);
        var vMax = Vector128.Create(scale - 1f);
        ref float s = ref MemoryMarshal.GetReference(source);
        ref int d = ref MemoryMarshal.GetReference(destination);
        int i = 0;
        for (; i + 4 <= source.Length; i += 4)
        {
            var v = Vector128.LoadUnsafe(ref s, (nuint)i) * vScale;
            v = Vector128.ConditionalSelect(Vector128.Equals(v, v), v, Vector128<float>.Zero);
            v = Vector128.Min(Vector128.Max(Vector128.Round(v), vMin), vMax);
            Vector128.ConvertToInt32(v).StoreUnsafe(ref d, (nuint)i);
        }
        ToIntegerScalar(source[i..], destination[i..], bits);
    }
}
