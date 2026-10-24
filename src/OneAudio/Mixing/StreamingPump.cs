namespace OneAudio;

/// <summary>
/// Keeps any number of <see cref="StreamingSource"/> rings topped up from one decode thread, so a host can start many
/// streaming voices without managing a thread for each. The host owns it: create one next to the mixer, <see cref="Add"/>
/// every source before (or just after) playing it, and <see cref="Dispose"/> it at shutdown.
/// <para>
/// Either let it run its own background thread with <see cref="Start"/>, or call <see cref="PumpOnce"/> from a thread the
/// host already has; do not mix the two. A source whose decoder has finished and drained, or whose decoder failed, is removed
/// automatically. Sources of voices that were stopped early are not noticed: call <see cref="Remove"/> for them.
/// </para>
/// <para>
/// Decoding happens here, never on the render thread; a slow disk or codec shows up as underruns (counted by the mixer and
/// the source), not as a stall of the audio callback.
/// </para>
/// </summary>
public sealed class StreamingPump : IDisposable
{
    private readonly object _gate = new();
    private readonly SemaphoreSlim _wake = new(0);
    private readonly int _intervalMs;
    private readonly double _refillBelow;
    private StreamingSource[] _sources = [];
    private Thread? _thread;
    private volatile bool _stop;
    private bool _disposed;
    private long _passes;

    /// <summary>Creates an idle pump.</summary>
    /// <param name="intervalMilliseconds">How long the background thread sleeps between rounds (1..1000). Default 5.</param>
    /// <param name="refillBelow">A source is refilled once its ring holds at most this fraction of its capacity (0..1). Default 0.75: refills happen in
    /// sizeable chunks rather than a few frames at a time.</param>
    /// <exception cref="ArgumentOutOfRangeException">An argument is out of range.</exception>
    public StreamingPump(int intervalMilliseconds = 5, double refillBelow = 0.75)
    {
        ArgumentOutOfRangeException.ThrowIfLessThan(intervalMilliseconds, 1);
        ArgumentOutOfRangeException.ThrowIfGreaterThan(intervalMilliseconds, 1000);
        if (!(refillBelow >= 0 && refillBelow <= 1)) throw new ArgumentOutOfRangeException(nameof(refillBelow));
        _intervalMs = intervalMilliseconds;
        _refillBelow = refillBelow;
    }

    /// <summary>Number of sources currently being kept filled.</summary>
    public int Count => Volatile.Read(ref _sources).Length;

    /// <summary>Completed rounds of the background thread or <see cref="PumpOnce"/>. Safe to read from any thread.</summary>
    public long Rounds => Volatile.Read(ref _passes);

    /// <summary>
    /// Adds a source. With <paramref name="prime"/> (the default) its ring is filled before this returns, on the calling thread,
    /// so a voice started right afterwards does not begin with an underrun. A source belongs to one pump at a time.
    /// </summary>
    /// <param name="source">The source; it must not be pumped by anything else.</param>
    /// <param name="prime">Fill the ring before returning.</param>
    /// <exception cref="ObjectDisposedException">The pump was disposed.</exception>
    /// <exception cref="InvalidOperationException">The source was already added.</exception>
    public void Add(StreamingSource source, bool prime = true)
    {
        ArgumentNullException.ThrowIfNull(source);
        ObjectDisposedException.ThrowIf(_disposed, this);
        lock (_gate)
        {
            if (Array.IndexOf(_sources, source) >= 0) throw new InvalidOperationException("The source is already added.");
            // Primed under the lock so the background thread cannot pump the same decoder at the same time.
            if (prime) PumpGuarded(source);
            var next = new StreamingSource[_sources.Length + 1];
            _sources.CopyTo(next, 0);
            next[^1] = source;
            Volatile.Write(ref _sources, next);
        }
        _wake.Release();
    }

    /// <summary>
    /// Stops pumping a source. When this returns the pump is guaranteed not to touch the source's decoder any more, so the
    /// caller may dispose it. It may wait for a decode round in progress.
    /// </summary>
    /// <param name="source">The source.</param>
    /// <returns>False if it was not in the pump (already finished and removed, for example).</returns>
    public bool Remove(StreamingSource source)
    {
        ArgumentNullException.ThrowIfNull(source);
        lock (_gate) return RemoveLocked(source);
    }

    /// <summary>
    /// Runs one round on the calling thread: refills every source whose ring has room, and drops finished or failed ones.
    /// Decoder errors of the library's own types and I/O errors are caught per source and kept in <see cref="StreamingSource.Fault"/>;
    /// anything else is a bug and propagates.
    /// </summary>
    /// <returns>Frames decoded this round.</returns>
    public int PumpOnce()
    {
        int total = 0;
        lock (_gate)
        {
            foreach (var s in _sources)
            {
                if (s.BufferedFrames <= s.CapacityFrames * _refillBelow) total += PumpGuarded(s);
                if (s.Ended || s.Fault != null) RemoveLocked(s);
            }
        }
        Interlocked.Increment(ref _passes);
        return total;
    }

    /// <summary>Starts the background decode thread (idempotent).</summary>
    /// <exception cref="ObjectDisposedException">The pump was disposed.</exception>
    public void Start()
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        lock (_gate)
        {
            if (_thread != null) return;
            _thread = new Thread(Run) { IsBackground = true, Name = "OneAudio stream pump" };
            _thread.Start();
        }
    }

    /// <summary>Stops the background thread and waits for it. Sources are left as they are; the host disposes them.</summary>
    public void Dispose()
    {
        Thread? t;
        lock (_gate)
        {
            if (_disposed) return;
            _disposed = true;
            _stop = true;
            t = _thread;
            _thread = null;
        }
        _wake.Release();
        t?.Join();
        _wake.Dispose();
    }

    private void Run()
    {
        while (!_stop)
        {
            PumpOnce();
            _wake.Wait(_intervalMs); // returns early when a source is added or the pump is disposed
        }
    }

    private bool RemoveLocked(StreamingSource source)
    {
        int i = Array.IndexOf(_sources, source);
        if (i < 0) return false;
        var next = new StreamingSource[_sources.Length - 1];
        Array.Copy(_sources, 0, next, 0, i);
        Array.Copy(_sources, i + 1, next, i, next.Length - i);
        Volatile.Write(ref _sources, next);
        return true;
    }

    private static int PumpGuarded(StreamingSource s)
    {
        try { return s.Pump(); }
        catch (OneAudioException e) { s.Fail(e); }
        catch (IOException e) { s.Fail(e); }
        return 0;
    }
}
