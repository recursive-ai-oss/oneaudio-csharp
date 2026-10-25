using BenchmarkDotNet.Attributes;
using Concentus;
using OneAudio.Opus;

namespace OneAudio.Benchmarks;

/// <summary>
/// Decodes a whole RFC 8251 test vector per invocation (stereo, 48 kHz, float output). Real-time factor is the
/// vector's duration divided by the mean time. Vectors come from ONEAUDIO_CORPUS (tools/fetch-corpus.sh):
/// 01 is CELT only, 04 is SILK only, 05 is hybrid.
/// </summary>
[MemoryDiagnoser]
public class OpusDecodeBenchmarks
{
    private readonly List<byte[]> _packets = [];
    private float[] _out = [];
    private OpusDecoder _ours = null!;
    private IOpusDecoder _concentus = null!;

    [Params("testvector01", "testvector04", "testvector05")]
    public string Vector { get; set; } = "";

    /// <summary>Audio seconds in the vector, printed once so the table can be read as x real time.</summary>
    public double Seconds { get; private set; }

    [GlobalSetup]
    public void Setup()
    {
        var root = Environment.GetEnvironmentVariable("ONEAUDIO_CORPUS") ?? throw new InvalidOperationException("Set ONEAUDIO_CORPUS.");
        var bit = File.ReadAllBytes(Path.Combine(root, "opus-rfc8251", "opus_newvectors", Vector + ".bit"));
        int pos = 0;
        long frames = 0;
        while (pos + 8 <= bit.Length)
        {
            int len = (bit[pos] << 24) | (bit[pos + 1] << 16) | (bit[pos + 2] << 8) | bit[pos + 3];
            pos += 8;
            var p = bit.AsSpan(pos, len).ToArray();
            pos += len;
            _packets.Add(p);
            frames += OpusPacket.GetSampleCount(p, 48000);
        }
        Seconds = frames / 48000.0;
        Console.WriteLine($"{Vector}: {_packets.Count} packets, {Seconds:F2} s");
        _out = new float[5760 * 2];
        _ours = new OpusDecoder(48000, 2);
        _concentus = OpusCodecFactory.CreateDecoder(48000, 2);
    }

    [Benchmark(Baseline = true)]
    public long OneAudio()
    {
        _ours.Reset();
        long n = 0;
        foreach (var p in _packets) n += _ours.Decode(p, _out);
        return n;
    }

    [Benchmark]
    public long Concentus()
    {
        _concentus.ResetState();
        long n = 0;
        foreach (var p in _packets) n += _concentus.Decode(p, _out, 5760, false);
        return n;
    }
}

/// <summary>
/// Encodes 10 seconds of generated stereo music-like audio at 48 kHz in 20 ms frames, per invocation. Real-time factor is
/// 10 s divided by the mean time. The reference row runs the RFC 6716 encoder (opus_demo, native) as a child process, so it
/// includes process start and file I/O. Set OPUS_DEMO to its path.
/// </summary>
[MemoryDiagnoser]
public class OpusEncodeBenchmarks
{
    private const int Frames = 480_000;
    private float[] _pcm = [];
    private short[] _pcm16 = [];
    private string _work = "";
    private string _demo = "";
    private byte[] _out = new byte[1500];
    private OpusEncoder _ours = null!;
    private IOpusEncoder _concentus = null!;

    [Params(16000, 32000, 64000, 128000)]
    public int Bitrate { get; set; }

    [Params(10, 5)]
    public int Complexity { get; set; }

    [GlobalSetup]
    public void Setup()
    {
        var rng = new Random(7);
        _pcm = new float[Frames * 2];
        _pcm16 = new short[Frames * 2];
        double[] f0 = [110, 164.8, 220, 261.6, 329.6, 440, 880, 1760];
        for (int i = 0; i < Frames; i++)
        {
            double t = i / 48000.0;
            double v = 0;
            for (int k = 0; k < f0.Length; k++)
            {
                double env = Math.Exp(-((t + k * 0.31) % 1.7) * 2.0);
                for (int h = 1; h <= 5; h++) v += env * Math.Sin(2 * Math.PI * f0[k] * h * t) / (h * 1.5) * 0.08;
            }
            v += (i / 4800 % 5 == 2 ? Math.Exp(-(i % 4800) / 600.0) : 0) * (rng.NextDouble() - 0.5) * 0.5;
            v += (rng.NextDouble() - 0.5) * 0.004;
            short s = (short)Math.Clamp(Math.Round(v * 20000), -32768, 32767);
            _pcm16[2 * i] = s;
            _pcm16[2 * i + 1] = (short)Math.Clamp(s * 0.8, -32768, 32767);
            _pcm[2 * i] = _pcm16[2 * i] * (1f / 32768f);
            _pcm[2 * i + 1] = _pcm16[2 * i + 1] * (1f / 32768f);
        }
        _work = Path.Combine(Path.GetTempPath(), "oneaudio-encbench-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_work);
        var bytes = new byte[_pcm16.Length * 2];
        Buffer.BlockCopy(_pcm16, 0, bytes, 0, bytes.Length);
        File.WriteAllBytes(Path.Combine(_work, "in.pcm"), bytes);
        _demo = Environment.GetEnvironmentVariable("OPUS_DEMO") ?? "";
        _ours = new OpusEncoder(new OpusEncoderSettings { BitrateBps = Bitrate, Complexity = Complexity });
        _concentus = OpusCodecFactory.CreateEncoder(48000, 2, global::Concentus.Enums.OpusApplication.OPUS_APPLICATION_AUDIO);
        _concentus.Bitrate = Bitrate;
        _concentus.Complexity = Complexity;
        _concentus.UseVBR = true;
        _concentus.UseConstrainedVBR = false;
        // Which coding modes each encoder picks at this rate, so the rows can be read as like-for-like (or not).
        Console.WriteLine($"[{Bitrate} bps] modes (TOC config x count) OneAudio: {Modes(_ours, null)}; Concentus: {Modes(null, _concentus)}");
        _ours.Reset();
        _concentus.ResetState();
    }

    private string Modes(OpusEncoder? ours, IOpusEncoder? other)
    {
        var seen = new SortedDictionary<int, int>();
        for (int pos = 0, n = 0; pos + 1920 <= _pcm.Length && n < 100; pos += 1920, n++)
        {
            int len = ours is not null ? ours.Encode(_pcm.AsSpan(pos, 1920), 960, _out) : other!.Encode(_pcm.AsSpan(pos, 1920), 960, _out, 1275);
            if (len > 0) seen[_out[0] >> 3] = seen.GetValueOrDefault(_out[0] >> 3) + 1;
        }
        return string.Join(" ", seen.Select(kv => $"{kv.Key}x{kv.Value}"));
    }

    [GlobalCleanup]
    public void Cleanup() => Directory.Delete(_work, true);

    [Benchmark(Baseline = true)]
    public long OneAudio()
    {
        _ours.Reset();
        long bytes = 0;
        for (int pos = 0; pos + 1920 <= _pcm.Length; pos += 1920)
            bytes += _ours.Encode(_pcm.AsSpan(pos, 1920), 960, _out);
        return bytes;
    }

    [Benchmark]
    public long Concentus()
    {
        _concentus.ResetState();
        long bytes = 0;
        for (int pos = 0; pos + 1920 <= _pcm.Length; pos += 1920)
            bytes += _concentus.Encode(_pcm.AsSpan(pos, 1920), 960, _out, 1275);
        return bytes;
    }

    [Benchmark]
    public long ReferenceDemoProcess()
    {
        if (_demo.Length == 0) return 0;
        var psi = new System.Diagnostics.ProcessStartInfo(_demo) { RedirectStandardError = true, RedirectStandardOutput = true };
        foreach (var a in new[] { "-e", "audio", "48000", "2", Bitrate.ToString(), "-complexity", Complexity.ToString(),
                     Path.Combine(_work, "in.pcm"), Path.Combine(_work, "out.bit") })
            psi.ArgumentList.Add(a);
        using var p = System.Diagnostics.Process.Start(psi)!;
        p.StandardOutput.ReadToEnd();
        p.StandardError.ReadToEnd();
        p.WaitForExit();
        return new FileInfo(Path.Combine(_work, "out.bit")).Length;
    }
}
