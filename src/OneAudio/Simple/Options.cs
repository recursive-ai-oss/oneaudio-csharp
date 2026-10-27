using OneAudio.Loudness;
using OneAudio.Flac;
using OneAudio.Opus;

namespace OneAudio;

/// <summary>An audio file format <see cref="AudioKit.Transcode(string, string, TranscodeOptions?, CancellationToken)"/> can write.</summary>
public enum AudioFileFormat
{
    /// <summary>RIFF/WAVE (RF64 when the data passes 4 GiB). File extensions <c>.wav</c> and <c>.wave</c>.</summary>
    Wav = 0,
    /// <summary>Native FLAC. File extension <c>.flac</c>.</summary>
    Flac = 1,
    /// <summary>Opus in an Ogg container (RFC 7845). File extensions <c>.opus</c>, <c>.ogg</c> and <c>.oga</c>; it is the only Ogg format the library writes.</summary>
    Opus = 2,
}

/// <summary>Sample-rate and channel conversion shared by the happy-path calls.</summary>
public record ConversionOptions
{
    /// <summary>Output sample rate in Hz, or 0 (default) to keep the source rate. Ratios between 1/64 and 64 are supported.</summary>
    public int SampleRate { get; init; }

    /// <summary>
    /// Output channel count, or 0 (default) to keep the source layout. Mono goes to two or more channels as a copy on the
    /// front left and right speakers (unity gain); everything else follows the mixer's speaker-folding rules (centre into left
    /// and right at -3 dB, a mono target takes left and right at -6 dB each, nothing is invented for speakers the source lacks).
    /// Folding sums are not normalised, so loud multichannel material can exceed full scale.
    /// </summary>
    public int Channels { get; init; }

    /// <summary>Resampler used when the rate changes. Default <see cref="ResamplerQuality.Polyphase"/>.</summary>
    public ResamplerQuality ResamplerQuality { get; init; } = ResamplerQuality.Polyphase;

    /// <summary>Limits applied to untrusted input. <see cref="AudioLimits.Default"/> when null.</summary>
    public AudioLimits? Limits { get; init; }
}

/// <summary>Options for <see cref="AudioKit.Load(string, LoadOptions?, CancellationToken)"/>.</summary>
public sealed record LoadOptions : ConversionOptions
{
    /// <summary>
    /// Refuse audio longer than this many output frames. Default 46 080 000 (16 minutes at 48 kHz).
    /// Decoded audio is held as 32-bit floats, so one frame costs 4 x channels bytes.
    /// </summary>
    public long MaxFrames { get; init; } = 48_000L * 60 * 16;
}

/// <summary>Options for transcoding and for file-to-file normalisation.</summary>
public sealed record TranscodeOptions : ConversionOptions
{
    /// <summary>
    /// The output format. When null (default) a path-based call takes it from the output file's extension; a stream-based call needs it set.
    /// </summary>
    public AudioFileFormat? Format { get; init; }

    /// <summary>
    /// WAV settings. By default the output keeps the source's sample type (floating point included); a lossy source is written as 16-bit PCM.
    /// </summary>
    public WavEncoderSettings? Wav { get; init; }

    /// <summary>
    /// FLAC settings (default level 5). When <see cref="FlacEncoderSettings.BitsPerSample"/> is 0, a lossless integer source keeps its
    /// bit depth (converted 24- and 32-bit audio is written at 24) and anything else is written at 16 bits.
    /// </summary>
    public FlacEncoderSettings? Flac { get; init; }

    /// <summary>
    /// Ogg Opus settings (default: unconstrained VBR, complexity 10, general audio). Its input rate and channel count are
    /// always taken from the converted source; sources not at 48 kHz are resampled by the writer.
    /// </summary>
    public OpusFileWriterSettings? Opus { get; init; }
}

/// <summary>What <see cref="AudioKit.Transcode(string, string, TranscodeOptions?, CancellationToken)"/> wrote.</summary>
/// <param name="Format">The file format written.</param>
/// <param name="Input">Sample rate and channels of the audio handed to the encoder (after any conversion).</param>
/// <param name="Frames">Frames handed to the encoder, at <c>Input.SampleRate</c>.</param>
public readonly record struct TranscodeResult(AudioFileFormat Format, AudioFormat Input, long Frames);

/// <summary>Options for loudness normalisation.</summary>
public sealed record NormalizeOptions
{
    /// <summary>Target integrated loudness in LUFS. Default -14, the level streaming services commonly use.</summary>
    public double TargetLufs { get; init; } = Audio.DefaultTargetLufs;

    /// <summary>
    /// True-peak ceiling in dBTP: the gain is reduced if boosting to the target would pass it. Default -1.
    /// Null disables the ceiling, in which case a boost may clip when converted to integers.
    /// </summary>
    public double? TruePeakCeilingDbtp { get; init; } = Audio.DefaultTruePeakCeilingDbtp;

    /// <summary>Conversion and encoder options for file-to-file normalisation. Ignored by the in-memory overloads.</summary>
    public TranscodeOptions? Output { get; init; }
}

/// <summary>The outcome of a normalisation.</summary>
/// <param name="Before">Loudness of the audio before the gain (after any sample-rate or channel conversion).</param>
/// <param name="GainDb">The gain applied, in dB. 0 for silence.</param>
/// <param name="LimitedByPeak">True when the true-peak ceiling, not the target, set the gain, so the target was not reached.</param>
public readonly record struct NormalizeResult(LoudnessResult Before, double GainDb, bool LimitedByPeak);

/// <summary>Options for <see cref="AudioMixerPlayExtensions"/> and <see cref="AudioKit.Play(AudioMixer, string, PlayOptions?, CancellationToken)"/>.</summary>
public sealed record PlayOptions
{
    /// <summary>Linear gain, default 1.</summary>
    public float Gain { get; init; } = 1f;

    /// <summary>Pan from -1 (left) to 1 (right), default 0.</summary>
    public float Pan { get; init; }

    /// <summary>Loop until stopped. Default false. Streams loop by seeking their decoder, so they need a seekable file.</summary>
    public bool Loop { get; init; }

    /// <summary>First frame of the loop body, in source frames. Default 0.</summary>
    public long LoopStartFrame { get; init; }

    /// <summary>Frame after the loop body, or -1 (default) for the end of the audio.</summary>
    public long LoopEndFrame { get; init; } = -1;

    /// <summary>Destination bus; 0 (default) is the first bus.</summary>
    public int Bus { get; init; }

    /// <summary>Voice-stealing priority; higher survives longer. Default 0.</summary>
    public int Priority { get; init; }

    /// <summary>Playback speed, 1 (default) is normal; this also shifts pitch.</summary>
    public float Pitch { get; init; } = 1f;

    /// <summary>Fade-in length in frames of the mixer's rate. Default 0 (none).</summary>
    public int FadeInFrames { get; init; }

    /// <summary>Limits applied to untrusted input. <see cref="AudioLimits.Default"/> when null.</summary>
    public AudioLimits? Limits { get; init; }

    /// <summary>For clips: refuse files longer than this many frames (default 16 minutes at 48 kHz). Long audio belongs in <c>PlayStream</c>.</summary>
    public long MaxClipFrames { get; init; } = 48_000L * 60 * 16;

    /// <summary>For streams: the decode-ahead buffer in frames. Default 32768 (about 0.7 s at 48 kHz); larger tolerates longer decode-thread stalls.</summary>
    public int StreamBufferFrames { get; init; } = 32768;

    internal PlayParams ToParams(AudioSource source, bool voiceLoop) => new(source)
    {
        Gain = Gain,
        Pan = Pan,
        Loop = voiceLoop && Loop,
        LoopStartFrame = LoopStartFrame,
        LoopEndFrame = LoopEndFrame,
        Bus = Bus,
        Priority = Priority,
        Pitch = Pitch,
        FadeInFrames = FadeInFrames,
    };
}
