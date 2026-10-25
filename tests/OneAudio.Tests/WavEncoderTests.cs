using System.Buffers.Binary;
using System.Diagnostics;
using Xunit;

namespace OneAudio.Tests;

/// <summary>WAV encoder: every sample type bit-exact through our decoder, header rules, RF64, independent readers.</summary>
public class WavEncoderTests
{
    private static readonly AudioCodecRegistry Registry = new AudioCodecRegistry().AddWav();

    public static TheoryData<NativeSampleType, int> TypesAndChannels()
    {
        var d = new TheoryData<NativeSampleType, int>();
        foreach (var t in new[] { NativeSampleType.PcmUnsigned8, NativeSampleType.PcmSigned16, NativeSampleType.PcmSigned24, NativeSampleType.PcmSigned32, NativeSampleType.IeeeFloat32, NativeSampleType.IeeeFloat64 })
            foreach (int ch in new[] { 1, 2, 3, 6, 8 })
                d.Add(t, ch);
        return d;
    }

    // RIFF header (12) + JUNK placeholder (36) + fmt chunk header (8).
    private const int FmtBody = 56;

    private static int Bits(NativeSampleType t) => t switch { NativeSampleType.PcmUnsigned8 => 8, NativeSampleType.PcmSigned16 => 16, NativeSampleType.PcmSigned24 => 24, NativeSampleType.PcmSigned32 => 32, NativeSampleType.IeeeFloat32 => 32, _ => 64 };

    private static byte[] Encode(NativeSampleType t, int ch, int[]? ints, float[]? floats, WavEncoderSettings? s = null, int rate = 44100)
    {
        using var ms = new MemoryStream();
        using (var enc = new WavEncoder(ms, new AudioFormat(rate, ch), (s ?? new WavEncoderSettings()) with { SampleType = t }, leaveOpen: true))
        {
            if (ints is not null) enc.WriteInteger(ints);
            if (floats is not null) enc.Write(floats);
            enc.Finish();
        }
        return ms.ToArray();
    }

    [Theory, MemberData(nameof(TypesAndChannels))]
    public void RoundTrip_IsBitExact(NativeSampleType type, int channels)
    {
        const int Frames = 3001; // odd data length for the narrow types, to exercise the pad byte
        bool isFloat = type is NativeSampleType.IeeeFloat32 or NativeSampleType.IeeeFloat64;
        int[]? ints = null;
        float[]? floats = null;
        byte[] wav;
        if (isFloat)
        {
            floats = new float[Frames * channels];
            for (int i = 0; i < floats.Length; i++) floats[i] = 1.5f * MathF.Sin(i * 0.037f);
            floats[5] = float.NaN; floats[6] = float.PositiveInfinity; floats[7] = -0f;
            wav = Encode(type, channels, null, floats);
        }
        else
        {
            ints = EncoderSignals.Make("noise", Bits(type), channels, Frames);
            ints[0] = EncoderSignals.Min(Bits(type)); ints[1] = EncoderSignals.Max(Bits(type));
            wav = Encode(type, channels, ints, null);
        }
        using var dec = AudioDecoder.Open(new MemoryStream(wav), Registry);
        Assert.Equal(Frames, dec.LengthFrames);
        Assert.Equal(type, dec.Format.NativeType);
        Assert.Equal(channels, dec.Format.Channels);
        Assert.Equal(44100, dec.Format.SampleRate);
        Assert.Equal(AudioFormat.DefaultMask(channels), dec.Format.ChannelMask);
        if (isFloat)
        {
            var back = new float[Frames * channels];
            Assert.Equal(Frames, dec.Read(back));
            for (int i = 0; i < back.Length; i++) Assert.Equal(BitConverter.SingleToUInt32Bits(floats![i]), BitConverter.SingleToUInt32Bits(back[i]));
        }
        else
        {
            var back = new int[Frames * channels];
            Assert.Equal(Frames, dec.ReadInteger(back));
            Assert.Equal(ints, back);
        }
        // RIFF sizes are consistent with the file.
        Assert.Equal((uint)(wav.Length - 8), BinaryPrimitives.ReadUInt32LittleEndian(wav.AsSpan(4)));
        Assert.Equal(0, wav.Length & 1);
    }

    [Fact]
    public void Float_ToInteger_RoundsClipsAndDithers()
    {
        var f = new float[] { 0f, 0.5f / 32768f, 1.5f / 32768f, 2.5f / 32768f, -0.5f / 32768f, 1f, -1f, 2f, -2f, float.NaN, float.NegativeInfinity, 0.25f };
        var wav = Encode(NativeSampleType.PcmSigned16, 1, null, f);
        using var dec = AudioDecoder.Open(new MemoryStream(wav), Registry);
        var back = new int[f.Length];
        dec.ReadInteger(back);
        Assert.Equal([0, 0, 2, 2, 0, 32767, -32768, 32767, -32768, 0, -32768, 8192], back);

        // 8-bit: stored unsigned, read back signed.
        var w8 = Encode(NativeSampleType.PcmUnsigned8, 1, null, [0f, 0.5f, -1f, 1f]);
        Assert.Equal(128, w8[^4]);
        using var d8 = AudioDecoder.Open(new MemoryStream(w8), Registry);
        var b8 = new int[4];
        d8.ReadInteger(b8);
        Assert.Equal([0, 64, -128, 127], b8);

        var ramp = new float[20000];
        for (int i = 0; i < ramp.Length; i++) ramp[i] = 0.3f * MathF.Sin(i * 0.01f);
        var s = new WavEncoderSettings { Dither = true };
        var a = Encode(NativeSampleType.PcmSigned16, 1, null, ramp, s);
        var b = Encode(NativeSampleType.PcmSigned16, 1, null, ramp, s);
        var plain = Encode(NativeSampleType.PcmSigned16, 1, null, ramp);
        Assert.Equal(a, b);
        Assert.NotEqual(plain, a);
    }

    [Fact]
    public void ExtensibleFollowsMicrosoftRules_AndCarriesTheMask()
    {
        var f = new float[300];
        byte[] Fmt(NativeSampleType t, int ch, bool? ext = null, ChannelMask mask = ChannelMask.None)
        {
            using var ms = new MemoryStream();
            using (var enc = new WavEncoder(ms, new AudioFormat(48000, ch, mask), new WavEncoderSettings { SampleType = t, Extensible = ext }, true))
                enc.Write(f.AsSpan(0, ch * 10));
            return ms.ToArray();
        }
        ushort Tag(byte[] w) => BinaryPrimitives.ReadUInt16LittleEndian(w.AsSpan(FmtBody));
        Assert.Equal(1, Tag(Fmt(NativeSampleType.PcmSigned16, 2)));
        Assert.Equal(0xFFFE, Tag(Fmt(NativeSampleType.PcmSigned16, 3)));
        Assert.Equal(0xFFFE, Tag(Fmt(NativeSampleType.PcmSigned24, 2)));
        Assert.Equal(0xFFFE, Tag(Fmt(NativeSampleType.IeeeFloat32, 2)));
        Assert.Equal(1, Tag(Fmt(NativeSampleType.PcmSigned24, 2, ext: false)));
        Assert.Equal(0xFFFE, Tag(Fmt(NativeSampleType.PcmSigned16, 1, ext: true)));

        var w = Fmt(NativeSampleType.PcmSigned16, 4, mask: ChannelMask.FrontLeft | ChannelMask.FrontRight | ChannelMask.SideLeft | ChannelMask.SideRight);
        Assert.Equal(0x603u, BinaryPrimitives.ReadUInt32LittleEndian(w.AsSpan(FmtBody + 20)));
        using var dec = AudioDecoder.Open(new MemoryStream(w), Registry);
        Assert.Equal(ChannelMask.FrontLeft | ChannelMask.FrontRight | ChannelMask.SideLeft | ChannelMask.SideRight, dec.Format.ChannelMask);
        // Standard GUID: 00000001-0000-0010-8000-00aa00389b71.
        Assert.Equal(Convert.FromHexString("0100000000001000800000AA00389B71"), w.AsSpan(FmtBody + 24, 16).ToArray());

        Assert.Throws<ArgumentException>(() => new WavEncoder(new MemoryStream(), new AudioFormat(48000, 2, ChannelMask.FrontLeft), null));
        Assert.Throws<ArgumentException>(() => new WavEncoder(new MemoryStream(), new AudioFormat(48000, 2), new WavEncoderSettings { SampleType = NativeSampleType.Lossy }));
        Assert.Throws<ArgumentException>(() => new WavEncoder(new MemoryStream(), new AudioFormat(0, 2), null));
    }

    [Fact]
    public void ForcedRf64_WritesAValidHeaderOurDecoderReads()
    {
        var ints = EncoderSignals.Make("music", 16, 2, 5000);
        var wav = Encode(NativeSampleType.PcmSigned16, 2, ints, null, new WavEncoderSettings { ForceRf64 = true });
        Assert.Equal("RF64", System.Text.Encoding.ASCII.GetString(wav, 0, 4));
        Assert.Equal(0xFFFF_FFFFu, BinaryPrimitives.ReadUInt32LittleEndian(wav.AsSpan(4)));
        Assert.Equal("ds64", System.Text.Encoding.ASCII.GetString(wav, 12, 4));
        Assert.Equal((ulong)(wav.Length - 8), BinaryPrimitives.ReadUInt64LittleEndian(wav.AsSpan(20)));
        Assert.Equal((ulong)ints.Length * 2, BinaryPrimitives.ReadUInt64LittleEndian(wav.AsSpan(28)));
        Assert.Equal((ulong)5000, BinaryPrimitives.ReadUInt64LittleEndian(wav.AsSpan(36)));
        using var dec = AudioDecoder.Open(new MemoryStream(wav), Registry);
        Assert.Equal(5000, dec.LengthFrames);
        var back = new int[ints.Length];
        dec.ReadInteger(back);
        Assert.Equal(ints, back);
    }

    [Fact]
    public void DataBeyond4GiB_BecomesRf64_OnSeekableOutput()
    {
        // 4.5 GB of 32-bit mono into a stream that keeps only its first bytes and its length.
        using var sink = new HeadSink(256);
        const int Chunk = 1 << 20;
        long frames = 0;
        using (var enc = new WavEncoder(sink, new AudioFormat(48000, 1), new WavEncoderSettings { SampleType = NativeSampleType.PcmSigned32 }, true))
        {
            var block = new int[Chunk];
            for (int i = 0; i < block.Length; i++) block[i] = i * 977;
            while (frames * 4 < 4_500_000_000L)
            {
                enc.WriteInteger(block);
                frames += Chunk;
            }
            enc.Finish();
        }
        var h = sink.Head;
        long dataBytes = frames * 4;
        Assert.Equal("RF64", System.Text.Encoding.ASCII.GetString(h, 0, 4));
        Assert.Equal("ds64", System.Text.Encoding.ASCII.GetString(h, 12, 4));
        Assert.Equal((ulong)dataBytes, BinaryPrimitives.ReadUInt64LittleEndian(h.AsSpan(28)));
        Assert.Equal((ulong)frames, BinaryPrimitives.ReadUInt64LittleEndian(h.AsSpan(36)));
        Assert.Equal((ulong)(sink.Length - 8), BinaryPrimitives.ReadUInt64LittleEndian(h.AsSpan(20)));
        Assert.Equal("fmt ", System.Text.Encoding.ASCII.GetString(h, 48, 4));
        int dataAt = 48 + 8 + 40;
        Assert.Equal("data", System.Text.Encoding.ASCII.GetString(h, dataAt, 4));
        Assert.Equal(0xFFFF_FFFFu, BinaryPrimitives.ReadUInt32LittleEndian(h.AsSpan(dataAt + 4)));
    }

    [Fact]
    public void NonSeekable_UsesDeclaredLengthOrTheUnknownMarker()
    {
        var ints = EncoderSignals.Make("music", 16, 2, 1000);
        byte[] Run(long total)
        {
            using var ms = new MemoryStream();
            using (var ns = new WriteOnly(ms))
            using (var enc = new WavEncoder(ns, new AudioFormat(44100, 2), new WavEncoderSettings { SampleType = NativeSampleType.PcmSigned16, TotalFrames = total }, true))
            {
                enc.WriteInteger(ints);
                enc.Finish();
            }
            return ms.ToArray();
        }
        var known = Run(1000);
        Assert.Equal((uint)(known.Length - 8), BinaryPrimitives.ReadUInt32LittleEndian(known.AsSpan(4)));
        var unknown = Run(-1);
        Assert.Equal(0xFFFF_FFFFu, BinaryPrimitives.ReadUInt32LittleEndian(unknown.AsSpan(4)));
        foreach (var w in new[] { known, unknown })
        {
            using var dec = AudioDecoder.Open(new MemoryStream(w), Registry);
            var back = new int[ints.Length];
            Assert.Equal(1000, dec.ReadInteger(back));
            Assert.Equal(ints, back);
        }
        Assert.Throws<ArgumentException>(() => new WavEncoder(new WriteOnly(new MemoryStream()), new AudioFormat(44100, 2), new WavEncoderSettings { ForceRf64 = true }));
        var ns2 = new WriteOnly(new MemoryStream());
        var enc2 = new WavEncoder(ns2, new AudioFormat(44100, 2), new WavEncoderSettings { TotalFrames = 5 });
        Assert.Throws<InvalidOperationException>(() => enc2.WriteInteger(new int[12]));
    }

    [Fact]
    public void EncodeFromDecoder_RoundTripsThroughFlac()
    {
        var ints = EncoderSignals.Make("music", 24, 2, 20000);
        var flac = EncoderSignals.Encode(ints, 24, 2, 44100);
        using var src = OneAudio.Flac.FlacDecoder.Open(new MemoryStream(flac));
        using var ms = new MemoryStream();
        long n = WavEncoder.Encode(src, ms, null, leaveOpen: true);
        Assert.Equal(20000, n);
        using var dec = AudioDecoder.Open(new MemoryStream(ms.ToArray()), Registry);
        Assert.Equal(NativeSampleType.PcmSigned24, dec.Format.NativeType);
        var back = new int[ints.Length];
        dec.ReadInteger(back);
        Assert.Equal(ints, back);
    }

    [Fact]
    public void Write_AllocatesNothingAfterWarmUp()
    {
        var f = new float[8192 * 2];
        for (int i = 0; i < f.Length; i++) f[i] = 0.5f * MathF.Sin(i * 0.013f);
        var ints = EncoderSignals.Make("noise", 24, 2, 8192);
        foreach (var t in new[] { NativeSampleType.PcmSigned16, NativeSampleType.PcmSigned24, NativeSampleType.IeeeFloat32 })
        {
            using var sink = new HeadSink(64);
            using var enc = new WavEncoder(sink, new AudioFormat(44100, 2), new WavEncoderSettings { SampleType = t, Dither = true }, true);
            enc.Write(f);
            if (t != NativeSampleType.IeeeFloat32) enc.WriteInteger(ints.Select(v => v >> 8).ToArray());
            long before = GC.GetAllocatedBytesForCurrentThread();
            for (int i = 0; i < 10; i++) enc.Write(f);
            Assert.Equal(0, GC.GetAllocatedBytesForCurrentThread() - before);
        }
    }

    // ---- independent readers (black boxes) ----------------------------------------------------------------------------

    private static string? Which(string tool)
    {
        foreach (var dir in (Environment.GetEnvironmentVariable("PATH") ?? "").Split(':'))
        {
            var p = Path.Combine(dir, tool);
            if (File.Exists(p)) return p;
        }
        return null;
    }

    private static Dictionary<string, string> Probe(string ffprobe, byte[] wav)
    {
        string path = Path.Combine(Path.GetTempPath(), "oneaudio-wav-" + Guid.NewGuid().ToString("N")[..8] + ".wav");
        File.WriteAllBytes(path, wav);
        try
        {
            var psi = new ProcessStartInfo(ffprobe) { RedirectStandardOutput = true, RedirectStandardError = true, UseShellExecute = false };
            foreach (var a in new[] { "-v", "error", "-show_entries", "stream=codec_name,channels,sample_rate,bits_per_sample,channel_layout,duration_ts:format=format_name", "-of", "default=nw=1", path }) psi.ArgumentList.Add(a);
            using var p = Process.Start(psi)!;
            string o = p.StandardOutput.ReadToEnd();
            string e = p.StandardError.ReadToEnd();
            p.WaitForExit();
            Assert.True(p.ExitCode == 0, e);
            return o.Split('\n', StringSplitOptions.RemoveEmptyEntries).Select(l => l.Split('=', 2)).Where(a => a.Length == 2).ToDictionary(a => a[0].Trim(), a => a[1].Trim());
        }
        finally { File.Delete(path); }
    }

    [Theory]
    [InlineData(NativeSampleType.PcmUnsigned8, 1, "pcm_u8", false, false)]
    [InlineData(NativeSampleType.PcmSigned16, 2, "pcm_s16le", false, false)]
    [InlineData(NativeSampleType.PcmSigned16, 6, "pcm_s16le", false, false)]
    [InlineData(NativeSampleType.PcmSigned24, 2, "pcm_s24le", false, false)]
    [InlineData(NativeSampleType.PcmSigned32, 8, "pcm_s32le", false, false)]
    [InlineData(NativeSampleType.IeeeFloat32, 2, "pcm_f32le", false, false)]
    [InlineData(NativeSampleType.IeeeFloat64, 1, "pcm_f64le", false, false)]
    [InlineData(NativeSampleType.PcmSigned16, 2, "pcm_s16le", true, false)]
    [InlineData(NativeSampleType.PcmSigned24, 6, "pcm_s24le", true, true)]
    [InlineData(NativeSampleType.IeeeFloat32, 2, "pcm_f32le", true, true)]
    public void IndependentReader_SeesTheRightFormat(NativeSampleType type, int channels, string codec, bool rf64, bool extensible)
    {
        string? ffprobe = Which("ffprobe");
        Assert.SkipUnless(ffprobe is not null, "ffprobe not available.");
        const int Frames = 4410;
        byte[] wav;
        if (type is NativeSampleType.IeeeFloat32 or NativeSampleType.IeeeFloat64)
        {
            var f = new float[Frames * channels];
            for (int i = 0; i < f.Length; i++) f[i] = 0.5f * MathF.Sin(i * 0.05f);
            wav = Encode(type, channels, null, f, new WavEncoderSettings { ForceRf64 = rf64 }, 48000);
        }
        else wav = Encode(type, channels, EncoderSignals.Make("music", Bits(type), channels, Frames), null, new WavEncoderSettings { ForceRf64 = rf64 }, 48000);
        var info = Probe(ffprobe!, wav);
        Assert.Equal(codec, info["codec_name"]);
        Assert.Equal(channels.ToString(), info["channels"]);
        Assert.Equal("48000", info["sample_rate"]);
        Assert.Equal(Frames.ToString(), info["duration_ts"]);
        Assert.Equal("wav", info["format_name"]);
        if (extensible || channels > 2) Assert.True(info.ContainsKey("channel_layout") && info["channel_layout"].Length > 0);
    }

    [Fact]
    public void IndependentReader_DecodesTheSameSamples()
    {
        string? ffmpeg = Which("ffmpeg");
        Assert.SkipUnless(ffmpeg is not null, "ffmpeg not available.");
        var ints = EncoderSignals.Make("music", 24, 2, 8000);
        var wav = Encode(NativeSampleType.PcmSigned24, 2, ints, null);
        string path = Path.Combine(Path.GetTempPath(), "oneaudio-wav-" + Guid.NewGuid().ToString("N")[..8] + ".wav");
        File.WriteAllBytes(path, wav);
        try
        {
            var psi = new ProcessStartInfo(ffmpeg!) { RedirectStandardOutput = true, RedirectStandardError = true, UseShellExecute = false };
            foreach (var a in new[] { "-v", "error", "-i", path, "-f", "s32le", "-" }) psi.ArgumentList.Add(a);
            using var p = Process.Start(psi)!;
            using var o = new MemoryStream();
            p.StandardOutput.BaseStream.CopyTo(o);
            p.WaitForExit();
            var raw = o.ToArray();
            Assert.Equal(ints.Length * 4, raw.Length);
            for (int i = 0; i < ints.Length; i++) Assert.Equal(ints[i] << 8, BinaryPrimitives.ReadInt32LittleEndian(raw.AsSpan(4 * i)));
        }
        finally { File.Delete(path); }
    }

    /// <summary>Seekable stream that keeps only its first bytes and its length.</summary>
    private sealed class HeadSink(int keep) : Stream
    {
        private readonly byte[] _head = new byte[keep];
        private long _pos, _len;
        public byte[] Head => _head;
        public override bool CanRead => false;
        public override bool CanSeek => true;
        public override bool CanWrite => true;
        public override long Length => _len;
        public override long Position { get => _pos; set => _pos = value; }
        public override void Flush() { }
        public override int Read(byte[] buffer, int offset, int count) => throw new NotSupportedException();
        public override long Seek(long offset, SeekOrigin origin) => _pos = origin switch { SeekOrigin.Begin => offset, SeekOrigin.Current => _pos + offset, _ => _len + offset };
        public override void SetLength(long value) => _len = value;
        public override void Write(byte[] buffer, int offset, int count) => Write(buffer.AsSpan(offset, count));
        public override void Write(ReadOnlySpan<byte> buffer)
        {
            if (_pos < _head.Length) buffer[..(int)Math.Min(buffer.Length, _head.Length - _pos)].CopyTo(_head.AsSpan((int)_pos));
            _pos += buffer.Length;
            _len = Math.Max(_len, _pos);
        }
    }

    private sealed class WriteOnly(Stream inner) : Stream
    {
        public override bool CanRead => false;
        public override bool CanSeek => false;
        public override bool CanWrite => true;
        public override long Length => throw new NotSupportedException();
        public override long Position { get => throw new NotSupportedException(); set => throw new NotSupportedException(); }
        public override void Flush() => inner.Flush();
        public override int Read(byte[] buffer, int offset, int count) => throw new NotSupportedException();
        public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
        public override void SetLength(long value) => throw new NotSupportedException();
        public override void Write(byte[] buffer, int offset, int count) => inner.Write(buffer, offset, count);
        public override void Write(ReadOnlySpan<byte> buffer) => inner.Write(buffer);
    }
}
