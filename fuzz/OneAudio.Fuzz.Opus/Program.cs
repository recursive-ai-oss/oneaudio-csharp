using OneAudio;
using OneAudio.Opus;
using SharpFuzz;

// Fuzz target for Opus. The first byte picks the mode: odd means the rest is an Ogg Opus file read through the
// container and the codec (headers, multistream mapping, seeking); even means the rest is a series of raw packets
// (a one-byte length, then that many bytes; a zero length is a lost packet) fed to the packet decoder, whose
// channel count and rate come from bits of the first byte. Under libFuzzer (after SharpFuzz instrumentation of
// OneAudio.dll) run with no arguments; with file arguments it replays those inputs once.
static void Target(byte[] data)
{
    if (data.Length < 2) return;
    try
    {
        if ((data[0] & 1) != 0) FuzzFile(data.AsSpan(1).ToArray());
        else FuzzPackets(data);
    }
    catch (OneAudioException) { } // expected rejection of bad input; anything else is a finding
}

static void FuzzPackets(byte[] data)
{
    int channels = ((data[0] >> 1) & 1) + 1;
    int rate = ((data[0] >> 2) & 1) != 0 ? 24000 : 48000;
    var dec = new OpusDecoder(rate, channels) { PhaseInversionDisabled = (data[0] & 8) != 0 };
    var pcm = new float[5760 * channels];
    int pos = 1;
    while (pos < data.Length)
    {
        int len = data[pos++];
        len = Math.Min(len, data.Length - pos);
        var packet = data.AsSpan(pos, len);
        pos += len;
        if (len == 0) dec.DecodePacketLoss(pcm.AsSpan(0, 960 * channels));
        else dec.Decode(packet, pcm);
    }
}

static void FuzzFile(byte[] data)
{
    var registry = new AudioCodecRegistry().AddOgg().AddOpus();
    var limits = new AudioLimits { MaxChannels = 8, MaxPacketBytes = 1 << 20, MaxContainerElements = 5000 };
    using var decoder = AudioDecoder.Open(new MemoryStream(data), registry, limits);
    var pcm = new float[4096 * decoder.Format.Channels];
    for (int i = 0; i < 48; i++)
    {
        if (decoder.Read(pcm) == 0) break;
    }
    if (decoder.CanSeek && decoder.LengthFrames >= 0)
    {
        long len = decoder.LengthFrames;
        foreach (long f in new[] { 0, len / 2, len })
        {
            decoder.Seek(f);
            decoder.Read(pcm);
        }
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
