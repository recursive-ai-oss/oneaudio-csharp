using System.Buffers.Binary;

namespace OneAudio.Kernels;

/// <summary>
/// Conversions between little-endian packed PCM bytes and <see cref="float"/> / <see cref="int"/> samples.
/// These are plain scalar reference loops over buffers the caller has already sized and validated; they never
/// parse headers. Integer-to-float scaling divides by 2^(bits-1), so full-scale negative maps to exactly -1.0 and
/// full-scale positive to just below 1.0. No clipping, dithering or rounding is involved in this direction.
/// </summary>
internal static class SampleConvert
{
    /// <summary>Bytes per sample of a native type.</summary>
    public static int BytesPerSample(NativeSampleType t) => t switch
    {
        NativeSampleType.PcmUnsigned8 => 1,
        NativeSampleType.PcmSigned16 => 2,
        NativeSampleType.PcmSigned24 => 3,
        NativeSampleType.PcmSigned32 or NativeSampleType.IeeeFloat32 => 4,
        NativeSampleType.IeeeFloat64 => 8,
        _ => throw new ArgumentOutOfRangeException(nameof(t)),
    };

    /// <summary>Converts <c>destination.Length</c> packed samples from <paramref name="source"/> to float.</summary>
    public static void ToFloat(NativeSampleType type, ReadOnlySpan<byte> source, Span<float> destination)
    {
        int n = destination.Length;
        if (source.Length < n * BytesPerSample(type)) throw new ArgumentException("Source is too short.", nameof(source));
        switch (type)
        {
            case NativeSampleType.PcmUnsigned8:
                for (int i = 0; i < n; i++) destination[i] = (source[i] - 128) * (1f / 128f);
                break;
            case NativeSampleType.PcmSigned16:
                for (int i = 0; i < n; i++) destination[i] = BinaryPrimitives.ReadInt16LittleEndian(source[(i * 2)..]) * (1f / 32768f);
                break;
            case NativeSampleType.PcmSigned24:
                for (int i = 0; i < n; i++) destination[i] = Int24(source[(i * 3)..]) * (1f / 8388608f);
                break;
            case NativeSampleType.PcmSigned32:
                // Convert in double: a float has only 24 mantissa bits, so scale after widening to keep the rounding to one step.
                for (int i = 0; i < n; i++) destination[i] = (float)(BinaryPrimitives.ReadInt32LittleEndian(source[(i * 4)..]) * (1.0 / 2147483648.0));
                break;
            case NativeSampleType.IeeeFloat32:
                for (int i = 0; i < n; i++) destination[i] = BitConverter.Int32BitsToSingle(BinaryPrimitives.ReadInt32LittleEndian(source[(i * 4)..]));
                break;
            case NativeSampleType.IeeeFloat64:
                for (int i = 0; i < n; i++) destination[i] = (float)BitConverter.Int64BitsToDouble(BinaryPrimitives.ReadInt64LittleEndian(source[(i * 8)..]));
                break;
            default:
                throw new ArgumentOutOfRangeException(nameof(type));
        }
    }

    /// <summary>
    /// Unpacks <c>destination.Length</c> integer samples. 8-bit values are re-centred to -128..127; 16/24/32-bit are
    /// sign-extended. Float types are rejected.
    /// </summary>
    public static void ToInteger(NativeSampleType type, ReadOnlySpan<byte> source, Span<int> destination)
    {
        int n = destination.Length;
        if (source.Length < n * BytesPerSample(type)) throw new ArgumentException("Source is too short.", nameof(source));
        switch (type)
        {
            case NativeSampleType.PcmUnsigned8:
                for (int i = 0; i < n; i++) destination[i] = source[i] - 128;
                break;
            case NativeSampleType.PcmSigned16:
                for (int i = 0; i < n; i++) destination[i] = BinaryPrimitives.ReadInt16LittleEndian(source[(i * 2)..]);
                break;
            case NativeSampleType.PcmSigned24:
                for (int i = 0; i < n; i++) destination[i] = Int24(source[(i * 3)..]);
                break;
            case NativeSampleType.PcmSigned32:
                for (int i = 0; i < n; i++) destination[i] = BinaryPrimitives.ReadInt32LittleEndian(source[(i * 4)..]);
                break;
            default:
                throw new NotSupportedException("Not an integer PCM type.");
        }
    }

    private static int Int24(ReadOnlySpan<byte> b) => ((b[0] << 8) | (b[1] << 16) | (b[2] << 24)) >> 8;
}
