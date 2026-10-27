using System.Runtime.InteropServices;
using System.Threading;
using OneAudio.Devices.Interop;

namespace OneAudio.Devices;

/// <summary>
/// One open AAudio stream. AAudio calls <c>OnOutput</c> (or <c>OnInput</c>) on its own thread whenever the device wants (or has) a
/// burst; the stream hands the device's buffer straight to the engine, so the callback copies and allocates nothing. The device's own counters
/// supply the position (timestamp, frames read) and the xrun count.
/// </summary>
internal sealed class AAudioStream : IOpenDevice, IAAudioClient
{
    private static readonly long s_waitNanoseconds = 2_000_000_000;

    private readonly DeviceDirection _direction;
    private readonly IDeviceEngine _engine;
    private nint _stream;
    private GCHandle _handle;
    private string? _detail;
    private int _burst;
    private int _buffer;
    private AudioDeviceInfo _info;
    private int _closing;
    private int _inCallback;
    private int _lost;
    private int _seenXruns;
    private long _played;

    public AAudioStream(DeviceDirection direction, IDeviceEngine engine, AudioDeviceFormat format)
    {
        _direction = direction;
        _engine = engine;
        Format = format;
    }

    /// <summary>Completes construction once the system has opened the stream and the callbacks may find it.</summary>
    internal void Attach(nint stream, GCHandle handle, AudioDeviceInfo info, int burst, int buffer, string detail)
    {
        _stream = stream;
        _handle = handle;
        _info = info;
        _burst = burst;
        _buffer = buffer;
        _detail = detail;
    }

    public AudioDeviceFormat Format { get; }

    public AudioDeviceInfo? Device => _info;

    public int PeriodFrames => _burst;

    public int BufferFrames => _buffer;

    int IAAudioClient.Channels => Format.Channels;

    bool IAAudioClient.IsOutput => _direction == DeviceDirection.Output;

    bool IAAudioClient.IsClosing => Volatile.Read(ref _closing) != 0;

    public string? Detail => _detail;

    public int QueuedFrames
    {
        get
        {
            if (Volatile.Read(ref _closing) != 0 || _stream == 0)
            {
                return 0;
            }

            long written = AAudioNative.GetFramesWritten(_stream);
            long read = AAudioNative.GetFramesRead(_stream);
            // Output: written by the program, not yet read by the device. Input: written by the device, not yet read by the program.
            return (int)Math.Clamp(written - read, 0, int.MaxValue);
        }
    }

    public long FramesPlayed
    {
        get
        {
            if (Volatile.Read(ref _closing) != 0 || _stream == 0)
            {
                return Volatile.Read(ref _played);
            }

            long previous = Volatile.Read(ref _played);
            long now;
            if (_direction == DeviceDirection.Output)
            {
                long read = AAudioNative.GetFramesRead(_stream);
                now = AAudioNative.GetTimestamp(_stream, AAudioNative.ClockMonotonic, out long position, out long time) == AAudioNative.Ok
                    ? AAudioLogic.PlayedFrames(position, time, AAudioNative.MonotonicNanoseconds(), Format.SampleRate, read, previous)
                    : Math.Max(read, previous);
            }
            else
            {
                // Captured frames are the ones the device has written into its buffer.
                now = Math.Max(AAudioNative.GetFramesWritten(_stream), previous);
            }

            // Monotonic across threads: only ever move forward.
            while (now > previous)
            {
                long seen = Interlocked.CompareExchange(ref _played, now, previous);
                if (seen == previous)
                {
                    break;
                }

                previous = seen;
            }

            return Math.Max(now, previous);
        }
    }

    public bool Start(out string reason)
    {
        reason = string.Empty;
        if (Volatile.Read(ref _lost) != 0)
        {
            reason = "the device was lost";
            return false;
        }

        int rc = AAudioNative.RequestStart(_stream);
        if (rc != AAudioNative.Ok)
        {
            reason = $"AAudio could not start the stream (code {rc})";
            if (rc == AAudioNative.ErrorDisconnected)
            {
                Lose("AAudio device disconnected");
            }

            return false;
        }

        return true;
    }

    public void Pause()
    {
        if (Volatile.Read(ref _lost) != 0 || Volatile.Read(ref _closing) != 0)
        {
            return;
        }

        // An input stream cannot be paused in AAudio; stopping it keeps the stream open and it starts again the same way.
        bool input = _direction == DeviceDirection.Input;
        _ = input ? AAudioNative.RequestStop(_stream) : AAudioNative.RequestPause(_stream);
        WaitUntilSettled(input ? AAudioNative.StateStopped : AAudioNative.StatePaused);
        WaitForCallbackToLeave();
    }

    public void Dispose()
    {
        if (Interlocked.Exchange(ref _closing, 1) != 0 || _stream == 0)
        {
            return;
        }

        // Stop first: the system guarantees no callback runs once a stream has stopped, and closing from the callback thread is not allowed.
        _ = AAudioNative.RequestStop(_stream);
        WaitUntilSettled(AAudioNative.StateStopped);
        _ = AAudioNative.Close(_stream);
        _stream = 0;
        WaitForCallbackToLeave();
        _handle.Free();
    }

    void IAAudioClient.Enter() => Interlocked.Exchange(ref _inCallback, 1);

    void IAAudioClient.Leave() => Volatile.Write(ref _inCallback, 0);

    void IAAudioClient.OnOutput(Span<float> buffer)
    {
        _engine.Render(buffer);
        ReportXruns();
    }

    void IAAudioClient.OnInput(ReadOnlySpan<float> buffer)
    {
        _engine.Capture(buffer);
        ReportXruns();
    }

    void IAAudioClient.OnError(int error)
    {
        // Called from a system thread; the stream is not closed here (the NDK forbids it), only reported. The engine disposes it.
        if (Volatile.Read(ref _closing) == 0)
        {
            Lose(error == AAudioNative.ErrorDisconnected ? "AAudio device disconnected" : "AAudio stream error");
        }
    }

    private void ReportXruns()
    {
        // The device's own count of buffers it had to fill with silence (output) or drop (input).
        int xruns = AAudioNative.GetXRunCount(_stream);
        if (xruns > _seenXruns)
        {
            _seenXruns = xruns;
            _engine.NotifyDeviceUnderruns(xruns);
        }
    }

    private void Lose(string reason)
    {
        if (Interlocked.Exchange(ref _lost, 1) == 0)
        {
            _engine.NotifyLost(reason);
        }
    }

    /// <summary>Waits (bounded) until the stream reaches <paramref name="target"/> or ends up disconnected.</summary>
    private void WaitUntilSettled(int target)
    {
        int state = AAudioNative.GetState(_stream);
        long deadline = AAudioNative.MonotonicNanoseconds() + s_waitNanoseconds;
        while (state != target && state != AAudioNative.StateDisconnected && state != AAudioNative.StateStopped)
        {
            long remaining = deadline - AAudioNative.MonotonicNanoseconds();
            if (remaining <= 0)
            {
                return;
            }

            if (AAudioNative.WaitForStateChange(_stream, state, out int next, remaining) != AAudioNative.Ok)
            {
                return;
            }

            state = next;
        }
    }

    private void WaitForCallbackToLeave()
    {
        var wait = new SpinWait();
        long deadline = AAudioNative.MonotonicNanoseconds() + s_waitNanoseconds;
        while (Volatile.Read(ref _inCallback) != 0 && AAudioNative.MonotonicNanoseconds() < deadline)
        {
            wait.SpinOnce();
        }
    }
}
