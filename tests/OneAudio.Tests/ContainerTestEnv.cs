using System.Diagnostics;
using System.Globalization;
using System.Security.Cryptography;
using OneAudio.Aac;
using OneAudio.Amr;
using OneAudio.Mp4;
using Xunit;

namespace OneAudio.Tests;

/// <summary>Test files for the MP4, ADTS and AMR readers, and ffprobe as the packet oracle (black-box, tests only).</summary>
internal static class ContainerTestEnv
{
    public static readonly AudioCodecRegistry Registry = new AudioCodecRegistry().AddMp4().AddAdts().AddAmr().AddAac();

    public static string TestData(string name) => Path.Combine(AppContext.BaseDirectory, "testdata", "containers", name);

    public static string? CorpusDir => Environment.GetEnvironmentVariable("ONEAUDIO_CORPUS");

    /// <summary>A fetched ISO conformance MP4 (tools/fetch-corpus.sh); the test is skipped when it is missing.</summary>
    public static string Conformance(string name)
    {
        var p = CorpusDir is null ? null : Path.Combine(CorpusDir, "aac-iso-" + name, name + ".mp4");
        Assert.SkipUnless(p is not null && File.Exists(p), "ISO conformance MP4s not fetched (run tools/fetch-corpus.sh, set ONEAUDIO_CORPUS).");
        return p!;
    }

    public static readonly string[] ConformanceNames = ["aabper01", "Abv3_Aab01", "Abv3_Acc01", "Abv3_Ws03", "Abv3_Acc04", "Abv3_T3da01", "ac12", "al14_08", "al960_sbr_e_48_1"];

    public static TheoryData<string> ConformanceData()
    {
        var d = new TheoryData<string>();
        foreach (var n in ConformanceNames) d.Add(n);
        return d;
    }

    public static string? Ffprobe
    {
        get
        {
            if (Environment.GetEnvironmentVariable("ONEAUDIO_FFPROBE") is { Length: > 0 } e && File.Exists(e)) return e;
            foreach (var dir in (Environment.GetEnvironmentVariable("PATH") ?? "").Split(Path.PathSeparator))
            {
                var p = Path.Combine(dir, "ffprobe");
                if (File.Exists(p)) return p;
            }
            return null;
        }
    }

    public static string? Ffmpeg => Ffprobe is { } p ? Path.Combine(Path.GetDirectoryName(p)!, "ffmpeg") : null;

    public sealed record Probed(long Pts, long Dts, long Duration, int Size, string Hash);

    public static string Run(string exe, params string[] args)
    {
        var psi = new ProcessStartInfo(exe) { RedirectStandardOutput = true, RedirectStandardError = true };
        foreach (var a in args) psi.ArgumentList.Add(a);
        using var p = Process.Start(psi)!;
        var err = p.StandardError.ReadToEndAsync();
        string output = p.StandardOutput.ReadToEnd();
        p.WaitForExit();
        err.Wait();
        return output;
    }

    /// <summary>Packets of an audio stream (<paramref name="stream"/> counts audio streams) as ffprobe reports them.</summary>
    public static List<Probed> Probe(string path, int stream = 0)
    {
        Assert.SkipUnless(Ffprobe is not null, "ffprobe not found (set ONEAUDIO_FFPROBE or put it on PATH).");
        string csv = Run(Ffprobe!, "-v", "error", "-select_streams", "a:" + stream.ToString(CultureInfo.InvariantCulture), "-show_packets",
            "-show_data_hash", "sha256", "-show_entries", "packet=pts,dts,duration,size,data_hash", "-of", "csv=p=0", path);
        var list = new List<Probed>();
        foreach (var line in csv.Split('\n', StringSplitOptions.RemoveEmptyEntries))
        {
            var f = line.TrimEnd('\r').Split(',');
            if (f.Length < 5) continue;
            long Parse(string s) => long.TryParse(s, CultureInfo.InvariantCulture, out var v) ? v : long.MinValue;
            list.Add(new Probed(Parse(f[0]), Parse(f[1]), Parse(f[2]), (int)Parse(f[3]), f[4].Replace("SHA256:", "", StringComparison.Ordinal)));
        }
        return list;
    }

    /// <summary>Copies the stream of an ADTS file into an MP4 (ffmpeg strips the ADTS headers), whose packets are then the raw payloads.</summary>
    public static string RemuxToMp4(string path)
    {
        Assert.SkipUnless(Ffmpeg is not null && File.Exists(Ffmpeg), "ffmpeg not found.");
        string dir = Environment.GetEnvironmentVariable("TMPDIR") is { Length: > 0 } t ? t : Path.GetTempPath();
        string output = Path.Combine(dir, "oneaudio-remux-" + Path.GetFileNameWithoutExtension(path) + ".mp4");
        Run(Ffmpeg!, "-v", "error", "-y", "-i", path, "-c", "copy", "-f", "mp4", output);
        return output;
    }

    public sealed record Packet(byte[] Data, long Timestamp, long Duration, long EndFrame)
    {
        public string Hash => Convert.ToHexStringLower(SHA256.HashData(Data));
    }

    public static List<Packet> ReadAll(IPacketSource src)
    {
        var list = new List<Packet>();
        while (src.TryReadPacket(out var p)) list.Add(new Packet(p.Data.ToArray(), p.Timestamp, p.Duration, p.EndFrame));
        return list;
    }

    public static Mp4PacketSource OpenMp4(string path, AudioLimits? limits = null) => Mp4PacketSource.Open(File.OpenRead(path), limits);

    /// <summary>A stream that reads like a pipe: no seeking, no length.</summary>
    public sealed class ForwardOnly(byte[] data) : Stream
    {
        private readonly MemoryStream _ms = new(data);
        public override bool CanRead => true;
        public override bool CanSeek => false;
        public override bool CanWrite => false;
        public override long Length => throw new NotSupportedException();
        public override long Position { get => throw new NotSupportedException(); set => throw new NotSupportedException(); }
        public override void Flush() { }
        public override int Read(byte[] buffer, int offset, int count) => _ms.Read(buffer, offset, count);
        public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
        public override void SetLength(long value) => throw new NotSupportedException();
        public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();
    }
}
