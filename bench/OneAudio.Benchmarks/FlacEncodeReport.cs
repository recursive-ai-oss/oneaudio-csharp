using System.Diagnostics;
using System.Globalization;
using System.Text;
using OneAudio.Flac;

namespace OneAudio.Benchmarks;

/// <summary>
/// Compression comparison of our FLAC encoder against the reference <c>flac</c> command at the same levels.
/// Inputs: the CELLAR subset files (re-encoded from their decoded samples; the reference re-encodes the file itself) and a
/// music set decoded from the Xiph Vorbis test files (real recordings, first 30 s, rounded to 16 bits). Sizes compare the
/// audio frames only (metadata, padding and seek tables are excluded on both sides). Output is a TSV plus a summary per level.
/// Run: <c>dotnet run -c Release --project bench/OneAudio.Benchmarks -- flac-encode-report out.tsv [levels] [subset|music]</c>
/// with ONEAUDIO_CORPUS set and the reference binary in ONEAUDIO_FLAC (or next to the corpus).
/// </summary>
internal static class FlacEncodeReport
{
    public static int Run(string[] args)
    {
        string outPath = args.Length > 1 ? args[1] : "flac-encode-report.tsv";
        int[] levels = args.Length > 2 ? args[2].Split(',').Select(int.Parse).ToArray() : [0, 5, 8];
        string which = args.Length > 3 ? args[3] : "all";
        string corpus = Environment.GetEnvironmentVariable("ONEAUDIO_CORPUS") ?? throw new InvalidOperationException("Set ONEAUDIO_CORPUS.");
        string flac = Environment.GetEnvironmentVariable("ONEAUDIO_FLAC") ?? Path.Combine(corpus, "..", "tools", "bin", "flac");
        string work = Path.Combine(Path.GetTempPath(), "flac-report-" + Environment.ProcessId);
        Directory.CreateDirectory(work);

        var rows = new List<string>();
        var totals = new Dictionary<(string Set, int Level), (long Ours, long Ref, double OursSec, double RefSec, double AudioSec)>();
        void Add(string set, string name, int level, long ours, long reference, double oursSec, double refSec, double audioSec)
        {
            rows.Add(string.Join('\t', set, name, level, ours, reference, ((double)ours / reference - 1).ToString("P3", CultureInfo.InvariantCulture),
                oursSec.ToString("F3", CultureInfo.InvariantCulture), refSec.ToString("F3", CultureInfo.InvariantCulture), audioSec.ToString("F2", CultureInfo.InvariantCulture)));
            var t = totals.GetValueOrDefault((set, level));
            totals[(set, level)] = (t.Ours + ours, t.Ref + reference, t.OursSec + oursSec, t.RefSec + refSec, t.AudioSec + audioSec);
        }

        if (which is "all" or "subset")
        {
            foreach (string file in Directory.GetFiles(Path.Combine(corpus, "flac-cellar", "subset"), "*.flac").OrderBy(f => f, StringComparer.Ordinal))
            {
                string name = Path.GetFileNameWithoutExtension(file);
                int num = int.Parse(name.AsSpan(0, 2), CultureInfo.InvariantCulture);
                if (num is >= 45 and <= 59) continue;
                if (Environment.GetEnvironmentVariable("ONEAUDIO_ONLY") is { } only && num != int.Parse(only)) continue; // metadata torture files, not audio variety
                var (samples, fmt, bits) = DecodeFlac(file);
                foreach (int level in levels)
                {
                    var (ours, sec) = EncodeOurs(samples, fmt, bits, level);
                    string refOut = Path.Combine(work, "ref.flac");
                    double refSec = RunFlac(flac, level, file, refOut, work);
                    Add("subset", name, level, AudioBytes(ours), AudioBytes(File.ReadAllBytes(refOut)), sec, refSec, (double)(samples.Length / fmt.Channels) / fmt.SampleRate);
                }
                Console.Error.WriteLine("subset " + name);
            }
        }

        if (which is "all" or "music")
        {
            var registry = new AudioCodecRegistry().AddOgg().AddVorbis();
            foreach (string dir in Directory.GetDirectories(corpus, "vorbis-xiph-*").OrderBy(d => d, StringComparer.Ordinal))
                foreach (string ogg in Directory.GetFiles(dir, "*.ogg").OrderBy(f => f, StringComparer.Ordinal))
                {
                    int[] samples;
                    AudioFormat fmt;
                    try { (samples, fmt) = DecodeToPcm16(registry, ogg, 30); }
                    catch (OneAudioException) { continue; }
                    if (samples.Length / fmt.Channels < fmt.SampleRate * 2 || fmt.Channels > 2) continue;
                    string wav = Path.Combine(work, "in.wav");
                    using (var fs = File.Create(wav))
                    using (var enc = new WavEncoder(fs, fmt, new WavEncoderSettings { SampleType = NativeSampleType.PcmSigned16 }, true))
                    {
                        enc.WriteInteger(samples);
                        enc.Finish();
                    }
                    string name = Path.GetFileNameWithoutExtension(ogg);
                    foreach (int level in levels)
                    {
                        var (ours, sec) = EncodeOurs(samples, fmt, 16, level);
                        string refOut = Path.Combine(work, "ref.flac");
                        double refSec = RunFlac(flac, level, wav, refOut, work);
                        Add("music", name, level, AudioBytes(ours), AudioBytes(File.ReadAllBytes(refOut)), sec, refSec, (double)(samples.Length / fmt.Channels) / fmt.SampleRate);
                    }
                    Console.Error.WriteLine("music " + name);
                }
        }

        var sb = new StringBuilder();
        sb.AppendLine("set\tfile\tlevel\tours_audio_bytes\tref_audio_bytes\tdelta\tours_sec\tref_sec_incl_process\taudio_sec");
        foreach (string r in rows) sb.AppendLine(r);
        File.WriteAllText(outPath, sb.ToString());
        Console.WriteLine("set\tlevel\tours_bytes\tref_bytes\tdelta\tours_x_realtime\tref_x_realtime(process incl.)");
        foreach (var ((set, level), t) in totals.OrderBy(k => k.Key.Set).ThenBy(k => k.Key.Level))
            Console.WriteLine($"{set}\t{level}\t{t.Ours}\t{t.Ref}\t{((double)t.Ours / t.Ref - 1):P3}\t{t.AudioSec / t.OursSec:F0}\t{t.AudioSec / t.RefSec:F0}");
        Directory.Delete(work, true);
        return 0;
    }

    /// <summary>Quick throughput probe on one decoded music file: encode speed in multiples of real time for a few settings.</summary>
    public static int Probe(string[] args)
    {
        string corpus = Environment.GetEnvironmentVariable("ONEAUDIO_CORPUS")!;
        var registry = new AudioCodecRegistry().AddOgg().AddVorbis();
        var (samples, fmt) = DecodeToPcm16(registry, Path.Combine(corpus, "vorbis-xiph-moog", "moog.ogg"), 30);
        double seconds = (double)(samples.Length / fmt.Channels) / fmt.SampleRate;
        Console.WriteLine($"{seconds:F1} s, {fmt.Channels} ch @ {fmt.SampleRate}");
        var variants = new (string, FlacEncoderSettings)[]
        {
            ("L0", new() { CompressionLevel = 0 }),
            ("L1", new() { CompressionLevel = 1 }),
            ("L2", new() { CompressionLevel = 2 }),
            ("L3", new() { CompressionLevel = 3 }),
            ("L5", new() { CompressionLevel = 5 }),
            ("L5 lpc0", new() { CompressionLevel = 5, MaxLpcOrder = 0 }),
            ("L8", new() { CompressionLevel = 8 }),
        };
        foreach (var (name, s) in variants.Where(v => args.Length < 2 || v.Item1 == args[1]))
        {
            EncodeOurs(samples, fmt, 16, s.CompressionLevel); // warm-up
            var sw = Stopwatch.StartNew();
            int reps = args.Length > 2 ? int.Parse(args[2]) : 3;
            for (int i = 0; i < reps; i++) Encode(samples, fmt, s);
            Console.WriteLine($"{name}: {seconds * reps / sw.Elapsed.TotalSeconds:F0}x real time");
        }
        return 0;
    }

    private static void Encode(int[] samples, AudioFormat fmt, FlacEncoderSettings s)
    {
        using var sink = new NullSink();
        using var enc = new FlacEncoder(sink, new AudioFormat(fmt.SampleRate, fmt.Channels), s with { BitsPerSample = 16 }, true);
        enc.WriteInteger(samples);
        enc.Finish();
    }

    private sealed class NullSink : Stream
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

    private static (int[] Samples, AudioFormat Format, int Bits) DecodeFlac(string path)
    {
        using var dec = FlacDecoder.Open(File.OpenRead(path), new AudioLimits { MaxSampleRate = 1_048_575 });
        int ch = dec.Format.Channels, shift = dec.Format.BitsPerSample - dec.BitsPerSample;
        var list = new List<int>();
        var buf = new int[4096 * ch];
        int n;
        while ((n = dec.ReadInteger(buf)) > 0)
            for (int i = 0; i < n * ch; i++) list.Add(buf[i] >> shift);
        return (list.ToArray(), dec.Format, dec.BitsPerSample);
    }

    private static (int[] Samples, AudioFormat Format) DecodeToPcm16(AudioCodecRegistry registry, string path, int maxSeconds)
    {
        using var dec = AudioDecoder.OpenFile(path, registry);
        int ch = dec.Format.Channels;
        var list = new List<int>();
        var buf = new float[4096 * ch];
        int n;
        long limit = (long)maxSeconds * dec.Format.SampleRate;
        while (list.Count / ch < limit && (n = dec.Read(buf)) > 0)
            for (int i = 0; i < n * ch; i++) list.Add((int)Math.Clamp(Math.Round(buf[i] * 32768.0), -32768, 32767));
        return (list.ToArray(), dec.Format);
    }

    private static (byte[] Bytes, double Seconds) EncodeOurs(int[] samples, AudioFormat fmt, int bits, int level)
    {
        var settings = new FlacEncoderSettings { CompressionLevel = level, BitsPerSample = bits, QlpPrecision = int.Parse(Environment.GetEnvironmentVariable("ONEAUDIO_QLP") ?? "0"), MaxLpcOrder = int.Parse(Environment.GetEnvironmentVariable("ONEAUDIO_LPC") ?? "-1") };
        var sw = Stopwatch.StartNew();
        using var ms = new MemoryStream();
        using (var enc = new FlacEncoder(ms, new AudioFormat(fmt.SampleRate, fmt.Channels), settings, true))
        {
            enc.WriteInteger(samples);
            enc.Finish();
        }
        return (ms.ToArray(), sw.Elapsed.TotalSeconds);
    }

    private static double RunFlac(string flac, int level, string input, string output, string work)
    {
        var psi = new ProcessStartInfo(flac) { RedirectStandardError = true, RedirectStandardOutput = true, UseShellExecute = false, WorkingDirectory = work };
        foreach (string a in new[] { "-" + level, "-s", "-f", "--no-padding", "-o", output, input }) psi.ArgumentList.Add(a);
        var sw = Stopwatch.StartNew();
        using var p = Process.Start(psi)!;
        p.StandardError.ReadToEnd();
        p.WaitForExit();
        if (p.ExitCode != 0) throw new InvalidOperationException("flac failed on " + input);
        return sw.Elapsed.TotalSeconds;
    }

    /// <summary>Size of the frames after the metadata blocks.</summary>
    private static long AudioBytes(byte[] flac)
    {
        int pos = 4;
        while (true)
        {
            bool last = (flac[pos] & 0x80) != 0;
            int len = (flac[pos + 1] << 16) | (flac[pos + 2] << 8) | flac[pos + 3];
            pos += 4 + len;
            if (last) break;
        }
        return flac.Length - pos;
    }
}
