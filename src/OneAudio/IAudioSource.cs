namespace OneAudio;

/// <summary>
/// Anything that produces interleaved float frames on demand, in the shape of <see cref="AudioMixer.Render"/>. An output device calls
/// it from its real-time thread, so an implementation must not allocate, lock, block or throw.
/// </summary>
public interface IAudioSource
{
    /// <summary>Frames per second the source renders at.</summary>
    int SampleRate { get; }

    /// <summary>Samples per frame.</summary>
    int Channels { get; }

    /// <summary>
    /// Fills <paramref name="output"/> (a whole number of frames) with the next block. Overwrites the span; values may exceed -1..1.
    /// Called from one thread at a time.
    /// </summary>
    /// <param name="output">Interleaved samples at <see cref="SampleRate"/> and <see cref="Channels"/>.</param>
    void Render(Span<float> output);
}
