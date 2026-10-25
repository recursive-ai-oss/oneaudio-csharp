using System.Diagnostics;
using OneAudio.Opus;

namespace OneAudio.Tests;

/// <summary>Shared pieces of the Opus encoder tests: deterministic test signals and the reference encoder as an oracle.</summary>
internal static class OpusEncoderSupport
{
    /// <summary>Path of the reference opus_demo binary, from OPUS_DEMO or next to OPUS_COMPARE; null when unavailable.</summary>
    public static string? DemoPath()
    {
        var p = Environment.GetEnvironmentVariable("OPUS_DEMO");
        if (p is not null && File.Exists(p)) return p;
        var cmp = Environment.GetEnvironmentVariable("OPUS_COMPARE");
        if (cmp is not null)
        {
            var q = Path.Combine(Path.GetDirectoryName(cmp)!, "rfc_opus_demo");
            if (File.Exists(q)) return q;
        }
        return null;
    }

    /// <summary>
    /// A deterministic music-like test signal: a few decaying plucked harmonic tones, a slow sweep, a hi-hat-like noise burst
    /// pattern and a low-level noise floor, different per channel. Returns interleaved 16-bit samples.
    /// </summary>
    public static short[] MakeSignal(int channels, int frames, int seed = 1)
    {
        var rng = new Random(seed);
        var pcm = new short[frames * channels];
        double[] f0 = [110, 164.8, 220, 261.6, 329.6, 440];
        for (int c = 0; c < channels; c++)
        {
            double phase = c * 0.7;
            for (int i = 0; i < frames; i++)
            {
                double t = i / 48000.0;
                double v = 0;
                for (int k = 0; k < f0.Length; k++)
                {
                    double start = k * 0.37;
                    double age = (t - start) % 1.9;
                    if (age < 0) continue;
                    double env = Math.Exp(-age * 2.2);
                    for (int h = 1; h <= 6; h++)
                        v += env * Math.Sin(2 * Math.PI * f0[k] * h * t + phase * h) / (h * 1.5) * 0.12;
                }
                v += 0.05 * Math.Sin(2 * Math.PI * (200 + 3000 * (t % 4.0) / 4.0) * t);
                double burst = (i / 4800) % 5 == 2 ? Math.Exp(-(i % 4800) / 600.0) : 0;
                v += burst * (rng.NextDouble() - 0.5) * 0.5;
                v += (rng.NextDouble() - 0.5) * 0.003;
                pcm[i * channels + c] = (short)Math.Clamp(Math.Round(v * 20000), -32768, 32767);
            }
        }
        return pcm;
    }

    public static float[] ToFloat(short[] pcm)
    {
        var f = new float[pcm.Length];
        for (int i = 0; i < f.Length; i++) f[i] = pcm[i] * (1f / 32768f);
        return f;
    }

    public sealed record RefPacket(byte[] Data, uint Range);

    /// <summary>Runs the reference encoder (opus_demo -e) over raw 16-bit PCM and parses its bit file.</summary>
    public static List<RefPacket> RunReference(string demo, string workDir, short[] pcm, string application, int channels,
        int bitrate, params string[] options)
    {
        Directory.CreateDirectory(workDir);
        var inPath = Path.Combine(workDir, "in.pcm");
        var outPath = Path.Combine(workDir, "out.bit");
        var bytes = new byte[pcm.Length * 2];
        Buffer.BlockCopy(pcm, 0, bytes, 0, bytes.Length);
        File.WriteAllBytes(inPath, bytes);
        if (File.Exists(outPath)) File.Delete(outPath);
        var psi = new ProcessStartInfo(demo) { RedirectStandardError = true, RedirectStandardOutput = true };
        psi.ArgumentList.Add("-e");
        psi.ArgumentList.Add(application);
        psi.ArgumentList.Add("48000");
        psi.ArgumentList.Add(channels.ToString());
        psi.ArgumentList.Add(bitrate.ToString());
        foreach (var o in options) psi.ArgumentList.Add(o);
        psi.ArgumentList.Add(inPath);
        psi.ArgumentList.Add(outPath);
        using var p = Process.Start(psi)!;
        p.StandardOutput.ReadToEnd();
        p.StandardError.ReadToEnd();
        p.WaitForExit();
        if (p.ExitCode != 0) throw new InvalidOperationException("opus_demo failed");
        var bit = File.ReadAllBytes(outPath);
        var list = new List<RefPacket>();
        int pos = 0;
        while (pos + 8 <= bit.Length)
        {
            int len = (bit[pos] << 24) | (bit[pos + 1] << 16) | (bit[pos + 2] << 8) | bit[pos + 3];
            uint range = (uint)((bit[pos + 4] << 24) | (bit[pos + 5] << 16) | (bit[pos + 6] << 8) | bit[pos + 7]);
            pos += 8;
            list.Add(new RefPacket(bit.AsSpan(pos, len).ToArray(), range));
            pos += len;
        }
        return list;
    }

    /// <summary>Encodes all frames of <paramref name="pcm"/> with our encoder, as opus_demo does (the tail is zero padded).</summary>
    public static List<RefPacket> Encode(OpusEncoder enc, short[] pcm, int frameSize)
    {
        int ch = enc.Channels;
        var list = new List<RefPacket>();
        var buf = new byte[1500];
        var frame = new short[frameSize * ch];
        for (int pos = 0; pos + frameSize * ch <= pcm.Length; pos += frameSize * ch)
        {
            pcm.AsSpan(pos, frame.Length).CopyTo(frame);
            int n = enc.Encode(frame, frameSize, buf);
            list.Add(new RefPacket(buf.AsSpan(0, n).ToArray(), enc.FinalRange));
        }
        return list;
    }
}
