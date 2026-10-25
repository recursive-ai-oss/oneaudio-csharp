using OneAudio;
using OneAudio.Ogg;
using SharpFuzz;

// Fuzz target for the Ogg container: page framing and checksums, packet assembly across pages, chain discovery,
// granule-based seeking and the forward-only path. Under libFuzzer (after SharpFuzz instrumentation of OneAudio.dll)
// run with no arguments; with file arguments it replays those inputs once, which is how crashes are reproduced.
static void Walk(OggPacketSource src, int budget)
{
    for (int i = 0; i < budget && src.TryReadPacket(out var p); i++)
    {
        // Touch every byte so out-of-range slices would surface.
        byte x = 0;
        foreach (byte b in p.Data) x ^= b;
        _ = x;
    }
}

static void Target(byte[] data)
{
    var limits = new AudioLimits { MaxPacketBytes = 1 << 20, MaxContainerElements = 5000 };
    try
    {
        using var src = OggPacketSource.Open(new MemoryStream(data), limits);
        _ = (src.Codec, src.LengthFrames, src.LinkCount);
        for (int i = 0; i < src.LinkCount; i++) _ = src.GetLink(i);
        Walk(src, 2000);
        long len = Math.Max(src.LengthFrames, 1);
        foreach (long f in new[] { 0, len / 3, len / 2, len - 1, len, len * 2 })
        {
            src.SeekToFrame(f, out _);
            Walk(src, 20);
        }
        if (src.LinkCount > 1) { src.SeekToLink(src.LinkCount - 1); Walk(src, 20); }
    }
    catch (OneAudioException) { } // expected rejection of bad input; anything else is a finding

    try
    {
        using var src = OggPacketSource.Open(new ForwardOnly(data), limits);
        Walk(src, 2000);
    }
    catch (OneAudioException) { }
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
