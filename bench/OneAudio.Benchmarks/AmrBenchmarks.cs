using BenchmarkDotNet.Attributes;

namespace OneAudio.Benchmarks;

/// <summary>
/// AMR / AMR-WB speech decode: 3000 frames (60 s) per call through the frame decoders, one mode per row. The payloads
/// are pseudo-random bits, which exercise the same fixed-point paths as speech (the decoder does the same work per
/// frame whatever the bits). Each call decodes 3000 frames, so frames per second is 3000 divided by the mean time and
/// the speed as a multiple of real time is 60 s divided by the mean time.
/// </summary>
[MemoryDiagnoser]
public class AmrDecodeBenchmark
{
    private const int Frames = 3000;
    private static readonly int[] NbBytes = [12, 13, 15, 17, 19, 20, 26, 31, 5];
    private static readonly int[] WbBytes = [17, 23, 32, 36, 40, 46, 50, 58, 60, 5];

    private byte[][] _frames = [];
    private readonly AmrNbDecoder _nb = new();
    private readonly AmrWbDecoder _wb = new();
    private readonly short[] _pcm = new short[320];

    /// <summary>Codec and mode: "nb:7" is AMR 12.2 kbit/s, "wb:8" is AMR-WB 23.85 kbit/s.</summary>
    [Params("nb:0", "nb:7", "wb:0", "wb:2", "wb:8")]
    public string Mode { get; set; } = "";

    [GlobalSetup]
    public void Setup()
    {
        bool wide = Mode.StartsWith("wb", StringComparison.Ordinal);
        int ft = int.Parse(Mode[3..], System.Globalization.CultureInfo.InvariantCulture);
        int bytes = (wide ? WbBytes : NbBytes)[ft];
        var rng = new Random(7);
        _frames = new byte[Frames][];
        for (int i = 0; i < Frames; i++)
        {
            var f = new byte[1 + bytes];
            rng.NextBytes(f);
            f[0] = (byte)((ft << 3) | 4);
            _frames[i] = f;
        }
    }

    [Benchmark]
    public int Decode()
    {
        int sum = 0;
        if (Mode[0] == 'n')
        {
            _nb.Reset();
            foreach (byte[] f in _frames) { _nb.Decode(f, _pcm); sum += _pcm[0]; }
        }
        else
        {
            _wb.Reset();
            foreach (byte[] f in _frames) { _wb.Decode(f, _pcm); sum += _pcm[0]; }
        }
        return sum;
    }
}
