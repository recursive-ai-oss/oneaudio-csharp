namespace OneAudio.Opus;

/// <summary>
/// A packet-level Opus decoder (RFC 6716, with the RFC 8251 corrections): SILK, CELT and hybrid modes, all
/// bandwidths and frame sizes from 2.5 to 120 ms, mono and stereo, and packet loss concealment.
/// </summary>
/// <remarks>
/// <para>Output is interleaved <see cref="float"/> at the decoder's sample rate. Samples are not clipped; full scale
/// is ±1.0 and a stream may exceed it slightly.</para>
/// <para>Supported output rates are 48000 and 24000 Hz. Instances are not thread safe. After construction nothing
/// allocates: all state and scratch buffers are created up front.</para>
/// <para>Derived from the reference decoder in RFC 6716 (Simplified BSD licence, see THIRD-PARTY-NOTICES.md).</para>
/// </remarks>
public sealed class OpusDecoder
{
    private const int ModeSilk = 1000;
    private const int ModeHybrid = 1001;
    private const int ModeCelt = 1002;

    private readonly int _fs;
    private readonly int _channels;
    private readonly SilkDecoder _silk = new();
    private readonly CeltDecoder _celt;
    private readonly float[] _pcmTransition;
    private readonly float[] _redundant;
    private readonly short[] _pcmSilk;
    private readonly byte[] _silence = [0xFF, 0xFF];

    private int _streamChannels;
    private int _bandwidth;
    private int _mode;
    private int _prevMode;
    private int _frameSize;
    private bool _prevRedundancy;
    private uint _finalRange;

    /// <summary>Creates a decoder.</summary>
    /// <param name="sampleRate">Output rate in Hz: 48000 or 24000.</param>
    /// <param name="channels">Output channels: 1 or 2. A stereo stream is downmixed to mono and a mono stream is
    /// duplicated to stereo as the Opus decoder specifies.</param>
    /// <exception cref="ArgumentOutOfRangeException">The rate or channel count is not supported.</exception>
    public OpusDecoder(int sampleRate, int channels)
    {
        if (sampleRate != 48000 && sampleRate != 24000) throw new ArgumentOutOfRangeException(nameof(sampleRate), "Supported rates are 48000 and 24000 Hz.");
        if (channels != 1 && channels != 2) throw new ArgumentOutOfRangeException(nameof(channels), "Opus decodes one or two channels.");
        _fs = sampleRate;
        _channels = channels;
        _celt = new CeltDecoder(channels, sampleRate);
        int f20 = sampleRate / 50;
        _pcmTransition = new float[f20 / 4 * channels];
        _redundant = new float[f20 / 4 * channels];
        _pcmSilk = new short[sampleRate / 1000 * 120 * channels];
        _silk.ApiSampleRate = sampleRate;
        _silk.ChannelsApi = channels;
        Reset();
    }

    /// <summary>Output sample rate in Hz.</summary>
    public int SampleRate => _fs;

    /// <summary>Output channel count.</summary>
    public int Channels => _channels;

    /// <summary>The range coder's final state after the last packet (XOR of all its frames), used to check
    /// conformance. Zero after a concealed frame.</summary>
    public uint FinalRange => _finalRange;

    /// <summary>
    /// When true, the decoder does not apply the 180 degree phase shift that intensity stereo may code between the
    /// channels (RFC 8251 section 10). Output then downmixes to mono without cancelling those bands. Default false.
    /// </summary>
    public bool PhaseInversionDisabled
    {
        get => _celt.DisablePhaseInversion;
        set => _celt.DisablePhaseInversion = value;
    }

    /// <summary>Clears all history, as for a new stream.</summary>
    public void Reset()
    {
        _silk.Init();
        _silk.ApiSampleRate = _fs;
        _silk.ChannelsApi = _channels;
        _celt.Reset();
        _celt.Start = 0;
        _celt.End = 21;
        _celt.StreamChannels = _channels;
        _streamChannels = _channels;
        _bandwidth = 0;
        _mode = 0;
        _prevMode = 0;
        _frameSize = _fs / 400;
        _prevRedundancy = false;
        _finalRange = 0;
    }

    /// <summary>Decodes one packet.</summary>
    /// <param name="packet">The packet. An empty packet counts as lost and is concealed.</param>
    /// <param name="pcm">Receives interleaved samples. Must hold at least the packet's duration.</param>
    /// <returns>Samples per channel written.</returns>
    /// <exception cref="AudioFormatException">The packet is malformed.</exception>
    /// <exception cref="ArgumentException"><paramref name="pcm"/> is too small.</exception>
    public int Decode(ReadOnlySpan<byte> packet, Span<float> pcm)
    {
        int cap = pcm.Length / _channels;
        if (packet.IsEmpty) return Conceal(pcm.Slice(0, cap * _channels), Math.Min(cap, _frameSize));

        int toc = packet[0];
        int mode = (toc & 0x80) != 0 ? ModeCelt : (toc & 0x60) == 0x60 ? ModeHybrid : ModeSilk;
        int bandwidth = Bandwidth(toc);
        int frameSize = OpusPacket.SamplesPerFrame((byte)toc, _fs);
        int streamChannels = (toc & 4) != 0 ? 2 : 1;

        Span<int> offsets = stackalloc int[OpusPacket.MaxFrames];
        Span<int> sizes = stackalloc int[OpusPacket.MaxFrames];
        int count = OpusPacket.Parse(packet, false, offsets, sizes, out _, out _);
        if (count < 0) throw new AudioFormatException("Invalid Opus packet.");
        if (count * frameSize > cap) throw new ArgumentException("Output buffer is too small for the packet.", nameof(pcm));

        _mode = mode;
        _bandwidth = bandwidth;
        _frameSize = frameSize;
        _streamChannels = streamChannels;

        int done = 0;
        for (int i = 0; i < count; i++)
        {
            var frame = packet.Slice(offsets[i], sizes[i]);
            int ret = DecodeFrame(frame, pcm.Slice(done * _channels), cap - done);
            done += ret;
        }
        return done;
    }

    /// <summary>Conceals one lost packet.</summary>
    /// <param name="pcm">Receives interleaved samples; its length sets how much is concealed (a multiple of 2.5 ms
    /// is expected, at most 120 ms).</param>
    /// <returns>Samples per channel written.</returns>
    public int DecodePacketLoss(Span<float> pcm)
    {
        int frames = Math.Min(pcm.Length / _channels, OpusPacket.MaxSamplesPer48k * _fs / 48000);
        return Conceal(pcm, frames);
    }

    private int Conceal(Span<float> pcm, int frames)
    {
        int f25 = _fs / 400;
        if (frames < f25) throw new ArgumentException("Output buffer is too small (at least 2.5 ms).", nameof(pcm));
        frames -= frames % f25;
        return DecodeFrame(ReadOnlySpan<byte>.Empty, pcm, frames);
    }

    private static int Bandwidth(int toc)
    {
        // 0 narrow, 1 medium, 2 wide, 3 super-wide, 4 full (RFC 6716 table 2).
        if ((toc & 0x80) != 0)
        {
            int b = (toc >> 5) & 3;
            return b == 0 ? 0 : b + 1;
        }
        if ((toc & 0x60) == 0x60) return (toc & 0x10) != 0 ? 4 : 3;
        return (toc >> 5) & 3;
    }

    // opus_decode_frame. An empty `data` (length 0 or 1) conceals instead of decoding.
    private int DecodeFrame(ReadOnlySpan<byte> data, Span<float> pcm, int frameSize)
    {
        int len = data.Length;
        int ch = _channels;
        int f20 = _fs / 50, f10 = f20 >> 1, f5 = f10 >> 1, f25 = f5 >> 1;
        if (frameSize < f25) throw new ArgumentException("Output buffer is too small (at least 2.5 ms).", nameof(pcm));

        bool hasData = len > 1;
        int audioSize;
        int mode;
        var dec = new RangeDecoder(hasData ? data : ReadOnlySpan<byte>.Empty);
        if (!hasData)
        {
            // A one-byte packet carries only a TOC: conceal no more than it announces.
            if (len == 1) frameSize = Math.Min(frameSize, _frameSize);
            audioSize = frameSize;
            if (_prevMode == 0)
            {
                pcm.Slice(0, audioSize * ch).Clear();
                return audioSize;
            }
            mode = _prevMode;
        }
        else
        {
            audioSize = _frameSize;
            mode = _mode;
        }

        if (!hasData && frameSize > f20 && mode != ModeSilk)
        {
            int n = 0;
            do
            {
                int r = DecodeFrame(ReadOnlySpan<byte>.Empty, pcm.Slice(n * ch), f20);
                n += r;
            } while (n < frameSize);
            return frameSize;
        }

        bool transition = false;
        if (hasData && _prevMode > 0 &&
            ((mode == ModeCelt && _prevMode != ModeCelt && !_prevRedundancy) || (mode != ModeCelt && _prevMode == ModeCelt)))
        {
            transition = true;
            if (mode == ModeCelt) DecodeFrame(ReadOnlySpan<byte>.Empty, _pcmTransition, Math.Min(f5, audioSize));
        }

        if (audioSize > frameSize) throw new ArgumentException("Output buffer is too small for the frame.", nameof(pcm));
        frameSize = audioSize;

        // SILK.
        if (mode != ModeCelt)
        {
            if (_prevMode == ModeCelt) _silk.Init();
            _silk.PayloadSizeMs = Math.Max(10, 1000 * audioSize / _fs);
            if (hasData)
            {
                _silk.ChannelsInternal = _streamChannels;
                _silk.InternalSampleRate = mode == ModeSilk ? (_bandwidth == 0 ? 8000 : _bandwidth == 1 ? 12000 : 16000) : 16000;
            }
            bool lost = !hasData;
            int decoded = 0;
            var silkOut = _pcmSilk.AsSpan();
            do
            {
                _silk.Decode(ref dec, lost, decoded == 0, silkOut, out int nOut);
                silkOut = silkOut.Slice(nOut * ch);
                decoded += nOut;
            } while (decoded < frameSize);
        }

        // Redundancy side information (RFC 6716 section 4.5.1).
        int startBand = 0;
        bool redundancy = false, celtToSilk = false;
        int redundancyBytes = 0;
        uint redundantRng = 0;
        if (mode != ModeCelt && hasData && dec.Tell() + 17 + (_mode == ModeHybrid ? 20 : 0) <= 8 * len)
        {
            redundancy = mode == ModeHybrid ? dec.BitLogP(12) != 0 : true;
            if (redundancy)
            {
                celtToSilk = dec.BitLogP(1) != 0;
                redundancyBytes = mode == ModeHybrid ? (int)dec.DecodeUInt(256) + 2 : len - ((dec.Tell() + 7) >> 3);
                len -= redundancyBytes;
                if (len * 8 < dec.Tell())
                {
                    len = 0;
                    redundancyBytes = 0;
                    redundancy = false;
                }
                dec.ShrinkStorage(redundancyBytes);
            }
        }
        if (mode != ModeCelt) startBand = 17;

        int endBand = _bandwidth switch { 0 => 13, 1 or 2 => 17, 3 => 19, _ => 21 };
        _celt.End = endBand;
        _celt.StreamChannels = _streamChannels;

        if (redundancy) transition = false;
        if (transition && mode != ModeCelt)
            DecodeFrame(ReadOnlySpan<byte>.Empty, _pcmTransition, Math.Min(f5, audioSize));

        if (redundancy && celtToSilk)
        {
            _celt.Start = 0;
            _celt.Decode(data.Slice(len, redundancyBytes), _redundant, f5);
            redundantRng = _celt.FinalRange;
        }

        _celt.Start = startBand;

        var window = CeltTables.Window;
        if (mode != ModeSilk)
        {
            int celtFrame = Math.Min(f20, frameSize);
            if (mode != _prevMode && _prevMode > 0 && !_prevRedundancy) _celt.Reset();
            if (hasData) _celt.DecodeFrame(ref dec, len, pcm, celtFrame);
            else _celt.DecodeLost(pcm, celtFrame);
        }
        else
        {
            pcm.Slice(0, frameSize * ch).Clear();
            // Hybrid to SILK: let the CELT overlap fade out by decoding a silence frame.
            if (_prevMode == ModeHybrid && !(redundancy && celtToSilk && _prevRedundancy))
            {
                _celt.Start = 0;
                _celt.Decode(_silence, pcm, f25);
            }
        }

        if (mode != ModeCelt)
        {
            const float scale = 1f / 32768f;
            int n = frameSize * ch;
            for (int i = 0; i < n; i++) pcm[i] += scale * _pcmSilk[i];
        }

        if (redundancy && !celtToSilk)
        {
            _celt.Reset();
            _celt.Start = 0;
            _celt.Decode(data.Slice(len, redundancyBytes), _redundant, f5);
            redundantRng = _celt.FinalRange;
            var tail = pcm.Slice(ch * (frameSize - f25));
            SmoothFade(tail, _redundant.AsSpan(ch * f25), tail, f25, window);
        }
        if (redundancy && celtToSilk)
        {
            _redundant.AsSpan(0, ch * f25).CopyTo(pcm);
            var head = pcm.Slice(ch * f25);
            SmoothFade(_redundant.AsSpan(ch * f25), head, head, f25, window);
        }
        if (transition)
        {
            if (audioSize >= f5)
            {
                _pcmTransition.AsSpan(0, ch * f25).CopyTo(pcm);
                var head = pcm.Slice(ch * f25);
                SmoothFade(_pcmTransition.AsSpan(ch * f25), head, head, f25, window);
            }
            else
            {
                SmoothFade(_pcmTransition, pcm, pcm, f25, window);
            }
        }

        _finalRange = len <= 1 ? 0 : dec.Range ^ redundantRng;
        _prevMode = mode;
        _prevRedundancy = redundancy && !celtToSilk;
        return audioSize;
    }

    // Cross-fade over `overlap` samples with the squared CELT window (RFC 6716 section 4.5.1.2).
    private void SmoothFade(ReadOnlySpan<float> in1, ReadOnlySpan<float> in2, Span<float> output, int overlap, float[] window)
    {
        int inc = 48000 / _fs;
        int ch = _channels;
        for (int c = 0; c < ch; c++)
        {
            for (int i = 0; i < overlap; i++)
            {
                float w = window[i * inc] * window[i * inc];
                output[i * ch + c] = w * in2[i * ch + c] + (1f - w) * in1[i * ch + c];
            }
        }
    }
}
