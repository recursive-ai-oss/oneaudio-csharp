using System.Diagnostics;
using OneAudio.Devices;
using OneAudio.Devices.Interop;
using Xunit;

namespace OneAudio.Devices.Tests;

/// <summary>Capture through ALSA: a PCM that reads a file (see <see cref="AlsaTestConfig"/>) and the <c>null</c> PCM, which captures silence.</summary>
public class AlsaCaptureTests
{
    private const int Rate = 48_000;

    private static void RequireAlsa() =>
        Assert.SkipUnless(OperatingSystem.IsLinux() && AlsaNative.IsPresent, "libasound is not available");

    private static void RequireFileCapture()
    {
        RequireAlsa();
        Assert.SkipUnless(AlsaTestConfig.Ready, "the test capture PCM could not be configured");
    }

    private static float[] FileSamples()
    {
        byte[] bytes = File.ReadAllBytes(AlsaTestConfig.InputFile!);
        var samples = new float[bytes.Length / 4];
        Buffer.BlockCopy(bytes, 0, samples, 0, bytes.Length);
        return samples;
    }

    [Fact]
    public void FileCapture_DeliversTheFileBitExact_ThroughTheReadBuffer()
    {
        RequireFileCapture();
        var expected = FileSamples();
        var options = new DeviceStreamOptions { DeviceId = AlsaTestConfig.CaptureDevice, CaptureBufferMilliseconds = 2000 };
        using var input = AudioInputDevice.Open(options, new AlsaBackend());
        input.Start();
        Assert.Equal(DeviceState.Running, input.State);
        Assert.True(Wait.Until(() => input.AvailableFrames >= AlsaTestConfig.CaptureFrames), "nothing captured: " + input.Counters);

        var got = new float[AlsaTestConfig.CaptureFrames * 2];
        Assert.Equal(AlsaTestConfig.CaptureFrames, input.Read(got));
        Assert.Equal(expected, got);

        var c = input.Counters;
        Assert.True(c.FramesWritten >= AlsaTestConfig.CaptureFrames);
        Assert.True(c.FramesPlayed >= AlsaTestConfig.CaptureFrames - 1024 && c.FramesPlayed <= c.FramesWritten, c.ToString());
        Assert.InRange(c.PeakPlayed, 0.249f, 0.251f);
        Assert.Equal(0, c.CallbackFaults);
        Assert.True(c.PeriodFrames > 0);
        Assert.Equal(AlsaTestConfig.CaptureDevice, input.Device?.Id);
    }

    [Fact]
    public void FileCapture_Callback_ReceivesTheFileBitExact_WithoutAllocating()
    {
        RequireFileCapture();
        var expected = FileSamples();
        var got = new float[AlsaTestConfig.CaptureFrames * 2];
        int filled = 0;
        CaptureCallback callback = (ReadOnlySpan<float> block, in RenderContext context) =>
        {
            int at = Volatile.Read(ref filled);
            int n = Math.Min(block.Length, got.Length - at);
            block[..n].CopyTo(got.AsSpan(at));
            Volatile.Write(ref filled, at + n);
        };

        using var input = AudioInputDevice.Open(callback, new DeviceStreamOptions { DeviceId = AlsaTestConfig.CaptureDevice, MeasureRenderAllocations = true }, new AlsaBackend());
        input.Start();
        Assert.True(Wait.Until(() => Volatile.Read(ref filled) >= got.Length), "the callback never got the whole file");
        Assert.Equal(expected, got);
        Assert.Equal(0, input.Counters.RenderAllocatedBytes);
        Assert.Throws<InvalidOperationException>(() => input.Read(new float[4]));
    }

    [Fact]
    public void NullCapture_DeliversSilence_AndCountsOverrunsWhenNobodyReads()
    {
        RequireAlsa();
        var options = new DeviceStreamOptions { DeviceId = "null", CaptureBufferMilliseconds = 50 };
        using var input = AudioInputDevice.Open(options, new AlsaBackend());
        input.Start();
        Assert.Equal(DeviceState.Running, input.State);
        Assert.True(Wait.Until(() => input.Counters.Overruns > 0), "the read buffer never overflowed: " + input.Counters);
        var got = new float[4096];
        int frames = input.Read(got);
        Assert.True(frames > 0);
        Assert.All(got.Take(frames * 2).ToArray(), v => Assert.Equal(0f, v));
        Assert.Equal(0f, input.Counters.PeakPlayed);
    }

    [Fact]
    public void SuspendStopsCapturing_AndResumeContinues()
    {
        RequireAlsa();
        using var input = AudioInputDevice.Open(new DeviceStreamOptions { DeviceId = "null", CaptureBufferMilliseconds = 100 }, new AlsaBackend());
        input.Start();
        Assert.True(Wait.Until(() => input.Counters.FramesWritten > 2000));
        input.Suspend();
        long frozen = input.Counters.FramesWritten;
        Thread.Sleep(100);
        Assert.Equal(frozen, input.Counters.FramesWritten);
        input.Resume();
        Assert.True(Wait.Until(() => input.Counters.FramesWritten > frozen + 2000), input.Counters.ToString());
    }

    [Fact]
    public void ThePlatformBackend_ListsInputs_AndOneCallCaptureWorks()
    {
        RequireAlsa();
        Assert.NotEmpty(AudioDevices.List(DeviceDirection.Input));
        using var input = Audio.CreateInput(new DeviceStreamOptions { DeviceId = "null", CaptureBufferMilliseconds = 100 });
        Assert.True(Wait.Until(() => input.Counters.FramesWritten > 2000), input.Counters.ToString());
        Assert.Equal("alsa", input.BackendName);
    }

    [Fact]
    public void RealDefaultInput_CapturesForASecondAtWallClockPace()
    {
        Assert.SkipUnless(Environment.GetEnvironmentVariable("ONEAUDIO_DEVICE_TESTS") == "1", "set ONEAUDIO_DEVICE_TESTS=1 to record from the real default input");
        RequireAlsa();
        using var input = AudioInputDevice.Open(new DeviceStreamOptions { CaptureBufferMilliseconds = 2000, Channels = 1, MeasureRenderAllocations = true });
        var wall = Stopwatch.StartNew();
        input.Start();
        Assert.SkipUnless(input.State == DeviceState.Running, "no default input device: " + input.LastError);
        Thread.Sleep(1200);
        var c = input.Counters;
        double seconds = wall.Elapsed.TotalSeconds;
        Assert.InRange(c.FramesWritten / (double)Rate, seconds - 0.4, seconds + 0.05);
        Assert.Equal(0, c.RenderAllocatedBytes);
        string? log = Environment.GetEnvironmentVariable("ONEAUDIO_DEVICE_TESTS_LOG");
        if (log is not null)
        {
            File.AppendAllText(log, $"capture {seconds:F3} s wall, {c}{Environment.NewLine}");
        }
    }
}
