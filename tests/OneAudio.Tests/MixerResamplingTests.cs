using Xunit;

namespace OneAudio.Tests;

public class MixerResamplingTests
{
    private const int Rate = 48000;

    private static AudioMixer Mixer(int channels = 1, int block = 64, int voices = 8, Func<int, ResamplerQuality, IResampler>? factory = null) =>
        new(new MixerOptions
        {
            SampleRate = Rate, Channels = channels, MaxVoices = voices, MaxBlockFrames = block,
            ResamplerFactory = factory ?? ((ch, q) => new LinearTestResampler(ch, q)),
            PreallocatedConverters = [],
        });

    private static AudioClip RampClip(int rate, int frames, int channels = 1)
    {
        var s = new float[frames * channels];
        for (int i = 0; i < frames; i++)
            for (int c = 0; c < channels; c++) s[i * channels + c] = (i + 1) * (c + 1) / 1000f;
        return new AudioClip(new AudioFormat(rate, channels), s);
    }

    private static float[] Render(AudioMixer m, int frames)
    {
        var o = new float[frames * m.Channels];
        m.Render(o);
        return o;
    }

    [Fact]
    public void DifferentRate_IsConverted_UpToMixerRate()
    {
        var m = Mixer();
        var v = m.Play(new PlayParams(RampClip(24000, 200)));
        var o = Render(m, 500);
        // Linear interpolation of a ramp at ratio 2: output k is the ramp at k/2 (the first output equals the first input).
        for (int k = 0; k < 390; k++) Assert.Equal((k / 2.0f + 1) / 1000f, o[k], 2e-6f);
        // About 2x the frames came out, then the voice ended.
        int nonZero = o.Count(x => x != 0f);
        Assert.InRange(nonZero, 396, 400);
        Assert.Equal(0, m.ActiveVoices);
        Assert.True(m.TryGetEvent(out var e));
        Assert.Equal(MixerEventKind.VoiceEnded, e.Kind);
        Assert.Equal(v.Id, e.VoiceId);
    }

    [Fact]
    public void DifferentRate_DownToMixerRate_AndAcrossPassBoundaries()
    {
        var m = Mixer(block: 16); // many small passes
        m.Play(new PlayParams(RampClip(96000, 1000)));
        var o = Render(m, 400);
        for (int k = 0; k < 400; k++) Assert.Equal((2f * k + 1) / 1000f, o[k], 2e-6f);
    }

    [Fact]
    public void Pitch_ScalesSpeed_AndSetPitch_ChangesItMidVoice()
    {
        var m = Mixer();
        var v = m.Play(new PlayParams(RampClip(Rate, 4000)) { Pitch = 2f });
        var o = Render(m, 100);
        for (int k = 0; k < 100; k++) Assert.Equal((2f * k + 1) / 1000f, o[k], 2e-6f);

        m.SetPitch(v, 0.5f);
        var o2 = Render(m, 200);
        // Slope in the new region is half a source frame per output frame.
        float slope = (o2[199] - o2[100]) / 99f;
        Assert.Equal(0.5f / 1000f, slope, 1e-6f);
    }

    [Fact]
    public void SetPitch_Glide_IsContinuous()
    {
        var m = Mixer();
        var v = m.Play(new PlayParams(RampClip(Rate, 20000)) { VariablePitch = true });
        Render(m, 128);
        m.SetPitch(v, 2f, rampFrames: 512);
        var o = Render(m, 1024);
        for (int k = 1; k < 1024; k++)
        {
            float d = o[k] - o[k - 1];
            Assert.InRange(d, 0.999f / 1000, 2.001f / 1000); // speed moves between 1x and 2x; the signal never jumps
        }
    }

    [Fact]
    public void SetPitch_OnVoiceWithoutConverter_IsIgnored()
    {
        var m = Mixer();
        var v = m.Play(new PlayParams(RampClip(Rate, 4000)));
        m.SetPitch(v, 2f);
        var o = Render(m, 10);
        for (int k = 0; k < 10; k++) Assert.Equal((k + 1) / 1000f, o[k], 1e-7f);
    }

    [Fact]
    public void Stereo_And_Multichannel_Convert_PerChannel()
    {
        var m = Mixer(channels: 2);
        m.Play(new PlayParams(RampClip(24000, 100, channels: 2)));
        var o = Render(m, 100);
        for (int k = 0; k < 90; k++)
        {
            float x = (k / 2.0f + 1) / 1000f;
            Assert.Equal(x, o[2 * k], 2e-6f);
            Assert.Equal(2 * x, o[2 * k + 1], 4e-6f);
        }

        var m6 = Mixer(channels: 6);
        m6.Play(new PlayParams(RampClip(24000, 100, channels: 6)));
        var o6 = Render(m6, 100);
        for (int k = 0; k < 90; k++)
        {
            float x = (k / 2.0f + 1) / 1000f;
            Assert.Equal(x, o6[6 * k], 2e-6f);
            Assert.Equal(6 * x, o6[6 * k + 5], 2e-5f);
        }
    }

    [Fact]
    public void StartAtFrame_IsExact_ForConvertedVoices()
    {
        var m = Mixer(block: 32);
        m.Play(new PlayParams(RampClip(24000, 50)) { StartAtFrame = 77 });
        var o = Render(m, 200);
        for (int k = 0; k < 77; k++) Assert.Equal(0f, o[k]);
        Assert.Equal(1 / 1000f, o[77], 1e-7f);
        Assert.Equal(1.5f / 1000f, o[78], 1e-6f);
    }

    [Fact]
    public void Looping_ConvertedVoice_StaysContinuous()
    {
        var m = Mixer();
        // A 10-frame ramp at 24 kHz looping: the converter sees one endless periodic signal.
        m.Play(new PlayParams(RampClip(24000, 10)) { Loop = true });
        var o = Render(m, 400);
        Assert.Equal(1, m.ActiveVoices);
        // 10 source frames = 20 output frames per period.
        for (int k = 0; k < 360; k++) Assert.Equal(o[k], o[k + 20], 1e-5f);
    }

    [Fact]
    public void StreamingSource_AtOtherRate_PlaysThroughConverter()
    {
        var m = Mixer();
        var src = new StreamingSource(new RampDecoder(24000, 400), capacityFrames: 512);
        src.Pump();
        m.Play(new PlayParams(src));
        var o = Render(m, 1000);
        for (int k = 0; k < 780; k++) Assert.Equal((k / 2.0f + 1) / 1000f, o[k], 2e-6f);
        Assert.Equal(0, m.ActiveVoices);
        Assert.Equal(0, m.UnderrunEvents);
    }

    [Fact]
    public void StreamingSource_Underrun_WithConverter_IsCountedAndRecovers()
    {
        var m = Mixer();
        var src = new StreamingSource(new RampDecoder(24000, 100_000), capacityFrames: 256);
        m.Play(new PlayParams(src));
        var o = Render(m, 64);
        Assert.All(o, x => Assert.Equal(0f, x));
        Assert.True(m.UnderrunEvents >= 1);
        src.Pump();
        var o2 = Render(m, 64);
        Assert.Contains(o2, x => x != 0f);
    }

    [Fact]
    public void Resamplers_AreRecycled_AcrossVoices()
    {
        int created = 0;
        var m = Mixer(voices: 4, factory: (ch, q) => { created++; return new LinearTestResampler(ch, q); });
        var clip = RampClip(24000, 20);
        for (int i = 0; i < 100; i++)
        {
            m.Play(new PlayParams(clip));
            Render(m, 128); // the voice plays out and its converter goes back to the pool
        }
        Assert.True(created <= 4, $"created {created}");
    }

    [Fact]
    public void Play_NeedingConverter_Validates()
    {
        // Without a custom factory the library's resamplers are used.
        var plain = new AudioMixer(new MixerOptions { SampleRate = Rate });
        Assert.True(plain.Play(new PlayParams(RampClip(44100, 4))).IsValid);
        Assert.True(plain.Play(new PlayParams(RampClip(Rate, 4)) { Pitch = 2f }).IsValid);
        // The ratio is checked against the converter range on the control thread.
        Assert.Throws<ArgumentOutOfRangeException>(() => plain.Play(new PlayParams(RampClip(1, 4))));
        Assert.Throws<ArgumentOutOfRangeException>(() => plain.Play(new PlayParams(RampClip(Rate, 4)) { Pitch = 4f })); // ratio 0.25 < 0.5
        var wide = new AudioMixer(new MixerOptions { SampleRate = Rate, ConverterMinRatio = 0.25 });
        Assert.True(wide.Play(new PlayParams(RampClip(Rate, 4)) { Pitch = 4f }).IsValid);

        var wrong = new AudioMixer(new MixerOptions { SampleRate = Rate, PreallocatedConverters = [], ResamplerFactory = (_, q) => new LinearTestResampler(1, q) });
        Assert.Throws<InvalidOperationException>(() => wrong.Play(new PlayParams(RampClip(24000, 4, channels: 2))));
    }

    [Fact]
    public void ConverterCap_RefusesPlay_AndEvictsIdleConvertersOfOtherKinds()
    {
        var m = new AudioMixer(new MixerOptions
        {
            SampleRate = Rate, Channels = 2, MaxVoices = 8, MaxConvertedVoices = 2,
            PreallocatedConverters = [new ConverterReservation(1, ResamplerQuality.Linear, 2)],
        });
        var stereo = RampClip(24000, 4000, channels: 2);
        Assert.True(m.Play(new PlayParams(stereo)).IsValid);   // idle mono converters make room for a stereo one
        Assert.True(m.Play(new PlayParams(stereo)).IsValid);
        Assert.False(m.Play(new PlayParams(stereo)).IsValid);  // both converters are busy
        Assert.Equal(1, m.DroppedConverterRequests);
        Assert.Throws<ArgumentOutOfRangeException>(() => new AudioMixer(new MixerOptions
        {
            MaxVoices = 4, MaxConvertedVoices = 1, PreallocatedConverters = [new ConverterReservation(1, ResamplerQuality.Linear, 2)],
        }));
    }

    [Fact]
    public void DefaultQuality_FollowsTheKindOfConversion()
    {
        var seen = new List<ResamplerQuality>();
        var m = new AudioMixer(new MixerOptions
        {
            SampleRate = Rate, Channels = 1, PreallocatedConverters = [],
            ResamplerFactory = (ch, q) => { seen.Add(q); return new LinearTestResampler(ch, q); },
        });
        m.Play(new PlayParams(RampClip(24000, 100)));                                   // rate only
        m.Play(new PlayParams(RampClip(Rate, 100)) { Pitch = 1.5f });                    // pitched
        m.Play(new PlayParams(RampClip(Rate, 100)) { VariablePitch = true });            // reserved for SetPitch
        m.Play(new PlayParams(RampClip(24000, 100)) { Resampling = ResamplerQuality.Polyphase });
        Assert.Equal([ResamplerQuality.PolyphaseFast, ResamplerQuality.Linear, ResamplerQuality.Linear, ResamplerQuality.Polyphase], seen);
    }

    [Fact]
    public void Crossfade_WithConvertedVoice_Works()
    {
        var m = Mixer();
        var a = m.Play(new PlayParams(RampClip(Rate, 4000)));
        Render(m, 8);
        var b = m.Crossfade(a, new PlayParams(RampClip(24000, 4000)), 16);
        Assert.True(b.IsValid);
        Render(m, 64);
        Assert.Equal(1, m.ActiveVoices);
    }
}
