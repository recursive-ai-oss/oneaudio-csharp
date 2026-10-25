using OneAudio;
using SharpFuzz;

// Fuzz target for the MP3 frame finder, tag handling, side-information parser, Huffman decoder and filterbank.
// Under libFuzzer (after SharpFuzz instrumentation of OneAudio.dll) run with no arguments; with file arguments it
// replays those inputs once, which is how crashes are reproduced.
static void Target(Stream input)
{
    var registry = new AudioCodecRegistry().AddMp3();
    var limits = new AudioLimits { MaxChannels = 8, MaxMetadataBytes = 1 << 16 };
    try
    {
        using var decoder = AudioDecoder.Open(input, registry, limits, leaveOpen: true);
        var pcm = new float[2 * 1152];
        for (int i = 0; i < 64; i++)
        {
            if (decoder.Read(pcm) == 0) break;
        }
        if (decoder.CanSeek)
        {
            long length = decoder.LengthFrames;
            long[] targets = [0, 1, length / 3, length / 2, length - 1, length];
            foreach (long t in targets)
            {
                if (t < 0 || t > length) continue;
                decoder.Seek(t);
                decoder.Read(pcm);
            }
        }
    }
    catch (OneAudioException) { } // expected rejection of bad input; anything else is a finding
}

if (args.Length == 0)
{
    Fuzzer.LibFuzzer.Run(span =>
    {
        using var ms = new MemoryStream(span.ToArray());
        Target(ms);
    });
}
else
{
    foreach (var path in args)
    {
        using var fs = File.OpenRead(path);
        Target(fs);
    }
    Console.WriteLine($"replayed {args.Length} input(s) without a crash");
}
