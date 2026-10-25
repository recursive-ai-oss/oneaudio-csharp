namespace OneAudio.Opus;

/// <summary>
/// A packet-level Opus encoder (RFC 6716 section 5): turns interleaved 48 kHz PCM into Opus packets, one packet per
/// call. Frame sizes of 2.5, 5, 10, 20, 40 and 60 ms are accepted; 40 and 60 ms packets hold two or three 20 ms frames
/// (or one SILK packet of that length).
/// </summary>
/// <remarks>
/// <para>Like the reference encoder, it picks the SILK, hybrid or CELT mode and the audio bandwidth from the bitrate, the frame
/// size and the application, and switches between them with a redundant CELT frame and decoder-state prefill. Settings
/// can be changed between packets. Input is interleaved <see cref="float"/> with full scale ±1.0, clipped at ±2.0 for
/// CELT and rounded to 16 bits for SILK. Output is deterministic for identical input and settings on a platform. After
/// construction, <see cref="Encode(ReadOnlySpan{float}, int, Span{byte})"/> allocates nothing. Instances are not
/// thread safe.</para>
/// <para>Derived from the reference encoder in RFC 6716 (Simplified BSD licence, see THIRD-PARTY-NOTICES.md).</para>
/// </remarks>
public sealed class OpusEncoder
{
    private const int ModeSilk = 1000;
    private const int ModeHybrid = 1001;
    private const int ModeCelt = 1002;
    private const int AutoValue = -1000;
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
    private readonly SilkEncoder _silk = new();
    private readonly SilkEncControl _ctl = new();
    private readonly short[] _pcmSilk = new short[2880 * 2];
    private readonly float[] _tmpPrefill = new float[(Fs / 400) * 2];
    private readonly byte[] _dummy2 = new byte[2];
    private int _prevMode;
    private bool _silkBwSwitch;
    private short _hybridStereoWidthQ14 = 1 << 14;
    private int _userForcedMode = AutoValue;

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
        _ctl.Complexity = _s.Complexity;
        _ctl.PacketLossPercentage = _s.PacketLossPercent;
        _ctl.UseInBandFec = _s.InBandFec;
        _ctl.UseDtx = _s.Dtx;
    }

    private void ResetControl()
    {
        var c = _ctl;
        c.NChannelsApi = _channels;
        c.NChannelsInternal = _channels;
        c.ApiSampleRate = Fs;
        c.MaxInternalSampleRate = 16000;
        c.MinInternalSampleRate = 8000;
        c.DesiredInternalSampleRate = 16000;
        c.PayloadSizeMs = 20;
        c.BitRate = 25000;
        c.UseCbr = false;
        c.ToMono = false;
        c.OpusCanSwitch = false;
        c.InternalSampleRate = 0;
        c.AllowBandwidthSwitch = false;
        c.InWbModeWithoutVariableLp = false;
        c.StereoWidthQ14 = 0;
        c.SwitchReady = false;
        c.Complexity = _s.Complexity;
        c.PacketLossPercentage = _s.PacketLossPercent;
        c.UseInBandFec = _s.InBandFec;
        c.UseDtx = _s.Dtx;
    }

    /// <summary>Clears all signal history, as for a new stream.</summary>
    public void Reset()
    {
        _celt.Reset();
        _silk.Init();
        ResetControl();
        _prevMode = 0;
        _silkBwSwitch = false;
        _hybridStereoWidthQ14 = 1 << 14;
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

    /// <summary>Forces the coding mode (1000 SILK, 1001 hybrid, 1002 CELT) or -1000 for automatic; for tests against the reference encoder.</summary>
    internal void ForceMode(int mode) => _userForcedMode = mode;

    private static void CheckFrame(int frameSize)
    {
        if (frameSize != 120 && frameSize != 240 && frameSize != 480 && frameSize != 960 && frameSize != 1920 && frameSize != 2880)
            throw new ArgumentException("frameSize must be 120, 240, 480, 960, 1920 or 2880.", nameof(frameSize));
    }

    /// <summary>
    /// Encodes a packet in the self-delimited framing of RFC 6716 appendix B, used for all but the last stream of a
    /// multistream packet. <paramref name="packet"/> needs two bytes more than the size limit.
    /// </summary>
    internal int EncodeSelfDelimited(ReadOnlySpan<float> pcm, int frameSize, Span<byte> packet, int maxBytes)
    {
        CheckFrame(frameSize);
        return EncodeNative(pcm, frameSize, packet, Math.Min(maxBytes, 1276), false, true);
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

    private static short Float2Int16(float x)
    {
        x *= 32768f;
        x = Math.Max(x, -32768f);
        x = Math.Min(x, 32767f);
        return (short)SilkFlp.Float2Int(x);
    }

    // g1/g2 are the previous and new stereo widths; blends the side signal in over the CELT overlap window (opus_encoder.c stereo_fade).
    private static void StereoFade(Span<float> buf, float g1, float g2, int overlap, int frameSize, int channels)
    {
        g1 = 1f - g1;
        g2 = 1f - g2;
        var window = CeltTables.Window;
        int i;
        for (i = 0; i < overlap; i++)
        {
            float w = window[i] * window[i];
            float g = w * g2 + (1f - w) * g1;
            float diff = 0.5f * (buf[i * channels] - buf[i * channels + 1]);
            diff = g * diff;
            buf[i * channels] = buf[i * channels] - diff;
            buf[i * channels + 1] = buf[i * channels + 1] + diff;
        }
        for (; i < frameSize; i++)
        {
            float diff = 0.5f * (buf[i * channels] - buf[i * channels + 1]);
            diff = g2 * diff;
            buf[i * channels] = buf[i * channels] - diff;
            buf[i * channels + 1] = buf[i * channels + 1] + diff;
        }
    }

    private int EncodeNative(ReadOnlySpan<float> pcm, int frameSize, Span<byte> data, int maxDataBytes, bool inner, bool selfDelimited = false)
    {
        maxDataBytes = Math.Min(maxDataBytes, 1276);
        _rangeFinal = 0;
        if (maxDataBytes <= 0) return -1;
        var ctl = _ctl;
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
        int maxRate = frameRate * maxDataBytes * 8;
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

        // Mode choice (RFC 6716 section 2.1.3 leaves it to the encoder): CELT above a bitrate threshold, SILK below it.
        if (_application == OpusApplication.RestrictedLowDelay)
            _mode = ModeCelt;
        else if (_userForcedMode == AutoValue)
        {
            const int modeVoice = 48000, modeMusic = 24000;
            int threshold = modeMusic + ((voiceEst * voiceEst * (modeVoice - modeMusic)) >> 14);
            if (_prevMode == ModeCelt) threshold -= 4000;
            else if (_prevMode > 0) threshold += 4000;
            _mode = equivRate >= threshold ? ModeCelt : ModeSilk;
            if (ctl.UseInBandFec && ctl.PacketLossPercentage > (128 - voiceEst) >> 4) _mode = ModeSilk;
            if (ctl.UseDtx && voiceEst > 100) _mode = ModeSilk;
        }
        else _mode = _userForcedMode;
        if (_mode != ModeCelt && frameSize < Fs / 100) _mode = ModeCelt;

        if (_streamChannels == 1 && _prevChannels == 2 && !ctl.ToMono && _mode != ModeCelt && _prevMode != ModeCelt)
        {
            // Stereo to mono: SILK fades the side channel out over this packet before the stream really goes mono.
            ctl.ToMono = true;
            _streamChannels = 2;
        }
        else ctl.ToMono = false;

        bool redundancy = false, celtToSilk = false, toCelt = false, prefill = false;
        if (_prevMode > 0 && ((_mode != ModeCelt && _prevMode == ModeCelt) || (_mode == ModeCelt && _prevMode != ModeCelt)))
        {
            redundancy = true;
            celtToSilk = _mode != ModeCelt;
            if (!celtToSilk)
            {
                // Switching to CELT: this frame still uses the previous mode and carries a redundant CELT frame.
                if (frameSize >= Fs / 100)
                {
                    _mode = _prevMode;
                    toCelt = true;
                }
                else redundancy = false;
            }
        }
        if (_silkBwSwitch)
        {
            redundancy = true;
            celtToSilk = true;
            _silkBwSwitch = false;
        }
        if (_mode != ModeCelt && _prevMode == ModeCelt)
        {
            _silk.Init();
            prefill = true;
        }

        if (_mode == ModeCelt || _first || ctl.AllowBandwidthSwitch)
        {
            int bandwidth = (int)OpusBandwidth.Fullband;
            int equivRate2 = equivRate;
            if (_mode != ModeCelt)
            {
                equivRate2 = equivRate2 * (45 + ctl.Complexity) / 50;
                if (!useVbr) equivRate2 -= 1000;
            }
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
            // Without the variable low-pass the SILK layer cannot code more than wideband.
            if (!_first && _mode != ModeCelt && !ctl.InWbModeWithoutVariableLp && _bandwidth > (int)OpusBandwidth.Wideband)
                _bandwidth = (int)OpusBandwidth.Wideband;
        }
        if (_bandwidth > (int)_s.MaxBandwidth) _bandwidth = (int)_s.MaxBandwidth;
        if (_userBandwidth != -1000) _bandwidth = _userBandwidth;
        if (_mode != ModeCelt && maxRate < 15000) _bandwidth = Math.Min(_bandwidth, (int)OpusBandwidth.Wideband);
        if (maxDataBytes < (frameRate > 50 ? 12000 : 8000) * frameSize / (Fs * 8)) _mode = ModeCelt;
        if (_mode == ModeCelt && _bandwidth == (int)OpusBandwidth.Mediumband) _bandwidth = (int)OpusBandwidth.Wideband;

        // Long frames are coded as several 20 ms frames and packed into one packet (RFC 6716 section 3.2).
        if (frameSize > Fs / 50 && (_mode == ModeCelt || _bandwidth > (int)OpusBandwidth.Wideband))
        {
            int nbFrames = frameSize > Fs / 25 ? 3 : 2;
            int bytesPerFrame = maxDataBytes / nbFrames - 3;
            if (bytesPerFrame < 2) return -1;
            int bakMode = _userForcedMode, bakBandwidth = _userBandwidth, bakChannels = _forceChannels;
            _userForcedMode = _mode;
            _userBandwidth = _bandwidth;
            _forceChannels = _streamChannels;
            bool bakToMono = ctl.ToMono;
            if (bakToMono) _forceChannels = 1;
            else _prevChannels = _streamChannels;
            int chStride = Fs / 50 * _channels;
            for (int i = 0; i < nbFrames; i++)
            {
                ctl.ToMono = false;
                if (toCelt && i == nbFrames - 1) _userForcedMode = ModeCelt;
                int len = EncodeNative(pcm.Slice(i * chStride), Fs / 50, _subFrames.AsSpan(i * 1280, bytesPerFrame), bytesPerFrame, true);
                if (len < 0) return -1;
                _subLen[i] = len;
            }
            _userForcedMode = bakMode;
            _userBandwidth = bakBandwidth;
            _forceChannels = bakChannels;
            ctl.ToMono = bakToMono;
            return Pack(nbFrames, data, selfDelimited ? data.Length : maxDataBytes, selfDelimited);
        }

        int currBandwidth = _bandwidth;
        if (_mode == ModeSilk && currBandwidth > (int)OpusBandwidth.Wideband) _mode = ModeHybrid;
        if (_mode == ModeHybrid && currBandwidth <= (int)OpusBandwidth.Wideband) _mode = ModeSilk;
        int bytesTarget = Math.Min(maxDataBytes, bitrateBps * frameSize / (Fs * 8)) - 1;
        var body = data.Slice(1);
        var enc = new RangeEncoder(body, maxDataBytes - 1);
        int ch = _channels;
        var pcmBuf = _pcmBuf;
        int encoderBuffer = Fs / 100;
        for (int i = 0; i < delayCompensation * ch; i++)
            pcmBuf[i] = _delayBuffer[(encoderBuffer - delayCompensation) * ch + i];
        int hpFreqSmth1 = _mode == ModeCelt ? Lin2Log(60) << 8 : _silk.Channel0.VariableHpSmth1Q15;
        _variableHpSmth2Q15 = SilkMath.SMlaWB(_variableHpSmth2Q15, hpFreqSmth1 - _variableHpSmth2Q15, FixConst(0.015, 16));
        int cutoffHz = SilkMath.Log2Lin(_variableHpSmth2Q15 >> 8);
        if (_application == OpusApplication.Voip)
            HpCutoff(pcm, cutoffHz, pcmBuf.AsSpan(delayCompensation * ch), frameSize);
        else
            pcm.Slice(0, frameSize * ch).CopyTo(pcmBuf.AsSpan(delayCompensation * ch));

        int ret = 0;
        if (_mode != ModeCelt)
        {
            var pcmSilk = _pcmSilk;
            ctl.BitRate = 8 * bytesTarget * frameRate;
            if (_mode == ModeHybrid)
            {
                ctl.BitRate /= _streamChannels;
                if (currBandwidth == (int)OpusBandwidth.SuperWideband)
                {
                    if (Fs == 100 * frameSize) ctl.BitRate = ((ctl.BitRate + 2000 + (useVbr ? 1 : 0) * 1000) * 2) / 3;
                    else ctl.BitRate = ((ctl.BitRate + 1000 + (useVbr ? 1 : 0) * 1000) * 2) / 3;
                }
                else
                {
                    if (Fs == 100 * frameSize) ctl.BitRate = (ctl.BitRate + 8000 + (useVbr ? 1 : 0) * 3000) / 2;
                    else ctl.BitRate = (ctl.BitRate + 9000 + (useVbr ? 1 : 0) * 1000) / 2;
                }
                ctl.BitRate *= _streamChannels;
                if (ctl.BitRate > (bitrateBps - 8 * Fs / frameSize) * 4 / 5)
                    ctl.BitRate = (bitrateBps - 8 * Fs / frameSize) * 4 / 5;
            }
            ctl.PayloadSizeMs = 1000 * frameSize / Fs;
            ctl.NChannelsApi = _channels;
            ctl.NChannelsInternal = _streamChannels;
            if (currBandwidth == (int)OpusBandwidth.Narrowband) ctl.DesiredInternalSampleRate = 8000;
            else if (currBandwidth == (int)OpusBandwidth.Mediumband) ctl.DesiredInternalSampleRate = 12000;
            else ctl.DesiredInternalSampleRate = 16000;
            ctl.MinInternalSampleRate = _mode == ModeHybrid ? 16000 : 8000;
            if (_mode == ModeSilk)
            {
                int effectiveMaxRate = maxRate;
                ctl.MaxInternalSampleRate = 16000;
                if (frameRate > 50) effectiveMaxRate = effectiveMaxRate * 2 / 3;
                if (effectiveMaxRate < 13000)
                {
                    ctl.MaxInternalSampleRate = 12000;
                    ctl.DesiredInternalSampleRate = Math.Min(12000, ctl.DesiredInternalSampleRate);
                }
                if (effectiveMaxRate < 9600)
                {
                    ctl.MaxInternalSampleRate = 8000;
                    ctl.DesiredInternalSampleRate = Math.Min(8000, ctl.DesiredInternalSampleRate);
                }
            }
            else ctl.MaxInternalSampleRate = 16000;
            ctl.UseCbr = !useVbr;
            int nBytes = Math.Min(1275, maxDataBytes - 1);
            ctl.MaxBits = nBytes * 8;
            if (_mode == ModeHybrid) ctl.MaxBits = ctl.MaxBits * 9 / 10;
            if (ctl.UseCbr)
            {
                ctl.MaxBits = (ctl.BitRate * frameSize / (Fs * 8)) * 8;
                ctl.BitRate = Math.Max(1, ctl.BitRate - 2000);
            }
            if (redundancy) ctl.MaxBits -= ctl.MaxBits / (1 + frameSize / (Fs / 200));
            if (prefill)
            {
                // Replay the last 10 ms so SILK starts with warmed-up filters after CELT-only packets.
                for (int i = 0; i < encoderBuffer * ch; i++) pcmSilk[i] = Float2Int16(_delayBuffer[i]);
                var dummyRc = new RangeEncoder(Span<byte>.Empty, 0);
                int zero = 0;
                _silk.Encode(ctl, pcmSilk.AsSpan(0, encoderBuffer * ch), encoderBuffer, ref dummyRc, ref zero, true);
            }
            for (int i = 0; i < frameSize * ch; i++) pcmSilk[i] = Float2Int16(pcmBuf[delayCompensation * ch + i]);
            _silk.Encode(ctl, pcmSilk.AsSpan(0, frameSize * ch), frameSize, ref enc, ref nBytes, false);
            if (nBytes == 0)
            {
                // DTX: nothing to send.
                _rangeFinal = 0;
                data[0] = GenToc(_mode, Fs / frameSize, currBandwidth, _streamChannels);
                return 1;
            }
            if (_mode == ModeSilk)
            {
                if (ctl.InternalSampleRate == 8000) currBandwidth = (int)OpusBandwidth.Narrowband;
                else if (ctl.InternalSampleRate == 12000) currBandwidth = (int)OpusBandwidth.Mediumband;
                else if (ctl.InternalSampleRate == 16000) currBandwidth = (int)OpusBandwidth.Wideband;
            }
            ctl.OpusCanSwitch = ctl.SwitchReady;
            if (ctl.OpusCanSwitch)
            {
                redundancy = true;
                celtToSilk = false;
                _silkBwSwitch = true;
            }
        }

        _celt.End = EndBand(currBandwidth);
        _celt.StreamChannels = _streamChannels;
        _celt.Bitrate = -1;
        int nbComprBytes;
        if (_mode != ModeSilk)
        {
            _celt.Vbr = false;
            _celt.DisablePrefilter = false;
            _celt.ForceIntra = false;
            if (_mode == ModeHybrid)
            {
                int len = (enc.Tell() + 7) >> 3;
                if (redundancy) len += 3;
                if (useVbr) nbComprBytes = len + bytesTarget - (ctl.BitRate * frameSize) / (8 * Fs);
                else nbComprBytes = len > bytesTarget ? len : bytesTarget;
            }
            else if (useVbr)
            {
                _celt.Vbr = true;
                _celt.ConstrainedVbr = vbrConstraint;
                _celt.Bitrate = bitrateBps;
                nbComprBytes = maxDataBytes - 1;
            }
            else nbComprBytes = bytesTarget;
        }
        else nbComprBytes = 0;

        var tmpPrefill = _tmpPrefill;
        bool needPrefill = _mode != ModeSilk && _mode != _prevMode && _prevMode > 0;
        if (needPrefill)
            for (int i = 0; i < ch * (Fs / 400); i++)
                tmpPrefill[i] = _delayBuffer[(encoderBuffer - _delayCompensation - Fs / 400) * ch + i];
        for (int i = 0; i < ch * (encoderBuffer - (frameSize + delayCompensation)); i++)
            _delayBuffer[i] = _delayBuffer[i + ch * frameSize];
        for (int i = Math.Max(0, ch * (encoderBuffer - (frameSize + delayCompensation))); i < encoderBuffer * ch; i++)
            _delayBuffer[i] = pcmBuf[(frameSize + delayCompensation - encoderBuffer) * ch + i];

        if (_mode != ModeHybrid || _streamChannels == 1) ctl.StereoWidthQ14 = 1 << 14;
        if (ch == 2 && (_hybridStereoWidthQ14 < (1 << 14) || ctl.StereoWidthQ14 < (1 << 14)))
        {
            float g1 = (float)(_hybridStereoWidthQ14 * (1.0 / 16384));
            float g2 = (float)(ctl.StereoWidthQ14 * (1.0 / 16384));
            StereoFade(pcmBuf, g1, g2, CeltTables.Overlap, frameSize, ch);
            _hybridStereoWidthQ14 = (short)ctl.StereoWidthQ14;
        }

        int redundancyBytes = 0;
        if (_mode != ModeCelt && enc.Tell() + 17 + 20 * (_mode == ModeHybrid ? 1 : 0) <= 8 * (maxDataBytes - 1))
        {
            // RFC 6716 section 4.5.1: redundancy flag (hybrid only), direction, and length (hybrid only).
            if (_mode == ModeHybrid && (redundancy || enc.Tell() + 37 <= 8 * nbComprBytes))
                enc.BitLogP(redundancy ? 1 : 0, 12);
            if (redundancy)
            {
                enc.BitLogP(celtToSilk ? 1 : 0, 1);
                int maxRedundancy = _mode == ModeHybrid
                    ? (maxDataBytes - 1) - nbComprBytes - 1
                    : (maxDataBytes - 1) - ((enc.Tell() + 7) >> 3);
                redundancyBytes = Math.Min(maxRedundancy, bitrateBps / 1600);
                redundancyBytes = Math.Min(257, Math.Max(2, redundancyBytes));
                if (_mode == ModeHybrid) enc.EncodeUInt((uint)(redundancyBytes - 2), 256);
            }
        }
        else redundancy = false;
        if (!redundancy) _silkBwSwitch = false;

        int startBand = _mode != ModeCelt ? 17 : 0;
        if (_mode == ModeSilk)
        {
            ret = (enc.Tell() + 7) >> 3;
            enc.Done();
            nbComprBytes = ret;
        }
        else
        {
            nbComprBytes = Math.Min((maxDataBytes - 1) - redundancyBytes, nbComprBytes);
            enc.Shrink(nbComprBytes);
        }

        uint redundantRng = 0;
        RangeEncoder side = default;
        if (redundancy && celtToSilk)
        {
            _celt.Start = 0;
            _celt.Vbr = false;
            int err = _celt.Encode(pcmBuf.AsSpan(0, (Fs / 200) * ch), Fs / 200, body.Slice(nbComprBytes, redundancyBytes), redundancyBytes, ref side, false);
            if (err < 0) return -1;
            redundantRng = _celt.FinalRange;
            _celt.Reset();
        }
        _celt.Start = startBand;
        if (_mode != ModeSilk)
        {
            if (_mode != _prevMode && _prevMode > 0)
            {
                _celt.Reset();
                _celt.Encode(tmpPrefill.AsSpan(0, (Fs / 400) * ch), Fs / 400, _dummy2, 2, ref side, false);
                _celt.DisablePrefilter = true;
                _celt.ForceIntra = true;
            }
            if (enc.Tell() <= 8 * nbComprBytes)
            {
                ret = _celt.Encode(pcmBuf.AsSpan(0, frameSize * ch), frameSize, Span<byte>.Empty, nbComprBytes, ref enc, true);
                if (ret < 0) return -1;
            }
        }
        if (redundancy && !celtToSilk)
        {
            int n2 = Fs / 200, n4 = Fs / 400;
            _celt.Reset();
            _celt.Start = 0;
            _celt.DisablePrefilter = true;
            _celt.ForceIntra = true;
            _celt.Encode(pcmBuf.AsSpan(ch * (frameSize - n2 - n4)), n4, _dummy2, 2, ref side, false);
            int err = _celt.Encode(pcmBuf.AsSpan(ch * (frameSize - n2)), n2, body.Slice(nbComprBytes, redundancyBytes), redundancyBytes, ref side, false);
            if (err < 0) return -1;
            redundantRng = _celt.FinalRange;
        }
        data[0] = GenToc(_mode, Fs / frameSize, currBandwidth, _streamChannels);
        _rangeFinal = enc.Range ^ redundantRng;
        _prevMode = toCelt ? ModeCelt : _mode;
        _prevChannels = _streamChannels;
        _prevFramesize = frameSize;
        _first = false;

        // In the unlikely case that the encoder busted its target, tell the decoder to conceal the frame.
        if (enc.Tell() > (maxDataBytes - 1) * 8)
        {
            data[1] = 0;
            ret = 1;
            _rangeFinal = 0;
        }
        else if (_mode == ModeSilk && !redundancy)
        {
            while (ret > 2 && data[ret] == 0) ret--;
        }
        int result = ret + 1 + redundancyBytes;
        if (selfDelimited)
        {
            int frameLen = result - 1;
            int sb = frameLen >= 252 ? 2 : 1;
            data.Slice(1, frameLen).CopyTo(data.Slice(1 + sb));
            EncodeSize(frameLen, data.Slice(1));
            return result + sb;
        }
        if (!useVbr && result >= 3 && !inner)
        {
            if (PadFrame(data, result, maxDataBytes)) return -1;
            result = maxDataBytes;
        }
        return result;
    }

    // Packs the frames in _subFrames as one packet (opus_repacketizer_out).
    private int Pack(int count, Span<byte> data, int maxLen, bool selfDelimited)
    {
        // Every frame has its own TOC byte first; the configuration of the first one is the packet's.
        int toc = _subFrames[0];
        int total = selfDelimited ? 1 + (_subLen[count - 1] - 1 >= 252 ? 1 : 0) : 0;
        Span<int> len = stackalloc int[3];
        for (int i = 0; i < count; i++) len[i] = _subLen[i] - 1;
        int pos = 0;
        switch (count)
        {
            case 2 when len[0] == len[1]:
                total += 2 * len[0] + 1;
                if (total > maxLen) return -1;
                data[pos++] = (byte)((toc & 0xFC) | 0x1);
                break;
            case 2:
                total += len[0] + len[1] + 2 + (len[0] >= 252 ? 1 : 0);
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
                    total += 2;
                    for (int i = 0; i < count - 1; i++) total += 1 + (len[i] >= 252 ? 1 : 0) + len[i];
                    total += len[count - 1];
                    if (total > maxLen) return -1;
                    data[pos++] = (byte)((toc & 0xFC) | 0x3);
                    data[pos++] = (byte)(count | 0x80);
                    for (int i = 0; i < count - 1; i++) pos += EncodeSize(len[i], data.Slice(pos));
                }
                else
                {
                    total += count * len[0] + 2;
                    if (total > maxLen) return -1;
                    data[pos++] = (byte)((toc & 0xFC) | 0x3);
                    data[pos++] = (byte)count;
                }
                break;
            }
        }
        if (selfDelimited) pos += EncodeSize(len[count - 1], data.Slice(pos));
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
