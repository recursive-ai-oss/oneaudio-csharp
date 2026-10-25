using Xunit;

namespace OneAudio.Tests;

/// <summary>The mixer driving the library's own converters: alignment, tails, bypass, bending and the redesign budget.</summary>
public class MixerConversionTests
{
    private const int Rate = 48000;

    private static AudioClip Sine(int rate, int frames, double hz, float amp)
    {
        var s = new float[frames];
        for (int i = 0; i < frames; i++) s[i] = (float)(amp * Math.Sin(2 * Math.PI * hz * i / rate));
        return new AudioClip(new AudioFormat(rate, 1), s);
    }

    private static float[] Render(AudioMixer m, int frames)
    {
        var o = new float[frames * m.Channels];
        m.Render(o);
        return o;
    }

    [Theory]
    [InlineData(ResamplerQuality.Linear)]
    [InlineData(ResamplerQuality.PolyphaseFast)]
    [InlineData(ResamplerQuality.Polyphase)]
    public void Impulse_KeepsItsPosition_ThroughConversion(ResamplerQuality q)
    {
        var s = new float[3000];
        s[1000] = 1f;
        var m = new AudioMixer(new MixerOptions { SampleRate = Rate, Channels = 1 });
        m.Play(new PlayParams(new AudioClip(new AudioFormat(44100, 1), s)) { Resampling = q });
        var o = Render(m, 3400);
        int peak = Array.IndexOf(o, o.MaxBy(Math.Abs));
        Assert.InRange(peak, 1087, 1090); // 1000 * 48000 / 44100 = 1088.44: the converter compensates its own delay
    }

    [Theory]
    [InlineData(ResamplerQuality.Linear, 0.03f)]
    [InlineData(ResamplerQuality.PolyphaseFast, 0.01f)]
    [InlineData(ResamplerQuality.Polyphase, 0.01f)]
    public void Tone_KeepsLevel_AndLength_AndTailIsDelivered(ResamplerQuality q, float tolerance)
    {
        var m = new AudioMixer(new MixerOptions { SampleRate = Rate, Channels = 1, MaxBlockFrames = 256 });
        m.Play(new PlayParams(Sine(44100, 44100, 1000, 0.5f)) { Resampling = q });
        var all = new List<float>();
        var block = new float[200];
        int passes = 0;
        while (passes++ < 1000)
        {
            m.Render(block);
            all.AddRange(block);
            if (m.ActiveVoices == 0) break;
        }
        // One second at 44.1 kHz becomes one second at 48 kHz, to within a frame or two; the voice has ended.
        int lastNonZero = all.FindLastIndex(x => x != 0f);
        Assert.InRange(lastNonZero + 1, 48000 - 4, 48000 + 2);
        Assert.Equal(0, m.ActiveVoices);
        float peak = all.Skip(20000).Take(8000).Max(Math.Abs);
        Assert.Equal(0.5f, peak, tolerance);
        // The tail written by the flush is still the tone, not silence.
        float tail = all.Skip(47600).Take(300).Max(Math.Abs);
        Assert.True(tail > 0.4f, $"tail peak {tail}");
    }

    [Fact]
    public void RatioOfOne_BypassesTheConverter_Exactly()
    {
        var src = new float[500];
        for (int i = 0; i < src.Length; i++) src[i] = (i + 1) / 1000f;
        var m = new AudioMixer(new MixerOptions { SampleRate = Rate, Channels = 1 });
        // 96 kHz source at pitch 0.5 is exactly the mixer's rate: no filtering, no latency.
        m.Play(new PlayParams(new AudioClip(new AudioFormat(96000, 1), src)) { Pitch = 0.5f });
        var o = Render(m, 600);
        for (int i = 0; i < 500; i++) Assert.Equal(src[i], o[i]);
        Assert.Equal(0f, o[500]);
    }

    [Fact]
    public void VariablePitch_AtUnity_IsFilteredButPassesTheSignal()
    {
        var m = new AudioMixer(new MixerOptions { SampleRate = Rate, Channels = 1 });
        var v = m.Play(new PlayParams(Sine(Rate, 48000, 500, 0.5f)) { VariablePitch = true, Loop = true });
        var o = Render(m, 4000);
        Assert.Equal(0.5f, o.Skip(1000).Take(2000).Max(Math.Abs), 0.01f);
        m.SetPitch(v, 2f);
        var p = Render(m, 4000);
        // After the change the pitch doubled: 500 Hz becomes 1 kHz (48 samples per cycle).
        int crossings = 0;
        for (int i = 2000; i < 4000; i++) if (p[i - 1] < 0 && p[i] >= 0) crossings++;
        Assert.InRange(crossings, 40, 44); // 2000 frames x 1 kHz / 48 kHz = 41.7 cycles
    }

    [Fact]
    public void Stereo_Surround_AndStreaming_ConvertThroughRealResamplers()
    {
        var m = new AudioMixer(new MixerOptions { SampleRate = Rate, Channels = 6 });
        var surround = new float[6 * 4410];
        for (int i = 0; i < 4410; i++) for (int c = 0; c < 6; c++) surround[i * 6 + c] = 0.25f * (c + 1);
        m.Play(new PlayParams(new AudioClip(new AudioFormat(44100, 6), surround)));
        var o = Render(m, 3000);
        Assert.Equal(0.25f, o[6 * 1500], 1e-3f);
        Assert.Equal(1.5f, o[6 * 1500 + 5], 6e-3f);

        var m2 = new AudioMixer(new MixerOptions { SampleRate = Rate, Channels = 1 });
        var stream = new StreamingSource(new RampDecoder(22050, 20000), capacityFrames: 4096);
        using var pump = new StreamingPump();
        pump.Add(stream);
        m2.Play(new PlayParams(stream));
        var block = new float[512];
        for (int i = 0; i < 40 && m2.ActiveVoices > 0; i++) { m2.Render(block); pump.PumpOnce(); }
        Assert.Equal(0, m2.ActiveVoices);
        Assert.Equal(0, m2.UnderrunEvents);
    }

    private sealed class SpyResampler(IResampler inner, Action<IResampler, double> onSetRatio) : IResampler
    {
        public int Channels => inner.Channels;
        public ResamplerQuality Quality => inner.Quality;
        public double Ratio => inner.Ratio;
        public double LatencyFrames => inner.LatencyFrames;
        public void SetRatio(double ratio) { inner.SetRatio(ratio); onSetRatio(this, ratio); }
        public int MaxOutputFrames(int inputFrames) => inner.MaxOutputFrames(inputFrames);
        public void Process(ReadOnlySpan<float> input, Span<float> output, out int used, out int written) => inner.Process(input, output, out used, out written);
        public int Flush(Span<float> output) => inner.Flush(output);
        public void Reset() => inner.Reset();
    }

    [Fact]
    public void RedesignBudget_LimitsExpensiveRatioMovesPerPass_AndEveryVoiceGetsThere()
    {
        const int voices = 8;
        int callsThisPass = 0, maxPerPass = 0;
        var lastRatio = new Dictionary<IResampler, double>();
        var options = new ResamplerOptions { MinRatio = 0.5, MaxRatio = 16 };
        var m = new AudioMixer(new MixerOptions
        {
            SampleRate = Rate, Channels = 1, MaxVoices = voices, MaxBlockFrames = 256, RatioRedesignsPerPass = 1,
            PreallocatedConverters = [],
            ResamplerFactory = (ch, q) => new SpyResampler(AudioResampler.Create(ch, q, 1.0, options), (r, ratio) => { callsThisPass++; lastRatio[r] = ratio; }),
        });
        var clip = Sine(Rate, 20000, 300, 0.1f);
        var handles = new VoiceHandle[voices];
        for (int i = 0; i < voices; i++)
            handles[i] = m.Play(new PlayParams(clip) { VariablePitch = true, Loop = true, Resampling = ResamplerQuality.PolyphaseFast });
        var o = new float[256];
        m.Render(o);
        callsThisPass = 0;
        foreach (var h in handles) m.SetPitch(h, 1.5f); // ratio 1 -> 0.667: every voice needs a filter redesign
        for (int pass = 0; pass < 30; pass++)
        {
            callsThisPass = 0;
            m.Render(o);
            maxPerPass = Math.Max(maxPerPass, callsThisPass);
        }
        Assert.Equal(1, maxPerPass);
        Assert.Equal(voices, lastRatio.Count(kv => Math.Abs(kv.Value - 1.0 / 1.5) < 1e-9));
        foreach (var x in o) Assert.True(float.IsFinite(x));
    }

    [Fact]
    public void SetPitch_ClampsToConverterRange()
    {
        var m = new AudioMixer(new MixerOptions { SampleRate = Rate, Channels = 1 }); // ratio >= 0.5
        var clip = new AudioClip(new AudioFormat(Rate, 1), Enumerable.Range(0, 40000).Select(i => (i + 1) / 40000f).ToArray());
        var v = m.Play(new PlayParams(clip) { VariablePitch = true });
        Render(m, 256);
        m.SetPitch(v, 8f); // would be ratio 0.125; clamps to 0.5, i.e. twice the speed
        var o = Render(m, 2048);
        float slope = (o[2000] - o[1000]) / 1000f;
        Assert.Equal(2f / 40000f, slope, 2e-7f);
    }

    [Fact]
    public void ConverterMemory_IsReported()
    {
        long before = GC.GetAllocatedBytesForCurrentThread();
        _ = new AudioMixer(new MixerOptions { MaxVoices = 4, MaxBuses = 1, MaxBlockFrames = 64, PreallocatedConverters = [] });
        long bare = GC.GetAllocatedBytesForCurrentThread() - before;
        foreach (var q in new[] { ResamplerQuality.Linear, ResamplerQuality.PolyphaseFast, ResamplerQuality.Polyphase })
            foreach (double min in new[] { 1.0, 0.5, 0.25 })
            {
                AudioResampler.Create(2, q, 1.0, new ResamplerOptions { MinRatio = min }); // shared tables are built once, outside the measurement
                before = GC.GetAllocatedBytesForCurrentThread();
                _ = AudioResampler.Create(2, q, 1.0, new ResamplerOptions { MinRatio = min });
                long bytes = GC.GetAllocatedBytesForCurrentThread() - before;
                Console.WriteLine($"converter memory: stereo {q} MinRatio {min}: {bytes / 1024} KiB");
            }
        before = GC.GetAllocatedBytesForCurrentThread();
        _ = new AudioMixer(new MixerOptions { MaxVoices = 64, MaxBuses = 8 });
        long full = GC.GetAllocatedBytesForCurrentThread() - before;
        Console.WriteLine($"mixer construction (4 voices bare): {bare / 1024} KiB; default mixer with default converter reservations: {full / 1024} KiB");
    }
}
