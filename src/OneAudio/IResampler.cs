namespace OneAudio;

/// <summary>Resampling quality/cost trade-off.</summary>
public enum ResamplerQuality
{
    /// <summary>Two-tap linear interpolation. Cheapest, no latency to speak of, audible aliasing and high-frequency loss; meant for real-time pitch shifting of short effects.</summary>
    Linear = 0,
    /// <summary>Windowed-sinc polyphase filter bank. Band-limited, with latency reported by <see cref="IResampler.LatencyFrames"/>.</summary>
    Polyphase = 1,
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
    /// Delay the converter adds, in output frames: a feature at input time t appears in the output at t + latency.
    /// Zero (or near it) for <see cref="ResamplerQuality.Linear"/>.
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
