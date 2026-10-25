using System.Buffers.Binary;

namespace OneAudio.Kernels;

/// <summary>
/// Packs samples into little-endian bytes for the WAV encoder: the inverse of <see cref="SampleConvert"/>. The caller
/// sizes the destination (<c>source.Length * bytes per sample</c>) and has already range-checked integer input.
/// 8-bit integers are signed -128..127 on input and stored unsigned (+128), as WAV requires.
/// </summary>
internal static class SamplePack
{
    /// <summary>Packs integers as 8, 16, 24 or 32-bit PCM.</summary>
    public static void Integer(NativeSampleType type, ReadOnlySpan<int> source, Span<byte> destination)
    {
        switch (type)
        {
            case NativeSampleType.PcmUnsigned8:
                for (int i = 0; i < source.Length; i++) destination[i] = (byte)(source[i] + 128);
                break;
            case NativeSampleType.PcmSigned16:
                for (int i = 0; i < source.Length; i++) BinaryPrimitives.WriteInt16LittleEndian(destination[(2 * i)..], (short)source[i]);
                break;
            case NativeSampleType.PcmSigned24:
                for (int i = 0; i < source.Length; i++)
                {
                    int v = source[i];
                    destination[3 * i] = (byte)v;
                    destination[3 * i + 1] = (byte)(v >> 8);
                    destination[3 * i + 2] = (byte)(v >> 16);
                }
                break;
            case NativeSampleType.PcmSigned32:
                for (int i = 0; i < source.Length; i++) BinaryPrimitives.WriteInt32LittleEndian(destination[(4 * i)..], source[i]);
                break;
            default:
                throw new ArgumentException("Not an integer PCM type.", nameof(type));
        }
    }

    /// <summary>Packs floats as IEEE 754 binary32 (<paramref name="doublePrecision"/> false) or binary64.</summary>
    public static void Float(ReadOnlySpan<float> source, Span<byte> destination, bool doublePrecision)
    {
        if (doublePrecision)
            for (int i = 0; i < source.Length; i++) BinaryPrimitives.WriteDoubleLittleEndian(destination[(8 * i)..], source[i]);
        else
            for (int i = 0; i < source.Length; i++) BinaryPrimitives.WriteSingleLittleEndian(destination[(4 * i)..], source[i]);
    }
}
