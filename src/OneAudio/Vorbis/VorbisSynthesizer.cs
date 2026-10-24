namespace OneAudio.Vorbis;

/// <summary>
/// Turns audio packets into PCM for one set of Vorbis headers (Vorbis I specification section 4.3): mode and window
/// selection, floors, residue, inverse coupling, dot product, inverse MDCT and overlap-add. All working memory is
/// allocated here, once, for the longest block, so decoding a packet allocates nothing.
/// </summary>
internal sealed class VorbisSynthesizer
{
    private readonly VorbisSetup _s;
    private readonly int _ch;
    private readonly VorbisImdct[] _imdct = new VorbisImdct[2];
    private readonly float[][] _windows = new float[5][]; // short, then long indexed by 1 + prev*2 + next
    private readonly float[][] _lap;
    private readonly float[][] _spec;
    private readonly float[][] _floorBuf;
    private readonly float[] _y;
    private readonly FloorChannel[] _fc;
    private readonly bool[] _floorUsed;
    private readonly bool[] _noResidue;
    private readonly bool[] _skip;
    private readonly float[][] _bundle;
    private readonly float[][] _interleaved;
    private readonly bool[] _skipOne = [false];
    private int _prevN;

    /// <summary>Decoded planar output of the last packet: <see cref="OutLength"/> valid frames per channel, in stream channel order.</summary>
    public float[][] Pcm { get; }

    /// <summary>Frames of finished audio the last <see cref="Decode"/> produced (0 for the first packet after a reset).</summary>
    public int OutLength { get; private set; }

    public VorbisSynthesizer(VorbisSetup setup)
    {
        _s = setup;
        _ch = setup.Channels;
        int n0 = setup.BlockSize0, n1 = setup.BlockSize1;
        _imdct[0] = new VorbisImdct(n0);
        _imdct[1] = n1 == n0 ? _imdct[0] : new VorbisImdct(n1);
        _windows[0] = BuildWindow(n0, false, true, true);
        for (int p = 0; p < 2; p++)
            for (int q = 0; q < 2; q++)
                _windows[1 + p * 2 + q] = BuildWindow(n1, true, p != 0, q != 0);

        int half = n1 / 2;
        Pcm = NewPlanes(_ch, half);
        _lap = NewPlanes(_ch, half);
        _spec = NewPlanes(_ch, half);
        _floorBuf = NewPlanes(_ch, half);
        _y = new float[n1];
        _fc = new FloorChannel[_ch];
        for (int i = 0; i < _ch; i++) _fc[i] = new FloorChannel { Curve = new int[half] };
        _floorUsed = new bool[_ch];
        _noResidue = new bool[_ch];
        _skip = new bool[_ch];
        _bundle = new float[_ch][];
        _interleaved = [new float[half * _ch]];
    }

    private static float[][] NewPlanes(int ch, int len)
    {
        var p = new float[ch][];
        for (int i = 0; i < ch; i++) p[i] = new float[len];
        return p;
    }

    /// <summary>Forgets the overlap state: the next packet only primes the transform and yields no audio.</summary>
    public void Reset()
    {
        _prevN = 0;
        OutLength = 0;
    }

    /// <summary>Window shape of section 4.3.1.</summary>
    private float[] BuildWindow(int n, bool longBlock, bool prevLong, bool nextLong)
    {
        int b0 = _s.BlockSize0;
        int center = n / 2;
        int leftStart, leftEnd, leftN, rightStart, rightEnd, rightN;
        if (longBlock && !prevLong) { leftStart = n / 4 - b0 / 4; leftEnd = n / 4 + b0 / 4; leftN = b0 / 2; }
        else { leftStart = 0; leftEnd = center; leftN = n / 2; }
        if (longBlock && !nextLong) { rightStart = n * 3 / 4 - b0 / 4; rightEnd = n * 3 / 4 + b0 / 4; rightN = b0 / 2; }
        else { rightStart = center; rightEnd = n; rightN = n / 2; }
        var w = new float[n];
        for (int i = leftStart; i < leftEnd; i++)
        {
            double s = Math.Sin((i - leftStart + 0.5) / leftN * Math.PI / 2);
            w[i] = (float)Math.Sin(Math.PI / 2 * s * s);
        }
        for (int i = leftEnd; i < rightStart; i++) w[i] = 1f;
        for (int i = rightStart; i < rightEnd; i++)
        {
            double s = Math.Sin((i - rightStart + 0.5) / rightN * Math.PI / 2 + Math.PI / 2);
            w[i] = (float)Math.Sin(Math.PI / 2 * s * s);
        }
        return w;
    }

    /// <summary>
    /// Decodes one audio packet. Returns false when the packet is not decodable audio (a header-type packet, a
    /// reserved mode, or a packet that ends before its mode and window flags); such a packet changes nothing.
    /// </summary>
    public bool Decode(ReadOnlySpan<byte> packet)
    {
        var r = new VorbisBitReader(packet);
        if (r.ReadBit()) return false;
        var modes = _s.Modes;
        int mode = (int)r.Read(VorbisCodebook.ILog(modes.Length - 1));
        if (r.EndOfPacket || mode >= modes.Length) return false;
        var md = modes[mode];
        int n = _s.BlockSizeOf(md);
        bool prevFlag = true, nextFlag = true;
        if (md.LongBlock)
        {
            prevFlag = r.ReadBit();
            nextFlag = r.ReadBit();
        }
        if (r.EndOfPacket) return false;

        int n2 = n / 2;
        int blockIndex = md.LongBlock ? 1 : 0;
        var mapping = _s.Mappings[md.Mapping];
        int ch = _ch;

        for (int i = 0; i < ch; i++)
        {
            var floor = _s.Floors[mapping.SubmapFloor[mapping.Mux[i]]];
            _floorUsed[i] = floor.Decode(ref r, _s.Books, _fc[i]);
            _noResidue[i] = !_floorUsed[i];
        }
        // End of packet inside the floors zeroes the whole block (section 4.3.2).
        bool zeroAll = r.EndOfPacket;

        if (!zeroAll)
        {
            for (int i = 0; i < ch; i++) Array.Clear(_spec[i], 0, n2);
            for (int s = 0; s < mapping.Magnitude.Length; s++)
            {
                int m = mapping.Magnitude[s], a = mapping.Angle[s];
                if (!_noResidue[m] || !_noResidue[a]) { _noResidue[m] = false; _noResidue[a] = false; }
            }
            for (int sm = 0; sm < mapping.Submaps; sm++)
            {
                int count = 0;
                bool allSkip = true;
                for (int j = 0; j < ch; j++)
                {
                    if (mapping.Mux[j] != sm) continue;
                    _skip[count] = _noResidue[j];
                    if (!_noResidue[j]) allSkip = false;
                    _bundle[count] = _spec[j];
                    count++;
                }
                if (count == 0) continue;
                var residue = _s.Residues[mapping.SubmapResidue[sm]];
                if (residue.Type == 2)
                {
                    if (allSkip) continue;
                    var flat = _interleaved[0];
                    Array.Clear(flat, 0, n2 * count);
                    residue.Decode(ref r, _s.Books, _interleaved, 1, _skipOne, n2 * count);
                    for (int i = 0; i < n2; i++)
                        for (int j = 0; j < count; j++)
                            _bundle[j][i] = flat[i * count + j];
                }
                else
                {
                    residue.Decode(ref r, _s.Books, _bundle, count, _skip, n2);
                }
            }

            // Inverse channel coupling (section 4.3.5), last step first.
            for (int s = mapping.Magnitude.Length - 1; s >= 0; s--)
            {
                var mv = _spec[mapping.Magnitude[s]];
                var av = _spec[mapping.Angle[s]];
                for (int i = 0; i < n2; i++)
                {
                    float M = mv[i], A = av[i];
                    float newM, newA;
                    if (M > 0)
                    {
                        if (A > 0) { newM = M; newA = M - A; }
                        else { newA = M; newM = M + A; }
                    }
                    else
                    {
                        if (A > 0) { newM = M; newA = M + A; }
                        else { newA = M; newM = M - A; }
                    }
                    mv[i] = newM;
                    av[i] = newA;
                }
            }

            // Dot product of floor and residue (section 4.3.6). A channel whose floor was unused stays silent.
            for (int i = 0; i < ch; i++)
            {
                if (!_floorUsed[i]) { Array.Clear(_spec[i], 0, n2); continue; }
                var fl = _floorBuf[i];
                _s.Floors[mapping.SubmapFloor[mapping.Mux[i]]].Synthesize(_fc[i], blockIndex, fl.AsSpan(0, n2));
                var sp = _spec[i];
                for (int k = 0; k < n2; k++) sp[k] *= fl[k];
            }
        }
        else
        {
            for (int i = 0; i < ch; i++) { Array.Clear(_spec[i], 0, n2); _floorUsed[i] = false; }
        }

        // Inverse MDCT, windowing and overlap-add (sections 4.3.7, 4.3.8). The previous block's second half and
        // this block's first half overlap so that the previous 3/4 point meets this 1/4 point.
        var window = md.LongBlock ? _windows[1 + (prevFlag ? 2 : 0) + (nextFlag ? 1 : 0)] : _windows[0];
        var mdct = _imdct[blockIndex];
        int prevN = _prevN;
        int outLen = prevN == 0 ? 0 : prevN / 4 + n / 4;
        int offset = prevN == 0 ? 0 : n / 4 - prevN / 4;
        for (int c = 0; c < ch; c++)
        {
            var y = _y;
            if (_floorUsed[c]) mdct.Compute(_spec[c].AsSpan(0, n2), y.AsSpan(0, n));
            else Array.Clear(y, 0, n);
            var lap = _lap[c];
            var pcm = Pcm[c];
            if (prevN != 0)
            {
                int lapLen = prevN / 2;
                for (int i = 0; i < outLen; i++)
                {
                    float v = i < lapLen ? lap[i] : 0f;
                    int idx = i + offset;
                    if (idx >= 0) v += y[idx] * window[idx];
                    pcm[i] = v;
                }
            }
            for (int i = 0; i < n2; i++) lap[i] = y[n2 + i] * window[n2 + i];
        }
        _prevN = n;
        OutLength = outLen;
        return true;
    }
}
