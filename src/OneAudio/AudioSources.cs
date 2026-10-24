namespace OneAudio;

/// <summary>
/// Something a mixer voice can play: a fully decoded <see cref="AudioClip"/> or a <see cref="StreamingSource"/> fed
/// by a decode thread. Sources must have 1 or 2 channels and the mixer's sample rate.
/// </summary>
public abstract class AudioSource
{
    private protected AudioSource(AudioFormat format) => Format = format;

    /// <summary>Format of the frames the source delivers to the mixer.</summary>
    public AudioFormat Format { get; }

    // Render-thread read. Must not allocate or block. 'position' belongs to the voice and is used by random-access sources.
    // Returns frames written; fewer than requested means the source ended unless 'underrun' is set.
    internal abstract int ReadFrames(ref long position, bool loop, long loopStart, long loopEnd, Span<float> destination, out bool underrun);
}

/// <summary>
/// Fully decoded interleaved PCM held in memory, the right choice for short effects. A clip is immutable and may be
/// played by any number of voices at once.
/// </summary>
public sealed class AudioClip : AudioSource
{
    private readonly float[] _samples;

    /// <summary>Wraps decoded samples without copying; the caller must not modify the array afterwards.</summary>
    /// <param name="format">Sample rate and channel count of <paramref name="samples"/>.</param>
    /// <param name="samples">Interleaved samples; its length must be a multiple of the channel count.</param>
    /// <exception cref="ArgumentException">The length is not a whole number of frames.</exception>
    public AudioClip(AudioFormat format, float[] samples) : base(format)
    {
        ArgumentNullException.ThrowIfNull(samples);
        if (format.Channels < 1 || samples.Length % format.Channels != 0)
            throw new ArgumentException("Sample count must be a whole number of frames.", nameof(samples));
        _samples = samples;
    }

    /// <summary>Length in frames.</summary>
    public long Frames => _samples.Length / Format.Channels;

    /// <summary>
    /// Decodes <paramref name="decoder"/> from its current position to the end into a new clip. Allocates; call it
    /// on a loading thread, never from the render path.
    /// </summary>
    /// <param name="decoder">Source of audio. Not disposed.</param>
    /// <param name="maxFrames">Refuse sources longer than this many frames (default about 16 minutes at 48 kHz).</param>
    /// <param name="cancellationToken">Checked between blocks.</param>
    /// <exception cref="AudioLimitExceededException">The audio is longer than <paramref name="maxFrames"/>.</exception>
    public static AudioClip FromDecoder(IAudioDecoder decoder, long maxFrames = 48_000L * 60 * 16, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(decoder);
        var format = decoder.Format;
        int ch = format.Channels;
        long hardMax = Math.Min(maxFrames, Array.MaxLength / ch);
        long declared = decoder.LengthFrames;
        if (declared > hardMax) throw new AudioLimitExceededException(nameof(maxFrames), hardMax, declared);

        long cap = declared >= 0 ? declared : Math.Min(65536, hardMax);
        var buf = new float[cap * ch];
        long frames = 0;
        while (true)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (frames == cap)
            {
                if (cap >= hardMax)
                {
                    // At the ceiling: any further frame is an overflow of the limit.
                    if (decoder.Read(new float[ch]) == 0) break;
                    throw new AudioLimitExceededException(nameof(maxFrames), hardMax, frames + 1);
                }
                long next = Math.Min(Math.Max(cap, 1) * 2, hardMax);
                Array.Resize(ref buf, (int)(next * ch));
                cap = next;
            }
            int n = (int)Math.Min(cap - frames, 16384);
            int got = decoder.Read(buf.AsSpan((int)(frames * ch), n * ch));
            if (got == 0) break;
            frames += got;
        }
        if (frames * ch != buf.Length) Array.Resize(ref buf, (int)(frames * ch));
        return new AudioClip(format, buf);
    }

    internal override int ReadFrames(ref long position, bool loop, long loopStart, long loopEnd, Span<float> destination, out bool underrun)
    {
        underrun = false;
        int ch = Format.Channels;
        long len = Frames;
        long limit = loop ? (loopEnd < 0 || loopEnd > len ? len : loopEnd) : len;
        long ls = loopStart < 0 || loopStart >= limit ? 0 : loopStart;
        int want = destination.Length / ch;
        int written = 0;
        while (written < want)
        {
            if (position >= limit)
            {
                if (!loop || limit <= ls) break;
                position = ls;
            }
            int n = (int)Math.Min(want - written, limit - position);
            _samples.AsSpan((int)(position * ch), n * ch).CopyTo(destination[(written * ch)..]);
            position += n;
            written += n;
        }
        return written;
    }
}

/// <summary>
/// A long sound decoded ahead of time on a decode thread. The decode thread calls <see cref="Pump"/> in a loop (for
/// example every few milliseconds, or whenever <see cref="BufferedFrames"/> drops under half of
/// <see cref="CapacityFrames"/>); the mixer's render thread only drains the preallocated ring buffer. If the ring
/// runs dry before the decoder has finished, the voice renders silence for the missing frames and the shortfall is
/// counted in <see cref="UnderrunEvents"/> and <see cref="UnderrunFrames"/>, readable from any thread.
/// <para>
/// One source feeds one voice at a time. Exactly one thread may call <see cref="Pump"/>; the decoder is touched only
/// by that thread. Looping is done by seeking the decoder to frame 0 when it ends, so it needs a seekable decoder.
/// The voice-level loop settings do not apply to streaming sources.
/// </para>
/// </summary>
public sealed class StreamingSource : AudioSource, IDisposable
{
    private readonly IAudioDecoder _decoder;
    private readonly bool _loop;
    private readonly float[] _ring;
    private readonly float[] _chunk;
    private readonly int _ch;
    private long _head; // samples consumed; written by the render thread
    private long _tail; // samples produced; written by the decode thread
    private int _done;  // 1 once the decoder has no more data
    private long _underrunEvents;
    private long _underrunFrames;

    /// <summary>Creates a streaming source. The ring is allocated here, once.</summary>
    /// <param name="decoder">Decoder to read from. The source owns it and disposes it with <see cref="Dispose"/>.</param>
    /// <param name="capacityFrames">Ring size in frames (default 32768, about 0.7 s at 48 kHz, minimum 64). Larger tolerates longer decode-thread stalls.</param>
    /// <param name="loop">Restart from frame 0 when the decoder ends, forever.</param>
    /// <exception cref="ArgumentException">The decoder has more than 2 channels, or <paramref name="loop"/> is set and it cannot seek.</exception>
    public StreamingSource(IAudioDecoder decoder, int capacityFrames = 32768, bool loop = false) : base(Validate(decoder, capacityFrames, loop))
    {
        _decoder = decoder;
        _loop = loop;
        _ch = decoder.Format.Channels;
        _ring = new float[capacityFrames * _ch];
        _chunk = new float[Math.Min(capacityFrames, 4096) * _ch];
    }

    private static AudioFormat Validate(IAudioDecoder decoder, int capacityFrames, bool loop)
    {
        ArgumentNullException.ThrowIfNull(decoder);
        ArgumentOutOfRangeException.ThrowIfLessThan(capacityFrames, 64);
        if (decoder.Format.Channels > 2) throw new ArgumentException("Streaming sources must be mono or stereo.", nameof(decoder));
        if (loop && !decoder.CanSeek) throw new ArgumentException("Looping needs a seekable decoder.", nameof(decoder));
        return decoder.Format;
    }

    /// <summary>Ring size in frames.</summary>
    public int CapacityFrames => _ring.Length / _ch;

    /// <summary>Frames decoded and waiting to be rendered.</summary>
    public int BufferedFrames => (int)((Volatile.Read(ref _tail) - Volatile.Read(ref _head)) / _ch);

    /// <summary>True once the decoder is exhausted (and not looping) and every buffered frame has been rendered.</summary>
    public bool Ended => Volatile.Read(ref _done) == 1 && Volatile.Read(ref _tail) == Volatile.Read(ref _head);

    /// <summary>Number of render callbacks that found the ring short while the decoder still had data. Thread-safe to read.</summary>
    public long UnderrunEvents => Volatile.Read(ref _underrunEvents);

    /// <summary>Total frames of silence rendered in place of missing data. Thread-safe to read.</summary>
    public long UnderrunFrames => Volatile.Read(ref _underrunFrames);

    /// <summary>
    /// Decodes into free ring space until the ring is full or the decoder ends. Call only from the decode thread.
    /// May block on I/O inside the decoder, which is why it never runs on the render thread.
    /// </summary>
    /// <returns>Frames added to the ring.</returns>
    public int Pump()
    {
        int added = 0;
        bool justSought = false;
        while (Volatile.Read(ref _done) == 0)
        {
            long tail = _tail;
            int free = (int)((_ring.Length - (tail - Volatile.Read(ref _head))) / _ch);
            if (free <= 0) break;
            int want = Math.Min(free, _chunk.Length / _ch);
            int got = _decoder.Read(_chunk.AsSpan(0, want * _ch));
            if (got == 0)
            {
                // Seek once; if the restarted decoder is still empty there is nothing to loop.
                if (_loop && !justSought) { _decoder.Seek(0); justSought = true; continue; }
                Volatile.Write(ref _done, 1);
                break;
            }
            justSought = false;
            int start = (int)(tail % _ring.Length);
            int count = got * _ch;
            int first = Math.Min(count, _ring.Length - start);
            _chunk.AsSpan(0, first).CopyTo(_ring.AsSpan(start));
            _chunk.AsSpan(first, count - first).CopyTo(_ring);
            Volatile.Write(ref _tail, tail + count);
            added += got;
        }
        return added;
    }

    /// <summary>Disposes the decoder. Call after the voice has stopped and the decode thread has finished.</summary>
    public void Dispose() => _decoder.Dispose();

    internal override int ReadFrames(ref long position, bool loop, long loopStart, long loopEnd, Span<float> destination, out bool underrun)
    {
        // Read 'done' before the tail: the decoder publishes its last data before setting done, so if done is
        // seen set, the tail we read next already includes every frame.
        bool done = Volatile.Read(ref _done) == 1;
        long head = _head;
        long avail = Volatile.Read(ref _tail) - head;
        int want = destination.Length / _ch;
        int n = (int)Math.Min(want, avail / _ch);
        int count = n * _ch;
        int start = (int)(head % _ring.Length);
        int first = Math.Min(count, _ring.Length - start);
        _ring.AsSpan(start, first).CopyTo(destination);
        _ring.AsSpan(0, count - first).CopyTo(destination[first..]);
        Volatile.Write(ref _head, head + count);
        underrun = n < want && !done;
        if (underrun)
        {
            Volatile.Write(ref _underrunEvents, _underrunEvents + 1);
            Volatile.Write(ref _underrunFrames, _underrunFrames + (want - n));
        }
        return n;
    }
}
