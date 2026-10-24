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
Console.WriteLine($"ok: {Frames} frames decoded, mixed peak L={peakL:F3} R={peakR:F3}, underruns={mixer.UnderrunEvents}");
return 0;

static int Fail(string why)
{
    Console.Error.WriteLine("FAIL: " + why);
    return 1;
}
