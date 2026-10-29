using OneAudio.Amr;
using Xunit;

namespace OneAudio.Tests;

/// <summary>
/// Decodes every decoder test sequence of 3GPP TS 26.174 (AMR-WB) through the wideband core and compares the output
/// with the reference .out file sample for sample. The sequences are fetched by tools/fetch-corpus.sh.
/// </summary>
public class AmrWbConformanceTests
{
    internal static string? Dir => Environment.GetEnvironmentVariable("ONEAUDIO_CORPUS") is { } c
        ? Path.Combine(c, "amr-wb-ts26174", "T", "T") : null;

    public static TheoryData<string> Sequences()
    {
        var d = new TheoryData<string>();
        if (Dir is null || !Directory.Exists(Dir)) { d.Add("(not fetched)"); return d; }
        foreach (var f in Directory.EnumerateFiles(Dir, "*.cod", SearchOption.AllDirectories).Order(StringComparer.Ordinal))
            if (File.Exists(Path.ChangeExtension(f, ".out"))) d.Add(Path.GetRelativePath(Dir, f));
        return d;
    }

    /// <summary>Counts of received frame types seen while decoding, indexed by the Rx constants of the core.</summary>
    internal static readonly long[] FrameTypeCounts = new long[8];

    /// <summary>
    /// Test-format adapter (TS 26.173 bits.c, default format): per frame the words [0x6b21 or 0x6b20][frame type][mode]
    /// followed by 35 bits for SID and no-data frames or the bit count of the mode, one word per bit (+127 is a set bit).
    /// </summary>
    internal static (long frames, long bad, int maxDiff) Compare(string codPath, long[]? typeCounts = null, bool viaStorage = false)
    {
        byte[] cod = File.ReadAllBytes(codPath);
        byte[] reference = File.ReadAllBytes(Path.ChangeExtension(codPath, ".out"));
        var core = new AmrWbCore();
        var api = new AmrWbDecoder();
        var bits = new short[480];
        var pcm = new short[320];
        long frames = 0, bad = 0;
        int maxDiff = 0, pos = 0;
        while (pos + 6 <= cod.Length)
        {
            int kind = BitConverter.ToInt16(cod, pos);
            int ft = BitConverter.ToInt16(cod, pos + 2);
            int mode = BitConverter.ToInt16(cod, pos + 4);
            pos += 6;
            int rx = ft;
            if (kind == 0x6b21)
            {
                rx = ft switch { 0 => AmrWbCore.RxSpeechGood, 1 => AmrWbCore.RxSidFirst, 2 => AmrWbCore.RxSidUpdate, 3 => AmrWbCore.RxNoData, _ => throw new InvalidDataException() };
            }
            int codingMode = rx is AmrWbCore.RxSidFirst or AmrWbCore.RxSidUpdate or AmrWbCore.RxNoData or AmrWbCore.RxSidBad ? AmrWbCore.MrDtx : mode;
            int n = AmrWbCore.NbOfBits[codingMode];
            if (pos + n * 2 > cod.Length) break;
            for (int i = 0; i < n; i++) bits[i] = (short)(BitConverter.ToInt16(cod, pos + i * 2) > -127 ? 1 : 0);
            pos += n * 2;
            if (typeCounts is not null) typeCounts[rx]++;
            if (viaStorage) api.Decode(PackStorage(rx, mode, bits), pcm);
            else core.Decode(rx, mode, bits, n, pcm);
            for (int i = 0; i < 320; i++)
            {
                int ro = (int)((frames * 320 + i) * 2);
                short want = ro + 2 <= reference.Length ? BitConverter.ToInt16(reference, ro) : (short)0x7777;
                int diff = Math.Abs(want - pcm[i]);
                if (diff != 0) { bad++; maxDiff = Math.Max(maxDiff, diff); }
            }
            frames++;
        }
        Assert.Equal(frames * 640, reference.Length);
        return (frames, bad, maxDiff);
    }

    private static readonly short[][] SortTables =
    [
        AmrWbTables.Sort660, AmrWbTables.Sort885, AmrWbTables.Sort1265, AmrWbTables.Sort1425, AmrWbTables.Sort1585,
        AmrWbTables.Sort1825, AmrWbTables.Sort1985, AmrWbTables.Sort2305, AmrWbTables.Sort2385, AmrWbTables.SortSid,
    ];

    /// <summary>Packs test-format frame data into the RFC 4867 storage layout.</summary>
    internal static byte[] PackStorage(int rx, int mode, short[] bits)
    {
        int ft = rx switch
        {
            AmrWbCore.RxSpeechGood => mode,
            AmrWbCore.RxSidFirst or AmrWbCore.RxSidUpdate => 9,
            AmrWbCore.RxNoData => 15,
            AmrWbCore.RxSpeechLost => 14,
            _ => throw new InvalidDataException(),
        };
        if (ft >= 14) return [(byte)((ft << 3) | 4)];
        int n = AmrWbCore.NbOfBits[ft];
        var bytes = new byte[1 + (n + 7 + (ft == 9 ? 4 : 0)) / 8];
        bytes[0] = (byte)((ft << 3) | 4);
        for (int i = 0; i < n; i++)
            if (bits[SortTables[ft][i]] != 0) bytes[1 + (i >> 3)] |= (byte)(0x80 >> (i & 7));
        if (ft == 9 && rx == AmrWbCore.RxSidUpdate) bytes[5] |= 0x10;
        return bytes;
    }

    [Theory]
    [MemberData(nameof(Sequences))]
    public void SequenceIsBitExact(string relative)
    {
        Assert.SkipUnless(Dir is not null && File.Exists(Path.Combine(Dir, relative)),
            "TS 26.174 test sequences not fetched (tools/fetch-corpus.sh amr-wb-ts26174, set ONEAUDIO_CORPUS).");
        var counts = new long[8];
        var (frames, bad, maxDiff) = Compare(Path.Combine(Dir!, relative), counts);
        lock (FrameTypeCounts) for (int i = 0; i < 8; i++) FrameTypeCounts[i] += counts[i];
        Assert.True(bad == 0, $"{relative}: {bad} of {frames * 320} samples differ (max {maxDiff}) over {frames} frames");
    }

    [Theory]
    [MemberData(nameof(Sequences))]
    public void StorageFramesAreBitExact(string relative)
    {
        Assert.SkipUnless(Dir is not null && File.Exists(Path.Combine(Dir, relative)),
            "TS 26.174 test sequences not fetched (tools/fetch-corpus.sh amr-wb-ts26174, set ONEAUDIO_CORPUS).");
        var (frames, bad, maxDiff) = Compare(Path.Combine(Dir!, relative), viaStorage: true);
        Assert.True(bad == 0, $"{relative}: {bad} of {frames * 320} samples differ (max {maxDiff}) over {frames} frames");
    }
}
