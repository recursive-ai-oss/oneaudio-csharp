using static OneAudio.Opus.SilkMath;

namespace OneAudio.Opus;

/// <summary>
/// The SILK layer's decoder front end (RFC 6716 section 4.2): frame sequencing across a packet, mid/side stereo
/// prediction, and resampling to the output rate. Derived from the reference decoder in RFC 6716 (Simplified BSD
/// licence, see THIRD-PARTY-NOTICES.md).
/// </summary>
internal sealed class SilkDecoder
{
    private const int MaxFrameSamples = 16 * 20;

    private readonly SilkChannel[] _ch = [new SilkChannel(), new SilkChannel()];
    private readonly short[] _predPrevQ13 = new short[2];
    private readonly short[] _sMid = new short[2];
    private readonly short[] _sSide = new short[2];
    private int _nChannelsApi;
    private int _nChannelsInternal;
    private bool _prevDecodeOnlyMiddle;

    private readonly short[][] _out1 = [new short[MaxFrameSamples + 2], new short[MaxFrameSamples + 2]];
    private readonly short[] _out2 = new short[48 * 20];

    // Control values the caller sets before each call (silk_DecControlStruct).
    public int ApiSampleRate;
    public int ChannelsApi;
    public int ChannelsInternal;
    public int InternalSampleRate;
    public int PayloadSizeMs;
    /// <summary>Pitch lag of the last voiced frame at the output rate, or 0.</summary>
    public int PrevPitchLag;

    /// <summary>Resets everything, as for a new stream (silk_InitDecoder).</summary>
    public void Init()
    {
        _ch[0].Init();
        _ch[1].Init();
        Array.Clear(_predPrevQ13);
        Array.Clear(_sMid);
        Array.Clear(_sSide);
        _prevDecodeOnlyMiddle = false;
        _nChannelsApi = 0;
        _nChannelsInternal = 0;
    }

    /// <summary>
    /// Decodes or conceals one SILK frame (10 or 20 ms) and writes interleaved samples at the API rate.
    /// </summary>
    /// <param name="rd">Range decoder positioned in the SILK data.</param>
    /// <param name="lost">True to conceal a lost packet (the range decoder is not read).</param>
    /// <param name="newPacket">True for the first call of a packet.</param>
    /// <param name="samplesOut">Interleaved output at <see cref="ApiSampleRate"/>.</param>
    /// <param name="nSamplesOut">Samples per channel written.</param>
    public void Decode(ref RangeDecoder rd, bool lost, bool newPacket, Span<short> samplesOut, out int nSamplesOut)
    {
        int decodeOnlyMiddle = 0;
        Span<int> msPredQ13 = stackalloc int[2];
        var ch = _ch;

        if (newPacket)
            for (int n = 0; n < ChannelsInternal; n++) ch[n].NFramesDecoded = 0;

        if (ChannelsInternal > _nChannelsInternal) ch[1].Init();

        bool stereoToMono = ChannelsInternal == 1 && _nChannelsInternal == 2 && InternalSampleRate == 1000 * ch[0].FsKHz;

        if (ch[0].NFramesDecoded == 0)
        {
            for (int n = 0; n < ChannelsInternal; n++)
            {
                switch (PayloadSizeMs)
                {
                    case 0:
                    case 10: ch[n].NFramesPerPacket = 1; ch[n].NbSubfr = 2; break;
                    case 20: ch[n].NFramesPerPacket = 1; ch[n].NbSubfr = 4; break;
                    case 40: ch[n].NFramesPerPacket = 2; ch[n].NbSubfr = 4; break;
                    case 60: ch[n].NFramesPerPacket = 3; ch[n].NbSubfr = 4; break;
                    default: throw new AudioFormatException("Invalid SILK frame size.");
                }
                int fsKHzDec = (InternalSampleRate >> 10) + 1;
                if (fsKHzDec != 8 && fsKHzDec != 12 && fsKHzDec != 16)
                    throw new AudioFormatException("Invalid SILK sample rate.");
                ch[n].SetFs(fsKHzDec, ApiSampleRate);
            }
        }

        if (ChannelsApi == 2 && ChannelsInternal == 2 && (_nChannelsApi == 1 || _nChannelsInternal == 1))
        {
            Array.Clear(_predPrevQ13);
            Array.Clear(_sSide);
            ch[1].Resampler.CopyStateFrom(ch[0].Resampler);
        }
        _nChannelsApi = ChannelsApi;
        _nChannelsInternal = ChannelsInternal;

        if (ApiSampleRate > 48000 || ApiSampleRate < 8000)
            throw new AudioFormatException("Invalid API sample rate.");

        if (!lost && ch[0].NFramesDecoded == 0)
        {
            // Voice activity and redundancy flags for every frame in the packet (RFC 6716 section 4.2.3).
            for (int n = 0; n < ChannelsInternal; n++)
            {
                for (int i = 0; i < ch[n].NFramesPerPacket; i++) ch[n].VadFlags[i] = rd.BitLogP(1);
                ch[n].LbrrFlag = rd.BitLogP(1);
            }
            for (int n = 0; n < ChannelsInternal; n++)
            {
                Array.Clear(ch[n].LbrrFlags);
                if (ch[n].LbrrFlag != 0)
                {
                    if (ch[n].NFramesPerPacket == 1)
                    {
                        ch[n].LbrrFlags[0] = 1;
                    }
                    else
                    {
                        int symbol = rd.ICdf(ch[n].NFramesPerPacket == 2 ? SilkTables.LBRRFlags2ICDF : SilkTables.LBRRFlags3ICDF, 8) + 1;
                        for (int i = 0; i < ch[n].NFramesPerPacket; i++) ch[n].LbrrFlags[i] = (symbol >> i) & 1;
                    }
                }
            }

            // Redundant (LBRR) frames are only for loss recovery; read them to stay aligned, discard the audio.
            for (int i = 0; i < ch[0].NFramesPerPacket; i++)
            {
                for (int n = 0; n < ChannelsInternal; n++)
                {
                    if (ch[n].LbrrFlags[i] != 0)
                    {
                        if (ChannelsInternal == 2 && n == 0)
                        {
                            StereoDecodePred(ref rd, msPredQ13);
                            if (ch[1].LbrrFlags[i] == 0) StereoDecodeMidOnly(ref rd);
                        }
                        int cond = i > 0 && ch[n].LbrrFlags[i - 1] != 0 ? 2 : 0;
                        ch[n].SkipLbrrFrame(ref rd, i, cond);
                    }
                }
            }
        }

        if (ChannelsInternal == 2)
        {
            if (!lost)
            {
                StereoDecodePred(ref rd, msPredQ13);
                if (ch[1].VadFlags[ch[0].NFramesDecoded] == 0) decodeOnlyMiddle = StereoDecodeMidOnly(ref rd);
                else decodeOnlyMiddle = 0;
            }
            else
            {
                msPredQ13[0] = _predPrevQ13[0];
                msPredQ13[1] = _predPrevQ13[1];
            }
        }

        if (ChannelsInternal == 2 && decodeOnlyMiddle == 0 && _prevDecodeOnlyMiddle)
            ch[1].RestartAfterMidOnly();

        bool hasSide = lost ? !_prevDecodeOnlyMiddle : decodeOnlyMiddle == 0;

        int nSamplesOutDec = 0;
        for (int n = 0; n < ChannelsInternal; n++)
        {
            var o = _out1[n].AsSpan(2);
            if (n == 0 || hasSide)
            {
                int frameIndex = ch[0].NFramesDecoded - n;
                int cond;
                if (frameIndex <= 0) cond = 0;                        // independent coding
                else if (n > 0 && _prevDecodeOnlyMiddle) cond = 1;    // independent, no LTP scaling
                else cond = 2;                                        // conditional
                ch[n].DecodeFrame(ref rd, o, lost, cond);
                nSamplesOutDec = ch[n].FrameLength;
            }
            else
            {
                o.Slice(0, nSamplesOutDec).Clear();
            }
            ch[n].NFramesDecoded++;
        }

        if (ChannelsApi == 2 && ChannelsInternal == 2)
        {
            StereoMsToLr(_out1[0], _out1[1], msPredQ13, ch[0].FsKHz, nSamplesOutDec);
        }
        else
        {
            _sMid.CopyTo(_out1[0], 0);
            Array.Copy(_out1[0], nSamplesOutDec, _sMid, 0, 2);
        }
        nSamplesOut = nSamplesOutDec * ApiSampleRate / (ch[0].FsKHz * 1000);

        Span<short> resampleOut = ChannelsApi == 2 ? _out2 : samplesOut;
        int nMin = Math.Min(ChannelsApi, ChannelsInternal);
        for (int n = 0; n < nMin; n++)
        {
            ch[n].Resampler.Resample(resampleOut, _out1[n].AsSpan(1), nSamplesOutDec);
            if (ChannelsApi == 2)
                for (int i = 0; i < nSamplesOut; i++) samplesOut[n + 2 * i] = resampleOut[i];
        }

        if (ChannelsApi == 2 && ChannelsInternal == 1)
        {
            if (stereoToMono)
            {
                // The right channel's resampler was not running while the stream was mono; run it now.
                ch[1].Resampler.Resample(resampleOut, _out1[0].AsSpan(1), nSamplesOutDec);
                for (int i = 0; i < nSamplesOut; i++) samplesOut[1 + 2 * i] = resampleOut[i];
            }
            else
            {
                for (int i = 0; i < nSamplesOut; i++) samplesOut[1 + 2 * i] = samplesOut[2 * i];
            }
        }

        if (ch[0].PrevSignalType == 2)
        {
            int mult = ch[0].FsKHz == 8 ? 6 : ch[0].FsKHz == 12 ? 4 : 3;
            PrevPitchLag = ch[0].LagPrev * mult;
        }
        else
        {
            PrevPitchLag = 0;
        }

        if (lost)
        {
            // Remove the gain clamping so the energy does not bounce back after a loss.
            for (int i = 0; i < _nChannelsInternal; i++) ch[i].LastGainIndex = 10;
        }
        else
        {
            _prevDecodeOnlyMiddle = decodeOnlyMiddle != 0;
        }
    }

    private static void StereoDecodePred(ref RangeDecoder rd, scoped Span<int> predQ13)
    {
        Span<int> ix = stackalloc int[6]; // [n][0..2]
        int n = rd.ICdf(SilkTables.StereoPredJointICDF, 8);
        ix[2] = n / 5;
        ix[5] = n - 5 * ix[2];
        for (n = 0; n < 2; n++)
        {
            ix[n * 3] = rd.ICdf(SilkTables.Uniform3ICDF, 8);
            ix[n * 3 + 1] = rd.ICdf(SilkTables.Uniform5ICDF, 8);
        }
        for (n = 0; n < 2; n++)
        {
            ix[n * 3] += 3 * ix[n * 3 + 2];
            int lowQ13 = SilkTables.StereoPredQuantQ13[ix[n * 3]];
            int stepQ13 = SMulWB(SilkTables.StereoPredQuantQ13[ix[n * 3] + 1] - lowQ13, 6554); // 0.5 / 5 in Q16
            predQ13[n] = SMlaBB(lowQ13, stepQ13, 2 * ix[n * 3 + 1] + 1);
        }
        predQ13[0] -= predQ13[1];
    }

    private static int StereoDecodeMidOnly(ref RangeDecoder rd) => rd.ICdf(SilkTables.StereoOnlyCodeMidICDF, 8);

    private void StereoMsToLr(short[] x1, short[] x2, ReadOnlySpan<int> predQ13, int fsKHz, int frameLength)
    {
        _sMid.CopyTo(x1, 0);
        _sSide.CopyTo(x2, 0);
        Array.Copy(x1, frameLength, _sMid, 0, 2);
        Array.Copy(x2, frameLength, _sSide, 0, 2);

        int pred0 = _predPrevQ13[0];
        int pred1 = _predPrevQ13[1];
        int denomQ16 = (1 << 16) / (8 * fsKHz);
        int delta0 = RShiftRound(SMulBB(predQ13[0] - _predPrevQ13[0], denomQ16), 16);
        int delta1 = RShiftRound(SMulBB(predQ13[1] - _predPrevQ13[1], denomQ16), 16);
        int interp = 8 * fsKHz;
        for (int n = 0; n < interp; n++)
        {
            pred0 += delta0;
            pred1 += delta1;
            int sum = ((x1[n] + x1[n + 2] + (x1[n + 1] << 1)) << 9);
            sum = SMlaWB(x2[n + 1] << 8, sum, pred0);
            sum = SMlaWB(sum, x1[n + 1] << 11, pred1);
            x2[n + 1] = (short)Sat16(RShiftRound(sum, 8));
        }
        pred0 = predQ13[0];
        pred1 = predQ13[1];
        for (int n = interp; n < frameLength; n++)
        {
            int sum = ((x1[n] + x1[n + 2] + (x1[n + 1] << 1)) << 9);
            sum = SMlaWB(x2[n + 1] << 8, sum, pred0);
            sum = SMlaWB(sum, x1[n + 1] << 11, pred1);
            x2[n + 1] = (short)Sat16(RShiftRound(sum, 8));
        }
        _predPrevQ13[0] = (short)predQ13[0];
        _predPrevQ13[1] = (short)predQ13[1];

        for (int n = 0; n < frameLength; n++)
        {
            int sum = x1[n + 1] + x2[n + 1];
            int diff = x1[n + 1] - x2[n + 1];
            x1[n + 1] = (short)Sat16(sum);
            x2[n + 1] = (short)Sat16(diff);
        }
    }
}
