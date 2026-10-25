using System.Buffers.Binary;
using OneAudio.Flac;

namespace OneAudio.Tests;

/// <summary>Test helpers: reading small FLAC fixtures and re-wrapping a native FLAC file as Ogg FLAC.</summary>
internal static class FlacFixtures
{
    public static string Path_(string name) => System.IO.Path.Combine(AppContext.BaseDirectory, "testdata", "flac", name + ".flac");

    public static byte[] Bytes(string name) => File.ReadAllBytes(Path_(name));

    public static readonly string[] Names = ["stereo16_seektable", "stereo16_noseektable", "mono24_lpc", "mono8", "stereo16_midside_small", "six16"];

    public static FlacDecoder Open(byte[] data, AudioLimits? limits = null) => (FlacDecoder)AudioDecoder.Open(new MemoryStream(data), FlacTestEnv.Registry, limits);

    /// <summary>Decodes everything to interleaved native integers.</summary>
    public static int[] DecodeAll(IAudioDecoder d)
    {
        var all = new List<int>();
        var buf = new int[d.Format.Channels * 1000];
        int n;
        while ((n = d.ReadInteger(buf)) > 0) all.AddRange(buf.AsSpan(0, n * d.Format.Channels).ToArray());
        return [.. all];
    }

    /// <summary>
    /// Re-wraps the frames of a native FLAC file as an Ogg FLAC stream (one frame per packet), following the
    /// Ogg FLAC mapping: an identification packet, the remaining metadata blocks, then audio with the sample count
    /// after each frame as the granule position.
    /// </summary>
    public static byte[] ToOgg(byte[] flac, int maxBody = 4000)
    {
        int pos = 4;
        var blocks = new List<byte[]>();
        bool last = false;
        while (!last)
        {
            last = (flac[pos] & 0x80) != 0;
            int len = (flac[pos + 1] << 16) | (flac[pos + 2] << 8) | flac[pos + 3];
            blocks.Add(flac.AsSpan(pos, 4 + len).ToArray());
            pos += 4 + len;
        }
        var si = FlacStreamInfo.Parse(blocks[0].AsSpan(4), AudioLimits.Default);
        var packets = new List<byte[]>();
        var granules = new List<long>();
        var id = new byte[13 + blocks[0].Length];
        id[0] = 0x7F;
        "FLAC"u8.CopyTo(id.AsSpan(1));
        id[5] = 1;
        BinaryPrimitives.WriteUInt16BigEndian(id.AsSpan(7), (ushort)(blocks.Count - 1));
        "fLaC"u8.CopyTo(id.AsSpan(9));
        blocks[0].CopyTo(id, 13);
        id[13] &= 0x7F; // the STREAMINFO block is never the last in Ogg FLAC when others follow
        packets.Add(id);
        granules.Add(0);
        for (int i = 1; i < blocks.Count; i++)
        {
            var b = (byte[])blocks[i].Clone();
            packets.Add(b);
            granules.Add(0);
        }
        int lastHeader = packets.Count - 1;
        var fd = new FlacFrameDecoder(si);
        long samples = 0;
        while (pos < flac.Length)
        {
            fd.Decode(flac.AsSpan(pos), out var h, out int used);
            samples += h.BlockSize;
            packets.Add(flac.AsSpan(pos, used).ToArray());
            granules.Add(samples);
            pos += used;
        }
        return new OggBuilder().AddStream(0x464C4143, packets, granules, maxBody, new HashSet<int> { lastHeader }).ToArray();
    }
}
