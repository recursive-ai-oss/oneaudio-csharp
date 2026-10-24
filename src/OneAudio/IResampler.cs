namespace OneAudio;

/// <summary>Resampling quality/cost trade-off.</summary>
public enum ResamplerQuality
{
    /// <summary>Two-tap linear interpolation. Cheapest, no latency to speak of, audible aliasing and high-frequency loss; meant for real-time pitch shifting of short effects.</summary>
    Linear = 0,
    /// <summary>Windowed-sinc polyphase filter bank. Band-limited, with latency reported by <see cref="IResampler.LatencyFrames"/>.</summary>
    Polyphase = 1,
    /// <summary>A shorter windowed-sinc polyphase filter (about 60 % of the cost of <see cref="Polyphase"/>): flat to 0.8 of the lower Nyquist frequency with roughly 70 dB of alias rejection. For real-time pitch shifting where <see cref="Linear"/> aliases too much.</summary>
    PolyphaseFast = 2,
}

/// <summary>
/// A streaming sample-rate converter for interleaved <see cref="float"/> audio. Implementations keep their filter
/// history between calls so block boundaries are inaudible, allocate all state at construction, and allocate nothing in
/// <see cref="Process"/>, <see cref="Flush"/>, <see cref="Reset"/> or <see cref="SetRatio"/>, so they may be used on
/// the render path. Not thread-safe.
/// </summary>
public interface IResampler
{
    /// <summary>Channels per frame; fixed at construction.</summary>
    int Channels { get; }

    /// <summary>The quality mode; fixed at construction.</summary>
    ResamplerQuality Quality { get; }

    /// <summary>Current output rate divided by input rate (2.0 doubles the rate and the frame count).</summary>
    double Ratio { get; }

    /// <summary>
    /// Changes the ratio for subsequent calls, for example to bend pitch. Implementations glide or switch without
    /// discontinuity in the filter state; extreme jumps may click.
    /// </summary>
    /// <param name="ratio">Output/input rate ratio, within the implementation's documented range.</param>
    /// <exception cref="ArgumentOutOfRangeException">The ratio is not finite, not positive, or outside the supported range.</exception>
    void SetRatio(double ratio);

    /// <summary>
    /// Look-ahead the converter needs, in output frames. Output frame m always represents input time m / <see cref="Ratio"/>
    /// (the filter's own group delay is compensated, so an impulse at input frame n peaks at output frame n * ratio),
    /// but an output frame can only be produced once the input has advanced past it by this much: after N input frames
    /// the converter has produced about N * ratio - LatencyFrames output frames. <see cref="Flush"/> supplies the rest.
    /// Changes with the ratio.
    /// </summary>
    double LatencyFrames { get; }

    /// <summary>
    /// An upper bound on the output frames <see cref="Process"/> can produce from <paramref name="inputFrames"/>
    /// input frames at the current ratio, for sizing output buffers.
    /// </summary>
    /// <param name="inputFrames">Input frame count.</param>
    int MaxOutputFrames(int inputFrames);

    /// <summary>
    /// Converts as much of <paramref name="input"/> as fits into <paramref name="output"/>. Both are interleaved;
    /// only whole frames are used. Input not consumed (because output filled up) must be offered again on the next call.
    /// </summary>
    /// <param name="input">Input samples.</param>
    /// <param name="output">Output samples.</param>
    /// <param name="inputFramesUsed">Frames consumed from <paramref name="input"/>.</param>
    /// <param name="outputFramesWritten">Frames written to <paramref name="output"/>.</param>
    void Process(ReadOnlySpan<float> input, Span<float> output, out int inputFramesUsed, out int outputFramesWritten);

    /// <summary>
    /// Emits the tail still held in the filter after the last input (as if followed by silence), then resets.
    /// Call repeatedly with fresh buffers until it returns 0 if <paramref name="output"/> may be too small.
    /// </summary>
    /// <param name="output">Output samples.</param>
    /// <returns>Frames written.</returns>
    int Flush(Span<float> output);

    /// <summary>Discards filter history, as at the start of a new, unrelated signal (for example after a seek).</summary>
    void Reset();
}
