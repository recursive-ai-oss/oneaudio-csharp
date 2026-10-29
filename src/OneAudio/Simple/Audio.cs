using OneAudio.Loudness;

namespace OneAudio;

/// <summary>
/// The one-call entry point: open, load, play, transcode, resample, measure and normalise audio with sensible defaults.
/// Everything here is built on the explicit low-level API (<see cref="AudioDecoder"/>, <see cref="AudioMixer"/>,
/// <see cref="LoudnessAnalyzer"/>, the encoders) and returns the same types, so you can drop down at any point.
/// <code>
/// var clip = Audio.Load("hit.wav");                       // any supported format, decoded to float
/// var mixer = Audio.CreateMixer();                        // 48 kHz stereo
/// mixer.Play("hit.wav");                                  // or mixer.PlayStream("music.ogg", loop: true)
/// Audio.Transcode("take.wav", "take.flac");
/// var loudness = Audio.Measure("take.wav");               // loudness.IntegratedLufs, loudness.TruePeakDbtp
/// </code>
/// <para>
/// <b>Trimming.</b> The file-based calls use <see cref="Kit"/>, which registers every codec the library has, so a published
/// application that touches them carries all of them. To keep unused codecs out, build your own
/// <see cref="AudioKit"/> over a registry with only the formats you need, or use the low-level API. The calls that take
/// no file (<see cref="Resample(ReadOnlySpan{float}, int, int, int, ResamplerQuality)"/>, the span overloads of
/// <see cref="Measure(AudioClip, bool)"/> and <see cref="Normalize(Span{float}, AudioFormat, NormalizeOptions?)"/>, <see cref="CreateMixer"/>) never touch
/// the default registry and leave the codecs trimmable.
/// </para>
/// </summary>
public static class Audio
{
    /// <summary>The default normalisation target, -14 LUFS.</summary>
    public const double DefaultTargetLufs = -14.0;

    /// <summary>The default true-peak ceiling for normalisation, -1 dBTP.</summary>
    public const double DefaultTruePeakCeilingDbtp = -1.0;

    /// <summary>
    /// A kit over a registry with WAV, Ogg, Vorbis, Opus, FLAC and MP3. Created on first use. Touching this property (or any call
    /// that uses it) keeps every codec in a trimmed application.
    /// </summary>
    public static AudioKit Kit => DefaultKit.Instance;

    // ---------------------------------------------------------------- open, load

    /// <inheritdoc cref="AudioKit.Open(string, AudioLimits?, CancellationToken)"/>
    public static IAudioDecoder Open(string path, AudioLimits? limits = null, CancellationToken cancellationToken = default)
        => Kit.Open(path, limits, cancellationToken);

    /// <inheritdoc cref="AudioKit.Open(Stream, bool, AudioLimits?, CancellationToken)"/>
    public static IAudioDecoder Open(Stream stream, bool leaveOpen = false, AudioLimits? limits = null, CancellationToken cancellationToken = default)
        => Kit.Open(stream, leaveOpen, limits, cancellationToken);

    /// <inheritdoc cref="AudioKit.Load(string, LoadOptions?, CancellationToken)"/>
    public static AudioClip Load(string path, LoadOptions? options = null, CancellationToken cancellationToken = default)
        => Kit.Load(path, options, cancellationToken);

    /// <inheritdoc cref="AudioKit.Load(Stream, LoadOptions?, CancellationToken)"/>
    public static AudioClip Load(Stream stream, LoadOptions? options = null, CancellationToken cancellationToken = default)
        => Kit.Load(stream, options, cancellationToken);

    // ---------------------------------------------------------------- mixer

    /// <summary>
    /// Creates a mixer with the defaults for games: 48 kHz stereo, 64 voices, 8 buses, a lookahead limiter on the master at -1 dB
    /// so summed voices cannot clip. Drive it from your audio device callback with <see cref="AudioMixer.Render"/>.
    /// </summary>
    /// <param name="sampleRate">Output rate in Hz (default 48000), normally the device's.</param>
    /// <param name="channels">Output channels (default 2).</param>
    /// <param name="maxVoices">Voice cap (default 64).</param>
    /// <returns>A new mixer. Use <c>new AudioMixer(MixerOptions)</c> for full control.</returns>
    public static AudioMixer CreateMixer(int sampleRate = 48_000, int channels = 2, int maxVoices = 64)
    {
        var mixer = new AudioMixer(new MixerOptions { SampleRate = sampleRate, Channels = channels, MaxVoices = maxVoices, LimiterLookaheadMilliseconds = 1.0 });
        mixer.SetLimiter(new LimiterSettings());
        return mixer;
    }

    // ---------------------------------------------------------------- transcode

    /// <inheritdoc cref="AudioKit.Transcode(string, string, TranscodeOptions?, CancellationToken)"/>
    public static TranscodeResult Transcode(string inputPath, string outputPath, TranscodeOptions? options = null, CancellationToken cancellationToken = default)
        => Kit.Transcode(inputPath, outputPath, options, cancellationToken);

    /// <inheritdoc cref="AudioKit.Transcode(Stream, Stream, TranscodeOptions, CancellationToken)"/>
    public static TranscodeResult Transcode(Stream input, Stream output, TranscodeOptions options, CancellationToken cancellationToken = default)
        => Kit.Transcode(input, output, options, cancellationToken);

    // ---------------------------------------------------------------- resample

    /// <summary>
    /// Converts interleaved samples from one sample rate to another in one call. The output is delay-compensated (an
    /// impulse at input frame n lands at output frame n x ratio) and has exactly <c>round(frames x toRate / fromRate)</c> frames.
    /// Allocates the result.
    /// </summary>
    /// <param name="samples">Interleaved input; its length must be a multiple of <paramref name="channels"/>.</param>
    /// <param name="channels">Channels per frame.</param>
    /// <param name="fromRate">Input rate in Hz.</param>
    /// <param name="toRate">Output rate in Hz; the ratio to <paramref name="fromRate"/> must lie between 1/64 and 64.</param>
    /// <param name="quality">Default <see cref="ResamplerQuality.Polyphase"/> (144 taps, at least 100 dB stopband rejection).</param>
    /// <returns>The converted samples.</returns>
    /// <exception cref="ArgumentException">The length is not a whole number of frames.</exception>
    /// <exception cref="ArgumentOutOfRangeException">A rate or channel count is not positive, or the ratio is out of range.</exception>
    public static float[] Resample(ReadOnlySpan<float> samples, int channels, int fromRate, int toRate, ResamplerQuality quality = ResamplerQuality.Polyphase)
    {
        ArgumentOutOfRangeException.ThrowIfLessThan(channels, 1);
        ArgumentOutOfRangeException.ThrowIfLessThan(fromRate, 1);
        ArgumentOutOfRangeException.ThrowIfLessThan(toRate, 1);
        if (samples.Length % channels != 0) throw new ArgumentException("Sample count must be a whole number of frames.", nameof(samples));
        if (fromRate == toRate) return samples.ToArray();
        double ratio = (double)toRate / fromRate;
        if (ratio < 1.0 / 64 || ratio > 64) throw new ArgumentOutOfRangeException(nameof(toRate), "Sample rates may differ by at most a factor of 64.");
        long frames = samples.Length / channels;
        long expected = (long)Math.Round(frames * ratio, MidpointRounding.AwayFromZero);
        if (expected * channels > Array.MaxLength) throw new ArgumentOutOfRangeException(nameof(samples), "The converted audio would be too large for an array.");
        var resampler = AudioResampler.Create(channels, quality, ratio, new ResamplerOptions { MinRatio = Math.Min(1.0, ratio), MaxRatio = Math.Max(1.0, ratio) });
        int capacity = resampler.MaxOutputFrames((int)frames) + (int)Math.Ceiling(resampler.LatencyFrames) + 64;
        var output = new float[(long)capacity * channels];
        resampler.Process(samples, output, out _, out int written);
        written += resampler.Flush(output.AsSpan(written * channels));
        return TrimTo(output, written, expected, channels);
    }

    /// <summary>Resamples a clip; see <see cref="Resample(ReadOnlySpan{float}, int, int, int, ResamplerQuality)"/>.</summary>
    /// <param name="clip">The clip.</param>
    /// <param name="toRate">Output rate in Hz.</param>
    /// <param name="quality">Default <see cref="ResamplerQuality.Polyphase"/>.</param>
    /// <returns>A new clip at <paramref name="toRate"/>.</returns>
    public static AudioClip Resample(AudioClip clip, int toRate, ResamplerQuality quality = ResamplerQuality.Polyphase)
    {
        ArgumentNullException.ThrowIfNull(clip);
        var f = clip.Format;
        return new AudioClip(f with { SampleRate = toRate }, Resample(clip.Samples.Span, f.Channels, f.SampleRate, toRate, quality));
    }

    private static float[] TrimTo(float[] buffer, int producedFrames, long expectedFrames, int channels)
    {
        // The filter delivers the expected count to within a frame; make it exact (silence after the signal is correct padding).
        var result = new float[expectedFrames * channels];
        buffer.AsSpan(0, (int)Math.Min(producedFrames, expectedFrames) * channels).CopyTo(result);
        return result;
    }

    // ---------------------------------------------------------------- loudness

    /// <inheritdoc cref="AudioKit.Measure(string, bool, CancellationToken)"/>
    public static LoudnessResult Measure(string path, bool truePeak = true, CancellationToken cancellationToken = default)
        => Kit.Measure(path, truePeak, cancellationToken);

    /// <inheritdoc cref="AudioKit.Measure(Stream, bool, CancellationToken)"/>
    public static LoudnessResult Measure(Stream stream, bool truePeak = true, CancellationToken cancellationToken = default)
        => Kit.Measure(stream, truePeak, cancellationToken);

    /// <summary>Measures a decoded clip; equals <see cref="LoudnessAnalyzer.Measure(ReadOnlySpan{float}, AudioFormat, bool)"/> over its samples.</summary>
    /// <param name="clip">The clip.</param>
    /// <param name="truePeak">Measure the true peak (default true).</param>
    /// <returns>Integrated LUFS, loudness range, true peak and maxima.</returns>
    public static LoudnessResult Measure(AudioClip clip, bool truePeak = true)
    {
        ArgumentNullException.ThrowIfNull(clip);
        return LoudnessAnalyzer.Measure(clip.Samples.Span, clip.Format, truePeak);
    }

    /// <inheritdoc cref="AudioKit.Normalize(string, string, NormalizeOptions?, CancellationToken)"/>
    public static NormalizeResult Normalize(string inputPath, string outputPath, NormalizeOptions? options = null, CancellationToken cancellationToken = default)
        => Kit.Normalize(inputPath, outputPath, options, cancellationToken);

    /// <inheritdoc cref="AudioKit.Normalize(Stream, Stream, NormalizeOptions, CancellationToken)"/>
    public static NormalizeResult Normalize(Stream input, Stream output, NormalizeOptions options, CancellationToken cancellationToken = default)
        => Kit.Normalize(input, output, options, cancellationToken);

    /// <summary>
    /// Scales samples in place so their integrated loudness reaches the target (default -14 LUFS), reduced if the true peak would
    /// pass the ceiling (default -1 dBTP). Silence is left alone. No clipping or limiting is applied: with a null ceiling a boost can
    /// exceed full scale.
    /// </summary>
    /// <param name="samples">Interleaved samples, modified in place.</param>
    /// <param name="format">Rate, channels and layout of <paramref name="samples"/>.</param>
    /// <param name="options">Target and ceiling; <see cref="NormalizeOptions.Output"/> is ignored.</param>
    /// <returns>The loudness measured before and the gain applied.</returns>
    public static NormalizeResult Normalize(Span<float> samples, AudioFormat format, NormalizeOptions? options = null)
    {
        options ??= new NormalizeOptions();
        var before = LoudnessAnalyzer.Measure(samples, format, truePeak: true);
        var gain = before.GainFor(options.TargetLufs, options.TruePeakCeilingDbtp);
        if (gain.GainDb != 0)
        {
            float g = (float)gain.Linear;
            for (int i = 0; i < samples.Length; i++) samples[i] *= g;
        }
        return new NormalizeResult(before, gain.GainDb, gain.LimitedByPeak);
    }

    /// <summary>Returns a normalised copy of a clip; see <see cref="Normalize(Span{float}, AudioFormat, NormalizeOptions?)"/>. The original is untouched.</summary>
    /// <param name="clip">The clip.</param>
    /// <param name="result">The loudness measured before and the gain applied.</param>
    /// <param name="options">Target and ceiling; defaults when null.</param>
    /// <returns>A new clip.</returns>
    public static AudioClip Normalize(AudioClip clip, out NormalizeResult result, NormalizeOptions? options = null)
    {
        ArgumentNullException.ThrowIfNull(clip);
        var copy = clip.Samples.ToArray();
        result = Normalize(copy, clip.Format, options);
        return new AudioClip(clip.Format, copy);
    }
}

/// <summary>Holds the all-codecs registry apart from <see cref="Audio"/> so that only the calls that need it keep the codecs.</summary>
internal static class DefaultKit
{
    public static readonly AudioKit Instance = new(new AudioCodecRegistry().AddWav().AddOgg().AddVorbis().AddOpus().AddFlac().AddAdts().AddMp3().AddMp4().AddAmr());
}
