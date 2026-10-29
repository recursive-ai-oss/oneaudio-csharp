using OneAudio;
using OneAudio.Aac;
using OneAudio.Mp4;
using SharpFuzz;

// Fuzz target for the AAC-LC decoder. The input is: one byte N (the AudioSpecificConfig length, at most 16), the N config
// bytes, then access units each preceded by a 16-bit little-endian length. A config the decoder rejects ends the input;
// units it rejects with a typed error are skipped (or concealed, when the first config byte's top bit asks for it).
// A seek back into the stream is made now and then. Anything other than a typed library error escaping is a finding.
// Under libFuzzer (after SharpFuzz instrumentation of OneAudio.dll) run with no arguments; with file arguments it replays
// them once. "--seed <input.m4a|aac> <output>" turns a real file into an input in this format.
if (args.Length == 3 && args[0] == "--seed")
{
    MakeSeed(args[1], args[2]);
    return;
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

static void Target(byte[] input)
{
    if (input.Length < 2) return;
    int n = Math.Min(input[0] & 0x0F, 16);
    bool conceal = (input[0] & 0x80) != 0;
    if (input.Length < 1 + n) return;
    byte[] config = input[1..(1 + n)];
    var units = new List<byte[]>();
    int pos = 1 + n;
    while (pos + 2 <= input.Length && units.Count < 64)
    {
        int len = input[pos] | (input[pos + 1] << 8);
        pos += 2;
        len = Math.Min(len, input.Length - pos);
        units.Add(input[pos..(pos + len)]);
        pos += len;
    }
    try
    {
        using var dec = new AacDecoder(new UnitSource(units, config)) { ConcealErrors = conceal };
        var pcm = new float[1500 * dec.Format.Channels];
        for (int i = 0; i < 200; i++)
        {
            int got = dec.Read(pcm);
            if (got == 0) break;
            foreach (float v in pcm.AsSpan(0, got * dec.Format.Channels)) if (float.IsNaN(v)) throw new InvalidOperationException("NaN in output");
        }
        if (dec.CanSeek && dec.LengthFrames > 0)
        {
            dec.Seek(dec.LengthFrames / 3);
            dec.Read(pcm);
            dec.Seek(0);
            dec.Read(pcm);
        }
    }
    catch (OneAudioException) { } // typed rejection of bad or unsupported data
}

static void MakeSeed(string input, string output)
{
    using var fs = File.OpenRead(input);
    IPacketSource src = input.EndsWith(".aac", StringComparison.OrdinalIgnoreCase) ? AdtsPacketSource.Open(fs) : Mp4PacketSource.Open(fs);
    var config = src.CodecConfig.ToArray();
    var o = new List<byte> { (byte)config.Length };
    o.AddRange(config);
    for (int i = 0; i < 40 && src.TryReadPacket(out var p); i++)
    {
        o.Add((byte)p.Data.Length);
        o.Add((byte)(p.Data.Length >> 8));
        o.AddRange(p.Data.ToArray());
    }
    File.WriteAllBytes(output, o.ToArray());
}

sealed class UnitSource(List<byte[]> units, byte[] config) : IPacketSource
{
    private int _next;
    public CodecId Codec => CodecId.Aac;
    public long LengthFrames => units.Count * 1024L;
    public bool CanSeek => true;
    public ReadOnlyMemory<byte> CodecConfig => config;
    public bool TryReadPacket(out PacketView packet)
    {
        if (_next >= units.Count) { packet = default; return false; }
        packet = new PacketView(units[_next++], -1, false);
        return true;
    }
    public void SeekToFrame(long frame, out long startFrame)
    {
        _next = (int)Math.Clamp(frame / 1024, 0, units.Count);
        startFrame = _next * 1024L;
    }
    public void Dispose() { }
}
