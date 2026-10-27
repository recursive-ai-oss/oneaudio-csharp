/// <summary>A one-second 16-bit mono sine as WAV bytes, built without the library so every variant starts from the same input.</summary>
internal static class Wav
{
    public static MemoryStream Make()
    {
        const int rate = 48_000;
        var ms = new MemoryStream();
        using (var w = new BinaryWriter(ms, System.Text.Encoding.ASCII, leaveOpen: true))
        {
            w.Write("RIFF"u8); w.Write(36 + rate * 2); w.Write("WAVEfmt "u8);
            w.Write(16); w.Write((short)1); w.Write((short)1); w.Write(rate); w.Write(rate * 2); w.Write((short)2); w.Write((short)16);
            w.Write("data"u8); w.Write(rate * 2);
            for (int i = 0; i < rate; i++) w.Write((short)(Math.Sin(2 * Math.PI * 440 * i / rate) * 12000));
        }
        ms.Position = 0;
        return ms;
    }
}
