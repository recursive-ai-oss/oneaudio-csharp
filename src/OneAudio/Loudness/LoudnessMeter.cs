using System.Numerics;
using OneAudio.Kernels;

namespace OneAudio.Loudness;

/// <summary>
/// Streaming loudness meter after ITU-R BS.1770-5 and EBU R 128 (Tech 3341 'EBU Mode', Tech 3342 loudness range).
/// Feed interleaved <see cref="float"/> blocks of any size to <see cref="Process"/>; read momentary (400 ms), short-term (3 s)
/// and gated integrated loudness in LUFS, the loudness range in LU, and the true peak in dBTP at any time.
/// <para>
/// Results depend only on the samples fed, never on how they were split into blocks. All memory is allocated by the
/// constructor; <see cref="Process"/> and the readouts allocate nothing, take no locks and throw nothing for valid
/// arguments, so a meter can sit on a real-time thread. The readouts of the gated measures scan a fixed 9000-bin
/// histogram (microseconds). A meter is not thread-safe: call it from one thread at a time.
/// </para>
/// <para>
/// Channel weights follow BS.1770-5: 1.0 for front and back-centre channels, 1.41 (+1.5 dB) for surround and side channels,
/// and the LFE channel is left out of the loudness (it still counts for the true peak). Weights come from the
/// channel mask in WAVE_FORMAT_EXTENSIBLE order. Without a usable mask, 1 to 4, 6 and 8 channels use the standard layouts;
/// 5 channels are read as L R C Ls Rs; any other count is weighted 1.0 per channel.
/// </para>
/// <para>
/// Values that have no data yet read as <see cref="double.NegativeInfinity"/> LUFS (or dBTP). Non-finite input samples are
/// measured as zero. Loudness windows are built from 1 ms steps aligned to the stream start (to the nearest sample), so
/// the momentary and short-term readouts lag by less than 1 ms and the maxima are tracked at 1 ms resolution. Before the
/// first full window the signal is taken to be preceded by silence.
/// </para>
/// </summary>
public sealed class LoudnessMeter
{
    /// <summary>Lowest accepted sample rate in Hz.</summary>
    public const int MinSampleRate = 4000;

    private const int MomentarySteps = 400;     // 1 ms steps in 400 ms
    private const int ShortTermSteps = 3000;    // 1 ms steps in 3 s
    private const int GateStepEvery = 100;      // 100 ms hop = 75 % overlap of 400 ms blocks

    private readonly int _rate, _channels;
    private readonly double[] _weights;         // per channel; 0 = excluded from loudness
    private readonly KWeighting _k;
    private readonly double[] _state;           // 4 per channel: stage 1 s1,s2, stage 2 s1,s2
    private readonly double[] _acc;             // per-channel running sum of squares inside the current 1 ms step
    private readonly double[] _stepEnergy = new double[ShortTermSteps];
    private readonly int[] _stepFrames = new int[ShortTermSteps];
    private readonly LoudnessHistogram _gating = new();
    private readonly LoudnessHistogram? _range;
    private readonly bool _wantPeak;
    private readonly float[] _branches = [];
    private readonly float[] _history = [];
    private readonly int[] _historyPos = [];
    private readonly bool _vector = LoudnessKernels.UseVector;
    private readonly int _nominalFrames;

    private long _framesDone;       // frames consumed
    private long _steps;            // completed 1 ms steps
    private long _nextBoundary;     // frame index that completes the next step
    private int _ring;             // index of the next step slot
    private double _sumM, _sumS;    // weighted energy over the last 400 ms / 3 s
    private long _framesM, _framesS;
    private double _maxM, _maxS;    // linear mean-square maxima
    private float _peak;

    /// <summary>Creates a meter.</summary>
    /// <param name="sampleRate">Sample rate in Hz, <see cref="MinSampleRate"/> to <see cref="AudioLimits.MaxSampleRate"/> of the default limits. K-weighting is designed for this exact rate.</param>
    /// <param name="channels">Interleaved channel count, 1 to <see cref="AudioLimits.MaxChannels"/> of the default limits.</param>
    /// <param name="channelMask">Speaker positions in interleave order; <see cref="ChannelMask.None"/> picks a layout from the channel count.</param>
    /// <param name="truePeak">Measure the true peak (4x oversampling). On by default; turning it off roughly halves the per-frame cost.</param>
    /// <param name="loudnessRange">Collect the short-term distribution for <see cref="LoudnessRange"/>. On by default.</param>
    /// <exception cref="ArgumentOutOfRangeException">The rate or channel count is outside the accepted range.</exception>
    public LoudnessMeter(int sampleRate, int channels, ChannelMask channelMask = ChannelMask.None, bool truePeak = true, bool loudnessRange = true)
    {
        ArgumentOutOfRangeException.ThrowIfLessThan(sampleRate, MinSampleRate);
        ArgumentOutOfRangeException.ThrowIfGreaterThan(sampleRate, AudioLimits.Default.MaxSampleRate);
        ArgumentOutOfRangeException.ThrowIfLessThan(channels, 1);
        ArgumentOutOfRangeException.ThrowIfGreaterThan(channels, AudioLimits.Default.MaxChannels);
        _rate = sampleRate;
        _channels = channels;
        _weights = ResolveWeights(channels, channelMask);
        _k = new KWeighting(sampleRate);
        _state = new double[4 * channels];
        _acc = new double[channels];
        _wantPeak = truePeak;
        if (truePeak)
        {
            _branches = LoudnessKernels.DesignBranches();
            _history = new float[channels * 2 * LoudnessKernels.Taps];
            _historyPos = new int[channels];
        }
        if (loudnessRange) _range = new LoudnessHistogram();
        _nominalFrames = Math.Max(1, sampleRate / 1000);
        Reset();
    }

    /// <summary>Creates a meter for a decoder or stream format.</summary>
    public LoudnessMeter(AudioFormat format, bool truePeak = true, bool loudnessRange = true)
        : this(format.SampleRate, format.Channels, format.ChannelMask, truePeak, loudnessRange)
    {
    }

    /// <summary>Sample rate in Hz.</summary>
    public int SampleRate => _rate;

    /// <summary>Interleaved channel count.</summary>
    public int Channels => _channels;

    /// <summary>Frames fed since construction or the last <see cref="Reset"/>.</summary>
    public long FramesProcessed => _framesDone;

    /// <summary>Whether the true peak is being measured.</summary>
    public bool MeasuresTruePeak => _wantPeak;

    /// <summary>Clears all state: filters, windows, gated history, maxima and peak (Tech 3341: the meter is reset before each measurement).</summary>
    public void Reset()
    {
        Array.Clear(_state);
        Array.Clear(_acc);
        Array.Clear(_stepEnergy);
        Array.Fill(_stepFrames, _nominalFrames);
        _gating.Clear();
        _range?.Clear();
        if (_wantPeak) { Array.Clear(_history); Array.Clear(_historyPos); }
        _framesDone = 0;
        _steps = 0;
        _nextBoundary = BoundaryOf(1);
        _ring = 0;
        _sumM = _sumS = 0;
        _framesM = (long)MomentarySteps * _nominalFrames;
        _framesS = (long)ShortTermSteps * _nominalFrames;
        _maxM = _maxS = 0;
        _peak = 0;
    }

    /// <summary>Momentary loudness (400 ms sliding window, ungated) in LUFS.</summary>
    public double MomentaryLufs => ToLufs(_sumM, _framesM);

    /// <summary>Short-term loudness (3 s sliding window, ungated) in LUFS.</summary>
    public double ShortTermLufs => ToLufs(_sumS, _framesS);

    /// <summary>Highest momentary loudness since the last reset, in LUFS.</summary>
    public double MaxMomentaryLufs => _maxM > 0 ? LoudnessHistogram.ToLufs(_maxM) : double.NegativeInfinity;

    /// <summary>Highest short-term loudness since the last reset, in LUFS.</summary>
    public double MaxShortTermLufs => _maxS > 0 ? LoudnessHistogram.ToLufs(_maxS) : double.NegativeInfinity;

    /// <summary>
    /// Gated integrated loudness in LUFS (absolute gate -70 LUFS, relative gate 10 LU below). Only complete 400 ms blocks
    /// at a 100 ms hop count; a trailing partial block is dropped, as BS.1770-5 requires.
    /// </summary>
    public double IntegratedLufs
        => _gating.GatedMean(-10.0, out double ms, out _, out _) ? LoudnessHistogram.ToLufs(ms) : double.NegativeInfinity;

    /// <summary>
    /// Loudness range in LU (EBU Tech 3342): the spread between the 10th and 95th percentile of the gated short-term
    /// loudness, sampled every 100 ms (absolute gate -70 LUFS, relative gate 20 LU below). Zero until at least one full 3 s
    /// window exists, and always zero when the meter was built without <c>loudnessRange</c>. Tech 3342 asks to flag the value as unstable during the first 60 s.
    /// </summary>
    public double LoudnessRange
    {
        get
        {
            if (_range is null || !_range.GatedMean(-20.0, out _, out long n, out int first)) return 0.0;
            long lo = (long)Math.Round((n - 1) * 0.10, MidpointRounding.AwayFromZero);
            long hi = (long)Math.Round((n - 1) * 0.95, MidpointRounding.AwayFromZero);
            return Math.Max(0.0, _range.Select(first, hi) - _range.Select(first, lo));
        }
    }

    /// <summary>
    /// Largest true-peak value seen on any channel, linear (1.0 = full scale), from 4x oversampling and the samples themselves.
    /// The interpolation filter lags the input by 12 samples, so a peak inside the last 12 frames fed is not yet visible.
    /// Zero when true peak is off.
    /// </summary>
    public double TruePeak => _peak;

    /// <summary>Largest true-peak value in dBTP (<see cref="double.NegativeInfinity"/> for silence or when true peak is off).</summary>
    public double TruePeakDbtp => _peak > 0 ? 20.0 * Math.Log10(_peak) : double.NegativeInfinity;

    /// <summary>
    /// Measures a block of interleaved samples.
    /// </summary>
    /// <param name="interleaved">Whole frames of <see cref="Channels"/> samples each, nominal full scale ±1.0.</param>
    /// <exception cref="ArgumentException">The length is not a multiple of the channel count.</exception>
    public void Process(ReadOnlySpan<float> interleaved)
    {
        int ch = _channels;
        if (interleaved.Length % ch != 0) throw new ArgumentException("Length must be a whole number of frames.", nameof(interleaved));
        int frames = interleaved.Length / ch;
        if (frames == 0) return;

        if (_wantPeak)
        {
            float peak = _peak;
            int taps2 = 2 * LoudnessKernels.Taps;
            for (int c = 0; c < ch; c++)
            {
                peak = LoudnessKernels.ProcessChannel(interleaved, c, ch, _history.AsSpan(c * taps2, taps2), ref _historyPos[c], _branches, peak, _vector);
            }
            _peak = peak;
        }

        int done = 0;
        while (done < frames)
        {
            long toBoundary = _nextBoundary - _framesDone;
            int n = (int)Math.Min(frames - done, toBoundary);
            Accumulate(interleaved.Slice(done * ch, n * ch), n);
            done += n;
            _framesDone += n;
            if (_framesDone == _nextBoundary) CompleteStep();
        }
    }

    private long BoundaryOf(long step) => step * _rate / 1000;

    private void Accumulate(ReadOnlySpan<float> block, int n)
    {
        int ch = _channels;
        var k = _k;
        for (int c = 0; c < ch; c++)
        {
            if (_weights[c] == 0.0) continue;
            int s = 4 * c;
            double s1a = _state[s], s1b = _state[s + 1], s2a = _state[s + 2], s2b = _state[s + 3];
            double acc = _acc[c];
            int idx = c;
            for (int f = 0; f < n; f++, idx += ch)
            {
                double x = block[idx];
                if (!double.IsFinite(x)) x = 0.0;
                // Transposed direct form II, shelf then high-pass.
                double y1 = k.S1b0 * x + s1a;
                s1a = k.S1b1 * x - k.S1a1 * y1 + s1b;
                s1b = k.S1b2 * x - k.S1a2 * y1;
                double y2 = k.S2b0 * y1 + s2a;
                s2a = k.S2b1 * y1 - k.S2a1 * y2 + s2b;
                s2b = k.S2b2 * y1 - k.S2a2 * y2;
                acc += y2 * y2;
            }
            // Keep decaying filter state out of the denormal range: it would slow every later block after long silence.
            if (Math.Abs(s1a) < 1e-30) s1a = 0;
            if (Math.Abs(s1b) < 1e-30) s1b = 0;
            if (Math.Abs(s2a) < 1e-30) s2a = 0;
            if (Math.Abs(s2b) < 1e-30) s2b = 0;
            _state[s] = s1a; _state[s + 1] = s1b; _state[s + 2] = s2a; _state[s + 3] = s2b;
            _acc[c] = acc;
        }
    }

    private void CompleteStep()
    {
        double energy = 0;
        for (int c = 0; c < _channels; c++)
        {
            energy += _weights[c] * _acc[c];
            _acc[c] = 0;
        }
        int frames = (int)(_nextBoundary - BoundaryOf(_steps));
        _steps++;
        _nextBoundary = BoundaryOf(_steps + 1);

        int slot = _ring;
        int mOld = (slot - MomentarySteps + ShortTermSteps) % ShortTermSteps;
        _sumM += energy - _stepEnergy[mOld];
        _framesM += frames - _stepFrames[mOld];
        _sumS += energy - _stepEnergy[slot];
        _framesS += frames - _stepFrames[slot];
        _stepEnergy[slot] = energy;
        _stepFrames[slot] = frames;
        _ring = (slot + 1) % ShortTermSteps;

        if (_sumM < 0) _sumM = 0;
        if (_sumS < 0) _sumS = 0;
        double m = _sumM / _framesM, s = _sumS / _framesS;
        if (m > _maxM) _maxM = m;
        if (s > _maxS) _maxS = s;

        if (_steps % GateStepEvery != 0) return;

        // Re-sum the windows exactly every 100 ms so the incremental sums cannot drift over very long programmes.
        double sm = 0, ss = 0;
        long fm = 0, fs = 0;
        for (int i = 1; i <= ShortTermSteps; i++)
        {
            int j = (_ring - i + ShortTermSteps) % ShortTermSteps;
            ss += _stepEnergy[j]; fs += _stepFrames[j];
            if (i <= MomentarySteps) { sm += _stepEnergy[j]; fm += _stepFrames[j]; }
        }
        _sumM = sm; _framesM = fm; _sumS = ss; _framesS = fs;

        if (_steps >= MomentarySteps) _gating.Add(sm / fm);
        if (_range is not null && _steps >= ShortTermSteps) _range.Add(ss / fs);
    }

    private static double ToLufs(double energy, long frames)
        => energy > 0 && frames > 0 ? LoudnessHistogram.ToLufs(energy / frames) : double.NegativeInfinity;

    /// <summary>Per-channel loudness weights for a layout (0 = excluded). Exposed for tests.</summary>
    internal static double[] ResolveWeights(int channels, ChannelMask mask)
    {
        var w = new double[channels];
        uint bits = (uint)mask;
        if (bits == 0 || BitOperations.PopCount(bits) != channels)
        {
            bits = (uint)AudioFormat.DefaultMask(channels);
        }
        if (bits != 0 && BitOperations.PopCount(bits) == channels)
        {
            int i = 0;
            for (int b = 0; b < 32 && i < channels; b++)
            {
                uint bit = 1u << b;
                if ((bits & bit) == 0) continue;
                w[i++] = WeightOf((ChannelMask)bit);
            }
            return w;
        }
        Array.Fill(w, 1.0);
        if (channels == 5) { w[3] = 1.41; w[4] = 1.41; }   // L R C Ls Rs
        return w;
    }

    private const double SurroundWeight = 1.41;   // BS.1770-5 Table 3: +1.5 dB

    private static double WeightOf(ChannelMask position) => position switch
    {
        ChannelMask.LowFrequency => 0.0,
        ChannelMask.BackLeft or ChannelMask.BackRight or ChannelMask.SideLeft or ChannelMask.SideRight => SurroundWeight,
        _ => 1.0,
    };
}
