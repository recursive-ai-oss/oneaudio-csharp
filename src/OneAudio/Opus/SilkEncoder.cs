using static OneAudio.Opus.SilkMath;
using static OneAudio.Opus.SilkEncMath;

namespace OneAudio.Opus;

/// <summary>
/// The SILK encoder front end (silk_Encode of the RFC 6716 reference encoder): input buffering and resampling to the
/// internal rate, stereo mid/side conversion, bit-reservoir rate control, in-band redundancy flags and the per-frame
/// encoding loop. Only a 48 kHz API sampling rate is supported.
/// </summary>
internal sealed class SilkEncoder
{
    private readonly SilkEncState[] _state = [new(), new()];
    private readonly SilkStereoEnc _stereo = new();
    private int _nBitsExceeded;
    private int _nChannelsApi = 1;
    private int _nChannelsInternal = 1;
    private int _nPrevChannelsInternal;
    private int _timeSinceSwitchAllowedMs;
    private bool _allowBandwidthSwitch;
    private bool _prevDecodeOnlyMiddle;
    private readonly short[] _buf = new short[20 * 48];
    private readonly byte[] _icdf2 = new byte[2];
    private readonly int[] _msTargetRates = new int[2];

    public SilkEncoder() => Init();

    /// <summary>The first channel's state, for the Opus layer (high-pass corner, internal rate, speech activity).</summary>
    public SilkEncState Channel0 => _state[0];

    /// <summary>Stereo prediction state (widths for the CELT stage).</summary>
    public SilkStereoEnc Stereo => _stereo;

    /// <summary>silk_InitEncoder.</summary>
    public void Init()
    {
        _state[0].Init();
        _state[1].Init();
        _stereo.Reset();
        _nBitsExceeded = 0;
        _nChannelsApi = 1;
        _nChannelsInternal = 1;
        _nPrevChannelsInternal = 0;
        _timeSinceSwitchAllowedMs = 0;
        _allowBandwidthSwitch = false;
        _prevDecodeOnlyMiddle = false;
    }

    /// <summary>
    /// silk_Encode. <paramref name="samplesIn"/> are interleaved 16-bit samples at 48 kHz; <paramref name="nBytesOut"/> is
    /// updated with the number of bytes in the packet once all frames of the packet have been encoded.
    /// </summary>
    public void Encode(SilkEncControl ctl, ReadOnlySpan<short> samplesIn, int nSamplesIn, ref RangeEncoder rc, ref int nBytesOut, bool prefill)
    {
        Span<int> msTargetRates = _msTargetRates;
        int tmpPayloadSizeMs = 0, tmpComplexity = 0;
        _state[0].NFramesEncoded = _state[1].NFramesEncoded = 0;
        ctl.SwitchReady = false;

        if (ctl.NChannelsInternal > _nChannelsInternal)
        {
            _state[1].Init();
            _stereo.StartStereo();
            if (_nChannelsApi == 2)
            {
                _state[1].Resampler.CopyStateFrom(_state[0].Resampler);
                Array.Copy(_state[0].InHpState, _state[1].InHpState, 2);
            }
        }

        bool transition = ctl.PayloadSizeMs != _state[0].PacketSizeMs || _nChannelsInternal != ctl.NChannelsInternal;
        _nChannelsApi = ctl.NChannelsApi;
        _nChannelsInternal = ctl.NChannelsInternal;
        int nBlocksOf10ms = 100 * nSamplesIn / ctl.ApiSampleRate;
        int totBlocks = nBlocksOf10ms > 1 ? nBlocksOf10ms >> 1 : 1;
        int currBlock = 0;

        if (prefill)
        {
            if (nBlocksOf10ms != 1) throw new ArgumentException("Prefill needs exactly 10 ms of input.");
            for (int n = 0; n < ctl.NChannelsInternal; n++) _state[n].Init();
            tmpPayloadSizeMs = ctl.PayloadSizeMs;
            ctl.PayloadSizeMs = 10;
            tmpComplexity = ctl.Complexity;
            ctl.Complexity = 0;
            for (int n = 0; n < ctl.NChannelsInternal; n++)
            {
                _state[n].ControlledSinceLastPayload = false;
                _state[n].PrefillFlag = true;
            }
        }
        else
        {
            if (nBlocksOf10ms * ctl.ApiSampleRate != 100 * nSamplesIn || nSamplesIn < 0)
                throw new ArgumentException("Input must be a whole number of 10 ms blocks.");
            if (1000 * nSamplesIn > ctl.PayloadSizeMs * ctl.ApiSampleRate)
                throw new ArgumentException("Input is longer than the packet duration.");
        }

        int targetRateBps = ctl.BitRate >> (ctl.NChannelsInternal - 1);
        for (int n = 0; n < ctl.NChannelsInternal; n++)
        {
            int forceFsKHz = n == 1 ? _state[0].FsKHz : 0;
            _state[n].Control(ctl, targetRateBps, _allowBandwidthSwitch, n, forceFsKHz);
            if (_state[n].FirstFrameAfterReset || transition)
                for (int i = 0; i < _state[0].NFramesPerPacket; i++) _state[n].LbrrFlags[i] = 0;
            _state[n].InDtx = _state[n].UseDtx;
        }

        var buf = _buf;
        while (true)
        {
            var s0 = _state[0];
            var s1 = _state[1];
            int nSamplesToBuffer = s0.FrameLength - s0.InputBufIx;
            nSamplesToBuffer = Math.Min(nSamplesToBuffer, 10 * nBlocksOf10ms * s0.FsKHz);
            int nSamplesFromInput = nSamplesToBuffer * s0.ApiFsHz / (s0.FsKHz * 1000);

            if (ctl.NChannelsApi == 2 && ctl.NChannelsInternal == 2)
            {
                int id = s0.NFramesEncoded;
                for (int n = 0; n < nSamplesFromInput; n++) buf[n] = samplesIn[2 * n];
                if (_nPrevChannelsInternal == 1 && id == 0) s1.Resampler.CopyStateFrom(s0.Resampler);
                s0.Resampler.Resample(s0.InputBuf.AsSpan(s0.InputBufIx + 2), buf, nSamplesFromInput);
                s0.InputBufIx += nSamplesToBuffer;

                nSamplesToBuffer = s1.FrameLength - s1.InputBufIx;
                nSamplesToBuffer = Math.Min(nSamplesToBuffer, 10 * nBlocksOf10ms * s1.FsKHz);
                for (int n = 0; n < nSamplesFromInput; n++) buf[n] = samplesIn[2 * n + 1];
                s1.Resampler.Resample(s1.InputBuf.AsSpan(s1.InputBufIx + 2), buf, nSamplesFromInput);
                s1.InputBufIx += nSamplesToBuffer;
            }
            else if (ctl.NChannelsApi == 2 && ctl.NChannelsInternal == 1)
            {
                for (int n = 0; n < nSamplesFromInput; n++)
                {
                    int sum = samplesIn[2 * n] + samplesIn[2 * n + 1];
                    buf[n] = (short)RShiftRound(sum, 1);
                }
                s0.Resampler.Resample(s0.InputBuf.AsSpan(s0.InputBufIx + 2), buf, nSamplesFromInput);
                if (_nPrevChannelsInternal == 2 && s0.NFramesEncoded == 0)
                {
                    s1.Resampler.Resample(s1.InputBuf.AsSpan(s1.InputBufIx + 2), buf, nSamplesFromInput);
                    for (int n = 0; n < s0.FrameLength; n++)
                        s0.InputBuf[s0.InputBufIx + n + 2] = (short)((s0.InputBuf[s0.InputBufIx + n + 2] + s1.InputBuf[s1.InputBufIx + n + 2]) >> 1);
                }
                s0.InputBufIx += nSamplesToBuffer;
            }
            else
            {
                samplesIn.Slice(0, nSamplesFromInput).CopyTo(buf);
                s0.Resampler.Resample(s0.InputBuf.AsSpan(s0.InputBufIx + 2), buf, nSamplesFromInput);
                s0.InputBufIx += nSamplesToBuffer;
            }

            samplesIn = samplesIn.Slice(nSamplesFromInput * ctl.NChannelsApi);
            nSamplesIn -= nSamplesFromInput;

            _allowBandwidthSwitch = false;

            if (s0.InputBufIx >= s0.FrameLength)
            {
                if (s0.NFramesEncoded == 0 && !prefill)
                {
                    var icdf = _icdf2;
                    icdf[0] = (byte)(256 - (256 >> ((s0.NFramesPerPacket + 1) * ctl.NChannelsInternal)));
                    rc.ICdf(0, icdf, 8);
                    for (int n = 0; n < ctl.NChannelsInternal; n++)
                    {
                        int lbrrSymbol = 0;
                        for (int i = 0; i < _state[n].NFramesPerPacket; i++) lbrrSymbol |= _state[n].LbrrFlags[i] << i;
                        _state[n].LbrrFlag = (sbyte)(lbrrSymbol > 0 ? 1 : 0);
                        if (lbrrSymbol != 0 && _state[n].NFramesPerPacket > 1)
                            rc.ICdf(lbrrSymbol - 1, _state[n].NFramesPerPacket == 2 ? SilkTables.LBRRFlags2ICDF : SilkTables.LBRRFlags3ICDF, 8);
                    }

                    for (int i = 0; i < s0.NFramesPerPacket; i++)
                    {
                        for (int n = 0; n < ctl.NChannelsInternal; n++)
                        {
                            if (_state[n].LbrrFlags[i] != 0)
                            {
                                int condCodingL;
                                if (ctl.NChannelsInternal == 2 && n == 0)
                                {
                                    _stereo.EncodePred(ref rc, i);
                                    if (_state[1].LbrrFlags[i] == 0) _stereo.EncodeMidOnly(ref rc, i);
                                }
                                if (i > 0 && _state[n].LbrrFlags[i - 1] != 0) condCodingL = SilkEncState.CodeConditionally;
                                else condCodingL = SilkEncState.CodeIndependently;
                                _state[n].EncodeIndices(ref rc, i, true, condCodingL);
                                var il = _state[n].IndicesLbrr[i];
                                _state[n].EncodePulses(ref rc, il.SignalType, il.QuantOffsetType, _state[n].PulsesLbrr[i], _state[n].FrameLength);
                            }
                        }
                    }

                    for (int n = 0; n < ctl.NChannelsInternal; n++) Array.Clear(_state[n].LbrrFlags);
                }

                s0.HpVariableCutoff();

                int nBits = ctl.BitRate * ctl.PayloadSizeMs / 1000;
                if (!prefill) nBits -= rc.Tell() >> 1;
                nBits /= s0.NFramesPerPacket - s0.NFramesEncoded;
                targetRateBps = ctl.PayloadSizeMs == 10 ? SMulBB(nBits, 100) : SMulBB(nBits, 50);
                targetRateBps -= _nBitsExceeded * 1000 / 500;
                targetRateBps = Limit(targetRateBps, ctl.BitRate, 5000);

                int frameIdx = s0.NFramesEncoded;
                if (ctl.NChannelsInternal == 2)
                {
                    _stereo.LrToMs(s0.InputBuf, s1.InputBuf, frameIdx, msTargetRates, targetRateBps, s0.SpeechActivityQ8,
                        ctl.ToMono, s0.FsKHz, s0.FrameLength);
                    if (_stereo.MidOnlyFlags[frameIdx] == 0)
                    {
                        if (_prevDecodeOnlyMiddle)
                        {
                            s1.ClearShape();
                            s1.ClearPrefilter();
                            s1.Nsq.Clear();
                            Array.Clear(s1.PrevNlsfqQ15);
                            Array.Clear(s1.LpInLpState);
                            s1.PrevLag = 100;
                            s1.Nsq.LagPrev = 100;
                            s1.ShapeLastGainIndex = 10;
                            s1.PrevSignalType = SilkEncState.TypeNoVoiceActivity;
                            s1.Nsq.PrevGainQ16 = 65536;
                            s1.FirstFrameAfterReset = true;
                        }
                        s1.DoVad();
                    }
                    else
                    {
                        s1.VadFlags[frameIdx] = 0;
                    }
                    if (!prefill)
                    {
                        _stereo.EncodePred(ref rc, frameIdx);
                        if (s1.VadFlags[frameIdx] == 0) _stereo.EncodeMidOnly(ref rc, frameIdx);
                    }
                }
                else
                {
                    _stereo.SMid.AsSpan().CopyTo(s0.InputBuf);
                    s0.InputBuf.AsSpan(s0.FrameLength, 2).CopyTo(_stereo.SMid);
                }
                s0.DoVad();

                for (int n = 0; n < ctl.NChannelsInternal; n++)
                {
                    int maxBits = ctl.MaxBits;
                    if (totBlocks == 2 && currBlock == 0) maxBits = maxBits * 3 / 5;
                    else if (totBlocks == 3)
                    {
                        if (currBlock == 0) maxBits = maxBits * 2 / 5;
                        else if (currBlock == 1) maxBits = maxBits * 3 / 4;
                    }
                    bool useCbr = ctl.UseCbr && currBlock == totBlocks - 1;
                    int channelRateBps;
                    if (ctl.NChannelsInternal == 1)
                    {
                        channelRateBps = targetRateBps;
                    }
                    else
                    {
                        channelRateBps = msTargetRates[n];
                        if (n == 0 && msTargetRates[1] > 0)
                        {
                            useCbr = false;
                            maxBits -= ctl.MaxBits / (totBlocks * 2);
                        }
                    }

                    if (channelRateBps > 0)
                    {
                        int condCoding;
                        _state[n].ControlSnr(channelRateBps);
                        if (s0.NFramesEncoded - n <= 0) condCoding = SilkEncState.CodeIndependently;
                        else if (n > 0 && _prevDecodeOnlyMiddle) condCoding = SilkEncState.CodeIndependentlyNoLtpScaling;
                        else condCoding = SilkEncState.CodeConditionally;
                        nBytesOut = _state[n].EncodeFrame(ref rc, condCoding, maxBits, useCbr);
                    }
                    _state[n].ControlledSinceLastPayload = false;
                    _state[n].InputBufIx = 0;
                    _state[n].NFramesEncoded++;
                }
                _prevDecodeOnlyMiddle = _stereo.MidOnlyFlags[s0.NFramesEncoded - 1] != 0;

                if (nBytesOut > 0 && s0.NFramesEncoded == s0.NFramesPerPacket)
                {
                    int flags = 0;
                    for (int n = 0; n < ctl.NChannelsInternal; n++)
                    {
                        for (int i = 0; i < _state[n].NFramesPerPacket; i++)
                        {
                            flags <<= 1;
                            flags |= (byte)_state[n].VadFlags[i];
                        }
                        flags <<= 1;
                        flags |= (byte)_state[n].LbrrFlag;
                    }
                    if (!prefill) rc.PatchInitialBits((uint)flags, (s0.NFramesPerPacket + 1) * ctl.NChannelsInternal);

                    if (s0.InDtx && (ctl.NChannelsInternal == 1 || s1.InDtx)) nBytesOut = 0;

                    _nBitsExceeded += nBytesOut * 8;
                    _nBitsExceeded -= ctl.BitRate * ctl.PayloadSizeMs / 1000;
                    _nBitsExceeded = Limit(_nBitsExceeded, 0, 10000);

                    // SILK_FIX_CONST(0.05, 8) plus a ramp of (1 - 0.05) / 5000 per ms in Q24
                    int speechActThrForSwitchQ8 = SMlaWB(13, 3188, _timeSinceSwitchAllowedMs);
                    if (s0.SpeechActivityQ8 < speechActThrForSwitchQ8)
                    {
                        _allowBandwidthSwitch = true;
                        _timeSinceSwitchAllowedMs = 0;
                    }
                    else
                    {
                        _allowBandwidthSwitch = false;
                        _timeSinceSwitchAllowedMs += ctl.PayloadSizeMs;
                    }
                }

                if (nSamplesIn == 0) break;
            }
            else break;
            currBlock++;
        }

        _nPrevChannelsInternal = ctl.NChannelsInternal;
        ctl.AllowBandwidthSwitch = _allowBandwidthSwitch;
        ctl.InWbModeWithoutVariableLp = _state[0].FsKHz == 16 && _state[0].LpMode == 0;
        ctl.InternalSampleRate = _state[0].FsKHz * 1000;
        ctl.StereoWidthQ14 = ctl.ToMono ? 0 : _stereo.SmthWidthQ14;
        if (prefill)
        {
            ctl.PayloadSizeMs = tmpPayloadSizeMs;
            ctl.Complexity = tmpComplexity;
            for (int n = 0; n < ctl.NChannelsInternal; n++)
            {
                _state[n].ControlledSinceLastPayload = false;
                _state[n].PrefillFlag = false;
            }
        }
    }
}
