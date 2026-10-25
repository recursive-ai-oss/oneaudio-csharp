using static OneAudio.Opus.SilkMath;
using static OneAudio.Opus.SilkEncMath;

namespace OneAudio.Opus;

internal sealed partial class SilkEncState
{
    private const int TransitionFrames = 256;
    private const int TransitionIntNum = 5;

    private static readonly int[] TransitionLpBQ28 =
    [
        250767114, 501534038, 250767114,
        209867381, 419732057, 209867381,
        170987846, 341967853, 170987846,
        131531482, 263046905, 131531482,
        89306658, 178584282, 89306658,
    ];

    private static readonly int[] TransitionLpAQ28 =
    [
        506393414, 239854379,
        411067935, 169683996,
        306733530, 116694253,
        185807084, 77959395,
        35497197, 57401098,
    ];

    private static readonly int[] TargetRateTableNb = [0, 8000, 9400, 11500, 13500, 17500, 25000, 80000];
    private static readonly int[] TargetRateTableMb = [0, 9000, 12000, 14500, 18500, 24500, 35500, 80000];
    private static readonly int[] TargetRateTableWb = [0, 10500, 14000, 17000, 21500, 28500, 42000, 80000];
    private static readonly int[] SnrTableQ1 = [18, 29, 38, 40, 46, 52, 62, 84];

    private readonly short[] _scratchApi = new short[(2 * 20 + 5) * 48];
    private readonly short[] _scratchFix = new short[(2 * 20 + 5) * 16];
    private readonly SilkResampler _tempUp = new();

    /// <summary>silk_HP_variable_cutoff: follows the pitch of voiced speech with the high-pass corner used by the Opus layer.</summary>
    public void HpVariableCutoff()
    {
        if (PrevSignalType == TypeVoiced)
        {
            int pitchFreqHzQ16 = ((FsKHz * 1000) << 16) / PrevLag;
            int pitchFreqLogQ7 = Lin2Log(pitchFreqHzQ16) - (16 << 7);
            int qualityQ15 = InputQualityBandsQ15[0];
            pitchFreqLogQ7 = SMlaWB(pitchFreqLogQ7, SMulWB(-qualityQ15 << 2, qualityQ15),
                pitchFreqLogQ7 - (Lin2Log(60 << 16) - (16 << 7)));
            int deltaFreqQ7 = pitchFreqLogQ7 - (VariableHpSmth1Q15 >> 8);
            if (deltaFreqQ7 < 0) deltaFreqQ7 *= 3;
            deltaFreqQ7 = Limit(deltaFreqQ7, -51, 51); // 0.4 in Q7
            VariableHpSmth1Q15 = SMlaWB(VariableHpSmth1Q15, SMulBB(SpeechActivityQ8, deltaFreqQ7), 6554); // 0.1 in Q16
            VariableHpSmth1Q15 = Limit(VariableHpSmth1Q15, Lin2Log(60) << 8, Lin2Log(100) << 8);
        }
    }

    private static void BiquadAlt(Span<short> data, int len, ReadOnlySpan<int> bQ28, ReadOnlySpan<int> aQ28, Span<int> s)
    {
        int a0LQ28 = -aQ28[0] & 0x00003FFF;
        int a0UQ28 = -aQ28[0] >> 14;
        int a1LQ28 = -aQ28[1] & 0x00003FFF;
        int a1UQ28 = -aQ28[1] >> 14;
        for (int k = 0; k < len; k++)
        {
            int inval = data[k];
            int out32Q14 = SMlaWB(s[0], bQ28[0], inval) << 2;
            s[0] = s[1] + RShiftRound(SMulWB(out32Q14, a0LQ28), 14);
            s[0] = SMlaWB(s[0], out32Q14, a0UQ28);
            s[0] = SMlaWB(s[0], bQ28[1], inval);
            s[1] = RShiftRound(SMulWB(out32Q14, a1LQ28), 14);
            s[1] = SMlaWB(s[1], out32Q14, a1UQ28);
            s[1] = SMlaWB(s[1], bQ28[2], inval);
            data[k] = (short)Sat16((out32Q14 + (1 << 14) - 1) >> 14);
        }
    }

    /// <summary>silk_LP_variable_cutoff: smooth low-pass during an internal bandwidth switch, applied to the input of the frame.</summary>
    private void LpVariableCutoff()
    {
        if (LpMode == 0) return;
        Span<int> bQ28 = stackalloc int[3];
        Span<int> aQ28 = stackalloc int[2];
        int facQ16 = (TransitionFrames - LpTransitionFrameNo) << 10;
        int ind = facQ16 >> 16;
        facQ16 -= ind << 16;
        if (ind < TransitionIntNum - 1)
        {
            if (facQ16 > 0)
            {
                if (facQ16 < 32768)
                {
                    for (int nb = 0; nb < 3; nb++)
                        bQ28[nb] = SMlaWB(TransitionLpBQ28[ind * 3 + nb], TransitionLpBQ28[(ind + 1) * 3 + nb] - TransitionLpBQ28[ind * 3 + nb], facQ16);
                    for (int na = 0; na < 2; na++)
                        aQ28[na] = SMlaWB(TransitionLpAQ28[ind * 2 + na], TransitionLpAQ28[(ind + 1) * 2 + na] - TransitionLpAQ28[ind * 2 + na], facQ16);
                }
                else
                {
                    for (int nb = 0; nb < 3; nb++)
                        bQ28[nb] = SMlaWB(TransitionLpBQ28[(ind + 1) * 3 + nb], TransitionLpBQ28[(ind + 1) * 3 + nb] - TransitionLpBQ28[ind * 3 + nb], facQ16 - (1 << 16));
                    for (int na = 0; na < 2; na++)
                        aQ28[na] = SMlaWB(TransitionLpAQ28[(ind + 1) * 2 + na], TransitionLpAQ28[(ind + 1) * 2 + na] - TransitionLpAQ28[ind * 2 + na], facQ16 - (1 << 16));
                }
            }
            else
            {
                TransitionLpBQ28.AsSpan(ind * 3, 3).CopyTo(bQ28);
                TransitionLpAQ28.AsSpan(ind * 2, 2).CopyTo(aQ28);
            }
        }
        else
        {
            TransitionLpBQ28.AsSpan((TransitionIntNum - 1) * 3, 3).CopyTo(bQ28);
            TransitionLpAQ28.AsSpan((TransitionIntNum - 1) * 2, 2).CopyTo(aQ28);
        }
        LpTransitionFrameNo = Limit(LpTransitionFrameNo + LpMode, 0, TransitionFrames);
        BiquadAlt(InputBuf.AsSpan(1, FrameLength), FrameLength, bQ28, aQ28, LpInLpState);
    }

    /// <summary>silk_control_audio_bandwidth: chooses the internal sampling rate, with a smooth switch between rates.</summary>
    private int ControlAudioBandwidth(SilkEncControl ctl)
    {
        int fsKHz = FsKHz;
        int fsHz = fsKHz * 1000;
        if (fsHz == 0)
        {
            fsHz = Math.Min(DesiredInternalFsHz, ApiFsHz);
            fsKHz = fsHz / 1000;
        }
        else if (fsHz > ApiFsHz || fsHz > MaxInternalFsHz || fsHz < MinInternalFsHz)
        {
            fsHz = ApiFsHz;
            fsHz = Math.Min(fsHz, MaxInternalFsHz);
            fsHz = Math.Max(fsHz, MinInternalFsHz);
            fsKHz = fsHz / 1000;
        }
        else
        {
            if (LpTransitionFrameNo >= TransitionFrames) LpMode = 0;
            if (AllowBandwidthSwitch || ctl.OpusCanSwitch)
            {
                if (FsKHz * 1000 > DesiredInternalFsHz)
                {
                    if (LpMode == 0)
                    {
                        LpTransitionFrameNo = TransitionFrames;
                        Array.Clear(LpInLpState);
                    }
                    if (ctl.OpusCanSwitch)
                    {
                        LpMode = 0;
                        fsKHz = FsKHz == 16 ? 12 : 8;
                    }
                    else
                    {
                        if (LpTransitionFrameNo <= 0) ctl.SwitchReady = true;
                        else LpMode = -2;
                    }
                }
                else if (FsKHz * 1000 < DesiredInternalFsHz)
                {
                    if (ctl.OpusCanSwitch)
                    {
                        fsKHz = FsKHz == 8 ? 12 : 16;
                        LpTransitionFrameNo = 0;
                        Array.Clear(LpInLpState);
                        LpMode = 1;
                    }
                    else
                    {
                        if (LpMode == 0) ctl.SwitchReady = true;
                        else LpMode = 1;
                    }
                }
            }
        }
        return fsKHz;
    }

    /// <summary>silk_control_SNR: maps the target bit rate to the quantiser's SNR target.</summary>
    public void ControlSnr(int targetRateBps)
    {
        targetRateBps = Limit(targetRateBps, 5000, 80000);
        if (targetRateBps != TargetRateBps)
        {
            TargetRateBps = targetRateBps;
            var table = FsKHz == 8 ? TargetRateTableNb : FsKHz == 12 ? TargetRateTableMb : TargetRateTableWb;
            if (NbSubfr == 2) targetRateBps -= 2200;
            for (int k = 1; k < 8; k++)
            {
                if (targetRateBps <= table[k])
                {
                    int fracQ6 = ((targetRateBps - table[k - 1]) << 6) / (table[k] - table[k - 1]);
                    SnrDbQ7 = (SnrTableQ1[k - 1] << 6) + fracQ6 * (SnrTableQ1[k] - SnrTableQ1[k - 1]);
                    break;
                }
            }
            if (LbrrEnabled) SnrDbQ7 = SMlaBB(SnrDbQ7, 12 - LbrrGainIncreases, -32); // -0.25 in Q7
        }
    }

    private void SetupComplexity(int complexity)
    {
        if (complexity < 2)
        {
            PitchEstimationComplexity = 0;
            PitchEstimationThresholdQ16 = 52429; // 0.8
            PitchEstimationLpcOrder = 6;
            ShapingLpcOrder = 8;
            LaShape = 3 * FsKHz;
            NStatesDelayedDecision = 1;
            UseInterpolatedNlsfs = 0;
            LtpQuantLowComplexity = 1;
            NlsfMsvqSurvivors = 2;
            WarpingQ16 = 0;
        }
        else if (complexity < 4)
        {
            PitchEstimationComplexity = 1;
            PitchEstimationThresholdQ16 = 49807; // 0.76
            PitchEstimationLpcOrder = 8;
            ShapingLpcOrder = 10;
            LaShape = 5 * FsKHz;
            NStatesDelayedDecision = 1;
            UseInterpolatedNlsfs = 0;
            LtpQuantLowComplexity = 0;
            NlsfMsvqSurvivors = 4;
            WarpingQ16 = 0;
        }
        else if (complexity < 6)
        {
            PitchEstimationComplexity = 1;
            PitchEstimationThresholdQ16 = 48497; // 0.74
            PitchEstimationLpcOrder = 10;
            ShapingLpcOrder = 12;
            LaShape = 5 * FsKHz;
            NStatesDelayedDecision = 2;
            UseInterpolatedNlsfs = 1;
            LtpQuantLowComplexity = 0;
            NlsfMsvqSurvivors = 8;
            WarpingQ16 = FsKHz * 983; // 0.015 in Q16
        }
        else if (complexity < 8)
        {
            PitchEstimationComplexity = 1;
            PitchEstimationThresholdQ16 = 47186; // 0.72
            PitchEstimationLpcOrder = 12;
            ShapingLpcOrder = 14;
            LaShape = 5 * FsKHz;
            NStatesDelayedDecision = 3;
            UseInterpolatedNlsfs = 1;
            LtpQuantLowComplexity = 0;
            NlsfMsvqSurvivors = 16;
            WarpingQ16 = FsKHz * 983;
        }
        else
        {
            PitchEstimationComplexity = 2;
            PitchEstimationThresholdQ16 = 45875; // 0.7
            PitchEstimationLpcOrder = 16;
            ShapingLpcOrder = 16;
            LaShape = 5 * FsKHz;
            NStatesDelayedDecision = 4;
            UseInterpolatedNlsfs = 1;
            LtpQuantLowComplexity = 0;
            NlsfMsvqSurvivors = 32;
            WarpingQ16 = FsKHz * 983;
        }
        PitchEstimationLpcOrder = Math.Min(PitchEstimationLpcOrder, PredictLpcOrder);
        ShapeWinLength = SubFrameLengthMs * FsKHz + 2 * LaShape;
        Complexity = complexity;
    }

    private void SetupLbrr(int targetRateBps)
    {
        LbrrEnabled = false;
        if (UseInBandFec && PacketLossPerc > 0)
        {
            int thres = FsKHz == 8 ? 12000 : FsKHz == 12 ? 14000 : 16000;
            thres = SMulWB(thres * (125 - Math.Min(PacketLossPerc, 25)), 655); // 0.01 in Q16
            if (targetRateBps > thres)
            {
                LbrrEnabled = true;
                LbrrGainIncreases = Math.Max(7 - SMulWB(PacketLossPerc, 26214), 2); // 0.4 in Q16
            }
        }
    }

    private void SetupResamplers(int fsKHz)
    {
        if (FsKHz != fsKHz || PrevApiFsHz != ApiFsHz)
        {
            if (FsKHz == 0)
            {
                Resampler.Init(fsKHz * 1000);
            }
            else
            {
                // Re-sample the analysis history to the new internal rate through the API rate.
                int nSamplesTemp = (FrameLength << 1) + LaShapeMs * FsKHz;
                for (int i = 0; i < nSamplesTemp; i++) _scratchFix[i] = (short)Sat16(SilkFlp.Float2Int(XBuf[i]));
                _tempUp.Init(FsKHz * 1000, ApiFsHz);
                _tempUp.Resample(_scratchApi, _scratchFix, nSamplesTemp);
                nSamplesTemp = nSamplesTemp * ApiFsHz / (FsKHz * 1000);
                Resampler.Init(fsKHz * 1000);
                Resampler.Resample(_scratchFix, _scratchApi, nSamplesTemp);
                int n = (2 * 20 + LaShapeMs) * fsKHz;
                for (int i = 0; i < n; i++) XBuf[i] = _scratchFix[i];
            }
        }
        PrevApiFsHz = ApiFsHz;
    }

    private void SetupFs(int fsKHz, int packetSizeMs)
    {
        if (packetSizeMs != PacketSizeMs)
        {
            if (packetSizeMs <= 10)
            {
                NFramesPerPacket = 1;
                NbSubfr = packetSizeMs == 10 ? 2 : 1;
                FrameLength = packetSizeMs * fsKHz;
                PitchLpcWinLength = (10 + (2 << 1)) * fsKHz;
                PitchContourICdf = FsKHz == 8 ? SilkTables.PitchContour10MsNBICDF : SilkTables.PitchContour10MsICDF;
            }
            else
            {
                NFramesPerPacket = packetSizeMs / 20;
                NbSubfr = MaxNbSubfr;
                FrameLength = 20 * fsKHz;
                PitchLpcWinLength = (20 + (2 << 1)) * fsKHz;
                PitchContourICdf = FsKHz == 8 ? SilkTables.PitchContourNBICDF : SilkTables.PitchContourICDF;
            }
            PacketSizeMs = packetSizeMs;
            TargetRateBps = 0;
        }

        if (FsKHz != fsKHz)
        {
            ClearShape();
            ClearPrefilter();
            Nsq.Clear();
            Array.Clear(PrevNlsfqQ15);
            Array.Clear(LpInLpState);
            InputBufIx = 0;
            NFramesEncoded = 0;
            TargetRateBps = 0;
            PrevLag = 100;
            FirstFrameAfterReset = true;
            PrefiltLagPrev = 100;
            ShapeLastGainIndex = 10;
            Nsq.LagPrev = 100;
            Nsq.PrevGainQ16 = 65536;
            PrevSignalType = TypeNoVoiceActivity;
            FsKHz = fsKHz;
            if (FsKHz == 8)
                PitchContourICdf = NbSubfr == MaxNbSubfr ? SilkTables.PitchContourNBICDF : SilkTables.PitchContour10MsNBICDF;
            else
                PitchContourICdf = NbSubfr == MaxNbSubfr ? SilkTables.PitchContourICDF : SilkTables.PitchContour10MsICDF;
            if (FsKHz == 8 || FsKHz == 12)
            {
                PredictLpcOrder = 10;
                NlsfCb = NlsfCodebook.NbMb;
            }
            else
            {
                PredictLpcOrder = 16;
                NlsfCb = NlsfCodebook.Wb;
            }
            SubfrLength = SubFrameLengthMs * fsKHz;
            FrameLength = SubfrLength * NbSubfr;
            LtpMemLength = 20 * fsKHz;
            LaPitch = 2 * fsKHz;
            MaxPitchLag = 18 * fsKHz;
            PitchLpcWinLength = NbSubfr == MaxNbSubfr ? (20 + (2 << 1)) * fsKHz : (10 + (2 << 1)) * fsKHz;
            if (FsKHz == 16)
            {
                MuLtpQ9 = 10; // 0.02 in Q9
                PitchLagLowBitsICdf = SilkTables.Uniform8ICDF;
            }
            else if (FsKHz == 12)
            {
                MuLtpQ9 = 13; // 0.025 in Q9
                PitchLagLowBitsICdf = SilkTables.Uniform6ICDF;
            }
            else
            {
                MuLtpQ9 = 15; // 0.03 in Q9
                PitchLagLowBitsICdf = SilkTables.Uniform4ICDF;
            }
        }
    }

    /// <summary>silk_control_encoder: applies the per-call control parameters and (re)configures the channel.</summary>
    public void Control(SilkEncControl ctl, int targetRateBps, bool allowBwSwitch, int channelNb, int forceFsKHz)
    {
        UseDtx = ctl.UseDtx;
        UseCbr = ctl.UseCbr;
        ApiFsHz = ctl.ApiSampleRate;
        MaxInternalFsHz = ctl.MaxInternalSampleRate;
        MinInternalFsHz = ctl.MinInternalSampleRate;
        DesiredInternalFsHz = ctl.DesiredInternalSampleRate;
        UseInBandFec = ctl.UseInBandFec;
        NChannelsApi = ctl.NChannelsApi;
        NChannelsInternal = ctl.NChannelsInternal;
        AllowBandwidthSwitch = allowBwSwitch;
        ChannelNb = channelNb;

        if (ControlledSinceLastPayload && !PrefillFlag)
        {
            if (ApiFsHz != PrevApiFsHz && FsKHz > 0) SetupResamplers(FsKHz);
            return;
        }

        int fsKHz = ControlAudioBandwidth(ctl);
        if (forceFsKHz != 0) fsKHz = forceFsKHz;
        SetupResamplers(fsKHz);
        SetupFs(fsKHz, ctl.PayloadSizeMs);
        SetupComplexity(ctl.Complexity);
        PacketLossPerc = ctl.PacketLossPercentage;
        SetupLbrr(targetRateBps);
        ControlledSinceLastPayload = true;
    }
}
