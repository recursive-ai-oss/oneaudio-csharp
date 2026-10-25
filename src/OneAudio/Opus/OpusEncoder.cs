namespace OneAudio.Opus;

/// <summary>
/// A packet-level Opus encoder (RFC 6716 section 5): turns interleaved 48 kHz PCM into Opus packets, one packet per
/// call. Frame sizes of 2.5, 5, 10, 20, 40 and 60 ms are accepted; 40 and 60 ms packets hold two or three 20 ms frames.
/// </summary>
/// <remarks>
/// <para>This version codes with the CELT layer for every bitrate and bandwidth (the mode, bandwidth and rate logic of
/// the reference encoder is in place; SILK and hybrid packets are not produced). Input is interleaved
/// <see cref="float"/> with full scale ±1.0, clipped at ±2.0 and never rounded. Output is deterministic for identical
/// input and settings on a platform. After construction, <see cref="Encode(ReadOnlySpan{float}, int, Span{byte})"/>
/// allocates nothing. Instances are not thread safe.</para>
/// <para>Derived from the reference encoder in RFC 6716 (Simplified BSD licence, see THIRD-PARTY-NOTICES.md).</para>
/// </remarks>
public sealed class OpusEncoder
{
    private const int ModeSilk = 1000;
    private const int ModeHybrid = 1001;
    private const int ModeCelt = 1002;
    private const int Fs = 48000;
    private const int MaxEncoderBuffer = 480;

    private static readonly int[] MonoVoiceBw = [11000, 1000, 14000, 1000, 21000, 2000, 29000, 2000];
    private static readonly int[] MonoMusicBw = [14000, 1000, 18000, 2000, 24000, 2000, 33000, 2000];
    private static readonly int[] StereoVoiceBw = [11000, 1000, 14000, 1000, 21000, 2000, 32000, 2000];
    private static readonly int[] StereoMusicBw = [14000, 1000, 18000, 2000, 24000, 2000, 48000, 2000];
    private const int StereoVoiceThreshold = 26000;
    private const int StereoMusicThreshold = 36000;

    private readonly CeltEncoder _celt;
    private readonly int _channels;
    private readonly OpusApplication _application;
    private OpusEncoderSettings _s;

    private readonly float[] _delayBuffer = new float[MaxEncoderBuffer * 2];
    private readonly float[] _pcmBuf = new float[(Fs / 250 + 2880) * 2];
    private readonly float[] _floatIn = new float[2880 * 2];
    private readonly byte[] _subFrames = new byte[3 * 1280];
    private readonly int[] _subLen = new int[3];
    private readonly float[] _hpMem = new float[4];

    private int _streamChannels;
    private int _mode;
    private int _prevChannels;
    private int _prevFramesize;
    private int _bandwidth;
    private bool _first = true;
    private int _variableHpSmth2Q15;
    private uint _rangeFinal;
    private int _userBandwidth;
    private int _forceChannels;
    private readonly int _delayCompensation;

    /// <summary>Creates an encoder.</summary>
    /// <param name="settings">Initial settings; <see langword="null"/> means all defaults (48 kHz stereo audio).</param>
    /// <exception cref="ArgumentOutOfRangeException">A setting is outside its documented range, or the sample rate is not 48000.</exception>
    public OpusEncoder(OpusEncoderSettings? settings = null)
    {
        settings ??= new OpusEncoderSettings();
        Validate(settings);
        _s = settings;
        _channels = settings.Channels;
        _application = settings.Application;
        _celt = new CeltEncoder(_channels);
        _delayCompensation = _application == OpusApplication.RestrictedLowDelay ? 0 : Fs / 250;
        Apply();
        Reset();
    }

    /// <summary>The current settings. Assigning validates them; the channel count, sample rate and application cannot change.</summary>
    /// <exception cref="ArgumentOutOfRangeException">A setting is outside its documented range.</exception>
    /// <exception cref="ArgumentException">The channel count, sample rate or application differs from the creation settings.</exception>
    public OpusEncoderSettings Settings
    {
        get => _s;
        set
        {
            ArgumentNullException.ThrowIfNull(value);
            Validate(value);
            if (value.Channels != _channels || value.Application != _application)
                throw new ArgumentException("Channels and application cannot change after creation.", nameof(value));
            _s = value;
            Apply();
        }
    }

    /// <summary>Range coder state after the last packet, which the decoder reproduces (RFC 6716 section 6).</summary>
    public uint FinalRange => _rangeFinal;

    /// <summary>
    /// Samples (per channel, at 48 kHz) of look-ahead the encoder adds to the signal: the first decoded sample
    /// corresponds to this many samples before the first input sample. Ogg Opus stores it as the pre-skip.
    /// </summary>
    public int LookaheadSamples => Fs / 400 + _delayCompensation;

    /// <summary>The input channel count.</summary>
    public int Channels => _channels;

    private static void Validate(OpusEncoderSettings s)
    {
        if (s.SampleRate != Fs)
            throw new ArgumentOutOfRangeException(nameof(s), s.SampleRate, "Only 48000 Hz input is supported; resample first.");
        if (s.Channels is < 1 or > 2)
            throw new ArgumentOutOfRangeException(nameof(s), s.Channels, "Channels must be 1 or 2.");
        if (!Enum.IsDefined(s.Application))
            throw new ArgumentOutOfRangeException(nameof(s), s.Application, "Unknown application.");
        if (s.BitrateBps != OpusEncoderSettings.AutoBitrate && s.BitrateBps != OpusEncoderSettings.MaxBitrate
            && (s.BitrateBps < 500 || s.BitrateBps > 300000 * s.Channels))
            throw new ArgumentOutOfRangeException(nameof(s), s.BitrateBps, "Bitrate must be 500 to 300000 per channel.");
        if (!Enum.IsDefined(s.BitrateMode))
            throw new ArgumentOutOfRangeException(nameof(s), s.BitrateMode, "Unknown bitrate mode.");
        if (s.Complexity is < 0 or > 10)
            throw new ArgumentOutOfRangeException(nameof(s), s.Complexity, "Complexity must be 0 to 10.");
        if (s.Bandwidth != OpusBandwidth.Auto && !Enum.IsDefined(s.Bandwidth))
            throw new ArgumentOutOfRangeException(nameof(s), s.Bandwidth, "Unknown bandwidth.");
        if (!Enum.IsDefined(s.MaxBandwidth) || s.MaxBandwidth == OpusBandwidth.Auto)
            throw new ArgumentOutOfRangeException(nameof(s), s.MaxBandwidth, "Unknown maximum bandwidth.");
        if (!Enum.IsDefined(s.Signal))
            throw new ArgumentOutOfRangeException(nameof(s), s.Signal, "Unknown signal type.");
        if (s.ForceChannels < 0 || s.ForceChannels > s.Channels)
            throw new ArgumentOutOfRangeException(nameof(s), s.ForceChannels, "ForceChannels must be 0 (automatic) or 1..Channels.");
        if (s.PacketLossPercent is < 0 or > 100)
            throw new ArgumentOutOfRangeException(nameof(s), s.PacketLossPercent, "PacketLossPercent must be 0 to 100.");
    }

    private void Apply()
    {
        _userBandwidth = _s.Bandwidth == OpusBandwidth.Auto ? -1000 : (int)_s.Bandwidth;
        _forceChannels = _s.ForceChannels == 0 ? -1000 : _s.ForceChannels;
        _celt.Complexity = _s.Complexity;
        _celt.LossRate = _s.PacketLossPercent;
    }

    /// <summary>Clears all signal history, as for a new stream.</summary>
    public void Reset()
    {
        _celt.Reset();
        _streamChannels = _channels;
        _prevChannels = 0;
        _prevFramesize = 0;
        _bandwidth = (int)OpusBandwidth.Fullband;
        _first = true;
        _mode = ModeHybrid;
        _rangeFinal = 0;
        Array.Clear(_delayBuffer);
        Array.Clear(_hpMem);
        _variableHpSmth2Q15 = Lin2Log(60) << 8;
    }

    /// <summary>Encodes 16-bit PCM; see <see cref="Encode(ReadOnlySpan{float}, int, Span{byte})"/>. Samples are scaled by 1/32768.</summary>
    public int Encode(ReadOnlySpan<short> pcm, int frameSize, Span<byte> packet)
    {
        CheckFrame(frameSize);
        if (pcm.Length < frameSize * _channels)
            throw new ArgumentException("pcm holds fewer than frameSize samples per channel.", nameof(pcm));
        var f = _floatIn;
        int n = frameSize * _channels;
        for (int i = 0; i < n; i++) f[i] = pcm[i] * (1f / 32768f);
        return Encode(f.AsSpan(0, n), frameSize, packet);
    }

    /// <summary>Encodes one packet.</summary>
    /// <param name="pcm">Interleaved samples, at least <paramref name="frameSize"/> per channel, full scale ±1.0.</param>
    /// <param name="frameSize">Samples per channel in the packet: 120, 240, 480, 960, 1920 or 2880 (2.5 to 60 ms).</param>
    /// <param name="packet">Receives the packet. Its length is the size limit (at most 1275 bytes are used per frame
    /// in a packet; longer buffers are fine). At least 2 bytes are required.</param>
    /// <returns>Bytes written. A result of 1 (a TOC byte only) tells the decoder to conceal a lost or discontinuous frame.</returns>
    /// <exception cref="ArgumentException"><paramref name="frameSize"/> is not a valid frame size, or a buffer is too small.</exception>
    public int Encode(ReadOnlySpan<float> pcm, int frameSize, Span<byte> packet)
    {
        CheckFrame(frameSize);
        if (pcm.Length < frameSize * _channels)
            throw new ArgumentException("pcm holds fewer than frameSize samples per channel.", nameof(pcm));
        if (packet.Length < 2)
            throw new ArgumentException("The packet buffer must hold at least 2 bytes.", nameof(packet));
        int max = Math.Min(1276, packet.Length);
        int ret = EncodeNative(pcm, frameSize, packet, max, false);
        if (ret < 0)
            throw new InvalidOperationException("The encoder failed to produce a packet.");
        return ret;
    }

    private static void CheckFrame(int frameSize)
    {
        if (frameSize != 120 && frameSize != 240 && frameSize != 480 && frameSize != 960 && frameSize != 1920 && frameSize != 2880)
            throw new ArgumentException("frameSize must be 120, 240, 480, 960, 1920 or 2880.", nameof(frameSize));
    }

    // ------------------------------------------------------------------------------------------------

    private static int Lin2Log(int inLin)
    {
        int lz = SilkMath.Clz32(inLin);
        int frac = (int)(System.Numerics.BitOperations.RotateRight((uint)inLin, 24 - lz) & 0x7f);
        return (31 - lz << 7) + SilkMath.SMlaWB(frac, frac * (128 - frac), 179);
    }

    private static int FixConst(double c, int q) => (int)(c * (1L << q) + 0.5);

    private int UserBitrateToBitrate(int frameSize, int maxDataBytes)
    {
        if (frameSize == 0) frameSize = Fs / 400;
        if (_s.BitrateBps == OpusEncoderSettings.AutoBitrate)
            return 60 * Fs / frameSize + Fs * _channels;
        if (_s.BitrateBps == OpusEncoderSettings.MaxBitrate)
            return maxDataBytes * 8 * Fs / frameSize;
        return _s.BitrateBps;
    }

    private static byte GenToc(int mode, int framerate, int bandwidth, int channels)
    {
        int period = 0;
        while (framerate < 400)
        {
            framerate <<= 1;
            period++;
        }
        int toc;
        if (mode == ModeSilk)
        {
            toc = (bandwidth - (int)OpusBandwidth.Narrowband) << 5;
            toc |= (period - 2) << 3;
        }
        else if (mode == ModeCelt)
        {
            int tmp = bandwidth - (int)OpusBandwidth.Mediumband;
            if (tmp < 0) tmp = 0;
            toc = 0x80;
            toc |= tmp << 5;
            toc |= period << 3;
        }
        else
        {
            toc = 0x60;
            toc |= (bandwidth - (int)OpusBandwidth.SuperWideband) << 4;
            toc |= (period - 2) << 3;
        }
        toc |= channels == 2 ? 4 : 0;
        return (byte)toc;
    }

    // High-pass filter for the VoIP application (RFC 6716 reference hp_cutoff).
    private void HpCutoff(ReadOnlySpan<float> input, int cutoffHz, Span<float> output, int len)
    {
        Span<int> b = stackalloc int[3];
        Span<int> a = stackalloc int[2];
        int fcQ19 = (short)FixConst(1.5 * 3.14159 / 1000, 19) * (short)cutoffHz / (Fs / 1000);
        int rQ28 = FixConst(1.0, 28) - FixConst(0.92, 9) * fcQ19;
        b[0] = rQ28;
        b[1] = -rQ28 << 1;
        b[2] = rQ28;
        int rQ22 = rQ28 >> 6;
        a[0] = SilkMath.SMulWW(rQ22, SilkMath.SMulWW(fcQ19, fcQ19) - FixConst(2.0, 22));
        a[1] = SilkMath.SMulWW(rQ22, rQ22);
        float a0 = (float)(a[0] * (1.0 / (1 << 28))), a1 = (float)(a[1] * (1.0 / (1 << 28)));
        float b0 = (float)(b[0] * (1.0 / (1 << 28))), b1 = (float)(b[1] * (1.0 / (1 << 28))), b2 = (float)(b[2] * (1.0 / (1 << 28)));
        int ch = _channels;
        for (int c = 0; c < ch; c++)
        {
            float s0 = _hpMem[2 * c], s1 = _hpMem[2 * c + 1];
            for (int k = 0; k < len; k++)
            {
                float inval = input[k * ch + c];
                float vout = s0 + b0 * inval;
                s0 = s1 - vout * a0 + b1 * inval;
                s1 = -(vout * a1) + b2 * inval;
                output[k * ch + c] = vout;
            }
            _hpMem[2 * c] = s0;
            _hpMem[2 * c + 1] = s1;
        }
    }

    private static int EndBand(int bandwidth) => bandwidth switch
    {
        (int)OpusBandwidth.Narrowband => 13,
        (int)OpusBandwidth.Mediumband or (int)OpusBandwidth.Wideband => 17,
        (int)OpusBandwidth.SuperWideband => 19,
        _ => 21,
    };

    private int EncodeNative(ReadOnlySpan<float> pcm, int frameSize, Span<byte> data, int maxDataBytes, bool inner)
    {
        maxDataBytes = Math.Min(maxDataBytes, 1276);
        _rangeFinal = 0;
        if (maxDataBytes <= 0) return -1;
        int delayCompensation = _delayCompensation;
        int bitrateBps = UserBitrateToBitrate(frameSize, maxDataBytes);
        int frameRate = Fs / frameSize;
        bool useVbr = _s.BitrateMode != OpusBitrateMode.Cbr;
        bool vbrConstraint = _s.BitrateMode == OpusBitrateMode.ConstrainedVbr;

        // Too few bytes or bits to code anything: the TOC byte alone asks the decoder to conceal.
        if (maxDataBytes < 3 || bitrateBps < 3 * frameRate * 8
            || (frameRate < 50 && (maxDataBytes * frameRate < 300 || bitrateBps < 2400)))
        {
            int tocMode = _mode;
            if (tocMode == 0) tocMode = ModeSilk;
            if (frameRate > 100) tocMode = ModeCelt;
            if (frameRate < 50) tocMode = ModeSilk;
            data[0] = GenToc(tocMode, frameRate, _bandwidth == 0 ? (int)OpusBandwidth.Narrowband : _bandwidth, _streamChannels);
            return 1;
        }
        if (!useVbr)
        {
            int cbrBytes = Math.Min((bitrateBps + 4 * frameRate) / (8 * frameRate), maxDataBytes);
            bitrateBps = cbrBytes * (8 * frameRate);
            maxDataBytes = cbrBytes;
        }
        int equivRate = bitrateBps - 60 * (Fs / frameSize - 50);
        int voiceEst;
        if (_s.Signal == OpusSignal.Voice) voiceEst = 127;
        else if (_s.Signal == OpusSignal.Music) voiceEst = 0;
        else if (_application == OpusApplication.Voip) voiceEst = 115;
        else voiceEst = 48;

        if (_forceChannels != -1000 && _channels == 2)
            _streamChannels = _forceChannels;
        else if (_channels == 2)
        {
            int stereoThreshold = StereoMusicThreshold + ((voiceEst * voiceEst * (StereoVoiceThreshold - StereoMusicThreshold)) >> 14);
            if (_streamChannels == 2) stereoThreshold -= 4000;
            else stereoThreshold += 4000;
            _streamChannels = equivRate > stereoThreshold ? 2 : 1;
        }
        else _streamChannels = _channels;

        // SILK and hybrid packets are not produced by this encoder, so every frame uses the CELT mode.
        _mode = ModeCelt;

        {
            int bandwidth = (int)OpusBandwidth.Fullband;
            int equivRate2 = equivRate;
            int[] voiceTh, musicTh;
            if (_channels == 2 && _forceChannels != 1)
            {
                voiceTh = StereoVoiceBw;
                musicTh = StereoMusicBw;
            }
            else
            {
                voiceTh = MonoVoiceBw;
                musicTh = MonoMusicBw;
            }
            Span<int> th = stackalloc int[8];
            for (int i = 0; i < 8; i++)
                th[i] = musicTh[i] + ((voiceEst * voiceEst * (voiceTh[i] - musicTh[i])) >> 14);
            do
            {
                int threshold = th[2 * (bandwidth - (int)OpusBandwidth.Mediumband)];
                int hysteresis = th[2 * (bandwidth - (int)OpusBandwidth.Mediumband) + 1];
                if (!_first)
                {
                    if (_bandwidth >= bandwidth) threshold -= hysteresis;
                    else threshold += hysteresis;
                }
                if (equivRate2 >= threshold) break;
            } while (--bandwidth > (int)OpusBandwidth.Narrowband);
            _bandwidth = bandwidth;
        }
        if (_bandwidth > (int)_s.MaxBandwidth) _bandwidth = (int)_s.MaxBandwidth;
        if (_userBandwidth != -1000) _bandwidth = _userBandwidth;
        if (_bandwidth == (int)OpusBandwidth.Mediumband) _bandwidth = (int)OpusBandwidth.Wideband;

        // Long frames are coded as several 20 ms frames and packed into one packet (RFC 6716 section 3.2).
        if (frameSize > Fs / 50)
        {
            int nbFrames = frameSize > Fs / 25 ? 3 : 2;
            int bytesPerFrame = maxDataBytes / nbFrames - 3;
            if (bytesPerFrame < 2) return -1;
            int bakBandwidth = _userBandwidth, bakChannels = _forceChannels;
            _userBandwidth = _bandwidth;
            _forceChannels = _streamChannels;
            _prevChannels = _streamChannels;
            int chStride = Fs / 50 * _channels;
            for (int i = 0; i < nbFrames; i++)
            {
                int len = EncodeNative(pcm.Slice(i * chStride), Fs / 50, _subFrames.AsSpan(i * 1280, bytesPerFrame), bytesPerFrame, true);
                if (len < 0) return -1;
                _subLen[i] = len;
            }
            _userBandwidth = bakBandwidth;
            _forceChannels = bakChannels;
            int ret = Pack(nbFrames, data, maxDataBytes);
            if (ret < 0) return -1;
            if (!useVbr && ret >= 3 && !inner)
            {
                if (PadFrame(data, ret, maxDataBytes)) return -1;
                ret = maxDataBytes;
            }
            return ret;
        }

        int currBandwidth = _bandwidth;
        int bytesTarget = Math.Min(maxDataBytes, bitrateBps * frameSize / (Fs * 8)) - 1;
        var body = data.Slice(1);
        int ch = _channels;
        var pcmBuf = _pcmBuf;
        int encoderBuffer = Fs / 100;
        for (int i = 0; i < delayCompensation * ch; i++)
            pcmBuf[i] = _delayBuffer[(encoderBuffer - delayCompensation) * ch + i];
        int hpFreqSmth1 = Lin2Log(60) << 8;
        _variableHpSmth2Q15 = SilkMath.SMlaWB(_variableHpSmth2Q15, hpFreqSmth1 - _variableHpSmth2Q15, FixConst(0.015, 16));
        int cutoffHz = SilkMath.Log2Lin(_variableHpSmth2Q15 >> 8);
        if (_application == OpusApplication.Voip)
            HpCutoff(pcm, cutoffHz, pcmBuf.AsSpan(delayCompensation * ch), frameSize);
        else
            pcm.Slice(0, frameSize * ch).CopyTo(pcmBuf.AsSpan(delayCompensation * ch));

        _celt.End = EndBand(currBandwidth);
        _celt.StreamChannels = _streamChannels;
        _celt.Bitrate = -1;
        _celt.Vbr = false;
        _celt.DisablePrefilter = false;
        _celt.ForceIntra = false;
        _celt.Start = 0;
        int nbComprBytes;
        if (useVbr)
        {
            _celt.Vbr = true;
            _celt.ConstrainedVbr = vbrConstraint;
            _celt.Bitrate = bitrateBps;
            nbComprBytes = maxDataBytes - 1;
        }
        else nbComprBytes = bytesTarget;

        for (int i = 0; i < ch * (encoderBuffer - (frameSize + delayCompensation)); i++)
            _delayBuffer[i] = _delayBuffer[i + ch * frameSize];
        for (int i = Math.Max(0, ch * (encoderBuffer - (frameSize + delayCompensation))); i < encoderBuffer * ch; i++)
            _delayBuffer[i] = pcmBuf[(frameSize + delayCompensation - encoderBuffer) * ch + i];

        var enc = new RangeEncoder(body, maxDataBytes - 1);
        int celtRet = 0;
        if (enc.Tell() <= 8 * nbComprBytes)
        {
            celtRet = _celt.Encode(pcmBuf.AsSpan(0, frameSize * ch), frameSize, body, nbComprBytes, ref enc, true);
            if (celtRet < 0) return -1;
        }
        data[0] = GenToc(ModeCelt, Fs / frameSize, currBandwidth, _streamChannels);
        _rangeFinal = enc.Range;
        _prevChannels = _streamChannels;
        _prevFramesize = frameSize;
        _first = false;

        int result = celtRet;
        // In the unlikely case that the encoder busted its target, tell the decoder to conceal the frame.
        if (enc.Tell() > (maxDataBytes - 1) * 8)
        {
            data[1] = 0;
            result = 1;
            _rangeFinal = 0;
        }
        result += 1;
        if (!useVbr && result >= 3 && !inner)
        {
            if (PadFrame(data, result, maxDataBytes)) return -1;
            result = maxDataBytes;
        }
        return result;
    }

    // Packs the frames in _subFrames as one packet (opus_repacketizer_out).
    private int Pack(int count, Span<byte> data, int maxLen)
    {
        // Every frame has its own TOC byte first; the configuration of the first one is the packet's.
        int toc = _subFrames[0];
        int total = 0;
        Span<int> len = stackalloc int[3];
        for (int i = 0; i < count; i++) len[i] = _subLen[i] - 1;
        int pos = 0;
        switch (count)
        {
            case 2 when len[0] == len[1]:
                total = 2 * len[0] + 1;
                if (total > maxLen) return -1;
                data[pos++] = (byte)((toc & 0xFC) | 0x1);
                break;
            case 2:
                total = len[0] + len[1] + 2 + (len[0] >= 252 ? 1 : 0);
                if (total > maxLen) return -1;
                data[pos++] = (byte)((toc & 0xFC) | 0x2);
                pos += EncodeSize(len[0], data.Slice(pos));
                break;
            default:
            {
                bool vbr = false;
                for (int i = 1; i < count; i++)
                    if (len[i] != len[0]) vbr = true;
                if (vbr)
                {
                    total = 2;
                    for (int i = 0; i < count - 1; i++) total += 1 + (len[i] >= 252 ? 1 : 0) + len[i];
                    total += len[count - 1];
                    if (total > maxLen) return -1;
                    data[pos++] = (byte)((toc & 0xFC) | 0x3);
                    data[pos++] = (byte)(count | 0x80);
                    for (int i = 0; i < count - 1; i++) pos += EncodeSize(len[i], data.Slice(pos));
                }
                else
                {
                    total = count * len[0] + 2;
                    if (total > maxLen) return -1;
                    data[pos++] = (byte)((toc & 0xFC) | 0x3);
                    data[pos++] = (byte)count;
                }
                break;
            }
        }
        for (int i = 0; i < count; i++)
        {
            _subFrames.AsSpan(i * 1280 + 1, len[i]).CopyTo(data.Slice(pos));
            pos += len[i];
        }
        return total;
    }

    private static int EncodeSize(int size, Span<byte> data)
    {
        if (size < 252)
        {
            data[0] = (byte)size;
            return 1;
        }
        data[0] = (byte)(252 + (size & 0x3));
        data[1] = (byte)((size - data[0]) >> 2);
        return 2;
    }

    // Pads a one-frame packet with a code 3 header to the requested length (RFC 6716 section 3.2.5).
    private static bool PadFrame(Span<byte> data, int len, int newLen)
    {
        if (len == newLen) return false;
        if (len > newLen) return true;
        if ((data[0] & 0x3) == 0)
        {
            int padding = newLen - len;
            if (padding >= 2)
            {
                int nb255s = (padding - 2) / 255;
                for (int i = len - 1; i >= 1; i--) data[i + nb255s + 2] = data[i];
                data[0] |= 0x3;
                data[1] = 0x41;
                for (int i = 0; i < nb255s; i++) data[i + 2] = 255;
                data[nb255s + 2] = (byte)(padding - 255 * nb255s - 2);
                for (int i = len + 3 + nb255s; i < newLen; i++) data[i] = 0;
            }
            else
            {
                for (int i = len - 1; i >= 1; i--) data[i + 1] = data[i];
                data[0] |= 0x3;
                data[1] = 1;
            }
            return false;
        }
        return true;
    }
}
