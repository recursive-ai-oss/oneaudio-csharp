using OneAudio;
using SharpFuzz;

// Fuzz target for the WAV parser and decoder. Under libFuzzer (after SharpFuzz instrumentation of OneAudio.dll)
// run with no arguments; with file arguments it replays those inputs once, which is how crashes are reproduced.
static void Target(Stream input)
{
    var registry = new AudioCodecRegistry().AddWav();
    var limits = new AudioLimits { MaxChannels = 8 };
    try
    {
        using var decoder = AudioDecoder.Open(input, registry, limits, leaveOpen: true);
        var pcm = new float[1024];
        var ints = new int[1024];
        for (int i = 0; i < 64; i++)
        {
            int n = decoder.Read(pcm);
            if (decoder.HasNativeIntegers && decoder.CanSeek) { decoder.Seek(decoder.LengthFrames / 2); decoder.ReadInteger(ints); }
            if (n == 0) break;
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
