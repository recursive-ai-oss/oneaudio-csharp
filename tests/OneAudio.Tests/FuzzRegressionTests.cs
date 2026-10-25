using Xunit;
using OneAudio.Vorbis;

namespace OneAudio.Tests;

/// <summary>
/// Replays every input that once crashed or hung a decoder under the fuzzer (testdata/fuzz/&lt;target&gt;/). Each must now
/// either decode or be rejected with the library's own exception types; nothing else may escape.
/// </summary>
public class FuzzRegressionTests
{
    private static readonly AudioCodecRegistry Registry = new AudioCodecRegistry().AddWav().AddOgg().AddVorbis();

    public static TheoryData<string> Inputs()
    {
        var d = new TheoryData<string>();
        string root = Path.Combine(AppContext.BaseDirectory, "testdata", "fuzz");
        if (Directory.Exists(root))
            foreach (var f in Directory.EnumerateFiles(root, "*", SearchOption.AllDirectories).Order(StringComparer.Ordinal))
                d.Add(Path.GetRelativePath(root, f));
        return d;
    }

    [Theory]
    [MemberData(nameof(Inputs))]
    public void Replay_OnlyTypedExceptionsEscape(string relative)
    {
        byte[] data = File.ReadAllBytes(Path.Combine(AppContext.BaseDirectory, "testdata", "fuzz", relative));
        var limits = new AudioLimits { MaxChannels = 8, MaxSetupBytes = 4 << 20, MaxPacketBytes = 1 << 20, MaxContainerElements = 5000 };
        try
        {
            using var decoder = AudioDecoder.Open(new MemoryStream(data), Registry, limits);
            var pcm = new float[4096 * decoder.Format.Channels];
            for (int i = 0; i < 48 && decoder.Read(pcm) > 0; i++) { }
            if (decoder.CanSeek)
            {
                long len = Math.Max(decoder.LengthFrames, 1);
                foreach (long f in new[] { 0, len / 2, len - 1, len })
                {
                    if (f > decoder.LengthFrames) continue;
                    decoder.Seek(f);
                    decoder.Read(pcm);
                }
                if (decoder is VorbisDecoder v)
                    for (int link = 1; link < Math.Min(v.LinkCount, 4); link++)
                        if (v.GetLinkFormat(link) is not null) { v.SelectRun(link); decoder.Read(pcm); }
            }
        }
        catch (OneAudioException) { }
    }
}
