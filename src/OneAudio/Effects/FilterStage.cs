using OneAudio.Kernels;

namespace OneAudio.Effects;

/// <summary>
/// One second-order filter (low-pass or high-pass) over a planar buffer, with per-channel state. Cutoff (in log2 Hz) and
/// Q move linearly over a ramp, with coefficients refreshed every <see cref="SubBlock"/> frames, so sweeps are free of zipper
/// noise. The trapezoidal state-variable form keeps the filter stable however the parameters move. Render thread only.
/// </summary>
internal sealed class FilterStage
{
    private const int SubBlock = 16;
    public const double MinCutoffHz = 1.0;
    public const double MinQ = 0.1;
    public const double MaxQ = 24.0;

    private readonly bool _highPass;
    private readonly double _sampleRate;
    private readonly float[] _ic1, _ic2;
    private readonly double _maxCutoff;       // user limit, just under Nyquist
    private readonly double _transparent;     // cutoff at which the stage passes everything (LP at Nyquist, HP at 1 Hz)

    private double _lc, _lcTarget, _lcStep;   // log2 of the cutoff in Hz
    private double _q, _qTarget, _qStep;
    private int _left;
    private bool _disabling;
    private bool _coefValid;
    private SvfCoefficients _coef;

    public bool Active { get; private set; }
    public bool Ringing { get; private set; }

    public FilterStage(bool highPass, int maxChannels, int sampleRate)
    {
        _highPass = highPass;
        _sampleRate = sampleRate;
        _ic1 = new float[maxChannels];
        _ic2 = new float[maxChannels];
        _maxCutoff = 0.49 * sampleRate;
        _transparent = highPass ? MinCutoffHz : 0.4995 * sampleRate;
    }

    /// <summary>Turns the filter on, or moves it, to <paramref name="cutoffHz"/> and <paramref name="q"/> over <paramref name="rampFrames"/> (0 = at once).</summary>
    public void Set(double cutoffHz, double q, int rampFrames)
    {
        double target = Math.Log2(Math.Clamp(cutoffHz, MinCutoffHz, _maxCutoff));
        double qt = Math.Clamp(q, MinQ, MaxQ);
        if (!Active)
        {
            Active = true;
            Ringing = false;
            Array.Clear(_ic1);
            Array.Clear(_ic2);
            // Start from the transparent setting so that switching on is a glide, not a step, unless a jump was asked for.
            _lc = rampFrames > 0 ? Math.Log2(_transparent) : target;
            _q = qt;
        }
        _disabling = false;
        Retarget(target, qt, rampFrames);
    }

    /// <summary>Glides to the transparent setting over <paramref name="rampFrames"/> and then switches off (0 = at once).</summary>
    public void Clear(int rampFrames)
    {
        if (!Active) return;
        if (rampFrames <= 0) { Active = false; Ringing = false; return; }
        _disabling = true;
        Retarget(Math.Log2(_transparent), _q, rampFrames);
    }

    private void Retarget(double lcTarget, double qTarget, int rampFrames)
    {
        _lcTarget = lcTarget;
        _qTarget = qTarget;
        if (rampFrames <= 0) { _lc = lcTarget; _q = qTarget; _left = 0; _lcStep = _qStep = 0; }
        else
        {
            _left = rampFrames;
            _lcStep = (lcTarget - _lc) / rampFrames;
            _qStep = (qTarget - _q) / rampFrames;
        }
        _coefValid = false;
    }

    public void Reset()
    {
        Array.Clear(_ic1);
        Array.Clear(_ic2);
        Ringing = false;
    }

    /// <summary>Filters <paramref name="frames"/> frames of the first <paramref name="channels"/> rows of <paramref name="buf"/> in place.</summary>
    public void Process(float[] buf, int stride, int channels, int frames)
    {
        if (!Active || frames <= 0) return;
        int f = 0;
        while (f < frames && Active)
        {
            int len;
            if (_left > 0)
            {
                len = Math.Min(SubBlock, frames - f);
                if (_left <= len)
                {
                    len = _left;
                    _left = 0;
                    _lc = _lcTarget; _q = _qTarget;
                }
                else
                {
                    _left -= len;
                    _lc += _lcStep * len;
                    _q += _qStep * len;
                }
                _coefValid = false;
            }
            else len = frames - f;
            if (!_coefValid)
            {
                _coef = new SvfCoefficients(Math.Pow(2.0, _lc), _q, _sampleRate);
                _coefValid = true;
            }
            EffectKernels.Svf(buf, stride, channels, f, len, _coef, _ic1, _ic2, _highPass);
            f += len;
            if (_left == 0 && _disabling) { Active = false; Reset(); return; }
        }
        Settle(channels);
    }

    // Keeps the states finite and out of the denormal range, and records whether the filter still rings.
    private void Settle(int channels)
    {
        float big = 0f;
        for (int c = 0; c < channels; c++)
        {
            float a = _ic1[c], b = _ic2[c];
            if (!float.IsFinite(a) || !float.IsFinite(b)) { _ic1[c] = 0f; _ic2[c] = 0f; continue; }
            float ma = Math.Abs(a), mb = Math.Abs(b);
            if (ma < 1e-20f) _ic1[c] = 0f;
            if (mb < 1e-20f) _ic2[c] = 0f;
            big = Math.Max(big, Math.Max(ma, mb));
        }
        Ringing = big > 1e-7f;
    }
}
