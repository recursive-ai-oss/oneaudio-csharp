using OneAudio.Flac;
using OneAudio.Loudness;
using Xunit;

namespace OneAudio.Tests;

/// <summary>The happy-path layer against the low-level API it is built on.</summary>
public sealed class HappyPathTests : IDisposable
{
    private readonly string _dir = Path.Combine(Path.GetTempPath(), "oneaudio-happy-" + Guid.NewGuid().ToString("N"));

    public HappyPathTests() => Directory.CreateDirectory(_dir);

    public void Dispose() => Directory.Delete(_dir, recursive: true);

    private string P(string name) => Path.Combine(_dir, name);

    private static AudioCodecRegistry All() => new AudioCodecRegistry().AddWav().AddOgg().AddVorbis().AddOpus().AddFlac().AddMp3();

    // Stereo 16-bit music-like signal at 44.1 kHz written as WAV.
    private string MakeWav(string name, int frames = 44100 * 3, int channels = 2, int rate = 44100, double amp = 0.3, NativeSampleType type = NativeSampleType.PcmSigned16)
    {
        var path = P(name);
        var x = new float[frames * channels];
        for (int i = 0; i < frames; i++)
            for (int c = 0; c < channels; c++)
                x[i * channels + c] = (float)(amp * Math.Sin(2 * Math.PI * (440 + 110 * c) * i / rate) * (0.7 + 0.3 * Math.Sin(2 * Math.PI * 0.9 * i / rate)));
        using var fs = File.Create(path);
        using (var enc = new WavEncoder(fs, new AudioFormat(rate, channels), new WavEncoderSettings { SampleType = type }, leaveOpen: true))
        {
            enc.Write(x);
            enc.Finish();
        }
        return path;
    }

    private static (AudioFormat Format, float[] Samples) ManualDecode(string path)
    {
        using var d = AudioDecoder.OpenFile(path, All());
        var clip = AudioClip.FromDecoder(d);
        return (d.Format, clip.Samples.ToArray());
    }

    private static int[] ReadInts(string path, out AudioFormat format)
    {
        using var d = AudioDecoder.OpenFile(path, All());
        format = d.Format;
        Assert.True(d.HasNativeIntegers);
        var all = new List<int>();
        var buf = new int[4096 * d.Format.Channels];
        int n;
        while ((n = d.ReadInteger(buf)) > 0) all.AddRange(buf.AsSpan(0, n * d.Format.Channels).ToArray());
        return all.ToArray();
    }

    [Theory]
    [InlineData("wav")]
    [InlineData("flac")]
    [InlineData("opus")]
    public void Load_equals_manual_decode(string kind)
    {
        var wav = MakeWav("src.wav");
        string path = P("src." + kind);
        if (kind != "wav") Audio.Transcode(wav, path);
        var (format, expected) = ManualDecode(path);

        var clip = Audio.Load(path);
        Assert.Equal(format, clip.Format);
        Assert.True(expected.AsSpan().SequenceEqual(clip.Samples.Span));

        using var stream = File.OpenRead(path);
        var fromStream = Audio.Load(stream);
        Assert.True(stream.CanRead); // not disposed
        Assert.True(expected.AsSpan().SequenceEqual(fromStream.Samples.Span));
    }

    [Fact]
    public void Open_gives_a_streaming_decoder_with_every_codec()
    {
        var wav = MakeWav("a.wav", frames: 5000);
        var flac = P("a.flac");
        Audio.Transcode(wav, flac);
        using var d = Audio.Open(flac);
        Assert.Equal(5000, d.LengthFrames);
        Assert.True(d.CanSeek);
        using var ms = new MemoryStream(File.ReadAllBytes(wav));
        using var d2 = Audio.Open(ms);
        Assert.Equal(5000, d2.LengthFrames);
    }

    [Fact]
    public void Load_converts_rate_and_channels()
    {
        var wav = MakeWav("a.wav", frames: 44100 * 2);
        var clip = Audio.Load(wav, new LoadOptions { SampleRate = 48000, Channels = 1 });
        Assert.Equal(48000, clip.Format.SampleRate);
        Assert.Equal(1, clip.Format.Channels);
        Assert.Equal(96000, clip.Frames);

        // Same result as downmixing by hand and resampling in one call.
        var (_, src) = ManualDecode(wav);
        var mono = new float[src.Length / 2];
        for (int i = 0; i < mono.Length; i++) mono[i] = (src[2 * i] + src[2 * i + 1]) * 0.5f;
        var expected = Audio.Resample(mono, 1, 44100, 48000);
        Assert.Equal(expected.Length, clip.Samples.Length);
        double maxDiff = 0;
        for (int i = 0; i < expected.Length; i++) maxDiff = Math.Max(maxDiff, Math.Abs(expected[i] - clip.Samples.Span[i]));
        Assert.True(maxDiff < 1e-5, $"max difference {maxDiff}");
    }

    [Fact]
    public void Channel_conversion_rules()
    {
        var mono = MakeWav("m.wav", frames: 1000, channels: 1);
        var (_, m) = ManualDecode(mono);
        var st = Audio.Load(mono, new LoadOptions { Channels = 2 });
        for (int i = 0; i < m.Length; i++)
        {
            Assert.Equal(m[i], st.Samples.Span[2 * i]);
            Assert.Equal(m[i], st.Samples.Span[2 * i + 1]);
        }
        var stereo = MakeWav("s.wav", frames: 1000, channels: 2);
        var (_, s) = ManualDecode(stereo);
        var down = Audio.Load(stereo, new LoadOptions { Channels = 1 });
        for (int i = 0; i < down.Frames; i++)
            Assert.Equal((s[2 * i] + s[2 * i + 1]) * 0.5f, down.Samples.Span[i], 1e-6f);
        var six = Audio.Load(stereo, new LoadOptions { Channels = 6 });
        Assert.Equal(6, six.Format.Channels);
        Assert.Equal(s[0], six.Samples.Span[0]);
        Assert.Equal(0f, six.Samples.Span[2]);
    }

    [Fact]
    public void Load_limits_and_typed_errors()
    {
        var wav = MakeWav("a.wav", frames: 10000);
        Assert.Throws<AudioLimitExceededException>(() => Audio.Load(wav, new LoadOptions { MaxFrames = 9999 }));
        var junk = P("junk.bin");
        File.WriteAllBytes(junk, new byte[200]);
        Assert.Throws<UnsupportedAudioFormatException>(() => Audio.Load(junk));
        var slow = MakeWav("slow.wav", frames: 800, rate: 8000);
        Assert.Throws<ArgumentOutOfRangeException>(() => Audio.Load(slow, new LoadOptions { SampleRate = 600_000 }));
        Assert.Throws<AudioLimitExceededException>(() => Audio.Load(wav, new LoadOptions { SampleRate = 44100 * 100 }));

        var wavOnly = new AudioKit(new AudioCodecRegistry().AddWav());
        Assert.Equal(10000, wavOnly.Load(wav).Frames);
        var flac = P("a.flac");
        Audio.Transcode(wav, flac);
        Assert.Throws<UnsupportedAudioFormatException>(() => wavOnly.Load(flac));
        Assert.Throws<AudioLimitExceededException>(() => Audio.Load(wav, new LoadOptions { Limits = new AudioLimits { MaxSampleRate = 1000 } }));
    }

    [Theory]
    [InlineData(NativeSampleType.PcmSigned16)]
    [InlineData(NativeSampleType.PcmSigned24)]
    public void Transcode_wav_flac_wav_is_bit_exact(NativeSampleType type)
    {
        var wav = MakeWav("a.wav", type: type);
        var flac = P("a.flac");
        var back = P("b.wav");
        var r1 = Audio.Transcode(wav, flac);
        Assert.Equal(AudioFileFormat.Flac, r1.Format);
        Assert.Equal(44100 * 3, r1.Frames);
        Audio.Transcode(flac, back);
        var a = ReadInts(wav, out var fa);
        var b = ReadInts(back, out var fb);
        Assert.Equal(fa, fb);
        Assert.True(a.AsSpan().SequenceEqual(b));
        // FLAC through the reference-style manual route gives the same ints.
        Assert.True(a.AsSpan().SequenceEqual(ReadInts(flac, out _)));
    }

    [Fact]
    public void Transcode_to_opus_decodes_with_the_expected_length()
    {
        var wav = MakeWav("a.wav", frames: 48000 * 2, rate: 48000);
        var opus = P("a.opus");
        var r = Audio.Transcode(wav, opus, new TranscodeOptions { Opus = new OneAudio.Opus.OpusFileWriterSettings { Encoder = new OneAudio.Opus.OpusEncoderSettings { BitrateBps = 96000 } } });
        Assert.Equal(AudioFileFormat.Opus, r.Format);
        var clip = Audio.Load(opus);
        Assert.Equal(48000, clip.Format.SampleRate);
        Assert.Equal(2, clip.Format.Channels);
        Assert.Equal(96000, clip.Frames);
        // Resampled input: 44.1 kHz in, 48 kHz out of the Opus decoder.
        var wav441 = MakeWav("b.wav", frames: 44100 * 2);
        Audio.Transcode(wav441, P("b.opus"));
        Assert.InRange(Audio.Load(P("b.opus")).Frames, 96000 - 4, 96000 + 4);
    }

    [Fact]
    public void Transcode_converts_on_the_way_and_streams()
    {
        var wav = MakeWav("a.wav");
        var r = Audio.Transcode(wav, P("c.flac"), new TranscodeOptions { SampleRate = 22050, Channels = 1 });
        Assert.Equal(new AudioFormat(22050, 1, AudioFormat.DefaultMask(1)), r.Input);
        Assert.Equal(22050 * 3, r.Frames);
        using var d = Audio.Open(P("c.flac"));
        Assert.Equal(22050, d.Format.SampleRate);
        Assert.Equal(1, d.Format.Channels);
        Assert.Equal(22050 * 3, d.LengthFrames);
        Assert.Equal(16, d.Format.BitsPerSample);

        using var input = File.OpenRead(wav);
        using var output = new MemoryStream();
        Audio.Transcode(input, output, new TranscodeOptions { Format = AudioFileFormat.Wav, SampleRate = 8000 });
        output.Position = 0;
        using var back = Audio.Open(output);
        Assert.Equal(8000, back.Format.SampleRate);
        Assert.Equal(24000, back.LengthFrames);
    }

    [Fact]
    public void Transcode_argument_and_failure_handling()
    {
        var wav = MakeWav("a.wav", frames: 4000);
        Assert.Throws<ArgumentException>(() => Audio.Transcode(wav, P("out.mp4")));
        Assert.Throws<ArgumentException>(() => Audio.Transcode(wav, wav));
        using (var i = File.OpenRead(wav)) Assert.Throws<ArgumentException>(() => Audio.Transcode(i, new MemoryStream(), new TranscodeOptions()));
        var bad = P("bad.wav");
        File.WriteAllBytes(bad, File.ReadAllBytes(wav)[..30]);
        Assert.ThrowsAny<OneAudioException>(() => Audio.Transcode(bad, P("bad.flac")));
        Assert.False(File.Exists(P("bad.flac")));
        using var cts = new CancellationTokenSource();
        cts.Cancel();
        Assert.ThrowsAny<OperationCanceledException>(() => Audio.Transcode(wav, P("x.wav"), null, cts.Token));
        Assert.False(File.Exists(P("x.wav")));
    }

    [Theory]
    [InlineData(44100, 48000, 1)]
    [InlineData(48000, 44100, 2)]
    [InlineData(48000, 8000, 1)]
    [InlineData(8000, 48000, 1)]
    public void Resample_length_rate_and_signal(int from, int to, int channels)
    {
        int frames = from * 2;
        const double f = 1000;
        var x = new float[frames * channels];
        for (int i = 0; i < frames; i++)
            for (int c = 0; c < channels; c++) x[i * channels + c] = (float)(0.5 * Math.Sin(2 * Math.PI * f * i / from + c));
        var y = Audio.Resample(x, channels, from, to);
        Assert.Equal((long)Math.Round(frames * (double)to / from), y.Length / channels);

        // The output is the same sine sampled at the new rate: rate and delay compensation are both right.
        double maxErr = 0;
        int margin = 400;
        for (int i = margin; i < y.Length / channels - margin; i++)
            for (int c = 0; c < channels; c++)
                maxErr = Math.Max(maxErr, Math.Abs(y[i * channels + c] - 0.5 * Math.Sin(2 * Math.PI * f * i / to + c)));
        Assert.True(maxErr < 1e-3, $"{from}->{to}: max error {maxErr}");

        var clip = Audio.Resample(new AudioClip(new AudioFormat(from, channels), x), to);
        Assert.Equal(to, clip.Format.SampleRate);
        Assert.True(y.AsSpan().SequenceEqual(clip.Samples.Span));
        Assert.True(Audio.Resample(x, channels, from, from).AsSpan().SequenceEqual(x));
        Assert.Throws<ArgumentException>(() => Audio.Resample(new float[3], 2, 1000, 2000));
        Assert.Throws<ArgumentOutOfRangeException>(() => Audio.Resample(x, channels, 1000, 1000 * 100));
    }

    [Fact]
    public void Resample_linear_and_fast_modes_work()
    {
        var x = new float[4800];
        for (int i = 0; i < x.Length; i++) x[i] = (float)Math.Sin(i * 0.05);
        foreach (var q in new[] { ResamplerQuality.Linear, ResamplerQuality.PolyphaseFast })
            Assert.Equal(2400, Audio.Resample(x, 1, 48000, 24000, q).Length);
    }

    [Fact]
    public void Measure_equals_the_loudness_analyzer()
    {
        var wav = MakeWav("a.wav", amp: 0.2);
        LoudnessResult expected;
        using (var d = AudioDecoder.OpenFile(wav, All())) expected = LoudnessAnalyzer.Measure(d);
        Assert.Equal(expected, Audio.Measure(wav));
        using (var s = File.OpenRead(wav)) Assert.Equal(expected, Audio.Measure(s));
        Assert.Equal(expected, Audio.Measure(Audio.Load(wav)));
        Assert.True(double.IsFinite(expected.IntegratedLufs));
        Assert.NotEqual(Audio.Measure(wav, truePeak: false).TruePeakDbtp, expected.TruePeakDbtp);
    }

    [Fact]
    public void Normalize_samples_reaches_the_target()
    {
        var wav = MakeWav("a.wav", frames: 44100 * 8, amp: 0.05);
        var clip = Audio.Load(wav);
        var norm = Audio.Normalize(clip, out var result);
        Assert.False(result.LimitedByPeak);
        Assert.True(result.GainDb > 5);
        var after = Audio.Measure(norm);
        Assert.InRange(after.IntegratedLufs, Audio.DefaultTargetLufs - 0.1, Audio.DefaultTargetLufs + 0.1);
        Assert.Equal(result.Before, Audio.Measure(clip)); // original untouched

        var inPlace = clip.Samples.ToArray();
        var r2 = Audio.Normalize(inPlace, clip.Format, new NormalizeOptions { TargetLufs = -23 });
        Assert.InRange(LoudnessAnalyzer.Measure(inPlace, clip.Format).IntegratedLufs, -23.1, -22.9);
        Assert.Equal(-23 - r2.Before.IntegratedLufs, r2.GainDb, 1e-9);
    }

    [Fact]
    public void Normalize_respects_the_true_peak_ceiling_and_silence()
    {
        // Quiet programme with one loud click: reaching -14 LUFS would pass the ceiling.
        int rate = 48000;
        var x = new float[rate * 6 * 2];
        for (int i = 0; i < rate * 6; i++) x[2 * i] = x[2 * i + 1] = (float)(0.02 * Math.Sin(2 * Math.PI * 300 * i / rate));
        x[rate * 3 * 2] = 0.8f;
        var fmt = new AudioFormat(rate, 2);
        var r = Audio.Normalize(x, fmt);
        Assert.True(r.LimitedByPeak);
        var after = LoudnessAnalyzer.Measure(x, fmt);
        Assert.InRange(after.TruePeakDbtp, -1.1, -0.9);

        var silent = new float[rate * 2];
        var rs = Audio.Normalize(silent, fmt);
        Assert.Equal(0, rs.GainDb);
        Assert.All(silent, v => Assert.Equal(0f, v));
    }

    [Fact]
    public void Normalize_file_to_file_hits_the_target()
    {
        var wav = MakeWav("a.wav", frames: 44100 * 8, amp: 0.05);
        var r = Audio.Normalize(wav, P("n.wav"));
        Assert.InRange(Audio.Measure(P("n.wav")).IntegratedLufs, Audio.DefaultTargetLufs - 0.1, Audio.DefaultTargetLufs + 0.1);
        Assert.Equal(Audio.Measure(wav), r.Before);

        var r2 = Audio.Normalize(wav, P("n.flac"), new NormalizeOptions { TargetLufs = -20, Output = new TranscodeOptions { SampleRate = 48000 } });
        var m = Audio.Measure(P("n.flac"));
        Assert.InRange(m.IntegratedLufs, -20.1, -19.9);
        using (var check = Audio.Open(P("n.flac"))) Assert.Equal(48000, check.Format.SampleRate);
        Assert.Equal(-20 - r2.Before.IntegratedLufs, r2.GainDb, 1e-9);

        using var input = File.OpenRead(wav);
        using var output = new MemoryStream();
        Audio.Normalize(input, output, new NormalizeOptions { Output = new TranscodeOptions { Format = AudioFileFormat.Wav } });
        output.Position = 0;
        Assert.InRange(Audio.Measure(output).IntegratedLufs, -14.1, -13.9);
    }

    private static float[] RenderAll(AudioMixer mixer, int frames)
    {
        var buf = new float[frames * mixer.Channels];
        mixer.Render(buf);
        return buf;
    }

    [Fact]
    public void Play_renders_like_playing_the_loaded_clip()
    {
        var wav = MakeWav("a.wav", frames: 44100, rate: 44100);
        var opts = new MixerOptions { SampleRate = 48000, Channels = 2 };
        var a = new AudioMixer(opts);
        var b = new AudioMixer(opts);
        var va = a.Play(wav, new PlayOptions { Gain = 0.5f, Pan = 0.25f });
        var vb = b.Play(new PlayParams(Audio.Load(wav, new LoadOptions { SampleRate = 48000 })) { Gain = 0.5f, Pan = 0.25f });
        Assert.True(va.IsValid && vb.IsValid);
        var ra = RenderAll(a, 48000);
        var rb = RenderAll(b, 48000);
        Assert.True(ra.AsSpan().SequenceEqual(rb));
        Assert.Contains(ra, v => Math.Abs(v) > 0.01f);

        // The loop overload keeps the voice alive past the end of the clip.
        var c = new AudioMixer(opts);
        c.Play(wav, loop: true);
        RenderAll(c, 48000);
        Assert.Contains(RenderAll(c, 4800), v => Math.Abs(v) > 0.01f);
        Assert.Equal(1, c.ActiveVoices);
    }

    [Fact]
    public void CreateMixer_has_the_documented_defaults()
    {
        var m = Audio.CreateMixer();
        Assert.Equal(48000, m.SampleRate);
        Assert.Equal(2, m.Channels);
        Assert.True(m.HasLimiter);
        var v = m.Play(MakeWav("a.wav", frames: 4800, rate: 48000, amp: 0.9));
        Assert.True(v.IsValid);
        var o = RenderAll(m, 2400);
        Assert.All(o, s => Assert.InRange(s, -1f, 1f));
    }

    [Fact]
    public void PlayStream_streams_loops_and_disposes()
    {
        var wav = MakeWav("a.wav", frames: 48000, rate: 48000);
        var (_, expected) = ManualDecode(wav);
        var mixer = new AudioMixer(new MixerOptions { SampleRate = 48000, Channels = 2 });
        using var sv = mixer.PlayStream(wav, loop: true);
        Assert.True(sv.Voice.IsValid);

        var got = new List<float>();
        var block = new float[1024 * 2];
        var deadline = DateTime.UtcNow.AddSeconds(20);
        while (got.Count < 48000 * 2 * 2 + 4096)
        {
            while (sv.Source.BufferedFrames < 1024 && DateTime.UtcNow < deadline) Thread.Sleep(1);
            Assert.True(DateTime.UtcNow < deadline, "decode thread stalled");
            mixer.Render(block);
            got.AddRange(block);
        }
        // Two passes through the file, seamless: the stream is the file twice over (stereo, unity gain, pan 0).
        Assert.Equal(0, sv.Source.UnderrunFrames);
        for (int i = 0; i < 96000 * 2; i++)
            Assert.True(Math.Abs(expected[i % expected.Length] - got[i]) < 1e-6f, $"sample {i}");

        sv.Dispose();
        sv.Dispose();
        mixer.Render(block);
        mixer.Render(block);
        Assert.Equal(0, mixer.ActiveVoices);
    }

    [Fact]
    public void PlayStream_non_looping_ends_and_the_kit_is_explicit()
    {
        var wav = MakeWav("a.wav", frames: 4800, rate: 48000);
        var mixer = new AudioMixer(new MixerOptions { SampleRate = 48000, Channels = 2 });
        var kit = new AudioKit(new AudioCodecRegistry().AddWav());
        using var sv = kit.PlayStream(mixer, wav, new PlayOptions { Gain = 0.5f });
        var block = new float[480 * 2];
        for (int i = 0; i < 10; i++) { while (sv.Source.BufferedFrames < 480 && !sv.Source.Ended) Thread.Sleep(1); mixer.Render(block); }
        Assert.True(sv.Source.Ended);
        Assert.Throws<ArgumentException>(() => mixer.PlayStream(P("a.wav"), new PlayOptions { Loop = true, LoopStartFrame = -5 }));
    }
}
