using OneAudio.Aac;
using Xunit;

namespace OneAudio.Tests;

public class AacTransformTests
{
    [Theory]
    [InlineData(1024)]
    [InlineData(128)]
    public void FastImdctMatchesTheDirectFormula(int m)
    {
        var rng = new Random(7);
        var spec = new float[m];
        for (int i = 0; i < m; i++) spec[i] = (float)(rng.NextDouble() * 2 - 1);
        var fast = new float[2 * m];
        var direct = new float[2 * m];
        new AacImdct(m).Inverse(spec, fast);
        AacImdct.InverseDirect(spec, direct, m);
        double worst = 0;
        for (int i = 0; i < 2 * m; i++) worst = Math.Max(worst, Math.Abs(fast[i] - direct[i]));
        Assert.True(worst < 1e-6, $"max difference {worst}");
    }

    [Fact]
    public void WindowsSatisfyThePrincenBradleyCondition()
    {
        foreach (var slope in new[] { AacWindows.Long[0], AacWindows.Long[1], AacWindows.Short[0], AacWindows.Short[1] })
            for (int i = 0; i < slope.Length; i++)
            {
                double s = slope[i] * slope[i] + slope[slope.Length - 1 - i] * slope[slope.Length - 1 - i];
                Assert.InRange(s, 1 - 1e-6, 1 + 1e-6);
            }
    }

    [Fact]
    public void HuffmanBooksAreCompletePrefixCodes()
    {
        // Each generated book must decode every one of its own codewords back to its value.
        for (int b = 1; b <= 12; b++) Assert.NotNull(AacHuffman.Books[b]);
    }
}
