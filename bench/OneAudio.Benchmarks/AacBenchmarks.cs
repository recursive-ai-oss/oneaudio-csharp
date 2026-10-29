using System.Diagnostics;
using BenchmarkDotNet.Attributes;

namespace OneAudio.Benchmarks;

/// <summary>
/// Streaming AAC-LC decode of a whole 30 s M4A to interleaved float. The file is made at setup with the ffmpeg command-line
/// encoder (a mix of tones and noise, so TNS, M/S and short blocks occur) and needs ffmpeg on PATH. Speed as a multiple
/// of real time is 30 s divided by the mean time; the allocation column covers opening the file and the whole decode.
/// </summary>
[MemoryDiagnoser]
public class AacDecodeBenchmark
{
    private readonly AudioCodecRegistry _registry = new AudioCodecRegistry().AddMp4().AddAac();
    private byte[] _file = [];
    private float[] _block = [];

    /// <summary>Encoder setting: channels and bit rate in kbit/s.</summary>
    [Params("2:128", "1:64")]
    public string Setting { get; set; } = "";

    /// <summary>Audio duration of the file in seconds.</summary>
    public double Seconds { get; private set; }

    [GlobalSetup]
    public void Setup()
    {
        string[] parts = Setting.Split(':');
        string dir = Environment.GetEnvironmentVariable("TMPDIR") is { Length: > 0 } t ? t : Path.GetTempPath();
        string path = Path.Combine(dir, $"oneaudio-bench-aac-{parts[0]}-{parts[1]}.m4a");
        if (!File.Exists(path))
        {
            var psi = new ProcessStartInfo("ffmpeg") { RedirectStandardError = true, RedirectStandardOutput = true };
            foreach (string a in new[]
            {
                "-v", "error", "-y", "-f", "lavfi", "-i", "sine=f=440:d=30:r=44100,volume=0.3", "-f", "lavfi", "-i", "anoisesrc=d=30:r=44100:a=0.1:seed=3",
                "-filter_complex", "amix=inputs=2:duration=first,aformat=channel_layouts=" + (parts[0] == "1" ? "mono" : "stereo"),
                "-c:a", "aac", "-b:a", parts[1] + "k", path,
            }) psi.ArgumentList.Add(a);
            using var p = Process.Start(psi) ?? throw new InvalidOperationException("ffmpeg did not start.");
            p.WaitForExit();
            if (p.ExitCode != 0) throw new InvalidOperationException("ffmpeg failed.");
        }
        _file = File.ReadAllBytes(path);
        using var d = AudioDecoder.Open(new MemoryStream(_file), _registry);
        _block = new float[4096 * d.Format.Channels];
        Seconds = (double)d.LengthFrames / d.Format.SampleRate;
    }

    [Benchmark]
    public long Decode()
    {
        using var decoder = AudioDecoder.Open(new MemoryStream(_file), _registry);
        long frames = 0;
        int n;
        while ((n = decoder.Read(_block)) > 0) frames += n;
        return frames;
    }
}
