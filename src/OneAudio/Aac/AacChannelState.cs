namespace OneAudio.Aac;

/// <summary>Per-channel data of the frame being decoded plus the state that persists between frames.</summary>
internal sealed class AacChannelState
{
    internal const int MaxBands = 128; // 8 groups x 16 bands (short) or up to 51 bands (long)

    // ics_info of the current frame
    internal int WindowSequence;
    internal int WindowShape;
    internal int MaxSfb;
    internal int TotalSfb;
    internal int NumGroups;
    internal readonly int[] GroupLength = new int[8];
    internal bool IsShort => WindowSequence == AacFrameDecoder.EightShort;
    internal int NumWindows => IsShort ? 8 : 1;

    // per band data, indexed group * 16 + band
    internal readonly byte[] Codebook = new byte[MaxBands];
    internal readonly short[] Scale = new short[MaxBands];

    // spectrum, window-major: window w occupies [w * 128, w * 128 + 128) for short blocks
    internal readonly float[] Spec = new float[AacFrameDecoder.FrameSize];
    internal readonly int[] Quant = new int[AacFrameDecoder.FrameSize];
    internal readonly float[] Overlap = new float[AacFrameDecoder.FrameSize];
    internal readonly float[] Time = new float[AacFrameDecoder.FrameSize];

    // TNS
    internal bool TnsPresent;
    internal readonly int[] TnsFilterCount = new int[8];
    internal readonly int[] TnsStart = new int[8 * 3];
    internal readonly int[] TnsStop = new int[8 * 3];
    internal readonly int[] TnsOrder = new int[8 * 3];
    internal readonly int[] TnsDirection = new int[8 * 3];
    internal readonly float[] TnsCoef = new float[8 * 3 * 32];

    // pulse data
    internal bool PulsePresent;
    internal int PulseCount;
    internal int PulseStartBand;
    internal readonly int[] PulseOffset = new int[4];
    internal readonly int[] PulseAmp = new int[4];

    // persists between frames
    internal int PrevShape;
    internal readonly float[] LastSpec = new float[AacFrameDecoder.FrameSize];
    internal int LastSequence; // window sequence of the last decoded or concealed frame
    internal bool LastSpecUsable; // LastSpec is a long-block spectrum that can be repeated
    internal int ConcealRun;

    internal void CopyIcsFrom(AacChannelState o)
    {
        WindowSequence = o.WindowSequence;
        WindowShape = o.WindowShape;
        MaxSfb = o.MaxSfb;
        TotalSfb = o.TotalSfb;
        NumGroups = o.NumGroups;
        Array.Copy(o.GroupLength, GroupLength, 8);
    }
}
