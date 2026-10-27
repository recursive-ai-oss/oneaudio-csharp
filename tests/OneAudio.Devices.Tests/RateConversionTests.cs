using OneAudio.Devices;
using Xunit;

namespace OneAudio.Devices.Tests;

public class RateConversionTests
{
    // Counts upward zero crossings of the left channel between two frames and turns them into Hz.
    private static double Frequency(ReadOnlySpan<float> interleaved, int channels, int rate, int fromFrame, int toFrame)
    {
        int crossings = 0;
        for (int f = fromFrame + 1; f < toFrame; f++)
        {
            if (interleaved[(f - 1) * channels] < 0f && interleaved[f * channels] >= 0f)
            {
                crossings++;
            }
        }

        return crossings * (double)rate / (toFrame - fromFrame);
    }

    [Theory]
    [InlineData(44_100)]
    [InlineData(96_000)]
    public void ADeviceAtAnotherRate_GetsTheSourceConverted(int deviceRate)
    {
        var backend = new ManualBackend(recordFrames: deviceRate * 2, recordChannels: 2) { DeviceSampleRate = deviceRate };
        var tone = new ToneSource(48_000, 2, 0.5f);
        using var output = AudioOutputDevice.Open(tone, new DeviceStreamOptions { Limiter = OutputLimiterMode.Clamp }, backend);
        output.Start();
        Assert.Equal(DeviceState.Running, output.State);
        Assert.Equal(new AudioDeviceFormat(deviceRate, 2), output.DeviceFormat);
        Assert.Equal(new AudioDeviceFormat(48_000, 2), output.Format);

        Assert.Equal(deviceRate, output.Advance(deviceRate)); // one second at the device's rate
        var recorded = new float[deviceRate * 2];
        Assert.Equal(deviceRate * 2, backend.CopyRecording(recorded));

        // The length is exactly what the device asked for, the tone is still 440 Hz and the level is kept.
        double hz = Frequency(recorded, 2, deviceRate, deviceRate / 4, deviceRate * 3 / 4);
        Assert.InRange(hz, 438.0, 442.0);
        float peak = recorded.Skip(deviceRate / 4 * 2).Take(deviceRate).Max(MathF.Abs);
        Assert.InRange(peak, 0.49f, 0.51f);

        var c = output.Counters;
        Assert.Equal(deviceRate, c.FramesWritten);
        Assert.Equal(deviceRate, c.FramesPlayed);
        // The source is asked for what the device consumed, in chunks, at its own rate (a few chunks of latency and rounding).
        Assert.InRange(c.FramesRendered, 48_000 - 200, 48_000 + (2 * 512) + 200);
        Assert.Equal(c.FramesRendered, tone.Rendered);
    }

    [Fact]
    public void RateConversion_AllocatesNothingOnTheRenderPath()
    {
        var backend = new ManualBackend { DeviceSampleRate = 44_100 };
        var mixer = new AudioMixer(new MixerOptions { SampleRate = 48_000, Channels = 2, MaxVoices = 8 });
        var samples = new float[48_000];
        for (int i = 0; i < samples.Length; i++)
        {
            samples[i] = 0.3f * MathF.Sin(2f * MathF.PI * 330f * i / 48_000);
        }

        var clip = new AudioClip(new AudioFormat(48_000, 1), samples);
        using var output = AudioOutputDevice.Open(mixer, new DeviceStreamOptions { MeasureRenderAllocations = true, Metering = true }, backend);
        output.Start();
        VoiceHandle v = default;
        for (int i = 0; i < 300; i++)
        {
            if (i % 40 == 0)
            {
                v = mixer.Play(new PlayParams(clip) { Loop = true });
            }

            output.Advance(256);
        }

        long allocated = 0;
        for (int i = 0; i < 3000; i++)
        {
            if (i % 50 == 0)
            {
                v = mixer.Play(new PlayParams(clip) { Gain = 0.7f });
            }

            if (i % 70 == 69)
            {
                mixer.Stop(v, fadeOutFrames: 32);
            }

            long before = GC.GetAllocatedBytesForCurrentThread();
            output.Advance(256);
            allocated += GC.GetAllocatedBytesForCurrentThread() - before;
        }

        Assert.Equal(0, allocated);
        Assert.Equal(0, output.Counters.RenderAllocatedBytes);
        Assert.True(output.Counters.PeakPlayed > 0.1f);
        Assert.True(output.Loudness.MomentaryLufs > double.NegativeInfinity); // the meter runs at the device's rate
    }

    [Fact]
    public void WithConversionOff_AMismatchedDeviceIsRefusedWithAReason()
    {
        var backend = new ManualBackend { DeviceSampleRate = 44_100 };
        using var output = AudioOutputDevice.Open(new ToneSource(48_000, 2, 0.5f), new DeviceStreamOptions { AllowRateConversion = false }, backend);
        output.Start();
        Assert.Equal(DeviceState.NoDevice, output.State);
        Assert.Contains("AllowRateConversion", output.LastError);
    }

    [Fact]
    public void ARateTooFarAway_IsRefusedWithAReason()
    {
        var backend = new ManualBackend { DeviceSampleRate = 384_000 };
        using var output = AudioOutputDevice.Open(new ToneSource(8_000, 2, 0.5f), null, backend);
        output.Start();
        Assert.Equal(DeviceState.NoDevice, output.State);
        Assert.Contains("too far", output.LastError);
    }

    [Fact]
    public void ConversionSurvivesAReopen()
    {
        var backend = new ManualBackend { DeviceSampleRate = 44_100 };
        using var output = AudioOutputDevice.Open(new ToneSource(48_000, 2, 0.5f), null, backend);
        output.Start();
        output.Advance(4410);
        backend.SimulateLoss();
        Assert.Equal(4410, output.Advance(4410));
        var c = output.Counters;
        Assert.Equal(1, c.Reopens);
        Assert.Equal(8820, c.FramesWritten);
        Assert.InRange(c.PeakPlayed, 0.4f, 0.51f);
    }
}
