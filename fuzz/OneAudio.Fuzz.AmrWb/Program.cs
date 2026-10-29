using OneAudio;
using SharpFuzz;

// Fuzz target for the AMR wideband speech decoder. The input is a run of storage-format frames (a table-of-contents byte,
// then the payload that frame type needs; a short tail is zero-padded), optionally after the file magic. Every frame
// goes through the decoder, with a lost-frame concealment call or a reset now and then. Frames the decoder rejects with
// a typed error are skipped; anything else escaping is a finding. Under libFuzzer (after SharpFuzz instrumentation of
// OneAudio.dll) run with no arguments; with file arguments it replays them once.
static void Target(byte[] input)
{
    var dec = new AmrWbDecoder();
    var pcm = new short[320];
    var fl = new float[320];
    sbyte[] sizes = new sbyte[] { 17, 23, 32, 36, 40, 46, 50, 58, 60, 5, -1, -1, -1, -1, 0, 0 };
    ReadOnlySpan<byte> data = input;
    if (data.StartsWith("#!AMR"u8))
    {
        int nl = data.IndexOf((byte)10);
        data = nl < 0 ? default : data[(nl + 1)..];
    }
    int pos = 0, frames = 0;
    while (pos < data.Length && frames++ < 2000)
    {
        byte toc = data[pos];
        int size = sizes[(toc >> 3) & 15];
        var frame = new byte[1 + Math.Max(size, 0)];
        int avail = Math.Min(frame.Length, data.Length - pos);
        data.Slice(pos, avail).CopyTo(frame);
        pos += frame.Length;
        try
        {
            if ((toc & 0x80) != 0 && (frames & 3) == 0) dec.Decode(frame, fl);
            else dec.Decode(frame, pcm);
            if ((toc & 3) == 3) dec.DecodeLost(pcm);
            if ((toc & 3) == 2) dec.Reset();
        }
        catch (OneAudioException) { } // expected rejection of a reserved frame type
    }
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
