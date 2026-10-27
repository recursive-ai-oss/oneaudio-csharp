using System.Threading;

namespace OneAudio.Devices;

/// <summary>A single-producer single-consumer ring of floats (whole frames), preallocated; full writes drop the excess.</summary>
internal sealed class FloatRing
{
    private readonly float[] _data;
    private readonly int _channels;
    private long _head; // next sample the consumer reads
    private long _tail; // next sample the producer writes

    public FloatRing(int capacityFrames, int channels)
    {
        _channels = channels;
        _data = new float[checked(capacityFrames * channels)];
    }

    public int CapacityFrames => _data.Length / _channels;

    public int AvailableFrames => (int)((Volatile.Read(ref _tail) - Volatile.Read(ref _head)) / _channels);

    /// <summary>Writes as many whole frames as fit; returns the number of frames written.</summary>
    public int Write(ReadOnlySpan<float> samples)
    {
        long tail = _tail;
        long free = _data.Length - (tail - Volatile.Read(ref _head));
        int n = (int)Math.Min(samples.Length, free) / _channels * _channels;
        int at = (int)(tail % _data.Length);
        int first = Math.Min(n, _data.Length - at);
        samples[..first].CopyTo(_data.AsSpan(at));
        samples.Slice(first, n - first).CopyTo(_data);
        Volatile.Write(ref _tail, tail + n);
        return n / _channels;
    }

    /// <summary>Reads up to the whole frames that fit in <paramref name="destination"/>; returns frames read.</summary>
    public int Read(Span<float> destination)
    {
        long head = _head;
        long avail = Volatile.Read(ref _tail) - head;
        int n = (int)Math.Min(destination.Length, avail) / _channels * _channels;
        int at = (int)(head % _data.Length);
        int first = Math.Min(n, _data.Length - at);
        _data.AsSpan(at, first).CopyTo(destination);
        _data.AsSpan(0, n - first).CopyTo(destination[first..]);
        Volatile.Write(ref _head, head + n);
        return n / _channels;
    }

    /// <summary>Drops everything; both sides must be quiet.</summary>
    public void Clear() => Volatile.Write(ref _head, Volatile.Read(ref _tail));
}

/// <summary>
/// Remembers, for blocks handed to the device, the frame where each ends and its peak, so the peak of what the device has really
/// consumed can be found once its position passes the block. One producer (the render thread), one consumer (under the engine's fold lock).
/// </summary>
internal sealed class PeakRing
{
    private readonly long[] _end;
    private readonly float[] _peak;
    private readonly int _mask;
    private long _head;
    private long _tail;

    public PeakRing(int capacityPowerOfTwo)
    {
        _end = new long[capacityPowerOfTwo];
        _peak = new float[capacityPowerOfTwo];
        _mask = capacityPowerOfTwo - 1;
    }

    /// <summary>Records a block ending at stream frame <paramref name="endFrame"/>; false (and nothing recorded) when the ring is full.</summary>
    public bool Add(long endFrame, float peak)
    {
        long tail = _tail;
        if (tail - Volatile.Read(ref _head) > _mask)
        {
            return false;
        }

        int i = (int)(tail & _mask);
        _end[i] = endFrame;
        _peak[i] = peak;
        Volatile.Write(ref _tail, tail + 1);
        return true;
    }

    /// <summary>Takes every block that ends at or before <paramref name="playedFrame"/>; returns the largest peak among them (0 if none).</summary>
    public float Take(long playedFrame)
    {
        float max = 0f;
        long head = _head;
        long tail = Volatile.Read(ref _tail);
        while (head < tail)
        {
            int i = (int)(head & _mask);
            if (_end[i] > playedFrame)
            {
                break;
            }

            if (_peak[i] > max)
            {
                max = _peak[i];
            }

            head++;
        }

        Volatile.Write(ref _head, head);
        return max;
    }

    /// <summary>Forgets everything not yet taken.</summary>
    public void Clear() => Volatile.Write(ref _head, Volatile.Read(ref _tail));
}

/// <summary>A small single-producer single-consumer queue of events; a full queue refuses the newest.</summary>
internal sealed class EventQueue
{
    private readonly DeviceEvent[] _items;
    private long _head;
    private long _tail;

    public EventQueue(int capacity) => _items = new DeviceEvent[capacity];

    public bool TryEnqueue(in DeviceEvent e)
    {
        long tail = _tail;
        if (tail - Volatile.Read(ref _head) >= _items.Length)
        {
            return false;
        }

        _items[(int)(tail % _items.Length)] = e;
        Volatile.Write(ref _tail, tail + 1);
        return true;
    }

    public bool TryDequeue(out DeviceEvent e)
    {
        long head = _head;
        if (head >= Volatile.Read(ref _tail))
        {
            e = default;
            return false;
        }

        e = _items[(int)(head % _items.Length)];
        Volatile.Write(ref _head, head + 1);
        return true;
    }
}
