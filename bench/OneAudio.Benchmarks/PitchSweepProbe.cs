using System.Diagnostics;

namespace OneAudio.Benchmarks;

/// <summary>
/// Worst-case render time while many voices bend pitch together: every voice's ratio moves on every pass, up through an
/// octave and back, which is what makes a polyphase converter redesign its filter bank. Run with
/// <c>OneAudio.Benchmarks pitch-sweep</c>; it prints per-callback render time (1024 frames, a 21.3 ms deadline at 48 kHz).
/// </summary>
internal static class PitchSweepProbe
{
    public static int Run()
    {
        Console.WriteLine($"pitch sweep, 1024-frame callbacks at 48 kHz, stereo voices, ratio 1.0 <-> 0.5 each pass");
        Console.WriteLine("quality          voices  budget/pass   mean ms   p99 ms  p99.9 ms   max ms");
        foreach (int voices in new[] { 16, 64 })
        {
            foreach (var (q, budget) in new[]
            {
                (ResamplerQuality.Linear, 2),
                (ResamplerQuality.PolyphaseFast, 2), (ResamplerQuality.PolyphaseFast, int.MaxValue),
                (ResamplerQuality.Polyphase, 2), (ResamplerQuality.Polyphase, int.MaxValue),
            })
                Measure(voices, q, budget);
        }
        return 0;
    }

    private static void Measure(int voices, ResamplerQuality quality, int budget)
    {
        var m = new AudioMixer(new MixerOptions
        {
            SampleRate = 48000, Channels = 2, MaxVoices = voices, MaxBuses = 2, MaxBlockFrames = 1024,
            RatioRedesignsPerPass = budget,
            PreallocatedConverters = [new ConverterReservation(2, quality, voices)],
        });
        var rng = new Random(3);
        var samples = new float[2 * 48000];
        for (int i = 0; i < samples.Length; i++) samples[i] = (float)(rng.NextDouble() * 2 - 1) * 0.05f;
        var clip = new AudioClip(new AudioFormat(48000, 2), samples);
        var handles = new VoiceHandle[voices];
        for (int i = 0; i < voices; i++)
            handles[i] = m.Play(new PlayParams(clip) { Loop = true, VariablePitch = true, Resampling = quality, Gain = 0.2f });
        var o = new float[2 * 1024];
        for (int i = 0; i < 300; i++) m.Render(o); // warm-up: JIT and filter tables

        const int Cycle = 200;
        const int Passes = Cycle * 10;
        var times = new double[Passes];
        for (int p = 0; p < Passes; p++)
        {
            double phase = (p % (2 * Cycle)) / (double)Cycle;
            float pitch = (float)(1.0 + (phase < 1 ? phase : 2 - phase));
            foreach (var h in handles) m.SetPitch(h, pitch);
            long t0 = Stopwatch.GetTimestamp();
            m.Render(o);
            times[p] = (Stopwatch.GetTimestamp() - t0) * 1000.0 / Stopwatch.Frequency;
        }
        Array.Sort(times);
        string b = budget == int.MaxValue ? "unlimited" : budget.ToString();
        Console.WriteLine($"{quality,-16} {voices,6}  {b,11}  {times.Average(),8:F3} {times[(int)(Passes * 0.99)],8:F3} {times[(int)(Passes * 0.999)],9:F3} {times[^1],8:F3}");
    }
}
