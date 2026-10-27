namespace OneAudio;

/// <summary>
/// A sound started with <see cref="AudioKit.PlayStream(AudioMixer, string, PlayOptions?, CancellationToken)"/>: the mixer voice plus the
/// file behind it. Dispose it when the sound is no longer needed (it is safe after the sound has ended); that stops the voice
/// if it is still playing, stops decoding and closes the file.
/// </summary>
public sealed class StreamingVoice : IDisposable
{
    private readonly AudioMixer _mixer;
    private int _disposed;

    internal StreamingVoice(AudioMixer mixer, VoiceHandle voice, StreamingSource source)
    {
        _mixer = mixer;
        Voice = voice;
        Source = source;
    }

    /// <summary>The mixer voice, for <see cref="AudioMixer.SetGain"/>, <see cref="AudioMixer.Stop"/> and the rest. Invalid if the mixer's command queue was full.</summary>
    public VoiceHandle Voice { get; }

    /// <summary>The streaming source, for position, underrun counts and <see cref="StreamingSource.Ended"/>.</summary>
    public StreamingSource Source { get; }

    /// <summary>Stops the voice, stops decoding and closes the file. Idempotent.</summary>
    public void Dispose()
    {
        if (Interlocked.Exchange(ref _disposed, 1) != 0) return;
        _mixer.Stop(Voice);
        SharedStreamPump.Instance.Remove(Source);
        Source.Dispose();
    }
}

/// <summary>One decode thread shared by every stream the happy-path layer starts.</summary>
internal static class SharedStreamPump
{
    private static readonly object Gate = new();
    private static StreamingPump? _pump;

    public static StreamingPump Instance
    {
        get
        {
            lock (Gate)
            {
                if (_pump == null)
                {
                    var pump = new StreamingPump();
                    pump.Start();
                    _pump = pump;
                }
                return _pump;
            }
        }
    }
}

/// <summary>
/// <c>mixer.Play("hit.wav")</c> and <c>mixer.PlayStream("music.ogg", loop: true)</c>: play a file on a mixer using the all-codecs
/// <see cref="Audio.Kit"/>. For a trimmed application call the same methods on your own <see cref="AudioKit"/>.
/// </summary>
public static class AudioMixerPlayExtensions
{
    /// <inheritdoc cref="AudioKit.Play(AudioMixer, string, PlayOptions?, CancellationToken)"/>
    public static VoiceHandle Play(this AudioMixer mixer, string path, PlayOptions? options = null, CancellationToken cancellationToken = default)
        => Audio.Kit.Play(mixer, path, options, cancellationToken);

    /// <summary>Loads a file as a clip and plays it, looping if asked; see <see cref="AudioKit.Play(AudioMixer, string, PlayOptions?, CancellationToken)"/>.</summary>
    /// <param name="mixer">The mixer.</param>
    /// <param name="path">File path.</param>
    /// <param name="loop">Loop until stopped.</param>
    /// <returns>The voice.</returns>
    public static VoiceHandle Play(this AudioMixer mixer, string path, bool loop)
        => Audio.Kit.Play(mixer, path, new PlayOptions { Loop = loop });

    /// <summary>Plays a clip or streaming source with simple options.</summary>
    /// <param name="mixer">The mixer.</param>
    /// <param name="source">What to play.</param>
    /// <param name="options">Gain, pan, loop, bus and so on; defaults when null. Streaming sources loop as configured on the source itself.</param>
    /// <returns>The voice, or an invalid handle if the command queue was full.</returns>
    public static VoiceHandle Play(this AudioMixer mixer, AudioSource source, PlayOptions? options = null)
    {
        ArgumentNullException.ThrowIfNull(mixer);
        ArgumentNullException.ThrowIfNull(source);
        return mixer.Play((options ?? new PlayOptions()).ToParams(source, voiceLoop: source is not StreamingSource));
    }

    /// <inheritdoc cref="AudioKit.PlayStream(AudioMixer, string, PlayOptions?, CancellationToken)"/>
    public static StreamingVoice PlayStream(this AudioMixer mixer, string path, PlayOptions? options = null, CancellationToken cancellationToken = default)
        => Audio.Kit.PlayStream(mixer, path, options, cancellationToken);

    /// <summary>Streams a file, looping if asked; see <see cref="AudioKit.PlayStream(AudioMixer, string, PlayOptions?, CancellationToken)"/>.</summary>
    /// <param name="mixer">The mixer.</param>
    /// <param name="path">File path.</param>
    /// <param name="loop">Loop until stopped (needs a seekable file).</param>
    /// <returns>A handle to dispose when done.</returns>
    public static StreamingVoice PlayStream(this AudioMixer mixer, string path, bool loop)
        => Audio.Kit.PlayStream(mixer, path, new PlayOptions { Loop = loop });
}
