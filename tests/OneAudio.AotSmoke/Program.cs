using OneAudio;

// NativeAOT smoke test: build a WAV in memory, open it through the format-detecting entry point, decode it,
// then play it through the mixer's render contract. Exits non-zero on any mismatch.

const int Rate = 48_000;
const int Frames = 4800;

var pcm = new byte[Frames * 2];
for (int i = 0; i < Frames; i++)
{
    short s = (short)(MathF.Sin(2 * MathF.PI * 440 * i / Rate) * 16000);
    pcm[2 * i] = (byte)s;
    pcm[2 * i + 1] = (byte)(s >> 8);
}

var wav = new MemoryStream();
using (var w = new BinaryWriter(wav, System.Text.Encoding.ASCII, leaveOpen: true))
{
    w.Write("RIFF"u8); w.Write(36 + pcm.Length); w.Write("WAVEfmt "u8);
    w.Write(16); w.Write((short)1); w.Write((short)1); w.Write(Rate); w.Write(Rate * 2); w.Write((short)2); w.Write((short)16);
    w.Write("data"u8); w.Write(pcm.Length); w.Write(pcm);
}
wav.Position = 0;

var registry = new AudioCodecRegistry().AddWav();
using var decoder = AudioDecoder.Open(wav, registry);
if (decoder.Format.SampleRate != Rate || decoder.Format.Channels != 1 || decoder.LengthFrames != Frames)
    return Fail("unexpected format");

var ints = new int[Frames];
if (decoder.ReadInteger(ints) != Frames || ints[0] != 0) return Fail("integer read");
decoder.Seek(0);

var clip = AudioClip.FromDecoder(decoder);
var mixer = new AudioMixer(new MixerOptions { SampleRate = Rate, Channels = 2 });
var voice = mixer.Play(new PlayParams(clip) { Gain = 0.5f, Pan = -1f });
if (!voice.IsValid) return Fail("play");

var block = new float[512 * 2];
float peakL = 0, peakR = 0;
for (int pass = 0; pass < 12; pass++)
{
    mixer.Render(block);
    for (int i = 0; i < block.Length; i += 2)
    {
        peakL = MathF.Max(peakL, MathF.Abs(block[i]));
        peakR = MathF.Max(peakR, MathF.Abs(block[i + 1]));
    }
}

if (peakL < 0.2f || peakR > 1e-6f) return Fail($"mix peaks L={peakL} R={peakR}");
if (!mixer.TryGetEvent(out var ev) || ev.Kind != MixerEventKind.VoiceEnded) return Fail("expected VoiceEnded");
foreach (var q in new[] { ResamplerQuality.Linear, ResamplerQuality.Polyphase, ResamplerQuality.PolyphaseFast })
{
    var rs = AudioResampler.Create(Rate, 44_100, 1, q);
    var rin = new float[Frames];
    for (int i = 0; i < Frames; i++) rin[i] = MathF.Sin(2 * MathF.PI * 440 * i / Rate);
    var rout = new float[rs.MaxOutputFrames(Frames) + 4096];
    rs.Process(rin, rout, out int ru, out int rw);
    rw += rs.Flush(rout.AsSpan(rw));
    if (ru != Frames || rw < Frames * 44_100 / Rate - 2 || rw > Frames * 44_100 / Rate + 2) return Fail($"resample {q}: used {ru}, wrote {rw}");
}

if (VorbisSmoke.Run() != 0) return 1;
// Opus: every mode through the packet decoder (random payloads are valid Opus), plus codec registration.
var opusRegistry = new AudioCodecRegistry().AddOgg().AddOpus();
if (opusRegistry.GetCodec(CodecId.Opus).Id != CodecId.Opus) return Fail("opus registration");
var opus = new OneAudio.Opus.OpusDecoder(48_000, 2);
var opusPcm = new float[2 * 5760];
var opusRng = new Random(3);
for (int config = 0; config < 32; config += 5)
{
    var packet = new byte[80];
    opusRng.NextBytes(packet);
    packet[0] = (byte)(config << 3 | 4);
    int got = opus.Decode(packet, opusPcm);
    if (got != OneAudio.Opus.OpusPacket.GetSampleCount(packet, 48_000)) return Fail("opus length");
    if (opus.DecodePacketLoss(opusPcm.AsSpan(0, 960 * 2)) != 960) return Fail("opus loss");
}

Console.WriteLine($"ok: {Frames} frames decoded, mixed peak L={peakL:F3} R={peakR:F3}, underruns={mixer.UnderrunEvents}");
return 0;

static int Fail(string why)
{
    Console.Error.WriteLine("FAIL: " + why);
    return 1;
}
