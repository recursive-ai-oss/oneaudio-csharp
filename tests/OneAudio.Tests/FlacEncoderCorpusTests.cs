using OneAudio.Flac;
using Xunit;

namespace OneAudio.Tests;

/// <summary>
/// Every CELLAR subset audio file is decoded, re-encoded at levels 0, 5 and 8, and the result is checked by our decoder
/// (samples and STREAMINFO MD5), by <c>flac -t</c>, and by <c>flac -d</c> against the reference decode of the original.
/// </summary>
public class FlacEncoderCorpusTests
{
    // Files 48 to 59 differ from the others only in their metadata (huge blocks, pictures, cue sheets): the audio is the same as file 47's.
    public static TheoryData<string> Subset()
    {
        var all = FlacTestEnv.Group("subset");
        var d = new TheoryData<string>();
        foreach (var row in all)
        {
            string rel = row.Data;
            string name = Path.GetFileName(rel);
            bool metadataOnly = name.Length > 2 && int.TryParse(name.AsSpan(0, 2), out int n) && n is >= 48 and <= 59;
            if (!metadataOnly) d.Add(rel);
        }
        return d;
    }

    [Theory, MemberData(nameof(Subset))]
    public void Subset_ReEncodes_BitExact(string rel)
    {
        var path = FlacTestEnv.Path_(rel);
        Assert.SkipUnless(FlacTestEnv.FlacBinary is not null, "reference flac binary not available (set ONEAUDIO_FLAC).");
        int num = int.Parse(Path.GetFileName(path).AsSpan(0, 2), System.Globalization.CultureInfo.InvariantCulture);

        var limits = new AudioLimits { MaxSampleRate = 1_048_575 };
        int[] src;
        AudioFormat fmt;
        int bits;
        using (var dec = FlacDecoder.Open(File.OpenRead(path), limits))
        {
            fmt = dec.Format;
            bits = dec.BitsPerSample;
            int ch = fmt.Channels, shift = fmt.BitsPerSample - bits;
            var list = new List<int>();
            var buf = new int[4096 * ch];
            int n;
            while ((n = dec.ReadInteger(buf)) > 0)
                for (int i = 0; i < n * ch; i++) list.Add(buf[i] >> shift);
            src = list.ToArray();
        }
        string reference = FlacTestEnv.ReferenceHash(path);

        foreach (int level in new[] { 0, 5, 8 })
        {
            // Odd write sizes so blocks straddle calls.
            var flac = EncoderSignals.Encode(src, bits, fmt.Channels, fmt.SampleRate, new FlacEncoderSettings { CompressionLevel = level }, writeChunk: 3001);
            var back = EncoderSignals.Decode(flac, out var d2);
            d2.Dispose();
            Assert.True(src.AsSpan().SequenceEqual(back), $"level {level}: decoded samples differ");

            string tmp = Path.Combine(Path.GetTempPath(), $"oneaudio-enc-{Environment.ProcessId}-{num}-{level}.flac");
            File.WriteAllBytes(tmp, flac);
            try
            {
                Assert.Equal(reference, FlacTestEnv.ReferenceHash(tmp)); // flac -d agrees with the original's decode
            }
            finally { File.Delete(tmp); }
            string? err = EncoderSignals.FlacTest(flac, $"corpus-{num}-L{level}");
            Assert.True(string.IsNullOrEmpty(err), $"flac -t failed at level {level}: {err}");
        }
    }
}
