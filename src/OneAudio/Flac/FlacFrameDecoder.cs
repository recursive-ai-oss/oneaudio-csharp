using System.Numerics;

namespace OneAudio.Flac;

/// <summary>Result of decoding one frame from a byte span.</summary>
internal enum FlacFrameStatus { Ok, NeedMore }

/// <summary>
/// Decodes FLAC frames (RFC 9639 section 9) into per-channel <see cref="int"/> buffers sized for the STREAMINFO maximum
/// block size. Errors in the data throw <see cref="AudioFormatException"/>; a frame that simply runs past the end of
/// the supplied bytes reports <see cref="FlacFrameStatus.NeedMore"/> so a streaming caller can supply more.
/// Nothing is allocated per frame after construction, except the 33-bit working buffers that a 32-bit stream with
/// stereo decorrelation needs, which are created on first use.
/// </summary>
internal sealed class FlacFrameDecoder
{
    private FlacStreamInfo _si;
    private readonly int _capacity;
    private readonly int[] _coef = new int[32];
    private readonly int[] _coefRev = new int[32];
    private long[]? _wideA, _wideB;
    private int[]? _wideRes;
    private string _error = "";

    /// <param name="si">The stream the frames belong to.</param>
    /// <param name="blockCapacity">Largest block size the buffers must hold; at least <c>si.MaxBlockSize</c>. A chained stream passes the largest of its links so one decoder serves all of them.</param>
    public FlacFrameDecoder(FlacStreamInfo si, int blockCapacity = 0)
    {
        _si = si;
        _capacity = Math.Max(blockCapacity, si.MaxBlockSize);
        Channels = new int[si.Channels][];
        for (int c = 0; c < Channels.Length; c++) Channels[c] = new int[_capacity];
    }

    /// <summary>Largest block size the buffers hold.</summary>
    public int Capacity => _capacity;

    /// <summary>Switches to another stream with the same channel count whose block size fits the buffers (a later link of a chain).</summary>
    public void SetStreamInfo(FlacStreamInfo si)
    {
        if (si.Channels != Channels.Length || si.MaxBlockSize > _capacity) throw new ArgumentException("Stream does not fit this decoder's buffers.", nameof(si));
        _si = si;
    }

    /// <summary>Decoded samples of the last frame, one array per channel; the first <c>BlockSize</c> entries are valid.</summary>
    public int[][] Channels { get; }

    /// <summary>True forces the scalar path of the LPC restore loop (used by tests to compare against the vector path).</summary>
    public bool ForceScalar { get; set; }

    /// <summary>
    /// Decodes the frame that starts at <c>data[0]</c>.
    /// </summary>
    /// <param name="data">Bytes from the frame's sync code onward; may extend past the frame.</param>
    /// <param name="header">The frame header.</param>
    /// <param name="used">Bytes the frame occupied, including the trailing CRC-16.</param>
    public FlacFrameStatus Decode(ReadOnlySpan<byte> data, out FlacFrameHeader header, out int used)
    {
        used = 0;
        var hs = FlacFrameHeaderParser.Parse(data, out header);
        if (hs == FlacHeaderStatus.NeedMore) return FlacFrameStatus.NeedMore;
        if (hs == FlacHeaderStatus.Invalid) throw new AudioFormatException("Invalid FLAC frame header (lost sync or header checksum mismatch).");
        if (header.SampleRate == 0) header.SampleRate = _si.SampleRate;
        if (header.BitsPerSample == 0) header.BitsPerSample = _si.BitsPerSample;
        if (header.SampleRate != _si.SampleRate) throw new AudioFormatException("FLAC frame changes the sample rate mid-stream.");
        if (header.Channels != _si.Channels) throw new AudioFormatException("FLAC frame changes the channel count mid-stream.");
        if (header.BitsPerSample != _si.BitsPerSample) throw new AudioFormatException("FLAC frame changes the bit depth mid-stream.");
        if (header.BlockSize > _si.MaxBlockSize) throw new AudioFormatException("FLAC frame is larger than the STREAMINFO maximum block size.");

        int n = header.BlockSize;
        var br = new FlacBitReader(data[header.Length..]);
        int bps = header.BitsPerSample;
        int ca = header.ChannelAssignment;
        bool ok;
        if (bps == 32 && ca >= 8) ok = DecodeWideStereo(ref br, n, ca);
        else
        {
            ok = true;
            for (int c = 0; c < header.Channels && ok; c++)
            {
                int depth = bps + (IsSide(ca, c) ? 1 : 0);
                ok = DecodeSubframe(ref br, Channels[c].AsSpan(0, n), depth);
            }
            if (ok && ca >= 8) Decorrelate(ca, Channels[0].AsSpan(0, n), Channels[1].AsSpan(0, n));
        }
        if (br.Overrun) return FlacFrameStatus.NeedMore;
        if (!ok) throw new AudioFormatException(_error);

        br.AlignToByte();
        int end = header.Length + br.BytePosition;
        if (end + 2 > data.Length) return FlacFrameStatus.NeedMore;
        // RFC 9639 section 9.3: CRC-16 over everything from the sync code to the last padding bit.
        if (FlacCrc.Crc16(data[..end]) != (ushort)((data[end] << 8) | data[end + 1]))
            throw new AudioFormatException("FLAC frame checksum mismatch.");
        used = end + 2;
        return FlacFrameStatus.Ok;
    }

    private static bool IsSide(int ca, int c) => ca switch { 8 => c == 1, 9 => c == 0, 10 => c == 1, _ => false };

    private bool Fail(string message)
    {
        _error = message;
        return false;
    }

    // ---- Subframes ----------------------------------------------------------------------------------

    private bool DecodeSubframe(ref FlacBitReader br, Span<int> s, int depth)
    {
        // RFC 9639 section 9.2: padding bit, 6-bit type, optional wasted-bits count.
        if (br.ReadBits(1) != 0) return Fail("FLAC subframe header padding bit is set.");
        int type = (int)br.ReadBits(6);
        int wasted = 0;
        if (br.ReadBits(1) != 0) wasted = br.ReadUnary() + 1;
        depth -= wasted;
        if (depth < 1 || depth > 32) return Fail("FLAC subframe has an invalid number of wasted bits.");

        bool ok;
        if (type == 0)
        {
            s.Fill(br.ReadSigned(depth));
            ok = true;
        }
        else if (type == 1)
        {
            for (int i = 0; i < s.Length; i++) s[i] = br.ReadSigned(depth);
            ok = true;
        }
        else if (type >= 8 && type <= 12) ok = DecodeFixed(ref br, s, depth, type - 8);
        else if (type >= 32) ok = DecodeLpc(ref br, s, depth, type - 31);
        else return Fail("FLAC subframe uses a reserved type.");

        if (ok && wasted > 0)
            for (int i = 0; i < s.Length; i++) s[i] <<= wasted;
        return ok;
    }

    private bool DecodeFixed(ref FlacBitReader br, Span<int> s, int depth, int order)
    {
        if (order > s.Length) return Fail("FLAC fixed predictor order exceeds the block size.");
        for (int i = 0; i < order; i++) s[i] = br.ReadSigned(depth);
        if (!ReadResidual(ref br, s, order)) return false;
        // Integer wrap-around is harmless here: the true values fit the sample depth, so modular arithmetic is exact.
        switch (order)
        {
            case 1:
                for (int i = 1; i < s.Length; i++) s[i] += s[i - 1];
                break;
            case 2:
                for (int i = 2; i < s.Length; i++) s[i] += 2 * s[i - 1] - s[i - 2];
                break;
            case 3:
                for (int i = 3; i < s.Length; i++) s[i] += 3 * (s[i - 1] - s[i - 2]) + s[i - 3];
                break;
            case 4:
                for (int i = 4; i < s.Length; i++) s[i] += 4 * (s[i - 1] + s[i - 3]) - 6 * s[i - 2] - s[i - 4];
                break;
        }
        return true;
    }

    private bool DecodeLpc(ref FlacBitReader br, Span<int> s, int depth, int order)
    {
        if (order > s.Length) return Fail("FLAC LPC order exceeds the block size.");
        for (int i = 0; i < order; i++) s[i] = br.ReadSigned(depth);
        int precisionCode = (int)br.ReadBits(4);
        if (precisionCode == 15) return Fail("FLAC LPC coefficient precision is invalid.");
        int precision = precisionCode + 1;
        int shift = br.ReadSigned(5);
        if (shift < 0) return Fail("FLAC LPC uses a negative shift."); // RFC 9639 section 9.2.8
        long absSum = 0;
        for (int j = 0; j < order; j++)
        {
            int c = br.ReadSigned(precision);
            _coef[j] = c;
            _coefRev[order - 1 - j] = c;
            absSum += Math.Abs((long)c);
        }
        if (!ReadResidual(ref br, s, order)) return false;

        // The sum fits 32 bits when sum|c| * 2^(depth-1) < 2^31; otherwise accumulate in 64 bits.
        if ((absSum << (depth - 1)) < (1L << 31))
        {
            if (!ForceScalar && order >= System.Numerics.Vector<int>.Count && System.Numerics.Vector.IsHardwareAccelerated)
                RestoreLpcVector(s, order, shift);
            else
                RestoreLpcScalar(s, order, shift);
        }
        else RestoreLpcWide(s, order, shift);
        return true;
    }

    /// <summary>Scalar reference for the 32-bit accumulation path.</summary>
    internal void RestoreLpcScalar(Span<int> s, int order, int shift)
    {
        var c = _coef;
        for (int i = order; i < s.Length; i++)
        {
            int sum = 0;
            for (int j = 0; j < order; j++) sum += c[j] * s[i - 1 - j];
            s[i] += sum >> shift;
        }
    }

    private void RestoreLpcVector(Span<int> s, int order, int shift)
    {
        int vc = System.Numerics.Vector<int>.Count;
        int vend = order - (order % vc);
        var rc = _coefRev;
        for (int i = order; i < s.Length; i++)
        {
            int baseIdx = i - order;
            var acc = System.Numerics.Vector<int>.Zero;
            for (int j = 0; j < vend; j += vc)
                acc += new System.Numerics.Vector<int>(rc.AsSpan(j)) * new System.Numerics.Vector<int>(s.Slice(baseIdx + j));
            int sum = System.Numerics.Vector.Sum(acc);
            // Tail coefficients: rc[j] pairs with s[baseIdx + j].
            for (int j = vend; j < order; j++) sum += rc[j] * s[baseIdx + j];
            s[i] += sum >> shift;
        }
    }

    private void RestoreLpcWide(Span<int> s, int order, int shift)
    {
        var c = _coef;
        for (int i = order; i < s.Length; i++)
        {
            long sum = 0;
            for (int j = 0; j < order; j++) sum += (long)c[j] * s[i - 1 - j];
            s[i] += (int)(sum >> shift);
        }
    }

    // ---- Residual (RFC 9639 section 9.2.7) -----------------------------------------------------------

    private bool ReadResidual(ref FlacBitReader br, Span<int> s, int order)
    {
        int method = (int)br.ReadBits(2);
        if (method > 1) return Fail("FLAC residual uses a reserved coding method.");
        int partOrder = (int)br.ReadBits(4);
        int parts = 1 << partOrder;
        int n = s.Length;
        if ((n & (parts - 1)) != 0) return Fail("FLAC block size is not divisible by the number of residual partitions.");
        int partSize = n >> partOrder;
        if (partSize < order) return Fail("FLAC first residual partition is shorter than the predictor order.");
        int paramBits = method == 0 ? 4 : 5;
        int escape = method == 0 ? 15 : 31;
        int idx = order;
        for (int p = 0; p < parts; p++)
        {
            int count = p == 0 ? partSize - order : partSize;
            int k = (int)br.ReadBits(paramBits);
            var dst = s.Slice(idx, count);
            if (k == escape)
            {
                int bits = (int)br.ReadBits(5);
                for (int i = 0; i < dst.Length; i++) dst[i] = br.ReadSigned(bits);
            }
            else
            {
                for (int i = 0; i < dst.Length; i++)
                    if (!br.TryReadRice(k, out dst[i]))
                    {
                        return br.Overrun ? true : Fail("FLAC Rice residual is out of range.");
                    }
            }
            idx += count;
        }
        return true;
    }

    // ---- Stereo decorrelation (RFC 9639 section 4.2 and 9.1.3) ---------------------------------------

    internal static void Decorrelate(int assignment, Span<int> ch0, Span<int> ch1)
    {
        int n = ch0.Length;
        switch (assignment)
        {
            case 8: // left + side: right = left - side
                for (int i = 0; i < n; i++) ch1[i] = ch0[i] - ch1[i];
                break;
            case 9: // side + right: left = right + side
                for (int i = 0; i < n; i++) ch0[i] += ch1[i];
                break;
            default: // mid + side: the side channel's low bit restores the bit lost when mid was halved
                for (int i = 0; i < n; i++)
                {
                    int side = ch1[i];
                    int mid = (ch0[i] << 1) | (side & 1);
                    ch0[i] = (mid + side) >> 1;
                    ch1[i] = (mid - side) >> 1;
                }
                break;
        }
    }

    // ---- 32-bit streams with stereo decorrelation: the side channel needs 33 bits ----------------------

    private bool DecodeWideStereo(ref FlacBitReader br, int n, int ca)
    {
        _wideA ??= new long[_capacity];
        _wideB ??= new long[_capacity];
        _wideRes ??= new int[_capacity];
        var a = _wideA.AsSpan(0, n);
        var b = _wideB.AsSpan(0, n);
        if (!DecodeSubframeWide(ref br, a, ca == 9 ? 33 : 32)) return false;
        if (!DecodeSubframeWide(ref br, b, ca == 9 ? 32 : 33)) return false;
        var o0 = Channels[0];
        var o1 = Channels[1];
        for (int i = 0; i < n; i++)
        {
            long x = a[i], y = b[i];
            switch (ca)
            {
                case 8: o0[i] = (int)x; o1[i] = (int)(x - y); break;
                case 9: o0[i] = (int)(x + y); o1[i] = (int)y; break;
                default:
                    long mid = (x << 1) | (y & 1);
                    o0[i] = (int)((mid + y) >> 1);
                    o1[i] = (int)((mid - y) >> 1);
                    break;
            }
        }
        return true;
    }

    private bool DecodeSubframeWide(ref FlacBitReader br, Span<long> s, int depth)
    {
        if (br.ReadBits(1) != 0) return Fail("FLAC subframe header padding bit is set.");
        int type = (int)br.ReadBits(6);
        int wasted = 0;
        if (br.ReadBits(1) != 0) wasted = br.ReadUnary() + 1;
        depth -= wasted;
        if (depth < 1 || depth > 33) return Fail("FLAC subframe has an invalid number of wasted bits.");
        if (type == 0) s.Fill(br.ReadSignedLong(depth));
        else if (type == 1)
        {
            for (int i = 0; i < s.Length; i++) s[i] = br.ReadSignedLong(depth);
        }
        else if ((type >= 8 && type <= 12) || type >= 32)
        {
            int order = type <= 12 ? type - 8 : type - 31;
            if (order > s.Length) return Fail("FLAC predictor order exceeds the block size.");
            for (int i = 0; i < order; i++) s[i] = br.ReadSignedLong(depth);
            int shift = 0;
            bool lpc = type >= 32;
            if (lpc)
            {
                int precisionCode = (int)br.ReadBits(4);
                if (precisionCode == 15) return Fail("FLAC LPC coefficient precision is invalid.");
                shift = br.ReadSigned(5);
                if (shift < 0) return Fail("FLAC LPC uses a negative shift.");
                for (int j = 0; j < order; j++) _coef[j] = br.ReadSigned(precisionCode + 1);
            }
            var res = _wideRes!.AsSpan(0, s.Length);
            if (!ReadResidual(ref br, res, order)) return false;
            for (int i = order; i < s.Length; i++)
            {
                long pred;
                if (lpc)
                {
                    long sum = 0;
                    for (int j = 0; j < order; j++) sum += (long)_coef[j] * s[i - 1 - j];
                    pred = sum >> shift;
                }
                else
                {
                    pred = order switch
                    {
                        0 => 0,
                        1 => s[i - 1],
                        2 => 2 * s[i - 1] - s[i - 2],
                        3 => 3 * (s[i - 1] - s[i - 2]) + s[i - 3],
                        _ => 4 * (s[i - 1] + s[i - 3]) - 6 * s[i - 2] - s[i - 4],
                    };
                }
                s[i] = res[i] + pred;
            }
        }
        else return Fail("FLAC subframe uses a reserved type.");
        if (wasted > 0)
            for (int i = 0; i < s.Length; i++) s[i] <<= wasted;
        return true;
    }
}
