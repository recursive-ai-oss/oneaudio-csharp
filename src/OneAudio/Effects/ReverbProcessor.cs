using System.Numerics;

namespace OneAudio.Effects;

/// <summary>
/// Algorithmic reverb on a planar buffer (render thread only): per channel a pre-delay and four Schroeder allpass diffusers
/// (Schroeder, "Natural sounding artificial reverberation", JAES 1962), then an eight-line feedback delay network with a
/// Householder feedback matrix (Jot and Chaigne, "Digital delay networks for designing artificial reverberators", AES 1991;
/// the matrix is I - (2/8) 1 1^T, which is orthogonal, so the loop is lossless before the per-line filters).
/// <para>
/// Decay: each line has a gain g = 10^(-3 L / (fs RT60)) for its length L, which gives every line, and so the network,
/// the requested RT60. Damping adds a one-pole low-pass per line, H(z) = g (1 - p) / (1 - p z^-1), with p chosen so that the gain
/// at Nyquist equals the gain that a shorter RT60 would give (Jot and Chaigne's design). Channels are injected into and read
/// from the lines with different rows of an 8 x 8 Walsh-Hadamard sign matrix, which makes the channels' tails uncorrelated.
/// </para>
/// </summary>
internal sealed class ReverbProcessor
{
    private const int Lines = 8;
    private const int SubBlock = 32;
    private const int Stages = 4;
    private const float ApGain = 0.5f;
    // Line lengths in samples at 48 kHz (about 26 to 79 ms), chosen to share no small factors; scaled for other rates.
    private static readonly int[] LineBase = [1259, 1583, 1951, 2287, 2663, 3011, 3407, 3797];
    // Diffuser lengths at 48 kHz: primes between 2 and 7 ms.
    private static readonly int[] ApBase = [113, 167, 241, 331];

    private struct Ramp
    {
        public double Cur, Tgt, Step;
        public readonly bool Moving => Cur != Tgt;

        public void Set(double target, int frames)
        {
            Tgt = target;
            if (frames <= 0) { Cur = target; Step = 0; }
            else Step = (target - Cur) / frames;
        }

        public void Advance(int n)
        {
            if (Cur == Tgt) return;
            Cur += Step * n;
            if (Step >= 0 ? Cur >= Tgt : Cur <= Tgt) Cur = Tgt;
        }
    }

    private readonly int _ch;
    private readonly double _sr;
    private readonly float[] _sign;           // [channel * Lines + line]
    private readonly float[] _lineBuf;        // [line * _lineSize + i]
    private readonly int _lineSize, _lineMask;
    private int _w, _pw;
    private readonly float[] _damp = new float[Lines];
    private readonly float[] _lineDelay = new float[Lines];
    private readonly float[] _lineIn = new float[Lines];      // g (1 - p)
    private readonly float[] _linePole = new float[Lines];    // p
    private readonly int[] _lineMax = new int[Lines];
    private readonly float[] _preBuf;         // [channel * _preSize + i]
    private readonly int _preSize, _preMask;
    private readonly float[] _apBuf;
    private readonly int[] _apOff, _apLen, _apPos;
    private readonly float[] _x, _dif, _y;
    private Ramp _room, _logDecay, _damping, _pre, _wet, _dry;
    private float _norm;
    private bool _dirty = true;
    private int _quiet;
    private readonly int _apTotal, _lineMaxAll;

    public bool Ringing { get; private set; }

    public ReverbProcessor(int channels, int sampleRate)
    {
        _ch = channels;
        _sr = sampleRate;
        double k = sampleRate / 48000.0;
        _sign = new float[channels * Lines];
        for (int c = 0; c < channels; c++)
        {
            int row = 1 + c % (Lines - 1);
            for (int i = 0; i < Lines; i++)
                _sign[c * Lines + i] = (BitOperations.PopCount((uint)(row & i)) & 1) == 0 ? 1f : -1f;
        }
        int maxLine = 0;
        for (int i = 0; i < Lines; i++)
        {
            _lineMax[i] = (int)Math.Ceiling(LineBase[i] * k);
            maxLine = Math.Max(maxLine, _lineMax[i]);
        }
        _lineSize = (int)BitOperations.RoundUpToPowerOf2((uint)(maxLine + 4));
        _lineMask = _lineSize - 1;
        _lineBuf = new float[Lines * _lineSize];
        _preSize = (int)BitOperations.RoundUpToPowerOf2((uint)(ReverbSettings.MaxPreDelayMilliseconds * 0.001 * sampleRate + 4));
        _preMask = _preSize - 1;
        _preBuf = new float[channels * _preSize];
        _apLen = new int[Stages];
        _apOff = new int[channels * Stages];
        int total = 0;
        for (int s = 0; s < Stages; s++) _apLen[s] = Math.Max(2, (int)Math.Round(ApBase[s] * k));
        for (int c = 0; c < channels; c++)
            for (int s = 0; s < Stages; s++) { _apOff[c * Stages + s] = total; total += _apLen[s]; }
        _apBuf = new float[total];
        _apPos = new int[channels * Stages];
        _x = new float[channels];
        _dif = new float[channels];
        _y = new float[Lines];
        foreach (int len in _apLen) _apTotal += len;
        _lineMaxAll = maxLine;
        Configure(new ReverbSettings(), 0);
        Reset();
    }

    /// <summary>Moves the parameters to <paramref name="s"/> over <paramref name="rampFrames"/> (0 = at once).</summary>
    public void Configure(ReverbSettings s, int rampFrames)
    {
        _room.Set(0.25 + 0.75 * s.RoomSize, rampFrames);
        _logDecay.Set(Math.Log(s.DecaySeconds), rampFrames);
        _damping.Set(s.Damping, rampFrames);
        _pre.Set(s.PreDelayMilliseconds * 0.001 * _sr, rampFrames);
        _wet.Set(s.Wet, rampFrames);
        _dry.Set(s.Dry, rampFrames);
        _dirty = true;
    }

    /// <summary>Clears all state (and reports silence), as when a slot is handed to a bus.</summary>
    public void Reset()
    {
        Array.Clear(_lineBuf);
        Array.Clear(_preBuf);
        Array.Clear(_apBuf);
        Array.Clear(_damp);
        Ringing = false;
        _quiet = int.MaxValue / 2;
    }

    private void Recompute()
    {
        double room = _room.Cur, t = Math.Exp(_logDecay.Cur), d = _damping.Cur;
        bool gliding = _room.Moving;
        double tHf = t * (1.0 - 0.9 * d);
        double meanDelay = 0;
        for (int i = 0; i < Lines; i++)
        {
            double len = LineBase[i] * (_sr / 48000.0) * room;
            // While the size is gliding the read position is fractional (linear interpolation); at rest it is whole, which
            // keeps the interpolation's high-frequency loss out of the loop.
            if (!gliding) len = Math.Round(len);
            len = Math.Clamp(len, 2.0, _lineMax[i]);
            _lineDelay[i] = (float)len;
            meanDelay += len;
            double gDc = Math.Pow(10.0, -3.0 * len / (_sr * t));
            double gNy = Math.Pow(10.0, -3.0 * len / (_sr * tHf));
            double p = (gDc - gNy) / (gDc + gNy);
            _linePole[i] = (float)p;
            _lineIn[i] = (float)(gDc * (1.0 - p));
        }
        meanDelay /= Lines;
        double gMean = Math.Pow(10.0, -3.0 * meanDelay / (_sr * t));
        // Keeps the wet level about the same whatever the decay. Noise injected with gain a into the network and read with gain b
        // through lines of loop gain g comes out with power 8 a^2 b^2 g^2 / (1 - g^2) times the input power; a = b = norm makes that 1.
        double g2 = Math.Max(gMean * gMean, 1e-6);
        _norm = (float)(0.7 * Math.Min(1.5, Math.Pow((1.0 - g2) / (Lines * g2), 0.25)));
        _dirty = false;
    }

    public void Process(float[] buf, int stride, int frames)
    {
        int ch = _ch;
        float inMax = 0f, wetMax = 0f;
        for (int f0 = 0; f0 < frames; f0 += SubBlock)
        {
            int m = Math.Min(SubBlock, frames - f0);
            double wet0 = _wet.Cur, dry0 = _dry.Cur;
            bool moving = _room.Moving || _logDecay.Moving || _damping.Moving;
            _room.Advance(m); _logDecay.Advance(m); _damping.Advance(m); _pre.Advance(m); _wet.Advance(m); _dry.Advance(m);
            if (moving || _dirty) Recompute();
            float wetStep = (float)((_wet.Cur - wet0) / m), dryStep = (float)((_dry.Cur - dry0) / m);
            float wet = (float)wet0, dry = (float)dry0;
            float preDelay = (float)_pre.Cur;
            int preI = (int)preDelay;
            float preF = preDelay - preI;
            float norm = _norm;
            for (int f = f0; f < f0 + m; f++)
            {
                wet += wetStep;
                dry += dryStep;
                int w = _w, pw = _pw;
                // Pre-delay and diffusion, per channel.
                for (int c = 0; c < ch; c++)
                {
                    float x = buf[c * stride + f];
                    _x[c] = x;
                    inMax = Math.Max(inMax, Math.Abs(x));
                    int pb = c * _preSize;
                    _preBuf[pb + (pw & _preMask)] = x;
                    float s = _preBuf[pb + ((pw - preI) & _preMask)] * (1f - preF) + _preBuf[pb + ((pw - preI - 1) & _preMask)] * preF;
                    for (int st = 0; st < Stages; st++)
                    {
                        int k = c * Stages + st;
                        int at = _apOff[k] + _apPos[k];
                        float held = _apBuf[at];
                        float v = s + ApGain * held;
                        s = held - ApGain * v;
                        _apBuf[at] = v;
                        if (++_apPos[k] == _apLen[st]) _apPos[k] = 0;
                    }
                    _dif[c] = s;
                }
                // Delay network.
                float sum = 0f;
                for (int i = 0; i < Lines; i++)
                {
                    float dl = _lineDelay[i];
                    int di = (int)dl;
                    float fr = dl - di;
                    int b = i * _lineSize;
                    float d = _lineBuf[b + ((w - di) & _lineMask)] * (1f - fr) + _lineBuf[b + ((w - di - 1) & _lineMask)] * fr;
                    float y = _lineIn[i] * d + _linePole[i] * _damp[i];
                    // Flushing here keeps every value that re-enters the loop out of the denormal range.
                    if (Math.Abs(y) < 1e-15f) y = 0f;
                    _damp[i] = y;
                    _y[i] = y;
                    sum += y;
                }
                float mean = sum * 0.25f; // 2/8 of the sum: the Householder reflection
                for (int i = 0; i < Lines; i++)
                {
                    float inj = 0f;
                    for (int c = 0; c < ch; c++) inj += _sign[c * Lines + i] * _dif[c];
                    _lineBuf[i * _lineSize + (w & _lineMask)] = _y[i] - mean + norm * inj;
                }
                for (int c = 0; c < ch; c++)
                {
                    float o = 0f;
                    for (int i = 0; i < Lines; i++) o += _sign[c * Lines + i] * _y[i];
                    o *= norm;
                    wetMax = Math.Max(wetMax, Math.Abs(o));
                    buf[c * stride + f] = dry * _x[c] + wet * o;
                }
                _w = (w + 1) & _lineMask;
                _pw = (pw + 1) & _preMask;
            }
        }
        _quiet = inMax > 1e-9f ? 0 : (int)Math.Min((long)_quiet + frames, int.MaxValue / 2);
        // Input still on its way to the first output (pre-delay, diffusers, the shortest line) counts as ringing even though nothing
        // has come out yet; after that the tail's own level decides.
        Ringing = wetMax > 1e-6f || _quiet < (int)_pre.Cur + _apTotal + _lineMaxAll + 64;
        if (!Ringing) Reset();
    }
}
