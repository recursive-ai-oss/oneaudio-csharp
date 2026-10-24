using BenchmarkDotNet.Attributes;

namespace OneAudio.Benchmarks;

/// <summary>
/// Streaming decode of whole Ogg Vorbis files to interleaved float, ours against NVorbis. The files are the Xiph test
/// vectors fetched by tools/fetch-corpus.sh (set ONEAUDIO_CORPUS). Speed as a multiple of real time is the audio
/// duration divided by the mean time; <see cref="Seconds"/> is printed at setup.
/// </summary>
[MemoryDiagnoser]
public class VorbisDecodeBenchmark
{
    private readonly AudioCodecRegistry _registry = new AudioCodecRegistry().AddOgg().AddVorbis();
    private byte[] _file = [];
    private float[] _block = [];

    /// <summary>Corpus file name without extension.</summary>
    [Params("rc3-test", "48k-mono", "1.0.1-test")]
    public string File { get; set; } = "";

    /// <summary>Audio duration of the file in seconds.</summary>
    public double Seconds { get; private set; }

    [GlobalSetup]
    public void Setup()
    {
        string root = Environment.GetEnvironmentVariable("ONEAUDIO_CORPUS") ?? throw new InvalidOperationException("Set ONEAUDIO_CORPUS (tools/fetch-corpus.sh).");
        _file = System.IO.File.ReadAllBytes(Path.Combine(root, $"vorbis-xiph-{File}", $"{File}.ogg"));
        using var d = AudioDecoder.Open(new MemoryStream(_file), _registry);
        _block = new float[4096 * d.Format.Channels];
        Seconds = (double)d.LengthFrames / d.Format.SampleRate;
        long ours = OneAudioDecode();
        long theirs = NVorbisDecode();
        Console.WriteLine($"[{File}] {Seconds:F2} s, {d.Format.Channels} ch @ {d.Format.SampleRate} Hz; frames ours {ours}, NVorbis {theirs}");
    }

    [Benchmark(Baseline = true)]
    public long OneAudioDecode()
    {
        using var decoder = AudioDecoder.Open(new MemoryStream(_file), _registry);
        long frames = 0;
        int n;
        while ((n = decoder.Read(_block)) > 0) frames += n;
        return frames;
    }

    [Benchmark]
    public long NVorbisDecode()
    {
        using var reader = new NVorbis.VorbisReader(new MemoryStream(_file), true);
        long samples = 0;
        int n;
        while ((n = reader.ReadSamples(_block, 0, _block.Length)) > 0) samples += n;
        return samples / reader.Channels;
    }
}
