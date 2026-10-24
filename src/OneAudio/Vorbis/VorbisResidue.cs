namespace OneAudio.Vorbis;

/// <summary>A residue configuration and its packet decoder, for types 0, 1 and 2 (Vorbis I specification section 8).</summary>
internal sealed class VorbisResidue
{
    private int _begin;
    private int _end;
    private int _partitionSize;
    private int _classifications;
    private int _classBook;
    private int[][] _books = [];
    private int _lastPass;
    private int[] _classScratch = [];

    public int Type { get; private set; }

    public static VorbisResidue Parse(ref VorbisBitReader r, int type, VorbisCodebook[] books)
    {
        var res = new VorbisResidue { Type = type };
        res._begin = (int)r.Read(24);
        res._end = (int)r.Read(24);
        res._partitionSize = (int)r.Read(24) + 1;
        res._classifications = (int)r.Read(6) + 1;
        res._classBook = (int)r.Read(8);
        if (res._classBook >= books.Length) throw new AudioFormatException("Vorbis residue class book is out of range.");
        var cascade = new int[res._classifications];
        for (int i = 0; i < cascade.Length; i++)
        {
            int low = (int)r.Read(3);
            int high = r.ReadBit() ? (int)r.Read(5) : 0;
            cascade[i] = high * 8 + low;
        }
        res._books = new int[res._classifications][];
        for (int i = 0; i < cascade.Length; i++)
        {
            var b = new int[8];
            for (int j = 0; j < 8; j++)
            {
                if ((cascade[i] & (1 << j)) != 0)
                {
                    b[j] = (int)r.Read(8);
                    if (b[j] >= books.Length) throw new AudioFormatException("Vorbis residue book is out of range.");
                    if (books[b[j]].LookupType == 0) throw new AudioFormatException("Vorbis residue book has no value mapping.");
                    res._lastPass = Math.Max(res._lastPass, j);
                }
                else b[j] = -1;
            }
            res._books[i] = b;
        }
        if (r.EndOfPacket) throw new AudioFormatException("Vorbis residue header truncated.");
        return res;
    }

    /// <summary>Sizes the classification scratch for the longest block and charges it to the setup budget.</summary>
    public void Prepare(VorbisCodebook[] books, AudioLimits limits, ref long budget, int channels, int longBlock)
    {
        long actual = (long)(longBlock / 2) * (Type == 2 ? channels : 1);
        long maxParts = actual / _partitionSize + 1;
        int vectors = Type == 2 ? 1 : channels;
        long ints = vectors * (maxParts + books[_classBook].Dimensions);
        limits.CheckSetupBytes(limits.MaxSetupBytes - budget + 4 * ints);
        budget -= 4 * ints;
        _classScratch = new int[ints];
    }

    /// <summary>
    /// Decodes <paramref name="vectorCount"/> vectors (already zeroed, at least <paramref name="actualSize"/> long) in the
    /// partition layout of this residue's type. For type 2 the caller passes one interleaved vector and deinterleaves afterwards.
    /// An end of packet simply stops decoding (section 8.6.2).
    /// </summary>
    public void Decode(ref VorbisBitReader r, VorbisCodebook[] books, float[][] vectors, int vectorCount, bool[] skip, int actualSize)
    {
        int limitBegin = Math.Min(_begin, actualSize);
        int limitEnd = Math.Min(_end, actualSize);
        int nToRead = limitEnd - limitBegin;
        if (nToRead <= 0) return;
        int partitionSize = _partitionSize;
        int partsToRead = nToRead / partitionSize;
        var classBook = books[_classBook];
        int classWords = classBook.Dimensions;
        int stride = partsToRead + classWords;
        var cls = _classScratch;
        bool interleaved = Type == 0;

        for (int pass = 0; pass <= _lastPass; pass++)
        {
            int pc = 0;
            while (pc < partsToRead)
            {
                if (pass == 0)
                {
                    for (int j = 0; j < vectorCount; j++)
                    {
                        if (skip[j]) continue;
                        int temp = classBook.DecodeScalar(ref r);
                        if (temp < 0) return;
                        for (int i = classWords - 1; i >= 0; i--)
                        {
                            cls[j * stride + i + pc] = temp % _classifications;
                            temp /= _classifications;
                        }
                    }
                }
                for (int i = 0; i < classWords && pc < partsToRead; i++, pc++)
                {
                    for (int j = 0; j < vectorCount; j++)
                    {
                        if (skip[j]) continue;
                        int bookIndex = _books[cls[j * stride + pc]][pass];
                        if (bookIndex < 0) continue;
                        var book = books[bookIndex];
                        int offset = limitBegin + pc * partitionSize;
                        bool ok = interleaved
                            ? DecodeInterleaved(ref r, book, vectors[j], offset, partitionSize)
                            : DecodeSequential(ref r, book, vectors[j], offset, partitionSize);
                        if (!ok) return;
                    }
                }
            }
        }
    }

    /// <summary>Residue 0 (section 8.6.3): consecutive codebook vectors are spread across the partition.</summary>
    private static bool DecodeInterleaved(ref VorbisBitReader r, VorbisCodebook book, float[] v, int offset, int n)
    {
        int dim = book.Dimensions;
        int step = n / dim;
        var vectors = book.Vectors;
        for (int i = 0; i < step; i++)
        {
            int entry = book.DecodeScalar(ref r);
            if (entry < 0) return false;
            int vo = entry * dim;
            int dst = offset + i;
            for (int j = 0; j < dim; j++, dst += step) v[dst] += vectors[vo + j];
        }
        return true;
    }

    /// <summary>Residue 1 (section 8.6.4): codebook vectors are laid end to end.</summary>
    private static bool DecodeSequential(ref VorbisBitReader r, VorbisCodebook book, float[] v, int offset, int n)
    {
        int dim = book.Dimensions;
        var vectors = book.Vectors;
        int i = 0;
        while (i < n)
        {
            int entry = book.DecodeScalar(ref r);
            if (entry < 0) return false;
            int vo = entry * dim;
            int count = Math.Min(dim, n - i);
            var dst = v.AsSpan(offset + i, count);
            var src = vectors.AsSpan(vo, count);
            for (int j = 0; j < dst.Length; j++) dst[j] += src[j];
            i += dim;
        }
        return true;
    }
}
