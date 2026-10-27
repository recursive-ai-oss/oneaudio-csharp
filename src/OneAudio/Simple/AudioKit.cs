using OneAudio.Loudness;
using OneAudio.Flac;
using OneAudio.Opus;

namespace OneAudio;

/// <summary>
/// The happy-path calls (open, load, play, transcode, measure, normalise) over one explicit <see cref="AudioCodecRegistry"/>.
/// <see cref="Audio"/> offers the same calls over a registry holding every codec; build an <see cref="AudioKit"/> yourself to
/// keep a trimmed application's size down:
/// <code>
/// var kit = new AudioKit(new AudioCodecRegistry().AddWav());   // only WAV reaches the published app
/// var clip = kit.Load("hit.wav");
/// </code>
/// The calls that write files also link the WAV, FLAC and Opus encoders. A kit is immutable and safe to share between threads
/// (the registry must not be changed after construction). Failures raise the library's typed exceptions
/// (<see cref="OneAudioException"/> and its subclasses), argument exceptions, I/O exceptions and
/// <see cref="OperationCanceledException"/>; nothing is swallowed.
/// </summary>
public sealed class AudioKit
{
    /// <summary>Creates a kit that reads the formats in <paramref name="registry"/>.</summary>
    /// <param name="registry">The containers and codecs to use. Do not add to it afterwards.</param>
    public AudioKit(AudioCodecRegistry registry)
    {
        ArgumentNullException.ThrowIfNull(registry);
        Registry = registry;
    }

    /// <summary>The registry this kit reads with.</summary>
    public AudioCodecRegistry Registry { get; }

    // ------------------------------------------------------------------ open and load

    /// <summary>
    /// Opens an audio file for streaming. The container is detected from the bytes, not the extension. This is
    /// <see cref="AudioDecoder.OpenFile"/> with this kit's registry.
    /// </summary>
    /// <param name="path">File path.</param>
    /// <param name="limits">Limits for untrusted input; <see cref="AudioLimits.Default"/> when null.</param>
    /// <param name="cancellationToken">Observed while scanning headers.</param>
    /// <returns>A decoder at frame 0. Dispose it to close the file.</returns>
    /// <exception cref="UnsupportedAudioFormatException">The format is not in the registry.</exception>
    /// <exception cref="AudioFormatException">The data is invalid.</exception>
    /// <exception cref="AudioLimitExceededException">The data exceeds <paramref name="limits"/>.</exception>
    public IAudioDecoder Open(string path, AudioLimits? limits = null, CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrEmpty(path);
        return AudioDecoder.OpenFile(path, Registry, limits, cancellationToken);
    }

    /// <summary>Opens an audio stream for decoding; see <see cref="Open(string, AudioLimits?, CancellationToken)"/>.</summary>
    /// <param name="stream">A readable stream at the start of the audio file. Seekable streams give a seekable decoder.</param>
    /// <param name="leaveOpen">When false (default), disposing the decoder disposes the stream.</param>
    /// <param name="limits">Limits for untrusted input; <see cref="AudioLimits.Default"/> when null.</param>
    /// <param name="cancellationToken">Observed while scanning headers.</param>
    /// <returns>A decoder at frame 0.</returns>
    public IAudioDecoder Open(Stream stream, bool leaveOpen = false, AudioLimits? limits = null, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(stream);
        return AudioDecoder.Open(stream, Registry, limits, leaveOpen, cancellationToken);
    }

    /// <summary>
    /// Decodes a whole file into memory as interleaved float, optionally converting the rate and channel count on the way.
    /// Without conversion the samples equal what <see cref="AudioClip.FromDecoder"/> returns over the same decoder.
    /// Allocates; call it on a loading thread, never from the render path.
    /// </summary>
    /// <param name="path">File path.</param>
    /// <param name="options">Conversion and limits; defaults (keep the file's rate and channels) when null.</param>
    /// <param name="cancellationToken">Checked between blocks.</param>
    /// <returns>A clip any mixer can play, with <see cref="AudioClip.Samples"/> for direct access.</returns>
    /// <exception cref="AudioLimitExceededException">The audio is longer than <see cref="LoadOptions.MaxFrames"/> or breaks a limit.</exception>
    /// <exception cref="ArgumentOutOfRangeException">The requested rate differs from the source by more than a factor of 64.</exception>
    public AudioClip Load(string path, LoadOptions? options = null, CancellationToken cancellationToken = default)
    {
        options ??= new LoadOptions();
        return LoadFrom(Open(path, options.Limits, cancellationToken), options, cancellationToken);
    }

    /// <summary>Decodes a stream into memory; see <see cref="Load(string, LoadOptions?, CancellationToken)"/>. The stream is left open.</summary>
    /// <param name="stream">A readable stream at the start of the audio file.</param>
    /// <param name="options">Conversion and limits; defaults when null.</param>
    /// <param name="cancellationToken">Checked between blocks.</param>
    /// <returns>The decoded clip.</returns>
    public AudioClip Load(Stream stream, LoadOptions? options = null, CancellationToken cancellationToken = default)
    {
        options ??= new LoadOptions();
        return LoadFrom(Open(stream, leaveOpen: true, options.Limits, cancellationToken), options, cancellationToken);
    }

    private static AudioClip LoadFrom(IAudioDecoder decoder, LoadOptions options, CancellationToken cancellationToken)
    {
        using var source = ConvertingDecoder.Wrap(decoder, options);
        return AudioClip.FromDecoder(source, options.MaxFrames, cancellationToken);
    }

    // ------------------------------------------------------------------ play

    /// <summary>
    /// Loads a file as a clip at the mixer's sample rate and starts a voice for it. Right for short effects; for music use
    /// <see cref="PlayStream(AudioMixer, string, PlayOptions?, CancellationToken)"/>. Every call decodes the file again: load once with
    /// <see cref="Load(string, LoadOptions?, CancellationToken)"/> and play the clip repeatedly for sounds that fire often.
    /// Call from a control thread, never from the render callback.
    /// </summary>
    /// <param name="mixer">The mixer.</param>
    /// <param name="path">File path.</param>
    /// <param name="options">Gain, pan, loop, bus and so on; defaults when null.</param>
    /// <param name="cancellationToken">Checked while decoding.</param>
    /// <returns>The voice, or an invalid handle if the mixer's command queue was full (see <see cref="AudioMixer.Play"/>).</returns>
    public VoiceHandle Play(AudioMixer mixer, string path, PlayOptions? options = null, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(mixer);
        options ??= new PlayOptions();
        var clip = Load(path, new LoadOptions { SampleRate = mixer.SampleRate, Limits = options.Limits, MaxFrames = options.MaxClipFrames }, cancellationToken);
        return mixer.Play(options.ToParams(clip, voiceLoop: true));
    }

    /// <summary>
    /// Starts a voice that streams a file: a shared background thread decodes ahead into a ring buffer, so memory use is
    /// small however long the file is. The file's own rate is converted by the mixer. Looping needs a seekable file.
    /// Call from a control thread.
    /// </summary>
    /// <param name="mixer">The mixer.</param>
    /// <param name="path">File path.</param>
    /// <param name="options">Gain, pan, loop, bus and so on; defaults when null.</param>
    /// <param name="cancellationToken">Observed while opening the file.</param>
    /// <returns>A handle to dispose when you are done with the sound. Disposing stops the voice and closes the file.</returns>
    /// <exception cref="ArgumentException"><see cref="PlayOptions.Loop"/> is set and the file cannot seek.</exception>
    public StreamingVoice PlayStream(AudioMixer mixer, string path, PlayOptions? options = null, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(mixer);
        options ??= new PlayOptions();
        var decoder = Open(path, options.Limits, cancellationToken);
        StreamingSource? source = null;
        try
        {
            source = new StreamingSource(decoder, options.StreamBufferFrames, options.Loop, options.LoopStartFrame, options.LoopEndFrame);
            SharedStreamPump.Instance.Add(source);
            var voice = mixer.Play(options.ToParams(source, voiceLoop: false));
            return new StreamingVoice(mixer, voice, source);
        }
        catch
        {
            if (source != null) { SharedStreamPump.Instance.Remove(source); source.Dispose(); }
            else decoder.Dispose();
            throw;
        }
    }

    // ------------------------------------------------------------------ transcode

    /// <summary>
    /// Converts an audio file to WAV, FLAC or Ogg Opus, resampling and remixing on the way if asked. The output format
    /// comes from <see cref="TranscodeOptions.Format"/> or else the output file's extension (<c>.wav</c>, <c>.flac</c>,
    /// <c>.opus</c>/<c>.ogg</c>). Streams block by block, so memory use does not depend on the length. WAV and FLAC from a lossless integer
    /// source are bit-exact when nothing is converted; the output file is deleted if the conversion fails.
    /// </summary>
    /// <param name="inputPath">Source file.</param>
    /// <param name="outputPath">Destination file; replaced if it exists. Must differ from the source.</param>
    /// <param name="options">Format, conversion and encoder settings; defaults when null.</param>
    /// <param name="cancellationToken">Checked between blocks.</param>
    /// <returns>What was written.</returns>
    /// <exception cref="ArgumentException">The format cannot be told from the extension, or the paths are the same file.</exception>
    public TranscodeResult Transcode(string inputPath, string outputPath, TranscodeOptions? options = null, CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrEmpty(inputPath);
        ArgumentException.ThrowIfNullOrEmpty(outputPath);
        options ??= new TranscodeOptions();
        var format = options.Format ?? FormatFromExtension(outputPath);
        CheckDistinct(inputPath, outputPath);
        return WriteFile(outputPath, output =>
        {
            using var source = ConvertingDecoder.Wrap(Open(inputPath, options.Limits, cancellationToken), options);
            return Encode(source, format, options, output, cancellationToken);
        });
    }

    /// <summary>Converts a stream; see <see cref="Transcode(string, string, TranscodeOptions?, CancellationToken)"/>. Neither stream is disposed.</summary>
    /// <param name="input">Readable stream at the start of the audio file.</param>
    /// <param name="output">Writable stream. WAV and FLAC patch their headers at the end, which is done when it is seekable.</param>
    /// <param name="options">Conversion and encoder settings; <see cref="TranscodeOptions.Format"/> is required.</param>
    /// <param name="cancellationToken">Checked between blocks.</param>
    /// <returns>What was written.</returns>
    /// <exception cref="ArgumentException"><see cref="TranscodeOptions.Format"/> is not set.</exception>
    public TranscodeResult Transcode(Stream input, Stream output, TranscodeOptions options, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(input);
        ArgumentNullException.ThrowIfNull(output);
        ArgumentNullException.ThrowIfNull(options);
        var format = options.Format ?? throw new ArgumentException("Set TranscodeOptions.Format when writing to a stream.", nameof(options));
        using var source = ConvertingDecoder.Wrap(Open(input, leaveOpen: true, options.Limits, cancellationToken), options);
        return Encode(source, format, options, output, cancellationToken);
    }

    // ------------------------------------------------------------------ loudness

    /// <summary>
    /// Measures a file: integrated loudness (LUFS), loudness range (LU) and true peak (dBTP). The result equals
    /// <see cref="LoudnessAnalyzer.Measure(IAudioDecoder, bool, CancellationToken)"/> over the same decoder. Streams, so memory use is small.
    /// </summary>
    /// <param name="path">File path.</param>
    /// <param name="truePeak">Measure the true peak (default true; it costs a few times more than the loudness alone).</param>
    /// <param name="cancellationToken">Checked between blocks.</param>
    /// <returns>The measurements.</returns>
    public LoudnessResult Measure(string path, bool truePeak = true, CancellationToken cancellationToken = default)
    {
        using var decoder = Open(path, null, cancellationToken);
        return LoudnessAnalyzer.Measure(decoder, truePeak, cancellationToken);
    }

    /// <summary>Measures a stream; see <see cref="Measure(string, bool, CancellationToken)"/>. The stream is left open.</summary>
    /// <param name="stream">Readable stream at the start of the audio file.</param>
    /// <param name="truePeak">Measure the true peak (default true).</param>
    /// <param name="cancellationToken">Checked between blocks.</param>
    /// <returns>The measurements.</returns>
    public LoudnessResult Measure(Stream stream, bool truePeak = true, CancellationToken cancellationToken = default)
    {
        using var decoder = Open(stream, leaveOpen: true, null, cancellationToken);
        return LoudnessAnalyzer.Measure(decoder, truePeak, cancellationToken);
    }

    /// <summary>
    /// Writes a copy of a file at the target loudness (default -14 LUFS, true peak at most -1 dBTP), converting the format
    /// as <see cref="Transcode(string, string, TranscodeOptions?, CancellationToken)"/> does. Decodes the input twice (measure, then
    /// apply the gain) and keeps no audio in memory. The loudness is measured after any rate or channel conversion.
    /// A lossy output format can overshoot the ceiling slightly. Gain is applied as float and rounded by the output format; no clipping
    /// or limiting is applied beyond the ceiling.
    /// </summary>
    /// <param name="inputPath">Source file.</param>
    /// <param name="outputPath">Destination file; replaced if it exists. Must differ from the source.</param>
    /// <param name="options">Target, ceiling and output settings; defaults when null.</param>
    /// <param name="cancellationToken">Checked between blocks.</param>
    /// <returns>The measured loudness and the gain applied.</returns>
    public NormalizeResult Normalize(string inputPath, string outputPath, NormalizeOptions? options = null, CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrEmpty(inputPath);
        ArgumentException.ThrowIfNullOrEmpty(outputPath);
        options ??= new NormalizeOptions();
        var output = options.Output ?? new TranscodeOptions();
        var format = output.Format ?? FormatFromExtension(outputPath);
        CheckDistinct(inputPath, outputPath);
        return WriteFile(outputPath, dest =>
            NormalizeCore(() => Open(inputPath, output.Limits, cancellationToken), dest, format, options, output, cancellationToken));
    }

    /// <summary>
    /// Normalises between streams; see <see cref="Normalize(string, string, NormalizeOptions?, CancellationToken)"/>. The input must be
    /// seekable (it is read twice) and neither stream is disposed.
    /// </summary>
    /// <param name="input">Seekable readable stream at the start of the audio file.</param>
    /// <param name="output">Writable stream.</param>
    /// <param name="options">Target, ceiling and output settings; <see cref="TranscodeOptions.Format"/> of <see cref="NormalizeOptions.Output"/> is required.</param>
    /// <param name="cancellationToken">Checked between blocks.</param>
    /// <returns>The measured loudness and the gain applied.</returns>
    /// <exception cref="ArgumentException">The input cannot seek, or the output format is not set.</exception>
    public NormalizeResult Normalize(Stream input, Stream output, NormalizeOptions options, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(input);
        ArgumentNullException.ThrowIfNull(output);
        ArgumentNullException.ThrowIfNull(options);
        if (!input.CanSeek) throw new ArgumentException("Normalising reads the input twice, so it must be seekable.", nameof(input));
        var o = options.Output ?? new TranscodeOptions();
        var format = o.Format ?? throw new ArgumentException("Set NormalizeOptions.Output.Format when writing to a stream.", nameof(options));
        long start = input.Position;
        return NormalizeCore(() =>
        {
            input.Position = start;
            return Open(input, leaveOpen: true, o.Limits, cancellationToken);
        }, output, format, options, o, cancellationToken);
    }

    private static NormalizeResult NormalizeCore(Func<IAudioDecoder> open, Stream output, AudioFileFormat format, NormalizeOptions options, TranscodeOptions conv, CancellationToken ct)
    {
        LoudnessResult before;
        using (var measured = ConvertingDecoder.Wrap(open(), conv))
            before = LoudnessAnalyzer.Measure(measured, truePeak: true, ct);
        var gain = before.GainFor(options.TargetLufs, options.TruePeakCeilingDbtp);
        using var source = ConvertingDecoder.Wrap(open(), conv, gain.Linear);
        Encode(source, format, conv, output, ct);
        return new NormalizeResult(before, gain.GainDb, gain.LimitedByPeak);
    }

    private static TranscodeResult Encode(IAudioDecoder source, AudioFileFormat format, TranscodeOptions options, Stream output, CancellationToken ct)
    {
        var fmt = source.Format;
        long frames;
        switch (format)
        {
            case AudioFileFormat.Wav:
                frames = WavEncoder.Encode(source, output, options.Wav, leaveOpen: true, ct);
                break;
            case AudioFileFormat.Flac:
            {
                var settings = options.Flac ?? new FlacEncoderSettings();
                // Converted audio is float, which would default to 16 bits; keep the source's depth up to 24.
                if (settings.BitsPerSample == 0 && !source.HasNativeIntegers && fmt.NativeType is NativeSampleType.PcmSigned24 or NativeSampleType.PcmSigned32)
                    settings = settings with { BitsPerSample = 24 };
                frames = FlacEncoder.Encode(source, output, settings, leaveOpen: true, ct);
                break;
            }
            case AudioFileFormat.Opus:
            {
                var settings = (options.Opus ?? new OpusFileWriterSettings()) with { InputSampleRate = fmt.SampleRate, Channels = fmt.Channels };
                using var writer = new OpusFileWriter(output, settings, leaveOpen: true);
                var buffer = new float[4096 * fmt.Channels];
                frames = 0;
                int n;
                while ((n = source.Read(buffer)) > 0)
                {
                    ct.ThrowIfCancellationRequested();
                    writer.Write(buffer.AsSpan(0, n * fmt.Channels));
                    frames += n;
                }
                writer.Finish();
                break;
            }
            default:
                throw new ArgumentOutOfRangeException(nameof(format));
        }
        return new TranscodeResult(format, new AudioFormat(fmt.SampleRate, fmt.Channels, fmt.ChannelMask), frames);
    }

    private static AudioFileFormat FormatFromExtension(string path) => Path.GetExtension(path).ToLowerInvariant() switch
    {
        ".wav" or ".wave" => AudioFileFormat.Wav,
        ".flac" => AudioFileFormat.Flac,
        ".opus" or ".ogg" or ".oga" => AudioFileFormat.Opus,
        var ext => throw new ArgumentException($"Cannot tell the output format from the extension '{ext}'; use .wav, .flac or .opus, or set TranscodeOptions.Format.", nameof(path)),
    };

    private static void CheckDistinct(string inputPath, string outputPath)
    {
        if (string.Equals(Path.GetFullPath(inputPath), Path.GetFullPath(outputPath), StringComparison.Ordinal))
            throw new ArgumentException("The output must be a different file from the input.", nameof(outputPath));
    }

    // Creates the file, runs the writer, and removes the partial file if anything fails.
    private static T WriteFile<T>(string path, Func<Stream, T> write)
    {
        var file = new FileStream(path, FileMode.Create, FileAccess.Write, FileShare.None, 4096);
        try
        {
            var result = write(file);
            file.Dispose();
            return result;
        }
        catch
        {
            file.Dispose();
            try { File.Delete(path); } catch (IOException) { } catch (UnauthorizedAccessException) { }
            throw;
        }
    }
}
