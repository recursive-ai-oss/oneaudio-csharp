namespace OneAudio.Aac;

/// <summary>
/// The spectral band replication decoder of HE-AAC (ISO/IEC 14496-3 4.6.18, subpart 4 clause 4.5.2.8 for the payload), for
/// one mono or stereo stream. It consumes the AAC core output of one access unit (1024 samples per channel) and the
/// SBR payload that came with it, and produces 2048 samples per channel at twice the core rate, or 1024 in the
/// downsampled mode where the output stays at the core rate. With parametric stereo (4.6.19) a mono core yields two
/// output channels. Works on 16-bit scaled values internally, as the energy constants of the standard assume.
/// </summary>
internal sealed class SbrDecoder
{
    internal const int Slots = 32;              // QMF time slots per frame
    internal const int NumTimeSlots = 16;       // envelope time slots per frame
    private const float ToInternal = 32768f, FromInternal = 1f / 32768f;

    private readonly int _fsSbr;                // twice the core rate: the rate the band tables are keyed by
    private readonly int _coreChannels;
    private readonly bool _downsampled;
    private readonly SbrChannel[] _ch;
    private readonly SbrFrame[] _frame = [new(), new()];
    private readonly SbrPrev[] _prev = [new(), new()];
    private readonly SbrHeader _hdr = new(), _newHdr = new();
    private readonly SbrBands _bands = new();
    private readonly float[] _saved0 = new float[SbrBands.MaxFreqCoeffs], _saved1 = new float[SbrBands.MaxFreqCoeffs];
    private readonly float[] _in = new float[1024];
    private readonly float[] _slotOut = new float[64];
    private readonly bool[] _needFreq = new bool[2];
    private readonly AacPs? _ps;

    private int _au;
    private bool _active;        // a header has been received and its tables built
    private bool _pair;
    private bool _frameOk;

    /// <summary>Output samples per channel for one access unit.</summary>
    public int OutputFrame { get; }

    /// <summary>Output channels: 2 when parametric stereo expands a mono core, else the core's.</summary>
    public int OutputChannels { get; }

    /// <summary>True once any frame carried parametric stereo data.</summary>
    public bool PsSeen { get; private set; }

    /// <summary>True when a header has been received and the high band is being generated.</summary>
    public bool Active => _active;

    public SbrDecoder(int coreRate, int coreChannels, bool downsampled, bool parametricStereo)
    {
        _fsSbr = 2 * coreRate;
        _coreChannels = coreChannels;
        _downsampled = downsampled;
        if (parametricStereo && coreChannels != 1) throw new ArgumentException("Parametric stereo needs a mono core.", nameof(parametricStereo));
        OutputChannels = parametricStereo ? 2 : coreChannels;
        OutputFrame = downsampled ? 1024 : 2048;
        int synth = downsampled ? 32 : 64;
        _ch = new SbrChannel[OutputChannels];
        for (int i = 0; i < _ch.Length; i++) _ch[i] = new SbrChannel(synth);
        if (parametricStereo) _ps = new AacPs();
        Reset(true);
    }

    /// <summary>Forgets the signal history. With <paramref name="all"/> the stream header is forgotten too.</summary>
    public void Reset(bool all)
    {
        foreach (var c in _ch) c.Reset();
        for (int i = 0; i < 2; i++)
        {
            _prev[i].Init(NumTimeSlots);
            _needFreq[i] = true;
        }
        _ps?.Reset();
        if (all)
        {
            _active = false;
            _bands.LowSubband = _bands.HighSubband = 32;
            _hdr.CopyFrom(new SbrHeader());
            PsSeen = false;
        }
    }

    // --- payload -------------------------------------------------------------------------------------------------------

    // (experiment)
    private readonly byte[] _dBuf = new byte[272]; private int _dBits; private bool _dCrc, _dPair, _dHave;
    private static readonly bool Delay = Environment.GetEnvironmentVariable("DELAY") is not null;
    /// <summary>
    /// Supplies the SBR payload of the next access unit (the bits after the extension type nibble of the fill element).
    /// <paramref name="bits"/> is its length in bits. <paramref name="crc"/> marks extension type 0xE, which starts with
    /// a 10-bit CRC that is skipped, not checked.
    /// </summary>
    public void SetPayload(ReadOnlySpan<byte> data, int bits, bool crc, bool pairElement)
    {
        if (Delay)
        {
            bool had = _dHave; int b0 = _dBits; bool c0 = _dCrc, p0 = _dPair; var tmp = _dBuf.AsSpan(0, (b0 + 7) / 8).ToArray();
            data.Slice(0, (bits + 7) / 8).CopyTo(_dBuf); _dBits = bits; _dCrc = crc; _dPair = pairElement; _dHave = true;
            if (!had) { _frameOk = false; return; }
            SetPayloadCore(tmp, b0, c0, p0);
            return;
        }
        SetPayloadCore(data, bits, crc, pairElement);
    }
    private void SetPayloadCore(ReadOnlySpan<byte> data, int bits, bool crc, bool pairElement)
    {
        _pair = pairElement;
        _frameOk = false;
        var br = new AacBitReader(data.Slice(0, Math.Min(data.Length, (bits + 7) >> 3)));
        if (crc) br.Skip(10);
        if (pairElement != (_coreChannels == 2))
        {
            // an SBR element for the wrong kind of core element (dual mono is not supported)
            return;
        }
        bool ok = true;
        if (Environment.GetEnvironmentVariable("DBG2") is not null) Console.Error.WriteLine($"payload bits={bits} first={data[0]:x2}{data[1]:x2}{data[2]:x2}{data[3]:x2} crc={crc} pair={pairElement}");
        if (br.ReadBit())
        {
            ok = ReadHeaderAndBuild(ref br);
        }
        else if (!_active)
        {
            ok = false; AacSbrSyntax.Why = "noheader";
        }
        if (ok && _active)
        {
            if (_pair) ok = AacSbrSyntax.ReadPair(ref br, _bands, _hdr, _frame[0], _frame[1], NumTimeSlots);
            else
            {
                if (_ps is not null) _ps.BeginFrame();
                ok = AacSbrSyntax.ReadSingle(ref br, _bands, _hdr, _frame[0], _ps, NumTimeSlots);
                if (_ps is not null && _ps.DataAvailable) PsSeen = true;
            }
            if (ok && br.Position > bits) { ok = false; AacSbrSyntax.Why = "overread"; }
        }
        _frameOk = ok && _active;
        if (!ok && Environment.GetEnvironmentVariable("DBG3") is not null) Console.Error.WriteLine($"PARSEFAIL {AacSbrSyntax.Why} pos={br.Position} bits={bits}");
        if (Environment.GetEnvironmentVariable("DBG") is not null) { _au++; if (_pair) { var g = _frame[1]; Console.Error.WriteLine($"   R: cls={g.FrameClass} nenv={g.NEnvelopes} borders={string.Join(',', g.Borders.Take(g.NEnvelopes+1))} fr={string.Join(',', g.FreqRes.Take(g.NEnvelopes))} dom={g.DomainVec[0]},{g.DomainVec[1]} cpl={g.Coupling} amp={g.AmpResCurrent} nsf={g.NScaleFactors}"); } var f = _frame[0]; Console.Error.WriteLine($"au{_au} ok={ok} cls={f.FrameClass} nenv={f.NEnvelopes} borders={string.Join(',', f.Borders.Take(f.NEnvelopes+1))} tran={f.TranEnv} freqres={string.Join(',', f.FreqRes.Take(f.NEnvelopes))} dom={f.DomainVec[0]} hdr: s{_hdr.StartFreq} e{_hdr.StopFreq} x{_hdr.XoverBand} fs{_hdr.FreqScale} as{_hdr.AlterScale} nb{_hdr.NoiseBands} lb{_hdr.LimiterBands} lg{_hdr.LimiterGains} if{_hdr.InterpolFreq} sm{_hdr.SmoothingLength} ar{_hdr.AmpResolution} dn={f.DomainVecNoise[0]},{f.DomainVecNoise[1]} nn={f.NNoiseEnvelopes} invf={string.Join(',', f.InvfMode.Take(_bands.NNoise))} ah={string.Join("",f.AddHarmonics.Take(_bands.NSfb[1]))} env={string.Join(',',f.Envelope.Take(Math.Min(f.NScaleFactors,12)))} nf={string.Join(',',f.NoiseFloor.Take(_bands.NNoise*f.NNoiseEnvelopes))} lo={_bands.LowSubband} hi={_bands.HighSubband} np={_bands.NPatches}"); }
    }

    /// <summary>Marks the access unit as having no SBR payload (a lost packet, or a stream whose payload is absent here).</summary>
    public void NoPayload()
    {
        _frameOk = false;
    }

    /// <summary>Parses a payload only, to learn whether the stream carries parametric stereo. Used when opening a stream.</summary>
    public void Scan(ReadOnlySpan<byte> data, int bits, bool crc, bool pairElement) => SetPayload(data, bits, crc, pairElement);

    private bool ReadHeaderAndBuild(ref AacBitReader br)
    {
        AacSbrSyntax.ReadHeader(ref br, _newHdr);
        if (br.Overrun) return false;
        bool layout = !_active || _newHdr.LayoutDiffers(_hdr);
        bool limiter = _newHdr.LimiterBands != _hdr.LimiterBands;
        _hdr.CopyFrom(_newHdr);
        if (layout)
        {
            int oldLsb = _bands.LowSubband;
            if (!_bands.Reset(_hdr, _fsSbr) || !_bands.ResetPatches(_fsSbr) || !_bands.ResetLimiter(_hdr.LimiterBands))
            {
                if (_bands.UnverifiedLayout)
                    throw new UnsupportedAudioFormatException($"HE-AAC at an SBR rate of {_fsSbr} Hz with bs_start_freq {_hdr.StartFreq} is not supported; only bs_start_freq 9 is verified at 64, 88.2 and 96 kHz.");
                _active = false;
                _bands.LowSubband = _bands.HighSubband = 32;
                return false;
            }
            for (int c = 0; c < _ch.Length; c++)
            {
                _ch[c].ResetEnvelope();
                _ch[c].ResetQmfRange(oldLsb, _bands.LowSubband, _prev[Math.Min(c, 1)].StopPos, c == 0);
            }
            _active = true;
        }
        else if (limiter && Environment.GetEnvironmentVariable("NOLIM") is null)
        {
            if (!_bands.ResetLimiter(_hdr.LimiterBands)) { _active = false; return false; }
        }
        return true;
    }

    // --- one access unit -----------------------------------------------------------------------------------------------

    /// <summary>
    /// Runs the SBR stages on the core output <paramref name="core"/> (interleaved, <c>coreChannels</c> x 1024 samples, 1.0
    /// being 16-bit full scale) and writes the interleaved result to <paramref name="output"/>.
    /// </summary>
    public void Process(ReadOnlySpan<float> core, Span<float> output)
    {
        int sbrChannels = _coreChannels;
        bool process = _active;
        if (process)
        {
            DecodeParameters(sbrChannels);
            process = _active;
        }
        // analysis of the core signal into the slot buffers
        for (int c = 0; c < sbrChannels; c++)
        {
            var ch = _ch[c];
            for (int i = 0; i < 1024; i++) _in[i] = core[i * _coreChannels + c] * ToInternal;
            for (int s = 0; s < Slots; s++)
            {
                ch.Analysis.Slot(_in.AsSpan(s * 32, 32), ch.Re[SbrChannel.Overlap + s], ch.Im[SbrChannel.Overlap + s]);
                Array.Clear(ch.Re[SbrChannel.Overlap + s], 32, SbrChannel.Bands - 32);
                Array.Clear(ch.Im[SbrChannel.Overlap + s], 32, SbrChannel.Bands - 32);
            }
        }
        if (process)
        {
            for (int c = 0; c < sbrChannels; c++)
            {
                var ch = _ch[c];
                ch.Transpose(_bands, _frame[c], _prev[c]);
                ch.Adjust(_bands, _hdr, _frame[c]);
                var p = _prev[c];
                var f = _frame[c];
                Array.Copy(f.InvfMode, p.InvfMode, SbrBands.MaxNoiseCoeffs);
                p.Coupling = f.Coupling;
                p.AmpRes = f.AmpResCurrent;
                p.StopPos = f.Borders[f.NEnvelopes];
            }
        }
        else
        {
            for (int c = 0; c < sbrChannels; c++) _ch[c].SaveLpcState();
        }
        int usb = process ? _bands.HighSubband : 32;
        if (_ps is not null)
        {
            _ps.Apply(_ch[0], _ch[1], process ? _bands.HighSubband : 32);
        }
        int synthBands = _downsampled ? 32 : 64;
        int frame = OutputFrame;
        int oc = OutputChannels;
        for (int c = 0; c < oc; c++)
        {
            var ch = _ch[c];
            for (int s = 0; s < Slots; s++)
            {
                ch.Synthesis.Slot(ch.Re[s], ch.Im[s], _downsampled ? Math.Min(usb, 32) : usb, _slotOut);
                int at = s * synthBands;
                for (int k = 0; k < synthBands; k++) output[(at + k) * oc + c] = _slotOut[k] * FromInternal;
            }
            ch.ShiftOverlap();
        }
        _frameOk = false;
    }

    private void DecodeParameters(int channels)
    {
        // A payload that failed to parse, or a stream whose first envelope after an error is delta coded in time, is
        // replaced by concealment (4.6.18.3.5 leaves this to the decoder).
        bool bad = !_frameOk;
        if (!bad)
        {
            for (int c = 0; c < channels; c++)
            {
                var f = _frame[c];
                if (_needFreq[c] && f.DomainVec[0] == 1) bad = true;
                else if (f.Borders[0] != _prev[c].StopPos - NumTimeSlots && f.DomainVec[0] == 1) bad = true;
            }
        }
        if (!bad)
        {
            bool ok = SbrEnvelope.DecodeEnvelope(_bands, _frame[0], _prev[0], _saved0);
            if (ok && channels == 2) ok = SbrEnvelope.DecodeEnvelope(_bands, _frame[1], _prev[1], _saved1);
            if (!ok)
            {
                SbrEnvelope.RestorePrev(_prev[0], _saved0);
                if (channels == 2) SbrEnvelope.RestorePrev(_prev[1], _saved1);
                bad = true;
            }
        }
        if (bad && Environment.GetEnvironmentVariable("DBG3") is not null) Console.Error.WriteLine($"BAD frameOk={_frameOk} needFreq={_needFreq[0]} dom={_frame[0].DomainVec[0]} b0={_frame[0].Borders[0]} stop={_prev[0].StopPos}");
        if (bad)
        {
            for (int c = 0; c < channels; c++)
            {
                SbrEnvelope.Conceal(_bands, _frame[c], _prev[c], NumTimeSlots);
                SbrEnvelope.DecodeEnvelope(_bands, _frame[c], _prev[c], c == 0 ? _saved0 : _saved1);
                _needFreq[c] = true;
            }
            _ps?.Conceal();
        }
        else
        {
            for (int c = 0; c < channels; c++) _needFreq[c] = false;
        }
        for (int c = 0; c < channels; c++)
        {
            SbrEnvelope.Requantize(_frame[c]);
            SbrEnvelope.DecodeNoise(_bands, _frame[c], _prev[c]);
        }
        if (channels == 2 && _frame[0].Coupling != 0) SbrEnvelope.Unmap(_bands, _frame[0], _frame[1]);
    }
}
