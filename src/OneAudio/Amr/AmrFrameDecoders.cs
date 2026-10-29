// Copyright (c) Recursive AI, LLC. MIT OR Apache-2.0.
using OneAudio.Amr;

namespace OneAudio;

/// <summary>
/// Decodes AMR (narrowband, 3GPP TS 26.071) speech frames to 8 kHz mono PCM, frame by frame. Input is one frame in the
/// storage layout of RFC 4867 section 5.3: a table-of-contents byte (<c>P FT(4) Q P P</c>) followed by the speech bits
/// padded to whole bytes, which is what AMR files and 3GP <c>samr</c> samples hold. The output is bit-exact against the
/// 3GPP reference decoder (TS 26.073, test sequences of TS 26.074).
/// <para>
/// Frame types: 0..7 are the speech modes 4.75..12.2 kbit/s, 8 is a comfort-noise (SID) frame, 15 is "no data" (which the
/// decoder bridges with comfort noise or concealment as TS 26.092 and TS 26.091 define). Types 9..14 (the SID frames of
/// other codecs and reserved values) are rejected. A clear Q bit marks a damaged frame, which is concealed.
/// </para>
/// <para>
/// Output samples are 13-bit values in 16-bit words (the low three bits are zero), as the reference decoder writes
/// them; the float overload divides by 32768. A decoder instance holds the codec state across frames and allocates
/// nothing per frame. Instances are not thread-safe.
/// </para>
/// </summary>
public sealed class AmrNbDecoder : IAmrFrameDecoder
{
    /// <summary>Output sample rate, 8000 Hz.</summary>
    public const int SampleRate = 8000;

    /// <summary>Samples in the 20 ms of audio each frame produces, 160.</summary>
    public const int SamplesPerFrame = 160;

    private static readonly byte[] PayloadBytes = [12, 13, 15, 17, 19, 20, 26, 31, 5];
    private static readonly short[] PayloadBits = [95, 103, 118, 134, 148, 159, 204, 244, 35];

    private static readonly short[][] Sort =
    [
        AmrNbTables.Sort475, AmrNbTables.Sort515, AmrNbTables.Sort59, AmrNbTables.Sort67, AmrNbTables.Sort74,
        AmrNbTables.Sort795, AmrNbTables.Sort102, AmrNbTables.Sort122, AmrNbTables.SortSid,
    ];

    private readonly AmrNbCore _core = new();
    private readonly short[] _serial = new short[244 + 8];
    private readonly short[] _pcm = new short[SamplesPerFrame];

    /// <summary>Returns the decoder to its initial state, as at the start of a new stream.</summary>
    public void Reset() => _core.ResetAll();

    /// <summary>Decodes one frame to 16-bit PCM.</summary>
    /// <param name="frame">TOC byte plus speech bits.</param>
    /// <param name="pcm">Receives <see cref="SamplesPerFrame"/> samples; must be at least that long.</param>
    /// <exception cref="AudioFormatException">The frame type is reserved or the frame length does not match it.</exception>
    /// <exception cref="ArgumentException"><paramref name="pcm"/> is too short.</exception>
    public void Decode(ReadOnlySpan<byte> frame, Span<short> pcm)
    {
        if (pcm.Length < SamplesPerFrame) throw new ArgumentException("Output must hold 160 samples.", nameof(pcm));
        if (frame.IsEmpty) throw new AudioFormatException("Empty AMR frame.");
        int ft = (frame[0] >> 3) & 15;
        bool good = (frame[0] & 4) != 0;
        int rx, mode = 0;
        if (ft == 15)
        {
            if (frame.Length != 1) throw new AudioFormatException("AMR no-data frame carries payload bytes.");
            rx = AmrNbCore.RxNoData;
        }
        else if (ft > 8)
        {
            throw new AudioFormatException($"AMR frame type {ft} is reserved or belongs to another codec.");
        }
        else
        {
            if (frame.Length != 1 + PayloadBytes[ft])
                throw new AudioFormatException($"AMR frame type {ft} needs {PayloadBytes[ft]} payload bytes, found {frame.Length - 1}.");
            ReadOnlySpan<byte> data = frame.Slice(1);
            short[] sort = Sort[ft];
            int bits = PayloadBits[ft];
            for (int i = 0; i < bits; i++) _serial[sort[i]] = (short)((data[i >> 3] >> (7 - (i & 7))) & 1);
            if (ft == 8)
            {
                // TS 26.101 section 4.1.3: after the 35 SID bits come the SID type (0 first, 1 update) and the mode
                // indication, least significant bit first.
                bool update = ((data[4] >> 4) & 1) != 0;
                mode = (data[4] >> 3) & 1 | (((data[4] >> 2) & 1) << 1) | (((data[4] >> 1) & 1) << 2);
                rx = good ? (update ? AmrNbCore.RxSidUpdate : AmrNbCore.RxSidFirst) : AmrNbCore.RxSidBad;
            }
            else
            {
                mode = ft;
                rx = good ? AmrNbCore.RxSpeechGood : AmrNbCore.RxSpeechBad;
            }
        }
        _core.Decode(rx, mode, _serial, pcm);
    }

    /// <summary>Decodes one frame to float samples in [-1, 1).</summary>
    /// <param name="frame">TOC byte plus speech bits.</param>
    /// <param name="pcm">Receives <see cref="SamplesPerFrame"/> samples.</param>
    /// <exception cref="AudioFormatException">The frame type is reserved or the frame length does not match it.</exception>
    public void Decode(ReadOnlySpan<byte> frame, Span<float> pcm)
    {
        if (pcm.Length < SamplesPerFrame) throw new ArgumentException("Output must hold 160 samples.", nameof(pcm));
        Decode(frame, _pcm);
        for (int i = 0; i < SamplesPerFrame; i++) pcm[i] = _pcm[i] * (1f / 32768f);
    }

    /// <summary>
    /// Produces a frame for a packet that never arrived, by concealment (TS 26.091): the frame is decoded as a damaged
    /// frame of the previous mode.
    /// </summary>
    /// <param name="pcm">Receives <see cref="SamplesPerFrame"/> samples.</param>
    public void DecodeLost(Span<short> pcm)
    {
        if (pcm.Length < SamplesPerFrame) throw new ArgumentException("Output must hold 160 samples.", nameof(pcm));
        _core.Decode(AmrNbCore.RxSpeechBad, _core.PrevMode, _serial, pcm);
    }

    int IAmrFrameDecoder.SamplesPerFrame => SamplesPerFrame;
    void IAmrFrameDecoder.Decode(ReadOnlySpan<byte> frame, Span<short> pcm) => Decode(frame, pcm);
}

/// <summary>Frame decoder the stream decoder drives; implemented by the narrowband and wideband decoders.</summary>
internal interface IAmrFrameDecoder
{
    /// <summary>Samples per frame, 160 (narrowband) or 320 (wideband).</summary>
    int SamplesPerFrame { get; }

    /// <summary>Returns the decoder to its initial state.</summary>
    void Reset();

    /// <summary>Decodes one TOC-prefixed frame to 16-bit PCM.</summary>
    /// <param name="frame">TOC byte plus speech bits.</param>
    /// <param name="pcm">Receives <see cref="SamplesPerFrame"/> samples.</param>
    void Decode(ReadOnlySpan<byte> frame, Span<short> pcm);
}

/// <summary>
/// Decodes AMR-WB (wideband, 3GPP TS 26.171, ITU-T G.722.2) speech frames to 16 kHz mono PCM, frame by frame. Input is
/// one frame in the storage layout of RFC 4867 section 5.3 (TOC byte, then the speech bits padded to whole bytes), as held
/// by <c>.awb</c> files and 3GP <c>sawb</c> samples. The output is bit-exact against the 3GPP reference decoder
/// (TS 26.173, test sequences of TS 26.174).
/// <para>
/// Frame types: 0..8 are the modes 6.60..23.85 kbit/s, 9 is a comfort-noise (SID) frame, 14 is "speech lost" and 15 is
/// "no data" (both concealed or bridged with comfort noise as TS 26.191 and TS 26.192 define). Types 10..13 are reserved
/// and rejected. A clear Q bit marks a damaged frame, which is concealed.
/// </para>
/// <para>
/// Output samples are 14-bit values in 16-bit words (the low two bits are zero), as the reference decoder writes
/// them; the float overload divides by 32768. A decoder instance holds the codec state across frames and allocates
/// nothing per frame. Instances are not thread-safe.
/// </para>
/// </summary>
public sealed class AmrWbDecoder : IAmrFrameDecoder
{
    /// <summary>Output sample rate, 16000 Hz.</summary>
    public const int SampleRate = 16000;

    /// <summary>Samples in the 20 ms of audio each frame produces, 320.</summary>
    public const int SamplesPerFrame = 320;

    private static readonly byte[] PayloadBytes = [17, 23, 32, 36, 40, 46, 50, 58, 60, 5];

    private static readonly short[][] Sort =
    [
        AmrWbTables.Sort660, AmrWbTables.Sort885, AmrWbTables.Sort1265, AmrWbTables.Sort1425, AmrWbTables.Sort1585,
        AmrWbTables.Sort1825, AmrWbTables.Sort1985, AmrWbTables.Sort2305, AmrWbTables.Sort2385, AmrWbTables.SortSid,
    ];

    private readonly AmrWbCore _core = new();
    private readonly short[] _bits = new short[480];
    private readonly short[] _pcm = new short[SamplesPerFrame];

    /// <summary>Returns the decoder to its initial state, as at the start of a new stream.</summary>
    public void Reset() => _core.ResetAll();

    /// <summary>Decodes one frame to 16-bit PCM.</summary>
    /// <param name="frame">TOC byte plus speech bits.</param>
    /// <param name="pcm">Receives <see cref="SamplesPerFrame"/> samples; must be at least that long.</param>
    /// <exception cref="AudioFormatException">The frame type is reserved or the frame length does not match it.</exception>
    /// <exception cref="ArgumentException"><paramref name="pcm"/> is too short.</exception>
    public void Decode(ReadOnlySpan<byte> frame, Span<short> pcm)
    {
        if (pcm.Length < SamplesPerFrame) throw new ArgumentException("Output must hold 320 samples.", nameof(pcm));
        if (frame.IsEmpty) throw new AudioFormatException("Empty AMR-WB frame.");
        int ft = (frame[0] >> 3) & 15;
        bool good = (frame[0] & 4) != 0;
        int rx, mode = _core.PrevMode, count = 0;
        if (ft == 15 || ft == 14)
        {
            if (frame.Length != 1) throw new AudioFormatException("AMR-WB frame without data carries payload bytes.");
            rx = ft == 15 ? AmrWbCore.RxNoData : AmrWbCore.RxSpeechLost;
        }
        else if (ft > 9)
        {
            throw new AudioFormatException($"AMR-WB frame type {ft} is reserved.");
        }
        else
        {
            if (frame.Length != 1 + PayloadBytes[ft])
                throw new AudioFormatException($"AMR-WB frame type {ft} needs {PayloadBytes[ft]} payload bytes, found {frame.Length - 1}.");
            ReadOnlySpan<byte> data = frame.Slice(1);
            short[] sort = Sort[ft];
            count = AmrWbCore.NbOfBits[ft];
            for (int i = 0; i < count; i++) _bits[sort[i]] = (short)((data[i >> 3] >> (7 - (i & 7))) & 1);
            if (ft == 9)
            {
                // TS 26.201 section 4.2.1: the SID type bit follows the 35 SID bits (0 first, 1 update).
                bool update = ((data[4] >> 4) & 1) != 0;
                rx = good ? (update ? AmrWbCore.RxSidUpdate : AmrWbCore.RxSidFirst) : AmrWbCore.RxSidBad;
            }
            else
            {
                mode = ft;
                rx = good ? AmrWbCore.RxSpeechGood : AmrWbCore.RxSpeechBad;
            }
        }
        _core.Decode(rx, mode, _bits, count, pcm);
    }

    /// <summary>Decodes one frame to float samples in [-1, 1).</summary>
    /// <param name="frame">TOC byte plus speech bits.</param>
    /// <param name="pcm">Receives <see cref="SamplesPerFrame"/> samples.</param>
    /// <exception cref="AudioFormatException">The frame type is reserved or the frame length does not match it.</exception>
    public void Decode(ReadOnlySpan<byte> frame, Span<float> pcm)
    {
        if (pcm.Length < SamplesPerFrame) throw new ArgumentException("Output must hold 320 samples.", nameof(pcm));
        Decode(frame, _pcm);
        for (int i = 0; i < SamplesPerFrame; i++) pcm[i] = _pcm[i] * (1f / 32768f);
    }

    /// <summary>Produces a frame for a packet that never arrived, by concealment (TS 26.191).</summary>
    /// <param name="pcm">Receives <see cref="SamplesPerFrame"/> samples.</param>
    public void DecodeLost(Span<short> pcm)
    {
        if (pcm.Length < SamplesPerFrame) throw new ArgumentException("Output must hold 320 samples.", nameof(pcm));
        _core.Decode(AmrWbCore.RxSpeechLost, _core.PrevMode, _bits, 0, pcm);
    }

    int IAmrFrameDecoder.SamplesPerFrame => SamplesPerFrame;
    void IAmrFrameDecoder.Decode(ReadOnlySpan<byte> frame, Span<short> pcm) => Decode(frame, pcm);
}
