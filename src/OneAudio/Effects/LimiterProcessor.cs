namespace OneAudio.Effects;

/// <summary>
/// Lookahead brickwall limiter on a planar buffer (render thread only). The channels are linked: one gain, set by the loudest
/// channel, applies to all, so the stereo image does not shift.
/// <para>
/// Method: the signal is delayed by the lookahead L. A sliding maximum over the last L+1 sample peaks gives, for every sample,
/// the gain that keeps the loudest of the next L samples at the ceiling; that gain drops instantly, recovers with a one-pole
/// release, and is then averaged over L samples. The average turns the drop into a linear ramp that reaches its full depth
/// exactly when the peak leaves the delay line, and an average of values that are each at or below the needed gain cannot exceed
/// it (the structure follows the lookahead limiters described by Giannoulis, Massberg and Reiss, "Digital Dynamic Range Compressor
/// Design", JAES 2012, with a moving-average attack). A final clamp at the ceiling covers float rounding and ceiling changes made
/// while loud audio is already in the delay line. Only sample peaks are limited; inter-sample peaks can exceed the ceiling by
/// up to about 3 dB on worst-case material, which a ceiling of -1 dBFS or lower leaves room for.
/// </para>
/// </summary>
internal sealed class LimiterProcessor
{
    private readonly int _look;
    private readonly float[] _ring;       // delay line, planar: channel c at c * _look
    private int _pos;
    private readonly float[] _dqVal;      // sliding-maximum deque of peaks, circular
    private readonly long[] _dqIdx;
    private int _dqHead, _dqCount;
    private readonly float[] _rRing;      // the last _look gain values, for the moving average
    private int _rPos;
    private double _rSum;
    private float _r = 1f;
    private long _t;
    private readonly float[] _peak, _gain;
    private bool _enabled;
    private float _ceiling = 1f;
    private float _releaseStep = 1f;      // 1 - release coefficient
    private long _silent;
    private float _minGain = 1f;

    public LimiterProcessor(int lookaheadFrames, int channels, int maxBlock)
    {
        _look = lookaheadFrames;
        _ring = new float[channels * lookaheadFrames];
        _dqVal = new float[lookaheadFrames + 2];
        _dqIdx = new long[lookaheadFrames + 2];
        _rRing = new float[lookaheadFrames];
        Array.Fill(_rRing, 1f);
        _rSum = lookaheadFrames;
        _peak = new float[maxBlock];
        _gain = new float[maxBlock];
    }

    public int LookaheadFrames => _look;

    /// <summary>True when the delay line holds only silence, so a silent input needs no processing.</summary>
    public bool Idle => _silent > _look;

    /// <summary>Smallest gain used for the last processed block (1 = no reduction).</summary>
    public float MinGain => _minGain;

    public void Configure(bool enabled, float ceilingLinear, float releaseSeconds, int sampleRate)
    {
        _enabled = enabled;
        _ceiling = ceilingLinear;
        _releaseStep = (float)(1.0 - Math.Exp(-1.0 / (releaseSeconds * sampleRate)));
    }

    public void Process(float[] buf, int stride, int channels, int frames)
    {
        int look = _look;
        Kernels.EffectKernels.PeakAcross(buf, stride, channels, frames, _peak);
        float ceil = _ceiling;
        bool enabled = _enabled;
        int cap = _dqVal.Length;
        float minGain = 1f;
        bool any = false;
        for (int f = 0; f < frames; f++)
        {
            float v = _peak[f];
            if (v > 0f) any = true;
            long idx = _t + f;
            // Sliding maximum over [idx - look, idx].
            while (_dqCount > 0)
            {
                int back = _dqHead + _dqCount - 1;
                if (back >= cap) back -= cap;
                if (_dqVal[back] > v) break;
                _dqCount--;
            }
            int slot = _dqHead + _dqCount;
            if (slot >= cap) slot -= cap;
            _dqVal[slot] = v;
            _dqIdx[slot] = idx;
            _dqCount++;
            while (_dqIdx[_dqHead] < idx - look)
            {
                _dqHead++;
                if (_dqHead == cap) _dqHead = 0;
                _dqCount--;
            }
            float top = _dqVal[_dqHead];
            float need = enabled && top > ceil ? ceil / top : 1f;
            float recovered = _r + (1f - _r) * _releaseStep;
            float r = need < recovered ? need : recovered;
            if (r > 1f) r = 1f;
            _r = r;
            _rSum += (double)r - _rRing[_rPos];
            _rRing[_rPos] = r;
            if (++_rPos == look) _rPos = 0;
            float g = (float)(_rSum / look);
            _gain[f] = g;
            if (g < minGain) minGain = g;
        }
        _t += frames;
        _silent = any ? 0 : _silent + frames;
        _minGain = minGain;

        float clamp = enabled ? ceil : float.MaxValue;
        for (int c = 0; c < channels; c++)
        {
            int done = 0, p = _pos;
            while (done < frames)
            {
                int len = Math.Min(frames - done, look - p);
                Kernels.EffectKernels.SwapScaleClamp(
                    buf.AsSpan(c * stride + done, len), _ring.AsSpan(c * look + p, len), _gain.AsSpan(done, len), clamp);
                done += len;
                p += len;
                if (p == look) p = 0;
            }
        }
        _pos = (_pos + frames) % look;
    }
}
