using OneAudio.Effects;
using OneAudio.Mixing;
using Xunit;

namespace OneAudio.Tests;

public class MixerWarmUpTests
{
    private static AudioClip Tone(int rate, int channels, int frames)
    {
        var s = new float[frames * channels];
        for (int i = 0; i < frames; i++)
            for (int c = 0; c < channels; c++) s[i * channels + c] = 0.25f * MathF.Sin(i * 0.05f * (c + 1));
        return new AudioClip(new AudioFormat(rate, channels), s);
    }

    // The interpreter warm-up never runs on a JIT runtime by itself; forcing it must still work and must not touch other mixers.
    [Fact]
    public void ForcedWarmUp_RunsCleanly_AndLeavesOtherMixersAlone()
    {
        MixerWarmUp.Run(force: true);

        var m = new AudioMixer(new MixerOptions { SampleRate = 48000, Channels = 2, MaxBlockFrames = 32 });
        var clip = new AudioClip(new AudioFormat(48000, 1), [0.5f, 0.25f, 0.125f]);
        m.Play(new PlayParams(clip));
        var o = new float[2 * 8];
        m.Render(o);
        Assert.True(o[0] > 0f && o[0] > o[2] && o[2] > o[4]);
        Assert.Equal(0f, o[6]);
        Assert.False(m.TryGetEvent(out var e) && e.Kind != MixerEventKind.VoiceEnded);
    }

    // The same Play / loop / crossfade / stop / bus / effect sequence the browser-wasm check runs, here on the JIT runtime:
    // the render calls themselves must allocate nothing once the mixer has rendered a few blocks.
    [Fact]
    public void RenderAllocatesNothing_ThroughPlayStopLoopsCrossfadeAndBusChanges()
    {
        var m = new AudioMixer(new MixerOptions { MaxVoices = 4, MaxBuses = 3, MaxReverbs = 1, MaxBusMeters = 1, LimiterLookaheadMilliseconds = 2, MaxSourceChannels = 6 });
        var mono = Tone(48000, 1, 20000);
        var stereo = Tone(48000, 2, 20000);
        var surround = Tone(48000, 6, 8000);
        var slow = Tone(24000, 2, 20000);
        var o = new float[2 * 256];
        long allocated = 0;
        void Blocks(int n)
        {
            for (int i = 0; i < n; i++)
            {
                long before = GC.GetAllocatedBytesForCurrentThread();
                m.Render(o);
                allocated += GC.GetAllocatedBytesForCurrentThread() - before;
            }
        }

        Blocks(64);
        allocated = 0;
        int collections = GC.CollectionCount(0);

        var v1 = m.Play(new PlayParams(mono) { Pan = -0.5f });
        Blocks(8);
        var v2 = m.Play(new PlayParams(stereo) { Bus = 1, Loop = true, LoopStartFrame = 100, LoopEndFrame = 5000 });
        m.SetGain(v1, 0.2f, 512); m.SetPan(v1, 0.7f, 512);
        Blocks(8);
        var v3 = m.Play(new PlayParams(surround) { Bus = 2 });
        var v4 = m.Play(new PlayParams(slow) { Resampling = ResamplerQuality.PolyphaseFast, VariablePitch = true });
        m.SetPitch(v4, 1.4f, 1024);
        Blocks(16);
        m.Crossfade(v1, new PlayParams(mono), 1024);
        m.Stop(v2, 512); m.Stop(v3);
        Blocks(16);
        for (int i = 0; i < 6; i++) m.Play(new PlayParams(stereo) { Priority = i });
        m.SetBusGain(1, 0.5f, 256); m.SetBusMute(2, true); m.SetBusOutput(1, 2); m.SetMasterGain(0.9f);
        m.SetBusFilter(1, FilterType.LowPass, 4000f); m.SetVoiceFilter(v4, FilterType.LowPass, 3000f);
        m.SetBusReverb(0, new ReverbSettings()); m.SetDucking(0, 1); m.EnableBusMeter(0); m.SetLimiter(new LimiterSettings());
        Blocks(32);
        m.ClearBusReverb(0); m.ClearDucking(0); m.StopAll(256);
        Blocks(16);

        Assert.Equal(0, allocated);
        Assert.Equal(collections, GC.CollectionCount(0));
    }
}
