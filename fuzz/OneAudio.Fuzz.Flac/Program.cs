using OneAudio;
using OneAudio.Flac;
using SharpFuzz;

// Fuzz target for the FLAC decoder: metadata parsing, frame decoding (all subframe and residual types), seeking by
// SEEKTABLE and by frame-sync search, and Ogg-carried FLAC (the format is sniffed, so native and Ogg inputs both work).
// The first input byte picks the stream type: bit 1 set reads through a stream that cannot seek. Under libFuzzer (after SharpFuzz
// instrumentation of OneAudio.dll) run with no arguments; with file arguments it replays them once.
static void Drain(IAudioDecoder d, int budgetReads)
{
    var ints = new int[d.Format.Channels * 512];
    var floats = new float[d.Format.Channels * 512];
    for (int i = 0; i < budgetReads; i++)
    {
        int n = (i & 1) == 0 ? d.ReadInteger(ints) : d.Read(floats);
        if (n == 0) break;
    }
}

static void Target(byte[] data)
{
    if (data.Length < 2) return;
    int mode = data[0];
    var rest = new MemoryStream(data, 1, data.Length - 1, writable: false);
    var registry = new AudioCodecRegistry().AddOgg().AddFlac();
    var limits = new AudioLimits { MaxPacketBytes = 1 << 20, MaxMetadataBytes = 1 << 16, MaxContainerElements = 5000 };
    Stream source = (mode & 2) != 0 ? new ForwardOnly(rest) : rest;
    try
    {
        using var d = AudioDecoder.Open(source, registry, limits);
        _ = (d.Format, d.LengthFrames);
        if (d is FlacDecoder f) { _ = (f.Vendor, f.Comments.Count, f.Md5.Length); }
        Drain(d, 400);
        if (d.CanSeek)
        {
            long len = Math.Max(d.LengthFrames, 1000);
            foreach (long t in new[] { 0, len / 3, len / 2, len - 1, 1, len / 3 })
            {
                try { d.Seek(Math.Min(t, d.LengthFrames >= 0 ? d.LengthFrames : t)); }
                catch (ArgumentOutOfRangeException) { }
                Drain(d, 4);
            }
            if (d is FlacDecoder g && g.LengthFrames > 0 && g.LengthFrames < 20000) _ = g.VerifyMd5();
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

internal sealed class ForwardOnly(Stream inner) : Stream
{
    public override bool CanRead => true;
    public override bool CanSeek => false;
    public override bool CanWrite => false;
    public override long Length => throw new NotSupportedException();
    public override long Position { get => throw new NotSupportedException(); set => throw new NotSupportedException(); }
    public override void Flush() { }
    public override int Read(byte[] buffer, int offset, int count) => inner.Read(buffer, offset, count);
    public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
    public override void SetLength(long value) => throw new NotSupportedException();
    public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();
}
