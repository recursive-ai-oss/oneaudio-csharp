using System.Diagnostics;
using System.Threading;
using OneAudio.Devices.Interop;

namespace OneAudio.Devices;

/// <summary>
/// One open ALSA PCM. For playback a thread of its own asks the engine for a period of audio, writes it (blocking while the device buffer is
/// full, which paces the thread) and then reads the device's delay to learn how much of what it wrote has really been played. For capture the
/// thread reads a period (blocking until the device has it) and hands it to the engine.
/// </summary>
internal sealed class AlsaStream : IOpenDevice
{
    private const int MaxRecoveries = 8;

    private readonly nint _pcm;
    private readonly DeviceDirection _direction;
    private readonly IDeviceEngine _engine;
    private readonly bool _isFloat;
    private readonly int _channels;
    private readonly float[] _buffer;
    private readonly short[]? _short;
    private readonly Thread _thread;
    private readonly ManualResetEventSlim _gate = new(false);
    private int _run;
    private int _busy;
    private int _quit;
    private int _lost;
    private int _needPrepare;
    private long _written;
    private long _played;
    private int _queued;

    public AlsaStream(nint pcm, DeviceDirection direction, IDeviceEngine engine, AudioDeviceFormat format, AudioDeviceInfo device, bool isFloat, int periodFrames, int bufferFrames)
    {
        _pcm = pcm;
        _direction = direction;
        _engine = engine;
        _isFloat = isFloat;
        _channels = format.Channels;
        Format = format;
        Device = device;
        PeriodFrames = periodFrames;
        BufferFrames = bufferFrames;
        _buffer = new float[periodFrames * _channels];
        _short = isFloat ? null : new short[periodFrames * _channels];
        _thread = new Thread(Loop) { IsBackground = true, Name = "oneaudio-alsa", Priority = ThreadPriority.Highest };
        _thread.Start();
    }

    public AudioDeviceFormat Format { get; }

    public AudioDeviceInfo? Device { get; }

    public int PeriodFrames { get; }

    public int BufferFrames { get; }

    public long FramesPlayed => Volatile.Read(ref _played);

    public int QueuedFrames => Volatile.Read(ref _queued);

    public string? Detail => _isFloat ? "alsa float" : "alsa s16";

    public bool Start(out string reason)
    {
        reason = string.Empty;
        if (Volatile.Read(ref _lost) != 0)
        {
            reason = "the device was lost";
            return false;
        }

        Volatile.Write(ref _run, 1);
        _gate.Set();
        return true;
    }

    public void Pause()
    {
        Interlocked.Exchange(ref _run, 0);
        var wait = new SpinWait();
        while (Volatile.Read(ref _busy) != 0)
        {
            wait.SpinOnce();
        }

        if (Volatile.Read(ref _lost) != 0)
        {
            return;
        }

        // The loop is parked, so this thread may use the handle. Let what is queued play out so the position stays exact, then
        // prepare again on the next start.
        if (_direction == DeviceDirection.Output)
        {
            if (AlsaNative.Drain(_pcm) < 0)
            {
                _ = AlsaNative.Drop(_pcm);
            }

            Refresh();
        }
        else
        {
            _ = AlsaNative.Drop(_pcm);
        }

        Volatile.Write(ref _needPrepare, 1);
    }

    public void Dispose()
    {
        Interlocked.Exchange(ref _run, 0);
        Volatile.Write(ref _quit, 1);
        _gate.Set();
        _thread.Join();
        _ = AlsaNative.Close(_pcm);
        _gate.Dispose();
    }

    private void Loop()
    {
        while (Volatile.Read(ref _quit) == 0)
        {
            if (Volatile.Read(ref _run) == 0 || Volatile.Read(ref _lost) != 0)
            {
                _gate.Wait();
                _gate.Reset();
                continue;
            }

            // Announce the cycle before re-checking the run flag, so Pause either sees us busy or we see it paused.
            Interlocked.Exchange(ref _busy, 1);
            if (Volatile.Read(ref _run) != 0 && Volatile.Read(ref _quit) == 0)
            {
                Cycle();
            }

            Volatile.Write(ref _busy, 0);
        }
    }

    private void Cycle()
    {
        if (Interlocked.Exchange(ref _needPrepare, 0) != 0)
        {
            _ = AlsaNative.Prepare(_pcm);
        }

        if (_direction == DeviceDirection.Output ? WriteBlock() : ReadBlock())
        {
            Refresh();
        }
    }

    /// <summary>Captures one period (blocking until the device has it) and hands it to the engine.</summary>
    private bool ReadBlock()
    {
        int frames = PeriodFrames;
        int ch = _channels;
        int done = 0;
        int failures = 0;
        while (done < frames)
        {
            if (Volatile.Read(ref _quit) != 0)
            {
                return false;
            }

            int remaining = frames - done;
            nint n = _short is null
                ? AlsaNative.ReadI(_pcm, _buffer.AsSpan(done * ch, remaining * ch), (nuint)remaining)
                : AlsaNative.ReadI(_pcm, _short.AsSpan(done * ch, remaining * ch), (nuint)remaining);
            if (n >= 0)
            {
                done += (int)n;
                failures = 0;
                continue;
            }

            int error = (int)n;
            if (error == -AlsaNative.Eintr)
            {
                continue;
            }

            if (error == -AlsaNative.Epipe || error == -AlsaNative.Estrpipe)
            {
                // The device overran: audio was lost between reads.
                _engine.NotifyXrun(0);
                if (++failures > MaxRecoveries || AlsaNative.Recover(_pcm, error, 1) < 0)
                {
                    Lose("the device could not recover from an overrun");
                    return false;
                }

                continue;
            }

            Lose($"snd_pcm_readi failed: errno {-error}");
            return false;
        }

        if (_short is not null)
        {
            for (int i = 0; i < _short.Length; i++)
            {
                _buffer[i] = _short[i] * (1f / 32768f);
            }
        }

        Interlocked.Add(ref _written, frames);
        _engine.Capture(_buffer);
        return true;
    }

    private bool WriteBlock()
    {
        int frames = PeriodFrames;
        int ch = _channels;
        _engine.Render(_buffer);
        if (_short is not null)
        {
            ToInt16(_buffer, _short);
        }

        int done = 0;
        int failures = 0;
        while (done < frames)
        {
            if (Volatile.Read(ref _quit) != 0)
            {
                return false;
            }

            int remaining = frames - done;
            nint n = _short is null
                ? AlsaNative.WriteI(_pcm, _buffer.AsSpan(done * ch, remaining * ch), (nuint)remaining)
                : AlsaNative.WriteI(_pcm, _short.AsSpan(done * ch, remaining * ch), (nuint)remaining);
            if (n >= 0)
            {
                done += (int)n;
                Interlocked.Add(ref _written, n);
                failures = 0;
                continue;
            }

            int error = (int)n;
            if (error == -AlsaNative.Eintr)
            {
                continue;
            }

            if (error == -AlsaNative.Epipe || error == -AlsaNative.Estrpipe)
            {
                _engine.NotifyXrun(0);
                if (++failures > MaxRecoveries || AlsaNative.Recover(_pcm, error, 1) < 0)
                {
                    Lose("the device could not recover from an underrun");
                    return false;
                }

                continue;
            }

            Lose($"snd_pcm_writei failed: errno {-error}");
            return false;
        }

        return true;
    }

    /// <summary>Converts float samples to 16-bit: scaled by 32767, rounded to nearest with ties toward +infinity, clamped (a sample outside -1..1 cannot happen after the engine's limiter, but would not wrap).</summary>
    internal static void ToInt16(ReadOnlySpan<float> source, Span<short> destination)
    {
        for (int i = 0; i < source.Length; i++)
        {
            destination[i] = (short)Math.Clamp((int)MathF.Floor((source[i] * 32767f) + 0.5f), short.MinValue, short.MaxValue);
        }
    }

    /// <summary>Reads how much of what was written is still queued; the rest has been played.</summary>
    private void Refresh()
    {
        long written = Volatile.Read(ref _written);
        if (_direction == DeviceDirection.Input)
        {
            // Captured frames are those the program has been given; the device's delay is what waits in its buffer to be read.
            Volatile.Write(ref _queued, AlsaNative.Delay(_pcm, out nint waiting) == 0 ? (int)Math.Clamp(waiting, 0, BufferFrames) : 0);
            Volatile.Write(ref _played, written);
            return;
        }

        long queued;
        if (AlsaNative.Delay(_pcm, out nint delay) == 0)
        {
            queued = Math.Clamp(delay, 0, written);
        }
        else
        {
            // After an underrun everything written has been consumed.
            queued = AlsaNative.State(_pcm) == AlsaNative.StateXrun ? 0 : Volatile.Read(ref _queued);
        }

        long played = Math.Max(Volatile.Read(ref _played), written - queued);
        Volatile.Write(ref _played, played);
        Volatile.Write(ref _queued, (int)queued);
    }

    private void Lose(string reason)
    {
        Volatile.Write(ref _lost, 1);
        _engine.NotifyLost(reason);
    }
}
