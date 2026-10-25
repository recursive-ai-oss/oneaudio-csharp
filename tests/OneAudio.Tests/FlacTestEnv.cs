using System.Diagnostics;
using System.Security.Cryptography;
using OneAudio.Flac;
using Xunit;

namespace OneAudio.Tests;

/// <summary>Locates the fetched FLAC test files and the reference <c>flac</c> binary (see tools/fetch-corpus.sh).</summary>
internal static class FlacTestEnv
{
    public static readonly AudioCodecRegistry Registry = new AudioCodecRegistry().AddOgg().AddFlac();

    public static string? CorpusDir => Environment.GetEnvironmentVariable("ONEAUDIO_CORPUS") is { } c ? Path.Combine(c, "flac-cellar") : null;

    /// <summary>Path of the reference <c>flac</c> executable: ONEAUDIO_FLAC, else the shared tools cache next to the corpus.</summary>
    public static string? FlacBinary
    {
        get
        {
            if (Environment.GetEnvironmentVariable("ONEAUDIO_FLAC") is { Length: > 0 } e && File.Exists(e)) return e;
            if (Environment.GetEnvironmentVariable("ONEAUDIO_CORPUS") is { } c)
            {
                foreach (var rel in new[] { "tools/bin/flac", ".oneaudio-tools/bin/flac" })
                {
                    var p = Path.GetFullPath(Path.Combine(c, "..", rel));
                    if (File.Exists(p)) return p;
                }
            }
            return null;
        }
    }

    public static TheoryData<string> Group(string group)
    {
        var d = new TheoryData<string>();
        var dir = CorpusDir is null ? null : Path.Combine(CorpusDir, group);
        if (dir is not null && Directory.Exists(dir))
            foreach (var f in Directory.GetFiles(dir, "*.flac").OrderBy(x => x, StringComparer.Ordinal)) d.Add(Path.GetRelativePath(CorpusDir!, f));
        else d.Add("(corpus not fetched)");
        return d;
    }

    public static string Path_(string rel)
    {
        Assert.SkipUnless(CorpusDir is not null && File.Exists(Path.Combine(CorpusDir, rel)), "FLAC test files not fetched (run tools/fetch-corpus.sh, set ONEAUDIO_CORPUS).");
        return Path.Combine(CorpusDir!, rel);
    }

    /// <summary>SHA-256 of the integers a decoder delivers, packed little-endian at its container width.</summary>
    public static (string Hash, long Frames) HashDecode(IAudioDecoder dec, int chunkFrames = 4096)
    {
        using var h = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
        int ch = dec.Format.Channels;
        int bytes = dec.Format.BitsPerSample / 8;
        var ints = new int[chunkFrames * ch];
        var raw = new byte[ints.Length * bytes];
        long frames = 0;
        while (true)
        {
            int n = dec.ReadInteger(ints);
            if (n == 0) break;
            int o = 0;
            for (int i = 0; i < n * ch; i++)
                for (int b = 0; b < bytes; b++) raw[o++] = (byte)(ints[i] >> (8 * b));
            if (bytes == 1) for (int i = 0; i < o; i++) raw[i] ^= 0x80; // WAV stores 8-bit unsigned
            h.AppendData(raw, 0, o);
            frames += n;
        }
        return (Convert.ToHexString(h.GetHashAndReset()), frames);
    }

    /// <summary>SHA-256 of <c>flac -d</c> raw little-endian signed output for a file.</summary>
    public static string ReferenceHash(string path)
    {
        var psi = new ProcessStartInfo(FlacBinary!)
        {
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
        };
        foreach (var a in new[] { "-d", "-c", "-s", path }) psi.ArgumentList.Add(a);
        using var p = Process.Start(psi)!;
        var err = p.StandardError.ReadToEndAsync();
        using var h = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
        var stdout = p.StandardOutput.BaseStream;
        // WAV output (raw output refuses depths that are not 8/16/24/32): skip RIFF chunks up to the sample data.
        uint dataSize;
        var head = new byte[12];
        stdout.ReadExactly(head);
        while (true)
        {
            var ck = new byte[8];
            stdout.ReadExactly(ck);
            uint size = BitConverter.ToUInt32(ck, 4);
            if (ck[0] == (byte)'d' && ck[1] == (byte)'a' && ck[2] == (byte)'t' && ck[3] == (byte)'a') { dataSize = size; break; }
            stdout.ReadExactly(new byte[size + (size & 1)]);
        }
        var buf = new byte[1 << 16];
        int n;
        // Unknown length (stream without a sample count) is written as 0 or 0xFFFFFFFF: hash to the end.
        long left = dataSize is 0 or uint.MaxValue ? long.MaxValue : dataSize;
        while (left > 0 && (n = stdout.Read(buf, 0, (int)Math.Min(buf.Length, left))) > 0)
        {
            h.AppendData(buf, 0, n);
            left -= n;
        }
        while (stdout.Read(buf) > 0) { } // padding byte or anything after the data
        p.WaitForExit();
        if (p.ExitCode != 0) throw new InvalidOperationException("flac failed: " + err.Result);
        return Convert.ToHexString(h.GetHashAndReset());
    }
}
