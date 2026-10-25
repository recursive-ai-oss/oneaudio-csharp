using System.Diagnostics;
using OneAudio.Effects;
using Xunit;

namespace OneAudio.Tests;

/// <summary>The limiter, the reverb and their use through the mixer.</summary>
public class EffectLimiterReverbTests
{
    private const int Rate = 48000;

    // ------------------------------------------------------------------ limiter

    private static float[] RunLimiter(LimiterProcessor lim, float[] planar, int channels, int frames, int block)
    {
        var o = new float[planar.Length];
        var tmp = new float[channels * block];
        for (int f0 = 0; f0 < frames; f0 += block)
        {
            int n = Math.Min(block, frames - f0);
            for (int c = 0; c < channels; c++) Array.Copy(planar, c * frames + f0, tmp, c * block, n);
            lim.Process(tmp, block, channels, n);
            for (int c = 0; c < channels; c++) Array.Copy(tmp, c * block, o, c * frames + f0, n);
        }
        return o;
    }

    [Theory]
    [InlineData(-1.0, 0)]
    [InlineData(-6.0, 1)]
    [InlineData(-0.1, 2)]
    public void Limiter_NeverExceedsCeiling_OnLoudNoiseAndTransients(double ceilingDb, int seed)
    {
        const int ch = 2, frames = Rate * 4;
        var rng = new Random(seed);
        var x = new float[ch * frames];
        for (int c = 0; c < ch; c++)
            for (int i = 0; i < frames; i++)
            {
                double v = (rng.NextDouble() * 2 - 1) * 3;                    // noise up to +9.5 dBFS
                if (i % 7919 == 0) v = rng.NextDouble() < 0.5 ? 40 : -40;       // lone transients, +32 dBFS
                if (i / 9600 % 5 == 4) v *= 0.01;                              // quiet gaps
                x[c * frames + i] = (float)v;
            }
        var lim = new LimiterProcessor(240, ch, 512);
        float ceil = (float)Math.Pow(10, ceilingDb / 20);
        lim.Configure(true, ceil, 0.05f, Rate);
        var y = RunLimiter(lim, x, ch, frames, 512);
        float peak = 0;
        foreach (float v in y) peak = Math.Max(peak, Math.Abs(v));
        Console.WriteLine($"ceiling {ceilingDb} dB ({ceil:F6}): output peak {peak:F6}");
        Assert.True(peak <= ceil, $"peak {peak} above ceiling {ceil}");
    }

    [Fact]
    public void Limiter_PassesQuietSignal_UnchangedExceptForTheDelay()
    {
        const int ch = 2, frames = 20000, look = 240;
        var rng = new Random(3);
        var x = new float[ch * frames];
        for (int i = 0; i < x.Length; i++) x[i] = (float)(rng.NextDouble() * 1.0 - 0.5);   // within +-0.5, ceiling is 0.89
        var lim = new LimiterProcessor(look, ch, 300);
        lim.Configure(true, 0.89f, 0.1f, Rate);
        var y = RunLimiter(lim, x, ch, frames, 300);
        for (int c = 0; c < ch; c++)
            for (int i = 0; i < frames; i++)
                Assert.Equal(i < look ? 0f : x[c * frames + i - look], y[c * frames + i]);
    }

    [Fact]
    public void Limiter_ReducesGainBeforeThePeakArrives_AndRecovers()
    {
        const int frames = 48000, look = 240;
        var x = new float[frames];
        Array.Fill(x, 0.1f);
        for (int i = 20000; i < 20010; i++) x[i] = 2f;                 // 10-sample burst at +6 dBFS
        var lim = new LimiterProcessor(look, 1, 1024);
        lim.Configure(true, 0.5f, 0.05f, Rate);
        var y = RunLimiter(lim, x, 1, frames, 1024);
        // The burst emerges at 20000 + look, at the ceiling; the gain was already falling for the samples before it.
        Assert.True(y[20000 + look] <= 0.5f);
        Assert.True(y[20000 + look - 1] < 0.1f * 0.999f, "no anticipation before the burst");
        Assert.True(y[20000 + look - 100] < 0.1f);
        Assert.Equal(0.1f, y[frames - 1], 1e-4f);                       // recovered
    }

    [Fact]
    public void Limiter_SurvivesSettingChangesAndSilence()
    {
        var lim = new LimiterProcessor(100, 2, 256);
        var rng = new Random(9);
        var buf = new float[2 * 256];
        for (int pass = 0; pass < 3000; pass++)
        {
            float gain = pass % 50 < 25 ? 8f : 0f;
            for (int c = 0; c < 2; c++) for (int i = 0; i < 256; i++) buf[c * 256 + i] = gain * (float)(rng.NextDouble() * 2 - 1);
            float ceil = 0.1f + (pass % 9) * 0.1f;
            lim.Configure(pass % 7 != 0, ceil, 0.001f + (pass % 5) * 0.1f, Rate);
            lim.Process(buf, 256, 2, 256);
            foreach (float v in buf) Assert.True(float.IsFinite(v));
        }
    }

    [Fact]
    public void Mixer_Limiter_BoundsOutput_ReportsLatency_AndDrains()
    {
        var clip = new AudioClip(new AudioFormat(Rate, 2), Enumerable.Range(0, 2 * 4800).Select(i => (i / 2 % 100 < 50 ? 1f : -1f) * 3f).ToArray());
        var m = new AudioMixer(new MixerOptions { SampleRate = Rate, Channels = 2, MaxBlockFrames = 256, LimiterLookaheadMilliseconds = 5 });
        Assert.True(m.HasLimiter);
        Assert.Equal(240, m.LatencyFrames);
        Assert.True(m.SetLimiter(new LimiterSettings { CeilingDb = -3, ReleaseMilliseconds = 20 }));
        m.Play(new PlayParams(clip));
        var o = new float[2 * 1000];
        m.Render(o);
        Assert.Equal(0f, o[0]);                                           // the first lookahead of output is silent
        float peak = 0;
        for (int p = 0; p < 20; p++) { m.Render(o); foreach (float v in o) peak = Math.Max(peak, Math.Abs(v)); }
        Assert.True(peak <= Math.Pow(10, -3 / 20.0) + 1e-6, $"peak {peak}");
        Assert.True(peak > 0.5f);
        Assert.True(m.LimiterGainReductionDb <= 0f);
        // The clip ended long ago: the delay line has drained to silence.
        foreach (float v in o) Assert.Equal(0f, v);
        Assert.False(new AudioMixer().SetLimiter(new LimiterSettings()));
    }

    // ------------------------------------------------------------------ reverb

    private static float[][] ImpulseResponse(ReverbSettings s, int seconds, int channels = 2, int inputChannels = 1)
    {
        var rv = new ReverbProcessor(channels, Rate);
        rv.Configure(s with { Dry = 0f, Wet = 1f }, 0);
        int frames = seconds * Rate;
        var ir = new float[channels][];
        for (int c = 0; c < channels; c++) ir[c] = new float[frames];
        const int block = 480;
        var buf = new float[channels * block];
        for (int f0 = 0; f0 < frames; f0 += block)
        {
            Array.Clear(buf);
            if (f0 == 0) for (int c = 0; c < inputChannels; c++) buf[c * block] = 1f;
            rv.Process(buf, block, block);
            for (int c = 0; c < channels; c++) Array.Copy(buf, c * block, ir[c], f0, block);
        }
        return ir;
    }

    // RT60 by Schroeder backward integration: fit the energy decay curve between -5 and -25 dB and extrapolate to -60 dB.
    private static double Rt60(float[] ir)
    {
        var edc = new double[ir.Length];
        double acc = 0;
        for (int i = ir.Length - 1; i >= 0; i--) { acc += (double)ir[i] * ir[i]; edc[i] = acc; }
        double total = edc[0];
        int i5 = -1, i25 = -1;
        for (int i = 0; i < edc.Length; i++)
        {
            double db = 10 * Math.Log10(edc[i] / total);
            if (i5 < 0 && db <= -5) i5 = i;
            if (i25 < 0 && db <= -25) { i25 = i; break; }
        }
        return 3.0 * (i25 - i5) / Rate;   // 20 dB of decay measured, 60 wanted
    }

    [Theory]
    [InlineData(0.5f, 0.5f)]
    [InlineData(1.5f, 0.5f)]
    [InlineData(3f, 0.2f)]
    [InlineData(6f, 1f)]
    [InlineData(1.5f, 0f)]
    public void Reverb_DecaysAtTheConfiguredRt60(float decay, float room)
    {
        var ir = ImpulseResponse(new ReverbSettings { DecaySeconds = decay, RoomSize = room, Damping = 0f }, (int)(decay * 1.5f) + 2);
        double t0 = Rt60(ir[0]), t1 = Rt60(ir[1]);
        Console.WriteLine($"RT60 requested {decay} s, room {room}: measured L {t0:F3} s, R {t1:F3} s");
        Assert.InRange(t0, decay * 0.9, decay * 1.1);
        Assert.InRange(t1, decay * 0.9, decay * 1.1);
    }

    [Fact]
    public void Reverb_Damping_ShortensTheHighFrequencyDecay()
    {
        double Band(float[] ir, bool high)
        {
            // Crude one-pole split: low band below about 0.5 kHz, high band above about 9 kHz.
            var lo = new float[ir.Length]; float s = 0; float a = high ? 0.7f : 0.06f;
            for (int i = 0; i < ir.Length; i++) { s += a * (ir[i] - s); lo[i] = s; }
            var band = high ? ir.Select((v, i) => v - lo[i]).ToArray() : lo;
            return Rt60(band);
        }
        var flat = ImpulseResponse(new ReverbSettings { DecaySeconds = 3f, Damping = 0f }, 6)[0];
        var damp = ImpulseResponse(new ReverbSettings { DecaySeconds = 3f, Damping = 0.8f }, 6)[0];
        double lowFlat = Band(flat, false), lowDamp = Band(damp, false), highFlat = Band(flat, true), highDamp = Band(damp, true);
        Console.WriteLine($"RT60 low/high band: undamped {lowFlat:F2}/{highFlat:F2} s, damped {lowDamp:F2}/{highDamp:F2} s");
        Assert.True(highDamp < 0.7 * highFlat);
        Assert.True(lowDamp > 0.8 * lowFlat);
    }

    [Fact]
    public void Reverb_Channels_AreDecorrelated_AndPreDelayDelays()
    {
        var ir = ImpulseResponse(new ReverbSettings { DecaySeconds = 2f, PreDelayMilliseconds = 40f }, 3);
        double lr = 0, ll = 0, rr = 0;
        for (int i = 0; i < ir[0].Length; i++) { lr += ir[0][i] * ir[1][i]; ll += ir[0][i] * ir[0][i]; rr += ir[1][i] * ir[1][i]; }
        double corr = lr / Math.Sqrt(ll * rr);
        Console.WriteLine($"L/R tail correlation {corr:F4}");
        Assert.True(Math.Abs(corr) < 0.1, $"correlation {corr}");
        // Nothing before the pre-delay plus the shortest diffuser/line path.
        int onset = Array.FindIndex(ir[0], v => Math.Abs(v) > 1e-6f);
        Console.WriteLine($"first output at {onset} samples ({onset * 1000.0 / Rate:F1} ms) with 40 ms pre-delay");
        Assert.True(onset >= 40 * Rate / 1000);
        // A mono input excites both outputs, and both tails carry the same energy to within 3 dB.
        Assert.InRange(10 * Math.Log10(ll / rr), -3, 3);
    }

    [Fact]
    public void Reverb_WetLevel_IsRoughlyIndependentOfDecay()
    {
        // White noise at -20 dBFS through Dry 0 / Wet 1: the output power should stay within a few dB across decay times.
        double Level(float decay)
        {
            var rv = new ReverbProcessor(2, Rate);
            rv.Configure(new ReverbSettings { DecaySeconds = decay, Dry = 0f, Wet = 1f }, 0);
            var rng = new Random(1);
            var buf = new float[2 * 480];
            double e = 0; long n = 0;
            for (int pass = 0; pass < 1000; pass++)
            {
                for (int i = 0; i < buf.Length; i++) buf[i] = (float)((rng.NextDouble() * 2 - 1) * 0.1);
                rv.Process(buf, 480, 480);
                if (pass >= 500) { foreach (float v in buf) e += v * v; n += buf.Length; }
            }
            return 10 * Math.Log10(e / n / (0.1 * 0.1 / 3));
        }
        double a = Level(0.3f), b = Level(1.5f), c = Level(8f);
        Console.WriteLine($"wet power relative to input: RT60 0.3 s {a:F1} dB, 1.5 s {b:F1} dB, 8 s {c:F1} dB");
        Assert.InRange(a - c, -3, 3);
        Assert.InRange(b - c, -3, 3);
        Assert.InRange(b, -6, 6);
    }

    [Fact]
    public void Reverb_SilentTail_DoesNotSlowDown_AndGoesIdle()
    {
        var rv = new ReverbProcessor(2, Rate);
        rv.Configure(new ReverbSettings { DecaySeconds = 0.4f, Wet = 1f }, 0);
        var buf = new float[2 * 480];
        buf[0] = buf[480] = 1f;
        rv.Process(buf, 480, 480);
        Assert.True(rv.Ringing);
        var sw = new Stopwatch();
        double early = 0, late = 0;
        int ringingBlocks = 0;
        // Past the tail the processor goes idle and clears itself; to time the arithmetic regardless, keep feeding it silence directly.
        for (int pass = 0; pass < 6000; pass++)
        {
            Array.Clear(buf);
            sw.Restart();
            rv.Process(buf, 480, 480);
            sw.Stop();
            if (rv.Ringing) ringingBlocks++;
            if (pass >= 100 && pass < 600) early += sw.Elapsed.TotalMilliseconds;
            if (pass >= 5000) late += sw.Elapsed.TotalMilliseconds;
            foreach (float v in buf) Assert.True(float.IsFinite(v));
        }
        Console.WriteLine($"tail timing: blocks 100-600 {early:F2} ms, blocks 5000-6000 {late:F2} ms (per 500/1000 blocks); ringing for {ringingBlocks} blocks ({ringingBlocks * 10} ms)");
        Assert.False(rv.Ringing);
        Assert.True(ringingBlocks < 600);
        // Per block, the late (idle, all-zero) blocks must not be slower than the early ringing ones.
        Assert.True(late / 1000 < early / 500 * 2.0 + 0.01);
    }

    [Fact]
    public void Reverb_NoDenormalSlowdown_WhenFedTinyValues()
    {
        // Worst case for denormals: a constant input of 1e-30 (not itself denormal) decaying through the loop.
        var rv = new ReverbProcessor(2, Rate);
        rv.Configure(new ReverbSettings { DecaySeconds = 1f, Wet = 1f }, 0);
        var buf = new float[2 * 480];
        var sw = new Stopwatch();
        double normal = 0, tiny = 0;
        for (int pass = 0; pass < 400; pass++)
        {
            for (int i = 0; i < buf.Length; i++) buf[i] = 0.1f * ((i * 7919) % 13 - 6) / 6f;
            sw.Restart(); rv.Process(buf, 480, 480); sw.Stop();
            if (pass >= 100) normal += sw.Elapsed.TotalMilliseconds;
        }
        for (int pass = 0; pass < 300; pass++)
        {
            for (int i = 0; i < buf.Length; i++) buf[i] = 1e-30f * ((i * 7919) % 13 - 6);
            sw.Restart(); rv.Process(buf, 480, 480); sw.Stop();
            if (pass >= 0) tiny += sw.Elapsed.TotalMilliseconds;
        }
        Console.WriteLine($"normal input {normal / 300:F4} ms/block, tiny input {tiny / 300:F4} ms/block");
        Assert.True(tiny / 300 < normal / 300 * 2.5 + 0.01);
    }

    // ------------------------------------------------------------------ through the mixer

    private static AudioClip Tone(double hz, int frames, float amp = 0.5f, int channels = 1)
    {
        var s = new float[frames * channels];
        for (int i = 0; i < frames; i++)
            for (int c = 0; c < channels; c++) s[i * channels + c] = amp * (float)Math.Sin(2 * Math.PI * hz * i / Rate);
        return new AudioClip(new AudioFormat(Rate, channels), s);
    }

    private static double Rms(float[] o, int ch, int channel, int from, int to)
    {
        double e = 0;
        for (int f = from; f < to; f++) e += o[f * ch + channel] * o[f * ch + channel];
        return Math.Sqrt(e / (to - from));
    }

    [Fact]
    public void Mixer_BusFilter_ShapesTheBus_AndClearingRestoresIt()
    {
        var m = new AudioMixer(new MixerOptions { SampleRate = Rate, Channels = 2, MaxBlockFrames = 256 });
        var hi = Tone(8000, Rate * 2);
        m.Play(new PlayParams(hi) { Loop = true, Bus = 1, Pan = -1f });
        m.Play(new PlayParams(hi) { Loop = true, Bus = 2, Pan = 1f });
        Assert.True(m.SetBusFilter(1, FilterType.LowPass, 500, rampFrames: 0));
        var o = new float[2 * 4800];
        m.Render(o);
        double filtered = Rms(o, 2, 0, 2400, 4800), open = Rms(o, 2, 1, 2400, 4800);
        Assert.True(filtered < open * 0.01, $"filtered {filtered}, open {open}");
        m.ClearBusFilter(1, FilterType.LowPass);
        for (int i = 0; i < 20; i++) m.Render(o);
        Assert.Equal(Rms(o, 2, 1, 0, 4800), Rms(o, 2, 0, 0, 4800), 0.01);
        Assert.Throws<ArgumentOutOfRangeException>(() => m.SetBusFilter(9, FilterType.LowPass, 500));
        Assert.Throws<ArgumentOutOfRangeException>(() => m.SetBusFilter(0, FilterType.LowPass, float.NaN));
    }

    [Fact]
    public void Mixer_VoiceAndMasterFilters_Work()
    {
        var m = new AudioMixer(new MixerOptions { SampleRate = Rate, Channels = 2, MaxBlockFrames = 256 });
        var v = m.Play(new PlayParams(Tone(8000, Rate * 2, 0.5f, 2)) { Loop = true });
        m.SetVoiceFilter(v, FilterType.LowPass, 400, rampFrames: 0);
        var o = new float[2 * 4800];
        m.Render(o);
        Assert.True(Rms(o, 2, 0, 2400, 4800) < 0.01);
        m.ClearVoiceFilter(v, FilterType.LowPass, 0);
        m.Render(o);
        Assert.True(Rms(o, 2, 0, 2400, 4800) > 0.3);                         // unfiltered again: 0.5 / sqrt 2
        m.SetBusFilter(AudioMixer.MasterBus, FilterType.HighPass, 12000, rampFrames: 0);
        m.SetBusFilter(AudioMixer.MasterBus, FilterType.LowPass, 1000, rampFrames: 0);
        m.Render(o);
        m.Render(o);
        Assert.True(Rms(o, 2, 0, 2400, 4800) < 0.02);                        // a 12 kHz high-pass and a 1 kHz low-pass leave nothing of 8 kHz
    }

    [Fact]
    public void Mixer_Reverb_RingsAfterTheVoiceEnds_ThenGoesQuiet_AndSlotsAreLimited()
    {
        var m = new AudioMixer(new MixerOptions { SampleRate = Rate, Channels = 2, MaxBlockFrames = 256, MaxReverbs = 1 });
        Assert.True(m.SetBusReverb(1, new ReverbSettings { DecaySeconds = 0.5f, Wet = 1f, Dry = 0f }));
        Assert.Equal(0, m.FreeReverbSlots);
        var full = Assert.Throws<InvalidOperationException>(() => m.SetBusReverb(2, new ReverbSettings()));   // the only slot is taken
        Assert.Contains("MaxReverbs", full.Message);
        Assert.True(m.SetBusReverb(1, new ReverbSettings { DecaySeconds = 0.5f, Wet = 1f, Dry = 0f }));   // retune is fine
        m.Play(new PlayParams(Tone(500, 2400)) { Bus = 1 });
        var o = new float[2 * 4800];
        m.Render(o);
        m.Render(o);                                                        // 100 ms in: the clip ended after 50 ms
        Assert.True(Rms(o, 2, 0, 0, 4800) > 0.001, "tail should still ring");
        for (int i = 0; i < 40; i++) m.Render(o);                           // four seconds
        Assert.Equal(0.0, Rms(o, 2, 0, 0, 4800));
        // The slot frees when the reverb is cleared.
        Assert.True(m.ClearBusReverb(1));
        Assert.True(m.SetBusReverb(2, new ReverbSettings()));
    }

    [Fact]
    public void Mixer_MutedBus_SilencesItsReverbTail()
    {
        var m = new AudioMixer(new MixerOptions { SampleRate = Rate, Channels = 2, MaxBlockFrames = 256, SmoothingMilliseconds = 0 });
        m.SetBusReverb(1, new ReverbSettings { DecaySeconds = 3f, Wet = 1f, Dry = 0f });
        m.Play(new PlayParams(Tone(500, 4800)) { Bus = 1 });
        var o = new float[2 * 4800];
        m.Render(o);
        m.SetBusMute(1, true);
        m.Render(o);
        Assert.Equal(0.0, Rms(o, 2, 0, 0, 4800));
    }

    [Fact]
    public void Mixer_Reverb_SurroundChannelsAreProcessedAndFinite()
    {
        var m = new AudioMixer(new MixerOptions { SampleRate = Rate, Channels = 6, MaxBlockFrames = 256 });
        m.SetBusReverb(MixerMaster, new ReverbSettings());
        m.Play(new PlayParams(Tone(300, 9600, 0.5f, 1)));
        var o = new float[6 * 4800];
        double rear = 0;
        for (int i = 0; i < 10; i++)
        {
            m.Render(o);
            foreach (float v in o) Assert.True(float.IsFinite(v));
            rear = Math.Max(rear, Rms(o, 6, 5, 0, 4800));
        }
        Assert.True(rear > 1e-4, $"rear channel got no reverb ({rear})");
    }

    private const int MixerMaster = AudioMixer.MasterBus;
}
