namespace OneAudio.Amr;

/// <summary>
/// Frame sizes of AMR (narrowband) and AMR-WB (wideband) speech frames, from the frame type in the table-of-contents byte
/// (3GPP TS 26.101 table A.1b, TS 26.201 table 2; RFC 4867 4.3.2 and 5.3). A stored frame is one TOC byte
/// (<c>P FT(4) Q P P</c>) followed by the speech bits padded to whole bytes.
/// </summary>
public static class AmrFrameTable
{
    // Speech bytes after the TOC byte; -1 marks frame types that carry no defined payload layout.
    // NB: 4.75 5.15 5.9 6.7 7.4 7.95 10.2 12.2 kbit/s, SID, then reserved 9-14, NO_DATA.
    private static readonly sbyte[] Narrow = [12, 13, 15, 17, 19, 20, 26, 31, 5, -1, -1, -1, -1, -1, -1, 0];
    // WB: 6.60 8.85 12.65 14.25 15.85 18.25 19.85 23.05 23.85 kbit/s, SID, reserved 10-13, SPEECH_LOST, NO_DATA.
    private static readonly sbyte[] Wide = [17, 23, 32, 36, 40, 46, 50, 58, 60, 5, -1, -1, -1, -1, 0, 0];

    /// <summary>Samples per frame: 160 at 8000 Hz (narrowband) or 320 at 16000 Hz (wideband); both are 20 ms.</summary>
    /// <param name="wideband">True for AMR-WB.</param>
    public static int SamplesPerFrame(bool wideband) => wideband ? 320 : 160;

    /// <summary>The number of speech bytes after the TOC byte for a frame type.</summary>
    /// <param name="wideband">True for AMR-WB, false for AMR.</param>
    /// <param name="frameType">The FT field, 0 to 15.</param>
    /// <returns>The byte count (0 for NO_DATA and SPEECH_LOST), or -1 for a reserved frame type or an out-of-range argument.</returns>
    public static int PayloadBytes(bool wideband, int frameType)
    {
        if ((uint)frameType > 15) return -1;
        return (wideband ? Wide : Narrow)[frameType];
    }

    /// <summary>The frame type field of a table-of-contents byte.</summary>
    /// <param name="toc">The first byte of a stored frame.</param>
    public static int FrameType(byte toc) => (toc >> 3) & 15;
}
