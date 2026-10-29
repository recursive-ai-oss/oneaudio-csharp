using OneAudio;
using OneAudio.Aac;
using SharpFuzz;

// Fuzz target for the Adts reader: header parsing, frame framing, resynchronisation limits, the seek index
// and seeking. The first input byte picks a seekable or forward-only stream; the rest is the file. Under libFuzzer
// (after SharpFuzz instrumentation of OneAudio.dll) run with no arguments; with file arguments it replays them once.
static void Walk(IPacketSource src, int budget)
{
    for (int i = 0; i < budget && src.TryReadPacket(out var p); i++)
    {
        byte x = 0;
        foreach (byte b in p.Data) x ^= b; // touch every byte so out-of-range slices would surface
        _ = x;
    }
}

static void Target(byte[] input)
{
    if (input.Length == 0) return;
    bool forward = (input[0] & 1) != 0;
    byte[] data = input[1..];
    var limits = new AudioLimits { MaxPacketBytes = 1 << 20, MaxContainerElements = 5000, MaxSamples = 1 << 20 };
    try
    {
        Stream s = forward ? new ForwardOnly(data) : new MemoryStream(data);
        using var src = AdtsPacketSource.Open(s, limits);
        _ = (src.Codec, src.LengthFrames, src.SampleRate, src.Channels, src.TimeScale, src.CodecConfig.Length);
        Walk(src, 5000);
        if (!forward)
        {
            long len = Math.Max(src.LengthFrames, 1);
            foreach (long f in new[] { 0L, len / 3, len / 2, len - 1, len, len * 2 })
            {
                src.SeekToFrame(f, out _);
                Walk(src, 20);
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

/// <summary>A readable stream that cannot seek, to exercise the streaming path.</summary>
sealed class ForwardOnly(byte[] data) : Stream
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
    protected override void Dispose(bool disposing) { if (disposing) _ms.Dispose(); base.Dispose(disposing); }
}
