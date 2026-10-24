using System.Buffers.Binary;
using System.Text;

namespace OneAudio.Tests;

/// <summary>Builds small WAV files in memory.</summary>
internal static class WavBuilder
{
    public static byte[] Build(int tag, int channels, int rate, int bits, byte[] data, bool extensible = false, int declaredDataSize = -1, bool rf64 = false, int validBits = 0, uint mask = 0)
    {
        using var ms = new MemoryStream();
        using var w = new BinaryWriter(ms, Encoding.ASCII, leaveOpen: true);
        int fmtSize = extensible ? 40 : 16;
        int blockAlign = channels * bits / 8;
        w.Write(rf64 ? "RF64"u8 : "RIFF"u8);
        w.Write(rf64 ? -1 : 4 + 8 + fmtSize + 8 + data.Length + (data.Length & 1));
        w.Write("WAVE"u8);
        if (rf64)
        {
            w.Write("ds64"u8); w.Write(28);
            w.Write((long)0); w.Write((long)data.Length); w.Write((long)0); w.Write(0);
        }
        w.Write("fmt "u8); w.Write(fmtSize);
        w.Write((short)(extensible ? 0xFFFE : tag)); w.Write((short)channels); w.Write(rate); w.Write(rate * blockAlign);
        w.Write((short)blockAlign); w.Write((short)bits);
        if (extensible)
        {
            w.Write((short)22); w.Write((short)(validBits == 0 ? bits : validBits)); w.Write(mask);
            w.Write((short)tag);
            w.Write(new byte[] { 0x00, 0x00, 0x00, 0x00, 0x10, 0x00, 0x80, 0x00, 0x00, 0xAA, 0x00, 0x38, 0x9B, 0x71 });
        }
        w.Write("LIST"u8); w.Write(5); w.Write("abcde"u8); w.Write((byte)0); // odd-sized chunk with pad byte
        w.Write("data"u8);
        w.Write(rf64 ? -1 : declaredDataSize >= 0 ? declaredDataSize : data.Length);
        w.Write(data);
        if ((data.Length & 1) != 0) w.Write((byte)0);
        return ms.ToArray();
    }

    public static byte[] Pcm16(short[] samples)
    {
        var b = new byte[samples.Length * 2];
        for (int i = 0; i < samples.Length; i++) BinaryPrimitives.WriteInt16LittleEndian(b.AsSpan(i * 2), samples[i]);
        return b;
    }

    public static byte[] Pcm24(int[] samples)
    {
        var b = new byte[samples.Length * 3];
        for (int i = 0; i < samples.Length; i++)
        {
            b[3 * i] = (byte)samples[i]; b[3 * i + 1] = (byte)(samples[i] >> 8); b[3 * i + 2] = (byte)(samples[i] >> 16);
        }
        return b;
    }

    public static byte[] Pcm32(int[] samples)
    {
        var b = new byte[samples.Length * 4];
        for (int i = 0; i < samples.Length; i++) BinaryPrimitives.WriteInt32LittleEndian(b.AsSpan(i * 4), samples[i]);
        return b;
    }

    public static byte[] Float32(float[] samples)
    {
        var b = new byte[samples.Length * 4];
        for (int i = 0; i < samples.Length; i++) BinaryPrimitives.WriteSingleLittleEndian(b.AsSpan(i * 4), samples[i]);
        return b;
    }

    public static byte[] Float64(double[] samples)
    {
        var b = new byte[samples.Length * 8];
        for (int i = 0; i < samples.Length; i++) BinaryPrimitives.WriteDoubleLittleEndian(b.AsSpan(i * 8), samples[i]);
        return b;
    }
}

/// <summary>A stream that hides seeking, like a socket or pipe.</summary>
internal sealed class NonSeekableStream(byte[] data) : Stream
{
    private readonly MemoryStream _inner = new(data);
    public override bool CanRead => true;
    public override bool CanSeek => false;
    public override bool CanWrite => false;
    public override long Length => throw new NotSupportedException();
    public override long Position { get => throw new NotSupportedException(); set => throw new NotSupportedException(); }
    public override void Flush() { }
    public override int Read(byte[] buffer, int offset, int count) => _inner.Read(buffer, offset, Math.Min(count, 7)); // short reads on purpose
    public override int Read(Span<byte> buffer) => _inner.Read(buffer[..Math.Min(buffer.Length, 7)]);
    public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
    public override void SetLength(long value) => throw new NotSupportedException();
    public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();
}
