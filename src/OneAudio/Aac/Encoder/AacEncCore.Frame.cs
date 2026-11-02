namespace OneAudio.Aac;

internal sealed partial class AacEncCore
{
    private readonly byte[] _out = new byte[6144 * 2 / 8 + 64];

    /// <summary>The finished block (no ADTS header) of the last <see cref="EncodeBlock"/> call.</summary>
    public ReadOnlySpan<byte> Output(int length) => _out.AsSpan(0, length);

    /// <summary>Moves the three-hop history on by one hop; call after <see cref="EncodeBlock"/>.</summary>
    public void Shift()
    {
        for (int ch = 0; ch < _ch; ch++)
        {
            var h = _hist[ch];
            Array.Copy(h, M, h, 0, 2 * M);
        }
    }

    /// <summary>
    /// Encodes the block whose window covers hops 0 and 1 of the history (hop 2 is the lookahead for window switching).
    /// </summary>
    /// <returns>The block's length in bytes, in <see cref="Output"/>.</returns>
    public int EncodeBlock()
    {
        _seq = ChooseSequence();
        Transform(_seq);
        Analyze(_seq);
        double target = TargetBits();
        float r = SelectShift(target, out bool avoid);
        Trial(r, avoid); // leaves the chosen quantization in the channel states

        var w = new AacBitWriter(_out);
        WriteBlock(ref w);
        int minBytes = (int)((w.Bits + 3 + 7) / 8);
        int bytes = minBytes;
        if (!_vbr)
        {
            double after = _reservoir + _avgBits - minBytes * 8.0;
            if (after > _reservoirMax)
            {
                int stuff = (int)((after - _reservoirMax) / 8);
                bytes = Math.Min(minBytes + stuff, _out.Length - 8);
                if (bytes > minBytes) PutFill(ref w, bytes);
            }
        }
        w.Put(7, 3); // ID_END
        w.AlignZero();
        int length = w.ByteLength;
        if (length > _out.Length - 8) throw new InvalidOperationException("The AAC encoder produced an oversize block.");

        _reservoir += _avgBits - length * 8.0;
        if (!_vbr) _reservoir = Math.Min(_reservoir, _reservoirMax);
        return length;
    }

    // Bit budget of this block. CBR/ABR: the nominal rate plus or minus what the block's perceptual entropy asks for, limited by the
    // reservoir. VBR: a fixed fraction of the perceptual entropy.
    private double TargetBits()
    {
        double pe = _pe;
        double target;
        if (_peMean < 0) _peMean = Math.Max(pe, 1);
        double ratio = pe / _peMean;
        if (_vbr)
        {
            // Bits follow the block's perceptual entropy relative to the running mean; quality scales the whole curve from half
            // (0) through the nominal rate (0.5) to double (1).
            double scale = Math.Pow(2, 2 * (_s.VbrQuality - 0.5));
            target = _avgBits * scale * Math.Pow(Math.Clamp(ratio, 0.2, 5.0), 0.8);
            target = Math.Clamp(target, 0.15 * _avgBits, Math.Min(_maxFrameBits, 4.0 * _avgBits));
        }
        else
        {
            double frac = _reservoirMax > 0 ? Math.Clamp(_reservoir / _reservoirMax, 0, 1) : 1;
            if (ratio > 1)
            {
                double spend = Math.Min((ratio - 1) * 0.8 * _avgBits, 0.5 * Math.Max(_reservoir, 0));
                target = _avgBits + spend;
                if (frac > 0.8) target += 0.25 * (_reservoir - 0.8 * _reservoirMax);
            }
            else
            {
                double save = (1 - ratio) * 0.5 * _avgBits * (1 - frac);
                target = _avgBits - save;
            }
            target = Math.Min(target, _avgBits + Math.Max(_reservoir, 0));
            target = Math.Clamp(target, 0.3 * _avgBits, _maxFrameBits);
        }
        _peMean = pe > 0 ? 0.97 * _peMean + 0.03 * pe : _peMean;
        return target;
    }

    // Finds the smallest threshold shift whose block fits the budget; hole avoidance is dropped only when nothing else fits.
    private float SelectShift(double target, out bool avoid)
    {
        avoid = true;
        int total = _numGroups * _active;
        float rHi = 0;
        for (int ch = 0; ch < _ch; ch++)
            for (int e = 0; e < total; e++)
                rHi = Math.Max(rHi, MathF.Sqrt(MathF.Sqrt(_c[ch].EntE[e])));
        if (rHi <= 0) return 0;
        if (Trial(0, true) + 3 <= target) return 0;
        if (Trial(rHi, true) + 3 > target) avoid = false;
        float lo = rHi * 1e-3f, hi = rHi;
        for (int i = 0; i < 12; i++)
        {
            float mid = MathF.Sqrt(lo * hi);
            if (Trial(mid, avoid) + 3 <= target) hi = mid; else lo = mid;
        }
        return hi;
    }
}
