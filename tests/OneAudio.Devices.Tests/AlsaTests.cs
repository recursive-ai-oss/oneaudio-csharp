using System.Diagnostics;
using OneAudio.Devices;
using OneAudio.Devices.Interop;
using Xunit;

namespace OneAudio.Devices.Tests;

/// <summary>
/// The ALSA backend against alsa-lib's own <c>file</c> and <c>null</c> PCMs, which need no sound card: they consume every frame at once, so
/// the device side of the hand-over (frames played, bytes on disk) can be checked without a speaker. A short, very quiet playback on the
/// real default device runs only when ONEAUDIO_DEVICE_TESTS=1.
/// </summary>
public class AlsaTests
{
    private const int Rate = 48_000;

    private static void RequireAlsa() =>
        Assert.SkipUnless(OperatingSystem.IsLinux() && AlsaNative.IsPresent, "libasound is not available");

    private static string TempDir()
    {
        string dir = Path.Combine(Path.GetTempPath(), "oneaudio-alsa-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(dir);
        return dir;
    }

    [Fact]
    public void FilePcm_ReceivesTheSourceBitExact_AndCountersComeFromTheDevicePosition()
    {
        RequireAlsa();
        string dir = TempDir();
        string file = Path.Combine(dir, "out.raw");
        try
        {
            var tone = new ToneSource(Rate, 2, 0.5f);
            var options = new DeviceStreamOptions { DeviceId = $"file:{file},raw", Limiter = OutputLimiterMode.Clamp, MeasureRenderAllocations = true };
            using (var output = AudioOutputDevice.Open(tone, options, new AlsaBackend()))
            {
                output.Start();
                Assert.Equal(DeviceState.Running, output.State);
                Assert.True(Wait.Until(() => output.Counters.FramesPlayed > 24_000), "the device never took the tone: " + output.Counters);
                var c = output.Counters;
                Assert.True(c.FramesWritten >= c.FramesPlayed);
                Assert.InRange(c.PeakPlayed, 0.49f, 0.5f);
                Assert.Equal(0, c.Underruns);
                Assert.Equal(0, c.CallbackFaults);
                Assert.Equal(0, c.RenderAllocatedBytes);
                Assert.True(c.PeriodFrames > 0 && c.BufferFrames >= c.PeriodFrames, c.ToString());
                Assert.Equal("alsa", output.BackendName);
                Assert.Equal($"file:{file},raw", output.Device?.Id);
            }

            // Everything the device took is on disk, as the stereo float stream the engine produced.
            byte[] bytes = File.ReadAllBytes(file);
            Assert.True(bytes.Length >= 24_000 * 8, $"only {bytes.Length} bytes reached the file");
            int frames = bytes.Length / 8;
            Assert.True(frames <= tone.Rendered);
            var twin = new ToneSource(Rate, 2, 0.5f);
            var expected = new float[frames * 2];
            twin.Render(expected);
            var got = new float[frames * 2];
            Buffer.BlockCopy(bytes, 0, got, 0, frames * 8);
            Assert.Equal(expected, got);
        }
        finally
        {
            Directory.Delete(dir, true);
        }
    }

    [Fact]
    public void NullPcm_AcceptsAMixer_ThroughTheOneCallLayer()
    {
        RequireAlsa();
        var mixer = new AudioMixer(new MixerOptions { SampleRate = Rate, Channels = 2 });
        var samples = new float[Rate];
        for (int i = 0; i < Rate; i++)
        {
            samples[i] = 0.3f * MathF.Sin(2f * MathF.PI * 330f * i / Rate);
        }

        mixer.Play(new PlayParams(new AudioClip(new AudioFormat(Rate, 1), samples)) { Loop = true });
        using var output = Audio.Play(mixer, new DeviceStreamOptions { DeviceId = "null", MeasureRenderAllocations = true });
        Assert.True(Wait.Until(() => output.Counters.FramesPlayed > 20_000), "nothing played: " + output.Counters);
        var c = output.Counters;
        Assert.True(c.PeakPlayed > 0.1f, c.ToString());
        Assert.Equal(0, c.Underruns);
        Assert.Equal(0, c.RenderAllocatedBytes);
        Assert.Equal("alsa", output.BackendName);
    }

    [Fact]
    public void ExplicitPeriodAndBuffer_AreNegotiated()
    {
        RequireAlsa();
        var options = new DeviceStreamOptions { DeviceId = "null", PeriodFrames = 480, BufferFrames = 1920 };
        using var output = AudioOutputDevice.Open(new ToneSource(Rate, 2, 0.1f), options, new AlsaBackend());
        output.Start();
        Assert.Equal(DeviceState.Running, output.State);
        var c = output.Counters;
        Assert.Equal(480, c.PeriodFrames);
        Assert.Equal(1920, c.BufferFrames);
        Assert.True(Wait.Until(() => output.Counters.FramesPlayed > 5000));
    }

    [Fact]
    public void SuspendDrainsTheDevice_AndResumeContinuesCounting()
    {
        RequireAlsa();
        using var output = AudioOutputDevice.Open(new ToneSource(Rate, 2, 0.1f), new DeviceStreamOptions { DeviceId = "null" }, new AlsaBackend());
        output.Start();
        Assert.True(Wait.Until(() => output.Counters.FramesPlayed > 5000));
        output.Suspend();
        var frozen = output.Counters;
        Assert.Equal(DeviceState.Suspended, output.State);
        Assert.Equal(frozen.FramesWritten, frozen.FramesPlayed); // drained: everything written was played
        Thread.Sleep(100);
        Assert.Equal(frozen.FramesPlayed, output.Counters.FramesPlayed);
        output.Resume();
        Assert.True(Wait.Until(() => output.Counters.FramesPlayed > frozen.FramesPlayed + 5000), output.Counters.ToString());
    }

    [Fact]
    public void Enumeration_ListsTheDefaultDevice_AndFollowsAlsaNames()
    {
        RequireAlsa();
        var backend = new AlsaBackend();
        var list = backend.List(DeviceDirection.Output);
        Assert.Contains(list, d => d.Id == "default" && d.IsDefault && d.Direction == DeviceDirection.Output);
        Assert.DoesNotContain(list, d => d.Id == "null");
        Assert.Equal("default", backend.GetDefault(DeviceDirection.Output)?.Id);
        Assert.NotEmpty(backend.List(DeviceDirection.Input));
        Assert.Equal(DeviceBackendKind.Alsa, list[0].Backend);
        Assert.True(backend.IsAvailable);
    }

    [Fact]
    public void UnknownDevice_FallsBackToSilence_WithTheReason()
    {
        RequireAlsa();
        var tone = new ToneSource(Rate, 2, 0.1f);
        using var output = AudioOutputDevice.Open(tone, new DeviceStreamOptions { DeviceId = "no-such-device" }, new AlsaBackend());
        output.Start();
        Assert.Equal(DeviceState.NoDevice, output.State);
        Assert.Contains("no-such-device", output.LastError);
        Thread.Sleep(300);
        Assert.True(output.Counters.SilentFrames > Rate / 10, output.Counters.ToString());
    }

    [Fact]
    public void ExclusiveMode_IsRefusedWithAReason()
    {
        RequireAlsa();
        using var output = AudioOutputDevice.Open(new ToneSource(Rate, 2, 0.1f), new DeviceStreamOptions { DeviceId = "null", Mode = ShareMode.Exclusive }, new AlsaBackend());
        output.Start();
        Assert.Equal(DeviceState.NoDevice, output.State);
        Assert.Contains("exclusive", output.LastError);
    }

    [Fact]
    public void AFailingDevice_IsReportedLost_AndTheEngineKeepsReopening()
    {
        RequireAlsa();
        Assert.SkipUnless(File.Exists("/dev/full"), "/dev/full is not available");

        // Writes to /dev/full fail with ENOSPC, which stands in for a device that goes away mid-stream.
        var engine = new RecordingEngine();
        Assert.True(new AlsaBackend().TryOpen(DeviceDirection.Output, new DeviceStreamOptions { DeviceId = "file:/dev/full,raw" }, engine, out var stream, out string reason), reason);
        using (stream)
        {
            Assert.True(stream!.Start(out reason), reason);
            Assert.True(Wait.Until(() => engine.Lost != null), "the loss was never reported");
            Assert.False(stream.Start(out _)); // a lost stream cannot restart
        }

        using var output = AudioOutputDevice.Open(new ToneSource(Rate, 2, 0.1f), new DeviceStreamOptions { DeviceId = "file:/dev/full,raw" }, new AlsaBackend());
        output.Start();
        Assert.True(Wait.Until(() => output.Counters.Reopens >= 2), output.Counters.ToString());
        Assert.True(output.Counters.DeviceLosses >= 2);
    }

    [Fact]
    public void SixteenBitConversion_RoundsToNearest_AndNeverWraps()
    {
        float[] input = [0f, 1f, -1f, 0.5f, -0.5f, 1.5f, -1.5f, 1f / 32767f];
        var output = new short[input.Length];
        AlsaStream.ToInt16(input, output);
        Assert.Equal(new short[] { 0, 32767, -32767, 16384, -16383, 32767, -32768, 1 }, output);
    }

    [Fact]
    public void RealDefaultDevice_PlaysAQuietToneForASecondAndAHalf()
    {
        Assert.SkipUnless(Environment.GetEnvironmentVariable("ONEAUDIO_DEVICE_TESTS") == "1", "set ONEAUDIO_DEVICE_TESTS=1 to play on the real default device");
        RequireAlsa();
        // -50 dBFS: present to the counters, barely audible.
        var tone = new ToneSource(Rate, 2, 0.003f);
        using var output = AudioOutputDevice.Open(tone, new DeviceStreamOptions { MeasureRenderAllocations = true, LatencyMilliseconds = 40 });
        var wall = Stopwatch.StartNew();
        output.Start();
        Assert.Equal(DeviceState.Running, output.State);
        Thread.Sleep(1500);
        var c = output.Counters;
        double seconds = wall.Elapsed.TotalSeconds;
        output.Dispose();

        Assert.True(c.FramesPlayed > Rate * 0.8, c.ToString());
        Assert.InRange(c.FramesPlayed / (double)Rate, seconds - 0.4, seconds + 0.05); // the device clock keeps wall-clock pace
        Assert.InRange(c.PeakPlayed, 0.002f, 0.0031f);
        Assert.Equal(0, c.Underruns);
        Assert.Equal(0, c.RenderAllocatedBytes);
        Assert.Equal(0, c.CallbackFaults);
        Assert.InRange(c.LatencyMilliseconds, 1, 200);
        string? log = Environment.GetEnvironmentVariable("ONEAUDIO_DEVICE_TESTS_LOG");
        if (log is not null)
        {
            File.AppendAllText(log, $"{seconds:F3} s wall, {c}{Environment.NewLine}");
        }
    }

    private sealed class RecordingEngine : IDeviceEngine
    {
        public string? Lost;

        public void Render(Span<float> interleaved) => interleaved.Clear();

        public void Capture(ReadOnlySpan<float> interleaved)
        {
        }

        public void NotifyLost(string? reason) => Lost = reason ?? "lost";

        public void NotifyXrun(int frames)
        {
        }

        public void NotifyDefaultChanged()
        {
        }

        public void NotifyDevicesChanged()
        {
        }

        public void NotifyInterruption(bool began)
        {
        }
    }
}
