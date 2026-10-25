namespace OneAudio;

/// <summary>
/// One value published by a single writer thread and read by any number of other threads without locks (a sequence lock).
/// The writer never waits and never allocates; a reader retries while a write is in progress, so it never sees a mix of two
/// writes. <typeparamref name="T"/> must be plain data (no references).
/// </summary>
internal sealed class SeqLockSlot<T> where T : struct
{
    private int _seq;       // odd while a write is in progress
    private T _value;

    /// <summary>Publishes a value. Single writer only.</summary>
    public void Write(in T value)
    {
        // The full fence of Interlocked keeps the data stores below from moving above the odd marker.
        Interlocked.Increment(ref _seq);
        _value = value;
        Volatile.Write(ref _seq, _seq + 1);   // release: the data stores complete before the even marker is visible
    }

    /// <summary>Reads the latest complete value. Spins only while the writer is inside <see cref="Write"/> (a few instructions).</summary>
    public T Read()
    {
        var wait = new SpinWait();
        while (true)
        {
            int s1 = Volatile.Read(ref _seq);
            if ((s1 & 1) == 0)
            {
                T copy = _value;
                Interlocked.MemoryBarrier();   // the data loads complete before the second sequence read
                if (Volatile.Read(ref _seq) == s1) return copy;
            }
            wait.SpinOnce(-1);
        }
    }
}
