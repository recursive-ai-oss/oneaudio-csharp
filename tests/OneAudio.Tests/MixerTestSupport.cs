namespace OneAudio.Tests;

/// <summary>
/// A minimal streaming linear-interpolation converter used to drive the mixer's resampling path in tests, standing in
/// for the library's converters. Output sample k sits at input position k / Ratio; the first output equals the first input.
/// </summary>
internal sealed class LinearTestResampler : IResampler
{
    private readonly int _ch;
    private readonly float[] _prev;
    private double _pos;      // position of the next output in input frames, relative to the start of the next input block (x[-1] = _prev)
    private bool _primed;

    public LinearTestResampler(int channels, ResamplerQuality quality = ResamplerQuality.Linear)
    {
        _ch = channels;
        Quality = quality;
        _prev = new float[channels];
        Ratio = 1.0;
    }

    public int Channels => _ch;
    public ResamplerQuality Quality { get; }
    public double Ratio { get; private set; }
    public double LatencyFrames => 0;

    public void SetRatio(double ratio)
    {
        if (!double.IsFinite(ratio) || ratio <= 0 || ratio > 1000) throw new ArgumentOutOfRangeException(nameof(ratio));
        Ratio = ratio;
    }

    public int MaxOutputFrames(int inputFrames) => (int)Math.Ceiling(inputFrames * Ratio) + 2;

    public void Process(ReadOnlySpan<float> input, Span<float> output, out int inputFramesUsed, out int outputFramesWritten)
    {
        int inFrames = input.Length / _ch;
        int outCap = output.Length / _ch;
        inputFramesUsed = 0;
        outputFramesWritten = 0;
        if (inFrames == 0) return;
        if (!_primed)
        {
            for (int c = 0; c < _ch; c++) _prev[c] = input[c];
            _pos = 0;
            _primed = true;
        }
        double step = 1.0 / Ratio;
        int written = 0;
        while (written < outCap)
        {
            int i0 = (int)Math.Floor(_pos);
            if (i0 + 1 > inFrames - 1) break;
            float t = (float)(_pos - i0);
            for (int c = 0; c < _ch; c++)
            {
                float a = i0 < 0 ? _prev[c] : input[i0 * _ch + c];
                float b = input[(i0 + 1) * _ch + c];
                output[written * _ch + c] = a + (b - a) * t;
            }
            written++;
            _pos += step;
        }
        int used = written == outCap ? Math.Clamp((int)Math.Floor(_pos), 0, inFrames) : inFrames;
        if (used > 0) for (int c = 0; c < _ch; c++) _prev[c] = input[(used - 1) * _ch + c];
        _pos -= used;
        inputFramesUsed = used;
        outputFramesWritten = written;
    }

    public int Flush(Span<float> output) => 0;

    public void Reset()
    {
        _primed = false;
        _pos = 0;
        Array.Clear(_prev);
    }
}
