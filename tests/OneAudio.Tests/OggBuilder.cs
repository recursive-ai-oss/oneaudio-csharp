using System.Buffers.Binary;

namespace OneAudio.Tests;

/// <summary>Builds Ogg files from packets for tests: page layout, checksums, chaining and damage are all under test control.</summary>
internal sealed class OggBuilder
{
    private readonly List<byte> _out = [];

    public byte[] ToArray() => _out.ToArray();

    /// <summary>The checksum of RFC 3533 section 6, written independently of the library's implementation.</summary>
    public static uint Crc(ReadOnlySpan<byte> data)
    {
        uint crc = 0;
        foreach (byte b in data)
        {
            crc ^= (uint)b << 24;
            for (int i = 0; i < 8; i++) crc = (crc & 0x80000000) != 0 ? (crc << 1) ^ 0x04C11DB7 : crc << 1;
        }
        return crc;
    }

    public static byte[] Page(uint serial, uint seq, byte flags, long granule, ReadOnlySpan<byte> lacing, ReadOnlySpan<byte> body)
    {
        var p = new byte[27 + lacing.Length + body.Length];
        "OggS"u8.CopyTo(p);
        p[5] = flags;
        BinaryPrimitives.WriteInt64LittleEndian(p.AsSpan(6), granule);
        BinaryPrimitives.WriteUInt32LittleEndian(p.AsSpan(14), serial);
        BinaryPrimitives.WriteUInt32LittleEndian(p.AsSpan(18), seq);
        p[26] = (byte)lacing.Length;
        lacing.CopyTo(p.AsSpan(27));
        body.CopyTo(p.AsSpan(27 + lacing.Length));
        BinaryPrimitives.WriteUInt32LittleEndian(p.AsSpan(22), Crc(p));
        return p;
    }

    /// <summary>
    /// Appends one logical stream. The first packet gets its own BOS page; the packets named by <paramref name="pageBreakAfter"/>
    /// end a page (so headers can be given their own pages). Other packets are packed until a page holds
    /// <paramref name="maxBody"/> bytes or 255 segments, splitting packets across pages when needed.
    /// <paramref name="granules"/>[i] is the granule position of the page on which packet i finishes (-1 for none).
    /// </summary>
    public OggBuilder AddStream(uint serial, IReadOnlyList<byte[]> packets, IReadOnlyList<long> granules, int maxBody = 4000, ISet<int>? pageBreakAfter = null, bool eos = true)
    {
        uint seq = 0;
        var lacing = new List<byte>();
        var body = new List<byte>();
        bool continued = false;
        long pageGranule = -1;
        bool first = true;

        void Flush(bool last)
        {
            byte flags = (byte)((continued ? 1 : 0) | (first ? 2 : 0) | (last && eos ? 4 : 0));
            _out.AddRange(Page(serial, seq++, flags, pageGranule, lacing.ToArray(), body.ToArray()));
            first = false;
            continued = false;
            lacing.Clear();
            body.Clear();
            pageGranule = -1;
        }

        for (int i = 0; i < packets.Count; i++)
        {
            var pk = packets[i];
            int off = 0;
            int remaining = pk.Length;
            while (true)
            {
                if (lacing.Count == 255 || body.Count >= maxBody)
                {
                    // A full page ends here; the next one continues this packet unless the packet has not started yet.
                    Flush(false);
                    continued = off > 0;
                }
                int seg = Math.Min(255, remaining);
                lacing.Add((byte)seg);
                body.AddRange(pk.AsSpan(off, seg).ToArray());
                off += seg;
                remaining -= seg;
                if (seg < 255) break;
                if (remaining == 0) { lacing.Add(0); break; }
            }
            pageGranule = granules[i];
            bool breakHere = i == 0 || (pageBreakAfter?.Contains(i) ?? false);
            if (breakHere) Flush(false);
        }
        if (lacing.Count > 0) Flush(true);
        else if (eos) PatchLastPageEos(serial);
        return this;
    }

    private void PatchLastPageEos(uint serial)
    {
        var data = _out.ToArray();
        int pos = 0, last = -1;
        while (pos < data.Length)
        {
            int n = data[pos + 26];
            int body = 0;
            for (int i = 0; i < n; i++) body += data[pos + 27 + i];
            if (BinaryPrimitives.ReadUInt32LittleEndian(data.AsSpan(pos + 14)) == serial) last = pos;
            pos += 27 + n + body;
        }
        data[last + 5] |= 4;
        BinaryPrimitives.WriteUInt32LittleEndian(data.AsSpan(last + 22), 0);
        int len = 27 + data[last + 26];
        for (int i = 0; i < data[last + 26]; i++) len += data[last + 27 + i];
        BinaryPrimitives.WriteUInt32LittleEndian(data.AsSpan(last + 22), Crc(data.AsSpan(last, len)));
        _out.Clear();
        _out.AddRange(data);
    }
}
