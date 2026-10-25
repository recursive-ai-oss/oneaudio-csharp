using System.Diagnostics;
using BenchmarkDotNet.Attributes;
using OneAudio.Flac;

namespace OneAudio.Benchmarks;

/// <summary>
/// FLAC encode speed: ours (samples already in memory, output discarded) against the reference <c>flac</c> command encoding a WAV
/// file of the same audio to <c>/dev/null</c> (set ONEAUDIO_FLAC; the process start and the WAV read are inside its time, which the
/// length of the input makes small). The input is about five minutes of 16-bit stereo 44.1 kHz music decoded from the Xiph Vorbis test
/// files (ONEAUDIO_CORPUS, tools/fetch-corpus.sh), written once to a WAV file by our WAV encoder. Speed as a multiple of real time
/// is the audio duration (printed at setup) divided by the mean time.
/// </summary>
[MemoryDiagnoser]
public class FlacEncodeBenchmark
{
    private int[] _samples = [];
    private string _wav = "";
    private double _seconds;

    /// <summary>Compression level 0 to 8.</summary>
    [Params(0, 2, 5, 8)]
    public int Level { get; set; }

    [GlobalSetup]
    public void Setup()
    {
        string corpus = Environment.GetEnvironmentVariable("ONEAUDIO_CORPUS") ?? throw new InvalidOperationException("Set ONEAUDIO_CORPUS (tools/fetch-corpus.sh).");
        var registry = new AudioCodecRegistry().AddOgg().AddVorbis();
        var all = new List<int>();
        const int Rate = 44100;
        foreach (string ogg in Directory.GetDirectories(corpus, "vorbis-xiph-*").OrderBy(d => d, StringComparer.Ordinal).SelectMany(d => Directory.GetFiles(d, "*.ogg").OrderBy(f => f, StringComparer.Ordinal)))
        {
            if (all.Count / 2 >= 300 * Rate) break;
            try
            {
                using var dec = AudioDecoder.OpenFile(ogg, registry);
                if (dec.Format.Channels != 2 || dec.Format.SampleRate != Rate) continue;
                var buf = new float[8192];
                int n;
                while ((n = dec.Read(buf)) > 0)
                    for (int i = 0; i < n * 2; i++) all.Add((int)Math.Clamp(Math.Round(buf[i] * 32768.0), -32768, 32767));
            }
            catch (OneAudioException) { }
        }
        _samples = all.ToArray();
        _seconds = (double)(_samples.Length / 2) / Rate;
        _wav = Path.Combine(Path.GetTempPath(), $"flac-encode-bench-{Environment.ProcessId}.wav");
        using (var fs = File.Create(_wav))
        using (var w = new WavEncoder(fs, new AudioFormat(Rate, 2), new WavEncoderSettings { SampleType = NativeSampleType.PcmSigned16 }, true))
        {
            w.WriteInteger(_samples);
            w.Finish();
        }
        Console.WriteLine($"[level {Level}] {_seconds:F1} s of 16-bit stereo {Rate} Hz music");
    }

    [GlobalCleanup]
    public void Cleanup()
    {
        if (File.Exists(_wav)) File.Delete(_wav);
    }

    [Benchmark(Baseline = true)]
    public long OneAudio()
    {
        using var sink = new CountingSink();
        using var enc = new FlacEncoder(sink, new AudioFormat(44100, 2), new FlacEncoderSettings { CompressionLevel = Level, BitsPerSample = 16 }, true);
        enc.WriteInteger(_samples);
        enc.Finish();
        return sink.Length;
    }

    [Benchmark]
    public int ReferenceFlacCommand()
    {
        string? exe = Environment.GetEnvironmentVariable("ONEAUDIO_FLAC");
        if (exe is null) return -1;
        var psi = new ProcessStartInfo(exe) { UseShellExecute = false, RedirectStandardError = true };
        foreach (var a in new[] { "-" + Level, "-s", "-f", "-o", "/dev/null", _wav }) psi.ArgumentList.Add(a);
        using var p = Process.Start(psi)!;
        p.WaitForExit();
        return p.ExitCode;
    }

    /// <summary>Seekable stream that keeps nothing.</summary>
    private sealed class CountingSink : Stream
    {
        private long _pos, _len;
        public override bool CanRead => false;
        public override bool CanSeek => true;
        public override bool CanWrite => true;
        public override long Length => _len;
        public override long Position { get => _pos; set => _pos = value; }
        public override void Flush() { }
        public override int Read(byte[] buffer, int offset, int count) => throw new NotSupportedException();
        public override long Seek(long offset, SeekOrigin origin) => _pos = origin switch { SeekOrigin.Begin => offset, SeekOrigin.Current => _pos + offset, _ => _len + offset };
        public override void SetLength(long value) => _len = value;
        public override void Write(byte[] buffer, int offset, int count) => Write(buffer.AsSpan(offset, count));
        public override void Write(ReadOnlySpan<byte> buffer) { _pos += buffer.Length; _len = Math.Max(_len, _pos); }
    }
}

/// <summary>WAV encode of 60 seconds of 48 kHz stereo float input to each stored type, output discarded.</summary>
[MemoryDiagnoser]
public class WavEncodeBenchmark
{
    private readonly float[] _block = new float[48000 * 2];

    /// <summary>Stored sample type.</summary>
    [Params(NativeSampleType.PcmSigned16, NativeSampleType.PcmSigned24, NativeSampleType.IeeeFloat32)]
    public NativeSampleType Type { get; set; }

    /// <summary>Whether TPDF dither is applied (integer types only).</summary>
    [Params(false, true)]
    public bool Dither { get; set; }

    [GlobalSetup]
    public void Setup()
    {
        for (int i = 0; i < _block.Length; i++) _block[i] = 0.5f * MathF.Sin(i * 0.01f);
    }

    [Benchmark]
    public long Encode60Seconds()
    {
        using var sink = new NullSeekable();
        using var enc = new WavEncoder(sink, new AudioFormat(48000, 2), new WavEncoderSettings { SampleType = Type, Dither = Dither }, true);
        for (int i = 0; i < 60; i++) enc.Write(_block);
        enc.Finish();
        return sink.Length;
    }

    private sealed class NullSeekable : Stream
    {
        private long _pos, _len;
        public override bool CanRead => false;
        public override bool CanSeek => true;
        public override bool CanWrite => true;
        public override long Length => _len;
        public override long Position { get => _pos; set => _pos = value; }
        public override void Flush() { }
        public override int Read(byte[] buffer, int offset, int count) => throw new NotSupportedException();
        public override long Seek(long offset, SeekOrigin origin) => _pos = origin switch { SeekOrigin.Begin => offset, SeekOrigin.Current => _pos + offset, _ => _len + offset };
        public override void SetLength(long value) => _len = value;
        public override void Write(byte[] buffer, int offset, int count) => Write(buffer.AsSpan(offset, count));
        public override void Write(ReadOnlySpan<byte> buffer) { _pos += buffer.Length; _len = Math.Max(_len, _pos); }
    }
}
