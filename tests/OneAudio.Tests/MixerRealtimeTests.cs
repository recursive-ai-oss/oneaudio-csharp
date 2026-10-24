using Xunit;

namespace OneAudio.Tests;

// Runs alone: the GC-count assertions would otherwise see collections triggered by tests running in parallel.
[CollectionDefinition("RealtimeAllocation", DisableParallelization = true)]
public class RealtimeAllocationDefinition;

[Collection("RealtimeAllocation")]
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

    private static void ExerciseRender(int outChannels, string label)
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
            ResamplerFactory = Factory, EventCapacity = 64,
        });
        var o = new float[outChannels * 480];
        var handles = new VoiceHandle[8];
        int hi = 0;
        var rng = new Random(42);

        void Control(int i)
        {
            var h = handles[rng.Next(8)];
            switch (i % 20)
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
        for (int i = 4000; i < 12000; i++)
        {
            Control(i);   // control calls, pumping and event polling run on other threads in real use: not measured
            stream.Pump(); stream2.Pump();
            long b = GC.GetAllocatedBytesForCurrentThread();
            m.Render(o);
            allocated += GC.GetAllocatedBytesForCurrentThread() - b;
            while (m.TryGetEvent(out _)) { }
        }
        int gcsAfter = GC.CollectionCount(0) + GC.CollectionCount(1) + GC.CollectionCount(2);
        Console.WriteLine($"render allocation [{label}]: {allocated} bytes over 8000 callbacks, GCs during: {gcsAfter - gcs} (control calls included in the GC count), peak voices {m.ActiveVoices}");
        Assert.Equal(0, allocated);
        foreach (var x in o) Assert.True(float.IsFinite(x));
    }

    [Fact]
    public void Render_AllocatesNothing_AcrossEveryFeature_Stereo() => ExerciseRender(2, "stereo");

    [Fact]
    public void Render_AllocatesNothing_AcrossEveryFeature_Surround() => ExerciseRender(6, "5.1");

    [Fact]
    public void Render_PureRenderLoop_LeavesGcCountUnchanged()
    {
        var mono = Ramp(Rate, 3000, 1);
        var m = new AudioMixer(new MixerOptions { SampleRate = Rate, Channels = 2, MaxVoices = 16, ResamplerFactory = Factory });
        for (int i = 0; i < 16; i++) m.Play(new PlayParams(i % 2 == 0 ? mono : Ramp(24000, 3000, 2)) { Loop = true, Pan = i / 8f - 1, Bus = i % 4 });
        m.SetDucking(0, 1);
        m.SetBusOutput(2, 3);
        var o = new float[2 * 512];
        for (int i = 0; i < 2000; i++) m.Render(o); // warm-up
        int gcs = GC.CollectionCount(0);
        long b = GC.GetAllocatedBytesForCurrentThread();
        for (int i = 0; i < 20000; i++) m.Render(o);
        long allocated = GC.GetAllocatedBytesForCurrentThread() - b;
        Console.WriteLine($"pure render: {allocated} bytes, {GC.CollectionCount(0) - gcs} gen0 collections over 20000 callbacks");
        Assert.Equal(0, allocated);
        Assert.Equal(gcs, GC.CollectionCount(0));
    }

    [Fact]
    public void ControlThreads_StreamingPump_AndRender_Concurrently()
    {
        var clip = Ramp(Rate, 2000, 1);
        var odd = Ramp(32000, 2000, 2);
        var surround = Ramp(Rate, 1500, 6);
        var m = new AudioMixer(new MixerOptions { SampleRate = Rate, Channels = 2, MaxVoices = 16, MaxBuses = 6, ResamplerFactory = Factory, CommandCapacity = 256 });
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
                    switch (rng.Next(12))
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
