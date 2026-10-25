using System.Buffers;

namespace OneAudio.Loudness;

/// <summary>Result of measuring a whole programme. Loudness values are in LUFS (or LU for the range), the peak in dBTP.</summary>
/// <param name="IntegratedLufs">Gated integrated loudness; <see cref="double.NegativeInfinity"/> for silence.</param>
/// <param name="LoudnessRange">Loudness range in LU (Tech 3342); 0 for programmes shorter than 3 s.</param>
/// <param name="TruePeakDbtp">Largest true peak on any channel; <see cref="double.NegativeInfinity"/> for silence.</param>
/// <param name="MaxMomentaryLufs">Highest 400 ms loudness.</param>
/// <param name="MaxShortTermLufs">Highest 3 s loudness.</param>
/// <param name="Frames">Frames measured.</param>
public readonly record struct LoudnessResult(
    double IntegratedLufs,
    double LoudnessRange,
    double TruePeakDbtp,
    double MaxMomentaryLufs,
    double MaxShortTermLufs,
    long Frames)
{
    /// <summary>The gain that brings this programme to <paramref name="targetLufs"/>; see <see cref="LoudnessNormalization.ComputeGain"/>.</summary>
    public NormalizationGain GainFor(double targetLufs = LoudnessNormalization.EbuR128Target, double? truePeakCeilingDbtp = LoudnessNormalization.DefaultCeilingDbtp)
        => LoudnessNormalization.ComputeGain(IntegratedLufs, TruePeakDbtp, targetLufs, truePeakCeilingDbtp);
}

/// <summary>A gain to apply to a programme to normalise it.</summary>
/// <param name="GainDb">Gain in dB (negative attenuates). Zero when the programme is silent.</param>
/// <param name="LimitedByPeak">True when the true-peak ceiling, not the loudness target, set the gain, so the target is not reached.</param>
public readonly record struct NormalizationGain(double GainDb, bool LimitedByPeak)
{
    /// <summary>The gain as a linear factor for multiplying samples.</summary>
    public double Linear => Math.Pow(10.0, GainDb / 20.0);
}

/// <summary>Loudness normalisation arithmetic.</summary>
public static class LoudnessNormalization
{
    /// <summary>EBU R 128 programme loudness target, -23 LUFS.</summary>
    public const double EbuR128Target = -23.0;

    /// <summary>Default true-peak ceiling, -1 dBTP (EBU R 128 maximum permitted true-peak level).</summary>
    public const double DefaultCeilingDbtp = -1.0;

    /// <summary>
    /// The gain that moves a programme from <paramref name="integratedLufs"/> to <paramref name="targetLufs"/>, reduced if
    /// the resulting true peak would pass <paramref name="truePeakCeilingDbtp"/>. Boosting is allowed; pass a
    /// <see langword="null"/> ceiling to ignore the peak. If either measured value is not finite (silence), the gain is 0 dB.
    /// </summary>
    public static NormalizationGain ComputeGain(double integratedLufs, double truePeakDbtp, double targetLufs = EbuR128Target, double? truePeakCeilingDbtp = DefaultCeilingDbtp)
    {
        if (!double.IsFinite(integratedLufs) || !double.IsFinite(targetLufs)) return new NormalizationGain(0.0, false);
        double gain = targetLufs - integratedLufs;
        if (truePeakCeilingDbtp is double ceiling && double.IsFinite(truePeakDbtp))
        {
            double allowed = ceiling - truePeakDbtp;
            if (gain > allowed) return new NormalizationGain(allowed, true);
        }
        return new NormalizationGain(gain, false);
    }
}

/// <summary>One-call loudness measurement of decoded audio.</summary>
public static class LoudnessAnalyzer
{
    /// <summary>
    /// Reads <paramref name="decoder"/> from its current position to the end and measures it. Decodes in 4096-frame blocks
    /// through a pooled buffer, so memory use does not depend on the length of the programme.
    /// </summary>
    /// <param name="decoder">The source; its format sets the sample rate, channel count and channel mask.</param>
    /// <param name="truePeak">Measure the true peak (default true).</param>
    /// <param name="cancellationToken">Checked between blocks.</param>
    /// <exception cref="OperationCanceledException">Cancelled.</exception>
    public static LoudnessResult Measure(IAudioDecoder decoder, bool truePeak = true, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(decoder);
        var meter = new LoudnessMeter(decoder.Format, truePeak);
        const int BlockFrames = 4096;
        var buffer = ArrayPool<float>.Shared.Rent(BlockFrames * meter.Channels);
        try
        {
            while (true)
            {
                cancellationToken.ThrowIfCancellationRequested();
                int got = decoder.Read(buffer.AsSpan(0, BlockFrames * meter.Channels));
                if (got <= 0) break;
                meter.Process(buffer.AsSpan(0, got * meter.Channels));
            }
        }
        finally
        {
            ArrayPool<float>.Shared.Return(buffer);
        }
        return Result(meter);
    }

    /// <summary>Measures interleaved samples held in memory.</summary>
    public static LoudnessResult Measure(ReadOnlySpan<float> interleaved, AudioFormat format, bool truePeak = true)
    {
        var meter = new LoudnessMeter(format, truePeak);
        meter.Process(interleaved);
        return Result(meter);
    }

    /// <summary>Snapshot of a meter's current readouts as a result.</summary>
    public static LoudnessResult Result(LoudnessMeter meter)
    {
        ArgumentNullException.ThrowIfNull(meter);
        return new LoudnessResult(meter.IntegratedLufs, meter.LoudnessRange, meter.TruePeakDbtp, meter.MaxMomentaryLufs, meter.MaxShortTermLufs, meter.FramesProcessed);
    }
}
