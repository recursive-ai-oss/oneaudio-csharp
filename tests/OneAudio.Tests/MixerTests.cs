using Xunit;

namespace OneAudio.Tests;

public class MixerTests
{
    private const int Rate = 48000;

    private static AudioClip Ramp(int frames, int channels = 1)
    {
        var s = new float[frames * channels];
        for (int i = 0; i < frames; i++)
            for (int c = 0; c < channels; c++) s[i * channels + c] = (i + 1) / 1000f;
        return new AudioClip(new AudioFormat(Rate, channels), s);
    }

    private static AudioMixer Mixer(int channels = 2, int voices = 8, int block = 64) =>
        new(new MixerOptions { SampleRate = Rate, Channels = channels, MaxVoices = voices, MaxBlockFrames = block });

    [Fact]
    public void StartAtFrame_IsExact_AcrossPasses()
    {
        var m = Mixer(channels: 1);
        m.Play(new PlayParams(Ramp(10)) { StartAtFrame = 100 });
        var o = new float[200];
        m.Render(o.AsSpan(0, 90));   // passes of 64 + 26
        m.Render(o.AsSpan(90, 110));
        for (int i = 0; i < 100; i++) Assert.Equal(0f, o[i]);
        for (int i = 0; i < 10; i++) Assert.Equal((i + 1) / 1000f, o[100 + i]);
        for (int i = 110; i < 200; i++) Assert.Equal(0f, o[i]);
        Assert.Equal(200, m.TimelineFrames);
    }

    [Fact]
    public void Loop_WrapsAtExactFrame()
    {
        var m = Mixer(channels: 1);
        m.Play(new PlayParams(Ramp(10)) { Loop = true, LoopStartFrame = 2, LoopEndFrame = 6 });
        var o = new float[20];
        m.Render(o);
        int[] expected = [0, 1, 2, 3, 4, 5, 2, 3, 4, 5, 2, 3, 4, 5, 2, 3, 4, 5, 2, 3];
        for (int i = 0; i < 20; i++) Assert.Equal((expected[i] + 1) / 1000f, o[i]);
    }

    [Fact]
    public void Pan_ConstantPower_AndBusGain()
    {
        var m = Mixer();
        m.SetBusGain(1, 0.5f);
        m.Play(new PlayParams(Ramp(4)) { Pan = 1f, Bus = 1 });
        var o = new float[8];
        m.Render(o);
        Assert.Equal(0f, o[0], 5f);
        Assert.Equal(0.001f * 0.5f, o[1], 1e-6f);
        // Centre pan keeps power: l^2 + r^2 == 1.
        var m2 = Mixer();
        m2.Play(new PlayParams(new AudioClip(new AudioFormat(Rate, 1), [1f])));
        var o2 = new float[2];
        m2.Render(o2);
        Assert.Equal(1f, o2[0] * o2[0] + o2[1] * o2[1], 1e-5f);
    }

    [Fact]
    public void Stop_WithFade_RampsToSilence_ThenEnds()
    {
        var clip = new AudioClip(new AudioFormat(Rate, 1), Enumerable.Repeat(1f, 1000).ToArray());
        var m = Mixer(channels: 1);
        var v = m.Play(new PlayParams(clip));
        m.Render(new float[8]);
        m.Stop(v, 8);
        var o = new float[16];
        m.Render(o);
        for (int i = 0; i < 8; i++) Assert.Equal(1f - (i + 1) / 8f, o[i], 1e-5f);
        Assert.Equal(0f, o[8]);
        Assert.Equal(0, m.ActiveVoices);
        Assert.True(m.TryGetEvent(out var e));
        Assert.Equal(new MixerEvent(MixerEventKind.VoiceEnded, v.Id, 0), e);
    }

    [Fact]
    public void Crossfade_SumsToUnity()
    {
        var clip = new AudioClip(new AudioFormat(Rate, 1), Enumerable.Repeat(1f, 1000).ToArray());
        var m = Mixer(channels: 1);
        var a = m.Play(new PlayParams(clip));
        m.Render(new float[4]);
        var b = m.Crossfade(a, new PlayParams(clip), 10);
        Assert.True(b.IsValid);
        var o = new float[12];
        m.Render(o);
        for (int i = 0; i < 10; i++) Assert.Equal(1f, o[i], 1e-5f);
        Assert.Equal(1f, o[11], 1e-5f);
    }

    [Fact]
    public void VoiceCap_StealsLowestPriority_OrDrops()
    {
        var clip = new AudioClip(new AudioFormat(Rate, 1), Enumerable.Repeat(0.1f, 10000).ToArray());
        var m = Mixer(channels: 1, voices: 2);
        var low = m.Play(new PlayParams(clip) { Priority = 0 });
        m.Play(new PlayParams(clip) { Priority = 5 });
        var high = m.Play(new PlayParams(clip) { Priority = 3 });
        var dropped = m.Play(new PlayParams(clip) { Priority = -1 });
        m.Render(new float[16]);
        Assert.Equal(2, m.ActiveVoices);
        Assert.Equal(1, m.StolenVoices);
        Assert.Equal(1, m.DroppedPlays);
        var kinds = new List<(MixerEventKind, long)>();
        while (m.TryGetEvent(out var e)) kinds.Add((e.Kind, e.VoiceId));
        Assert.Contains((MixerEventKind.VoiceStolen, low.Id), kinds);
        Assert.Contains((MixerEventKind.PlayDropped, dropped.Id), kinds);
        Assert.True(high.IsValid);
    }

    [Fact]
    public void StaleHandle_IsIgnored_And_ArgumentsValidated()
    {
        var m = Mixer();
        Assert.True(m.Stop(new VoiceHandle(999)));
        m.Render(new float[4]);
        Assert.Throws<ArgumentException>(() => m.Play(new PlayParams(new AudioClip(new AudioFormat(44100, 1), [0f]))));
        Assert.Throws<ArgumentException>(() => m.Play(new PlayParams(new AudioClip(new AudioFormat(Rate, 3), [0f, 0f, 0f]))));
        Assert.Throws<ArgumentOutOfRangeException>(() => m.Play(new PlayParams(Ramp(1)) { Bus = 99 }));
    }

    [Fact]
    public void FullCommandQueue_DropsAndCounts()
    {
        var m = new AudioMixer(new MixerOptions { SampleRate = Rate, CommandCapacity = 16 });
        int ok = 0;
        for (int i = 0; i < 40; i++) if (m.SetBusGain(0, 1f)) ok++;
        Assert.Equal(16, ok);
        Assert.Equal(24, m.DroppedCommands);
    }

    [Fact]
    public void StreamingUnderrun_RendersSilence_AndCounts()
    {
        var dec = new RampDecoder(Rate, totalFrames: 300);
        var src = new StreamingSource(dec, capacityFrames: 128);
        var m = Mixer(channels: 1);
        var v = m.Play(new PlayParams(src));
        var o = new float[64];
        m.Render(o); // nothing pumped yet: all silence
        Assert.Equal(1, m.UnderrunEvents);
        Assert.Equal(64, m.UnderrunFrames);
        Assert.Equal(64, src.UnderrunFrames);
        Assert.All(o, x => Assert.Equal(0f, x));
        Assert.True(m.TryGetEvent(out var e));
        Assert.Equal(MixerEventKind.Underrun, e.Kind);

        Assert.Equal(128, src.Pump());
        m.Render(o);
        m.Render(o);
        Assert.Equal(0.128f, o[63], 1e-6f); // the buffered data played in order
        Assert.Equal(1, m.UnderrunEvents);

        // Ring emptied, decoder not finished: underrun again; when the decoder finishes the voice ends quietly.
        m.Render(o);
        Assert.Equal(2, m.UnderrunEvents);
        while (src.Pump() > 0) { m.Render(o); }
        for (int i = 0; i < 10; i++) { src.Pump(); m.Render(o); }
        Assert.True(src.Ended);
        Assert.Equal(0, m.ActiveVoices);
        Assert.True(v.IsValid);
    }

    [Fact]
    public void StreamingLoop_RestartsDecoder()
    {
        var src = new StreamingSource(new RampDecoder(Rate, 5), capacityFrames: 64, loop: true);
        src.Pump();
        Assert.Equal(64, src.BufferedFrames);
        Assert.False(src.Ended);
    }

    [Fact]
    public void Render_AllocatesNothing()
    {
        var clip = Ramp(5000, 2);
        var dec = new RampDecoder(Rate, 1_000_000);
        var stream = new StreamingSource(dec, capacityFrames: 4096);
        var m = new AudioMixer(new MixerOptions { SampleRate = Rate, Channels = 2, MaxVoices = 4, MaxBlockFrames = 256 });
        var o = new float[2 * 480];
        VoiceHandle h = default;

        void Step(int i)
        {
            switch (i % 9)
            {
                case 0: h = m.Play(new PlayParams(clip) { Loop = i % 2 == 0, Priority = i % 3, FadeInFrames = 32 }); break;
                case 1: m.SetGain(h, 0.5f, 100); break;
                case 2: m.SetPan(h, (i % 5 - 2) / 2f); break;
                case 3: m.Stop(h, 64); break;
                case 4: m.SetBusGain(i % 8, 0.9f); break;
                case 5: m.Crossfade(h, new PlayParams(clip), 50); break;
                case 6: m.SetLoop(h, true, 10, 200); break;
                case 7: m.StopAll(0); break;
                case 8: m.Play(new PlayParams(stream)); break;
            }
            stream.Pump();
            m.Render(o);
            while (m.TryGetEvent(out _)) { }
        }

        for (int i = 0; i < 2000; i++) Step(i); // warm-up covers JIT and tiering
        int gcs = GC.CollectionCount(0);
        // Only Render is measured: control calls and Pump run on other threads in real use.
        long allocated = 0;
        for (int i = 0; i < 5000; i++)
        {
            switch (i % 9)
            {
                case 0: h = m.Play(new PlayParams(clip) { Loop = true, FadeInFrames = 16 }); break;
                case 3: m.Stop(h, 64); break;
                case 5: m.Crossfade(h, new PlayParams(clip), 50); break;
                case 8: m.Play(new PlayParams(stream)); break;
            }
            stream.Pump();
            long b = GC.GetAllocatedBytesForCurrentThread();
            m.Render(o);
            allocated += GC.GetAllocatedBytesForCurrentThread() - b;
        }
        Assert.Equal(0, allocated);
        Assert.Equal(gcs, GC.CollectionCount(0));
    }

    [Fact]
    public void ControlThreadsHammerRender_Concurrently()
    {
        var clip = Ramp(2000);
        var m = new AudioMixer(new MixerOptions { SampleRate = Rate, Channels = 2, MaxVoices = 16 });
        using var stop = new CancellationTokenSource();
        var workers = Enumerable.Range(0, 3).Select(t => Task.Run(() =>
        {
            var rng = new Random(t);
            VoiceHandle last = default;
            while (!stop.IsCancellationRequested)
            {
                last = m.Play(new PlayParams(clip) { Loop = true, Gain = 0.1f, Pan = (float)rng.NextDouble() * 2 - 1 });
                m.SetGain(last, 0.2f, 10);
                m.Stop(last, 5);
                while (m.TryGetEvent(out _)) { }
                Thread.Yield();
            }
        })).ToArray();
        var o = new float[2 * 128];
        for (int i = 0; i < 3000; i++)
        {
            m.Render(o);
            foreach (var x in o) Assert.True(float.IsFinite(x));
        }
        stop.Cancel();
        Task.WaitAll(workers);
        Assert.Equal(3000 * 128, m.TimelineFrames);
    }
}

/// <summary>A mono decoder producing the ramp 0.001, 0.002, ... for tests.</summary>
internal sealed class RampDecoder(int rate, long totalFrames) : IAudioDecoder
{
    private long _pos;
    public AudioFormat Format { get; } = new(rate, 1);
    public long LengthFrames => totalFrames;
    public long PositionFrames => _pos;
    public bool CanSeek => true;
    public bool HasNativeIntegers => false;
    public int Read(Span<float> destination)
    {
        int n = (int)Math.Min(destination.Length, totalFrames - _pos);
        for (int i = 0; i < n; i++) destination[i] = (_pos + i + 1) / 1000f;
        _pos += n;
        return n;
    }
    public int ReadInteger(Span<int> destination) => throw new NotSupportedException();
    public void Seek(long frame) => _pos = frame;
    public void Dispose() { }
}
