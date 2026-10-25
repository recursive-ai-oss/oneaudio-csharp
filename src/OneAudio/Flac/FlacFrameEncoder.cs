using System.Numerics;
using OneAudio.Kernels;

namespace OneAudio.Flac;

/// <summary>What the frame encoder needs, resolved from <see cref="FlacEncoderSettings"/> and the compression level.</summary>
internal sealed class FlacEncoderPlan
{
    public int SampleRate, Channels, BitsPerSample, BlockSize;
    public int MaxLpcOrder, MaxPartitionOrder;
    /// <summary>0 independent channels only, 1 pick the stereo mode from a cheap estimate, 2 encode every mode and keep the smallest.</summary>
    public int StereoMode;
    /// <summary>Run the full partition search on every fixed order instead of only the one with the smallest residual.</summary>
    public bool ExhaustiveOrders;
    public int QlpPrecision; // 0 = by block size
    public WindowSpec[] Windows = [];
}

/// <summary>An apodization window family used before the autocorrelation.</summary>
internal readonly record struct WindowSpec(int Kind, double Param, int Parts, double Overlap)
{
    public const int Tukey = 0, PartialTukey = 1, PunchoutTukey = 2;
    public int Count => Kind == Tukey ? 1 : Parts;
}

/// <summary>One encoded subframe candidate: its type, parameters and residual, ready to be written.</summary>
internal sealed class FlacSubframe
{
    public const int Constant = 0, Verbatim = 1, Fixed = 2, Lpc = 3;
    public int Kind, Order, Wasted, Bps, Count, Precision, Shift, PartOrder, Method;
    public long Bits;
    public int[] Data = [];
    public int[] Residual;
    public readonly int[] Coefs = new int[32];
    public int[] Params, Esc;

    public FlacSubframe(int maxBlock, int maxPartOrder)
    {
        Residual = new int[maxBlock];
        Params = new int[1 << maxPartOrder];
        Esc = new int[1 << maxPartOrder];
    }
}

/// <summary>
/// Turns one block of per-channel samples into a FLAC frame (RFC 9639 section 9): chooses the channel assignment, the
/// best subframe per channel (constant, verbatim, fixed or LPC predictor, with wasted bits removed), the Rice
/// partitioning of each residual, and writes header, subframes and checksums. Every choice is deterministic.
/// All buffers are allocated once for the plan's block size.
/// </summary>
internal sealed class FlacFrameEncoder
{
    private static readonly int[][] FixedCoefs = [[], [1], [2, -1], [3, -3, 1], [4, -6, 4, -1]];

    private readonly FlacEncoderPlan _plan;
    private readonly int _bps;
    private readonly FlacSubframe[] _slots;
    private readonly int[][] _shiftBufs;
    private readonly int[] _mid, _side, _tres, _qc;
    private readonly byte[] _frame;
    private FlacBitWriter _w;

    // Rice search scratch
    private readonly ulong[] _sums;
    private readonly uint[] _ors;
    private readonly int[] _pParams, _pEsc;
    private int _pOrder, _pMethod;

    // LPC scratch
    private readonly double[] _xw = [], _autoc = new double[34], _lpcTab = new double[33 * 32], _errTab = new double[33];
    private readonly float[][] _windows;
    private readonly LpcCandidate[] _cands = [new(), new()];
    private int _candCount;
    private readonly double[] _levTmp = new double[32];
    private int _windowsFor = -1;

    public FlacFrameEncoder(FlacEncoderPlan plan)
    {
        _plan = plan;
        _bps = plan.BitsPerSample;
        int bs = plan.BlockSize;
        int slotCount = Math.Max(plan.Channels, 4);
        _slots = new FlacSubframe[slotCount];
        _shiftBufs = new int[slotCount][];
        for (int i = 0; i < slotCount; i++)
        {
            _slots[i] = new FlacSubframe(bs, plan.MaxPartitionOrder);
            _shiftBufs[i] = new int[bs];
        }
        _mid = new int[bs];
        _side = new int[bs];
        _tres = new int[bs];
        _qc = new int[32];
        _sums = new ulong[2 << plan.MaxPartitionOrder];
        _ors = new uint[2 << plan.MaxPartitionOrder];
        _pParams = new int[1 << plan.MaxPartitionOrder];
        _pEsc = new int[1 << plan.MaxPartitionOrder];
        if (plan.MaxLpcOrder > 0)
        {
            _xw = new double[bs + 36];
            int total = 0;
            foreach (var w in plan.Windows) total += w.Count;
            _windows = new float[total][];
            for (int i = 0; i < total; i++) _windows[i] = new float[bs];
        }
        else
        {
            _windows = [];
        }
        // Worst case: every subframe verbatim at full width, plus headers, plus the CRC-16.
        _frame = new byte[64 + plan.Channels * (4 * bs + 16)];
        _w = new FlacBitWriter(_frame);
    }

    /// <summary>The bytes of the last frame produced by <see cref="Encode"/>.</summary>
    public ReadOnlySpan<byte> Frame => _frame.AsSpan(0, _frameLength);
    private int _frameLength;

    /// <summary>Encodes <paramref name="n"/> samples of every channel as frame number <paramref name="frameNumber"/>.</summary>
    public void Encode(int[][] channels, int n, long frameNumber)
    {
        int ch = _plan.Channels;
        int assignment = ch - 1;
        int first = 0, second = 1;
        bool full = _plan.MaxLpcOrder > 0;

        if (ch == 2 && _plan.StereoMode != 0 && _bps <= 31)
        {
            var l = channels[0];
            var r = channels[1];
            for (int i = 0; i < n; i++)
            {
                _mid[i] = (l[i] + r[i]) >> 1;
                _side[i] = l[i] - r[i];
            }
            if (_plan.StereoMode == 2)
            {
                Analyze(0, l, n, _bps, true);
                Analyze(1, r, n, _bps, true);
                Analyze(2, _mid, n, _bps, true);
                Analyze(3, _side, n, _bps + 1, true);
            }
            else
            {
                Analyze(0, l, n, _bps, false);
                Analyze(1, r, n, _bps, false);
                Analyze(2, _mid, n, _bps, false);
                Analyze(3, _side, n, _bps + 1, false);
            }
            long bl = _slots[0].Bits, br = _slots[1].Bits, bm = _slots[2].Bits, bs = _slots[3].Bits;
            long best = bl + br;
            assignment = 1; first = 0; second = 1;
            if (bl + bs < best) { best = bl + bs; assignment = 8; first = 0; second = 3; }
            if (br + bs < best) { best = br + bs; assignment = 9; first = 3; second = 1; }
            if (bm + bs < best) { assignment = 10; first = 2; second = 3; }
            if (_plan.StereoMode == 1 && full)
            {
                // The estimate used fixed predictors only; now give the two winners the full search.
                ReAnalyze(first, first switch { 0 => channels[0], 1 => channels[1], 2 => _mid, _ => _side }, n, first == 3 ? _bps + 1 : _bps);
                ReAnalyze(second, second switch { 0 => channels[0], 1 => channels[1], 2 => _mid, _ => _side }, n, second == 3 ? _bps + 1 : _bps);
            }
        }
        else
        {
            for (int c = 0; c < ch; c++) Analyze(c, channels[c], n, _bps, true);
        }

        WriteHeader(n, frameNumber, assignment);
        if (ch == 2) { WriteSubframe(_slots[first], n); WriteSubframe(_slots[second], n); }
        else for (int c = 0; c < ch; c++) WriteSubframe(_slots[c], n);
        _w.AlignToByte();
        ushort crc = FlacCrc.Crc16(_w.WrittenBytes);
        _w.Write(crc, 16);
        _w.AlignToByte();
        _frameLength = _w.BytePosition;
    }

    private void ReAnalyze(int idx, int[] src, int n, int bps) => Analyze(idx, src, n, bps, true);

    // ---- frame header (RFC 9639 9.1) ----------------------------------------------------------------------

    private void WriteHeader(int n, long frameNumber, int assignment)
    {
        _w.Reset();
        _w.Write(0xFFF8, 16); // sync code, reserved bit 0, fixed-blocksize strategy 0

        int bsCode = n switch
        {
            192 => 1, 576 => 2, 1152 => 3, 2304 => 4, 4608 => 5, 256 => 8, 512 => 9, 1024 => 10,
            2048 => 11, 4096 => 12, 8192 => 13, 16384 => 14, 32768 => 15,
            _ => n <= 256 ? 6 : 7,
        };
        int rate = _plan.SampleRate;
        int rateCode = rate switch
        {
            88200 => 1, 176400 => 2, 192000 => 3, 8000 => 4, 16000 => 5, 22050 => 6, 24000 => 7, 32000 => 8,
            44100 => 9, 48000 => 10, 96000 => 11,
            _ when rate % 1000 == 0 && rate / 1000 <= 255 => 12,
            _ when rate <= 65535 => 13,
            _ when rate % 10 == 0 && rate / 10 <= 65535 => 14,
            _ => 0,
        };
        int bpsCode = _bps switch { 8 => 1, 12 => 2, 16 => 4, 20 => 5, 24 => 6, 32 => 7, _ => 0 };

        _w.Write((uint)bsCode, 4);
        _w.Write((uint)rateCode, 4);
        _w.Write((uint)assignment, 4);
        _w.Write((uint)bpsCode, 3);
        _w.Write(0, 1);
        WriteUtf8(frameNumber);
        if (bsCode == 6) _w.Write((uint)(n - 1), 8);
        else if (bsCode == 7) _w.Write((uint)(n - 1), 16);
        if (rateCode == 12) _w.Write((uint)(rate / 1000), 8);
        else if (rateCode == 13) _w.Write((uint)rate, 16);
        else if (rateCode == 14) _w.Write((uint)(rate / 10), 16);
        _w.AlignToByte();
        _w.Write(FlacCrc.Crc8(_w.WrittenBytes), 8);
    }

    // UTF-8 style variable-length number (RFC 9639 9.1.5), up to 36 bits.
    private void WriteUtf8(long v)
    {
        if (v < 0x80) { _w.Write((uint)v, 8); return; }
        int extra = v < 0x800 ? 1 : v < 0x10000 ? 2 : v < 0x200000 ? 3 : v < 0x4000000 ? 4 : v < 0x80000000L ? 5 : 6;
        uint lead = (uint)(0xFF00 >> (extra + 1)) & 0xFF;
        _w.Write(lead | (uint)(v >> (6 * extra)), 8);
        for (int i = extra - 1; i >= 0; i--) _w.Write(0x80u | (uint)((v >> (6 * i)) & 0x3F), 8);
    }

    // ---- subframe writing (RFC 9639 9.2) --------------------------------------------------------------------

    private void WriteSubframe(FlacSubframe sf, int n)
    {
        int type = sf.Kind switch
        {
            FlacSubframe.Constant => 0,
            FlacSubframe.Verbatim => 1,
            FlacSubframe.Fixed => 8 + sf.Order,
            _ => 32 + sf.Order - 1,
        };
        _w.Write(0, 1);
        _w.Write((uint)type, 6);
        if (sf.Wasted > 0)
        {
            _w.Write(1, 1);
            _w.WriteUnary((uint)sf.Wasted - 1);
        }
        else _w.Write(0, 1);

        var data = sf.Data;
        int bps = sf.Bps;
        switch (sf.Kind)
        {
            case FlacSubframe.Constant:
                _w.WriteSigned(data[0], bps);
                break;
            case FlacSubframe.Verbatim:
                for (int i = 0; i < n; i++) _w.WriteSigned(data[i], bps);
                break;
            default:
                for (int i = 0; i < sf.Order; i++) _w.WriteSigned(data[i], bps);
                if (sf.Kind == FlacSubframe.Lpc)
                {
                    _w.Write((uint)(sf.Precision - 1), 4);
                    _w.WriteSigned(sf.Shift, 5);
                    for (int i = 0; i < sf.Order; i++) _w.WriteSigned(sf.Coefs[i], sf.Precision);
                }
                WriteResidual(sf, n);
                break;
        }
    }

    private void WriteResidual(FlacSubframe sf, int n)
    {
        var w = _w; // a local copy keeps the accumulator in registers through the sample loop
        w.Write((uint)sf.Method, 2);
        w.Write((uint)sf.PartOrder, 4);
        int pbits = sf.Method == 0 ? 4 : 5;
        uint escCode = sf.Method == 0 ? 15u : 31u;
        int parts = 1 << sf.PartOrder;
        int psz = n >> sf.PartOrder;
        int off = 0;
        var res = sf.Residual;
        for (int j = 0; j < parts; j++)
        {
            int cnt = psz - (j == 0 ? sf.Order : 0);
            int p = sf.Params[j];
            if (p < 0)
            {
                int width = sf.Esc[j];
                w.Write(escCode, pbits);
                w.Write((uint)width, 5);
                if (width > 0) for (int i = 0; i < cnt; i++) w.WriteSigned(res[off + i], width);
            }
            else
            {
                w.Write((uint)p, pbits);
                uint mask = (1u << p) - 1;
                for (int i = 0; i < cnt; i++)
                {
                    int r = res[off + i];
                    uint u = (uint)((r << 1) ^ (r >> 31));
                    uint q = u >> p;
                    if (q < 32u - (uint)p) w.Write((1u << p) | (u & mask), (int)q + 1 + p);
                    else
                    {
                        w.WriteUnary(q);
                        w.Write(u & mask, p);
                    }
                }
            }
            off += cnt;
        }
        _w = w;
    }

    // ---- analysis ------------------------------------------------------------------------------------------

    /// <summary>
    /// Finds the cheapest subframe for <paramref name="src"/> and leaves it in slot <paramref name="idx"/>. With
    /// <paramref name="full"/> false only the fixed predictors are tried (a quick estimate for the stereo decision).
    /// </summary>
    private void Analyze(int idx, int[] src, int n, int bps, bool full)
    {
        var sf = _slots[idx];
        sf.Count = n;
        sf.Wasted = 0;
        sf.Bps = bps;
        sf.Data = src;
        sf.Order = 0;

        int orv = 0;
        int first = src[0];
        bool allEqual = true;
        for (int i = 0; i < n; i++)
        {
            int v = src[i];
            orv |= v;
            if (v != first) allEqual = false;
        }
        if (orv == 0 || allEqual)
        {
            sf.Kind = FlacSubframe.Constant;
            sf.Bits = 8 + bps;
            return;
        }

        int tz = BitOperations.TrailingZeroCount(orv); // RFC 9639 9.2.2: wasted bits shared by every sample
        int bpsE = bps;
        int[] data = src;
        if (tz > 0)
        {
            var sb = _shiftBufs[idx];
            for (int i = 0; i < n; i++) sb[i] = src[i] >> tz;
            data = sb;
            bpsE = bps - tz;
            sf.Data = sb;
            sf.Wasted = tz;
            sf.Bps = bpsE;
        }
        int headerBits = 8 + tz; // type byte plus the unary wasted-bits count (tz bits)
        sf.Kind = FlacSubframe.Verbatim;
        sf.Bits = headerBits + (long)n * bpsE;

        // Fixed predictors, orders 0 to 4 (RFC 9639 9.2.8).
        int maxFixed = Math.Min(4, n - 1);
        int bestO = -1;
        if (!_plan.ExhaustiveOrders && n >= 5 && bpsE <= 27)
        {
            // One fused pass ranks the five orders; only the winner gets a residual and a partition search.
            Span<ulong> sums = stackalloc ulong[5];
            FlacEncKernels.FixedOrderSums(data.AsSpan(0, n), sums);
            ulong bestSum = ulong.MaxValue;
            for (int o = 0; o <= maxFixed; o++)
                if (sums[o] < bestSum) { bestSum = sums[o]; bestO = o; }
            if (FixedResidual(data, n, bestO, bpsE)) OfferFixed(sf, data, n, bestO, bpsE, headerBits);
        }
        else
        {
            ulong bestSum = ulong.MaxValue;
            for (int o = 0; o <= maxFixed; o++)
            {
                if (!FixedResidual(data, n, o, bpsE)) continue;
                FlacEncKernels.FoldedStats(_tres.AsSpan(0, n - o), out ulong s, out _);
                if (_plan.ExhaustiveOrders)
                    OfferFixed(sf, data, n, o, bpsE, headerBits);
                else if (s < bestSum) { bestSum = s; bestO = o; }
            }
            if (!_plan.ExhaustiveOrders && bestO >= 0 && FixedResidual(data, n, bestO, bpsE)) OfferFixed(sf, data, n, bestO, bpsE, headerBits);
        }

        if (full && _plan.MaxLpcOrder > 0 && n > 2) AnalyzeLpc(sf, data, n, bpsE, headerBits);
    }

    private bool FixedResidual(int[] data, int n, int order, int bpsE)
    {
        var c = FixedCoefs[order];
        if (FlacEncKernels.Fits32(c, bpsE))
        {
            FlacEncKernels.LpcResidual32(data.AsSpan(0, n), c, 0, _tres);
            return true;
        }
        return FlacEncKernels.LpcResidual64(data.AsSpan(0, n), c, 0, _tres);
    }

    // Uses the residual already in _tres.
    private void OfferFixed(FlacSubframe sf, int[] data, int n, int order, int bpsE, int headerBits)
    {
        long bits = headerBits + (long)order * bpsE + SearchPartitions(_tres.AsSpan(0, n - order), n, order);
        if (bits < sf.Bits) Adopt(sf, FlacSubframe.Fixed, order, bits, n, 0, 0, default);
    }

    private void Adopt(FlacSubframe sf, int kind, int order, long bits, int n, int precision, int shift, ReadOnlySpan<int> coefs)
    {
        sf.Kind = kind;
        sf.Order = order;
        sf.Bits = bits;
        sf.Precision = precision;
        sf.Shift = shift;
        coefs.CopyTo(sf.Coefs);
        sf.PartOrder = _pOrder;
        sf.Method = _pMethod;
        Array.Copy(_tres, sf.Residual, n - order);
        Array.Copy(_pParams, sf.Params, 1 << _pOrder);
        Array.Copy(_pEsc, sf.Esc, 1 << _pOrder);
    }

    // ---- LPC -------------------------------------------------------------------------------------------------

    private sealed class LpcCandidate
    {
        public int Order, Precision, Shift;
        public long Estimate;
        public readonly int[] Coefs = new int[32];
    }

    private void AnalyzeLpc(FlacSubframe sf, int[] data, int n, int bpsE, int headerBits)
    {
        int maxOrd = Math.Min(_plan.MaxLpcOrder, n - 1);
        if (maxOrd < 1) return;
        int prec = _plan.QlpPrecision != 0 ? _plan.QlpPrecision : n <= 192 ? 7 : n <= 384 ? 8 : n <= 576 ? 9 : n <= 1152 ? 10 : n <= 2304 ? 11 : n <= 4608 ? 12 : 13;
        // Wide samples (high-resolution audio is often heavily oversampled) are very predictable and need exact coefficients.
        if (_plan.QlpPrecision == 0 && bpsE > 16) prec = 15;
        EnsureWindows(n);
        int lags = maxOrd + 1;
        int padded = n + ((lags + 3) & ~3);
        bool direct = _windows.Length == 1;
        _candCount = 0;
        for (int wi = 0; wi < _windows.Length; wi++)
        {
            var win = _windows[wi];
            FlacEncKernels.ApplyWindow(data.AsSpan(0, n), win, _xw);
            _xw.AsSpan(n, padded - n).Clear();
            FlacEncKernels.Autocorrelation(_xw.AsSpan(0, padded), n, _autoc.AsSpan(0, lags));
            if (!(_autoc[0] > 0)) continue;
            _autoc[0] *= 1 + 1e-12; // keeps the recursion stable on nearly pure tones
            int valid = Levinson(maxOrd);
            if (valid < 1) continue;

            // The order with the smallest estimated size: the predicted residual entropy from the Levinson error plus the cost of
            // the warm-up samples and coefficients.
            int best = 1;
            double bestEst = double.MaxValue;
            for (int o = 1; o <= valid; o++)
            {
                double bpr = Math.Max(0.0, 0.5 * Math.Log2(Math.Max(_errTab[o], 1e-9) / n) + 1.0);
                double est = bpr * (n - o) + o * (double)(bpsE + prec);
                if (est < bestEst) { bestEst = est; best = o; }
            }
            if (direct) TryLpc(sf, data, n, bpsE, best, prec, headerBits);
            else ConsiderLpc(data, n, bpsE, best, prec, headerBits);
        }

        // Second stage: only the two most promising candidates get the full partition search.
        for (int c = 0; c < _candCount; c++)
        {
            var cd = _cands[c];
            var coefs = cd.Coefs.AsSpan(0, cd.Order);
            if (!Residual(data, n, bpsE, coefs, cd.Shift)) continue;
            long bits = headerBits + (long)cd.Order * bpsE + 9 + (long)cd.Order * cd.Precision + SearchPartitions(_tres.AsSpan(0, n - cd.Order), n, cd.Order);
            if (bits < sf.Bits) Adopt(sf, FlacSubframe.Lpc, cd.Order, bits, n, cd.Precision, cd.Shift, coefs);
        }
    }

    private bool Residual(int[] data, int n, int bpsE, ReadOnlySpan<int> coefs, int shift)
    {
        if (FlacEncKernels.Fits32(coefs, bpsE))
        {
            FlacEncKernels.LpcResidual32(data.AsSpan(0, n), coefs, shift, _tres);
            return true;
        }
        return FlacEncKernels.LpcResidual64(data.AsSpan(0, n), coefs, shift, _tres);
    }

    // Cheap size estimate for one candidate, kept if it is among the two best so far.
    private void ConsiderLpc(int[] data, int n, int bpsE, int order, int precision, int headerBits)
    {
        if (!Quantize(_lpcTab.AsSpan(order * 32, order), precision, _qc, out int shift)) return;
        var c = _qc.AsSpan(0, order);
        if (!Residual(data, n, bpsE, c, shift)) return;
        long est = headerBits + (long)order * bpsE + 9 + (long)order * precision + QuickCost(_tres.AsSpan(0, n - order), n, order);
        int slot;
        if (_candCount < 2) slot = _candCount++;
        else if (est < _cands[1].Estimate) slot = 1;
        else return;
        var cd = _cands[slot];
        cd.Order = order; cd.Precision = precision; cd.Shift = shift; cd.Estimate = est;
        c.CopyTo(cd.Coefs);
        if (_candCount == 2 && _cands[0].Estimate > _cands[1].Estimate) (_cands[0], _cands[1]) = (_cands[1], _cands[0]);
    }

    // Levinson-Durbin on _autoc; fills _lpcTab[o * 32 + j] (predictor coefficients) and _errTab[o]. Returns the highest usable order.
    private int Levinson(int maxOrd)
    {
        double err = _autoc[0];
        var cur = _levTmp;
        int valid = 0;
        for (int m = 0; m < maxOrd; m++)
        {
            double acc = _autoc[m + 1];
            for (int j = 0; j < m; j++) acc -= _lpcTab[m * 32 + j] * _autoc[m - j];
            double k = acc / err;
            if (double.IsNaN(k) || Math.Abs(k) >= 1.0) break;
            for (int j = 0; j < m; j++) cur[j] = _lpcTab[m * 32 + j] - k * _lpcTab[m * 32 + (m - 1 - j)];
            cur[m] = k;
            err *= 1 - k * k;
            if (!(err > 0)) break;
            for (int j = 0; j <= m; j++) _lpcTab[(m + 1) * 32 + j] = cur[j];
            _errTab[m + 1] = err;
            valid = m + 1;
        }
        return valid;
    }

    private void TryLpc(FlacSubframe sf, int[] data, int n, int bpsE, int order, int precision, int headerBits)
    {
        if (!Quantize(_lpcTab.AsSpan(order * 32, order), precision, _qc, out int shift)) return;
        var c = _qc.AsSpan(0, order);
        if (!Residual(data, n, bpsE, c, shift)) return;
        long bits = headerBits + (long)order * bpsE + 9 + (long)order * precision + SearchPartitions(_tres.AsSpan(0, n - order), n, order);
        if (bits < sf.Bits) Adopt(sf, FlacSubframe.Lpc, order, bits, n, precision, shift, c);
    }

    // RFC 9639 9.2.6: coefficients are signed precision-bit integers and the shift is 0..15. The rounding error of each
    // coefficient is carried into the next so the quantized predictor tracks the real one.
    private static bool Quantize(ReadOnlySpan<double> lpc, int precision, Span<int> q, out int shift)
    {
        double cmax = 0;
        foreach (double c in lpc) cmax = Math.Max(cmax, Math.Abs(c));
        shift = 0;
        if (!(cmax > 0) || double.IsInfinity(cmax)) return false;
        shift = Math.Clamp(precision - 2 - Math.ILogB(cmax), 0, 15);
        double scale = Math.ScaleB(1.0, shift);
        int qmax = (1 << (precision - 1)) - 1, qmin = -(1 << (precision - 1));
        double err = 0;
        for (int j = 0; j < lpc.Length; j++)
        {
            err += lpc[j] * scale;
            int v = (int)Math.Clamp(Math.Round(err), qmin, qmax);
            q[j] = v;
            err -= v;
        }
        return true;
    }

    // ---- windows -----------------------------------------------------------------------------------------------

    private void EnsureWindows(int n)
    {
        if (_windowsFor == n) return;
        _windowsFor = n;
        int k = 0;
        foreach (var spec in _plan.Windows)
        {
            switch (spec.Kind)
            {
                case WindowSpec.Tukey:
                    Array.Clear(_windows[k], 0, n);
                    Tukey(_windows[k], 0, n, spec.Param);
                    k++;
                    break;
                case WindowSpec.PartialTukey:
                    {
                        // Parts of length L that overlap their neighbours by Overlap * L.
                        double len = n / (spec.Parts - (spec.Parts - 1) * spec.Overlap);
                        for (int p = 0; p < spec.Parts; p++, k++)
                        {
                            Array.Clear(_windows[k], 0, n);
                            int s = (int)(p * len * (1 - spec.Overlap));
                            int e = Math.Min(n, s + (int)len);
                            Tukey(_windows[k], s, e, spec.Param);
                        }
                        break;
                    }
                default:
                    {
                        // Each window keeps everything except one part, with tapered edges where the part was cut out.
                        double len = (double)n / spec.Parts;
                        int ov = (int)(len * spec.Overlap);
                        for (int p = 0; p < spec.Parts; p++, k++)
                        {
                            Array.Clear(_windows[k], 0, n);
                            int cutStart = (int)(p * len), cutEnd = (int)((p + 1) * len);
                            if (cutStart > 0) Tukey(_windows[k], 0, Math.Min(n, cutStart + ov), spec.Param);
                            if (cutEnd < n) Tukey(_windows[k], Math.Max(0, cutEnd - ov), n, spec.Param);
                        }
                        break;
                    }
            }
        }
    }

    // Tukey window of parameter p over [start, end): cosine tapers of p/2 of the length at each end. Written with max so
    // that overlapping segments combine.
    private static void Tukey(float[] w, int start, int end, double p)
    {
        int len = end - start;
        if (len <= 0) return;
        int taper = (int)(p / 2 * len);
        for (int i = 0; i < len; i++)
        {
            int d = Math.Min(i, len - 1 - i);
            float v = d >= taper || taper == 0 ? 1f : (float)(0.5 - 0.5 * Math.Cos(Math.PI * (d + 0.5) / taper));
            if (v > w[start + i]) w[start + i] = v;
        }
    }

    // ---- Rice partition search (RFC 9639 9.2.7) ----------------------------------------------------------------------

    /// <summary>
    /// Picks the partition order and each partition's Rice parameter (or raw-escape width) for the residual in
    /// <paramref name="res"/> and returns the estimated bit cost of the whole residual section. The cost of a partition
    /// is <c>count * (k + 1) + (sum of folded values >> k)</c>, a close upper estimate of the exact size.
    /// </summary>
    private long SearchPartitions(ReadOnlySpan<int> res, int n, int order)
    {
        int pmax = FeasiblePartitionOrder(n, order, _plan.MaxPartitionOrder);
        int parts = 1 << pmax;
        int sz = n >> pmax;
        int off = 0;
        for (int j = 0; j < parts; j++)
        {
            int cnt = sz - (j == 0 ? order : 0);
            FlacEncKernels.FoldedStats(res.Slice(off, cnt), out _sums[parts + j], out _ors[parts + j]);
            off += cnt;
        }
        for (int p = pmax - 1; p >= 0; p--)
            for (int j = 0, np = 1 << p; j < np; j++)
            {
                _sums[np + j] = _sums[2 * np + 2 * j] + _sums[2 * np + 2 * j + 1];
                _ors[np + j] = _ors[2 * np + 2 * j] | _ors[2 * np + 2 * j + 1];
            }

        long best = long.MaxValue;
        int bestP = 0;
        for (int p = pmax; p >= 0; p--)
        {
            int np = 1 << p, psz = n >> p;
            long payload = 0;
            bool wide = false;
            for (int j = 0; j < np; j++)
            {
                payload += PartitionCost(_sums[np + j], psz - (j == 0 ? order : 0), _ors[np + j], out int k, out bool esc);
                if (!esc && k > 14) wide = true;
            }
            long total = 6 + (wide ? 5 : 4) * (long)np + payload;
            if (total < best) { best = total; bestP = p; }
        }

        // Re-cost the chosen partitioning exactly: the estimate above ranks partition orders, but the Rice parameter and the size
        // reported to the caller come from the real unary lengths of the neighbouring parameters.
        int bn = 1 << bestP, bsz = n >> bestP;
        long payloadExact = 0;
        bool wideExact = false;
        off = 0;
        for (int j = 0; j < bn; j++)
        {
            int cnt = bsz - (j == 0 ? order : 0);
            var part = res.Slice(off, cnt);
            off += cnt;
            ulong sum = _sums[bn + j];
            uint orBits = _ors[bn + j];
            PartitionCost(sum, cnt, orBits, out int kEst, out _);
            long bestC = long.MaxValue;
            int bestK = 0;
            int k0 = Math.Min(Math.Max(0, kEst - 1), 29);
            FlacEncKernels.SumShifted3(part, k0, out ulong u0, out ulong u1, out ulong u2);
            for (int t = 0; t < 3; t++)
            {
                int kk = k0 + t;
                if (kk > 30) break; // the parameter field stops at 30 (31 is the escape code of the wide form)
                long c = (long)cnt * (kk + 1) + (long)(t == 0 ? u0 : t == 1 ? u1 : u2);
                if (c < bestC) { bestC = c; bestK = kk; }
            }
            int w = RawWidth(sum, orBits);
            if (w <= 31 && 5 + (long)cnt * w < bestC) { bestC = 5 + (long)cnt * w; bestK = -1; }
            _pParams[j] = bestK;
            _pEsc[j] = w;
            payloadExact += bestC;
            if (bestK > 14) wideExact = true;
        }
        _pOrder = bestP;
        _pMethod = wideExact ? 1 : 0;
        return 6 + (wideExact ? 5 : 4) * (long)bn + payloadExact;
    }

    // Partitions must divide the block evenly and each hold more samples than the predictor order.
    private static int FeasiblePartitionOrder(int n, int order, int limit)
    {
        int p = Math.Min(limit, 15);
        while (p > 0 && ((n & ((1 << p) - 1)) != 0 || (n >> p) <= order)) p--;
        return p;
    }

    // Rice cost with at most 16 partitions and no order search: good enough to rank candidates against each other.
    private long QuickCost(ReadOnlySpan<int> res, int n, int order)
    {
        int p = FeasiblePartitionOrder(n, order, Math.Min(_plan.MaxPartitionOrder, 4));
        int parts = 1 << p, sz = n >> p, off = 0;
        long total = 6 + 4L * parts;
        for (int j = 0; j < parts; j++)
        {
            int cnt = sz - (j == 0 ? order : 0);
            FlacEncKernels.FoldedStats(res.Slice(off, cnt), out ulong sum, out uint or);
            total += PartitionCost(sum, cnt, or, out _, out _);
            off += cnt;
        }
        return total;
    }

    private static int RawWidth(ulong sum, uint orBits) => sum == 0 ? 0 : 32 - BitOperations.LeadingZeroCount(orBits) + 1;

    private static long PartitionCost(ulong sum, int cnt, uint orBits, out int k, out bool escape)
    {
        // floor(log2(sum / cnt)) is within one of floor(log2 sum) - floor(log2 cnt); the best parameter sits about half a bit
        // below the mean's logarithm, so three candidates cover it.
        int hi = sum == 0 ? 0 : Math.Max(0, 63 - BitOperations.LeadingZeroCount(sum) - (31 - BitOperations.LeadingZeroCount((uint)cnt)));
        long best = long.MaxValue;
        k = 0;
        for (int kk = Math.Max(0, hi - 2); kk <= Math.Min(30, hi); kk++)
        {
            long c = (long)cnt * (kk + 1) + (long)(sum >> kk);
            if (c < best) { best = c; k = kk; }
        }
        escape = false;
        int w = RawWidth(sum, orBits);
        if (w <= 31)
        {
            long ec = 5 + (long)cnt * w;
            if (ec < best) { escape = true; return ec; }
        }
        return best;
    }
}
