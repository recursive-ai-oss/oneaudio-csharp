using System.Diagnostics;
using OneAudio.Devices.Interop;

namespace OneAudio.Devices;

/// <summary>The native objects of an opened, initialised WASAPI stream; the stream owns them from construction.</summary>
internal readonly record struct WasapiParts(nint Client, nint Render, nint Capture, nint Clock, nint BufferEvent, nint ControlEvent);

/// <summary>
/// One open WASAPI stream, driven by the audio engine's event: the device signals when it wants (or has) a buffer's worth, the device thread
/// asks the engine for audio (or hands it the capture) and goes back to waiting. The thread is registered with the multimedia class scheduler
/// as "Pro Audio". Playback position comes from IAudioClock, read after every cycle and when paused.
/// </summary>
internal sealed class WasapiStream : IOpenDevice
{
    private const int WaitMilliseconds = 2000;
    private const int CommandNone = 0;
    private const int CommandStart = 1;
    private const int CommandPause = 2;
    private const int CommandQuit = 3;

    private readonly nint _client;
    private readonly nint _render;
    private readonly nint _capture;
    private readonly nint _clock;
    private readonly nint _bufferEvent;
    private readonly nint _controlEvent;
    private readonly IDeviceEngine _engine;
    private readonly WasapiNotifications? _notifications;
    private readonly bool _output;
    private readonly bool _exclusive;
    private readonly WasapiSampleKind _kind;
    private readonly int _channels;
    private readonly int _sampleRate;
    private readonly int _blockAlign;
    private readonly float[] _scratch;
    private readonly byte[]? _bytes;
    private readonly ulong _clockFrequency;
    private readonly Lock _control = new();
    private readonly ManualResetEventSlim _ack = new(false);
    private Thread? _thread;
    private int _command;
    private bool _commandOk;
    private string _commandReason = string.Empty;
    private bool _running;
    private bool _primed;
    private int _lost;
    private long _frames;
    private long _delivered;
    private int _queued;
    private bool _disposed;
    private bool _starting;

    public WasapiStream(
        WasapiParts parts,
        DeviceDirection direction,
        IDeviceEngine engine,
        AudioDeviceFormat format,
        AudioDeviceInfo? device,
        bool exclusive,
        WasapiSampleKind kind,
        int periodFrames,
        int bufferFrames,
        WasapiNotifications? notifications)
    {
        _client = parts.Client;
        _render = parts.Render;
        _capture = parts.Capture;
        _clock = parts.Clock;
        _bufferEvent = parts.BufferEvent;
        _controlEvent = parts.ControlEvent;
        _engine = engine;
        _notifications = notifications;
        _output = direction == DeviceDirection.Output;
        _exclusive = exclusive;
        _kind = kind;
        _channels = format.Channels;
        _sampleRate = format.SampleRate;
        _blockAlign = _channels * WasapiLogic.BytesPerSample(kind);
        Format = format;
        Device = device;
        PeriodFrames = periodFrames;
        BufferFrames = bufferFrames;
        _scratch = new float[Math.Max(1, bufferFrames) * _channels];
        _bytes = kind == WasapiSampleKind.Float32 ? null : new byte[Math.Max(1, bufferFrames) * _blockAlign];
        if (_clock != 0 && WasapiNative.ClockGetFrequency(_clock, out ulong frequency) >= 0)
        {
            _clockFrequency = frequency;
        }
    }

    public AudioDeviceFormat Format { get; }

    public AudioDeviceInfo? Device { get; }

    public int PeriodFrames { get; }

    public int BufferFrames { get; }

    /// <summary>Output: the device clock's position as of the last cycle. Input: the frames handed to the engine.</summary>
    public long FramesPlayed => _output ? Volatile.Read(ref _frames) : Volatile.Read(ref _delivered);

    public int QueuedFrames => Volatile.Read(ref _queued);

    public string? Detail => $"wasapi {(_exclusive ? "exclusive" : "shared")} {_kind}";

    public bool Start(out string reason)
    {
        lock (_control)
        {
            if (_disposed || Volatile.Read(ref _lost) != 0)
            {
                reason = "the device was lost";
                return false;
            }

            if (_thread is null)
            {
                _thread = new Thread(Loop) { IsBackground = true, Name = "oneaudio-wasapi", Priority = ThreadPriority.Highest };
                _thread.Start();
            }

            bool ok = Post(CommandStart);
            reason = ok ? string.Empty : _commandReason;
            return ok;
        }
    }

    public void Pause()
    {
        lock (_control)
        {
            if (_thread is not null && !_disposed)
            {
                _ = Post(CommandPause);
            }
        }
    }

    public void Dispose()
    {
        lock (_control)
        {
            if (_disposed)
            {
                return;
            }

            _disposed = true;
            if (_thread is not null)
            {
                _ = Post(CommandQuit);
                _thread.Join();
            }
        }

        _notifications?.Remove(_engine);
        WasapiNative.Release(_clock);
        WasapiNative.Release(_render);
        WasapiNative.Release(_capture);
        WasapiNative.Release(_client);
        WasapiNative.CloseEvent(_bufferEvent);
        WasapiNative.CloseEvent(_controlEvent);
        _ack.Dispose();
    }

    private bool Post(int command)
    {
        _ack.Reset();
        Volatile.Write(ref _command, command);
        WasapiNative.Signal(_controlEvent);
        _ack.Wait();
        return Volatile.Read(ref _commandOk);
    }

    // ------------------------------------------------------------------ device thread

    private void Loop()
    {
        bool initialised = WasapiNative.EnterCom();
        nint task = WasapiNative.EnterProAudio();
        try
        {
            while (true)
            {
                int which = WasapiNative.WaitAny(_bufferEvent, _controlEvent, _running ? WaitMilliseconds : -1);
                if (which == 1)
                {
                    if (Execute(Interlocked.Exchange(ref _command, CommandNone)))
                    {
                        break;
                    }
                }
                else if (_running)
                {
                    if (which == 0)
                    {
                        Step();
                    }
                    else if (which == -1)
                    {
                        Probe();
                    }
                    else
                    {
                        Lose("the audio wait failed");
                    }
                }
            }
        }
        finally
        {
            WasapiNative.LeaveProAudio(task);
            if (initialised)
            {
                WasapiNative.LeaveCom();
            }
        }
    }

    /// <summary>Runs a control command on the device thread and acknowledges it. Returns true to end the thread.</summary>
    private bool Execute(int command)
    {
        switch (command)
        {
            case CommandStart:
                _commandOk = BeginRunning(out _commandReason);
                break;
            case CommandPause:
                EndRunning();
                _commandOk = true;
                break;
            case CommandQuit:
                EndRunning();
                _commandOk = true;
                _ack.Set();
                return true;
            default:
                return false;
        }

        _ack.Set();
        return false;
    }

    /// <summary>Fills the buffer with the engine's first audio when playing (the device must not start empty), then starts the client.</summary>
    internal bool BeginRunning(out string reason)
    {
        reason = string.Empty;
        if (Volatile.Read(ref _lost) != 0)
        {
            reason = "the device was lost";
            return false;
        }

        if (_running)
        {
            return true;
        }

        _starting = true;
        bool filled = !_output || RenderStep(prime: true);
        _starting = false;
        if (!filled)
        {
            reason = "the device failed while filling its first buffer";
            return false;
        }

        int hr = WasapiNative.Start(_client);
        if (hr < 0)
        {
            reason = WasapiLogic.Describe("IAudioClient::Start failed", hr);
            return false;
        }

        _primed = true;
        _running = true;
        PollPosition();
        return true;
    }

    /// <summary>Stops the client; what is queued stays queued and plays on the next start, so the position stays consistent.</summary>
    internal void EndRunning()
    {
        if (!_running)
        {
            return;
        }

        _running = false;
        _ = WasapiNative.Stop(_client);
        PollPosition();
    }

    /// <summary>One cycle after the buffer event: render (or capture) and read the position.</summary>
    internal void Step()
    {
        bool ok = _output ? RenderStep(prime: false) : CaptureStep();
        if (ok)
        {
            PollPosition();
        }
    }

    /// <summary>The buffer event did not come for a while: ask the client something to learn whether the device is still there.</summary>
    internal void Probe()
    {
        int hr = WasapiNative.GetCurrentPadding(_client, out _);
        if (WasapiLogic.Classify(hr) is WasapiFault.Lost or WasapiFault.Failed)
        {
            Lose(WasapiLogic.LostReason(hr));
        }
    }

    internal bool IsLost => Volatile.Read(ref _lost) != 0;

    private void Lose(string reason)
    {
        if (Interlocked.Exchange(ref _lost, 1) == 0)
        {
            _running = false;
            _ = WasapiNative.Stop(_client);
            if (!_starting)
            {
                // While starting, the failed Start call itself tells the engine.
                _engine.NotifyLost(reason);
            }
        }
    }

    private void PollPosition()
    {
        if (!_output || _clockFrequency == 0 || _clock == 0)
        {
            return;
        }

        if (WasapiNative.ClockGetPosition(_clock, out ulong position) >= 0)
        {
            long frames = WasapiLogic.PositionToFrames(position, _clockFrequency, _sampleRate);
            if (frames > Volatile.Read(ref _frames))
            {
                Volatile.Write(ref _frames, frames);
            }
        }
    }

    // ------------------------------------------------------------------ playback

    /// <summary>
    /// Writes the free part of the device buffer: shared mode asks the client how much is queued, exclusive mode owns the whole buffer.
    /// Returns false when the stream was lost or failed.
    /// </summary>
    private bool RenderStep(bool prime)
    {
        int padding = 0;
        if (!_exclusive)
        {
            int hr = WasapiNative.GetCurrentPadding(_client, out uint queued);
            if (hr < 0)
            {
                return Fail(hr);
            }

            padding = (int)Math.Min(queued, (uint)BufferFrames);
            if (!prime && _primed && padding == 0)
            {
                // The device drained everything before we got here.
                _engine.NotifyXrun(0);
            }
        }

        int frames = _exclusive ? BufferFrames : WasapiLogic.WritableFrames(BufferFrames, padding);
        if (frames == 0)
        {
            return true;
        }

        int r = WasapiNative.RenderGetBuffer(_render, (uint)frames, out nint data);
        if (r < 0)
        {
            return WasapiLogic.Classify(r) == WasapiFault.Skip || Fail(r);
        }

        var block = _scratch.AsSpan(0, frames * _channels);
        _engine.Render(block);
        if (_bytes is null)
        {
            WasapiNative.CopyToNative<float>(data, block);
        }
        else
        {
            var bytes = _bytes.AsSpan(0, frames * _blockAlign);
            WasapiLogic.FromFloat(block, _kind, bytes);
            WasapiNative.CopyToNative<byte>(data, bytes);
        }

        r = WasapiNative.RenderReleaseBuffer(_render, (uint)frames, 0);
        if (r < 0)
        {
            return Fail(r);
        }

        Volatile.Write(ref _queued, _exclusive ? BufferFrames : padding + frames);
        return true;
    }

    // ------------------------------------------------------------------ capture

    /// <summary>Hands every packet the device holds to the engine.</summary>
    private bool CaptureStep()
    {
        // A device that keeps producing cannot trap the thread: bound the packets per cycle by the buffer size.
        for (int packets = 0; packets < 64; packets++)
        {
            int hr = WasapiNative.CaptureGetNextPacketSize(_capture, out uint next);
            if (hr < 0)
            {
                return Fail(hr);
            }

            if (next == 0)
            {
                return true;
            }

            hr = WasapiNative.CaptureGetBuffer(_capture, out nint data, out uint frames, out uint flags);
            if (hr == WasapiLogic.AudclntSBufferEmpty)
            {
                return true;
            }

            if (hr < 0)
            {
                return Fail(hr);
            }

            if ((flags & WasapiLogic.BufferFlagDataDiscontinuity) != 0)
            {
                _engine.NotifyXrun(0);
            }

            bool silent = (flags & WasapiLogic.BufferFlagSilent) != 0;
            int done = 0;
            int total = (int)Math.Min(frames, int.MaxValue);
            int chunkLimit = _scratch.Length / _channels;
            while (done < total)
            {
                int n = Math.Min(total - done, chunkLimit);
                var block = _scratch.AsSpan(0, n * _channels);
                if (silent)
                {
                    block.Clear();
                }
                else if (_bytes is null)
                {
                    WasapiNative.CopyFromNative<float>(data + (done * _blockAlign), block);
                }
                else
                {
                    var bytes = _bytes.AsSpan(0, n * _blockAlign);
                    WasapiNative.CopyFromNative<byte>(data + (done * _blockAlign), bytes);
                    WasapiLogic.ToFloat(bytes, _kind, block);
                }

                _engine.Capture(block);
                done += n;
            }

            int rr = WasapiNative.CaptureReleaseBuffer(_capture, frames);
            Interlocked.Add(ref _delivered, total);
            Volatile.Write(ref _queued, 0);
            if (rr < 0)
            {
                return Fail(rr);
            }
        }

        return true;
    }

    private bool Fail(int hresult)
    {
        Lose(WasapiLogic.LostReason(hresult));
        return false;
    }
}
