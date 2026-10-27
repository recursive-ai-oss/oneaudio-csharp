using System.Runtime.CompilerServices;
using OneAudio.Effects;

namespace OneAudio.Mixing;

/// <summary>
/// Runs a small throwaway mixer through every render-path feature once, so that the work a bytecode interpreter does the first
/// time it executes a method (it materialises runtime type objects and generic instantiations on the managed heap) is spent at
/// creation time on the creating thread, not on the render thread's first block that happens to reach the feature.
/// </summary>
/// <remarks>
/// Needed only where code is interpreted: on the Mono runtime for browser-wasm a render callback otherwise allocates a few
/// hundred bytes the first time a voice starts, a filter is switched on, a resampler runs, and so on. Compiled runtimes (CoreCLR's
/// JIT, NativeAOT) pay nothing for first use on the heap, so they skip it. Once per process: the objects the interpreter creates are
/// cached by the runtime and shared by every mixer.
/// </remarks>
internal static class MixerWarmUp
{
    private static int s_state; // 0 = not run, 1 = running or done

    /// <summary>True when code is interpreted: dynamic code is supported (so not NativeAOT) but not compiled (so not a JIT).</summary>
    internal static bool Needed => RuntimeFeature.IsDynamicCodeSupported && !RuntimeFeature.IsDynamicCodeCompiled;

    /// <summary>Warms up if this runtime needs it and nobody has yet. Called from the mixer constructor.</summary>
    internal static void RunIfNeeded()
    {
        if (Needed) Run(force: false);
    }

    /// <summary>Runs the warm-up scenario. <paramref name="force"/> repeats it even if it already ran (for tests).</summary>
    internal static void Run(bool force)
    {
        if (Interlocked.Exchange(ref s_state, 1) != 0 && !force) return;

        const int block = 128;
        var mixer = new AudioMixer(new MixerOptions
        {
            SampleRate = 48_000, Channels = 2, MaxVoices = 5, MaxBuses = 3, MaxSourceChannels = 6, MaxBlockFrames = block,
            CommandCapacity = 64, EventCapacity = 32, MaxReverbs = 1, MaxBusMeters = 2, LimiterLookaheadMilliseconds = 1,
        });
        var output = new float[block * 2];
        void Pass(int blocks = 3)
        {
            for (int i = 0; i < blocks; i++) mixer.Render(output);
        }

        // Short clips, so that loops wrap and voices run off their ends within a few blocks.
        var mono = Tone(48_000, 1, 400);
        var stereo = Tone(48_000, 2, 400);
        var surround = Tone(48_000, 6, 300);
        var slow = Tone(24_000, 2, 400);
        var odd = Tone(44_100, 1, 400);
        Pass();

        // Plain voices: direct read, gain and pan ramps, channel folding, loops, delayed start.
        var v1 = mixer.Play(new PlayParams(mono) { Pan = -0.5f, Loop = true, LoopStartFrame = 16, LoopEndFrame = 200 });
        var v2 = mixer.Play(new PlayParams(stereo) { Gain = 0.5f, Bus = 1, FadeInFrames = 64, Loop = true });
        Pass();
        mixer.SetGain(v1, 0.2f, 100); mixer.SetPan(v1, 0.7f, 100); mixer.SetGain(v2, 0.8f); mixer.SetPan(v2, -0.3f);
        var v3 = mixer.Play(new PlayParams(surround) { Bus = 2, StartAtFrame = mixer.TimelineFrames + 100 });
        Pass();
        mixer.SetLoop(v1, false);
        Pass(4);

        // Converted voices: both filter designs and the linear converter, a glide, a ratio change, running off the end.
        var v4 = mixer.Play(new PlayParams(slow) { Resampling = ResamplerQuality.PolyphaseFast, Loop = true });
        var v5 = mixer.Play(new PlayParams(odd) { Resampling = ResamplerQuality.Linear, VariablePitch = true, Loop = true });
        mixer.SetPitch(v5, 1.5f, 200);
        Pass(4);
        mixer.SetPitch(v5, 0.9f, 0);
        mixer.SetLoop(v5, false);
        Pass(6);
        // Run each converter off the end of its source, which takes the flush path.
        mixer.Play(new PlayParams(slow) { Resampling = ResamplerQuality.Polyphase });
        mixer.Play(new PlayParams(slow) { Resampling = ResamplerQuality.PolyphaseFast });
        mixer.Play(new PlayParams(odd) { Resampling = ResamplerQuality.Linear });
        Pass(12);

        // Buses and effects, with audio flowing through every bus involved, then ducking, metering and the limiter.
        mixer.SetBusGain(1, 0.5f, 64); mixer.SetBusMute(2, true); mixer.SetMasterGain(0.9f);
        mixer.SetBusFilter(1, FilterType.LowPass, 4000f); mixer.SetBusFilter(AudioMixer.MasterBus, FilterType.HighPass, 80f);
        mixer.SetVoiceFilter(v4, FilterType.LowPass, 3000f); mixer.SetVoiceFilter(v4, FilterType.HighPass, 100f);
        mixer.SetBusReverb(0, new ReverbSettings()); mixer.SetBusReverb(0, new ReverbSettings { Wet = 0.4f }, 64);
        mixer.SetDucking(0, 1);
        mixer.EnableBusMeter(0); mixer.EnableBusMeter(AudioMixer.MasterBus);
        mixer.SetLimiter(new LimiterSettings());
        mixer.Play(new PlayParams(mono) { Loop = true });
        Pass(8);
        // The loudness meters only reach their gated stages after a few hundred milliseconds of audio.
        Pass(220);
        mixer.SetBusMute(2, false); mixer.SetBusOutput(1, 2); mixer.SetBusGain(1, 0f, 0); mixer.SetBusMute(0, true);
        Pass(4);
        mixer.SetBusMute(0, false); mixer.SetBusGain(1, 1f);
        mixer.ClearVoiceFilter(v4, FilterType.LowPass); mixer.ClearBusFilter(1, FilterType.LowPass);
        mixer.ResetBusMeter(0);
        Pass(4);

        // A crossfade, stops with and without a fade, and steals by priority.
        mixer.Crossfade(v1, new PlayParams(mono), 100);
        mixer.Stop(v2, 64);
        mixer.Stop(v3);
        Pass(4);
        for (int i = 0; i < 7; i++) mixer.Play(new PlayParams(stereo) { Priority = i, Resampling = ResamplerQuality.Linear });
        Pass(4);

        // A decoded-ahead stream fed from this thread.
        using (var stream = new StreamingSource(new ToneDecoder(), capacityFrames: 1024, loop: true))
        {
            stream.Pump();
            mixer.Play(new PlayParams(stream));
            Pass();
            stream.Pump();
            Pass();
        }

        mixer.ClearBusReverb(0); mixer.ClearDucking(0); mixer.SetLimiter(null);
        mixer.StopAll(64);
        Pass(4);
        while (mixer.TryGetEvent(out _)) { }
        mixer.TryGetBusLoudness(0, out _);
        mixer.DisableBusMeter(0);
        Pass(8);
    }

    private static AudioClip Tone(int rate, int channels, int frames)
    {
        var samples = new float[frames * channels];
        for (int i = 0; i < frames; i++)
            for (int c = 0; c < channels; c++)
                samples[(i * channels) + c] = 0.25f * MathF.Sin(i * 0.05f * (c + 1));
        return new AudioClip(new AudioFormat(rate, channels), samples);
    }

    // A seekable endless-enough source for the streaming path.
    private sealed class ToneDecoder : IAudioDecoder
    {
        private long _position;

        public AudioFormat Format { get; } = new(48_000, 2);
        public long LengthFrames => 4096;
        public long PositionFrames => _position;
        public bool CanSeek => true;
        public bool HasNativeIntegers => false;

        public int Read(Span<float> destination)
        {
            int frames = (int)Math.Min(destination.Length / 2, LengthFrames - _position);
            for (int i = 0; i < frames; i++)
            {
                float s = 0.2f * MathF.Sin((_position + i) * 0.03f);
                destination[2 * i] = s;
                destination[(2 * i) + 1] = -s;
            }
            _position += frames;
            return frames;
        }

        public int ReadInteger(Span<int> destination) => throw new NotSupportedException();
        public void Seek(long frame) => _position = Math.Clamp(frame, 0, LengthFrames);
        public void Dispose() { }
    }
}
