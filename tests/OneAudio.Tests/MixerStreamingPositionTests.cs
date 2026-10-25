using Xunit;

namespace OneAudio.Tests;

/// <summary>Streaming position and length, and the ducking "off" setting.</summary>
public class MixerStreamingPositionTests
{
    private const int Rate = 48000;

    [Fact]
    public void StreamingSource_ReportsLength_AndAdvancesPositionWithPlayback()
    {
        var src = new StreamingSource(new RampDecoder(Rate, 5000), capacityFrames: 1024);
        Assert.Equal(5000, src.LengthFrames);
        Assert.Equal(0, src.PositionFrames);
        var m = new AudioMixer(new MixerOptions { SampleRate = Rate, Channels = 2, MaxBlockFrames = 128 });
        src.Pump();
        m.Play(new PlayParams(src));
        var o = new float[2 * 300];
        m.Render(o);
        Assert.Equal(300, src.PlayedFrames);
        Assert.Equal(300, src.PositionFrames);
        src.Pump();
        m.Render(o);
        Assert.Equal(600, src.PositionFrames);
    }

    [Fact]
    public void StreamingSource_PositionWrapsWithTheLoop_AndUnknownLengthIsMinusOne()
    {
        var src = new StreamingSource(new RampDecoder(Rate, 1000), capacityFrames: 512, loop: true, loopStartFrame: 200, loopEndFrame: 600);
        var m = new AudioMixer(new MixerOptions { SampleRate = Rate, Channels = 2, MaxBlockFrames = 128 });
        m.Play(new PlayParams(src));
        var o = new float[2 * 100];
        long played = 0;
        for (int i = 0; i < 20; i++)
        {
            src.Pump();
            m.Render(o);
            played += 100;
            long expect = played < 600 ? played : 200 + (played - 600) % 400;
            Assert.Equal(played, src.PlayedFrames);
            Assert.Equal(expect, src.PositionFrames);
        }
        Assert.Equal(1000, src.LengthFrames);
        Assert.Equal(-1, new StreamingSource(new UnknownLengthDecoder(), capacityFrames: 64).LengthFrames);
    }

    [Fact]
    public void Ducking_None_RemovesTheLink()
    {
        var loud = new AudioClip(new AudioFormat(Rate, 1), Enumerable.Repeat(0.5f, Rate).ToArray());
        var bed = new AudioClip(new AudioFormat(Rate, 1), Enumerable.Repeat(0.5f, Rate).ToArray());
        var m = new AudioMixer(new MixerOptions { SampleRate = Rate, Channels = 2, MaxBlockFrames = 256 });
        m.Play(new PlayParams(loud) { Loop = true, Bus = 1, Pan = -1f });
        m.Play(new PlayParams(bed) { Loop = true, Bus = 0, Pan = 1f });
        Assert.True(m.SetDucking(0, 1, new DuckingSettings { AttackMs = 1f, ReleaseMs = 5f, ReductionDb = 20f }));
        var o = new float[2 * 4800];
        m.Render(o);
        m.Render(o);
        Assert.True(Math.Abs(o[^1]) < 0.1f, $"ducked level {o[^1]}");
        Assert.True(m.SetDucking(0, 1, DuckingSettings.None));
        for (int i = 0; i < 5; i++) m.Render(o);
        Assert.Equal(0.5f, Math.Abs(o[^1]), 0.02f);
        Assert.False(DuckingSettings.None.Enabled);
        Assert.True(new DuckingSettings().Enabled);
    }

    private sealed class UnknownLengthDecoder : IAudioDecoder
    {
        public AudioFormat Format { get; } = new(Rate, 1);
        public long LengthFrames => -1;
        public long PositionFrames => 0;
        public bool CanSeek => false;
        public bool HasNativeIntegers => false;
        public int Read(Span<float> destination) => 0;
        public int ReadInteger(Span<int> destination) => throw new NotSupportedException();
        public void Seek(long frame) => throw new NotSupportedException();
        public void Dispose() { }
    }
}
