using System.Diagnostics;
using System.Globalization;
using OneAudio.Opus;
using Xunit;

namespace OneAudio.Tests;

/// <summary>
/// Conformance against the RFC 8251 test vectors: the plain stereo outputs, and the "m" outputs that skip the
/// intensity-stereo phase inversion (RFC 8251 section 11; a decoder is compliant if it passes either set). Needs the vectors (tools/fetch-corpus.sh, entry opus-rfc8251) under
/// the directory in ONEAUDIO_CORPUS and an <c>opus_compare</c> binary named by OPUS_COMPARE; skipped otherwise.
/// </summary>
public class OpusVectorTests
{
    public static TheoryData<int, bool> Cases()
    {
        var d = new TheoryData<int, bool>();
        for (int i = 1; i <= 12; i++)
        {
            d.Add(i, false);
            d.Add(i, true);
        }
        return d;
    }

    internal static string? VectorDir()
    {
        var root = Environment.GetEnvironmentVariable("ONEAUDIO_CORPUS");
        if (root is null) return null;
        var dir = Path.Combine(root, "opus-rfc8251", "opus_newvectors");
        return Directory.Exists(dir) ? dir : null;
    }

    /// <summary>Decodes an opus_demo style .bit file (big-endian length and final range before each packet) to 16-bit PCM.</summary>
    internal static byte[] DecodeBitFile(string path, int channels, bool noPhaseInversion, out int mismatches)
    {
        var bit = File.ReadAllBytes(path);
        var dec = new OpusDecoder(48000, channels) { PhaseInversionDisabled = noPhaseInversion };
        var pcm = new float[5760 * channels];
        var output = new MemoryStream();
        int pos = 0;
        mismatches = 0;
        while (pos + 8 <= bit.Length)
        {
            int len = (bit[pos] << 24) | (bit[pos + 1] << 16) | (bit[pos + 2] << 8) | bit[pos + 3];
            uint range = (uint)((bit[pos + 4] << 24) | (bit[pos + 5] << 16) | (bit[pos + 6] << 8) | bit[pos + 7]);
            pos += 8;
            if (len < 0 || pos + len > bit.Length) break;
            int n = dec.Decode(bit.AsSpan(pos, len), pcm);
            pos += len;
            if (len > 0 && dec.FinalRange != range) mismatches++;
            for (int i = 0; i < n * channels; i++)
            {
                double v = Math.Round(pcm[i] * 32768.0);
                short s = (short)Math.Clamp(v, -32768, 32767);
                output.WriteByte((byte)s);
                output.WriteByte((byte)(s >> 8));
            }
        }
        return output.ToArray();
    }

    [Theory]
    [MemberData(nameof(Cases))]
    public void Vector_passes_opus_compare(int number, bool mono)
    {
        var dir = VectorDir();
        var tool = Environment.GetEnvironmentVariable("OPUS_COMPARE");
        if (dir is null || tool is null) return;

        string name = $"testvector{number:00}";
        var pcm = DecodeBitFile(Path.Combine(dir, name + ".bit"), 2, mono, out int mismatches);
        var tmp = Path.Combine(Path.GetTempPath(), $"oneaudio-opus-{Environment.ProcessId}-{number}-{mono}.pcm");
        File.WriteAllBytes(tmp, pcm);
        try
        {
            var psi = new ProcessStartInfo(tool) { RedirectStandardError = true, RedirectStandardOutput = true };
            psi.ArgumentList.Add("-s");
            psi.ArgumentList.Add(Path.Combine(dir, name + (mono ? "m.dec" : ".dec")));
            psi.ArgumentList.Add(tmp);
            using var p = Process.Start(psi)!;
            string text = p.StandardError.ReadToEnd() + p.StandardOutput.ReadToEnd();
            p.WaitForExit();
            var line = $"{name} {(mono ? "m-set" : "stereo")}: {text.Replace('\n', ' ').Trim()} (final-range mismatches {mismatches})";
            Console.WriteLine(line);
            var log = Environment.GetEnvironmentVariable("OPUS_VECTOR_LOG");
            if (log is not null) lock (typeof(OpusVectorTests)) File.AppendAllText(log, line + "\n");
            Assert.True(p.ExitCode == 0 && text.Contains("PASS", StringComparison.Ordinal), text);
        }
        finally
        {
            if (Environment.GetEnvironmentVariable("OPUS_KEEP") is null) File.Delete(tmp);
        }
    }
}
