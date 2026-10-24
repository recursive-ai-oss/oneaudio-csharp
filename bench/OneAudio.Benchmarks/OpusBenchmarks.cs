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
