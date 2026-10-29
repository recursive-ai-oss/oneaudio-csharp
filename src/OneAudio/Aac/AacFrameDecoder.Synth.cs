namespace OneAudio.Aac;

internal sealed partial class AacFrameDecoder
{
    // 14496-3 4.6.11: inverse MDCT, windowing by window sequence, and overlap-add with the previous block's second half.
    // The left half of a window uses the shape of the previous block, the right half the shape of this block.
    private void Synthesize(AacChannelState c, bool remember = true)
    {
        var z = _windowed;
        int seq = c.WindowSequence;
        var leftLong = AacWindows.Long[c.PrevShape];
        var rightLong = AacWindows.Long[c.WindowShape];
        var leftShort = AacWindows.Short[c.PrevShape];
        var rightShort = AacWindows.Short[c.WindowShape];

        if (seq != EightShort)
        {
            _imdctLong.Inverse(c.Spec, _imdctOut);
            var y = _imdctOut;
            // first half: rising slope from the previous shape (long, or the short slope inside a stop window)
            switch (seq)
            {
                case OnlyLong:
                case LongStart:
                    for (int n = 0; n < 1024; n++) z[n] = y[n] * leftLong[n];
                    break;
                default: // LongStop
                    for (int n = 0; n < 448; n++) z[n] = 0;
                    for (int n = 448; n < 576; n++) z[n] = y[n] * leftShort[n - 448];
                    for (int n = 576; n < 1024; n++) z[n] = y[n];
                    break;
            }
            // second half: falling slope from this block's shape
            switch (seq)
            {
                case OnlyLong:
                case LongStop:
                    for (int n = 1024; n < 2048; n++) z[n] = y[n] * rightLong[2047 - n];
                    break;
                default: // LongStart
                    for (int n = 1024; n < 1472; n++) z[n] = y[n];
                    for (int n = 1472; n < 1600; n++) z[n] = y[n] * rightShort[1599 - n];
                    for (int n = 1600; n < 2048; n++) z[n] = 0;
                    break;
            }
        }
        else
        {
            Array.Clear(z);
            for (int w = 0; w < 8; w++)
            {
                _imdctShort.Inverse(c.Spec.AsSpan(w * ShortSize, ShortSize), _imdctOut);
                var y = _imdctOut;
                int at = 448 + 128 * w;
                var rise = w == 0 ? leftShort : rightShort;
                for (int n = 0; n < 128; n++) z[at + n] += y[n] * rise[n];
                for (int n = 128; n < 256; n++) z[at + n] += y[n] * rightShort[255 - n];
            }
        }

        var ov = c.Overlap;
        var t = c.Time;
        for (int n = 0; n < FrameSize; n++)
        {
            t[n] = ov[n] + z[n];
            ov[n] = z[FrameSize + n];
        }

        c.PrevShape = c.WindowShape;
        c.LastSequence = seq;
        // Keep a long-block spectrum to repeat if the next frame is lost.
        if (!remember)
        {
            c.ConcealRun++;
        }
        else if (seq == OnlyLong || seq == LongStop)
        {
            Array.Copy(c.Spec, c.LastSpec, FrameSize);
            c.LastSpecUsable = true;
        }
        else
        {
            c.LastSpecUsable = false;
        }
        if (remember) c.ConcealRun = 0;
    }

    /// <summary>
    /// Produces a frame for an access unit that is missing or unreadable (see <see cref="AacDecoder.ConcealedFrames"/>).
    /// If the previous frame was a long block the output repeats its spectrum, halved for every consecutive lost frame
    /// and silent from the fifth; after a short-block frame the spectrum is zero, so the overlap of the previous
    /// frame fades out and the window sequence continues legally (start, then eight short, then stop).
    /// </summary>
    internal void Conceal(Span<float> pcm)
    {
        for (int ch = 0; ch < _channels; ch++)
        {
            var c = _ch[ch];
            int prev = c.LastSequence;
            int seq = prev switch { LongStart => EightShort, EightShort => LongStop, _ => OnlyLong };
            c.WindowSequence = seq;
            c.WindowShape = c.PrevShape;
            Array.Clear(c.Spec);
            int run = c.ConcealRun + 1;
            if (seq == OnlyLong && c.LastSpecUsable && run <= 4)
            {
                float g = MathF.ScaleB(1f, -run);
                for (int i = 0; i < FrameSize; i++) c.Spec[i] = c.LastSpec[i] * g;
            }
            Synthesize(c, remember: false);
            var t = c.Time;
            for (int i = 0; i < FrameSize; i++) pcm[i * _channels + ch] = t[i];
        }
    }
}
