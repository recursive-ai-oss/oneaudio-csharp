using System.Runtime.InteropServices;
using System.Runtime.Intrinsics;

namespace OneAudio.Kernels;

/// <summary>
/// The two inner loops of the Layer III synthesis path: scaled row accumulation (IMDCT and the polyphase matrixing are
/// sums of scaled basis rows) and element-wise product accumulation (the synthesis window). Each has a <c>*Scalar</c>
/// reference. The vector paths do the same multiply-then-add per element in the same order and never fuse them, so
/// results are bit-identical to the reference on every hardware path, not merely close.
/// Callers have sized the buffers; these methods only check that the spans are long enough for the count.
/// </summary>
internal static class Mp3Kernels
{
    /// <summary>Scalar reference of <see cref="MulAdd"/>.</summary>
    public static void MulAddScalar(Span<float> acc, ReadOnlySpan<float> row, float scale)
    {
        if (acc.Length < row.Length) throw new ArgumentException("Accumulator is shorter than the row.", nameof(acc));
        for (int i = 0; i < row.Length; i++) acc[i] += row[i] * scale;
    }

    /// <summary>Adds <c>row[i] * scale</c> to <c>acc[i]</c> for every element of <paramref name="row"/>.</summary>
    public static void MulAdd(Span<float> acc, ReadOnlySpan<float> row, float scale)
    {
        int n = row.Length;
        if (acc.Length < n) throw new ArgumentException("Accumulator is shorter than the row.", nameof(acc));
        ref float d = ref MemoryMarshal.GetReference(acc);
        ref float s = ref MemoryMarshal.GetReference(row);
        int i = 0;
        if (Vector512.IsHardwareAccelerated)
        {
            var v = Vector512.Create(scale);
            for (; i <= n - 16; i += 16)
                (Vector512.LoadUnsafe(ref d, (nuint)i) + Vector512.LoadUnsafe(ref s, (nuint)i) * v).StoreUnsafe(ref d, (nuint)i);
        }
        if (Vector256.IsHardwareAccelerated)
        {
            var v = Vector256.Create(scale);
            for (; i <= n - 8; i += 8)
                (Vector256.LoadUnsafe(ref d, (nuint)i) + Vector256.LoadUnsafe(ref s, (nuint)i) * v).StoreUnsafe(ref d, (nuint)i);
        }
        if (Vector128.IsHardwareAccelerated)
        {
            var v = Vector128.Create(scale);
            for (; i <= n - 4; i += 4)
                (Vector128.LoadUnsafe(ref d, (nuint)i) + Vector128.LoadUnsafe(ref s, (nuint)i) * v).StoreUnsafe(ref d, (nuint)i);
        }
        for (; i < n; i++) acc[i] += row[i] * scale;
    }

    /// <summary>Scalar reference of <see cref="MulAddPairs"/>.</summary>
    public static void MulAddPairsScalar(Span<float> acc, ReadOnlySpan<float> a, ReadOnlySpan<float> b)
    {
        if (a.Length != b.Length || acc.Length < a.Length) throw new ArgumentException("Operand lengths differ.", nameof(a));
        for (int i = 0; i < a.Length; i++) acc[i] += a[i] * b[i];
    }

    /// <summary>Adds <c>a[i] * b[i]</c> to <c>acc[i]</c> for every element of <paramref name="a"/>.</summary>
    public static void MulAddPairs(Span<float> acc, ReadOnlySpan<float> a, ReadOnlySpan<float> b)
    {
        int n = a.Length;
        if (b.Length != n || acc.Length < n) throw new ArgumentException("Operand lengths differ.", nameof(a));
        ref float d = ref MemoryMarshal.GetReference(acc);
        ref float x = ref MemoryMarshal.GetReference(a);
        ref float y = ref MemoryMarshal.GetReference(b);
        int i = 0;
        if (Vector512.IsHardwareAccelerated)
        {
            for (; i <= n - 16; i += 16)
                (Vector512.LoadUnsafe(ref d, (nuint)i) + Vector512.LoadUnsafe(ref x, (nuint)i) * Vector512.LoadUnsafe(ref y, (nuint)i)).StoreUnsafe(ref d, (nuint)i);
        }
        if (Vector256.IsHardwareAccelerated)
        {
            for (; i <= n - 8; i += 8)
                (Vector256.LoadUnsafe(ref d, (nuint)i) + Vector256.LoadUnsafe(ref x, (nuint)i) * Vector256.LoadUnsafe(ref y, (nuint)i)).StoreUnsafe(ref d, (nuint)i);
        }
        if (Vector128.IsHardwareAccelerated)
        {
            for (; i <= n - 4; i += 4)
                (Vector128.LoadUnsafe(ref d, (nuint)i) + Vector128.LoadUnsafe(ref x, (nuint)i) * Vector128.LoadUnsafe(ref y, (nuint)i)).StoreUnsafe(ref d, (nuint)i);
        }
        for (; i < n; i++) acc[i] += a[i] * b[i];
    }
}
