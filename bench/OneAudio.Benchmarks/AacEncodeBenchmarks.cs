using BenchmarkDotNet.Attributes;
using OneAudio.Aac;
using OneAudio.Flac;

namespace OneAudio.Benchmarks;

/// <summary>
/// AAC-LC encode speed: 30 s of generated music-like audio (harmonic tones with vibrato, noise bursts that force short blocks, a
/// little hiss) pushed through <see cref="AacEncoder"/> in hops of 1024 frames, units discarded. Speed as a multiple of real time
/// is 30 s divided by the mean time; the allocation column covers construction and the whole encode (the steady state is zero).
/// </summary>
[MemoryDiagnoser]
public class AacEncodeBenchmark
{
    private const int Rate = 44100, Seconds = 30;
    private float[] _pcm = [];

    /// <summary>Channels and bit rate in kbit/s.</summary>
    [Params("2:128", "2:64", "1:64")]
    public string Setting { get; set; } = "";

    [GlobalSetup]
    public void Setup()
    {
        int channels = int.Parse(Setting.Split(':')[0]);
        var rng = new Random(1);
        _pcm = new float[Rate * Seconds * channels];
        for (int c = 0; c < channels; c++)
        {
            double ph = 0;
            for (int i = 0; i < Rate * Seconds; i++)
            {
                double t = i / (double)Rate;
                ph += 2 * Math.PI * (220 * (c + 1) + 5 * Math.Sin(2 * Math.PI * 5 * t)) / Rate;
                double v = 0.25 * Math.Sin(ph) + 0.12 * Math.Sin(2 * ph) + 0.06 * Math.Sin(3 * ph)
                    + 0.3 * Math.Exp(-((t * 3) % 1) * 40) * (rng.NextDouble() - 0.5) + 0.004 * (rng.NextDouble() - 0.5);
                _pcm[i * channels + c] = (float)v;
            }
        }
    }

    [Benchmark]
    public long Encode()
    {
        string[] parts = Setting.Split(':');
        int channels = int.Parse(parts[0]);
        var enc = new AacEncoder(Rate, channels, new AacEncoderSettings { Bitrate = int.Parse(parts[1]) * 1000 });
        int hop = 1024 * channels;
        long bytes = 0;
        for (int pos = 0; pos + hop <= _pcm.Length; pos += hop)
        {
            enc.Write(_pcm.AsSpan(pos, hop));
            while (enc.TryReadPacket(out var p)) bytes += p.Data.Length;
        }
        enc.Finish();
        while (enc.TryReadPacket(out var p)) bytes += p.Data.Length;
        return bytes;
    }
}

/// <summary>Packet-level FLAC encode speed over 30 s of generated 16-bit stereo audio at level 5, frames discarded.</summary>
[MemoryDiagnoser]
public class FlacPacketEncodeBenchmark
{
    private int[] _pcm = [];

    [GlobalSetup]
    public void Setup()
    {
        var rng = new Random(2);
        _pcm = new int[44100 * 30 * 2];
        for (int i = 0; i < _pcm.Length; i++)
            _pcm[i] = (int)(9000 * Math.Sin(i * 0.013) + 3000 * Math.Sin(i * 0.071) + 200 * (rng.NextDouble() - 0.5));
    }

    [Benchmark]
    public long Encode()
    {
        using var enc = new FlacPacketEncoder(new AudioFormat(44100, 2), new FlacEncoderSettings { BitsPerSample = 16, CompressionLevel = 5 });
        long bytes = 0;
        var span = _pcm.AsSpan();
        while (span.Length > 0)
        {
            int taken = enc.WriteInteger(span);
            span = span[(taken * 2)..];
            while (enc.TryReadPacket(out var p)) bytes += p.Data.Length;
        }
        enc.Finish();
        while (enc.TryReadPacket(out var p)) bytes += p.Data.Length;
        return bytes;
    }
}
