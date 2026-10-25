using static OneAudio.Opus.SilkEncMath;

namespace OneAudio.Opus;

/// <summary>Side information of one SILK frame (SideInfoIndices).</summary>
internal sealed class SilkIndices
{
    public readonly sbyte[] GainsIndices = new sbyte[4];
    public readonly sbyte[] LtpIndex = new sbyte[4];
    public readonly sbyte[] NlsfIndices = new sbyte[17];
    public short LagIndex;
    public sbyte ContourIndex;
    public sbyte SignalType;
    public sbyte QuantOffsetType;
    public sbyte NlsfInterpCoefQ2;
    public sbyte PerIndex;
    public sbyte LtpScaleIndex;
    public sbyte Seed;

    public void Clear()
    {
        Array.Clear(GainsIndices); Array.Clear(LtpIndex); Array.Clear(NlsfIndices);
        LagIndex = 0; ContourIndex = 0; SignalType = 0; QuantOffsetType = 0; NlsfInterpCoefQ2 = 0;
        PerIndex = 0; LtpScaleIndex = 0; Seed = 0;
    }

    public void CopyFrom(SilkIndices o)
    {
        Array.Copy(o.GainsIndices, GainsIndices, 4); Array.Copy(o.LtpIndex, LtpIndex, 4); Array.Copy(o.NlsfIndices, NlsfIndices, 17);
        LagIndex = o.LagIndex; ContourIndex = o.ContourIndex; SignalType = o.SignalType; QuantOffsetType = o.QuantOffsetType;
        NlsfInterpCoefQ2 = o.NlsfInterpCoefQ2; PerIndex = o.PerIndex; LtpScaleIndex = o.LtpScaleIndex; Seed = o.Seed;
    }
}

/// <summary>Noise shaping quantizer state (silk_nsq_state).</summary>
internal sealed partial class SilkNsqState
{
    public readonly short[] Xq = new short[2 * SilkEncState.MaxFrameLength];
    public readonly int[] SLtpShpQ14 = new int[2 * SilkEncState.MaxFrameLength];
    public readonly int[] SLpcQ14 = new int[SilkEncState.MaxSubFrameLength + 32];
    public readonly int[] SAr2Q14 = new int[16];
    public int SLfArShpQ14;
    public int LagPrev;
    public int SLtpBufIdx;
    public int SLtpShpBufIdx;
    public int RandSeed;
    public int PrevGainQ16;
    public int RewhiteFlag;

    public void Clear()
    {
        Array.Clear(Xq); Array.Clear(SLtpShpQ14); Array.Clear(SLpcQ14); Array.Clear(SAr2Q14);
        SLfArShpQ14 = 0; LagPrev = 0; SLtpBufIdx = 0; SLtpShpBufIdx = 0; RandSeed = 0; PrevGainQ16 = 0; RewhiteFlag = 0;
    }
}

/// <summary>Control parameters handed to the SILK encoder per call (silk_EncControlStruct).</summary>
internal sealed class SilkEncControl
{
    public int NChannelsApi, NChannelsInternal, ApiSampleRate, MaxInternalSampleRate, MinInternalSampleRate;
    public int DesiredInternalSampleRate, PayloadSizeMs, BitRate, PacketLossPercentage, Complexity;
    public bool UseInBandFec, UseDtx, UseCbr;
    public int MaxBits;
    public bool ToMono;
    public bool OpusCanSwitch;
    public int ReducedDependency;
    // Outputs
    public int InternalSampleRate;
    public bool AllowBandwidthSwitch;
    public bool InWbModeWithoutVariableLp;
    public int StereoWidthQ14;
    public bool SwitchReady;
}

/// <summary>
/// Per-channel state of the SILK encoder: the common part plus the floating-point analysis state of the reference
/// float build (silk_encoder_state and silk_encoder_state_FLP).
/// </summary>
internal sealed partial class SilkEncState
{
    public const int MaxFrameLength = 320;
    public const int MaxSubFrameLength = 80;
    public const int MaxNbSubfr = 4;
    public const int MaxLpcOrder = 16;
    public const int LtpOrder = 5;
    public const int LaShapeMax = 80;
    public const int LtpBufLength = 512;
    public const int MaxFramesPerPacket = 3;
    public const int VadNBands = 4;
    public const int TypeNoVoiceActivity = 0;
    public const int TypeUnvoiced = 1;
    public const int TypeVoiced = 2;
    public const int CodeIndependently = 0;
    public const int CodeIndependentlyNoLtpScaling = 1;
    public const int CodeConditionally = 2;

    // ---- common state ----
    public readonly int[] InHpState = new int[2];
    public int VariableHpSmth1Q15, VariableHpSmth2Q15;
    public readonly int[] LpInLpState = new int[2];
    public int LpTransitionFrameNo, LpMode;
    // VAD
    public readonly int[] VadAnaState = new int[2], VadAnaState1 = new int[2], VadAnaState2 = new int[2];
    public readonly int[] VadXnrgSubfr = new int[4], VadNrgRatioSmthQ8 = new int[4], VadNl = new int[4], VadInvNl = new int[4], VadNoiseLevelBias = new int[4];
    public short VadHpState;
    public int VadCounter;

    public readonly SilkNsqState Nsq = new();
    public readonly short[] PrevNlsfqQ15 = new short[MaxLpcOrder];
    public int SpeechActivityQ8;
    public bool AllowBandwidthSwitch;
    public sbyte LbrrPrevLastGainIndex;
    public int PrevSignalType;
    public int PrevLag;
    public int PitchLpcWinLength;
    public int MaxPitchLag;
    public int ApiFsHz, PrevApiFsHz, MaxInternalFsHz, MinInternalFsHz, DesiredInternalFsHz;
    public int FsKHz, NbSubfr, FrameLength, SubfrLength, LtpMemLength, LaPitch, LaShape, ShapeWinLength;
    public int TargetRateBps, PacketSizeMs, PacketLossPerc, FrameCounter, Complexity;
    public int NStatesDelayedDecision, UseInterpolatedNlsfs, ShapingLpcOrder, PredictLpcOrder;
    public int PitchEstimationComplexity, PitchEstimationLpcOrder, PitchEstimationThresholdQ16;
    public int LtpQuantLowComplexity, MuLtpQ9, NlsfMsvqSurvivors;
    public bool FirstFrameAfterReset;
    public bool ControlledSinceLastPayload;
    public int WarpingQ16;
    public bool UseCbr;
    public bool PrefillFlag;
    public byte[] PitchLagLowBitsICdf = SilkTables.Uniform4ICDF;
    public byte[] PitchContourICdf = SilkTables.PitchContourICDF;
    public NlsfCodebook NlsfCb = NlsfCodebook.NbMb;
    public readonly int[] InputQualityBandsQ15 = new int[4];
    public int InputTiltQ15;
    public int SnrDbQ7;
    public readonly sbyte[] VadFlags = new sbyte[MaxFramesPerPacket];
    public sbyte LbrrFlag;
    public readonly int[] LbrrFlags = new int[MaxFramesPerPacket];
    public readonly SilkIndices Indices = new();
    public readonly sbyte[] Pulses = new sbyte[MaxFrameLength + 16];
    public readonly short[] InputBuf = new short[MaxFrameLength + 2];
    public int InputBufIx;
    public int NFramesPerPacket, NFramesEncoded, NChannelsApi, NChannelsInternal, ChannelNb;
    public int FramesSinceOnset, EcPrevSignalType;
    public short EcPrevLagIndex;
    public readonly SilkDownResampler Resampler = new();
    public int ResamplerFs; // internal rate the resampler is configured for, 0 if none
    public bool UseDtx;
    public bool InDtx;
    public int NoSpeechCounter;
    public bool UseInBandFec;
    public bool LbrrEnabled;
    public int LbrrGainIncreases;
    public readonly SilkIndices[] IndicesLbrr = [new(), new(), new()];
    public readonly sbyte[][] PulsesLbrr = [new sbyte[MaxFrameLength + 16], new sbyte[MaxFrameLength + 16], new sbyte[MaxFrameLength + 16]];

    // ---- floating-point state ----
    public sbyte ShapeLastGainIndex;
    public float ShapeHarmBoostSmth, ShapeHarmShapeGainSmth, ShapeTiltSmth;
    public readonly float[] PrefiltSLtpShp = new float[LtpBufLength];
    public readonly float[] PrefiltSArShp = new float[MaxLpcOrder + 1];
    public int PrefiltSLtpShpBufIdx;
    public float PrefiltSLfArShp, PrefiltSLfMaShp, PrefiltSHarmHp;
    public int PrefiltRandSeed, PrefiltLagPrev;
    public readonly float[] XBuf = new float[2 * MaxFrameLength + LaShapeMax];
    public float LtpCorr;

    public SilkEncState() => Init();

    /// <summary>silk_init_encoder: zero everything, then the non-zero start values.</summary>
    public void Init()
    {
        Array.Clear(InHpState); VariableHpSmth1Q15 = 0; VariableHpSmth2Q15 = 0;
        Array.Clear(LpInLpState); LpTransitionFrameNo = 0; LpMode = 0;
        Array.Clear(VadAnaState); Array.Clear(VadAnaState1); Array.Clear(VadAnaState2);
        Array.Clear(VadXnrgSubfr); Array.Clear(VadNrgRatioSmthQ8); Array.Clear(VadNl); Array.Clear(VadInvNl); Array.Clear(VadNoiseLevelBias);
        VadHpState = 0; VadCounter = 0;
        Nsq.Clear();
        Array.Clear(PrevNlsfqQ15);
        SpeechActivityQ8 = 0; AllowBandwidthSwitch = false; LbrrPrevLastGainIndex = 0; PrevSignalType = 0; PrevLag = 0;
        PitchLpcWinLength = 0; MaxPitchLag = 0;
        ApiFsHz = 0; PrevApiFsHz = 0; MaxInternalFsHz = 0; MinInternalFsHz = 0; DesiredInternalFsHz = 0;
        FsKHz = 0; NbSubfr = 0; FrameLength = 0; SubfrLength = 0; LtpMemLength = 0; LaPitch = 0; LaShape = 0; ShapeWinLength = 0;
        TargetRateBps = 0; PacketSizeMs = 0; PacketLossPerc = 0; FrameCounter = 0; Complexity = 0;
        NStatesDelayedDecision = 0; UseInterpolatedNlsfs = 0; ShapingLpcOrder = 0; PredictLpcOrder = 0;
        PitchEstimationComplexity = 0; PitchEstimationLpcOrder = 0; PitchEstimationThresholdQ16 = 0;
        LtpQuantLowComplexity = 0; MuLtpQ9 = 0; NlsfMsvqSurvivors = 0;
        ControlledSinceLastPayload = false; WarpingQ16 = 0; UseCbr = false; PrefillFlag = false;
        PitchLagLowBitsICdf = SilkTables.Uniform4ICDF; PitchContourICdf = SilkTables.PitchContourICDF; NlsfCb = NlsfCodebook.NbMb;
        Array.Clear(InputQualityBandsQ15); InputTiltQ15 = 0; SnrDbQ7 = 0;
        Array.Clear(VadFlags); LbrrFlag = 0; Array.Clear(LbrrFlags);
        Indices.Clear(); Array.Clear(Pulses); Array.Clear(InputBuf); InputBufIx = 0;
        NFramesPerPacket = 0; NFramesEncoded = 0; NChannelsApi = 0; NChannelsInternal = 0; ChannelNb = 0;
        FramesSinceOnset = 0; EcPrevSignalType = 0; EcPrevLagIndex = 0;
        ResamplerFs = 0;
        UseDtx = false; InDtx = false; NoSpeechCounter = 0; UseInBandFec = false; LbrrEnabled = false; LbrrGainIncreases = 0;
        foreach (var i in IndicesLbrr) i.Clear();
        foreach (var p in PulsesLbrr) Array.Clear(p);
        ShapeLastGainIndex = 0; ShapeHarmBoostSmth = 0; ShapeHarmShapeGainSmth = 0; ShapeTiltSmth = 0;
        ClearPrefilter();
        Array.Clear(XBuf); LtpCorr = 0;

        // silk_init_encoder
        VariableHpSmth1Q15 = (Lin2Log(60 << 16) - (16 << 7)) << 8;
        VariableHpSmth2Q15 = VariableHpSmth1Q15;
        FirstFrameAfterReset = true;
        VadInit();
    }

    public void ClearShape()
    {
        ShapeLastGainIndex = 0; ShapeHarmBoostSmth = 0; ShapeHarmShapeGainSmth = 0; ShapeTiltSmth = 0;
    }

    public void ClearPrefilter()
    {
        Array.Clear(PrefiltSLtpShp); Array.Clear(PrefiltSArShp);
        PrefiltSLtpShpBufIdx = 0; PrefiltSLfArShp = 0; PrefiltSLfMaShp = 0; PrefiltSHarmHp = 0; PrefiltRandSeed = 0; PrefiltLagPrev = 0;
    }
}
