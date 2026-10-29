namespace OneAudio.Tests;

/// <summary>ITU-T G.711 A-law and mu-law compression, used only to compare against test sequences stored companded.</summary>
internal static class G711
{
    private static readonly int[] SegA = [0x1F, 0x3F, 0x7F, 0xFF, 0x1FF, 0x3FF, 0x7FF, 0xFFF];
    private static readonly int[] SegU = [0x3F, 0x7F, 0xFF, 0x1FF, 0x3FF, 0x7FF, 0xFFF, 0x1FFF];

    private static int Search(int v, int[] table)
    {
        for (int i = 0; i < table.Length; i++)
            if (v <= table[i]) return i;
        return table.Length;
    }

    public static int ALaw(int pcm)
    {
        pcm >>= 3;
        int mask;
        if (pcm >= 0) mask = 0xD5;
        else { mask = 0x55; pcm = -pcm - 1; }
        int seg = Search(pcm, SegA);
        if (seg >= 8) return 0x7F ^ mask;
        int v = seg << 4;
        v |= seg < 2 ? (pcm >> 1) & 0xF : (pcm >> seg) & 0xF;
        return v ^ mask;
    }

    public static int MuLaw(int pcm)
    {
        pcm >>= 2;
        int mask;
        if (pcm < 0) { pcm = -pcm; mask = 0x7F; }
        else mask = 0xFF;
        if (pcm > 8159) pcm = 8159;
        pcm += 33;
        int seg = Search(pcm, SegU);
        if (seg >= 8) return 0x7F ^ mask;
        return ((seg << 4) | ((pcm >> (seg + 1)) & 0xF)) ^ mask;
    }
}
