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
// Bus graph, ducking, multichannel mapping and the decode-ahead pump.
{
    wav.Position = 0;
    var streamed = new StreamingSource(AudioDecoder.Open(wav, registry), capacityFrames: 1024, loop: true);
    using var pump = new StreamingPump();
    pump.Add(streamed);
    pump.Start();

    var surround = new float[6 * 256];
    for (int i = 0; i < surround.Length; i++) surround[i] = 0.1f;
    var m6 = new AudioMixer(new MixerOptions { SampleRate = Rate, Channels = 6, MaxBuses = 4, MaxBlockFrames = 256 });
    if (!m6.SetBusOutput(1, 2) || !m6.SetDucking(2, 3, new DuckingSettings { ThresholdDb = -30 })) return Fail("bus setup");
    try { m6.SetBusOutput(2, 1); return Fail("loop accepted"); } catch (ArgumentException) { }
    m6.Play(new PlayParams(new AudioClip(new AudioFormat(Rate, 6), surround)) { Loop = true, Bus = 1 });
    m6.Play(new PlayParams(streamed) { Bus = 3, Pan = 0.5f });
    var out6 = new float[6 * 512];
    for (int pass = 0; pass < 20; pass++) { m6.Render(out6); Thread.Sleep(2); }
    float centre = 0;
    for (int f = 0; f < 512; f++) centre = MathF.Max(centre, MathF.Abs(out6[f * 6 + 2]));
    // The streamed tone on bus 3 ducks bus 2 by 12 dB, so the surround centre settles near 0.1 * 0.25.
    if (centre < 0.02f || centre > 0.05f) return Fail($"surround mix silent ({centre})");
}

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

// Per-voice conversion and pitch through the library's own resamplers (rate mismatch, pitched, polyphase).
{
    var tone = new float[44_100];
    for (int i = 0; i < tone.Length; i++) tone[i] = 0.5f * MathF.Sin(2 * MathF.PI * 1000 * i / 44_100);
    var mr = new AudioMixer(new MixerOptions { SampleRate = Rate, Channels = 2, MaxVoices = 4 });
    var toneClip = new AudioClip(new AudioFormat(44_100, 1), tone);
    var fast = mr.Play(new PlayParams(toneClip));
    var bent = mr.Play(new PlayParams(toneClip) { Loop = true, VariablePitch = true, Resampling = ResamplerQuality.Polyphase, Gain = 0.1f });
    var outr = new float[2 * 1024];
    float peak = 0;
    for (int pass = 0; pass < 10; pass++)
    {
        mr.SetPitch(bent, 1f + pass * 0.05f);
        mr.Render(outr);
        for (int i = 0; i < outr.Length; i++) peak = MathF.Max(peak, MathF.Abs(outr[i]));
    }
    if (!fast.IsValid || !bent.IsValid || peak < 0.2f || mr.DroppedConverterRequests != 0) return Fail($"converted voices (peak {peak})");
}

if (VorbisSmoke.Run() != 0) return 1;
if (Mp3Smoke.Run() != 0) return 1;
if (FlacSmoke.Run() != 0) return 1;

// Loudness: a -20 dBFS stereo tone must read about -20 LUFS, and the normalisation gain follows from it.
{
    var lm = new OneAudio.Loudness.LoudnessMeter(Rate, 2);
    var lb = new float[Rate * 2 * 5];
    for (int i = 0; i < Rate * 5; i++) lb[2 * i] = lb[2 * i + 1] = 0.1f * MathF.Sin(2 * MathF.PI * 1000 * i / Rate);
    lm.Process(lb);
    double li = lm.IntegratedLufs;
    var lg = OneAudio.Loudness.LoudnessNormalization.ComputeGain(li, lm.TruePeakDbtp);
    if (li < -20.1 || li > -19.9 || lg.GainDb > -2.8 || lg.GainDb < -3.2) return Fail($"loudness ({li}, {lg.GainDb})");
}

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

// Opus encoder: packets, a multistream file and a round trip through the decoder.
{
    var opusEnc = new OneAudio.Opus.OpusEncoder(new OneAudio.Opus.OpusEncoderSettings { BitrateBps = 64_000 });
    var tone = new float[960 * 2];
    for (int i = 0; i < 960; i++) tone[2 * i] = tone[2 * i + 1] = 0.3f * MathF.Sin(i * 0.1f);
    var opusOut = new byte[1500];
    int opusBytes = 0;
    for (int i = 0; i < 4; i++) opusBytes = opusEnc.Encode(tone, 960, opusOut);
    if (opusBytes < 10 || opus.Decode(opusOut.AsSpan(0, opusBytes), opusPcm) != 960) return Fail("opus encode");
    var opusFile = new MemoryStream();
    using (var writer = new OneAudio.Opus.OpusFileWriter(opusFile, new OneAudio.Opus.OpusFileWriterSettings { Channels = 6, InputSampleRate = 44_100 }, leaveOpen: true))
    {
        var six = new float[441 * 6];
        for (int i = 0; i < 441; i++) six[i * 6 + 2] = 0.2f * MathF.Sin(i * 0.2f);
        for (int i = 0; i < 20; i++) writer.Write(six);
    }
    opusFile.Position = 0;
    using var back = AudioDecoder.Open(opusFile, opusRegistry);
    if (back.Format.Channels != 6 || Math.Abs(back.LengthFrames - 9_600) > 4) return Fail("opus file");
}

Console.WriteLine($"ok: {Frames} frames decoded, mixed peak L={peakL:F3} R={peakR:F3}, underruns={mixer.UnderrunEvents}");
return 0;

static int Fail(string why)
{
    Console.Error.WriteLine("FAIL: " + why);
    return 1;
}
