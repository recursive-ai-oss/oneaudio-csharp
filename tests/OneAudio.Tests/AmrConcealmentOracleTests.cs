using System.Diagnostics;
using OneAudio.Amr;
using Xunit;

namespace OneAudio.Tests;

/// <summary>
/// The 3GPP conformance sequences contain only good, SID and no-data frames. This test closes the gap for
/// concealment: it rewrites real sequences with deterministic damage (bad, degraded, onset, lost and no-data frames
/// with corrupted bits, bad SID frames), decodes the result with the reference decoder built from TS 26.073 / 26.173
/// (run as a black box; paths in ONEAUDIO_AMR_NB_REF and ONEAUDIO_AMR_WB_REF) and requires identical output.
/// </summary>
public class AmrConcealmentOracleTests
{
    private static string? NbRef => Environment.GetEnvironmentVariable("ONEAUDIO_AMR_NB_REF");
    private static string? WbRef => Environment.GetEnvironmentVariable("ONEAUDIO_AMR_WB_REF");

    /// <summary>Frame-type tallies of the damaged streams that were checked, indexed by Rx type (shared by both codecs' tests).</summary>
    internal static readonly long[] NbTypes = new long[8], WbTypes = new long[8];

    private static uint Next(ref uint s)
    {
        s = s * 1664525u + 1013904223u;
        return s >> 8;
    }

    public static TheoryData<string> NbFiles()
    {
        var d = new TheoryData<string>();
        string? dir = AmrNbConformanceTests.Dir;
        if (dir is null || !Directory.Exists(dir)) { d.Add("(not fetched)"); return d; }
        foreach (string sub in new[] { "TS_AMR_h01_NODTX/NODTX_h00", "TS_AMR_h01_DTX/DTX_h00", "TS_AMR_h01_DTX2/DTX2_h00" })
        {
            string root = Path.Combine(dir, sub);
            if (!Directory.Exists(root)) continue;
            foreach (var g in Directory.EnumerateFiles(root, "*.COD", SearchOption.AllDirectories)
                         .GroupBy(Path.GetDirectoryName))
                foreach (var f in g.Order(StringComparer.Ordinal).Take(2)) d.Add(Path.GetRelativePath(dir, f));
        }
        return d;
    }

    public static TheoryData<string> WbFiles()
    {
        var d = new TheoryData<string>();
        string? dir = AmrWbConformanceTests.Dir;
        if (dir is null || !Directory.Exists(dir)) { d.Add("(not fetched)"); return d; }
        foreach (var g in Directory.EnumerateFiles(dir, "*.cod", SearchOption.AllDirectories).GroupBy(Path.GetDirectoryName))
            foreach (var f in g.Order(StringComparer.Ordinal).Take(2)) d.Add(Path.GetRelativePath(dir, f));
        return d;
    }

    private static byte[] RunOracle(string exe, string args, string outPath)
    {
        var psi = new ProcessStartInfo(exe, args) { RedirectStandardError = true, RedirectStandardOutput = true };
        using var p = Process.Start(psi)!;
        p.StandardOutput.ReadToEnd();
        p.StandardError.ReadToEnd();
        p.WaitForExit();
        Assert.Equal(0, p.ExitCode);
        return File.ReadAllBytes(outPath);
    }

    [Theory]
    [MemberData(nameof(NbFiles))]
    public void NarrowbandConcealmentMatchesReference(string relative)
    {
        Assert.SkipUnless(NbRef is not null && File.Exists(NbRef) && AmrNbConformanceTests.Dir is not null
            && File.Exists(Path.Combine(AmrNbConformanceTests.Dir, relative)), "NB reference decoder or sequences not available.");
        byte[] cod = File.ReadAllBytes(Path.Combine(AmrNbConformanceTests.Dir!, relative));
        const int Words = 250;
        int frames = cod.Length / (Words * 2);
        // TX type of the test format -> RX type of the decoder (TS 26.073 frame.h), then damage.
        int[] txToRx = [0, 4, 5, 7, 1, 3, 6, 2];
        uint seed = (uint)relative.Length * 2654435761u;
        var rxTypes = new int[frames];
        var damaged = new byte[cod.Length];
        cod.CopyTo(damaged, 0);
        for (int f = 0; f < frames; f++)
        {
            int o = f * Words * 2;
            int rx = txToRx[BitConverter.ToInt16(cod, o)];
            uint r = Next(ref seed) % 10;
            if (rx == AmrNbCore.RxSpeechGood && r < 4)
                rx = r switch { 0 => AmrNbCore.RxSpeechBad, 1 => AmrNbCore.RxSpeechDegraded, 2 => AmrNbCore.RxOnset, _ => AmrNbCore.RxNoData };
            else if (rx == AmrNbCore.RxSidUpdate && r < 3) rx = AmrNbCore.RxSidBad;
            if (rx is AmrNbCore.RxSpeechBad or AmrNbCore.RxSpeechDegraded or AmrNbCore.RxSidBad)
            {
                for (int k = 0; k < 20; k++)
                {
                    int bit = (int)(Next(ref seed) % 244);
                    short v = BitConverter.ToInt16(damaged, o + 2 + bit * 2);
                    BitConverter.TryWriteBytes(damaged.AsSpan(o + 2 + bit * 2), (short)(v ^ 1));
                }
            }
            rxTypes[f] = rx;
            BitConverter.TryWriteBytes(damaged.AsSpan(o), (short)rx);
            lock (NbTypes) NbTypes[rx]++;
        }
        string tmp = Path.Combine(Path.GetTempPath(), "amrnb-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(tmp);
        try
        {
            string inp = Path.Combine(tmp, "in.cod"), outp = Path.Combine(tmp, "out.raw");
            File.WriteAllBytes(inp, damaged);
            byte[] reference = RunOracle(NbRef!, $"-rxframetype \"{inp}\" \"{outp}\"", outp);
            Assert.Equal(frames * 320, reference.Length);

            var core = new AmrNbCore();
            var serial = new short[244];
            var pcm = new short[160];
            long bad = 0;
            for (int f = 0; f < frames; f++)
            {
                int o = f * Words * 2;
                for (int i = 0; i < 244; i++) serial[i] = BitConverter.ToInt16(damaged, o + 2 + i * 2);
                core.Decode(rxTypes[f], BitConverter.ToInt16(damaged, o + 2 + 244 * 2), serial, pcm);
                for (int i = 0; i < 160; i++)
                    if (BitConverter.ToInt16(reference, (f * 160 + i) * 2) != pcm[i]) bad++;
            }
            Assert.True(bad == 0, $"{relative}: {bad} samples differ from the reference over {frames} damaged frames");
        }
        finally
        {
            Directory.Delete(tmp, true);
        }
    }

    [Theory]
    [MemberData(nameof(WbFiles))]
    public void WidebandConcealmentMatchesReference(string relative)
    {
        Assert.SkipUnless(WbRef is not null && File.Exists(WbRef) && AmrWbConformanceTests.Dir is not null
            && File.Exists(Path.Combine(AmrWbConformanceTests.Dir, relative)), "WB reference decoder or sequences not available.");
        byte[] cod = File.ReadAllBytes(Path.Combine(AmrWbConformanceTests.Dir!, relative));
        int[] nbBits = [132, 177, 253, 285, 317, 365, 397, 461, 477, 35];
        uint seed = (uint)relative.Length * 2654435761u + 7;
        var damaged = new List<byte>(cod.Length);
        var frameList = new List<(int rx, int mode, short[] bits)>();
        int pos = 0;
        while (pos + 6 <= cod.Length)
        {
            int kind = BitConverter.ToInt16(cod, pos), ft = BitConverter.ToInt16(cod, pos + 2), mode = BitConverter.ToInt16(cod, pos + 4);
            pos += 6;
            int rx = kind == 0x6b21 ? new[] { 0, 4, 5, 7 }[ft] : ft;
            int cm = rx >= 4 ? 9 : mode;
            int n = nbBits[cm];
            var words = new short[n];
            for (int i = 0; i < n; i++) words[i] = BitConverter.ToInt16(cod, pos + i * 2);
            pos += n * 2;
            uint r = Next(ref seed) % 10;
            if (rx == AmrWbCore.RxSpeechGood && r < 4)
                rx = r switch { 0 => AmrWbCore.RxSpeechBad, 1 => AmrWbCore.RxSpeechProbablyDegraded, 2 => AmrWbCore.RxSpeechLost, _ => AmrWbCore.RxNoData };
            else if (rx == AmrWbCore.RxSidUpdate && r < 3) rx = AmrWbCore.RxSidBad;
            int cm2 = rx >= 4 ? 9 : mode;
            if (cm2 != cm) Array.Resize(ref words, nbBits[cm2]);
            if (rx is AmrWbCore.RxSpeechBad or AmrWbCore.RxSpeechProbablyDegraded or AmrWbCore.RxSidBad or AmrWbCore.RxSpeechLost)
                for (int k = 0; k < 20; k++)
                {
                    int bit = (int)(Next(ref seed) % (uint)words.Length);
                    words[bit] = words[bit] > -127 ? (short)-127 : (short)127;
                }
            damaged.AddRange(BitConverter.GetBytes((short)0x6b20));
            damaged.AddRange(BitConverter.GetBytes((short)rx));
            damaged.AddRange(BitConverter.GetBytes((short)mode));
            foreach (short w in words) damaged.AddRange(BitConverter.GetBytes(w));
            frameList.Add((rx, mode, words));
            lock (WbTypes) WbTypes[rx]++;
        }
        string tmp = Path.Combine(Path.GetTempPath(), "amrwb-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(tmp);
        try
        {
            string inp = Path.Combine(tmp, "in.cod"), outp = Path.Combine(tmp, "out.raw");
            File.WriteAllBytes(inp, damaged.ToArray());
            byte[] reference = RunOracle(WbRef!, $"\"{inp}\" \"{outp}\"", outp);
            Assert.Equal(frameList.Count * 640, reference.Length);

            var core = new AmrWbCore();
            var bits = new short[480];
            var pcm = new short[320];
            long bad = 0;
            for (int f = 0; f < frameList.Count; f++)
            {
                var (rx, mode, w) = frameList[f];
                for (int i = 0; i < w.Length; i++) bits[i] = (short)(w[i] > -127 ? 1 : 0);
                core.Decode(rx, mode, bits, w.Length, pcm);
                for (int i = 0; i < 320; i++)
                    if (BitConverter.ToInt16(reference, (f * 320 + i) * 2) != pcm[i]) bad++;
            }
            Assert.True(bad == 0, $"{relative}: {bad} samples differ from the reference over {frameList.Count} damaged frames");
        }
        finally
        {
            Directory.Delete(tmp, true);
        }
    }
}
