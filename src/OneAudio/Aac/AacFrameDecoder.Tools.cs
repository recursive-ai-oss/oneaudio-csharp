namespace OneAudio.Aac;

internal sealed partial class AacFrameDecoder
{
    // 14496-3 4.6.8.1 mid/side: L = M + S, R = M - S, in the bands flagged by ms_used.
    private void ApplyMidSide(AacChannelState l, AacChannelState r)
    {
        var offsets = l.IsShort ? _shortBands : _longBands;
        int window = 0;
        for (int g = 0; g < l.NumGroups; g++)
        {
            int groupWindows = l.IsShort ? l.GroupLength[g] : 1;
            for (int w = 0; w < groupWindows; w++)
            {
                int baseIndex = l.IsShort ? (window + w) * ShortSize : 0;
                for (int b = 0; b < l.MaxSfb; b++)
                {
                    if (_msUsed[g * 16 + b] == 0) continue;
                    for (int k = offsets[b]; k < offsets[b + 1]; k++)
                    {
                        float m = l.Spec[baseIndex + k], s = r.Spec[baseIndex + k];
                        l.Spec[baseIndex + k] = m + s;
                        r.Spec[baseIndex + k] = m - s;
                    }
                }
            }
            window += groupWindows;
        }
    }

    // 14496-3 4.6.8.2 intensity stereo: the right channel is the left scaled by 0.5^(position/4); codebook 14 is in phase,
    // 15 out of phase, and a set ms_used flag inverts that.
    private void ApplyIntensity(AacChannelState l, AacChannelState r, bool common)
    {
        var offsets = l.IsShort ? _shortBands : _longBands;
        int window = 0;
        for (int g = 0; g < l.NumGroups; g++)
        {
            int groupWindows = l.IsShort ? l.GroupLength[g] : 1;
            for (int b = 0; b < l.MaxSfb; b++)
            {
                int i = g * 16 + b;
                int cb = r.Codebook[i];
                if (cb != IntensityBook && cb != IntensityBook2) continue;
                float scale = (float)Math.Pow(0.5, 0.25 * r.Scale[i]);
                bool flip = common && _msUsed[i] != 0;
                bool outOfPhase = cb == IntensityBook2 ? !flip : flip;
                if (outOfPhase) scale = -scale;
                for (int w = 0; w < groupWindows; w++)
                {
                    int baseIndex = l.IsShort ? (window + w) * ShortSize : 0;
                    for (int k = offsets[b]; k < offsets[b + 1]; k++)
                        r.Spec[baseIndex + k] = l.Spec[baseIndex + k] * scale;
                }
            }
            window += groupWindows;
        }
    }

    // 14496-3 4.6.13 perceptual noise substitution: bands coded with codebook 13 are filled with noise whose total energy is
    // 2^(energy / 2) (full scale 32768). The noise is this decoder's own LCG, reseeded per frame, so a seek reproduces it.
    private void ApplyPns(AacChannelState c, bool[]? correlated, int channelIndex)
    {
        var offsets = c.IsShort ? _shortBands : _longBands;
        int window = 0;
        for (int g = 0; g < c.NumGroups; g++)
        {
            int groupWindows = c.IsShort ? c.GroupLength[g] : 1;
            for (int w = 0; w < groupWindows; w++)
            {
                int baseIndex = c.IsShort ? (window + w) * ShortSize : 0;
                for (int b = 0; b < c.MaxSfb; b++)
                {
                    int i = g * 16 + b;
                    if (c.Codebook[i] != NoiseBook) continue;
                    float level = PowQuarter(c.Scale[i] - 60);
                    int start = baseIndex + offsets[b], end = baseIndex + offsets[b + 1];
                    // A band flagged as correlated gives both channels the same noise vector: the right channel restarts the
                    // generator from the state the left channel used for this band and window.
                    bool shared = correlated != null && correlated[i];
                    uint state = shared && channelIndex == 1 ? _noiseSeed[i * 8 + w] : _noiseState;
                    if (shared && channelIndex == 0) _noiseSeed[i * 8 + w] = state;
                    double energy = 0;
                    for (int k = start; k < end; k++)
                    {
                        state = state * 1664525u + 1013904223u;
                        float v = (int)state;
                        c.Spec[k] = v;
                        energy += (double)v * v;
                    }
                    if (!(shared && channelIndex == 1)) _noiseState = state;
                    if (energy > 0)
                    {
                        float scale = level / (float)Math.Sqrt(energy);
                        for (int k = start; k < end; k++) c.Spec[k] *= scale;
                    }
                }
            }
            window += groupWindows;
        }
    }

    // 14496-3 4.6.9 temporal noise shaping: an all-pole filter along frequency over the bands the filter covers.
    private void ApplyTns(AacChannelState c)
    {
        if (!c.TnsPresent) return;
        bool isShort = c.IsShort;
        var offsets = isShort ? _shortBands : _longBands;
        int maxBands = isShort ? 14 : _tnsMaxLong;
        Span<float> lpc = stackalloc float[33];
        Span<float> tmp = stackalloc float[33];
        Span<float> state = stackalloc float[32];
        for (int w = 0; w < c.NumWindows; w++)
        {
            for (int f = 0; f < c.TnsFilterCount[w]; f++)
            {
                int slot = w * 3 + f;
                int order = c.TnsOrder[slot];
                if (order == 0) continue;
                int startBand = Math.Min(Math.Min(c.TnsStart[slot], maxBands), c.MaxSfb);
                int stopBand = Math.Min(Math.Min(c.TnsStop[slot], maxBands), c.MaxSfb);
                int start = offsets[startBand], stop = offsets[stopBand];
                int size = stop - start;
                if (size <= 0) continue;
                // Reflection coefficients to direct-form predictor coefficients (step-up recursion).
                lpc.Clear();
                lpc[0] = 1f;
                for (int m = 1; m <= order; m++)
                {
                    float k = c.TnsCoef[slot * 32 + m - 1];
                    tmp.Clear();
                    for (int i = 1; i < m; i++) tmp[i] = lpc[i] + k * lpc[m - i];
                    tmp[m] = k;
                    for (int i = 1; i <= m; i++) lpc[i] = tmp[i];
                }
                var spec = c.Spec.AsSpan(w * (isShort ? ShortSize : 0) + start, size);
                state.Clear();
                bool backwards = c.TnsDirection[slot] < 0;
                for (int n = 0; n < size; n++)
                {
                    int idx = backwards ? size - 1 - n : n;
                    float acc = spec[idx];
                    for (int j = 0; j < order; j++) acc -= lpc[j + 1] * state[j];
                    for (int j = order - 1; j > 0; j--) state[j] = state[j - 1];
                    state[0] = acc;
                    spec[idx] = acc;
                }
            }
        }
    }
}
