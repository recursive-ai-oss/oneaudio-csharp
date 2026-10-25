using System.Diagnostics;
using BenchmarkDotNet.Attributes;

namespace OneAudio.Benchmarks;

/// <summary>
/// Streaming decode of whole MP3 files to interleaved float: ours against NLayer (compiled NuGet package) and against
/// the native mpg123 command-line player (decode only, <c>mpg123 -q -t</c>, so its time includes process start-up,
/// about a millisecond, and no output). The files are LAME encodes of generated signals (tools/gen-mp3-testset.py into
/// $ONEAUDIO_CORPUS/mp3-gen). Speed as a multiple of real time is the audio duration divided by the mean time.
/// </summary>
[MemoryDiagnoser]
public class Mp3DecodeBenchmark
{
    private readonly AudioCodecRegistry _registry = new AudioCodecRegistry().AddMp3();
    private byte[] _file = [];
    private string _path = "";
    private float[] _block = [];

    /// <summary>File name without extension (30 s of audio each).</summary>
    [Params("bench_44100_2_128", "bench_44100_2_320", "bench_22050_1_64")]
    public string File { get; set; } = "";

    /// <summary>Audio duration of the file in seconds.</summary>
    public double Seconds { get; private set; }

    [GlobalSetup]
    public void Setup()
    {
        string root = Environment.GetEnvironmentVariable("ONEAUDIO_CORPUS") ?? throw new InvalidOperationException("Set ONEAUDIO_CORPUS.");
        _path = Path.Combine(root, "mp3-gen", File + ".mp3");
        _file = System.IO.File.ReadAllBytes(_path);
        using var d = AudioDecoder.Open(new MemoryStream(_file), _registry);
        _block = new float[4096 * d.Format.Channels];
        Seconds = (double)d.LengthFrames / d.Format.SampleRate;
        long ours = OneAudioDecode();
        long theirs = NLayerDecode();
        Console.WriteLine($"[{File}] {Seconds:F2} s, {d.Format.Channels} ch @ {d.Format.SampleRate} Hz; frames ours {ours}, NLayer {theirs}");
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
    public long NLayerDecode()
    {
        var mpeg = new NLayer.MpegFile(new MemoryStream(_file));
        long samples = 0;
        int n;
        while ((n = mpeg.ReadSamples(_block, 0, _block.Length)) > 0) samples += n;
        return samples / mpeg.Channels;
    }

    [Benchmark]
    public int Mpg123Native()
    {
        var psi = new ProcessStartInfo("mpg123") { UseShellExecute = false, RedirectStandardOutput = true, RedirectStandardError = true };
        foreach (var a in new[] { "-q", "-t", _path }) psi.ArgumentList.Add(a);
        using var p = Process.Start(psi)!;
        p.StandardOutput.ReadToEnd();
        p.StandardError.ReadToEnd();
        p.WaitForExit();
        return p.ExitCode;
    }
}
