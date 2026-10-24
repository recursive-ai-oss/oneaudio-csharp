namespace OneAudio.Vorbis;

/// <summary>Per-channel scratch for floor decode, allocated once per decoder.</summary>
internal sealed class FloorChannel
{
    public readonly int[] Y = new int[65];
    public readonly int[] FinalY = new int[65];
    public readonly bool[] Step2 = new bool[65];
    public float[] Coefficients = new float[256];
    public int Amplitude;
    public double[] Cos = new double[256];
    /// <summary>Integer floor curve scratch for floor 1, one entry per spectral line of the longest block.</summary>
    public int[] Curve = [];
}

/// <summary>A floor configuration (Vorbis I specification sections 6 and 7).</summary>
internal abstract class VorbisFloor
{
    /// <summary>Decodes the floor from the packet. False means the channel is unused in this packet.</summary>
    public abstract bool Decode(ref VorbisBitReader r, VorbisCodebook[] books, FloorChannel ch);

    /// <summary>Writes the linear-amplitude floor curve of <c>output.Length</c> lines for a decoded channel.</summary>
    public abstract void Synthesize(FloorChannel ch, int blockIndex, Span<float> output);

    /// <summary>Called once per block size when the setup is complete.</summary>
    public virtual void Prepare(int blockIndex, int n2) { }
}

/// <summary>Floor type 1: a piecewise linear curve on a dB scale (section 7).</summary>
internal sealed class VorbisFloor1 : VorbisFloor
{
    private static readonly int[] Ranges = [256, 128, 86, 64];

    private int[] _partitionClass = [];
    private int[] _classDimensions = [];
    private int[] _classSubclasses = [];
    private int[] _classMasterBook = [];
    private int[][] _subclassBooks = [];
    private int _multiplier;
    private int[] _x = [];
    private int _values;
    private int[] _sorted = [];
    private int[] _low = [];
    private int[] _high = [];
    private int _range;

    public static VorbisFloor1 Parse(ref VorbisBitReader r, int bookCount)
    {
        var f = new VorbisFloor1();
        int partitions = (int)r.Read(5);
        f._partitionClass = new int[partitions];
        int maxClass = -1;
        for (int i = 0; i < partitions; i++)
        {
            f._partitionClass[i] = (int)r.Read(4);
            if (f._partitionClass[i] > maxClass) maxClass = f._partitionClass[i];
        }
        int classes = maxClass + 1;
        f._classDimensions = new int[classes];
        f._classSubclasses = new int[classes];
        f._classMasterBook = new int[classes];
        f._subclassBooks = new int[classes][];
        for (int i = 0; i < classes; i++)
        {
            f._classDimensions[i] = (int)r.Read(3) + 1;
            int sub = (int)r.Read(2);
            f._classSubclasses[i] = sub;
            if (sub != 0)
            {
                f._classMasterBook[i] = (int)r.Read(8);
                if (f._classMasterBook[i] >= bookCount) throw new AudioFormatException("Vorbis floor 1 master book is out of range.");
            }
            var books = new int[1 << sub];
            for (int j = 0; j < books.Length; j++)
            {
                books[j] = (int)r.Read(8) - 1;
                if (books[j] >= bookCount) throw new AudioFormatException("Vorbis floor 1 subclass book is out of range.");
            }
            f._subclassBooks[i] = books;
        }
        f._multiplier = (int)r.Read(2) + 1;
        int rangeBits = (int)r.Read(4);
        var x = new int[65];
        x[0] = 0;
        x[1] = 1 << rangeBits;
        int values = 2;
        for (int i = 0; i < partitions; i++)
        {
            int dim = f._classDimensions[f._partitionClass[i]];
            for (int j = 0; j < dim; j++)
            {
                if (values >= 65) throw new AudioFormatException("Vorbis floor 1 has more than 65 points.");
                x[values++] = (int)r.Read(rangeBits);
            }
        }
        if (r.EndOfPacket) throw new AudioFormatException("Vorbis floor 1 header truncated.");
        f._values = values;
        f._x = x[..values];
        for (int i = 0; i < values; i++)
            for (int j = i + 1; j < values; j++)
                if (f._x[i] == f._x[j]) throw new AudioFormatException("Vorbis floor 1 has duplicate X positions.");

        f._range = Ranges[f._multiplier - 1];
        f._sorted = new int[values];
        for (int i = 0; i < values; i++) f._sorted[i] = i;
        Array.Sort(f._sorted, (a, b) => f._x[a].CompareTo(f._x[b]));

        // low_neighbor and high_neighbor (sections 9.2.4, 9.2.5) over the unsorted list.
        f._low = new int[values];
        f._high = new int[values];
        for (int i = 2; i < values; i++)
        {
            int lo = 0, hi = 1;
            for (int n = 0; n < i; n++)
            {
                if (f._x[n] < f._x[i] && f._x[n] > f._x[lo]) lo = n;
                if (f._x[n] > f._x[i] && f._x[n] < f._x[hi]) hi = n;
            }
            f._low[i] = lo;
            f._high[i] = hi;
        }
        return f;
    }

    public override bool Decode(ref VorbisBitReader r, VorbisCodebook[] books, FloorChannel ch)
    {
        if (!r.ReadBit()) return false;
        var y = ch.Y;
        int bits = VorbisCodebook.ILog(_range - 1);
        y[0] = (int)r.Read(bits);
        y[1] = (int)r.Read(bits);
        int offset = 2;
        for (int i = 0; i < _partitionClass.Length; i++)
        {
            int cls = _partitionClass[i];
            int cdim = _classDimensions[cls];
            int cbits = _classSubclasses[cls];
            int csub = (1 << cbits) - 1;
            int cval = 0;
            if (cbits > 0)
            {
                cval = books[_classMasterBook[cls]].DecodeScalar(ref r);
                if (cval < 0) return false;
            }
            var sb = _subclassBooks[cls];
            for (int j = 0; j < cdim; j++)
            {
                int book = sb[cval & csub];
                cval >>= cbits;
                if (book >= 0)
                {
                    int v = books[book].DecodeScalar(ref r);
                    if (v < 0) return false;
                    y[offset + j] = v;
                }
                else
                {
                    y[offset + j] = 0;
                }
            }
            offset += cdim;
        }
        if (r.EndOfPacket) return false;

        // Step 1: amplitude value synthesis (section 7.2.4).
        var fy = ch.FinalY;
        var step2 = ch.Step2;
        step2[0] = step2[1] = true;
        int range = _range;
        fy[0] = Math.Min(y[0], range - 1);
        fy[1] = Math.Min(y[1], range - 1);
        for (int i = 2; i < _values; i++)
        {
            int lo = _low[i], hi = _high[i];
            int predicted = RenderPoint(_x[lo], fy[lo], _x[hi], fy[hi], _x[i]);
            int val = y[i];
            int highroom = range - predicted;
            int lowroom = predicted;
            int room = (highroom < lowroom ? highroom : lowroom) * 2;
            if (val != 0)
            {
                step2[lo] = step2[hi] = step2[i] = true;
                if (val >= room)
                    fy[i] = highroom > lowroom ? val - lowroom + predicted : predicted - val + highroom - 1;
                else
                    fy[i] = (val & 1) != 0 ? predicted - ((val + 1) >> 1) : predicted + (val >> 1);
            }
            else
            {
                step2[i] = false;
                fy[i] = predicted;
            }
            // Valid streams cannot leave [0, range); abusive ones are clamped as the specification advises.
            if (fy[i] < 0) fy[i] = 0; else if (fy[i] >= range) fy[i] = range - 1;
        }
        return true;
    }

    public override void Synthesize(FloorChannel ch, int blockIndex, Span<float> output)
    {
        int n = output.Length;
        var curve = ch.Curve;
        var fy = ch.FinalY;
        var step2 = ch.Step2;
        int mult = _multiplier;
        var sorted = _sorted;

        int hx = 0, lx = 0;
        int ly = fy[sorted[0]] * mult;
        int hy = ly;
        for (int i = 1; i < _values; i++)
        {
            int idx = sorted[i];
            if (!step2[idx]) continue;
            hy = fy[idx] * mult;
            hx = _x[idx];
            RenderLine(lx, ly, hx, hy, curve, n);
            lx = hx;
            ly = hy;
        }
        if (hx < n) RenderLine(hx, hy, n, hy, curve, n);

        var table = VorbisTables.InverseDb;
        for (int i = 0; i < n; i++)
        {
            int v = curve[i];
            output[i] = table[v > 255 ? 255 : v];
        }
    }

    private static int RenderPoint(int x0, int y0, int x1, int y1, int x)
    {
        int dy = y1 - y0;
        int adx = x1 - x0;
        int ady = Math.Abs(dy);
        int err = ady * (x - x0);
        int off = err / adx;
        return dy < 0 ? y0 - off : y0 + off;
    }

    private static void RenderLine(int x0, int y0, int x1, int y1, int[] v, int n)
    {
        int dy = y1 - y0;
        int adx = x1 - x0;
        int ady = Math.Abs(dy);
        int bas = dy / adx;
        int x = x0;
        int y = y0;
        int err = 0;
        int sy = dy < 0 ? bas - 1 : bas + 1;
        ady -= Math.Abs(bas) * adx;
        if (x < n) v[x] = y;
        for (x = x0 + 1; x < x1; x++)
        {
            err += ady;
            if (err >= adx) { err -= adx; y += sy; }
            else y += bas;
            if (x >= n) return;
            v[x] = y;
        }
    }
}

/// <summary>Floor type 0: a line spectral pair envelope on a Bark axis (section 6).</summary>
internal sealed class VorbisFloor0 : VorbisFloor
{
    private int _order;
    private int _rate;
    private int _barkMapSize;
    private int _amplitudeBits;
    private int _amplitudeOffset;
    private int[] _bookList = [];
    private readonly int[][] _map = new int[2][];

    public static VorbisFloor0 Parse(ref VorbisBitReader r, VorbisCodebook[] books)
    {
        var f = new VorbisFloor0();
        f._order = (int)r.Read(8);
        f._rate = (int)r.Read(16);
        f._barkMapSize = (int)r.Read(16);
        f._amplitudeBits = (int)r.Read(6);
        f._amplitudeOffset = (int)r.Read(8);
        int nbooks = (int)r.Read(4) + 1;
        f._bookList = new int[nbooks];
        for (int i = 0; i < nbooks; i++)
        {
            f._bookList[i] = (int)r.Read(8);
            if (f._bookList[i] >= books.Length) throw new AudioFormatException("Vorbis floor 0 book is out of range.");
            if (books[f._bookList[i]].LookupType == 0) throw new AudioFormatException("Vorbis floor 0 book has no value mapping.");
        }
        if (r.EndOfPacket) throw new AudioFormatException("Vorbis floor 0 header truncated.");
        if (f._order == 0 || f._rate == 0 || f._barkMapSize == 0) throw new AudioFormatException("Vorbis floor 0 parameters are invalid.");
        if (f._amplitudeBits > 32) throw new UnsupportedAudioFormatException("Vorbis floor 0 amplitude wider than 32 bits is not supported.");
        return f;
    }

    private static double Bark(double x) => 13.1 * Math.Atan(0.00074 * x) + 2.24 * Math.Atan(0.0000000185 * x * x) + 0.0001 * x;

    public override void Prepare(int blockIndex, int n2)
    {
        var map = new int[n2 + 1];
        double scale = _barkMapSize / Bark(0.5 * _rate);
        for (int i = 0; i < n2; i++)
        {
            int foobar = (int)Math.Floor(Bark((double)_rate * i / (2.0 * n2)) * scale);
            map[i] = Math.Min(_barkMapSize - 1, foobar);
        }
        map[n2] = -1;
        _map[blockIndex] = map;
    }

    public override bool Decode(ref VorbisBitReader r, VorbisCodebook[] books, FloorChannel ch)
    {
        int amplitude = (int)r.Read(_amplitudeBits);
        if (r.EndOfPacket || amplitude <= 0) return false;
        ch.Amplitude = amplitude;
        if (ch.Coefficients.Length < _order + 1) ch.Coefficients = new float[_order + 1];
        if (ch.Cos.Length < _order + 1) ch.Cos = new double[_order + 1];
        int booknumber = (int)r.Read(VorbisCodebook.ILog(_bookList.Length));
        if (r.EndOfPacket || booknumber >= _bookList.Length) return false;
        var book = books[_bookList[booknumber]];
        int dims = book.Dimensions;
        int count = 0;
        float last = 0;
        while (count < _order)
        {
            int entry = book.DecodeScalar(ref r);
            if (entry < 0) return false;
            var vec = book.Vectors.AsSpan(entry * dims, dims);
            float lastOfVector = vec[dims - 1] + last;
            for (int j = 0; j < dims && count < _order; j++) ch.Coefficients[count++] = vec[j] + last;
            last = lastOfVector;
        }
        return true;
    }

    public override void Synthesize(FloorChannel ch, int blockIndex, Span<float> output)
    {
        int n = output.Length;
        var map = _map[blockIndex];
        int order = _order;
        var cosC = ch.Cos;
        for (int j = 0; j < order; j++) cosC[j] = Math.Cos(ch.Coefficients[j]);
        double ampScale = (double)ch.Amplitude * _amplitudeOffset / (Math.Pow(2, _amplitudeBits) - 1);
        int i = 0;
        while (i < n)
        {
            int cond = map[i];
            double omega = Math.PI * cond / _barkMapSize;
            double cw = Math.Cos(omega);
            double p, q;
            if ((order & 1) != 0)
            {
                p = 1 - cw * cw;
                for (int j = 0; j <= (order - 3) / 2; j++) { double t = cosC[2 * j + 1] - cw; p *= 4 * t * t; }
                q = 0.25;
                for (int j = 0; j <= (order - 1) / 2; j++) { double t = cosC[2 * j] - cw; q *= 4 * t * t; }
            }
            else
            {
                p = (1 - cw) / 2;
                q = (1 + cw) / 2;
                for (int j = 0; j <= (order - 2) / 2; j++)
                {
                    double tp = cosC[2 * j + 1] - cw; p *= 4 * tp * tp;
                    double tq = cosC[2 * j] - cw; q *= 4 * tq * tq;
                }
            }
            float value = (float)Math.Exp(0.11512925 * (ampScale / Math.Sqrt(p + q) - _amplitudeOffset));
            output[i++] = value;
            while (i < n && map[i] == cond) output[i++] = value;
        }
    }
}
