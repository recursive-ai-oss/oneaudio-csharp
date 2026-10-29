namespace OneAudio.Aac;

/// <summary>Coding tools an AAC stream has used so far, as reported by <see cref="AacDecoder.ToolsSeen"/>.</summary>
[Flags]
public enum AacTools
{
    /// <summary>Nothing recorded yet.</summary>
    None = 0,
    /// <summary>Pulse data.</summary>
    Pulse = 1,
    /// <summary>Temporal noise shaping.</summary>
    Tns = 2,
    /// <summary>Perceptual noise substitution.</summary>
    Pns = 4,
    /// <summary>Mid/side stereo.</summary>
    MidSide = 8,
    /// <summary>Intensity stereo.</summary>
    Intensity = 16,
    /// <summary>Eight-short-window blocks (with start and stop windows around them).</summary>
    ShortBlocks = 32,
    /// <summary>Kaiser-Bessel-derived window shape.</summary>
    KbdWindow = 64,
    /// <summary>Grouped short windows (scale factor grouping).</summary>
    WindowGrouping = 128,
    /// <summary>SBR data in fill elements (skipped).</summary>
    SbrFill = 256,
}
