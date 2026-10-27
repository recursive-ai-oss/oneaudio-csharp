using OneAudio.Mixing;

namespace OneAudio;

/// <summary>
/// Wraps a decoder and delivers its audio at another rate and channel count, optionally scaled by a gain. Streams block
/// by block, so memory use does not depend on the length of the source. Not seekable. Owns the wrapped decoder.
/// </summary>
internal sealed class ConvertingDecoder : IAudioDecoder
{
    private const int BlockFrames = 4096;

    private readonly IAudioDecoder _source;
    private readonly int _inCh, _midCh, _outCh;
    private readonly float[]? _preMatrix, _postMatrix;
    private readonly IResampler? _resampler;
    private readonly float _gain;
    private readonly long _expected;
    private readonly float[] _in, _mid, _res, _pend;
    private int _pendStart, _pendCount;
    private bool _sourceDone, _flushed;
    private long _produced;
    private bool _disposed;

    private ConvertingDecoder(IAudioDecoder source, int rate, int channels, ResamplerQuality quality, double gain)
    {
        _source = source;
        var f = source.Format;
        _inCh = f.Channels;
        _outCh = channels;
        _gain = (float)gain;
        bool upmix = _outCh > _inCh;
        _midCh = upmix ? _inCh : _outCh;
        var inMask = ChannelMapper.Resolve(f.ChannelMask, _inCh);
        var outMask = AudioFormat.DefaultMask(_outCh);
        if (_inCh != _midCh) _preMatrix = BuildMatrix(_inCh, inMask, _midCh, outMask);
        if (_midCh != _outCh) _postMatrix = BuildMatrix(_midCh, inMask, _outCh, outMask);

        long remaining = source.LengthFrames < 0 ? -1 : Math.Max(0, source.LengthFrames - source.PositionFrames);
        _expected = remaining;
        if (rate != f.SampleRate)
        {
            double ratio = (double)rate / f.SampleRate;
            if (ratio < 1.0 / 64 || ratio > 64) throw new ArgumentOutOfRangeException(nameof(rate), "Sample rates may differ by at most a factor of 64.");
            _resampler = AudioResampler.Create(_midCh, quality, ratio, new ResamplerOptions { MinRatio = Math.Min(1.0, ratio), MaxRatio = Math.Max(1.0, ratio) });
            if (remaining >= 0) _expected = (long)Math.Round(remaining * ratio, MidpointRounding.AwayFromZero);
        }

        Format = new AudioFormat(rate, _outCh, outMask, f.NativeType, f.BitsPerSample);
        int maxOut = _resampler?.MaxOutputFrames(BlockFrames) ?? BlockFrames;
        _in = new float[BlockFrames * _inCh];
        _mid = _preMatrix != null ? new float[BlockFrames * _midCh] : _in;
        _res = _resampler != null ? new float[maxOut * _midCh] : _mid;
        _pend = new float[Math.Max(maxOut, BlockFrames) * _outCh];
    }

    /// <summary>
    /// Returns <paramref name="source"/> itself when nothing needs converting; otherwise a converting wrapper that takes
    /// over disposing it. A zero rate or channel count in the options keeps the source value.
    /// </summary>
    public static IAudioDecoder Wrap(IAudioDecoder source, ConversionOptions? options, double gain = 1.0)
    {
        var f = source.Format;
        int rate = options is { SampleRate: > 0 } ? options.SampleRate : f.SampleRate;
        int channels = options is { Channels: > 0 } ? options.Channels : f.Channels;
        if (rate == f.SampleRate && channels == f.Channels && gain == 1.0) return source;
        try
        {
            if (rate < 1) throw new ArgumentOutOfRangeException(nameof(options), "SampleRate must not be negative.");
            (options?.Limits ?? AudioLimits.Default).CheckSampleRate(rate);
            (options?.Limits ?? AudioLimits.Default).CheckChannels(channels);
            return new ConvertingDecoder(source, rate, channels, options?.ResamplerQuality ?? ResamplerQuality.Polyphase, gain);
        }
        catch
        {
            source.Dispose();
            throw;
        }
    }

    // Mono to two or more channels is a plain copy onto front left and right; everything else uses the mixer's speaker rules.
    private static float[] BuildMatrix(int inCh, ChannelMask inMask, int outCh, ChannelMask outMask)
    {
        var m = new float[outCh * inCh];
        if (inCh == 1 && outCh >= 2)
        {
            m[0] = 1f;
            m[inCh + 0] = 1f;
        }
        else
        {
            ChannelMapper.Build(m, inCh, inMask, outCh, outMask, 0f);
        }
        return m;
    }

    private static void Apply(float[] matrix, int inCh, int outCh, ReadOnlySpan<float> src, Span<float> dst, int frames)
    {
        for (int n = 0; n < frames; n++)
        {
            var s = src.Slice(n * inCh, inCh);
            var d = dst.Slice(n * outCh, outCh);
            for (int o = 0; o < outCh; o++)
            {
                float acc = 0;
                var row = matrix.AsSpan(o * inCh, inCh);
                for (int i = 0; i < inCh; i++) acc += row[i] * s[i];
                d[o] = acc;
            }
        }
    }

    public AudioFormat Format { get; }
    public long LengthFrames => _expected;
    public long PositionFrames => _produced;
    public bool CanSeek => false;
    public bool HasNativeIntegers => false;

    public int ReadInteger(Span<int> destination) => throw new NotSupportedException("A converted source delivers float samples only.");
    public void Seek(long frame) => throw new NotSupportedException("A converted source cannot seek.");

    public int Read(Span<float> destination)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        int want = destination.Length / _outCh;
        if (_expected >= 0) want = (int)Math.Min(want, _expected - _produced);
        int written = 0;
        while (written < want)
        {
            if (_pendCount == 0 && !Refill()) break;
            int n = Math.Min(want - written, _pendCount);
            var from = _pend.AsSpan(_pendStart * _outCh, n * _outCh);
            var to = destination.Slice(written * _outCh, n * _outCh);
            if (_gain == 1f) from.CopyTo(to);
            else for (int i = 0; i < to.Length; i++) to[i] = from[i] * _gain;
            _pendStart += n;
            _pendCount -= n;
            written += n;
        }
        _produced += written;
        return written;
    }

    // Fills the pending buffer with the next converted block. False at the end.
    private bool Refill()
    {
        _pendStart = 0;
        while (true)
        {
            if (!_sourceDone)
            {
                int got = _source.Read(_in);
                if (got == 0) { _sourceDone = true; continue; }
                var mid = _mid.AsSpan(0, got * _midCh);
                if (_preMatrix != null) Apply(_preMatrix, _inCh, _midCh, _in.AsSpan(0, got * _inCh), mid, got);
                int outFrames = got;
                ReadOnlySpan<float> produced = mid;
                if (_resampler != null)
                {
                    // The output buffer is sized for a whole block, so one call consumes all of it.
                    _resampler.Process(mid, _res, out _, out outFrames);
                    produced = _res.AsSpan(0, outFrames * _midCh);
                }
                if (outFrames == 0) continue;
                Deliver(produced, outFrames);
                return true;
            }
            if (_resampler == null || _flushed) return false;
            // The buffer holds a whole block's output, far more than the filter's tail, so one call drains it.
            _flushed = true;
            int tail = _resampler.Flush(_res);
            if (tail == 0) return false;
            Deliver(_res.AsSpan(0, tail * _midCh), tail);
            return true;
        }
    }

    private void Deliver(ReadOnlySpan<float> frames, int count)
    {
        if (_postMatrix != null) Apply(_postMatrix, _midCh, _outCh, frames, _pend, count);
        else frames.CopyTo(_pend);
        _pendCount = count;
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        _source.Dispose();
    }
}
