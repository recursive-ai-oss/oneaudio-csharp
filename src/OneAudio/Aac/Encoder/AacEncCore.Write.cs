namespace OneAudio.Aac;

internal sealed partial class AacEncCore
{
    // raw_data_block elements of 14496-3 4.4.2 (without the terminating ID_END).
    private void WriteBlock<T>(ref T w) where T : struct, IAacBitSink
    {
        if (_ch == 1)
        {
            w.Put(0, 3); // ID_SCE
            w.Put(0, 4); // element_instance_tag
            WriteChannel(ref w, _c[0], ownIcsInfo: true);
            return;
        }
        w.Put(1, 3); // ID_CPE
        w.Put(0, 4);
        w.Put(1, 1); // common_window: both channels share the window sequence, grouping and max_sfb
        WriteIcsInfo(ref w);
        if (!_anyMs) w.Put(0, 2);
        else if (_allMs && AllBandsCodedMs()) w.Put(2, 2);
        else
        {
            w.Put(1, 2);
            for (int g = 0; g < _numGroups; g++)
                for (int b = 0; b < _maxSfb; b++) w.Put(_ms[g * _active + b] ? 1u : 0u, 1);
        }
        WriteChannel(ref w, _c[0], ownIcsInfo: false);
        WriteChannel(ref w, _c[1], ownIcsInfo: false);
    }

    private bool AllBandsCodedMs()
    {
        for (int g = 0; g < _numGroups; g++)
            for (int b = 0; b < _maxSfb; b++)
                if (!_ms[g * _active + b]) return false;
        return true;
    }

    private void WriteIcsInfo<T>(ref T w) where T : struct, IAacBitSink
    {
        w.Put(0, 1); // ics_reserved_bit
        w.Put((uint)_seq, 2);
        w.Put(1, 1); // window_shape: Kaiser-Bessel derived
        if (_isShort)
        {
            w.Put((uint)_maxSfb, 4);
            // scale_factor_grouping: bit i set when window i + 1 continues the group of window i.
            uint bits = 0;
            int win = 0;
            for (int g = 0; g < _numGroups; g++)
                for (int k = 0; k < _groupLen[g]; k++, win++)
                    if (win < 7) bits |= (k < _groupLen[g] - 1 ? 1u : 0u) << (6 - win);
            w.Put(bits, 7);
        }
        else
        {
            w.Put((uint)_maxSfb, 6);
            w.Put(0, 1); // predictor_data_present
        }
    }

    private void WriteChannel<T>(ref T w, AacEncChannel c, bool ownIcsInfo) where T : struct, IAacBitSink
    {
        // global_gain: the first coded scalefactor, so the first delta is zero.
        int gain = 100;
        bool found = false;
        for (int g = 0; g < _numGroups && !found; g++)
            for (int b = 0; b < _maxSfb && !found; b++)
            {
                int e = g * _active + b;
                if (c.Cb[e] != 0 && c.Cb[e] != 13) { gain = c.Sf[e]; found = true; }
            }
        c.Gain = gain;
        w.Put((uint)gain, 8);
        if (ownIcsInfo) WriteIcsInfo(ref w); // a single channel element carries its own ics_info (14496-3 4.4.2.1)

        // section_data
        int lenBits = _isShort ? 3 : 5, esc = (1 << lenBits) - 1;
        for (int g = 0; g < _numGroups; g++)
        {
            int b = 0;
            while (b < _maxSfb)
            {
                int cb = c.Cb[g * _active + b], end = b + 1;
                while (end < _maxSfb && c.Cb[g * _active + end] == cb) end++;
                w.Put((uint)cb, 4);
                int len = end - b;
                while (len >= esc) { w.Put((uint)esc, lenBits); len -= esc; }
                w.Put((uint)len, lenBits);
                b = end;
            }
        }

        // scale_factor_data: Huffman-coded differences along the coded bands
        var sfLen = AacEncTables.Len[12];
        var sfCode = AacEncTables.Code[12];
        int prev = gain, noisePrev = 0;
        bool noiseStarted = false;
        for (int g = 0; g < _numGroups; g++)
            for (int b = 0; b < _maxSfb; b++)
            {
                int e = g * _active + b;
                if (c.Cb[e] == 0) continue;
                if (c.Cb[e] == 13)
                {
                    // 14496-3 4.6.13.2: the first noise energy is 9 bits relative to global_gain - 90, later ones are deltas of their own chain.
                    if (!noiseStarted)
                    {
                        noisePrev = Math.Clamp(c.NoiseNrg[e], gain - 90 - 256, gain - 90 + 255);
                        w.Put((uint)(noisePrev - (gain - 90) + 256), 9);
                        noiseStarted = true;
                    }
                    else
                    {
                        int nd = Math.Clamp(c.NoiseNrg[e] - noisePrev, -60, 60);
                        noisePrev += nd;
                        w.Put(sfCode[nd + 60], sfLen[nd + 60]);
                    }
                    continue;
                }
                int d = c.Sf[e] - prev + 60;
                w.Put(sfCode[d], sfLen[d]);
                prev = c.Sf[e];
            }

        w.Put(0, 1); // pulse_data_present
        w.Put(0, 1); // tns_data_present
        w.Put(0, 1); // gain_control_data_present

        // spectral_data
        for (int g = 0; g < _numGroups; g++)
            for (int b = 0; b < _maxSfb; b++)
            {
                int e = g * _active + b;
                if (c.Cb[e] != 0 && c.Cb[e] != 13) PutBand(ref w, c.Q.AsSpan(c.Start[e], c.Len[e]), c.Cb[e]);
            }
    }

    private static void PutBand<T>(ref T w, ReadOnlySpan<int> q, int book) where T : struct, IAacBitSink
    {
        var len = AacEncTables.Len[book];
        var code = AacEncTables.Code[book];
        if (book <= 4)
        {
            for (int i = 0; i < q.Length; i += 4)
            {
                int sym;
                if (book <= 2) sym = (q[i] + 1) | (q[i + 1] + 1) << 2 | (q[i + 2] + 1) << 4 | (q[i + 3] + 1) << 6;
                else sym = Math.Abs(q[i]) | Math.Abs(q[i + 1]) << 2 | Math.Abs(q[i + 2]) << 4 | Math.Abs(q[i + 3]) << 6;
                w.Put(code[sym], len[sym]);
                if (book > 2)
                    for (int k = 0; k < 4; k++)
                        if (q[i + k] != 0) w.Put(q[i + k] < 0 ? 1u : 0u, 1);
            }
            return;
        }
        if (book <= 6)
        {
            for (int i = 0; i < q.Length; i += 2)
            {
                int sym = (q[i] + 4) | (q[i + 1] + 4) << 4;
                w.Put(code[sym], len[sym]);
            }
            return;
        }
        int cap = book == 11 ? 16 : int.MaxValue, fb = book == 11 ? 5 : 4;
        for (int i = 0; i < q.Length; i += 2)
        {
            int a0 = Math.Abs(q[i]), a1 = Math.Abs(q[i + 1]);
            int sym = Math.Min(a0, cap) | Math.Min(a1, cap) << fb;
            w.Put(code[sym], len[sym]);
            if (a0 != 0) w.Put(q[i] < 0 ? 1u : 0u, 1);
            if (a1 != 0) w.Put(q[i + 1] < 0 ? 1u : 0u, 1);
            if (book == 11)
            {
                if (a0 >= 16) PutEscape(ref w, a0);
                if (a1 >= 16) PutEscape(ref w, a1);
            }
        }
    }

    // 14496-3 4.6.3: N - 4 ones and a zero select the width N, then the N low bits of the value (whose top bit is implied).
    private static void PutEscape<T>(ref T w, int a) where T : struct, IAacBitSink
    {
        int n = 31 - System.Numerics.BitOperations.LeadingZeroCount((uint)a);
        for (int k = 4; k < n; k++) w.Put(1, 1);
        w.Put(0, 1);
        w.Put((uint)(a - (1 << n)), n);
    }

    /// <summary>Writes padding as fill elements (4.4.2.7 extension_payload, EXT_FILL_DATA) until the block would end at <paramref name="targetBytes"/>.</summary>
    private static void PutFill(ref AacBitWriter w, int targetBytes)
    {
        while (true)
        {
            int have = (int)((w.Bits + 3 + 7) / 8);
            int deficit = targetBytes - have;
            if (deficit <= 0) break;
            // A fill element costs 7 bits plus its payload (15 plus when the count escapes), i.e. about payload + 1 bytes.
            int payload = Math.Min(deficit - 1, 269);
            if (payload < 0) payload = 0;
            w.Put(6, 3); // ID_FIL
            if (payload < 15) w.Put((uint)payload, 4);
            else
            {
                w.Put(15, 4);
                w.Put((uint)(payload - 14), 8);
            }
            for (int i = 0; i < payload; i++)
                w.Put(i == 0 ? 0x10u : 0xA5u, 8); // EXT_FILL_DATA and a zero nibble, then the 0xA5 fill byte
        }
    }
}
