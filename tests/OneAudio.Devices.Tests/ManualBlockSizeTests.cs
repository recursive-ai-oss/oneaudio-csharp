using OneAudio.Devices;
using Xunit;

namespace OneAudio.Devices.Tests;

/// <summary>Regression tests for hosts that step the device themselves in 480-frame blocks and whose streams cannot name their device.</summary>
public class ManualBlockSizeTests
{
    /// <summary>A manual backend whose streams report no device (like a browser context) and can carry a state word.</summary>
    private sealed class UnnamedBackend : IAudioDeviceBackend
    {
        public int Opens;
        public string? State;

        public string Name => "unnamed";

        public DeviceBackendKind Kind => DeviceBackendKind.Custom;

        public bool IsManual => true;

        public bool NotifiesDeviceChanges => false; // the engine polls GetDefault

        public bool IsAvailable => true;

        public IReadOnlyList<AudioDeviceInfo> List(DeviceDirection direction) => [Default(direction)];

        public AudioDeviceInfo? GetDefault(DeviceDirection direction) => Default(direction);

        private static AudioDeviceInfo Default(DeviceDirection direction) =>
            new("page-default", "Page default", direction, true, 48_000, 2, 128, DeviceBackendKind.Custom);

        public bool TryOpen(DeviceDirection direction, DeviceStreamOptions options, IDeviceEngine engine, out IOpenDevice? stream, out string reason)
        {
            Opens++;
            reason = string.Empty;
            stream = new Stream(this, engine, options);
            return true;
        }

        private sealed class Stream(UnnamedBackend owner, IDeviceEngine engine, DeviceStreamOptions options) : IOpenDevice, IManualDevice
        {
            private readonly float[] _block = new float[480 * options.Channels];
            private long _frames;

            public AudioDeviceFormat Format { get; } = new(options.SampleRate, options.Channels);

            public AudioDeviceInfo? Device => null;

            public int PeriodFrames => 480;

            public int BufferFrames => 960;

            public long FramesPlayed => Volatile.Read(ref _frames);

            public int QueuedFrames => 0;

            public string? Detail => "page";

            public string? StateText => owner.State;

            public bool Start(out string reason)
            {
                reason = string.Empty;
                return true;
            }

            public void Pause()
            {
            }

            public void Dispose()
            {
            }

            public int Advance(int frames)
            {
                int done = 0;
                while (done < frames)
                {
                    int n = Math.Min(480, frames - done);
                    engine.Render(_block.AsSpan(0, n * options.Channels));
                    _frames += n;
                    done += n;
                }

                return done;
            }
        }
    }

    [Theory]
    [InlineData(0)]      // the device runs at the rate the program renders at
    [InlineData(44_100)] // rate conversion runs
    public void ManualAdvance_With480FrameBlocks_ReadsZeroAllocatedBytes(int deviceRate)
    {
        var backend = new ManualBackend { DeviceSampleRate = deviceRate };
        var mixer = new AudioMixer(new MixerOptions { SampleRate = 48_000, Channels = 2, MaxVoices = 8 });
        var samples = new float[48_000];
        for (int i = 0; i < samples.Length; i++)
        {
            samples[i] = 0.3f * MathF.Sin(2f * MathF.PI * 330f * i / 48_000);
        }

        var clip = new AudioClip(new AudioFormat(48_000, 1), samples);
        using var output = AudioOutputDevice.Open(mixer, new DeviceStreamOptions { Metering = true }, backend);
        output.Start();
        mixer.Play(new PlayParams(clip) { Loop = true });
        for (int i = 0; i < 2000; i++)
        {
            output.Advance(480);
        }

        var c = output.Counters;
        Assert.Equal(0, c.RenderAllocatedBytes);
        Assert.Equal(0, c.CallbackFaults);
        Assert.True(c.FramesWritten >= 480 * 2000);
    }

    [Fact]
    public void AllocationWarmupBlocks_DecidesWhichFirstUseAllocationsAreCounted()
    {
        static long Run(int warmup)
        {
            var source = new LateAllocatingSource(allocateAtCall: 20);
            using var output = AudioOutputDevice.Open(source, new DeviceStreamOptions { AllocationWarmupBlocks = warmup }, new ManualBackend());
            output.Start();
            for (int i = 0; i < 100; i++)
            {
                output.Advance(256);
            }

            return output.Counters.RenderAllocatedBytes;
        }

        Assert.True(Run(warmup: 8) >= 1280);   // the default counts an allocation on the 20th callback
        Assert.Equal(0, Run(warmup: 30));      // a longer warm-up passes it over
        Assert.True(Run(warmup: 0) >= 1280);
        Assert.Throws<ArgumentOutOfRangeException>(() => AudioOutputDevice.Open(new ToneSource(48_000, 2, 0.1f), new DeviceStreamOptions { AllocationWarmupBlocks = -1 }, new ManualBackend()));
    }

    private sealed class LateAllocatingSource(int allocateAtCall) : IAudioSource
    {
        private int _calls;
        private object? _sink;

        public int SampleRate => 48_000;

        public int Channels => 2;

        public void Render(Span<float> output)
        {
            output.Clear();
            if (++_calls == allocateAtCall)
            {
                _sink = new byte[1280];
            }
        }
    }

    [Fact]
    public void ADeviceThatCannotNameItself_IsNeverTakenForADefaultSwitch()
    {
        var backend = new UnnamedBackend();
        using var output = AudioOutputDevice.Open(new ToneSource(48_000, 2, 0.2f), null, backend);
        output.Start();
        Assert.Equal(DeviceState.Running, output.State);
        Assert.Null(output.Device);

        // The engine polls the default about once a second; cover several polls.
        var deadline = System.Diagnostics.Stopwatch.StartNew();
        int ticks = 0;
        while (deadline.ElapsedMilliseconds < 2300)
        {
            output.Advance(480);
            output.Pump();
            ticks++;
            Thread.Sleep(2);
        }

        var c = output.Counters;
        Assert.True(ticks > 100);
        Assert.Equal(1, backend.Opens);
        Assert.Equal(0, c.DefaultDeviceSwitches);
        Assert.Equal(0, c.Reopens);
        Assert.Equal(0, c.RenderAllocatedBytes);
    }

    [Fact]
    public void StateText_IsReportedAlone_AndInDetail()
    {
        var backend = new UnnamedBackend();
        using var output = AudioOutputDevice.Open(new ToneSource(48_000, 2, 0.2f), null, backend);
        Assert.Null(output.StateText);
        output.Start();
        Assert.Null(output.StateText);
        Assert.Equal("page", output.Detail);

        backend.State = "awaiting_gesture";
        Assert.Equal("awaiting_gesture", output.StateText);
        Assert.Equal("page; awaiting_gesture", output.Detail);

        backend.State = null;
        Assert.Null(output.StateText);
        Assert.Equal("page", output.Detail);

        // A stream that does not implement it (the manual backend's) reports none and leaves Detail alone.
        using var plain = AudioOutputDevice.Open(new ToneSource(48_000, 2, 0.2f), null, new ManualBackend());
        plain.Start();
        Assert.Null(plain.StateText);
        Assert.Equal("manual", plain.Detail);
    }
}
