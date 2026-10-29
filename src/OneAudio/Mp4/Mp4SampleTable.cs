namespace OneAudio.Mp4;

/// <summary>
/// The sample tables of one track (ISO/IEC 14496-12 8.6.1 stts, 8.7.3 stsz, 8.7.4 stsc, 8.7.5 stco/co64) in the compact form
/// they are stored in, with lookups from sample number to file offset, size and time, and from time to sample number.
/// Nothing is expanded per sample beyond the size list, so the memory is bounded by the box sizes in the file.
/// </summary>
internal sealed class Mp4SampleTable
{
    // stts: run k covers samples [_sttsFirst[k], _sttsFirst[k] + count) and starts at time _sttsStart[k].
    private readonly uint[] _sttsCount;
    private readonly uint[] _sttsDelta;
    private readonly long[] _sttsFirst;
    private readonly long[] _sttsStart;
    // stsc: run k covers chunks [_runChunk[k], next run's) with _runPerChunk[k] samples each and starts at sample _runFirst[k].
    private readonly int[] _runChunk;
    private readonly uint[] _runPerChunk;
    private readonly long[] _runFirst;
    private readonly long[] _chunkOffsets;
    private readonly uint[]? _sizes;
    private readonly uint _uniformSize;

    // Sequential-read cursor: the offset of sample _curSample when it follows the previous one inside the same chunk.
    private long _curSample = -1;
    private long _curOffset;

    public Mp4SampleTable(long sampleCount, uint uniformSize, uint[]? sizes, uint[] sttsPairs, uint[] stscTriples, long[] chunkOffsets)
    {
        SampleCount = sampleCount;
        _uniformSize = uniformSize;
        _sizes = sizes;
        _chunkOffsets = chunkOffsets;

        int runs = sttsPairs.Length / 2;
        _sttsCount = new uint[runs];
        _sttsDelta = new uint[runs];
        _sttsFirst = new long[runs];
        _sttsStart = new long[runs];
        long first = 0, start = 0;
        for (int k = 0; k < runs; k++)
        {
            _sttsCount[k] = sttsPairs[2 * k];
            _sttsDelta[k] = sttsPairs[2 * k + 1];
            _sttsFirst[k] = first;
            _sttsStart[k] = start;
            first += _sttsCount[k];
            ulong span = (ulong)_sttsCount[k] * _sttsDelta[k];
            if (span > (ulong)(MaxTicks - start)) throw new AudioFormatException("The time-to-sample table adds up to an impossible duration.");
            start += (long)span;
        }
        if (first < sampleCount) throw new AudioFormatException("The time-to-sample table covers fewer samples than the size table.");
        TotalTicks = 0;
        // Duration of the samples that exist (a time table may list more than the size table does).
        TotalTicks = TimeOf(sampleCount, out _);

        int n = stscTriples.Length / 3;
        _runChunk = new int[n];
        _runPerChunk = new uint[n];
        _runFirst = new long[n];
        long samples = 0;
        long chunks = chunkOffsets.Length;
        for (int k = 0; k < n; k++)
        {
            long firstChunk = stscTriples[3 * k];
            uint perChunk = stscTriples[3 * k + 1];
            if (stscTriples[3 * k + 2] != 1) throw new UnsupportedAudioFormatException("Tracks with more than one sample description are not supported.");
            if (perChunk == 0) throw new AudioFormatException("A sample-to-chunk entry has zero samples per chunk.");
            if (firstChunk < 1 || firstChunk > chunks) throw new AudioFormatException("A sample-to-chunk entry names a chunk that does not exist.");
            if (k == 0 ? firstChunk != 1 : firstChunk <= _runChunk[k - 1] + 1) throw new AudioFormatException("The sample-to-chunk table is not ascending.");
            _runChunk[k] = (int)(firstChunk - 1);
            _runPerChunk[k] = perChunk;
            if (k > 0) samples += (long)(_runChunk[k] - _runChunk[k - 1]) * _runPerChunk[k - 1];
            _runFirst[k] = samples;
        }
        if (n > 0) samples += (chunks - _runChunk[n - 1]) * _runPerChunk[n - 1];
        if (samples < sampleCount) throw new AudioFormatException("The chunk tables describe fewer samples than the size table.");
    }

    private const long MaxTicks = 1L << 62;

    public long SampleCount { get; }

    /// <summary>Sum of all sample durations in media time units.</summary>
    public long TotalTicks { get; }

    /// <summary>Largest sample size in bytes (the uniform size when there is no table).</summary>
    public uint MaxSampleSize
    {
        get
        {
            if (_sizes is null) return _uniformSize;
            uint max = 0;
            foreach (uint s in _sizes) if (s > max) max = s;
            return max;
        }
    }

    public uint SizeOf(long sample) => _sizes is null ? _uniformSize : _sizes[sample];

    /// <summary>Decode time of <paramref name="sample"/> in media time units; <paramref name="duration"/> is its length.</summary>
    public long TimeOf(long sample, out uint duration)
    {
        if (_sttsFirst.Length == 0) { duration = 0; return 0; }
        int k = Floor(_sttsFirst, sample);
        duration = _sttsDelta[k];
        return _sttsStart[k] + (sample - _sttsFirst[k]) * _sttsDelta[k];
    }

    /// <summary>The sample whose time span contains <paramref name="ticks"/>; <see cref="SampleCount"/> when the time is past the end.</summary>
    public long SampleAt(long ticks)
    {
        if (ticks < 0) ticks = 0;
        if (ticks >= TotalTicks || _sttsFirst.Length == 0) return SampleCount;
        int k = Floor(_sttsStart, ticks);
        // Runs that share a start time (zero deltas) end at the last such run; Floor already picks the last one.
        long delta = _sttsDelta[k];
        long index = delta == 0 ? _sttsFirst[k] : _sttsFirst[k] + (ticks - _sttsStart[k]) / delta;
        long last = _sttsFirst[k] + _sttsCount[k] - 1;
        return Math.Min(Math.Min(index, last), SampleCount - 1);
    }

    /// <summary>File offset of <paramref name="sample"/>. Sequential calls cost O(1).</summary>
    public long OffsetOf(long sample)
    {
        if (sample == _curSample) return _curOffset;
        int k = Floor(_runFirst, sample);
        long within = sample - _runFirst[k];
        long perChunk = _runPerChunk[k];
        long chunk = _runChunk[k] + within / perChunk;
        long indexInChunk = within % perChunk;
        long offset = _chunkOffsets[chunk];
        if (_sizes is null) offset += indexInChunk * _uniformSize;
        else for (long i = sample - indexInChunk; i < sample; i++) offset += _sizes[i];
        return offset;
    }

    /// <summary>Records that <paramref name="sample"/> was just read, so the next one can be found without searching.</summary>
    public void Advance(long sample, long offset)
    {
        long next = sample + 1;
        int k = Floor(_runFirst, sample);
        long within = sample - _runFirst[k];
        bool lastInChunk = (within + 1) % _runPerChunk[k] == 0;
        if (lastInChunk || next >= SampleCount) { _curSample = -1; return; }
        _curSample = next;
        _curOffset = offset + SizeOf(sample);
    }

    /// <summary>Forgets the cursor (after a seek the next offset is looked up again).</summary>
    public void ResetCursor() => _curSample = -1;

    // Index of the last element that is <= value (0 if all are greater; arrays always start at 0 for in-range queries).
    private static int Floor(long[] keys, long value)
    {
        int lo = 0, hi = keys.Length - 1;
        while (lo < hi)
        {
            int mid = (int)(((long)lo + hi + 1) >> 1);
            if (keys[mid] <= value) lo = mid; else hi = mid - 1;
        }
        return lo;
    }
}
