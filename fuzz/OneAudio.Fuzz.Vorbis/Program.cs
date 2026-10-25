using OneAudio;
using OneAudio.Vorbis;
using SharpFuzz;

// Fuzz target for Ogg Vorbis: setup-header parsing (codebooks, floors, residues, mappings, modes), audio packet
// decode, seeking and chain handling. Under libFuzzer (after SharpFuzz instrumentation of OneAudio.dll) run with no
// arguments; with file arguments it replays those inputs once, which is how crashes are reproduced.
static void Target(byte[] data)
{
    var registry = new AudioCodecRegistry().AddOgg().AddVorbis();
    var limits = new AudioLimits { MaxChannels = 8, MaxSetupBytes = 4 << 20, MaxPacketBytes = 1 << 20, MaxContainerElements = 5000 };
    try
    {
        using var decoder = AudioDecoder.Open(new MemoryStream(data), registry, limits);
        var pcm = new float[4096 * decoder.Format.Channels];
        for (int i = 0; i < 48; i++)
        {
            if (decoder.Read(pcm) == 0) break;
        }
        if (decoder.CanSeek)
        {
            long len = Math.Max(decoder.LengthFrames, 1);
            foreach (long f in new[] { 0, len / 2, len - 1, len })
            {
                if (f > decoder.LengthFrames) continue;
                decoder.Seek(f);
                decoder.Read(pcm);
            }
            var v = (VorbisDecoder)decoder;
            for (int link = 1; link < Math.Min(v.LinkCount, 4); link++)
            {
                if (v.GetLinkFormat(link) is null) continue;
                v.SelectRun(link);
                pcm = new float[4096 * decoder.Format.Channels];
                decoder.Read(pcm);
            }
        }
    }
    catch (OneAudioException) { } // expected rejection of bad input; anything else is a finding
}

if (args.Length == 0)
{
    Fuzzer.LibFuzzer.Run(span => Target(span.ToArray()));
}
else
{
    foreach (var path in args) Target(File.ReadAllBytes(path));
    Console.WriteLine($"replayed {args.Length} input(s) without a crash");
}
