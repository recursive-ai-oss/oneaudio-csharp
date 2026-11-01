using OneAudio.Aac;
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
        byte[] file = File.ReadAllBytes(AacTestEnv.Mp4(name));
        var buf = new float[4096 * 2];
        // First pass over the whole stream: JIT, first-use tables, every tool the stream uses. The runtime also promotes hot
        // methods to optimized code on a timer, and that transition can allocate on the calling thread, so wait for it.
        for (int pass = 0; pass < 2; pass++)
        {
            using var warm = AudioDecoder.Open(new MemoryStream(file), AacTestEnv.Registry);
            while (warm.Read(buf) > 0) { }
            Thread.Sleep(250);
        }
        // Second pass: opening allocates (buffers, tables); the frames after that must not.
        using var d = AudioDecoder.Open(new MemoryStream(file), AacTestEnv.Registry);
        int channels = d.Format.Channels;
        long before = GC.GetAllocatedBytesForCurrentThread();
        long frames = 0;
        int n;
        while ((n = d.Read(buf.AsSpan(0, 1000 * channels))) > 0) frames += n;
        long bytes = GC.GetAllocatedBytesForCurrentThread() - before;
        AacTestEnv.Row($"| {name} | allocation | {frames} frames | {bytes} B | - | - | - | {(bytes == 0 ? "pass" : "FAIL")} |");
        Assert.True(frames > 100_000);
        Assert.Equal(0, bytes);
    }

    // A hostile stream can make the AAC core produce arbitrarily large samples (a huge global gain); the SBR energy sums
    // used to overflow to infinity and the output became NaN (found by the fuzzer).
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void HugeCoreSamplesGiveFiniteOutput(bool parametricStereo)
    {
        var sbr = new SbrDecoder(22050, 1, false, parametricStereo);
        var core = new float[1024];
        for (int i = 0; i < core.Length; i++) core[i] = (i & 1) == 0 ? float.MaxValue : -float.MaxValue;
        var output = new float[sbr.OutputFrame * sbr.OutputChannels];
        for (int frame = 0; frame < 4; frame++)
        {
            sbr.Process(core, output);
            foreach (float v in output) Assert.True(float.IsFinite(v));
        }
    }
}
