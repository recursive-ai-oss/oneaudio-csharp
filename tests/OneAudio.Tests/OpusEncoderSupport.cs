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

    /// <summary>The reference demo variant that accepts -loss when encoding (so the encoder sees the expected loss rate), or null.</summary>
    public static string? DemoLossPath()
    {
        var cmp = Environment.GetEnvironmentVariable("OPUS_COMPARE");
        if (cmp is null) return null;
        var q = Path.Combine(Path.GetDirectoryName(cmp)!, "rfc_opus_demo_loss");
        return File.Exists(q) ? q : null;
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

    /// <summary>
    /// A deterministic speech-like test signal: a voiced glottal pulse train with moving pitch and formants in syllable-sized
    /// bursts, fricative noise bursts and near-silent gaps (to exercise voice activity detection and DTX). The second channel
    /// is a delayed, filtered mix of the first. Returns interleaved 16-bit samples.
    /// </summary>
    public static short[] MakeSpeechLike(int channels, int frames, int seed = 1)
    {
        var rng = new Random(seed);
        var mono = new double[frames];
        double phase = 0, y1a = 0, y2a = 0, y1b = 0, y2b = 0, y1c = 0, y2c = 0, lp = 0;
        for (int i = 0; i < frames; i++)
        {
            double t = i / 48000.0;
            double syl = t % 0.45;
            double env = syl < 0.30 ? Math.Sin(Math.PI * syl / 0.30) : 0.0;
            bool fric = (int)(t / 0.45) % 3 == 2 && syl > 0.30 && syl < 0.42;
            double f0 = 110 + 40 * Math.Sin(2 * Math.PI * 0.7 * t) + 8 * Math.Sin(2 * Math.PI * 5.5 * t);
            phase += f0 / 48000.0;
            double src = 0;
            if (phase >= 1) { phase -= 1; src = 1.0; }
            lp += 0.2 * (src - lp);
            double ex = lp * 8 + (rng.NextDouble() - 0.5) * 0.02;
            double f1 = 500 + 250 * Math.Sin(2 * Math.PI * 1.3 * t), f2 = 1500 + 600 * Math.Sin(2 * Math.PI * 0.9 * t), f3 = 2600;
            double Res(double x, double f, double bw, ref double y1, ref double y2)
            {
                double r = Math.Exp(-Math.PI * bw / 48000), th = 2 * Math.PI * f / 48000;
                double y = x + 2 * r * Math.Cos(th) * y1 - r * r * y2;
                y2 = y1; y1 = y;
                return y * (1 - r);
            }
            double v = Res(ex, f1, 90, ref y1a, ref y2a) + 0.6 * Res(ex, f2, 120, ref y1b, ref y2b) + 0.3 * Res(ex, f3, 200, ref y1c, ref y2c);
            v *= env * 2.0;
            if (fric) v += (rng.NextDouble() - 0.5) * 0.25;
            v += (rng.NextDouble() - 0.5) * 0.0004;
            mono[i] = v;
        }
        var pcm = new short[frames * channels];
        for (int i = 0; i < frames; i++)
            for (int c = 0; c < channels; c++)
            {
                double v = c == 0 ? mono[i] : 0.7 * mono[Math.Max(0, i - 23)] + 0.2 * mono[i];
                pcm[i * channels + c] = (short)Math.Clamp(Math.Round(v * 16000), -32768, 32767);
            }
        return pcm;
    }

    /// <summary>The glibc rand() sequence (default seed), which opus_demo uses for its random frame size and FEC options.</summary>
    public sealed class GlibcRand
    {
        private readonly uint[] _r = new uint[34 + 344 + 100000];
        private int _n;

        public GlibcRand()
        {
            var r = _r;
            r[0] = 1;
            for (int i = 1; i < 31; i++)
            {
                long hi = (int)r[i - 1] / 127773, lo = (int)r[i - 1] % 127773;
                long word = 16807 * lo - 2836 * hi;
                if (word < 0) word += 2147483647;
                r[i] = (uint)word;
            }
            for (int i = 31; i < 34; i++) r[i] = r[i - 31];
            for (int i = 34; i < 344; i++) r[i] = r[i - 31] + r[i - 3];
            _n = 344;
        }

        public int Next()
        {
            if (_n >= _r.Length) throw new InvalidOperationException("rand sequence exhausted");
            _r[_n] = _r[_n - 31] + _r[_n - 3];
            return (int)(_r[_n++] >> 1);
        }
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
