using BenchmarkDotNet.Attributes;
using BenchmarkDotNet.Running;
using OneAudio;
using OneAudio.Benchmarks;

if (args.Length > 0 && args[0] == "pitch-sweep") return PitchSweepProbe.Run();
BenchmarkSwitcher.FromAssembly(typeof(ResampleBenchmarks).Assembly).Run(args);
return 0;

namespace OneAudio.Benchmarks
{
/// <summary>Streaming decode of 60 seconds of 16-bit stereo 48 kHz WAV to float.</summary>
[MemoryDiagnoser]
public class WavDecodeBenchmark
{
    private const int Rate = 48_000;
    private const int Seconds = 60;
    private byte[] _file = [];
    private readonly AudioCodecRegistry _registry = new AudioCodecRegistry().AddWav();
    private readonly float[] _block = new float[4096 * 2];

    [GlobalSetup]
    public void Setup()
    {
        int frames = Rate * Seconds;
        var data = new byte[frames * 4];
        var rng = new Random(1);
        rng.NextBytes(data);
        using var ms = new MemoryStream();
        using var w = new BinaryWriter(ms);
        w.Write("RIFF"u8); w.Write(36 + data.Length); w.Write("WAVEfmt "u8);
        w.Write(16); w.Write((short)1); w.Write((short)2); w.Write(Rate); w.Write(Rate * 4); w.Write((short)4); w.Write((short)16);
        w.Write("data"u8); w.Write(data.Length); w.Write(data);
        _file = ms.ToArray();
    }

    [Benchmark]
    public long DecodeToFloat()
    {
        using var decoder = AudioDecoder.Open(new MemoryStream(_file), _registry);
        long total = 0;
        int n;
        while ((n = decoder.Read(_block)) > 0) total += n;
        return total;
    }
}
}
