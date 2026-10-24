namespace OneAudio;

/// <summary>
/// Preallocated single-producer single-consumer ring. Wait-free on both sides; neither side allocates, locks or blocks.
/// </summary>
internal sealed class SpscQueue<T>(int capacity)
{
    private readonly T[] _items = new T[capacity];
    private long _head; // next slot to read; written by the consumer only
    private long _tail; // next slot to write; written by the producer only

    public int Capacity => _items.Length;

    /// <summary>Free slots as seen by the producer.</summary>
    public int FreeSlots => _items.Length - (int)(Volatile.Read(ref _tail) - Volatile.Read(ref _head));

    public bool TryEnqueue(in T item)
    {
        long tail = _tail;
        if (tail - Volatile.Read(ref _head) >= _items.Length) return false;
        _items[(int)(tail % _items.Length)] = item;
        Volatile.Write(ref _tail, tail + 1);
        return true;
    }

    public bool TryDequeue(out T item)
    {
        long head = _head;
        if (head == Volatile.Read(ref _tail)) { item = default!; return false; }
        int i = (int)(head % _items.Length);
        item = _items[i];
        _items[i] = default!; // drop references so retired sources can be collected
        Volatile.Write(ref _head, head + 1);
        return true;
    }
}
