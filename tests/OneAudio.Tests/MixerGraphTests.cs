using Xunit;

namespace OneAudio.Tests;

public class MixerGraphTests
{
    private const int Rate = 48000;

    private static AudioClip Const(int frames, float value, int channels = 1)
    {
        var s = new float[frames * channels];
        Array.Fill(s, value);
        return new AudioClip(new AudioFormat(Rate, channels), s);
    }

    private static AudioMixer Mixer(int channels = 2, int buses = 8, double smoothMs = 10, int block = 128, ChannelMask mask = ChannelMask.None) =>
        new(new MixerOptions
        {
            SampleRate = Rate, Channels = channels, MaxVoices = 8, MaxBuses = buses, MaxBlockFrames = block,
            SmoothingMilliseconds = smoothMs, OutputChannelMask = mask,
        });

    private static float[] RenderFrames(AudioMixer m, int frames)
    {
        var o = new float[frames * m.Channels];
        m.Render(o);
        return o;
    }

    // ------------------------------------------------------------------ routing

    [Fact]
    public void SubMix_RoutesThroughBuses_InAnyIndexOrder()
    {
        var m = Mixer();
        // 0 -> 5 -> 3 -> master: a bus feeding a lower-numbered bus must still be processed first.
        Assert.True(m.SetBusOutput(0, 5));
        Assert.True(m.SetBusOutput(5, 3));
        foreach (int b in new[] { 0, 5, 3 }) m.SetBusGain(b, 0.5f, 0);
        m.Play(new PlayParams(Const(64, 1f, 2)));
        var o = RenderFrames(m, 16);
        Assert.All(o, x => Assert.Equal(0.125f, x, 1e-6f));
    }

    [Fact]
    public void BusOutput_BackToMaster_AndSiblingsSum()
    {
        var m = Mixer();
        m.SetBusOutput(1, 2);
        m.SetBusGain(2, 0.5f, 0);
        m.Play(new PlayParams(Const(64, 1f, 2)) { Bus = 1 });
        m.Play(new PlayParams(Const(64, 1f, 2)) { Bus = 2 });
        Assert.Equal(1.0f, RenderFrames(m, 4)[0], 1e-6f); // 0.5 + 0.5
        m.SetBusOutput(1, -1);
        Assert.Equal(1.5f, RenderFrames(m, 4)[0], 1e-6f); // 1.0 direct + 0.5 on bus 2
    }

    [Fact]
    public void BusLoops_AreRejected_OnControlSide()
    {
        var m = Mixer();
        Assert.True(m.SetBusOutput(1, 2));
        Assert.True(m.SetBusOutput(2, 3));
        Assert.Throws<ArgumentException>(() => m.SetBusOutput(3, 1));
        Assert.Throws<ArgumentException>(() => m.SetBusOutput(3, 3));
        Assert.Throws<ArgumentException>(() => m.SetBusOutput(2, 1));
        Assert.Throws<ArgumentOutOfRangeException>(() => m.SetBusOutput(1, 99));
        Assert.Throws<ArgumentOutOfRangeException>(() => m.SetBusOutput(-1, 0));
        // A ducking link is an edge too: 3 ducked by 1 would close 1 -> 2 -> 3 -> 1.
        Assert.Throws<ArgumentException>(() => m.SetDucking(1, 3));
        Assert.Throws<ArgumentException>(() => m.SetDucking(1, 1));
        Assert.True(m.SetDucking(3, 1)); // same direction as the routing: fine
        // The rejected calls changed nothing: rendering still works and 1 -> 2 -> 3 is intact.
        m.ClearDucking(3);
        m.Play(new PlayParams(Const(64, 1f, 2)) { Bus = 1 });
        Assert.Equal(1f, RenderFrames(m, 8)[0], 1e-6f);
    }

    [Fact]
    public void BusMute_RampsToSilence_AndBack_KeepingGain()
    {
        var m = Mixer(smoothMs: 1); // 48 frames
        m.SetBusGain(1, 0.5f, 0);
        m.Play(new PlayParams(Const(4000, 1f, 2)) { Bus = 1 });
        Assert.Equal(0.5f, RenderFrames(m, 8)[0], 1e-6f);
        m.SetBusMute(1, true);
        var o = RenderFrames(m, 128);
        Assert.True(o[0] < 0.5f && o[0] > 0.45f);
        for (int i = 1; i < 48; i++) Assert.True(o[2 * i] <= o[2 * (i - 1)]);
        Assert.Equal(0f, o[2 * 100]);
        m.SetBusMute(1, false);
        RenderFrames(m, 128);
        Assert.Equal(0.5f, RenderFrames(m, 8)[0], 1e-6f);
    }

    [Fact]
    public void MasterGain_AppliesAfterBuses()
    {
        var m = Mixer();
        m.SetMasterGain(0.25f);
        m.Play(new PlayParams(Const(4000, 1f, 2)));
        var o = RenderFrames(m, 1024);
        Assert.Equal(0.25f, o[^1], 1e-6f);
    }

    // ------------------------------------------------------------------ smoothing

    [Fact]
    public void BusGain_Change_Ramps_WithoutSteps()
    {
        var m = Mixer(smoothMs: 1); // 48 frames
        m.Play(new PlayParams(Const(20000, 1f, 2)));
        RenderFrames(m, 64);
        m.SetBusGain(0, 0.2f);
        var o = RenderFrames(m, 128);
        float maxStep = 0;
        for (int i = 1; i < 128; i++) maxStep = Math.Max(maxStep, Math.Abs(o[2 * i] - o[2 * (i - 1)]));
        Assert.True(maxStep < 0.8f / 48 + 1e-4f, $"step {maxStep}");
        Assert.Equal(0.2f, o[2 * 127], 1e-6f);
        Assert.True(o[0] > 0.9f);
    }

    [Fact]
    public void SetPan_Ramps_WithoutSteps_AndCanJump()
    {
        var m = Mixer(smoothMs: 1);
        var v = m.Play(new PlayParams(Const(20000, 1f, 2)) { Pan = -1f });
        RenderFrames(m, 64);
        m.SetPan(v, 1f);
        var o = RenderFrames(m, 128);
        float maxStep = 0;
        for (int i = 1; i < 128; i++) maxStep = Math.Max(maxStep, Math.Abs(o[2 * i] - o[2 * (i - 1)]));
        Assert.True(maxStep < 1.0f / 48 + 1e-4f, $"step {maxStep}");
        Assert.Equal(0f, o[2 * 127], 1e-6f); // left side fully attenuated
        Assert.Equal(1f, o[2 * 127 + 1], 1e-6f);
        m.SetPan(v, -1f, 0);
        var o2 = RenderFrames(m, 4);
        Assert.Equal(1f, o2[0], 1e-6f);
        Assert.Equal(0f, o2[1], 1e-6f);
    }

    // ------------------------------------------------------------------ ducking

    [Fact]
    public void Ducking_LowersTarget_WhileSourceIsLoud_ThenReleases()
    {
        var m = Mixer(block: 64);
        // Music (bus 1) is constant 0.5; dialogue (bus 2) is silent for 2000 frames, loud for 4000, then silent.
        var dialogue = new float[2 * 12000];
        for (int i = 2000; i < 6000; i++) { dialogue[2 * i] = 0.3f; dialogue[2 * i + 1] = 0.3f; }
        Assert.True(m.SetDucking(1, 2, new DuckingSettings { ThresholdDb = -20, ReductionDb = 12, AttackMs = 1, ReleaseMs = 20 }));
        m.Play(new PlayParams(Const(20000, 0.5f, 2)) { Bus = 1 });
        m.Play(new PlayParams(new AudioClip(new AudioFormat(Rate, 2), dialogue)) { Bus = 2 });
        var o = RenderFrames(m, 12000);
        float duckedGain = (float)Math.Pow(10, -12 / 20.0);
        // Total = music + dialogue. Before the dialogue: music untouched.
        Assert.Equal(0.5f, o[2 * 1000], 1e-5f);
        // Well inside the dialogue (attack long done): music at 0.5 * 0.251 + dialogue 0.3.
        Assert.Equal(0.5f * duckedGain + 0.3f, o[2 * 5500], 0.003f);
        // Long after: back to 0.5.
        Assert.Equal(0.5f, o[2 * 11500], 0.003f);
        // Never louder than undisturbed music at any frame outside the dialogue.
        for (int i = 0; i < 2000; i++) Assert.Equal(0.5f, o[2 * i], 1e-5f);
    }

    [Fact]
    public void Ducking_Cleared_ReleasesSmoothly()
    {
        var m = Mixer(block: 64);
        m.Play(new PlayParams(Const(40000, 0.5f, 2)) { Bus = 1 });
        m.Play(new PlayParams(Const(40000, 0.3f, 2)) { Bus = 2 });
        m.SetDucking(1, 2, new DuckingSettings { ThresholdDb = -20, ReductionDb = 12, AttackMs = 1, ReleaseMs = 20 });
        RenderFrames(m, 4000);
        m.ClearDucking(1);
        var o = RenderFrames(m, 8000);
        float prev = o[0];
        float maxStep = 0;
        for (int i = 1; i < 8000; i++) { maxStep = Math.Max(maxStep, Math.Abs(o[2 * i] - prev)); prev = o[2 * i]; }
        Assert.True(maxStep < 0.01f, $"step {maxStep}");
        Assert.Equal(0.8f, o[2 * 7999], 1e-4f);
    }

    [Fact]
    public void Ducking_ValidatesSettings()
    {
        var m = Mixer();
        Assert.Throws<ArgumentOutOfRangeException>(() => m.SetDucking(1, 2, new DuckingSettings { ReductionDb = -1 }));
        Assert.Throws<ArgumentOutOfRangeException>(() => m.SetDucking(1, 2, new DuckingSettings { AttackMs = float.NaN }));
        Assert.Throws<ArgumentOutOfRangeException>(() => m.SetDucking(9, 2));
    }

    // ------------------------------------------------------------------ multichannel mapping

    private static float[] Channels(params float[] perChannel) => perChannel;

    private static float[] PlayOneFrame(int outCh, ChannelMask outMask, int srcCh, ChannelMask srcMask, float[] frame, float pan = 0f)
    {
        var m = new AudioMixer(new MixerOptions
        {
            SampleRate = Rate, Channels = outCh, OutputChannelMask = outMask, MaxVoices = 2, MaxSourceChannels = 16, MaxBlockFrames = 16,
        });
        m.Play(new PlayParams(new AudioClip(new AudioFormat(Rate, srcCh, srcMask), frame)) { Pan = pan });
        var o = new float[outCh * 2];
        m.Render(o);
        return o[..outCh];
    }

    private const float R2 = 0.70710678f;

    [Fact]
    public void Mono_PansAcrossFrontPair_OnSurroundOutput()
    {
        var o = PlayOneFrame(6, ChannelMask.None, 1, ChannelMask.None, [1f]);
        Assert.Equal(R2, o[0], 1e-6f);
        Assert.Equal(R2, o[1], 1e-6f);
        Assert.Equal(0f, o[2]); Assert.Equal(0f, o[3]); Assert.Equal(0f, o[4]); Assert.Equal(0f, o[5]);
        var hard = PlayOneFrame(6, ChannelMask.None, 1, ChannelMask.None, [1f], pan: 1f);
        Assert.Equal(0f, hard[0], 1e-6f);
        Assert.Equal(1f, hard[1], 1e-6f);
    }

    [Fact]
    public void Mono_ToMono_IsUnity_AndStereoToMono_IsHalfSum()
    {
        Assert.Equal(0.8f, PlayOneFrame(1, ChannelMask.None, 1, ChannelMask.None, [0.8f])[0], 1e-6f);
        Assert.Equal(0.5f, PlayOneFrame(1, ChannelMask.None, 2, ChannelMask.None, [0.8f, 0.2f])[0], 1e-6f);
    }

    [Fact]
    public void Stereo_OnSurround_PlaysFrontLeftRightOnly()
    {
        var o = PlayOneFrame(6, ChannelMask.None, 2, ChannelMask.None, [0.5f, -0.25f]);
        Assert.Equal([0.5f, -0.25f, 0f, 0f, 0f, 0f], o);
    }

    [Fact]
    public void FivePointOne_ToStereo_FoldsCentreAndSurrounds_DropsLfe()
    {
        // order: FL FR FC LFE BL BR
        var o = PlayOneFrame(2, ChannelMask.None, 6, ChannelMask.None, [0.1f, 0.2f, 0.4f, 0.8f, 0.01f, 0.02f]);
        Assert.Equal(0.1f + 0.4f * R2 + 0.01f * R2, o[0], 1e-6f);
        Assert.Equal(0.2f + 0.4f * R2 + 0.02f * R2, o[1], 1e-6f);
    }

    [Fact]
    public void FivePointOne_ToFivePointOne_IsIdentity_WithBalance()
    {
        var f = new[] { 0.1f, 0.2f, 0.3f, 0.4f, 0.5f, 0.6f };
        Assert.Equal(f, PlayOneFrame(6, ChannelMask.None, 6, ChannelMask.None, f));
        var o = PlayOneFrame(6, ChannelMask.None, 6, ChannelMask.None, f, pan: 0.5f);
        // Pan right halves the left-side speakers (FL, BL); centre, LFE and the right side are untouched.
        Assert.Equal(new[] { 0.05f, 0.2f, 0.3f, 0.4f, 0.25f, 0.6f }, o.Select(x => MathF.Round(x, 5)).ToArray());
    }

    [Fact]
    public void SevenPointOne_ToFivePointOne_FoldsSidesIntoBack()
    {
        // 7.1 order: FL FR FC LFE BL BR SL SR
        var o = PlayOneFrame(6, ChannelMask.None, 8, ChannelMask.None, [0, 0, 0, 0, 0.2f, 0.4f, 0.1f, 0.3f]);
        Assert.Equal(0.2f + 0.1f * R2, o[4], 1e-6f);
        Assert.Equal(0.4f + 0.3f * R2, o[5], 1e-6f);
    }

    [Fact]
    public void FivePointOne_SideVariant_ToBackOutput_MapsSidesToBack()
    {
        var side51 = ChannelMask.FrontLeft | ChannelMask.FrontRight | ChannelMask.FrontCenter | ChannelMask.LowFrequency | ChannelMask.SideLeft | ChannelMask.SideRight;
        var o = PlayOneFrame(6, ChannelMask.None, 6, side51, [0, 0, 0, 0, 0.3f, 0.6f]);
        Assert.Equal(0.3f, o[4], 1e-6f);
        Assert.Equal(0.6f, o[5], 1e-6f);
    }

    [Fact]
    public void Stereo_ToSevenPointOne_AndUnknownLayouts_MapByIndex()
    {
        var o = PlayOneFrame(8, ChannelMask.None, 2, ChannelMask.None, [0.3f, 0.4f]);
        Assert.Equal(0.3f, o[0]); Assert.Equal(0.4f, o[1]);
        for (int i = 2; i < 8; i++) Assert.Equal(0f, o[i]);
        // 5 channels has no conventional layout: by index, extras dropped.
        var five = PlayOneFrame(2, ChannelMask.None, 5, ChannelMask.None, [1f, 2f, 3f, 4f, 5f]);
        Assert.Equal([1f, 2f], five);
        var nine = PlayOneFrame(3, ChannelMask.None, 9, ChannelMask.None, new float[] { 1, 2, 3, 4, 5, 6, 7, 8, 9 });
        Assert.Equal([1f, 2f, 3f], nine);
    }

    [Fact]
    public void MixerOptions_ExplicitOutputMask_IsUsed()
    {
        // A 3-channel L R C output: a stereo source plays on L and R, mono source pans across L/R.
        var mask = ChannelMask.FrontLeft | ChannelMask.FrontRight | ChannelMask.FrontCenter;
        var o = PlayOneFrame(3, mask, 2, ChannelMask.None, [0.5f, 0.25f]);
        Assert.Equal([0.5f, 0.25f, 0f], o);
        var f = PlayOneFrame(3, mask, 6, ChannelMask.None, [0, 0, 1f, 1f, 0, 0]);
        Assert.Equal([0f, 0f, 1f], f); // centre goes to centre; LFE dropped
    }

    [Fact]
    public void Multichannel_StartAtFrame_IsFrameExact()
    {
        var m = Mixer(channels: 6);
        var s = new float[6 * 5];
        Array.Fill(s, 1f);
        m.Play(new PlayParams(new AudioClip(new AudioFormat(Rate, 6), s)) { StartAtFrame = 130 });
        var o = RenderFrames(m, 200);
        for (int f = 0; f < 200; f++)
        {
            bool on = f >= 130 && f < 135;
            Assert.Equal(on ? 1f : 0f, o[f * 6], 1e-6f);
            Assert.Equal(on ? 1f : 0f, o[f * 6 + 5], 1e-6f);
        }
    }

    [Fact]
    public void SourceChannelLimit_IsEnforced()
    {
        var m = new AudioMixer(new MixerOptions { MaxSourceChannels = 2 });
        Assert.Throws<ArgumentException>(() => m.Play(new PlayParams(Const(8, 0f, 3))));
        Assert.Throws<ArgumentOutOfRangeException>(() => new AudioMixer(new MixerOptions { MaxSourceChannels = 33 }));
    }

    [Fact]
    public void NonFiniteParameters_AreRejected()
    {
        var m = Mixer();
        var v = m.Play(new PlayParams(Const(8, 0f)));
        Assert.Throws<ArgumentOutOfRangeException>(() => m.SetGain(v, float.NaN));
        Assert.Throws<ArgumentOutOfRangeException>(() => m.SetPan(v, float.PositiveInfinity));
        Assert.Throws<ArgumentOutOfRangeException>(() => m.SetBusGain(0, float.NaN));
        Assert.Throws<ArgumentOutOfRangeException>(() => m.SetMasterGain(-1));
        Assert.Throws<ArgumentOutOfRangeException>(() => m.SetPitch(v, 0f));
        Assert.Throws<ArgumentOutOfRangeException>(() => m.Play(new PlayParams(Const(8, 0f)) { Pitch = 100f }));
    }
}
