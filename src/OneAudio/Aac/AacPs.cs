namespace OneAudio.Aac;

/// <summary>
/// Parametric stereo for HE-AAC v2 (ISO/IEC 14496-3 8.6.4, with 3GPP TS 26.402): turns the mono output of the SBR decoder
/// into two channels from inter-channel intensity (IID) and coherence (ICC) parameters. The parameters arrive in the SBR
/// extension; the processing runs on the 64-band QMF slots of the SBR decoder: a hybrid filter bank splits the three lowest
/// QMF bands into ten sub-bands, a decorrelator (delay and allpass chains) makes a diffuse second signal, and a 2x2 rotation
/// mixes the two per parameter group. Baseline profile: inter-channel phase and overall phase parameters (IPD/OPD), which
/// travel in the extension of the parameter data, are skipped and not applied.
/// </summary>
internal sealed class AacPs
{
    private const int Slots = SbrDecoder.Slots;
    private const int HybridBands = 3, HybridSubbands = 10, QmfAllpass = 20, AllpassChannels = 23, DelayChannels = 64 - AllpassChannels;
    private const int Bins = 20, HiBins = 34, Groups = 22, SubGroups = 10, Links = 3, MaxEnv = 5;
    private const int HybridLength = 13, HybridDelay = 6;
    private const int LongDelay = 14, ShortDelay = 1, ShortDelayStart = 12, AllpassDelay = 2;
    private const int DecayCutoff = 3;
    private const float DecaySlope = 0.05f, PeakDecay = 0.765928338364649f, EnergySmooth = 0.25f;
    private static readonly int[] LinkDelay = [3, 4, 5];
    private static readonly float[] LinkDecay = [0.651439057531056f, 0.564718122007759f, 0.489541659556953f];

    // --- bitstream state (8.6.4.5) -----------------------------------------------------------------------------------
    private bool _enableIid, _enableIcc, _enableExt, _fineIid;
    private int _modeIid, _modeIcc;       // 0: 10 bands, 1: 20 bands, 2: 34 bands
    private int _frameClass, _numEnv;
    private readonly int[] _envStart = new int[MaxEnv + 2];
    private readonly bool[] _iidDt = new bool[MaxEnv + 1], _iccDt = new bool[MaxEnv + 1];
    private readonly int[][] _iid = NewIndex(), _icc = NewIndex();
    private readonly int[] _prevIid = new int[HiBins], _prevIcc = new int[HiBins];
    private bool _haveHeader;

    /// <summary>True when the current frame carried parameter data.</summary>
    public bool DataAvailable;

    // --- signal state ------------------------------------------------------------------------------------------------
    private readonly float[][] _histRe = NewRows(HybridBands, HybridLength - 1), _histIm = NewRows(HybridBands, HybridLength - 1);
    private readonly float[] _work = new float[HybridLength];
    private readonly float[] _workIm = new float[HybridLength];
    private readonly float[] _lRe = new float[HybridSubbands], _lIm = new float[HybridSubbands];
    private readonly float[] _rRe = new float[HybridSubbands], _rIm = new float[HybridSubbands];
    private readonly float[] _peak = new float[Bins], _prevPeakDiff = new float[Bins], _prevNrg = new float[Bins];
    private readonly float[] _power = new float[Bins], _transRatio = new float[Bins];

    // decorrelator delay lines: sub-QMF (hybrid) channels and QMF channels
    private readonly float[][] _subDelayRe = NewRows(SubGroups, AllpassDelay), _subDelayIm = NewRows(SubGroups, AllpassDelay);
    private readonly float[][] _qmfDelayRe, _qmfDelayIm;
    private readonly float[][][] _subSerRe, _subSerIm, _qmfSerRe, _qmfSerIm; // [link][delay slot][channel]
    private readonly int[] _serIndex = new int[Links];
    private readonly int[] _delayIndex = new int[DelayChannels];
    private readonly int[] _delayLength = new int[DelayChannels];
    private int _allpassIndex;

    // rotation
    private readonly float[] _h11 = new float[Groups], _h12 = new float[Groups], _h21 = new float[Groups], _h22 = new float[Groups];
    private readonly float[] _d11 = new float[Groups], _d12 = new float[Groups], _d21 = new float[Groups], _d22 = new float[Groups];
    private readonly float[] _p11 = new float[Groups], _p12 = new float[Groups], _p21 = new float[Groups], _p22 = new float[Groups];
    private int _usb, _lastUsb;

    private static readonly float[] Cos8 = BuildPhase(false), Sin8 = BuildPhase(true);

    public AacPs()
    {
        _qmfDelayRe = new float[QmfAllpass + DelayChannels][];
        _qmfDelayIm = new float[QmfAllpass + DelayChannels][];
        for (int i = 0; i < DelayChannels; i++) _delayLength[i] = i < ShortDelayStart ? LongDelay : ShortDelay;
        for (int i = 0; i < _qmfDelayRe.Length; i++)
        {
            int len = i < QmfAllpass ? AllpassDelay : _delayLength[i - QmfAllpass];
            _qmfDelayRe[i] = new float[len];
            _qmfDelayIm[i] = new float[len];
        }
        _subSerRe = NewSerial(SubGroups); _subSerIm = NewSerial(SubGroups);
        _qmfSerRe = NewSerial(QmfAllpass); _qmfSerIm = NewSerial(QmfAllpass);
        Reset();
    }

    private static int[][] NewIndex()
    {
        var a = new int[MaxEnv + 1][];
        for (int i = 0; i < a.Length; i++) a[i] = new int[HiBins];
        return a;
    }

    private static float[][] NewRows(int n, int len)
    {
        var a = new float[n][];
        for (int i = 0; i < n; i++) a[i] = new float[len];
        return a;
    }

    private static float[][][] NewSerial(int channels)
    {
        var a = new float[Links][][];
        for (int m = 0; m < Links; m++) a[m] = NewRows(LinkDelay[m], channels);
        return a;
    }

    // phase factors e^{-j 2 pi/8 (k + 1/2)(n - 6)} of the 8-band hybrid filter
    private static float[] BuildPhase(bool sine)
    {
        var t = new float[8 * HybridLength];
        for (int k = 0; k < 8; k++)
            for (int n = 0; n < HybridLength; n++)
            {
                double a = -2.0 * Math.PI / 8.0 * (k + 0.5) * (n - HybridDelay);
                t[k * HybridLength + n] = (float)(sine ? Math.Sin(a) : Math.Cos(a));
            }
        return t;
    }

    /// <summary>Forgets all signal and parameter history.</summary>
    public void Reset()
    {
        foreach (var r in _histRe) Array.Clear(r);
        foreach (var r in _histIm) Array.Clear(r);
        Array.Clear(_peak); Array.Clear(_prevPeakDiff); Array.Clear(_prevNrg);
        foreach (var r in _subDelayRe) Array.Clear(r);
        foreach (var r in _subDelayIm) Array.Clear(r);
        foreach (var r in _qmfDelayRe) Array.Clear(r);
        foreach (var r in _qmfDelayIm) Array.Clear(r);
        for (int m = 0; m < Links; m++)
            for (int j = 0; j < LinkDelay[m]; j++)
            {
                Array.Clear(_subSerRe[m][j]); Array.Clear(_subSerIm[m][j]);
                Array.Clear(_qmfSerRe[m][j]); Array.Clear(_qmfSerIm[m][j]);
            }
        Array.Clear(_serIndex); Array.Clear(_delayIndex);
        _allpassIndex = 0;
        for (int g = 0; g < Groups; g++) { _p11[g] = 1f; _p12[g] = 1f; _p21[g] = 0f; _p22[g] = 0f; }
        Array.Clear(_prevIid); Array.Clear(_prevIcc);
        _usb = _lastUsb = 0;
        _numEnv = 0;
        DataAvailable = false;
        _haveHeader = false;
        _enableIid = _enableIcc = _enableExt = _fineIid = false;
        _modeIid = _modeIcc = 0;
    }

    /// <summary>Marks the start of a frame: no parameters have been read yet.</summary>
    public void BeginFrame() => DataAvailable = false;

    /// <summary>The parameters of a lost or unusable frame are not taken: the previous ones stay in force.</summary>
    public void Conceal() => DataAvailable = false;

    // --- parsing (8.6.4.5 ps_data) -----------------------------------------------------------------------------------

    private static int Huff(ReadOnlySpan<sbyte> tree, ref AacBitReader br)
    {
        int index = 0;
        for (int guard = 0; guard < 64; guard++)
        {
            index = tree[index * 2 + (br.ReadBit() ? 1 : 0)];
            if (index <= 0) return index + 64;
            if (br.Overrun) return 0;
        }
        return 0;
    }

    private static int BinCount(int mode) => mode == 0 ? 10 : mode == 1 ? 20 : HiBins;

    /// <summary>
    /// Reads one ps_data element. <paramref name="bitsLeft"/> is what remains of the extension payload; the return value is
    /// the number of bits consumed, or -1 when the data is malformed.
    /// </summary>
    public int Read(ref AacBitReader br, int bitsLeft)
    {
        int start = br.Position;
        bool header = br.ReadBit();
        if (header)
        {
            _haveHeader = true;
            _enableIid = br.ReadBit();
            if (_enableIid)
            {
                int mode = (int)br.Read(3);
                _fineIid = mode > 2;
                _modeIid = _fineIid ? mode - 3 : mode;
                if (mode > 5) return -1;
            }
            _enableIcc = br.ReadBit();
            if (_enableIcc)
            {
                int mode = (int)br.Read(3);
                if (mode > 5) return -1;
                // modes 3..5 announce IPD/OPD data; it travels in the extension, which is skipped (baseline profile)
                _modeIcc = mode > 2 ? mode - 3 : mode;
            }
            _enableExt = br.ReadBit();
        }
        if (Environment.GetEnvironmentVariable("DBG4") is not null) Console.Error.WriteLine($"PS header={header} iid={_enableIid}/{_modeIid}/{_fineIid} icc={_enableIcc}/{_modeIcc} ext={_enableExt} bitsLeft={bitsLeft}");
        if (!_haveHeader) return Skip(ref br, start, bitsLeft);

        _frameClass = br.ReadBit() ? 1 : 0;
        if (_frameClass == 0)
        {
            ReadOnlySpan<int> fixedEnv = [0, 1, 2, 4];
            _numEnv = fixedEnv[(int)br.Read(2)];
        }
        else
        {
            _numEnv = 1 + (int)br.Read(2);
            for (int e = 1; e <= _numEnv; e++) _envStart[e] = (int)br.Read(5) + 1;
        }
        if (_enableIid)
        {
            for (int e = 0; e < _numEnv; e++)
            {
                bool dt = br.ReadBit();
                _iidDt[e] = dt;
                ReadOnlySpan<sbyte> tree = _fineIid ? (dt ? AacSbrTables.PsIidFineTime : AacSbrTables.PsIidFineFreq) : (dt ? AacSbrTables.PsIidTime : AacSbrTables.PsIidFreq);
                int n = BinCount(_modeIid);
                for (int b = 0; b < n; b++) _iid[e][b] = Huff(tree, ref br);
            }
        }
        if (_enableIcc)
        {
            for (int e = 0; e < _numEnv; e++)
            {
                bool dt = br.ReadBit();
                _iccDt[e] = dt;
                ReadOnlySpan<sbyte> tree = dt ? AacSbrTables.PsIccTime : AacSbrTables.PsIccFreq;
                int n = BinCount(_modeIcc);
                for (int b = 0; b < n; b++) _icc[e][b] = Huff(tree, ref br);
            }
        }
        if (_enableExt)
        {
            int cnt = (int)br.Read(4);
            if (cnt == 15) cnt += (int)br.Read(8);
            br.Skip(cnt * 8);
        }
        if (br.Overrun) return -1;
        DataAvailable = true;
        return br.Position - start;
    }

    private static int Skip(ref AacBitReader br, int start, int bitsLeft)
    {
        int rest = bitsLeft - (br.Position - start);
        if (rest < 0) return -1;
        br.Skip(rest);
        return br.Overrun ? -1 : bitsLeft;
    }

    // --- parameter decoding (8.6.4.6.2) ------------------------------------------------------------------------------

    private static int Clamp(int v, int lo, int hi) => v < lo ? lo : v > hi ? hi : v;

    // delta decoding of one envelope's indices in frequency or time direction, with expansion of the 10 band mode to 20
    private static void DeltaDecode(bool enabled, int[] index, int[] prev, bool timeDirection, int count, int stride, int lo, int hi)
    {
        if (enabled)
        {
            if (!timeDirection)
            {
                index[0] = Clamp(index[0], lo, hi);
                for (int i = 1; i < count; i++) index[i] = Clamp(index[i - 1] + index[i], lo, hi);
            }
            else
            {
                for (int i = 0; i < count; i++) index[i] = Clamp(prev[i * stride] + index[i], lo, hi);
            }
        }
        else
        {
            for (int i = 0; i < count; i++) index[i] = 0;
        }
        if (stride == 2)
            for (int i = count * stride - 1; i > 0; i--) index[i] = index[i / stride];
    }

    // the 34 band layout is folded onto the 20 band layout of the processing (8.6.4.6.3, mapping of table 8.A.?)
    private static void Map34To20(int[] a)
    {
        a[0] = (2 * a[0] + a[1]) / 3;
        a[1] = (a[1] + 2 * a[2]) / 3;
        a[2] = (2 * a[3] + a[4]) / 3;
        a[3] = (a[4] + 2 * a[5]) / 3;
        a[4] = (a[6] + a[7]) / 2;
        a[5] = (a[8] + a[9]) / 2;
        a[6] = a[10];
        a[7] = a[11];
        a[8] = (a[12] + a[13]) / 2;
        a[9] = (a[14] + a[15]) / 2;
        a[10] = a[16];
        a[11] = a[17];
        a[12] = a[18];
        a[13] = a[19];
        a[14] = (a[20] + a[21]) / 2;
        a[15] = (a[22] + a[23]) / 2;
        a[16] = (a[24] + a[25]) / 2;
        a[17] = (a[26] + a[27]) / 2;
        a[18] = (a[28] + a[29] + a[30] + a[31]) / 4;
        a[19] = (a[32] + a[33]) / 2;
    }

    private void DecodeParameters()
    {
        if (!DataAvailable) _numEnv = 0;
        int iidSteps = _fineIid ? 15 : 7;
        for (int e = 0; e < _numEnv; e++)
        {
            int[] prevI = e == 0 ? _prevIid : _iid[e - 1];
            int[] prevC = e == 0 ? _prevIcc : _icc[e - 1];
            DeltaDecode(_enableIid, _iid[e], prevI, _iidDt[e], BinCount(_modeIid), _modeIid != 0 ? 1 : 2, -iidSteps, iidSteps);
            DeltaDecode(_enableIcc, _icc[e], prevC, _iccDt[e], BinCount(_modeIcc), _modeIcc != 0 ? 1 : 2, 0, 7);
        }
        if (_numEnv == 0)
        {
            _numEnv = 1;
            for (int b = 0; b < HiBins; b++)
            {
                _iid[0][b] = _enableIid ? _prevIid[b] : 0;
                _icc[0][b] = _enableIcc ? _prevIcc[b] : 0;
            }
        }
        Array.Copy(_iid[_numEnv - 1], _prevIid, HiBins);
        Array.Copy(_icc[_numEnv - 1], _prevIcc, HiBins);
        DataAvailable = false;

        _envStart[0] = 0;
        if (_frameClass == 0)
        {
            int shift = _numEnv == 1 ? 0 : _numEnv == 2 ? 1 : 2;
            for (int e = 1; e < _numEnv; e++) _envStart[e] = (e * Slots) >> shift;
            _envStart[_numEnv] = Slots;
        }
        else
        {
            if (_envStart[_numEnv] < Slots)
            {
                _numEnv++;
                _envStart[_numEnv] = Slots;
                Array.Copy(_iid[_numEnv - 2], _iid[_numEnv - 1], HiBins);
                Array.Copy(_icc[_numEnv - 2], _icc[_numEnv - 1], HiBins);
            }
            for (int e = 1; e < _numEnv; e++)
            {
                int hiLimit = Slots - _numEnv + e;
                if (_envStart[e] > hiLimit) _envStart[e] = hiLimit;
                else if (_envStart[e] < _envStart[e - 1] + 1) _envStart[e] = _envStart[e - 1] + 1;
            }
        }
        for (int e = 0; e < _numEnv; e++)
        {
            if (_modeIid == 2) Map34To20(_iid[e]);
            if (_modeIcc == 2) Map34To20(_icc[e]);
        }
    }

    // --- processing --------------------------------------------------------------------------------------------------

    /// <summary>
    /// Runs the parametric stereo stages over the 32 slots of the frame. <paramref name="left"/> holds the mono SBR output on
    /// entry and becomes the left channel; the right channel is written to <paramref name="right"/>. Bands from
    /// <paramref name="usb"/> up are silent.
    /// </summary>
    public void Apply(SbrChannel left, SbrChannel right, int usb)
    {
        DecodeParameters();
        int env = 0;
        for (int s = 0; s < Slots; s++)
        {
            if (env < _numEnv && s == _envStart[env])
            {
                InitRotation(env, usb);
                env++;
            }
            ProcessSlot(left, right, s);
        }
    }

    private void ProcessSlot(SbrChannel left, SbrChannel right, int s)
    {
        var lRe = left.Re[s];
        var lIm = left.Im[s];
        var rRe = right.Re[s];
        var rIm = right.Im[s];
        Array.Clear(rRe);
        Array.Clear(rIm);
        HybridAnalysis(left, s);
        Decorrelate(lRe, lIm, rRe, rIm);
        Rotate(lRe, lIm, rRe, rIm);
        // hybrid synthesis: the sub-bands of a QMF band add up
        int o = 0;
        for (int b = 0; b < HybridBands; b++)
        {
            int n = b == 0 ? 6 : 2;
            float ar = 0, ai = 0, br = 0, bi = 0;
            for (int k = 0; k < n; k++)
            {
                ar += _lRe[o + k]; ai += _lIm[o + k];
                br += _rRe[o + k]; bi += _rIm[o + k];
            }
            lRe[b] = ar; lIm[b] = ai;
            rRe[b] = br; rIm[b] = bi;
            o += n;
        }
    }

    // 8.6.4.6.1: the lowest three QMF bands are split with a 13-tap filter; the sample used is six slots ahead
    private void HybridAnalysis(SbrChannel left, int s)
    {
        int o = 0;
        for (int b = 0; b < HybridBands; b++)
        {
            var hr = _histRe[b];
            var hi = _histIm[b];
            Array.Copy(hr, _work, HybridLength - 1);
            Array.Copy(hi, _workIm, HybridLength - 1);
            _work[HybridLength - 1] = left.Re[s + HybridDelay][b];
            _workIm[HybridLength - 1] = left.Im[s + HybridDelay][b];
            Array.Copy(_work, 1, hr, 0, HybridLength - 1);
            Array.Copy(_workIm, 1, hi, 0, HybridLength - 1);
            if (b == 0)
            {
                EightBand(o);
                o += 6;
            }
            else
            {
                TwoBand(_work, o, true);
                TwoBand(_workIm, o, false);
                o += 2;
            }
        }
    }

    private void TwoBand(float[] x, int o, bool real)
    {
        float centre = 0.5f * x[HybridDelay];
        float side = 0;
        var p = AacSbrTables.PsHybrid6;
        for (int n = 0; n < 6; n++) side += p[n] * x[2 * n + 1];
        if (real) { _lRe[o] = centre + side; _lRe[o + 1] = centre - side; }
        else { _lIm[o] = centre + side; _lIm[o + 1] = centre - side; }
    }

    // The 8-band complex modulated filter. The published prototype carries a sign on its first five taps that undoes the
    // sign flip a 13-tap filter gets when its outer taps fold onto the 8-point grid, so those taps are negated back.
    private void EightBand(int o)
    {
        Span<float> outRe = stackalloc float[8];
        Span<float> outIm = stackalloc float[8];
        var p = AacSbrTables.PsHybrid13;
        for (int k = 0; k < 8; k++)
        {
            float re = 0, im = 0;
            for (int n = 0; n < HybridLength; n++)
            {
                float g = n < 5 ? -p[n] : p[n];
                float c = Cos8[k * HybridLength + n], sn = Sin8[k * HybridLength + n];
                float xr = _work[n], xi = _workIm[n];
                re += g * (xr * c - xi * sn);
                im += g * (xr * sn + xi * c);
            }
            outRe[k] = re;
            outIm[k] = im;
        }
        _lRe[o] = outRe[0]; _lIm[o] = outIm[0];
        _lRe[o + 1] = outRe[1]; _lIm[o + 1] = outIm[1];
        _lRe[o + 2] = outRe[2] + outRe[5]; _lIm[o + 2] = outIm[2] + outIm[5];
        _lRe[o + 3] = outRe[3] + outRe[4]; _lIm[o + 3] = outIm[3] + outIm[4];
        _lRe[o + 4] = outRe[6]; _lIm[o + 4] = outIm[6];
        _lRe[o + 5] = outRe[7]; _lIm[o + 5] = outIm[7];
    }

    private static int Bin(int group) => AacSbrTables.PsBins2Group[group] & ~0x1000;

    private static int GroupEnd(int group, int usb) => group < SubGroups ? AacSbrTables.PsGroupBorders[group] + 1 : Math.Min(usb, AacSbrTables.PsGroupBorders[group + 1]);

    // 8.6.4.6.4: the diffuse signal. Transient detection per parameter bin attenuates it where the energy jumps.
    private void Decorrelate(float[] qRe, float[] qIm, float[] outRe, float[] outIm)
    {
        int usb = _usb;
        Array.Clear(_power);
        for (int g = 0; g < Groups; g++)
        {
            int bin = Bin(g);
            var re = g < SubGroups ? _lRe : qRe;
            var im = g < SubGroups ? _lIm : qIm;
            int end = GroupEnd(g, usb);
            for (int sb = AacSbrTables.PsGroupBorders[g]; sb < end; sb++) _power[bin] += re[sb] * re[sb] + im[sb] * im[sb];
        }
        for (int b = 0; b < Bins; b++)
        {
            _peak[b] *= PeakDecay;
            if (_peak[b] < _power[b]) _peak[b] = _power[b];
            float diff = _prevPeakDiff[b] + (_peak[b] - _power[b] - _prevPeakDiff[b]) * EnergySmooth;
            _prevPeakDiff[b] = diff;
            float nrg = _prevNrg[b] + (_power[b] - _prevNrg[b]) * EnergySmooth;
            _prevNrg[b] = nrg;
            diff *= 1.5f;
            _transRatio[b] = diff <= nrg ? 1.0f : nrg / diff;
        }
        for (int g = 0; g < Groups; g++)
        {
            bool sub = g < SubGroups;
            int bin = Bin(g);
            var inRe = sub ? _lRe : qRe;
            var inIm = sub ? _lIm : qIm;
            var dstRe = sub ? _rRe : outRe;
            var dstIm = sub ? _rIm : outIm;
            int offset = sub ? 0 : HybridBands;
            int end = GroupEnd(g, usb);
            for (int sb = AacSbrTables.PsGroupBorders[g]; sb < end; sb++)
            {
                int ch = sb - offset;
                float xr = inRe[sb], xi = inIm[sb];
                float yr, yi;
                if (!sub && sb >= AllpassChannels)
                {
                    // pure delay above the allpass region
                    int d = sb - AllpassChannels;
                    var dr = _qmfDelayRe[ch];
                    var di = _qmfDelayIm[ch];
                    int at = _delayIndex[d];
                    yr = dr[at]; yi = di[at];
                    dr[at] = xr; di[at] = xi;
                    if (++_delayIndex[d] >= _delayLength[d]) _delayIndex[d] = 0;
                }
                else
                {
                    float decay = sub || sb <= DecayCutoff ? 1.0f : (1.0f + DecayCutoff * DecaySlope) - DecaySlope * sb;
                    var dr = sub ? _subDelayRe[ch] : _qmfDelayRe[ch];
                    var di = sub ? _subDelayIm[ch] : _qmfDelayIm[ch];
                    float fr = sub ? AacSbrTables.PsFractDelayReSubQmf[ch] : AacSbrTables.PsFractDelayReQmf[ch];
                    float fi = sub ? AacSbrTables.PsFractDelayImSubQmf[ch] : AacSbrTables.PsFractDelayImQmf[ch];
                    float ir = dr[_allpassIndex], ii = di[_allpassIndex];
                    yr = ir * fr - ii * fi;
                    yi = ir * fi + ii * fr;
                    dr[_allpassIndex] = xr;
                    di[_allpassIndex] = xi;
                    for (int m = 0; m < Links; m++)
                    {
                        var serRe = sub ? _subSerRe[m][_serIndex[m]] : _qmfSerRe[m][_serIndex[m]];
                        var serIm = sub ? _subSerIm[m][_serIndex[m]] : _qmfSerIm[m][_serIndex[m]];
                        float tr = serRe[ch], ti = serIm[ch];
                        float pr = SerialPhaseRe(sub, m, ch), pi = SerialPhaseIm(sub, m, ch);
                        float nr = tr * pr - ti * pi;
                        float ni = tr * pi + ti * pr;
                        float a = decay * LinkDecay[m];
                        nr += -a * yr;
                        ni += -a * yi;
                        serRe[ch] = yr + a * nr;
                        serIm[ch] = yi + a * ni;
                        yr = nr;
                        yi = ni;
                    }
                }
                dstRe[sb] = _transRatio[bin] * yr;
                dstIm[sb] = _transRatio[bin] * yi;
            }
        }
        if (++_allpassIndex >= AllpassDelay) _allpassIndex = 0;
        for (int m = 0; m < Links; m++)
            if (++_serIndex[m] >= LinkDelay[m]) _serIndex[m] = 0;
    }

    private static float SerialPhaseRe(bool sub, int link, int ch) => sub
        ? link switch { 0 => AacSbrTables.PsSerReSubQmf0[ch], 1 => AacSbrTables.PsSerReSubQmf1[ch], _ => AacSbrTables.PsSerReSubQmf2[ch] }
        : link switch { 0 => AacSbrTables.PsSerReQmf0[ch], 1 => AacSbrTables.PsSerReQmf1[ch], _ => AacSbrTables.PsSerReQmf2[ch] };

    private static float SerialPhaseIm(bool sub, int link, int ch) => sub
        ? link switch { 0 => AacSbrTables.PsSerImSubQmf0[ch], 1 => AacSbrTables.PsSerImSubQmf1[ch], _ => AacSbrTables.PsSerImSubQmf2[ch] }
        : link switch { 0 => AacSbrTables.PsSerImQmf0[ch], 1 => AacSbrTables.PsSerImQmf1[ch], _ => AacSbrTables.PsSerImQmf2[ch] };

    // 8.6.4.6.5: mixing matrices of an envelope, interpolated slot by slot from the previous envelope's
    private void InitRotation(int env, int usb)
    {
        if (env == 0)
        {
            _lastUsb = _usb;
            _usb = usb;
            if (usb > _lastUsb && _lastUsb != 0)
            {
                // bands that were silent before have no history: clear their delay lines
                for (int sb = _lastUsb - HybridBands; sb < usb - HybridBands; sb++)
                {
                    int kmax = 2;
                    if (sb < QmfAllpass)
                    {
                        for (int m = 0; m < Links; m++)
                            for (int k = 0; k < LinkDelay[m]; k++) { _qmfSerRe[m][k][sb] = 0; _qmfSerIm[m][k][sb] = 0; }
                    }
                    else
                    {
                        kmax = _delayLength[sb - QmfAllpass];
                    }
                    for (int k = 0; k < kmax; k++) { _qmfDelayRe[sb][k] = 0; _qmfDelayIm[sb][k] = 0; }
                }
            }
        }
        float length = _envStart[env + 1] - _envStart[env];
        float inv = length == Slots ? 1.0f / Slots : 1.0f / length;
        int steps = _fineIid ? 15 : 7;
        var scale = _fineIid ? AacSbrTables.PsScaleFactorsFine : AacSbrTables.PsScaleFactors;
        var alphas = AacSbrTables.PsAlphas;
        for (int g = 0; g < Groups; g++)
        {
            int bin = Bin(g);
            float scaleR = scale[steps + _iid[env][bin]];
            float scaleL = scale[steps - _iid[env][bin]];
            float alpha = alphas[_icc[env][bin]];
            float beta = alpha * (scaleR - scaleL) / 1.41421356237f;
            float h11 = (float)(scaleL * Math.Cos(beta + alpha));
            float h12 = (float)(scaleR * Math.Cos(beta - alpha));
            float h21 = (float)(scaleL * Math.Sin(beta + alpha));
            float h22 = (float)(scaleR * Math.Sin(beta - alpha));
            _d11[g] = (h11 - _p11[g]) * inv;
            _d12[g] = (h12 - _p12[g]) * inv;
            _d21[g] = (h21 - _p21[g]) * inv;
            _d22[g] = (h22 - _p22[g]) * inv;
            _h11[g] = _p11[g]; _h12[g] = _p12[g]; _h21[g] = _p21[g]; _h22[g] = _p22[g];
            _p11[g] = h11; _p12[g] = h12; _p21[g] = h21; _p22[g] = h22;
        }
    }

    private void Rotate(float[] qLRe, float[] qLIm, float[] qRRe, float[] qRIm)
    {
        int usb = _usb;
        for (int g = 0; g < Groups; g++)
        {
            bool sub = g < SubGroups;
            var lr = sub ? _lRe : qLRe;
            var li = sub ? _lIm : qLIm;
            var rr = sub ? _rRe : qRRe;
            var ri = sub ? _rIm : qRIm;
            int end = GroupEnd(g, usb);
            _h11[g] += _d11[g]; _h12[g] += _d12[g]; _h21[g] += _d21[g]; _h22[g] += _d22[g];
            float h11 = _h11[g], h12 = _h12[g], h21 = _h21[g], h22 = _h22[g];
            for (int sb = AacSbrTables.PsGroupBorders[g]; sb < end; sb++)
            {
                float a = lr[sb], b = li[sb], c = rr[sb], d = ri[sb];
                lr[sb] = h11 * a + h21 * c;
                li[sb] = h11 * b + h21 * d;
                rr[sb] = h12 * a + h22 * c;
                ri[sb] = h12 * b + h22 * d;
            }
        }
    }
}
