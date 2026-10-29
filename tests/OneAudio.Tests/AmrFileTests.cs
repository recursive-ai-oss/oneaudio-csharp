using System.Diagnostics;
using OneAudio.Amr;
using Xunit;

namespace OneAudio.Tests;

/// <summary>
/// End to end: real encoded speech (the TS 26.074 / 26.174 sequences) wrapped in .amr / .awb files and re-muxed into 3GP
/// by ffmpeg (a black box, tests only) decodes through <c>Audio.Open</c> to exactly the reference output. Files with
/// random payloads (testdata/containers) only prove framing and are not used here.
/// </summary>
public class AmrFileTests
{
    private static readonly string[] NbSequences =
    [
        "TS_AMR_h01_NODTX/NODTX_h00/T_122/T00_122.COD",
        "TS_AMR_h01_NODTX/NODTX_h00/T_475/T01_475.COD",
        "TS_AMR_h01_NODTX/NODTX_h00/T_795/T00_795.COD",
        "TS_AMR_h01_NODTX/NODTX_h00/T_MODE/T21.COD",
        "TS_AMR_h01_DTX/DTX_h00/DTX/DTX1_122.COD",
    ];

    private static readonly string[] WbSequences =
    [
        "T_660/T00_660.cod", "T_885/T01_885.cod", "T_2385/T00_2385.cod", "T_mode/T22.cod", "DTX_test_vectors/T_2385/Dtx1_2385.cod",
    ];

    public static TheoryData<string> Nb() { var d = new TheoryData<string>(); foreach (var s in NbSequences) d.Add(s); return d; }
    public static TheoryData<string> Wb() { var d = new TheoryData<string>(); foreach (var s in WbSequences) d.Add(s); return d; }

    private static string Resolve(bool wide, string relative)
    {
        string? dir = wide ? AmrWbConformanceTests.Dir : AmrNbConformanceTests.Dir;
        string? path = dir is null ? null : Path.Combine(dir, relative);
        if (path is not null && !File.Exists(path) && !wide)
            path = Directory.EnumerateFiles(dir!, Path.GetFileName(relative), SearchOption.AllDirectories).FirstOrDefault();
        Assert.SkipUnless(path is not null && File.Exists(path), "3GPP test sequences not fetched (tools/fetch-corpus.sh, set ONEAUDIO_CORPUS).");
        return path!;
    }

    /// <summary>The storage-format frames (TOC + bits) of a test sequence.</summary>
    private static List<byte[]> Frames(bool wide, string cod)
    {
        byte[] d = File.ReadAllBytes(cod);
        var list = new List<byte[]>();
        if (!wide)
        {
            int[] txToRx = [0, 4, 5, 7];
            var serial = new short[244];
            for (int o = 0; o + 500 <= d.Length; o += 500)
            {
                int rx = txToRx[BitConverter.ToInt16(d, o)];
                for (int i = 0; i < 244; i++) serial[i] = BitConverter.ToInt16(d, o + 2 + i * 2);
                list.Add(AmrNbConformanceTests.PackStorage(rx, BitConverter.ToInt16(d, o + 2 + 488), serial));
            }
            return list;
        }
        int[] nbBits = [132, 177, 253, 285, 317, 365, 397, 461, 477, 35];
        var bits = new short[480];
        int pos = 0;
        while (pos + 6 <= d.Length)
        {
            int ft = BitConverter.ToInt16(d, pos + 2), mode = BitConverter.ToInt16(d, pos + 4);
            int rx = new[] { 0, 4, 5, 7 }[ft];
            int n = nbBits[rx == 0 ? mode : 9];
            pos += 6;
            for (int i = 0; i < n; i++) bits[i] = (short)(BitConverter.ToInt16(d, pos + i * 2) > -127 ? 1 : 0);
            pos += n * 2;
            list.Add(AmrWbConformanceTests.PackStorage(rx, mode, bits));
        }
        return list;
    }

    private static byte[] FileBytes(bool wide, List<byte[]> frames)
    {
        using var ms = new MemoryStream();
        ms.Write(wide ? "#!AMR-WB\n"u8 : "#!AMR\n"u8);
        foreach (var f in frames) ms.Write(f);
        return ms.ToArray();
    }

    private static float[] ReadAll(IAudioDecoder dec)
    {
        var list = new List<float>();
        var buf = new float[1000];
        int n;
        while ((n = dec.Read(buf)) > 0) list.AddRange(buf.AsSpan(0, n).ToArray());
        return list.ToArray();
    }

    private static void CheckAgainstOut(bool wide, string relative, string filePath)
    {
        string cod = Resolve(wide, relative);
        byte[] reference = File.ReadAllBytes(Path.ChangeExtension(cod, wide ? ".out" : ".OUT"));
        using var dec = Audio.Open(filePath);
        Assert.Equal(new AudioFormat(wide ? 16000 : 8000, 1, ChannelMask.FrontCenter), dec.Format);
        float[] got = ReadAll(dec);
        Assert.Equal(reference.Length / 2, got.Length);
        int bad = 0;
        for (int i = 0; i < got.Length; i++)
            if (got[i] != BitConverter.ToInt16(reference, i * 2) / 32768f) bad++;
        Assert.True(bad == 0, $"{relative}: {bad} of {got.Length} samples differ from the reference output");
    }

    private static string TempDir()
    {
        string d = Path.Combine(Path.GetTempPath(), "amrfile-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(d);
        return d;
    }

    [Theory]
    [MemberData(nameof(Nb))]
    public void NarrowbandFileDecodesBitExact(string relative) => FileCase(false, relative);

    [Theory]
    [MemberData(nameof(Wb))]
    public void WidebandFileDecodesBitExact(string relative) => FileCase(true, relative);

    private static void FileCase(bool wide, string relative)
    {
        string cod = Resolve(wide, relative);
        var frames = Frames(wide, cod);
        string dir = TempDir();
        try
        {
            string path = Path.Combine(dir, wide ? "t.awb" : "t.amr");
            File.WriteAllBytes(path, FileBytes(wide, frames));
            CheckAgainstOut(wide, relative, path);

            // Seeking inside the 10-frame preroll restarts at frame 0, so it is bit-exact; far seeks only converge.
            int n = wide ? 320 : 160;
            using var dec = Audio.Open(path);
            Assert.Equal((long)frames.Count * n, dec.LengthFrames);
            var all = new float[frames.Count * n];
            using (var full = Audio.Open(path)) Assert.Equal(all.Length, full.Read(all));
            dec.Seek(3 * n + 5);
            var buf = new float[2 * n];
            Assert.Equal(buf.Length, dec.Read(buf));
            for (int i = 0; i < buf.Length; i++) Assert.Equal(all[3 * n + 5 + i], buf[i]);
            long far = (frames.Count - 20) * n + 7;
            dec.Seek(far);
            Assert.Equal(far, dec.PositionFrames);
            Assert.Equal(buf.Length, dec.Read(buf));
        }
        finally
        {
            Directory.Delete(dir, true);
        }
    }

    private static string? Run(string exe, string args, string? stdoutFile = null)
    {
        try
        {
            var psi = new ProcessStartInfo(exe, args) { RedirectStandardError = true, RedirectStandardOutput = true };
            using var p = Process.Start(psi)!;
            var err = p.StandardError.ReadToEndAsync();
            p.StandardOutput.ReadToEnd();
            p.WaitForExit();
            _ = err.Result;
            return p.ExitCode == 0 ? "" : null;
        }
        catch (System.ComponentModel.Win32Exception)
        {
            return null;
        }
    }

    /// <summary>
    /// ffmpeg re-muxes the file (stream copy) into .amr/.awb and 3GP; our decoder must give the same samples for all
    /// three, equal to the reference decoder's output on the ffmpeg-written file (MMS storage mode).
    /// </summary>
    [Theory]
    [MemberData(nameof(Nb))]
    public void FfmpegMuxedNarrowbandMatchesReference(string relative) => FfmpegCase(false, relative);

    [Theory]
    [MemberData(nameof(Wb))]
    public void FfmpegMuxedWidebandMatchesReference(string relative) => FfmpegCase(true, relative);

    private static void FfmpegCase(bool wide, string relative)
    {
        string? refDec = Environment.GetEnvironmentVariable(wide ? "ONEAUDIO_AMR_WB_REF" : "ONEAUDIO_AMR_NB_MMS_REF");
        string cod = Resolve(wide, relative);
        Assert.SkipUnless(refDec is not null && File.Exists(refDec), "reference decoder (MMS/storage mode) not available.");
        string dir = TempDir();
        try
        {
            string src = Path.Combine(dir, wide ? "src.awb" : "src.amr");
            File.WriteAllBytes(src, FileBytes(wide, Frames(wide, cod)));
            string ext = wide ? "awb" : "amr";
            string viaAmr = Path.Combine(dir, "ff." + ext), via3gp = Path.Combine(dir, "ff.3gp");
            bool ok = Run("ffmpeg", $"-v error -y -i \"{src}\" -c:a copy -f amr \"{viaAmr}\"") is not null
                      && Run("ffmpeg", $"-v error -y -i \"{src}\" -c:a copy -f 3gp \"{via3gp}\"") is not null;
            Assert.SkipUnless(ok, "ffmpeg not available or cannot mux AMR.");

            string refOut = Path.Combine(dir, "ref.raw");
            Assert.NotNull(Run(refDec!, wide ? $"-mime \"{viaAmr}\" \"{refOut}\"" : $"\"{viaAmr}\" \"{refOut}\""));
            byte[] reference = File.ReadAllBytes(refOut);

            // Informational: how far ffmpeg's own decoder (a different implementation) is from the reference output.
            if (Environment.GetEnvironmentVariable("ONEAUDIO_AMR_REPORT") is { } report)
            {
                string ffPcm = Path.Combine(dir, "ff.pcm");
                if (Run("ffmpeg", $"-v error -y -i \"{viaAmr}\" -f s16le -acodec pcm_s16le \"{ffPcm}\"") is not null)
                {
                    byte[] ff = File.ReadAllBytes(ffPcm);
                    int count = Math.Min(ff.Length, reference.Length) / 2, max = 0;
                    double se = 0;
                    for (int i = 0; i < count; i++)
                    {
                        int diff = BitConverter.ToInt16(ff, i * 2) - BitConverter.ToInt16(reference, i * 2);
                        max = Math.Max(max, Math.Abs(diff));
                        se += (double)diff * diff;
                    }
                    File.AppendAllText(report, $"{(wide ? "wb" : "nb")} {relative} samples={count} ffmpeg-vs-reference max={max} rms={Math.Sqrt(se / count):F2}\n");
                }
            }

            foreach (string file in new[] { viaAmr, via3gp })
            {
                using var dec = Audio.Open(file);
                float[] got = ReadAll(dec);
                // The reference decoder emits every stored frame; a 3GP trims nothing here (no edit list from ffmpeg).
                Assert.Equal(reference.Length / 2, got.Length);
                int bad = 0;
                for (int i = 0; i < got.Length; i++)
                    if (got[i] != BitConverter.ToInt16(reference, i * 2) / 32768f) bad++;
                Assert.True(bad == 0, $"{relative} via {Path.GetFileName(file)}: {bad} of {got.Length} samples differ from the reference");
            }
        }
        finally
        {
            Directory.Delete(dir, true);
        }
    }

    [Theory]
    [InlineData("amrnb.3gp", 150 * 160, 8000)]
    [InlineData("amrwb.3gp", 150 * 320, 16000)]
    [InlineData("synthetic-nb.amr", -1, 8000)]
    [InlineData("synthetic-wb.awb", -1, 16000)]
    public void RandomPayloadFilesOpenAndDecodeWithoutErrors(string name, int length, int rate)
    {
        // Random payloads: only that framing, registry wiring and the decoder's robustness hold is checked.
        using var dec = Audio.Open(ContainerTestEnv.TestData(name));
        Assert.Equal(rate, dec.Format.SampleRate);
        if (length > 0) Assert.Equal(length, dec.LengthFrames);
        float[] all = ReadAll(dec);
        Assert.True(all.Length > 0);
        Assert.All(all, v => Assert.True(float.IsFinite(v) && Math.Abs(v) <= 1f));
    }
}
