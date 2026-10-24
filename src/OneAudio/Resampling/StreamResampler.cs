using OneAudio.Kernels;

namespace OneAudio.Resampling;

/// <summary>
/// Streaming resampler shared by the linear and polyphase modes.
///
/// Time bookkeeping: the read position is a 32.32 fixed-point index into a planar history buffer, advanced by
/// round(2^32 / ratio) per output frame. Integer arithmetic keeps the position independent of how the caller splits
/// the input, so any block split yields identical output. Output frame m is the signal at input time m / ratio; the
/// filter is centred there (group delay compensated) and needs <see cref="_halfWidth"/> input frames of look-ahead.
///
/// Polyphase: a bank of 512 + 1 coefficient rows for the current half-width; the output blends the two rows around
/// the fractional position (linear interpolation across phases keeps the interpolation error near -108 dB at 0.9 of
/// Nyquist; see Smith, "Digital Audio Resampling Home Page", on interpolating between filter phases). For ratios
/// below 1 the prototype is stretched by 1/ratio so that its cutoff follows the output Nyquist frequency.
/// </summary>
internal sealed class StreamResampler : IResampler
{
    private const int Phases = 512;
    private const int PhaseShift = 23;                       // 32 fraction bits - 9 phase bits
    private const float PhaseWeightScale = 1f / (1 << PhaseShift);
    private const int Block = 256;                           // input frames appended per step
    private const int Slack = 16;                            // zero-coefficient padding may read this far past the data

    private readonly int _channels;
    private readonly ResamplerQuality _quality;
    private readonly FilterProfile? _profile;
    private readonly double _minRatio, _maxRatio, _sMin;
    private readonly int _maxHalfWidth;
    private readonly float[][] _buf;
    private readonly int _cap;
    private readonly float[] _bank;

    private double _ratio;
    private ulong _step;
    private double _designScale;
    private int _halfWidth;   // input samples on each side of the interpolation point
    private int _taps;        // padded row length
    private int _bufCount;
    private ulong _pos;       // 32.32 index into _buf of the next output's time
    private long _origin;     // absolute input index of _buf[0]
    private long _totalIn;

    public StreamResampler(int channels, ResamplerQuality quality, double ratio, ResamplerOptions options)
    {
        ArgumentOutOfRangeException.ThrowIfLessThan(channels, 1);
        ArgumentOutOfRangeException.ThrowIfGreaterThan(channels, AudioLimits.Default.MaxChannels);
        if (!Enum.IsDefined(quality)) throw new ArgumentOutOfRangeException(nameof(quality));
        ArgumentNullException.ThrowIfNull(options);
        if (!(options.MinRatio >= 1.0 / 64 && options.MinRatio <= options.MaxRatio && options.MaxRatio <= 64))
            throw new ArgumentOutOfRangeException(nameof(options), "Ratio bounds must satisfy 1/64 <= MinRatio <= MaxRatio <= 64.");
        _minRatio = options.MinRatio;
        _maxRatio = options.MaxRatio;
        _channels = channels;
        _quality = quality;
        CheckRatio(ratio);
        _sMin = Math.Min(1.0, _minRatio);

        if (quality == ResamplerQuality.Linear)
        {
            _maxHalfWidth = 1;
            _halfWidth = 1;
            _bank = [];
        }
        else
        {
            _profile = FilterProfile.For(quality);
            _maxHalfWidth = HalfWidthFor(_sMin);
            _bank = new float[(Phases + 1) * RoundUp16(2 * _maxHalfWidth)];
        }

        int lead = _maxHalfWidth - 1;
        _cap = lead + _maxHalfWidth + Block + 2 * Slack + RoundUp16(2 * _maxHalfWidth);
        _buf = new float[channels][];
        for (int c = 0; c < channels; c++) _buf[c] = new float[_cap];

        _ratio = ratio;
        _step = StepFor(ratio);
        if (_profile != null) Design(Math.Min(1.0, ratio));
        Reset();
    }

    public int Channels => _channels;
    public ResamplerQuality Quality => _quality;
    public double Ratio => _ratio;
    public double LatencyFrames => _halfWidth * _ratio;

    public void SetRatio(double ratio)
    {
        CheckRatio(ratio);
        _ratio = ratio;
        _step = StepFor(ratio);
        if (_profile == null) return;
        // Keep the current bank while its cutoff stays within a few percent of the ideal and not above it (a cutoff
        // above the output Nyquist frequency would let aliases through). Otherwise redesign; when the ratio fell,
        // design 2 % low so that a continuing glide does not redesign on every call.
        double s = Math.Min(1.0, ratio);
        if (s >= _designScale && (s <= _designScale * 1.05 || _designScale >= 1.0)) return;
        Design(s < _designScale ? Math.Max(_sMin, s * 0.98) : s);
    }

    public int MaxOutputFrames(int inputFrames)
    {
        ArgumentOutOfRangeException.ThrowIfNegative(inputFrames);
        double ahead = _bufCount + (double)inputFrames - _pos * (1.0 / 4294967296.0);
        return (int)Math.Min(int.MaxValue / 2, Math.Max(0, ahead) * _ratio + 2);
    }

    public void Process(ReadOnlySpan<float> input, Span<float> output, out int inputFramesUsed, out int outputFramesWritten)
    {
        int inFrames = input.Length / _channels, outFrames = output.Length / _channels;
        int used = 0, written = 0;
        while (true)
        {
            written += Generate(output, written, outFrames, long.MaxValue);
            if (written >= outFrames || used >= inFrames) break;
            int n = Math.Min(inFrames - used, Block);
            n = Append(input.Slice(used * _channels, n * _channels), n);
            used += n;
            _totalIn += n;
        }
        inputFramesUsed = used;
        outputFramesWritten = written;
    }

    public int Flush(Span<float> output)
    {
        int outFrames = output.Length / _channels, written = 0;
        while (written < outFrames)
        {
            written += Generate(output, written, outFrames, _totalIn);
            if (written >= outFrames) break;
            if (_origin + (long)(_pos >> 32) >= _totalIn)
            {
                Reset();
                break;
            }
            AppendZeros();
        }
        return written;
    }

    public void Reset()
    {
        foreach (var b in _buf) Array.Clear(b);
        int lead = _maxHalfWidth - 1;
        _bufCount = lead;
        _pos = (ulong)lead << 32;
        _origin = -lead;
        _totalIn = 0;
    }

    private void CheckRatio(double ratio)
    {
        if (!(ratio >= _minRatio && ratio <= _maxRatio))
            throw new ArgumentOutOfRangeException(nameof(ratio), ratio, $"Ratio must be finite and within {_minRatio}..{_maxRatio}.");
    }

    private static ulong StepFor(double ratio) => (ulong)Math.Round(4294967296.0 / ratio);

    private static int RoundUp16(int n) => (n + 15) & ~15;

    private int HalfWidthFor(double scale) => (int)Math.Ceiling(_profile!.HalfWidth / scale - 1e-9);

    /// <summary>Fills the coefficient bank for a prototype stretched to <paramref name="scale"/> (at most 1).</summary>
    private void Design(double scale)
    {
        var p = _profile!;
        _designScale = scale;
        int hs = Math.Min(HalfWidthFor(scale), _maxHalfWidth);
        _halfWidth = hs;
        int taps = _taps = RoundUp16(2 * hs);
        var table = p.Table;
        int half = p.HalfWidth;
        Array.Clear(_bank, 0, (Phases + 1) * taps);
        for (int ph = 0; ph <= Phases; ph++)
        {
            var row = _bank.AsSpan(ph * taps, taps);
            double f = (double)ph / Phases;
            double sum = 0;
            for (int j = 0; j < 2 * hs; j++)
            {
                double u = Math.Abs((hs - 1 - j + f) * scale);
                if (u >= half) continue;
                double pos = u * FilterProfile.TableResolution;
                int i0 = (int)pos;
                float w = (float)(pos - i0);
                float v = table[i0] + (table[i0 + 1] - table[i0]) * w;
                row[j] = v;
                sum += v;
            }
            // Unit DC gain for every phase (the ideal sum is exactly 1; truncation perturbs it slightly).
            float norm = (float)(1.0 / sum);
            for (int j = 0; j < 2 * hs; j++) row[j] *= norm;
        }
    }

    private int Append(ReadOnlySpan<float> input, int frames)
    {
        Compact();
        frames = Math.Min(frames, _cap - Slack - _bufCount);
        int c = _channels;
        if (c == 1)
        {
            input.Slice(0, frames).CopyTo(_buf[0].AsSpan(_bufCount));
        }
        else
        {
            for (int ch = 0; ch < c; ch++)
            {
                var dst = _buf[ch].AsSpan(_bufCount, frames);
                for (int i = 0; i < dst.Length; i++) dst[i] = input[i * c + ch];
            }
        }
        _bufCount += frames;
        return frames;
    }

    private void AppendZeros()
    {
        Compact();
        int frames = Math.Min(Block, _cap - Slack - _bufCount);
        foreach (var b in _buf) b.AsSpan(_bufCount, frames).Clear();
        _bufCount += frames;
    }

    /// <summary>Drops history that no future window can reach, keeping the <c>_maxHalfWidth - 1</c> frames before the read position.</summary>
    private void Compact()
    {
        // The read position can sit past the data when the ratio is below 1; never drop frames it will still need.
        int shift = Math.Min((int)(_pos >> 32), _bufCount) - (_maxHalfWidth - 1);
        if (shift <= 0 || _bufCount + Block <= _cap - Slack) return;
        foreach (var b in _buf) b.AsSpan(shift, _bufCount - shift).CopyTo(b);
        _bufCount -= shift;
        _pos -= (ulong)shift << 32;
        _origin += shift;
    }

    private int Generate(Span<float> output, int written, int outFrames, long limitIndex)
    {
        int start = written;
        int c = _channels, hs = _halfWidth;
        ulong pos = _pos, step = _step;
        if (_profile == null)
        {
            while (written < outFrames)
            {
                int ip = (int)(pos >> 32);
                if (ip + 1 >= _bufCount || _origin + ip >= limitIndex) break;
                float f = (float)((uint)pos * (1.0 / 4294967296.0));
                for (int ch = 0; ch < c; ch++)
                {
                    float x0 = _buf[ch][ip], x1 = _buf[ch][ip + 1];
                    output[written * c + ch] = x0 + f * (x1 - x0);
                }
                written++;
                pos += step;
            }
        }
        else
        {
            int taps = _taps;
            while (written < outFrames)
            {
                int ip = (int)(pos >> 32);
                if (ip + hs >= _bufCount || _origin + ip >= limitIndex) break;
                uint frac = (uint)pos;
                int ph = (int)(frac >> PhaseShift);
                float w = (frac & ((1u << PhaseShift) - 1)) * PhaseWeightScale;
                var rowA = _bank.AsSpan(ph * taps, taps);
                var rowB = _bank.AsSpan((ph + 1) * taps, taps);
                int first = ip - (hs - 1);
                for (int ch = 0; ch < c; ch++)
                {
                    output[written * c + ch] = ResampleKernels.DotBlend(_buf[ch].AsSpan(first, taps), rowA, rowB, w);
                }
                written++;
                pos += step;
            }
        }
        _pos = pos;
        return written - start;
    }
}
