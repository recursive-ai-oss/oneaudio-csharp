using Xunit;

namespace OneAudio.Tests;

/// <summary>Seeking, steady-state allocation and errors for HE-AAC, on ISO/IEC 14496-26 streams (skipped when not fetched).</summary>
public class AacHeAacBehaviorTests
{
    // mono SBR, stereo SBR, parametric stereo with and without the phase parameters
    public static TheoryData<string> Streams() => new() { "al_sbr_e_48_1", "al_sbr_e_44_2", "al_sbr_ps_00", "al_sbr_ps_03" };

    [Theory]
    [MemberData(nameof(Streams))]
    public void SeekingLandsOnTheTargetFrameAndStaysCloseToALinearDecode(string name)
    {
        var all = AacTestEnv.DecodeAll(AacTestEnv.Mp4(name), out var fmt, out _, out long length);
        int c = fmt.Channels;
        using var f = File.OpenRead(AacTestEnv.Mp4(name));
        using var d = AudioDecoder.Open(f, AacTestEnv.Registry, leaveOpen: true);
        Assert.True(d.CanSeek);
        var buf = new float[8192 * c];
        foreach (long target in new long[] { 0, 1, 2047, 2048, 5000, length / 2 + 77, length - 3000 })
        {
            d.Seek(target);
            Assert.Equal(target, d.PositionFrames);
            int got = d.Read(buf);
            int expect = (int)Math.Min(8192, length - target);
            Assert.Equal(expect, got);
            // The first frames after a seek come from rebuilt filter bank history and a noise generator that did not
            // run on from the start, so only the part after one SBR frame (2048 samples) is held to a tight bound.
            double worst = 0;
            for (int i = Math.Min(2048, got) * c; i < got * c; i++)
                worst = Math.Max(worst, Math.Abs(buf[i] - all[target * c + i]));
            Assert.True(worst < 2e-2, $"{name} seek to {target}: max difference {worst:E2}");
            if (target <= 1) Assert.True(worst == 0 || got <= 2048 || worst < 1e-6);
        }
        d.Seek(length);
        Assert.Equal(0, d.Read(buf));
    }

    [Theory]
    [MemberData(nameof(Streams))]
    public void SteadyStateDecodingAllocatesNothing(string name)
    {
        using var f = File.OpenRead(AacTestEnv.Mp4(name));
        using var d = AudioDecoder.Open(f, AacTestEnv.Registry, leaveOpen: true);
        var buf = new float[1000 * d.Format.Channels];
        for (int i = 0; i < 60; i++) Assert.True(d.Read(buf) > 0); // warm-up: JIT, first-frame tables
        long before = GC.GetAllocatedBytesForCurrentThread();
        int reads = 0;
        for (; reads < 300; reads++) if (d.Read(buf) == 0) break;
        long bytes = GC.GetAllocatedBytesForCurrentThread() - before;
        AacTestEnv.Row($"| {name} | allocation | {reads} reads | {bytes} B | - | - | - | {(bytes == 0 ? "pass" : "FAIL")} |");
        Assert.True(reads >= 100);
        Assert.Equal(0, bytes);
    }
}
