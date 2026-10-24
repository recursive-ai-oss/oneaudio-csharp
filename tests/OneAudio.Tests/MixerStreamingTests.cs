using Xunit;

namespace OneAudio.Tests;

public class MixerStreamingTests
{
    private const int Rate = 48000;

    private static AudioMixer Mixer(int voices = 32) =>
        new(new MixerOptions { SampleRate = Rate, Channels = 1, MaxVoices = voices, MaxBlockFrames = 256 });

    [Fact]
    public void StreamingLoop_WithLoopPoints_WrapsExactly()
    {
        // Decoder frames 0..9 hold 1..10 (thousandths); loop body is frames 2..5 (values 3..6).
        var src = new StreamingSource(new RampDecoder(Rate, 10), capacityFrames: 64, loop: true, loopStartFrame: 2, loopEndFrame: 6);
        src.Pump();
        var m = Mixer();
        m.Play(new PlayParams(src));
        var o = new float[20];
        m.Render(o);
        int[] expected = [1, 2, 3, 4, 5, 6, 3, 4, 5, 6, 3, 4, 5, 6, 3, 4, 5, 6, 3, 4];
        for (int i = 0; i < 20; i++) Assert.Equal(expected[i] / 1000f, o[i], 1e-7f);
    }

    [Fact]
    public void StreamingLoop_ValidatesPoints()
    {
        Assert.Throws<ArgumentException>(() => new StreamingSource(new RampDecoder(Rate, 10), 64, true, 5, 5));
        Assert.Throws<ArgumentException>(() => new StreamingSource(new RampDecoder(Rate, 10), 64, true, -1));
        Assert.Throws<ArgumentException>(() => new StreamingSource(new NoSeekDecoder(), 64, true));
    }

    [Fact]
    public void Streaming_Multichannel_Source_Plays()
    {
        var src = new StreamingSource(new ConstDecoder(Rate, 6, 100, 0.5f), capacityFrames: 128);
        src.Pump();
        var m = new AudioMixer(new MixerOptions { SampleRate = Rate, Channels = 6, MaxVoices = 2, MaxBlockFrames = 64 });
        m.Play(new PlayParams(src));
        var o = new float[6 * 150];
        m.Render(o);
        Assert.Equal(0.5f, o[6 * 99 + 5], 1e-6f);
        Assert.Equal(0f, o[6 * 100 + 5]);
        Assert.Equal(0, m.ActiveVoices);
    }

    [Fact]
    public void Pump_PrimesAndRefillsManySources_WithoutUnderruns()
    {
        var m = Mixer();
        using var pump = new StreamingPump(intervalMilliseconds: 1);
        var sources = new List<StreamingSource>();
        for (int i = 0; i < 20; i++)
        {
            var s = new StreamingSource(new RampDecoder(Rate, 100_000), capacityFrames: 2048);
            pump.Add(s);
            Assert.Equal(2048, s.BufferedFrames); // primed before the voice starts
            sources.Add(s);
            m.Play(new PlayParams(s) { Gain = 0.01f });
        }
        pump.Start();
        var o = new float[256];
        for (int i = 0; i < 300; i++)
        {
            m.Render(o);
            // Pace the render loop a little under real time so the pump thread keeps up.
            Thread.Sleep(1);
        }
        Assert.Equal(0, m.UnderrunEvents);
        Assert.Equal(20, m.ActiveVoices);
        Assert.True(pump.Rounds > 10);
    }

    [Fact]
    public void Pump_RemovesFinishedSources_AndLoopsViaSeek()
    {
        using var pump = new StreamingPump();
        var finite = new StreamingSource(new RampDecoder(Rate, 100), capacityFrames: 128);
        var looping = new StreamingSource(new RampDecoder(Rate, 100), capacityFrames: 128, loop: true);
        pump.Add(finite);
        pump.Add(looping);
        Assert.Equal(2, pump.Count);
        var m = Mixer();
        m.Play(new PlayParams(finite));
        m.Play(new PlayParams(looping));
        m.Render(new float[300]);
        pump.PumpOnce();
        Assert.Equal(1, pump.Count); // the finite one drained and left
        Assert.True(looping.BufferedFrames > 0);
        Assert.False(pump.Remove(finite));
        Assert.True(pump.Remove(looping));
        Assert.Equal(0, pump.Count);
    }

    [Fact]
    public void Pump_FaultingDecoder_EndsSourceQuietly()
    {
        using var pump = new StreamingPump();
        var bad = new StreamingSource(new FaultyDecoder(Rate, failAfter: 50), capacityFrames: 128);
        pump.Add(bad); // priming hits the failure
        Assert.IsType<AudioFormatException>(bad.Fault);
        Assert.Equal(50, bad.BufferedFrames);
        var m = Mixer();
        m.Play(new PlayParams(bad));
        var o = new float[200];
        m.Render(o);
        Assert.Equal(0.05f, o[49], 1e-6f);
        Assert.Equal(0f, o[50]);
        Assert.Equal(0, m.UnderrunEvents); // a failed source ends instead of counting underruns
        Assert.Equal(0, m.ActiveVoices);
        pump.PumpOnce();
        Assert.Equal(0, pump.Count);
    }

    [Fact]
    public void Pump_Lifecycle_IsSafe()
    {
        var pump = new StreamingPump();
        var s = new StreamingSource(new RampDecoder(Rate, 1000), capacityFrames: 128);
        pump.Add(s);
        Assert.Throws<InvalidOperationException>(() => pump.Add(s));
        pump.Start();
        pump.Start();
        pump.Dispose();
        pump.Dispose();
        Assert.Throws<ObjectDisposedException>(() => pump.Add(s));
        Assert.Throws<ArgumentOutOfRangeException>(() => new StreamingPump(0));
        Assert.Throws<ArgumentOutOfRangeException>(() => new StreamingPump(5, 2.0));
    }

    private sealed class NoSeekDecoder : IAudioDecoder
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

    private sealed class ConstDecoder(int rate, int channels, long frames, float value) : IAudioDecoder
    {
        private long _pos;
        public AudioFormat Format { get; } = new(rate, channels);
        public long LengthFrames => frames;
        public long PositionFrames => _pos;
        public bool CanSeek => true;
        public bool HasNativeIntegers => false;
        public int Read(Span<float> destination)
        {
            int n = (int)Math.Min(destination.Length / channels, frames - _pos);
            destination[..(n * channels)].Fill(value);
            _pos += n;
            return n;
        }
        public int ReadInteger(Span<int> destination) => throw new NotSupportedException();
        public void Seek(long frame) => _pos = frame;
        public void Dispose() { }
    }

    private sealed class FaultyDecoder(int rate, int failAfter) : IAudioDecoder
    {
        private long _pos;
        public AudioFormat Format { get; } = new(rate, 1);
        public long LengthFrames => -1;
        public long PositionFrames => _pos;
        public bool CanSeek => false;
        public bool HasNativeIntegers => false;
        public int Read(Span<float> destination)
        {
            if (_pos >= failAfter) throw new AudioFormatException("corrupt packet");
            int n = (int)Math.Min(destination.Length, failAfter - _pos);
            for (int i = 0; i < n; i++) destination[i] = (_pos + i + 1) / 1000f;
            _pos += n;
            return n;
        }
        public int ReadInteger(Span<int> destination) => throw new NotSupportedException();
        public void Seek(long frame) => throw new NotSupportedException();
        public void Dispose() { }
    }
}
