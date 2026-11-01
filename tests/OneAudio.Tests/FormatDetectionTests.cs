using OneAudio.Mp3;
using Xunit;

namespace OneAudio.Tests;

/// <summary>Format detection through the default registry: every format opens, and the cheap-to-confuse ones are not misread as MP3.</summary>
public sealed class FormatDetectionTests
{
    private static string Path_(string dir, string name) => Path.Combine(AppContext.BaseDirectory, "testdata", dir, name);

    public static TheoryData<string, string> Files() => new()
    {
        { "wav", "pcm16_stereo.wav" },
        { "flac", "mono8.flac" },
        { "flac", "ogg_stereo16.oga" },
        { "opus", "silk-mono.opus" },
        { "mp3", "small_44100_2_joint.mp3" },
        { "containers", "aac-stereo.mp4" },
        { "containers", "aac-itunes-gapless.m4a" },
        { "containers", "aac-mono.3gp" },
        { "containers", "amrnb.3gp" },
        { "containers", "amrwb.3gp" },
        { "containers", "aac.aac" },
        { "containers", "synthetic-nb.amr" },
        { "containers", "synthetic-wb.awb" },
    };

    [Theory]
    [MemberData(nameof(Files))]
    public void Default_registry_opens_each_format(string dir, string name)
    {
        var path = Path_(dir, name);
        using (var d = Audio.Open(path))
        {
            var buf = new float[d.Format.Channels * 1024];
            Assert.True(d.Read(buf) > 0);
        }
        // Same through a stream, with explicit limits, seekable and not.
        using (var d = Audio.Open(File.OpenRead(path), AudioLimits.Default))
            Assert.True(d.Format.SampleRate > 0);
        try
        {
            using var d = Audio.Open(new NonSeekable(File.ReadAllBytes(path)), AudioLimits.Default);
            Assert.True(d.Format.SampleRate > 0);
        }
        catch (UnsupportedAudioFormatException ex) when (ex.Message.Contains("seekable"))
        {
            // MP4 files with the movie box after the media data need a seekable stream; that is a container rule, not a detection result.
        }
    }

    // Bytes that look like an MPEG audio header can appear anywhere in a binary payload; a recognised container magic must still win.
    [Theory]
    [InlineData("amrnb.3gp", 64)]
    [InlineData("amrwb.3gp", 100)]
    [InlineData("synthetic-nb.amr", 6)]
    [InlineData("synthetic-wb.awb", 9)]
    [InlineData("aac-stereo.mp4", 64)]
    public void Mpeg_like_bytes_inside_a_container_do_not_win(string name, int start)
    {
        var bytes = File.ReadAllBytes(Path_("containers", name));
        // Valid MPEG-1 Layer III 128 kbit/s 44.1 kHz headers, one frame length apart: the shape a sniffer accepts.
        for (int at = start; at + 4 < Math.Min(bytes.Length, AudioCodecRegistry.SniffBytes); at += 417)
        {
            bytes[at] = 0xFF; bytes[at + 1] = 0xFB; bytes[at + 2] = 0x90; bytes[at + 3] = 0x00;
        }
        var fmt = Audio.Kit.Registry.Detect(bytes.AsSpan(0, Math.Min(bytes.Length, AudioCodecRegistry.SniffBytes)));
        Assert.NotNull(fmt);
        Assert.IsNotType<Mp3Format>(fmt);
    }

    // AMR-NB files written by ffmpeg that older releases misread as MP3; they must open as AMR whatever the registration order.
    [Theory]
    [InlineData("voice.amr")]
    [InlineData("voice.3gp")]
    public void Ffmpeg_amr_files_are_not_taken_for_mp3(string name)
    {
        var path = Path_("detection", name);
        var head = File.ReadAllBytes(path);
        var mp3First = new AudioCodecRegistry().AddMp3().AddAmr().AddMp4().AddAac();
        Assert.IsNotType<Mp3Format>(mp3First.Detect(head.AsSpan(0, Math.Min(head.Length, AudioCodecRegistry.SniffBytes))));
        foreach (var open in new Func<IAudioDecoder>[] { () => Audio.Open(path), () => AudioDecoder.OpenFile(path, mp3First) })
        {
            using var d = open();
            Assert.Equal(8000, d.Format.SampleRate);
            Assert.Equal(1, d.Format.Channels);
            var buf = new float[4096];
            long frames = 0;
            int n;
            while ((n = d.Read(buf)) > 0) frames += n;
            Assert.InRange(frames, 47_000, 49_000); // 6 s at 8 kHz, give or take the encoder's padding
        }
    }

    [Fact]
    public void Registration_order_is_the_fallback_only()
    {
        // The MP3 format registered first still does not claim an AMR file.
        var reg = new AudioCodecRegistry().AddMp3().AddAmr().AddMp4().AddAac();
        var amr = File.ReadAllBytes(Path_("containers", "amrnb.3gp"));
        var fmt = reg.Detect(amr.AsSpan(0, Math.Min(amr.Length, AudioCodecRegistry.SniffBytes)));
        Assert.NotNull(fmt);
        Assert.IsNotType<Mp3Format>(fmt);
    }

    [Fact]
    public void Unregistered_formats_are_never_picked()
    {
        var reg = new AudioCodecRegistry().AddWav();
        var amr = File.ReadAllBytes(Path_("containers", "synthetic-nb.amr"));
        Assert.Null(reg.Detect(amr));
        Assert.Throws<UnsupportedAudioFormatException>(() => AudioDecoder.Open(new MemoryStream(amr), reg));
    }

    [Fact]
    public void Stream_and_limit_overloads_work()
    {
        var path = Path_("wav", "pcm16_stereo.wav");
        var limits = AudioLimits.Default;
        var byPath = Audio.Measure(path, limits);
        using var fs = File.OpenRead(path);
        var byStream = Audio.Measure(fs, limits);
        Assert.Equal(byPath.IntegratedLufs, byStream.IntegratedLufs);

        var mixer = Audio.CreateMixer(48_000, 2, 4);
        Assert.True(Audio.Kit.Play(mixer, File.OpenRead(path)).IsValid);
        using var voice = Audio.Kit.PlayStream(mixer, File.OpenRead(path));
        mixer.Render(new float[2 * 256]);
    }

    private sealed class NonSeekable(byte[] data) : Stream
    {
        private readonly MemoryStream _m = new(data);
        public override bool CanRead => true;
        public override bool CanSeek => false;
        public override bool CanWrite => false;
        public override long Length => throw new NotSupportedException();
        public override long Position { get => throw new NotSupportedException(); set => throw new NotSupportedException(); }
        public override void Flush() { }
        public override int Read(byte[] buffer, int offset, int count) => _m.Read(buffer, offset, count);
        public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
        public override void SetLength(long value) => throw new NotSupportedException();
        public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();
    }
}
