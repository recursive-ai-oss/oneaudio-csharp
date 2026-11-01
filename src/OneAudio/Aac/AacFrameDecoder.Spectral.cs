namespace OneAudio.Aac;

internal sealed partial class AacFrameDecoder
{
    // 14496-3 4.4.2.7 spectral_data: Huffman-coded quantized lines, section by section. Within a window group the spectrum
    // is sent band by band, and for each band once per window of the group.
    private bool ReadSpectralData(ref AacBitReader br, AacChannelState c)
    {
        Array.Clear(c.Quant);
        bool isShort = c.IsShort;
        var offsets = isShort ? _shortBands : _longBands;
        int window = 0;
        for (int g = 0; g < c.NumGroups; g++)
        {
            int groupWindows = isShort ? c.GroupLength[g] : 1;
            for (int b = 0; b < c.MaxSfb; b++)
            {
                int cb = c.Codebook[g * 16 + b];
                if (cb == ZeroBook || cb >= NoiseBook) continue;
                int start = offsets[b], width = offsets[b + 1] - start;
                for (int w = 0; w < groupWindows; w++)
                {
                    int baseIndex = (window + w) * ShortSize * (isShort ? 1 : 0) + start;
                    if (!ReadBand(ref br, cb, c.Quant.AsSpan(baseIndex, width))) return false;
                    if (Truncated(in br)) return false;
                }
            }
            window += groupWindows;
        }
        return !c.PulsePresent || ApplyPulses(c);
    }

    private bool ReadBand(ref AacBitReader br, int cb, Span<int> q)
    {
        var huff = AacHuffman.Books[cb]!;
        int dim = AacHuffman.Dimension[cb];
        int bits = AacHuffman.FieldBits[cb];
        int offset = AacHuffman.Offset[cb];
        int mask = (1 << bits) - 1;
        bool signs = offset == 0; // the unsigned books send an explicit sign bit per nonzero line
        for (int i = 0; i < q.Length; i += dim)
        {
            if (i + dim > q.Length) return Fail("An AAC band width is not a multiple of the codebook dimension.");
            int v = huff.Decode(ref br);
            for (int k = 0; k < dim; k++)
            {
                int x = ((v >> (bits * k)) & mask) - offset;
                if (signs && x != 0 && br.ReadBit()) x = -x;
                q[i + k] = x;
            }
            if (cb == EscBook)
            {
                for (int k = 0; k < 2; k++)
                {
                    int x = q[i + k];
                    if (x is 16 or -16)
                    {
                        int e = ReadEscape(ref br, x < 0);
                        if (e == int.MinValue) return false;
                        q[i + k] = e;
                    }
                }
            }
        }
        return true;
    }

    // 14496-3 4.6.3 escape sequence: N-4 ones and a zero select N, followed by N bits; the value is 2^N plus those bits.
    // Returns int.MinValue (after recording the reason) for an invalid sequence.
    private int ReadEscape(ref AacBitReader br, bool negative)
    {
        int n = 4;
        while (br.ReadBit())
        {
            n++;
            if (n > 12) { Fail("An AAC escape sequence is too long."); return int.MinValue; }
        }
        int value = (1 << n) + (int)br.Read(n);
        if (value > 8191) { Fail("An AAC quantized value is out of range."); return int.MinValue; }
        return negative ? -value : value;
    }

    // 14496-3 4.6.3.3 pulse data: adds amplitude to lines of a long block, away from zero.
    private bool ApplyPulses(AacChannelState c)
    {
        if (c.PulseStartBand >= _longBandCount) return Fail("An AAC pulse starts beyond the last scalefactor band.");
        int k = _longBands[c.PulseStartBand];
        for (int i = 0; i < c.PulseCount; i++)
        {
            k += c.PulseOffset[i];
            if (k >= FrameSize) return Fail("An AAC pulse lies beyond the spectrum.");
            if (c.Quant[k] > 0) c.Quant[k] += c.PulseAmp[i];
            else c.Quant[k] -= c.PulseAmp[i];
        }
        return true;
    }

    // 14496-3 4.6.2: x = sign(q) * |q|^(4/3) * 2^((sf - 100) / 4). The result is divided by 32768 so full scale is 1.0.
    private void Dequantize(AacChannelState c)
    {
        Array.Clear(c.Spec);
        bool isShort = c.IsShort;
        var offsets = isShort ? _shortBands : _longBands;
        int window = 0;
        for (int g = 0; g < c.NumGroups; g++)
        {
            int groupWindows = isShort ? c.GroupLength[g] : 1;
            for (int b = 0; b < c.MaxSfb; b++)
            {
                int i = g * 16 + b;
                int cb = c.Codebook[i];
                if (cb == ZeroBook || cb >= NoiseBook) continue;
                float gain = PowQuarter(c.Scale[i] - 100 - 60);
                int start = offsets[b], end = offsets[b + 1];
                for (int w = 0; w < groupWindows; w++)
                {
                    int baseIndex = isShort ? (window + w) * ShortSize : 0;
                    for (int k = start; k < end; k++)
                    {
                        int q = c.Quant[baseIndex + k];
                        if (q == 0) continue;
                        c.Spec[baseIndex + k] = q > 0 ? Pow43[q] * gain : -Pow43[-q] * gain;
                    }
                }
            }
            window += groupWindows;
        }
    }
}
