using OneAudio;
using OneAudio.Flac;

/// <summary>FLAC under NativeAOT: native file with MD5 check and a seek, and the same audio through Ogg FLAC.</summary>
internal static class FlacSmoke
{
    public static int Run()
    {
        var registry = new AudioCodecRegistry().AddOgg().AddFlac();
        using var native = (FlacDecoder)AudioDecoder.Open(Resource("smoke.flac"), registry);
        if (native.VerifyMd5() != true) return Fail("md5");
        var all = ReadAll(native);
        if (all.Length != native.LengthFrames * native.Format.Channels) return Fail("length");

        long target = native.LengthFrames / 2;
        native.Seek(target);
        var window = new int[2 * 64];
        if (native.ReadInteger(window) != 64 || !window.AsSpan().SequenceEqual(all.AsSpan((int)target * 2, 2 * 64))) return Fail("seek");

        using var ogg = AudioDecoder.Open(Resource("smoke.oga"), registry);
        if (!ReadAll(ogg).AsSpan().SequenceEqual(all)) return Fail("ogg flac differs");
        Console.WriteLine($"ok: flac {all.Length / 2} frames, md5 verified, seek and Ogg FLAC agree");
        return 0;
    }

    private static MemoryStream Resource(string name)
    {
        using var s = typeof(FlacSmoke).Assembly.GetManifestResourceStream(name)!;
        var m = new MemoryStream();
        s.CopyTo(m);
        m.Position = 0;
        return m;
    }

    private static int[] ReadAll(IAudioDecoder d)
    {
        var all = new List<int>();
        var buf = new int[d.Format.Channels * 1000];
        int n;
        while ((n = d.ReadInteger(buf)) > 0) all.AddRange(buf.AsSpan(0, n * d.Format.Channels).ToArray());
        return [.. all];
    }

    private static int Fail(string why)
    {
        Console.Error.WriteLine("FAIL: flac " + why);
        return 1;
    }
}
