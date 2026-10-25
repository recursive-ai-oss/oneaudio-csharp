using OneAudio.Loudness;
using Xunit;

namespace OneAudio.Tests;

public class MixerMeterTests
{
    private const int Rate = 48000;

    private static AudioClip Sine(double hz, double amplitude, int channels = 1)
    {
        var s = new float[Rate * channels];
        for (int i = 0; i < Rate; i++)
            for (int c = 0; c < channels; c++) s[i * channels + c] = (float)(amplitude * Math.Sin(2 * Math.PI * hz * i / Rate));
        return new AudioClip(new AudioFormat(Rate, channels), s);
    }

    private static AudioClip Noise(double amplitude, int seed)
    {
        var rng = new Random(seed);
        var s = new float[Rate];
        for (int i = 0; i < s.Length; i++) s[i] = (float)((rng.NextDouble() * 2 - 1) * amplitude);
        return new AudioClip(new AudioFormat(Rate, 1), s);
    }

    private static MixerOptions Options(int meters = 4, int channels = 2) => new()
    {
        SampleRate = Rate, Channels = channels, MaxBuses = 4, MaxVoices = 8, MaxBlockFrames = 256, MaxBusMeters = meters,
        SmoothingMilliseconds = 0, CommandCapacity = 16,
    };

    /// <summary>The checks that hold for every snapshot a reader can get, whatever the render thread is doing.</summary>
    internal static void AssertConsistent(LoudnessSnapshot s)
    {
        Assert.True(s.FramesMeasured >= 0);
        if (s.FramesMeasured == 0)
        {
            Assert.Equal(LoudnessSnapshot.Empty, s);
            return;
        }
        // A maximum is taken from the same windows as the current value, so it can never be below it (tolerance for the periodic exact re-sum).
        Assert.True(s.MaxMomentaryLufs >= s.MomentaryLufs - 1e-6, $"max momentary {s.MaxMomentaryLufs} < {s.MomentaryLufs}");
        Assert.True(s.MaxShortTermLufs >= s.ShortTermLufs - 1e-6, $"max short-term {s.MaxShortTermLufs} < {s.ShortTermLufs}");
        Assert.False(double.IsNaN(s.MomentaryLufs) || double.IsNaN(s.ShortTermLufs) || double.IsNaN(s.IntegratedLufs) || double.IsNaN(s.TruePeakDbtp));
    }

    private static void Feed(AudioMixer m, LoudnessMeter? reference, int frames, int step = 480)
    {
        var o = new float[m.Channels * step];
        for (int done = 0; done < frames; done += step)
        {
            int n = Math.Min(step, frames - done);
            m.Render(o.AsSpan(0, n * m.Channels));
            reference?.Process(o.AsSpan(0, n * m.Channels));
        }
    }

    private static void AssertSameLoudness(LoudnessSnapshot got, LoudnessMeter want, double tolerance = 0.1)
    {
        Assert.Equal(want.MomentaryLufs, got.MomentaryLufs, tolerance);
        Assert.Equal(want.ShortTermLufs, got.ShortTermLufs, tolerance);
        Assert.Equal(want.IntegratedLufs, got.IntegratedLufs, tolerance);
        Assert.Equal(want.TruePeakDbtp, got.TruePeakDbtp, tolerance);
        Assert.Equal(want.FramesProcessed, got.FramesMeasured);
    }

    [Fact]
    public void BusMeter_MatchesAStandaloneMeterOnThatBusAlone_WhileOtherBusesPlay()
    {
        // Mixer A: music on bus 1 (metered), effects on bus 2 and a quieter voice on bus 3. Mixer B: only the music voice, its output measured outside.
        var music = Sine(440, 0.1, 2);
        var a = new AudioMixer(Options());
        var b = new AudioMixer(Options());
        a.SetBusFilter(1, FilterType.LowPass, 3000);          // the tap is after effects and gain
        b.SetBusFilter(1, FilterType.LowPass, 3000);
        a.SetBusGain(1, 0.7f);
        b.SetBusGain(1, 0.7f);
        a.Play(new PlayParams(music) { Bus = 1, Loop = true });
        b.Play(new PlayParams(music) { Bus = 1, Loop = true });
        a.Play(new PlayParams(Noise(0.5, 1)) { Bus = 2, Loop = true });
        a.Play(new PlayParams(Sine(1000, 0.3)) { Bus = 3, Loop = true });
        Assert.True(a.EnableBusMeter(1));
        Assert.True(a.EnableBusMeter(AudioMixer.MasterBus));

        var refBus = new LoudnessMeter(Rate, 2, ChannelMask.None, truePeak: true, loudnessRange: false);
        var refMaster = new LoudnessMeter(Rate, 2, ChannelMask.None, truePeak: true, loudnessRange: false);
        var oa = new float[2 * 480];
        for (int pass = 0; pass < 600; pass++)     // 6 s in 10 ms callbacks
        {
            a.Render(oa);
            refMaster.Process(oa);
            var ob = new float[2 * 480];
            b.Render(ob);
            refBus.Process(ob);
        }
        Assert.True(a.TryGetBusLoudness(1, out var bus));
        Assert.True(a.TryGetBusLoudness(AudioMixer.MasterBus, out var master));
        Console.WriteLine($"bus 1: M {bus.MomentaryLufs:F2} S {bus.ShortTermLufs:F2} I {bus.IntegratedLufs:F2} TP {bus.TruePeakDbtp:F2} (standalone M {refBus.MomentaryLufs:F2} S {refBus.ShortTermLufs:F2} I {refBus.IntegratedLufs:F2} TP {refBus.TruePeakDbtp:F2}); master I {master.IntegratedLufs:F2}");
        Assert.True(bus.IntegratedLufs > -40 && bus.IntegratedLufs < -10);
        AssertSameLoudness(bus, refBus);
        AssertSameLoudness(master, refMaster);
        Assert.True(master.IntegratedLufs > bus.IntegratedLufs + 1, "the master carries the other buses too");
        Assert.False(a.TryGetBusLoudness(2, out _));          // not metered
    }

    [Fact]
    public void BusMeter_IsIndependentOfRenderBlockSizes_AndSeesSilenceWhenTheBusIsIdle()
    {
        var a = new AudioMixer(Options());
        var b = new AudioMixer(Options());
        var clip = Sine(300, 0.2);
        var ha = a.Play(new PlayParams(clip) { Bus = 2, Loop = true });
        var hb = b.Play(new PlayParams(clip) { Bus = 2, Loop = true });
        a.EnableBusMeter(2);
        b.EnableBusMeter(2);
        Feed(a, null, Rate * 2, 1000);
        Feed(b, null, Rate * 2, 37);
        a.TryGetBusLoudness(2, out var sa);
        b.TryGetBusLoudness(2, out var sb);
        Assert.Equal(sa.MomentaryLufs, sb.MomentaryLufs, 1e-6);
        Assert.Equal(sa.ShortTermLufs, sb.ShortTermLufs, 1e-6);
        Assert.Equal(sa.TruePeakDbtp, sb.TruePeakDbtp, 1e-6);
        Assert.Equal(Rate * 2, sa.FramesMeasured);

        a.Stop(ha);
        Feed(a, null, Rate, 480);                              // a second of idle bus: the 400 ms window is silent, the 3 s window is not
        a.TryGetBusLoudness(2, out var after);
        Assert.Equal(double.NegativeInfinity, after.MomentaryLufs);
        Assert.True(after.ShortTermLufs > double.NegativeInfinity);
        Assert.Equal(Rate * 3, after.FramesMeasured);
        Assert.InRange(after.IntegratedLufs, sa.IntegratedLufs - 1.0, sa.IntegratedLufs);   // silent blocks are gated out; only the fade-out blocks pull it down a little
    }

    [Fact]
    public void BusMeter_EnableDisableAndReset_WorkMidStream()
    {
        var m = new AudioMixer(Options(meters: 2));
        m.Play(new PlayParams(Sine(440, 0.1)) { Bus = 1, Loop = true });
        Feed(m, null, Rate);
        Assert.False(m.TryGetBusLoudness(1, out var none));
        Assert.Equal(LoudnessSnapshot.Empty, none);

        Assert.True(m.EnableBusMeter(1));
        Assert.True(m.TryGetBusLoudness(1, out var fresh));            // enabled, not yet measured
        Assert.Equal(LoudnessSnapshot.Empty, fresh);
        var reference = new LoudnessMeter(Rate, 2, ChannelMask.None, true, false);
        Feed(m, reference, Rate * 2);
        m.TryGetBusLoudness(1, out var s1);
        AssertSameLoudness(s1, reference);
        Assert.Equal(Rate * 2, s1.FramesMeasured);

        // Reset: the old readings are gone at once, and measuring starts over.
        Assert.True(m.ResetBusMeter(1));
        Assert.True(m.TryGetBusLoudness(1, out var cleared));
        Assert.Equal(LoudnessSnapshot.Empty, cleared);
        reference.Reset();
        Feed(m, reference, Rate / 2);
        m.TryGetBusLoudness(1, out var s2);
        AssertSameLoudness(s2, reference);
        Assert.Equal(Rate / 2, s2.FramesMeasured);

        // Disable frees the slot for another bus; the new bus never shows the old bus's readings.
        Assert.True(m.EnableBusMeter(2));
        Assert.Equal(0, m.FreeBusMeters);
        Assert.True(m.DisableBusMeter(1));
        Assert.False(m.TryGetBusLoudness(1, out _));
        Assert.Equal(1, m.FreeBusMeters);
        Assert.True(m.EnableBusMeter(3));
        Assert.True(m.TryGetBusLoudness(3, out var other));
        Assert.Equal(LoudnessSnapshot.Empty, other);
        Feed(m, null, 480);
        m.TryGetBusLoudness(3, out other);
        Assert.Equal(480, other.FramesMeasured);
        Assert.Equal(double.NegativeInfinity, other.IntegratedLufs);   // bus 3 is silent
        Assert.True(m.DisableBusMeter(1));                          // disabling twice is harmless
        Assert.True(m.ResetBusMeter(1));                            // so is resetting an unmetered bus
    }

    [Fact]
    public void BusMeter_Master_MeasuresTheLimitedOutput()
    {
        var o = Options(meters: 1);
        var m = new AudioMixer(o with { LimiterLookaheadMilliseconds = 5 });
        m.SetLimiter(new LimiterSettings { CeilingDb = -6 });
        m.Play(new PlayParams(Sine(200, 0.9)) { Loop = true });
        m.EnableBusMeter(AudioMixer.MasterBus);
        Feed(m, null, Rate * 2);
        m.TryGetBusLoudness(AudioMixer.MasterBus, out var s);
        Assert.True(s.TruePeakDbtp < -5.0, $"true peak {s.TruePeakDbtp}");   // the ceiling is -6 dB on samples
    }

    [Fact]
    public void Meters_AreOptIn_AndOptionsAreValidated()
    {
        var m = new AudioMixer(new MixerOptions());
        Assert.Equal(0, m.FreeBusMeters);
        Assert.Throws<InvalidOperationException>(() => m.EnableBusMeter(0));
        Assert.False(m.TryGetBusLoudness(0, out _));
        Assert.Throws<ArgumentOutOfRangeException>(() => m.EnableBusMeter(8));
        Assert.Throws<ArgumentOutOfRangeException>(() => m.TryGetBusLoudness(-2, out _));
        Assert.Throws<ArgumentOutOfRangeException>(() => new AudioMixer(new MixerOptions { MaxBusMeters = 65 }));
        Assert.Throws<ArgumentOutOfRangeException>(() => new AudioMixer(new MixerOptions { MaxBusMeters = 1, SampleRate = 2000 }));
        Assert.Equal(2000, new AudioMixer(new MixerOptions { SampleRate = 2000 }).SampleRate);   // without meters any rate remains fine
    }

    [Fact]
    public void MeterSlotsFull_ThrowsNamingTheCap_DistinctFromAFullQueue()
    {
        var m = new AudioMixer(Options(meters: 1));
        Assert.True(m.EnableBusMeter(0));
        Assert.True(m.EnableBusMeter(0));                          // already metered: fine
        var ex = Assert.Throws<InvalidOperationException>(() => m.EnableBusMeter(1));
        Assert.Contains("MaxBusMeters", ex.Message);
        Assert.Equal(0, m.DroppedCommands);                        // not counted as a dropped command
    }

    [Fact]
    public void ReverbSlotsFull_ThrowsNamingTheCap_DistinctFromAFullQueue()
    {
        var m = new AudioMixer(new MixerOptions { MaxReverbs = 1, CommandCapacity = 16 });
        Assert.Equal(1, m.FreeReverbSlots);
        Assert.True(m.SetBusReverb(0, new ReverbSettings()));
        Assert.Equal(0, m.FreeReverbSlots);
        long dropped = m.DroppedCommands;
        var ex = Assert.Throws<InvalidOperationException>(() => m.SetBusReverb(1, new ReverbSettings()));
        Assert.Contains("MaxReverbs", ex.Message);
        Assert.Equal(dropped, m.DroppedCommands);                  // a limit, not a dropped command
        Assert.True(m.SetBusReverb(0, new ReverbSettings { Wet = 0.2f }));   // retuning needs no free slot
        Assert.Throws<InvalidOperationException>(() => new AudioMixer(new MixerOptions { MaxReverbs = 0 }).SetBusReverb(0, new ReverbSettings()));
        Assert.True(m.ClearBusReverb(0));
        Assert.Equal(1, m.FreeReverbSlots);
    }

    [Fact]
    public void FullQueue_ReturnsFalse_AndCountsIt_ForReverbAndMeters_WithoutThrowing()
    {
        var m = new AudioMixer(new MixerOptions { MaxReverbs = 2, MaxBusMeters = 2, CommandCapacity = 16 });
        int queued = 0;
        while (m.SetMasterGain(0.5f)) queued++;                    // nothing renders, so the ring fills
        Assert.Equal(16, queued);
        long dropped = m.DroppedCommands;
        Assert.False(m.SetBusReverb(0, new ReverbSettings()));     // a free slot, a full queue
        Assert.False(m.EnableBusMeter(0));
        Assert.Equal(dropped + 2, m.DroppedCommands);
        Assert.Equal(2, m.FreeReverbSlots);                        // nothing was taken
        Assert.Equal(2, m.FreeBusMeters);
        Assert.False(m.TryGetBusLoudness(0, out _));
        m.Render(new float[2 * 64]);                               // drains the ring
        Assert.True(m.SetBusReverb(0, new ReverbSettings()));
        Assert.True(m.EnableBusMeter(0));
        Assert.True(m.TryGetBusLoudness(0, out _));
    }

    [Fact]
    public void TryEnable_SlotsExhausted_ReturnsFalseWithoutThrowingOrDropping()
    {
        var m = new AudioMixer(Options(meters: 1));
        Assert.True(m.TryEnableBusMeter(0));
        Assert.True(m.TryEnableBusMeter(0));                       // already metered: true, no slot needed
        Assert.Equal(0, m.FreeBusMeters);
        long dropped = m.DroppedCommands;
        Assert.False(m.TryEnableBusMeter(1));
        Assert.False(m.TryEnableBusMeter(AudioMixer.MasterBus));
        Assert.Equal(dropped, m.DroppedCommands);
        Assert.False(m.IsBusMetered(1));
        Assert.True(m.DisableBusMeter(0));
        Assert.True(m.TryEnableBusMeter(1));
        Assert.True(m.IsBusMetered(1));
    }

    [Fact]
    public void TryEnable_ZeroMeters_ReturnsFalse()
    {
        var m = new AudioMixer(Options(meters: 0));
        Assert.False(m.TryEnableBusMeter(0));
        Assert.False(m.IsBusMetered(0));
        Assert.Equal(0, m.FreeBusMeters);
    }

    [Fact]
    public void TryEnable_FullQueue_ReturnsFalseWithFreeSlotAndCountsDrop()
    {
        var m = new AudioMixer(Options(meters: 2));
        while (m.SetMasterGain(0.5f)) { }
        long dropped = m.DroppedCommands;
        Assert.False(m.TryEnableBusMeter(0));
        Assert.Equal(dropped + 1, m.DroppedCommands);
        Assert.Equal(2, m.FreeBusMeters);                          // free slot + false = queue was full
        Assert.False(m.IsBusMetered(0));
        m.Render(new float[2 * 64]);
        Assert.True(m.TryEnableBusMeter(0));
        Assert.True(m.IsBusMetered(0));
    }

    [Fact]
    public void IsBusMetered_FollowsEnableDisable_AndChecksRange()
    {
        var m = new AudioMixer(Options(meters: 2));
        Assert.False(m.IsBusMetered(2));
        Assert.False(m.IsBusMetered(AudioMixer.MasterBus));
        Assert.True(m.EnableBusMeter(2));
        Assert.True(m.EnableBusMeter(AudioMixer.MasterBus));
        Assert.True(m.IsBusMetered(2));
        Assert.True(m.IsBusMetered(AudioMixer.MasterBus));
        Assert.True(m.DisableBusMeter(2));
        Assert.False(m.IsBusMetered(2));
        Assert.True(m.IsBusMetered(AudioMixer.MasterBus));
        Assert.Throws<ArgumentOutOfRangeException>(() => m.IsBusMetered(4));
        Assert.Throws<ArgumentOutOfRangeException>(() => m.TryEnableBusMeter(-2));
        Assert.True(m.EnableBusMeter(0));
        Assert.Throws<InvalidOperationException>(() => m.EnableBusMeter(1));   // EnableBusMeter keeps throwing
    }

    [Fact]
    public void SeqLock_NeverShowsATornValue()
    {
        var slot = new SeqLockSlot<Quad>();
        slot.Write(new Quad(0, 0, 0, 0));
        using var stop = new CancellationTokenSource();
        long torn = 0, reads = 0;
        var readers = Enumerable.Range(0, 3).Select(_ => Task.Run(() =>
        {
            long last = -1;
            while (!stop.IsCancellationRequested)
            {
                var q = slot.Read();
                if (q.A != q.B || q.B != q.C || q.C != q.D) Interlocked.Increment(ref torn);
                if (q.A < last) Interlocked.Increment(ref torn);     // never goes back in time
                last = q.A;
                Interlocked.Increment(ref reads);
            }
        })).ToArray();
        for (long i = 1; i <= 3_000_000; i++) slot.Write(new Quad(i, i, i, i));
        stop.Cancel();
        Task.WaitAll(readers);
        Console.WriteLine($"seqlock: {reads} reads against 3000000 writes, {torn} torn");
        Assert.Equal(0, torn);
        Assert.True(reads > 0);
    }

    private readonly record struct Quad(long A, long B, long C, long D);

    [Fact]
    public void Render_AllocatesNothing_WithMetersEnabledAndToggling()
    {
        var m = new AudioMixer(Options(meters: 3, channels: 2) with { MaxBlockFrames = 512 });
        var clip = Sine(440, 0.2, 2);
        for (int i = 0; i < 4; i++) m.Play(new PlayParams(clip) { Bus = i, Loop = true, Gain = 0.2f });
        var o = new float[2 * 480];
        void Control(int i)
        {
            int bus = i % 5 - 1;
            try
            {
                switch (i % 4)
                {
                    case 0: m.EnableBusMeter(bus); break;
                    case 1: m.ResetBusMeter(bus); break;
                    case 2: m.DisableBusMeter((i / 4) % 5 - 1); break;
                    default: if (m.TryGetBusLoudness(bus, out var s)) AssertConsistent(s); break;
                }
            }
            catch (InvalidOperationException) { }
        }
        for (int i = 0; i < 2000; i++) { Control(i); m.Render(o); }
        int gcs = GC.CollectionCount(0) + GC.CollectionCount(1) + GC.CollectionCount(2);
        long allocated = 0;
        for (int i = 2000; i < 10000; i++)
        {
            Control(i);
            long b = GC.GetAllocatedBytesForCurrentThread();
            m.Render(o);
            allocated += GC.GetAllocatedBytesForCurrentThread() - b;
        }
        Console.WriteLine($"render with 3 bus meters toggling: {allocated} bytes over 8000 callbacks");
        Assert.Equal(0, allocated);
    }
}
