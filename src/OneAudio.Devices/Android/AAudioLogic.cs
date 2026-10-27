using System.Globalization;

namespace OneAudio.Devices;

/// <summary>The arithmetic of the AAudio backend, kept apart from the native calls so it can be tested anywhere.</summary>
internal static class AAudioLogic
{
    /// <summary>
    /// The buffer size to ask for, in frames: the requested size (or the latency in milliseconds) rounded up to whole bursts, never below two
    /// bursts so one late callback does not underrun, and never above the capacity the system granted.
    /// </summary>
    public static int BufferTarget(int burst, int capacity, int requestedFrames, int latencyMilliseconds, int sampleRate)
    {
        burst = Math.Max(burst, 16);
        long wanted = requestedFrames > 0 ? requestedFrames : (long)latencyMilliseconds * sampleRate / 1000;
        wanted = Math.Max(wanted, 2L * burst);
        wanted = (wanted + burst - 1) / burst * burst;
        if (capacity > 0 && wanted > capacity)
        {
            wanted = capacity;
        }

        return (int)Math.Min(wanted, int.MaxValue);
    }

    /// <summary>
    /// Frames the speaker has played, estimated from the device's last timestamp (frame position at a time on the monotonic clock) by
    /// advancing it to <paramref name="nowNanoseconds"/>. It never exceeds <paramref name="framesRead"/>, which the device has consumed,
    /// and never goes backwards past <paramref name="previous"/>.
    /// </summary>
    public static long PlayedFrames(long framePosition, long timeNanoseconds, long nowNanoseconds, int sampleRate, long framesRead, long previous)
    {
        long estimate = framePosition;
        if (nowNanoseconds > timeNanoseconds)
        {
            estimate += (long)((double)(nowNanoseconds - timeNanoseconds) * sampleRate / 1e9);
        }

        estimate = Math.Min(estimate, framesRead);
        return Math.Max(estimate, previous);
    }

    /// <summary>The AAudio device id for a <see cref="DeviceStreamOptions.DeviceId"/>: null or "default" is the system default (0, "unspecified"), otherwise a decimal Android device id.</summary>
    public static bool TryParseDeviceId(string? id, out int deviceId)
    {
        deviceId = 0;
        if (string.IsNullOrEmpty(id) || id == AAudioBackend.DefaultId)
        {
            return true;
        }

        return int.TryParse(id, NumberStyles.None, CultureInfo.InvariantCulture, out deviceId) && deviceId > 0;
    }
}
