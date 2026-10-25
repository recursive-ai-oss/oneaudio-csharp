using Xunit;

namespace OneAudio.Tests;

public class MixerRealtimeTests
{
    private const int Rate = 48000;

    private static AudioClip Ramp(int rate, int frames, int channels)
    {
        var s = new float[frames * channels];
        for (int i = 0; i < frames; i++)
            for (int c = 0; c < channels; c++) s[i * channels + c] = (i + 1) / 1000f;
        return new AudioClip(new AudioFormat(rate, channels), s);
    }

    [Fact]
    public void Loop_IsExact_AcrossPasses_ForStereoAndSurround()
    {
        foreach (int ch in new[] { 2, 6 })
        {
            var m = new AudioMixer(new MixerOptions { SampleRate = Rate, Channels = ch, MaxVoices = 2, MaxBlockFrames = 16 });
            m.Play(new PlayParams(Ramp(Rate, 10, ch)) { Loop = true, LoopStartFrame = 2, LoopEndFrame = 6 });
            var o = new float[ch * 101];
            m.Render(o.AsSpan(0, ch * 37));      // 16 + 16 + 5 frames
            m.Render(o.AsSpan(ch * 37));         // 64 frames
            for (int f = 0; f < 101; f++)
            {
                int src = f < 6 ? f : 2 + (f - 6) % 4;
                Assert.Equal((src + 1) / 1000f, o[f * ch], 1e-7f);
            }
        }
    }

    [Fact]
    public void StartAtFrame_IsExact_WhenStartedFromControlMidStream()
    {
        var m = new AudioMixer(new MixerOptions { SampleRate = Rate, Channels = 2, MaxBlockFrames = 32 });
        m.Render(new float[2 * 100]);
        long start = m.TimelineFrames + 53;
        m.Play(new PlayParams(Ramp(Rate, 4, 2)) { StartAtFrame = start });
        var o = new float[2 * 200];
        m.Render(o);
        for (int f = 0; f < 200; f++)
        {
            long t = 100 + f;
            float expect = t >= start && t < start + 4 ? (t - start + 1) / 1000f : 0f;
            Assert.Equal(expect, o[2 * f], 1e-7f);
            Assert.Equal(expect, o[2 * f + 1], 1e-7f);
        }
    }

    private static Func<int, ResamplerQuality, IResampler> Factory => (ch, q) => new LinearTestResampler(ch, q);

    private static void ExerciseRender(int outChannels, string label, bool realConverters)
    {
        var stereo = Ramp(Rate, 5000, 2);
        var mono = Ramp(Rate, 3000, 1);
        var surround = Ramp(Rate, 4000, 6);
        var odd = Ramp(44100, 3500, 2);
        var slow = Ramp(24000, 2500, 1);
        var stream = new StreamingSource(new RampDecoder(Rate, 10_000_000), capacityFrames: 4096);
        var stream2 = new StreamingSource(new RampDecoder(22050, 10_000_000), capacityFrames: 4096);
        var m = new AudioMixer(new MixerOptions
        {
            SampleRate = Rate, Channels = outChannels, MaxVoices = 8, MaxBuses = 6, MaxBlockFrames = 256,
            ResamplerFactory = realConverters ? null : Factory, EventCapacity = 64,
            MaxReverbs = 2, LimiterLookaheadMilliseconds = 5,
        });
        var o = new float[outChannels * 480];
        var handles = new VoiceHandle[8];
        int hi = 0;
        var rng = new Random(42);

        void Control(int i)
        {
            var h = handles[rng.Next(8)];
            switch (i % 26)
            {
                case 0: handles[hi++ & 7] = m.Play(new PlayParams(stereo) { Loop = true, Priority = i % 3, FadeInFrames = 32, Bus = i % 4 }); break;
                case 1: handles[hi++ & 7] = m.Play(new PlayParams(surround) { Pan = (i % 5 - 2) / 2f, Bus = 1 }); break;
                case 2: handles[hi++ & 7] = m.Play(new PlayParams(mono) { VariablePitch = true, Loop = true, Pan = -0.3f }); break;
                case 3: m.SetPitch(h, 0.5f + (i % 7) * 0.3f, i % 2 == 0 ? 0 : 300); break;
                case 4: m.SetPan(h, (i % 9 - 4) / 4f); break;
                case 5: m.SetBusOutput(2, i % 2 == 0 ? 3 : -1); break;
                case 6: if (i % 2 == 0) m.SetDucking(1, 2, new DuckingSettings { ThresholdDb = -40 }); else m.ClearDucking(1); break;
                case 7: m.SetBusGain(i % 6, 0.3f + (i % 4) * 0.2f); break;
                case 8: handles[hi++ & 7] = m.Crossfade(h, new PlayParams(mono) { Loop = true, Bus = 2, Pitch = 1.5f }, 50); break;
                case 9: m.Stop(h, 64); break;
                case 10: if (i % 40 == 10) m.StopAll(0); break;
                case 11: handles[hi++ & 7] = m.Play(new PlayParams(stream) { Bus = 2 }); break;
                case 12: handles[hi++ & 7] = m.Play(new PlayParams(odd) { Resampling = ResamplerQuality.Polyphase, Bus = 3 }); break;
                case 13: m.SetMasterGain(0.5f + (i % 3) * 0.2f); break;
                case 14: handles[hi++ & 7] = m.Play(new PlayParams(slow) { Priority = 5, StartAtFrame = m.TimelineFrames + 100 }); break;
                case 15: m.SetLoop(h, true, 10, 200); break;
                case 16: m.SetBusMute(i % 6, i % 3 == 0); break;
                case 17: handles[hi++ & 7] = m.Play(new PlayParams(stream2) { Bus = 4 }); break;
                case 18: m.SetGain(h, 0.7f, 100); break;
                case 19: m.SetBusOutput(4, i % 2 == 0 ? 5 : -1); break;
                case 20: m.SetBusFilter(i % 7 - 1, i % 3 == 0 ? FilterType.HighPass : FilterType.LowPass, 50 + (i * 37) % 15000, 0.3f + i % 11, i % 4 == 0 ? 0 : i % 4 * 200); break;
                case 21: m.ClearBusFilter(i % 7 - 1, i % 2 == 0 ? FilterType.HighPass : FilterType.LowPass, i % 3 * 100); break;
                case 22:
                    m.SetBusReverb(i % 3 - 1, new ReverbSettings { DecaySeconds = 0.2f + i % 7, RoomSize = (i % 5) / 4f, Damping = (i % 3) / 2f, PreDelayMilliseconds = i % 4 * 20, Wet = 0.5f }, i % 2 * 300);
                    break;
                case 23: m.ClearBusReverb(i % 3 - 1); break;
                case 24: m.SetLimiter(i % 5 == 0 ? null : new LimiterSettings { CeilingDb = -(i % 12), ReleaseMilliseconds = 5 + i % 100 }); break;
                case 25: if (i % 2 == 0) m.SetVoiceFilter(h, FilterType.LowPass, 300 + i % 5000, 1f + i % 4, i % 3 * 50); else m.ClearVoiceFilter(h, FilterType.LowPass, 10); break;
            }
        }

        // Warm-up covers JIT, tiering and the converter pool filling.
        for (int i = 0; i < 4000; i++)
        {
            Control(i);
            stream.Pump(); stream2.Pump();
            m.Render(o);
            while (m.TryGetEvent(out _)) { }
        }

        int gcs = GC.CollectionCount(0) + GC.CollectionCount(1) + GC.CollectionCount(2);
        long allocated = 0;
        var swC = new System.Diagnostics.Stopwatch(); var swR = new System.Diagnostics.Stopwatch(); long maxTicks = 0;
        for (int i = 4000; i < 12000; i++)
        {
            swC.Start(); Control(i); swC.Stop();   // control calls, pumping and event polling run on other threads in real use: not measured
            stream.Pump(); stream2.Pump();
            long b = GC.GetAllocatedBytesForCurrentThread();
            long t0 = System.Diagnostics.Stopwatch.GetTimestamp();
            m.Render(o);
            long dt = System.Diagnostics.Stopwatch.GetTimestamp() - t0; swR.Start(); swR.Stop(); maxTicks = Math.Max(maxTicks, dt);
            allocated += GC.GetAllocatedBytesForCurrentThread() - b;
            while (m.TryGetEvent(out _)) { }
        }
        int gcsAfter = GC.CollectionCount(0) + GC.CollectionCount(1) + GC.CollectionCount(2);
        Console.WriteLine($"render allocation [{label}]: {allocated} bytes over 8000 callbacks, GCs during: {gcsAfter - gcs} (control calls included in the GC count), peak voices {m.ActiveVoices}");
        Console.WriteLine($"  control time {swC.ElapsedMilliseconds} ms, slowest render {maxTicks * 1000.0 / System.Diagnostics.Stopwatch.Frequency:F3} ms");
        Assert.Equal(0, allocated);
        foreach (var x in o) Assert.True(float.IsFinite(x));
    }

    [Fact]
    public void Render_AllocatesNothing_AcrossEveryFeature_Stereo() => ExerciseRender(2, "stereo, library converters", realConverters: true);

    [Fact]
    public void Render_AllocatesNothing_AcrossEveryFeature_Surround() => ExerciseRender(6, "5.1, test converters", realConverters: false);

    [Fact]
    public void Render_AllocatesNothing_WithEveryEffectActive_AndParametersChanging()
    {
        var clips = new[] { Ramp(Rate, 5000, 2), Ramp(Rate, 3000, 1), Ramp(44100, 4000, 2) };
        var m = new AudioMixer(new MixerOptions
        {
            SampleRate = Rate, Channels = 2, MaxVoices = 64, MaxBuses = 4, MaxBlockFrames = 512, MaxReverbs = 2, LimiterLookaheadMilliseconds = 5,
        });
        for (int i = 0; i < 64; i++)
        {
            var h = m.Play(new PlayParams(clips[i % 3]) { Loop = true, Bus = i % 4, Gain = 0.05f });
            if (i % 2 == 0) m.SetVoiceFilter(h, FilterType.LowPass, 2000 + i * 50, 1f, 0);
        }
        for (int b = 0; b < 4; b++)
        {
            m.SetBusFilter(b, FilterType.LowPass, 5000, 0.9f);
            m.SetBusFilter(b, FilterType.HighPass, 100, 0.7f);
        }
        m.SetBusReverb(1, new ReverbSettings());
        m.SetBusReverb(AudioMixer.MasterBus, new ReverbSettings { Wet = 0.1f });
        m.SetLimiter(new LimiterSettings());
        var o = new float[2 * 480];
        void Tweak(int i)
        {
            m.SetBusFilter(i % 4, FilterType.LowPass, 200 + (i * 91) % 12000, 0.5f + i % 9, 480);
            m.SetBusFilter((i + 1) % 4, FilterType.HighPass, 40 + (i * 13) % 800, 0.7f, 100);
            m.SetBusReverb(1, new ReverbSettings { RoomSize = (i % 10) / 9f, DecaySeconds = 0.3f + i % 5, Damping = (i % 4) / 3f, PreDelayMilliseconds = i % 6 * 10, Wet = 0.2f + (i % 3) * 0.1f }, 200);
            m.SetLimiter(new LimiterSettings { CeilingDb = -1f - i % 6, ReleaseMilliseconds = 20 + i % 80 });
            while (m.TryGetEvent(out _)) { }
        }
        for (int i = 0; i < 3000; i++) { Tweak(i); m.Render(o); }
        int gcs = GC.CollectionCount(0);
        long allocated = 0;
        float peak = 0;
        for (int i = 3000; i < 9000; i++)
        {
            long b = GC.GetAllocatedBytesForCurrentThread();
            m.Render(o);
            allocated += GC.GetAllocatedBytesForCurrentThread() - b;
            foreach (float v in o) peak = Math.Max(peak, Math.Abs(v));
            Tweak(i);
        }
        Console.WriteLine($"render with every effect and moving parameters (64 voices, 4 buses with 2 filters each, 2 reverbs, limiter): {allocated} bytes over 6000 callbacks of 480 frames, output peak {peak:F3}, gen0 GCs {GC.CollectionCount(0) - gcs} (control calls included)");
        Assert.True(peak <= Math.Pow(10, -1 / 20.0) + 1e-6);
        Assert.Equal(0, allocated);
    }

    [Fact]
    public void Render_PureRenderLoop_LeavesGcCountUnchanged()
    {
        var mono = Ramp(Rate, 3000, 1);
        var m = new AudioMixer(new MixerOptions { SampleRate = Rate, Channels = 2, MaxVoices = 16 });
        for (int i = 0; i < 16; i++) m.Play(new PlayParams(i % 2 == 0 ? mono : Ramp(24000, 3000, 2)) { Loop = true, Resampling = i % 4 == 1 ? ResamplerQuality.Polyphase : null, Pan = i / 8f - 1, Bus = i % 4 });
        m.SetDucking(0, 1);
        m.SetBusOutput(2, 3);
        var o = new float[2 * 512];
        for (int i = 0; i < 2000; i++) m.Render(o); // warm-up
        int gcs = GC.CollectionCount(0);
        long b = GC.GetAllocatedBytesForCurrentThread();
        for (int i = 0; i < 5000; i++) m.Render(o);
        long allocated = GC.GetAllocatedBytesForCurrentThread() - b;
        Console.WriteLine($"pure render: {allocated} bytes, {GC.CollectionCount(0) - gcs} gen0 collections over 5000 callbacks");
        Assert.Equal(0, allocated);
        Assert.Equal(gcs, GC.CollectionCount(0));
    }

    [Fact]
    public void ControlThreads_StreamingPump_AndRender_Concurrently()
    {
        var clip = Ramp(Rate, 2000, 1);
        var odd = Ramp(32000, 2000, 2);
        var surround = Ramp(Rate, 1500, 6);
        var m = new AudioMixer(new MixerOptions { SampleRate = Rate, Channels = 2, MaxVoices = 16, MaxBuses = 6, CommandCapacity = 256, MaxReverbs = 3, LimiterLookaheadMilliseconds = 2 });
        using var pump = new StreamingPump(intervalMilliseconds: 1);
        pump.Start();
        using var stop = new CancellationTokenSource();
        var errors = new System.Collections.Concurrent.ConcurrentQueue<Exception>();

        Task Worker(int t) => Task.Run(() =>
        {
            try
            {
                var rng = new Random(t);
                VoiceHandle last = default;
                while (!stop.IsCancellationRequested)
                {
                    switch (rng.Next(17))
                    {
                        case 0: last = m.Play(new PlayParams(clip) { Loop = true, Gain = 0.1f, Pan = (float)rng.NextDouble() * 2 - 1, Bus = rng.Next(6) }); break;
                        case 1: last = m.Play(new PlayParams(odd) { Gain = 0.1f, VariablePitch = true, Bus = rng.Next(6) }); break;
                        case 2: last = m.Play(new PlayParams(surround) { Loop = true, Gain = 0.1f }); break;
                        case 3: m.SetPitch(last, 0.5f + (float)rng.NextDouble() * 2, rng.Next(2) * 200); break;
                        case 4: m.SetPan(last, (float)rng.NextDouble() * 2 - 1); break;
                        case 5:
                            try { m.SetBusOutput(rng.Next(6), rng.Next(-1, 6)); } catch (ArgumentException) { }
                            break;
                        case 6:
                            try { m.SetDucking(rng.Next(6), rng.Next(6)); } catch (ArgumentException) { }
                            break;
                        case 7: m.ClearDucking(rng.Next(6)); break;
                        case 8: m.SetBusGain(rng.Next(6), (float)rng.NextDouble()); m.SetBusMute(rng.Next(6), rng.Next(3) == 0); break;
                        case 9: m.SetGain(last, 0.2f, 10); m.Stop(last, 5); break;
                        case 10:
                        {
                            var s = new StreamingSource(new RampDecoder(Rate, 50_000), capacityFrames: 1024);
                            pump.Add(s);
                            last = m.Play(new PlayParams(s) { Gain = 0.05f });
                            m.Stop(last, 100);
                            pump.Remove(s);
                            break;
                        }
                        case 11: m.SetMasterGain((float)rng.NextDouble()); break;
                        case 12: m.SetBusFilter(rng.Next(-1, 6), (FilterType)rng.Next(2), 20 + (float)rng.NextDouble() * 20000, 0.1f + (float)rng.NextDouble() * 20, rng.Next(2) * 300); break;
                        case 13: m.ClearBusFilter(rng.Next(-1, 6), (FilterType)rng.Next(2), rng.Next(2) * 50); break;
                        case 14: m.SetBusReverb(rng.Next(-1, 6), new ReverbSettings { DecaySeconds = 0.1f + rng.Next(30) * 0.5f, RoomSize = (float)rng.NextDouble() }); break;
                        case 15: m.ClearBusReverb(rng.Next(-1, 6)); m.SetLimiter(rng.Next(3) == 0 ? null : new LimiterSettings { CeilingDb = -rng.Next(20) }); break;
                        case 16: m.SetVoiceFilter(last, (FilterType)rng.Next(2), 100 + (float)rng.NextDouble() * 9000, 1f, rng.Next(2) * 20); m.ClearVoiceFilter(last, FilterType.HighPass, 0); break;
                    }
                    while (m.TryGetEvent(out _)) { }
                    Thread.Yield();
                }
            }
            catch (Exception e) { errors.Enqueue(e); }
        });

        var workers = Enumerable.Range(0, 4).Select(Worker).ToArray();
        var o = new float[2 * 128];
        for (int i = 0; i < 4000; i++)
        {
            m.Render(o);
            foreach (var x in o) Assert.True(float.IsFinite(x));
        }
        stop.Cancel();
        Task.WaitAll(workers);
        Assert.Empty(errors);
        Assert.Equal(4000 * 128, m.TimelineFrames);
    }
}
