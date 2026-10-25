using System.Diagnostics;
using OneAudio.Flac;
using BenchmarkDotNet.Attributes;

namespace OneAudio.Benchmarks;

/// <summary>
/// Whole-file FLAC decode: ours (native integers, float, and with the STREAMINFO MD5 check), the managed PureFlac and
/// NAudio.Flac packages (compiled packages, used as black boxes), and the reference <c>flac -d</c> command (a separate
/// process writing to /dev/null, so it includes process start and its own MD5 check; set ONEAUDIO_FLAC). Files are the
/// CELLAR test files fetched by tools/fetch-corpus.sh (set ONEAUDIO_CORPUS). Speed as a multiple of real time is the
/// audio duration divided by the mean time; the duration is printed at setup.
/// </summary>
[MemoryDiagnoser]
public class FlacDecodeBenchmark
{
    private readonly AudioCodecRegistry _registry = new AudioCodecRegistry().AddFlac();
    private byte[] _file = [];
    private string _path = "";
    private int[] _ints = [];
    private float[] _floats = [];
    private byte[] _bytes = new byte[1 << 16];

    /// <summary>CELLAR subset file (without the folder).</summary>
    [Params("01 - blocksize 4096", "28 - high resolution audio, default settings", "33 - samplerate 192kHz")]
    public string File { get; set; } = "";

    [GlobalSetup]
    public void Setup()
    {
        string root = Environment.GetEnvironmentVariable("ONEAUDIO_CORPUS") ?? throw new InvalidOperationException("Set ONEAUDIO_CORPUS (tools/fetch-corpus.sh).");
        _path = Path.Combine(root, "flac-cellar", "subset", File + ".flac");
        _file = System.IO.File.ReadAllBytes(_path);
        using var d = AudioDecoder.Open(new MemoryStream(_file), _registry);
        _ints = new int[4096 * d.Format.Channels];
        _floats = new float[4096 * d.Format.Channels];
        double seconds = (double)d.LengthFrames / d.Format.SampleRate;
        Console.WriteLine($"[{File}] {seconds:F2} s, {d.Format.Channels} ch @ {d.Format.SampleRate} Hz, {((FlacDecoder)d).BitsPerSample}-bit, {_file.Length / 1024} KiB");
    }

    [Benchmark(Baseline = true)]
    public long OneAudioInteger()
    {
        using var d = AudioDecoder.Open(new MemoryStream(_file), _registry);
        long frames = 0;
        int n;
        while ((n = d.ReadInteger(_ints)) > 0) frames += n;
        return frames;
    }

    [Benchmark]
    public long OneAudioFloat()
    {
        using var d = AudioDecoder.Open(new MemoryStream(_file), _registry);
        long frames = 0;
        int n;
        while ((n = d.Read(_floats)) > 0) frames += n;
        return frames;
    }

    [Benchmark]
    public bool? OneAudioVerifyMd5()
    {
        using var d = (FlacDecoder)AudioDecoder.Open(new MemoryStream(_file), _registry);
        return d.VerifyMd5();
    }

    [Benchmark]
    public long PureFlac()
    {
        using var d = new global::PureFlac.FlacStreamDecoder(new MemoryStream(_file));
        long bytes = 0;
        int n;
        while ((n = d.Read(_bytes, 0, _bytes.Length)) > 0) bytes += n;
        return bytes;
    }

    [Benchmark]
    public long NAudioFlac()
    {
        using var r = new NAudio.Flac.FlacReader(new MemoryStream(_file));
        long bytes = 0;
        int n;
        while ((n = r.Read(_bytes, 0, _bytes.Length)) > 0) bytes += n;
        return bytes;
    }

    [Benchmark]
    public int ReferenceFlacCommand()
    {
        string? exe = Environment.GetEnvironmentVariable("ONEAUDIO_FLAC");
        if (exe is null) return -1;
        var psi = new ProcessStartInfo(exe) { UseShellExecute = false, RedirectStandardError = true };
        foreach (var a in new[] { "-d", "-s", "-f", "-o", "/dev/null", _path }) psi.ArgumentList.Add(a);
        using var p = Process.Start(psi)!;
        p.WaitForExit();
        return p.ExitCode;
    }
}
