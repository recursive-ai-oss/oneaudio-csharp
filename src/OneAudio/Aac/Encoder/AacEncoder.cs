namespace OneAudio.Aac;

/// <summary>
/// AAC-LC encoder (ISO/IEC 14496-3 profile LC, 1024-sample blocks) for mono and stereo at 8000 to 48000 Hz. PCM in, access
/// units out, with the AudioSpecificConfig an MP4 <c>esds</c> box needs and the timing a container needs for an edit list.
/// <para>
/// Usage mirrors the packet-level FLAC encoder: <see cref="Write"/> takes input until one hop of 1024 frames is complete and
/// returns how much it took; collect units with <see cref="TryReadPacket"/>; offer the rest. <see cref="Finish"/> flushes the
/// tail. The encoder looks one hop ahead to choose window shapes, so a unit appears after the hop that follows it is written.
/// </para>
/// <code>
/// var enc = new AacEncoder(44100, 2, new AacEncoderSettings { Bitrate = 128000 });
/// while (input.Length > 0)
/// {
///     int taken = enc.Write(input);
///     input = input.Slice(taken * 2);
///     while (enc.TryReadPacket(out var p)) mp4.AddSample(p.Data.Span);
/// }
/// enc.Finish();
/// while (enc.TryReadPacket(out var p)) mp4.AddSample(p.Data.Span);
/// // Edit list: start at enc.PrimingFrames, duration enc.FramesWritten; trailing padding = enc.EndPaddingFrames.
/// </code>
/// <para>
/// <b>Timing.</b> Unit <i>k</i> covers decoder output samples [<i>k</i>·1024, (<i>k</i>+1)·1024) of a stream whose first
/// <see cref="PrimingFrames"/> (1024) samples are the transform's overlap with silence: input sample 0 appears at decoder output
/// sample 1024. After <see cref="Finish"/> the stream holds ceil(frames/1024) + 1 units, and the last
/// <see cref="EndPaddingFrames"/> output samples (0 to 1023) are padding. Decoding the units and dropping the priming and the
/// padding returns exactly the input length.
/// </para>
/// <para>
/// <b>Method.</b> Kaiser-Bessel-derived windows; long and eight-short blocks with start and stop windows chosen from a
/// high-frequency attack detector; per-band mid/side; a threshold model of the kind in the floating-point reference encoder of 3GPP TS 26.410 (band energy at a fixed
/// signal-to-mask ratio, Bark spreading, absolute threshold, pre-echo limiting); a rate loop that raises the allowed noise in
/// the fourth-root domain of the thresholds until the block fits its budget; scalefactor search against the allowed noise;
/// optimal section partition over codebooks 1 to 11; perceptual noise substitution (ISO/IEC 14496-3 4.5.2.3.4) for bands above
/// 4 kHz that would otherwise decode as holes (on by default, see <see cref="AacEncoderSettings.NoiseSubstitution"/>). No TNS,
/// intensity stereo, LTP or SBR. The block's bits are exact: nothing is estimated.
/// </para>
/// <para>
/// <b>Decoder agreement.</b> Decoders agree to within rounding (about 1e-7 of full scale) everywhere except in noise-substituted
/// bands, which every decoder fills with its own noise of the coded energy: those bands match in energy, not sample by sample.
/// Set <see cref="AacEncoderSettings.NoiseSubstitution"/> to false for output that decodes identically everywhere.
/// </para>
/// After construction no allocation happens per block. Output is deterministic for identical input and settings on a platform.
/// Channels and rate are fixed at construction; feed one encoder one stream. Instances are not thread-safe.
/// </summary>
public sealed class AacEncoder
{
    private const int Hop = 1024;
    private const int QueueSize = 4;

    private readonly AacEncCore _core;
    private readonly int _channels, _rate, _rateIndex;
    private readonly bool _adts;
    private readonly byte[][] _slots = new byte[QueueSize][];
    private readonly int[] _lengths = new int[QueueSize];
    private int _head, _count;
    private int _fill, _slot = 1;
    private long _frames, _units, _expectedUnits;
    private bool _finished;
    private readonly byte[] _asc;

    /// <summary>Creates an encoder.</summary>
    /// <param name="sampleRate">8000, 11025, 12000, 16000, 22050, 24000, 32000, 44100 or 48000 Hz.</param>
    /// <param name="channels">1 or 2.</param>
    /// <param name="settings">Encoder options; defaults when null.</param>
    /// <exception cref="ArgumentException">The rate, channel count or a setting is not supported or out of range.</exception>
    public AacEncoder(int sampleRate, int channels, AacEncoderSettings? settings = null)
    {
        settings ??= new AacEncoderSettings();
        _rateIndex = AacEncTables.RateIndex(sampleRate);
        if (_rateIndex < 0) throw new ArgumentException($"AAC-LC encoding supports 8000 to 48000 Hz (8000, 11025, 12000, 16000, 22050, 24000, 32000, 44100, 48000), not {sampleRate}.", nameof(sampleRate));
        if (channels is < 1 or > 2) throw new ArgumentException("The AAC encoder takes one or two channels.", nameof(channels));
        double maxRate = 0.9 * 6144.0 * channels * sampleRate / Hop;
        if (settings.Bitrate == 0) settings = settings with { Bitrate = (int)Math.Max(8000 * channels, Math.Min(64000 * channels, maxRate / 2)) };
        if (settings.Bitrate < 8000 * channels) throw new ArgumentException("Bitrate must be at least 8000 bit/s per channel.", nameof(settings));
        if (settings.Bitrate > maxRate) throw new ArgumentException($"Bitrate {settings.Bitrate} is above the {(int)maxRate} bit/s the format allows at {sampleRate} Hz with {channels} channel(s).", nameof(settings));
        if (!Enum.IsDefined(settings.RateControl)) throw new ArgumentException("Unknown rate control mode.", nameof(settings));
        if (!(settings.VbrQuality >= 0 && settings.VbrQuality <= 1)) throw new ArgumentException("VbrQuality must be 0 to 1.", nameof(settings));
        if (settings.Bandwidth < 0) throw new ArgumentException("Bandwidth must not be negative.", nameof(settings));
        _rate = sampleRate;
        _channels = channels;
        _adts = settings.Adts;
        _core = new AacEncCore(sampleRate, channels, settings);
        for (int i = 0; i < QueueSize; i++) _slots[i] = new byte[_core.MaxBytes + 8];

        // AudioSpecificConfig (14496-3 1.6.2.1): object type 2 (AAC LC), rate index, channel configuration, GASpecificConfig 000.
        int v = (2 << 11) | (_rateIndex << 7) | (channels << 3);
        _asc = [(byte)(v >> 8), (byte)v];
        Bandwidth = _core.Bandwidth;
    }

    /// <summary>The AudioSpecificConfig: 2 bytes for the <c>esds</c> box of an MP4 track (object type 2, the rate, the channels).</summary>
    public ReadOnlyMemory<byte> AudioSpecificConfig => _asc;

    /// <summary>Sampling rate in Hz.</summary>
    public int SampleRate => _rate;

    /// <summary>Channel count (1 or 2).</summary>
    public int Channels => _channels;

    /// <summary>Samples per channel in every access unit: 1024.</summary>
    public const int FrameLength = Hop;

    /// <summary>The low-pass frequency in Hz in effect.</summary>
    public int Bandwidth { get; }

    /// <summary>
    /// Output samples per channel at the start of the decoded stream that precede the first input sample (1024: the first unit's
    /// transform window overlaps silence). Write it as the start of an MP4 edit list, or as the first value of iTunSMPB (plus the
    /// decoder's own delay if the player adds one).
    /// </summary>
    public const int PrimingFrames = Hop;

    /// <summary>Largest unit this encoder emits, in bytes (including the ADTS header when enabled): 768 per channel plus 8.</summary>
    public int MaxPacketBytes => _core.MaxBytes + (_adts ? 7 : 0);

    /// <summary>Input frames (samples per channel) accepted so far.</summary>
    public long FramesWritten => _frames;

    /// <summary>Access units produced so far.</summary>
    public long UnitsProduced => _units;

    /// <summary>Output samples per channel after the last input sample in the final unit; 0 to 1023, valid after <see cref="Finish"/>.</summary>
    public int EndPaddingFrames { get; private set; }

    /// <summary>
    /// Encodes interleaved <see cref="float"/> frames in [-1, 1] (full scale is 1.0; larger values are clipped by the quantizer's
    /// range, NaN counts as 0). Takes input only until the current hop of 1024 frames is complete.
    /// </summary>
    /// <param name="samples">Interleaved samples; the length must be a multiple of the channel count.</param>
    /// <returns>Frames taken, 0 to 1024. Less than the input holds when a hop completed; collect with <see cref="TryReadPacket"/> and offer the rest. 0 while units are unread.</returns>
    /// <exception cref="ArgumentException">The length is not a whole number of frames.</exception>
    /// <exception cref="InvalidOperationException">The encoder is finished.</exception>
    public int Write(ReadOnlySpan<float> samples)
    {
        if (_finished) throw new InvalidOperationException("The encoder is finished.");
        if (samples.Length % _channels != 0) throw new ArgumentException("The sample count must be a multiple of the channel count.", nameof(samples));
        if (_count > 0 || samples.Length == 0) return 0;
        int take = Math.Min(samples.Length / _channels, Hop - _fill);
        for (int ch = 0; ch < _channels; ch++)
        {
            var dst = _core.History(ch).AsSpan(_slot * Hop + _fill, take);
            int src = ch;
            for (int i = 0; i < take; i++, src += _channels)
            {
                float v = samples[src] * 32768f;
                dst[i] = float.IsNaN(v) ? 0f : Math.Clamp(v, -1e6f, 1e6f);
            }
        }
        _fill += take;
        _frames += take;
        if (_fill == Hop) CompleteHop();
        return take;
    }

    private void CompleteHop()
    {
        _fill = 0;
        if (_slot == 1)
        {
            _slot = 2; // the first hop only primes the lookahead
            return;
        }
        Emit();
    }

    private void Emit()
    {
        int tail = (_head + _count) % QueueSize;
        var buf = _slots[tail];
        int off = _adts ? 7 : 0;
        int n = _core.EncodeBlock();
        _core.Output(n).CopyTo(buf.AsSpan(off));
        _core.Shift();
        if (_adts) WriteAdts(buf, n + 7);
        _lengths[tail] = n + off;
        _count++;
        _units++;
    }

    // ADTS fixed and variable header (14496-3 1.A.2.2.1), MPEG-4, no CRC.
    private void WriteAdts(byte[] b, int frameLength)
    {
        int profile = 1; // object type 2 minus 1
        b[0] = 0xFF;
        b[1] = 0xF1; // sync, MPEG-4, layer 0, protection absent
        b[2] = (byte)((profile << 6) | (_rateIndex << 2) | (_channels >> 2));
        b[3] = (byte)(((_channels & 3) << 6) | (frameLength >> 11));
        b[4] = (byte)((frameLength >> 3) & 0xFF);
        b[5] = (byte)(((frameLength & 7) << 5) | 0x1F); // buffer fullness 0x7FF: variable rate
        b[6] = 0xFC; // fullness low bits, one raw data block
    }

    /// <summary>Takes the oldest finished access unit.</summary>
    /// <param name="packet">The unit; its data stays valid until the next <see cref="Write"/> or <see cref="Finish"/>.</param>
    /// <returns>False when none is waiting.</returns>
    public bool TryReadPacket(out AacPacket packet)
    {
        if (_count == 0)
        {
            packet = default;
            return false;
        }
        long index = _units - _count;
        packet = new AacPacket(_slots[_head].AsMemory(0, _lengths[_head]), index, index * Hop, Hop);
        _head = (_head + 1) % QueueSize;
        _count--;
        return true;
    }

    /// <summary>
    /// Ends the stream: pads the last hop with silence and flushes the units that cover it. Collect them with
    /// <see cref="TryReadPacket"/>. Does nothing when called again. With no input written there are no units.
    /// </summary>
    public void Finish()
    {
        if (_finished) return;
        _finished = true;
        if (_frames == 0) return;
        long k = (_frames + Hop - 1) / Hop;
        _expectedUnits = k + 1;
        EndPaddingFrames = (int)(k * Hop - _frames);
        // Zero-fill the rest of the hop in progress, then enough silent hops that the unit covering the last real hop is emitted.
        if (_fill > 0)
        {
            for (int ch = 0; ch < _channels; ch++)
                _core.History(ch).AsSpan(_slot * Hop + _fill, Hop - _fill).Clear();
            _fill = Hop;
            CompleteHop();
        }
        while (_units < _expectedUnits)
        {
            for (int ch = 0; ch < _channels; ch++) _core.History(ch).AsSpan(_slot * Hop, Hop).Clear();
            CompleteHop();
        }
    }
}
