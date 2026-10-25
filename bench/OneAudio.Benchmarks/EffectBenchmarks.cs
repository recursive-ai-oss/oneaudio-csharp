using BenchmarkDotNet.Attributes;

namespace OneAudio.Benchmarks;

/// <summary>Which insert is switched on for <see cref="EffectBenchmark"/>.</summary>
public enum EffectCase
{
    /// <summary>One looping voice, nothing else: the baseline to subtract.</summary>
    None,
    /// <summary>Master low-pass.</summary>
    LowPass,
    /// <summary>Master high-pass.</summary>
    HighPass,
    /// <summary>Master low-pass and high-pass.</summary>
    LowAndHighPass,
    /// <summary>Master reverb.</summary>
    Reverb,
    /// <summary>Master limiter (5 ms lookahead).</summary>
    Limiter,
    /// <summary>Master low-pass with its cutoff gliding on every call.</summary>
    LowPassSweeping,
}

/// <summary>
/// Cost of each effect: 1024 frames of 48 kHz stereo through one voice plus one effect on the master output. Subtract the
/// <see cref="EffectCase.None"/> row and divide by 1024 for nanoseconds per frame per effect instance. Allocated must read 0.
/// </summary>
[MemoryDiagnoser]
public class EffectBenchmark
{
    private const int Frames = 1024;
    private AudioMixer _mixer = null!;
    private readonly float[] _out = new float[Frames * 2];
    private int _tick;

    [Params(EffectCase.None, EffectCase.LowPass, EffectCase.HighPass, EffectCase.LowAndHighPass, EffectCase.Reverb, EffectCase.Limiter, EffectCase.LowPassSweeping)]
    public EffectCase Case { get; set; }

    [GlobalSetup]
    public void Setup()
    {
        _mixer = new AudioMixer(new MixerOptions
        {
            SampleRate = 48000, Channels = 2, MaxVoices = 4, MaxBuses = 2, MaxBlockFrames = Frames, MaxReverbs = 1,
            LimiterLookaheadMilliseconds = Case == EffectCase.Limiter ? 5 : 0,
        });
        var rng = new Random(3);
        var samples = new float[48000 * 2];
        for (int i = 0; i < samples.Length; i++) samples[i] = (float)(rng.NextDouble() * 2 - 1) * 0.3f;
        _mixer.Play(new PlayParams(new AudioClip(new AudioFormat(48000, 2), samples)) { Loop = true });
        const int m = AudioMixer.MasterBus;
        switch (Case)
        {
            case EffectCase.LowPass: case EffectCase.LowPassSweeping: _mixer.SetBusFilter(m, FilterType.LowPass, 4000, 1f, 0); break;
            case EffectCase.HighPass: _mixer.SetBusFilter(m, FilterType.HighPass, 120, 1f, 0); break;
            case EffectCase.LowAndHighPass: _mixer.SetBusFilter(m, FilterType.LowPass, 4000, 1f, 0); _mixer.SetBusFilter(m, FilterType.HighPass, 120, 1f, 0); break;
            case EffectCase.Reverb: _mixer.SetBusReverb(m, new ReverbSettings()); break;
            case EffectCase.Limiter: _mixer.SetLimiter(new LimiterSettings { CeilingDb = -6 }); break;
        }
        _mixer.Render(_out);
    }

    /// <summary>One render call of 1024 frames.</summary>
    [Benchmark]
    public float Render()
    {
        if (Case == EffectCase.LowPassSweeping) _mixer.SetBusFilter(AudioMixer.MasterBus, FilterType.LowPass, 1000 + (++_tick & 7) * 1000, 1f, Frames);
        _mixer.Render(_out);
        return _out[0];
    }
}

/// <summary>
/// A typical game mix: 64 looping voices (half of them mono, some at 44.1 kHz) on four buses that each carry a low-pass and a
/// high-pass, a reverb on one bus and the master limiter, against the same mix without effects. 1024 frames of 48 kHz stereo per call.
/// </summary>
[MemoryDiagnoser]
public class GameMixBenchmark
{
    private const int Frames = 1024;
    private AudioMixer _mixer = null!;
    private readonly float[] _out = new float[Frames * 2];

    [Params(false, true)]
    public bool Effects { get; set; }

    [GlobalSetup]
    public void Setup()
    {
        _mixer = new AudioMixer(new MixerOptions
        {
            SampleRate = 48000, Channels = 2, MaxVoices = 64, MaxBuses = 4, MaxBlockFrames = Frames, MaxReverbs = 1,
            LimiterLookaheadMilliseconds = Effects ? 5 : 0,
        });
        var rng = new Random(7);
        AudioClip Clip(int rate, int ch)
        {
            var s = new float[rate * ch];
            for (int i = 0; i < s.Length; i++) s[i] = (float)(rng.NextDouble() * 2 - 1) * 0.02f;
            return new AudioClip(new AudioFormat(rate, ch), s);
        }
        var clips = new[] { Clip(48000, 1), Clip(48000, 2), Clip(44100, 1) };
        for (int i = 0; i < 64; i++)
            _mixer.Play(new PlayParams(clips[i % 3]) { Loop = true, Pan = (i % 9 - 4) / 4f, Bus = i % 4, Gain = 0.5f });
        if (Effects)
        {
            for (int b = 0; b < 4; b++)
            {
                _mixer.SetBusFilter(b, FilterType.LowPass, 6000, 0.9f, 0);
                _mixer.SetBusFilter(b, FilterType.HighPass, 80, 0.7f, 0);
            }
            _mixer.SetBusReverb(1, new ReverbSettings());
            _mixer.SetLimiter(new LimiterSettings());
        }
        _mixer.Render(_out);
    }

    /// <summary>One render call of 1024 frames.</summary>
    [Benchmark]
    public float Render()
    {
        _mixer.Render(_out);
        return _out[0];
    }
}
