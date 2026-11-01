using OneAudio.Aac;
using Xunit;

namespace OneAudio.Tests;

/// <summary>
/// HE-AAC cases the decoder documents as not supported must give <see cref="UnsupportedAudioFormatException"/>, never a
/// silent core-only or high-band-less decode. The streams are built bit by bit: access units made of silent channel
/// elements and fill elements that carry SBR payloads (ISO/IEC 14496-3 4.4.2.7, 4.5.2.8).
/// </summary>
public class AacHeAacRefusalTests
{
    private sealed class Bits
    {
        private readonly List<byte> _bytes = [];
        private int _used; // bits used in the last byte

        public Bits Put(int value, int count)
        {
            for (int i = count - 1; i >= 0; i--)
            {
                if (_used == 0) _bytes.Add(0);
                if (((value >> i) & 1) != 0) _bytes[^1] |= (byte)(0x80 >> _used);
                _used = (_used + 1) & 7;
            }
            return this;
        }

        public Bits Align() { _used = 0; return this; }

        public byte[] ToArray() => [.. _bytes];
    }

    // A silent long-window single channel element with the given instance tag (29 bits).
    private static Bits Sce(Bits b, int tag) => b.Put(0, 3).Put(tag, 4).Put(0, 8).Put(0, 1).Put(0, 2).Put(0, 1).Put(0, 6).Put(0, 1).Put(0, 3);

    // A fill element whose extension is SBR data without CRC (extension type 0xD): the payload bits, padded to whole bytes.
    private static Bits SbrFill(Bits b, Action<Bits> payload)
    {
        var p = new Bits();
        payload(p);
        p.Align();
        byte[] bytes = p.ToArray();
        // count is in bytes and includes the 4-bit extension type, so the payload follows the nibble inside the count
        var all = new Bits().Put(0xD, 4);
        foreach (byte x in bytes) all.Put(x, 8);
        all.Put(0, 4);
        byte[] body = all.ToArray();
        b.Put(6, 3);
        if (body.Length < 15) b.Put(body.Length, 4); else b.Put(15, 4).Put(body.Length - 14, 8);
        foreach (byte x in body) b.Put(x, 8);
        return b;
    }

    private static byte[] MonoUnitWithSbr(Action<Bits> payload)
    {
        var b = new Bits();
        Sce(b, 0);
        SbrFill(b, payload);
        return b.Put(7, 3).ToArray();
    }

    private static byte[] DualMonoUnit(bool sbrData)
    {
        var b = new Bits();
        Sce(b, 0);
        if (sbrData) SbrFill(b, p => p.Put(0, 8));
        Sce(b, 1);
        if (sbrData) SbrFill(b, p => p.Put(0, 8));
        return b.Put(7, 3).ToArray();
    }

    // AudioSpecificConfig: channelConfiguration 0 with a program config element of two single channel elements (tags 0 and 1).
    // explicitSbr writes it as object type 5 over AAC-LC at the given core rate index.
    private static byte[] DualMonoConfig(int rateIndex, bool explicitSbr)
    {
        var b = new Bits();
        if (explicitSbr) b.Put(5, 5).Put(rateIndex, 4).Put(0, 4).Put(rateIndex - 3, 4).Put(2, 5);
        else b.Put(2, 5).Put(rateIndex, 4).Put(0, 4);
        b.Put(0, 3); // frameLengthFlag, dependsOnCoreCoder, extensionFlag
        b.Put(0, 4).Put(1, 2).Put(rateIndex, 4).Put(2, 4).Put(0, 4).Put(0, 4).Put(0, 2).Put(0, 3).Put(0, 4).Put(0, 3);
        b.Put(0, 1).Put(0, 4).Put(0, 1).Put(1, 4);
        b.Align().Put(0, 8);
        return b.ToArray();
    }

    private static byte[] MonoConfig(int rateIndex) => new Bits().Put(2, 5).Put(rateIndex, 4).Put(1, 4).Put(0, 3).ToArray();

    private static List<byte[]> Units(byte[] unit, int count) => Enumerable.Range(0, count).Select(_ => unit).ToList();

    [Fact]
    public void DualMonoLayoutWithoutSbrDecodesAsStereoLc()
    {
        using var d = new AacDecoder(new AacPacketList(Units(DualMonoUnit(false), 4), DualMonoConfig(3, false)));
        Assert.Equal(2, d.Format.Channels);
        Assert.Equal(48000, d.Format.SampleRate);
        Assert.False(d.SbrActive);
        var buf = new float[1024 * 2];
        Assert.Equal(1024, d.Read(buf));
    }

    [Fact]
    public void ImplicitDualMonoSbrIsRefusedAtOpen()
    {
        var ex = Assert.Throws<UnsupportedAudioFormatException>(() => new AacDecoder(new AacPacketList(Units(DualMonoUnit(true), 4), DualMonoConfig(3, false))));
        Assert.Contains("dual mono", ex.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void ExplicitDualMonoSbrIsRefusedAtOpenBeforeAnyPacketIsRead()
    {
        var src = new AacPacketList([], DualMonoConfig(3, true));
        var ex = Assert.Throws<UnsupportedAudioFormatException>(() => new AacDecoder(src));
        Assert.Contains("dual mono", ex.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void DualMonoSbrFirstSeenAfterTheOpenWindowIsSkippedAndReported()
    {
        // Only the access units read at open can switch SBR on; later SBR data in a decoder running as plain AAC-LC is skipped.
        var units = Units(DualMonoUnit(false), 300);
        units.Add(DualMonoUnit(true));
        using var d = new AacDecoder(new AacPacketList(units, DualMonoConfig(3, false)));
        var buf = new float[1024 * 2];
        while (d.Read(buf) > 0) { }
        Assert.False(d.SbrActive);
        Assert.True(d.SbrDataIgnored);
    }

    // Core 48000, 44100 and 32000 Hz give SBR output rates of 96000, 88200 and 64000 Hz.
    [Theory]
    [InlineData(3)]
    [InlineData(4)]
    [InlineData(5)]
    public void UnverifiedStartFrequencyAtHighOutputRatesIsRefused(int rateIndex)
    {
        // sbr_header: amp_res 0, start_freq 0, stop_freq 0, xover_band 0, reserved, no extra headers
        byte[] unit = MonoUnitWithSbr(p => p.Put(1, 1).Put(0, 1).Put(0, 4).Put(0, 4).Put(0, 3).Put(0, 2).Put(0, 2));
        var ex = Assert.Throws<UnsupportedAudioFormatException>(() => new AacDecoder(new AacPacketList(Units(unit, 3), MonoConfig(rateIndex))));
        Assert.Contains("bs_start_freq", ex.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void ExplicitSbrWithUnverifiedStartFrequencyIsRefusedAtOpen()
    {
        byte[] unit = MonoUnitWithSbr(p => p.Put(1, 1).Put(0, 1).Put(3, 4).Put(0, 4).Put(0, 3).Put(0, 2).Put(0, 2));
        var cfg = new Bits().Put(5, 5).Put(3, 4).Put(1, 4).Put(0, 4).Put(2, 5).Put(0, 3).ToArray();
        Assert.Throws<UnsupportedAudioFormatException>(() => new AacDecoder(new AacPacketList(Units(unit, 3), cfg)));
    }

    [Fact]
    public void StartFrequencyOutsideTheHighRatesIsNotRefused()
    {
        // 24 kHz core: SBR rate 48 kHz, where every start_freq row is known
        byte[] unit = MonoUnitWithSbr(p => p.Put(1, 1).Put(0, 1).Put(0, 4).Put(0, 4).Put(0, 3).Put(0, 2).Put(0, 2));
        using var d = new AacDecoder(new AacPacketList(Units(unit, 3), MonoConfig(6)));
        Assert.True(d.SbrActive);
        Assert.Equal(48000, d.Format.SampleRate);
    }

    [Fact]
    public void SbrDataFirstSeenAfterTheOpenWindowIsSkippedAndReported()
    {
        byte[] silent = MonoUnitWithSbr(p => p.Put(0, 8)); // a fill with SBR data but no header: nothing to decode
        byte[] plain = new Bits().Put(0, 3).Put(0, 4).Put(0, 8).Put(0, 1).Put(0, 2).Put(0, 1).Put(0, 6).Put(0, 1).Put(0, 3).Put(7, 3).ToArray();
        var units = Units(plain, 300);
        units.Add(silent);
        using var d = new AacDecoder(new AacPacketList(units, MonoConfig(3)));
        Assert.False(d.SbrActive);
        Assert.False(d.PsActive);
        Assert.False(d.SbrDataIgnored);
        var buf = new float[1024];
        while (d.Read(buf) > 0) { }
        Assert.True(d.SbrDataIgnored);
        Assert.False(d.SbrActive);
        Assert.Equal(48000, d.Format.SampleRate);
    }
}
