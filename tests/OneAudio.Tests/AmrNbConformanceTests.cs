using OneAudio.Amr;
using Xunit;

namespace OneAudio.Tests;

/// <summary>
/// Decodes every decoder test sequence of 3GPP TS 26.074 (AMR) through the narrowband core and compares the output
/// with the reference .OUT file sample for sample. The sequences are fetched by tools/fetch-corpus.sh.
/// </summary>
public class AmrNbConformanceTests
{
    internal static string? Dir => Environment.GetEnvironmentVariable("ONEAUDIO_CORPUS") is { } c
        ? Path.Combine(c, "amr-nb-ts26074") : null;

    public static TheoryData<string> Sequences()
    {
        var d = new TheoryData<string>();
        if (Dir is null || !Directory.Exists(Dir)) { d.Add("(not fetched)"); return d; }
        foreach (var f in Directory.EnumerateFiles(Dir, "*.COD", SearchOption.AllDirectories).Order(StringComparer.Ordinal))
            if (File.Exists(Path.ChangeExtension(f, ".OUT"))) d.Add(Path.GetRelativePath(Dir, f));
        return d;
    }

    // TX frame types of the test format (TS 26.073 frame.h) mapped to what the decoder receives.
    private static int TxToRx(int tx) => tx switch
    {
        0 => AmrNbCore.RxSpeechGood,
        1 => AmrNbCore.RxSidFirst,
        2 => AmrNbCore.RxSidUpdate,
        3 => AmrNbCore.RxNoData,
        4 => AmrNbCore.RxSpeechDegraded,
        5 => AmrNbCore.RxSpeechBad,
        6 => AmrNbCore.RxSidBad,
        7 => AmrNbCore.RxOnset,
        _ => throw new InvalidDataException("bad TX frame type " + tx),
    };

    /// <summary>
    /// The A-law and mu-law sequences (directories ending A or U before "h00") hold the decoder output run through
    /// ITU-T G.711 companding, one code word per 16-bit word.
    /// </summary>
    internal static char Companding(string path)
    {
        foreach (var part in path.Split('/', '\\'))
            if (part.EndsWith("h00", StringComparison.Ordinal) && part.Length > 3 && (part[^4] == 'A' || part[^4] == 'U') &&
                part.StartsWith("DTX", StringComparison.Ordinal) || part is "NODTXAh00" or "NODTXUh00")
                return part[^4];
        return ' ';
    }

    /// <summary>Test-format adapter: 250 little-endian words per frame, TX type, 244 bits, mode.</summary>
    internal static (long frames, long bad, int maxDiff) Compare(string codPath, bool viaStorage = false)
    {
        const int Words = 250;
        byte[] cod = File.ReadAllBytes(codPath);
        byte[] reference = File.ReadAllBytes(Path.ChangeExtension(codPath, ".OUT"));
        Assert.Equal(0, cod.Length % (Words * 2));
        long frames = cod.Length / (Words * 2);
        Assert.Equal(frames * 160 * 2, reference.Length);

        char kind = Companding(codPath);
        var core = new AmrNbCore();
        var api = new AmrNbDecoder();
        var serial = new short[244];
        var pcm = new short[160];
        long bad = 0;
        int maxDiff = 0;
        for (long f = 0; f < frames; f++)
        {
            int o = (int)(f * Words * 2);
            int rx = TxToRx(BitConverter.ToInt16(cod, o));
            for (int i = 0; i < 244; i++) serial[i] = BitConverter.ToInt16(cod, o + 2 + i * 2);
            int mode = BitConverter.ToInt16(cod, o + 2 + 244 * 2);
            if (viaStorage) api.Decode(PackStorage(rx, mode, serial), pcm);
            else core.Decode(rx, mode, serial, pcm);
            for (int i = 0; i < 160; i++)
            {
                short want = BitConverter.ToInt16(reference, (int)((f * 160 + i) * 2));
                int got = kind == 'A' ? G711.ALaw(pcm[i]) : kind == 'U' ? G711.MuLaw(pcm[i]) : pcm[i];
                int diff = Math.Abs(want - got);
                if (diff != 0) { bad++; maxDiff = Math.Max(maxDiff, diff); }
            }
        }
        return (frames, bad, maxDiff);
    }

    private static readonly short[][] SortTables =
    [
        AmrNbTables.Sort475, AmrNbTables.Sort515, AmrNbTables.Sort59, AmrNbTables.Sort67, AmrNbTables.Sort74,
        AmrNbTables.Sort795, AmrNbTables.Sort102, AmrNbTables.Sort122, AmrNbTables.SortSid,
    ];
    private static readonly int[] Bits = [95, 103, 118, 134, 148, 159, 204, 244, 35];

    /// <summary>Packs test-format frame data into the RFC 4867 storage layout (TOC byte, sorted bits, SID type and mode).</summary>
    internal static byte[] PackStorage(int rx, int mode, short[] serial)
    {
        int ft = rx switch
        {
            AmrNbCore.RxSpeechGood => mode,
            AmrNbCore.RxSidFirst or AmrNbCore.RxSidUpdate => 8,
            AmrNbCore.RxNoData => 15,
            _ => throw new InvalidDataException(),
        };
        if (ft == 15) return [(15 << 3) | 4];
        var bytes = new byte[1 + (Bits[ft] + 7 + (ft == 8 ? 4 : 0)) / 8];
        bytes[0] = (byte)((ft << 3) | 4);
        for (int i = 0; i < Bits[ft]; i++)
            if (serial[SortTables[ft][i]] != 0) bytes[1 + (i >> 3)] |= (byte)(0x80 >> (i & 7));
        if (ft == 8)
        {
            if (rx == AmrNbCore.RxSidUpdate) bytes[5] |= 0x10;
            bytes[5] |= (byte)(((mode & 1) << 3) | (((mode >> 1) & 1) << 2) | (((mode >> 2) & 1) << 1));
        }
        return bytes;
    }

    [Theory]
    [MemberData(nameof(Sequences))]
    public void SequenceIsBitExact(string relative)
    {
        Assert.SkipUnless(Dir is not null && File.Exists(Path.Combine(Dir, relative)),
            "TS 26.074 test sequences not fetched (tools/fetch-corpus.sh amr-nb-ts26074, set ONEAUDIO_CORPUS).");
        var (frames, bad, maxDiff) = Compare(Path.Combine(Dir!, relative));
        Assert.True(bad == 0, $"{relative}: {bad} of {frames * 160} samples differ (max {maxDiff}) over {frames} frames");
    }

    [Theory]
    [MemberData(nameof(Sequences))]
    public void StorageFramesAreBitExact(string relative)
    {
        Assert.SkipUnless(Dir is not null && File.Exists(Path.Combine(Dir, relative)),
            "TS 26.074 test sequences not fetched (tools/fetch-corpus.sh amr-nb-ts26074, set ONEAUDIO_CORPUS).");
        var (frames, bad, maxDiff) = Compare(Path.Combine(Dir!, relative), viaStorage: true);
        Assert.True(bad == 0, $"{relative}: {bad} of {frames * 160} samples differ (max {maxDiff}) over {frames} frames");
    }
}
