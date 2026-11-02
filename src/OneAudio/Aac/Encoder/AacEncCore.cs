namespace OneAudio.Aac;

/// <summary>The coding state of one channel of one block: the spectrum in bitstream order, per-band targets and the chosen quantization.</summary>
internal sealed class AacEncChannel
{
    public const int MaxEntries = 128;

    /// <summary>MDCT lines in natural order (long: line k; short: window * 128 + k).</summary>
    public readonly float[] Spec = new float[1024];
    /// <summary>The coded spectrum in bitstream order: per group, per band, per window of the group. M/S bands are already mid/side.</summary>
    public readonly float[] Co = new float[1024];
    public readonly float[] Abs = new float[1024];
    public readonly float[] Abs34 = new float[1024];
    public readonly int[] Q = new int[1024];

    // Band thresholds from the model, per band (long) or window * 16 + band (short).
    public readonly float[] E = new float[8 * 16];
    public readonly float[] T = new float[8 * 16];
    public readonly float[] PrevLong = new float[64];
    public readonly float[] PrevShort = new float[16];

    // Entries: one per (group, band) in bitstream order.
    public readonly int[] Start = new int[MaxEntries];
    public readonly int[] Len = new int[MaxEntries];
    public readonly float[] EntE = new float[MaxEntries];
    public readonly float[] EntT = new float[MaxEntries];
    public readonly float[] Cap = new float[MaxEntries];
    public readonly float[] Max34 = new float[MaxEntries];
    public readonly int[] Sf = new int[MaxEntries];
    public readonly int[] Cb = new int[MaxEntries];
    public readonly bool[] Zero = new bool[MaxEntries];
    /// <summary>Bands coded as perceptual noise substitution (14496-3 4.5.2.3.4): no spectrum is sent, only an energy.</summary>
    public readonly bool[] Noise = new bool[MaxEntries];
    public readonly int[] NoiseNrg = new int[MaxEntries];
    public int Gain;
}

/// <summary>
/// The block encoder: window switching, MDCT, psychoacoustic thresholds, stereo decisions, a rate loop that raises the allowed
/// noise until the block fits its bit budget, scalefactor and codebook choice, and the raw_data_block syntax of ISO/IEC 14496-3
/// 4.4. Lowpass, threshold shaping and bit-reservoir policy are this encoder's own; the band layout, spreading and minimum-SNR
/// constants follow the 3GPP enhanced aacPlus encoder description.
/// </summary>
internal sealed partial class AacEncCore
{
    private const int M = 1024, S = 128;
    private const float PcmScale = 32768f;

    private readonly int _rate, _rateIndex, _ch;
    private readonly AacEncoderSettings _s;
    private readonly AacEncPsyConfig _long, _short;
    private readonly AacEncMdct _mdctLong = new(M), _mdctShort = new(S);
    private readonly float[][] _winLong = new float[4][]; // by window_sequence; index 2 unused
    private readonly float[] _winShort = new float[2 * S];
    private readonly float[][] _hist;
    private readonly float[] _tmp = new float[2 * M];
    private readonly AacEncChannel[] _c;
    private readonly bool[] _ms = new bool[AacEncChannel.MaxEntries];
    private readonly float[] _seg = new float[16];

    // Window sequence state.
    private bool _detectCur, _prevEffShort, _first = true;
    private int _seq;
    private int _numGroups, _active, _maxSfb;
    private readonly int[] _groupLen = new int[8];
    private bool _isShort;

    // Rate control.
    private readonly double _avgBits;
    private double _reservoir, _reservoirMax, _peMean;
    private readonly bool _vbr, _abr;
    private readonly int _maxFrameBits;

    public readonly int Bandwidth;

    public AacEncCore(int rate, int channels, AacEncoderSettings settings)
    {
        _rate = rate;
        _rateIndex = AacEncTables.RateIndex(rate);
        _ch = channels;
        _s = settings;
        AacEncTables.Bands(_rateIndex, out var lb, out var sb);
        int perCh = settings.Bitrate / channels;
        Bandwidth = settings.Bandwidth > 0 ? Math.Min(settings.Bandwidth, rate / 2) : AutoBandwidth(perCh, rate);
        _long = new AacEncPsyConfig(rate, lb, M, perCh, Bandwidth);
        _short = new AacEncPsyConfig(rate, sb, S, perCh, Bandwidth);
        var rise = AacWindows.Long[1];
        var srise = AacWindows.Short[1];
        for (int seq = 0; seq < 4; seq++)
        {
            if (seq == 2) continue;
            var w = new float[2 * M];
            for (int n = 0; n < 2 * M; n++)
            {
                float v;
                if (n < M)
                    v = seq == 3 ? (n < 448 ? 0f : n < 576 ? srise[n - 448] : 1f) : rise[n];
                else
                    v = seq == 1 ? (n < 1472 ? 1f : n < 1600 ? srise[127 - (n - 1472)] : 0f) : rise[2 * M - 1 - n];
                w[n] = v;
            }
            _winLong[seq] = w;
        }
        for (int n = 0; n < S; n++)
        {
            _winShort[n] = srise[n];
            _winShort[2 * S - 1 - n] = srise[n];
        }
        _hist = new float[channels][];
        _c = new AacEncChannel[channels];
        for (int i = 0; i < channels; i++)
        {
            _hist[i] = new float[3 * M];
            _c[i] = new AacEncChannel();
            Array.Copy(_long.Quiet, _c[i].PrevLong, _long.BandCount);
            Array.Copy(_short.Quiet, _c[i].PrevShort, _short.BandCount);
        }

        _vbr = settings.RateControl == AacRateControl.Vbr;
        _abr = settings.RateControl == AacRateControl.Abr;
        _avgBits = settings.Bitrate * (double)M / rate;
        _maxFrameBits = 6144 * channels - 64;
        _reservoirMax = settings.RateControl == AacRateControl.Abr
            ? 8 * _avgBits // not bounded by a decoder buffer: ABR files carry no fixed-rate promise (compare CBR below)
            : Math.Max(0, _maxFrameBits - _avgBits);
        _reservoir = 0;
        _peMean = -1;
    }

    /// <summary>Low-pass frequency for a bit rate per channel: the band that the rate can afford to code.</summary>
    internal static int AutoBandwidth(int bitsPerChannel, int rate)
    {
        ReadOnlySpan<int> rates = [12000, 16000, 20000, 24000, 32000, 40000, 48000, 64000, 96000, 128000];
        ReadOnlySpan<int> bw = [4500, 6500, 8500, 10500, 13500, 15500, 16500, 18000, 20000, 22000];
        int r = bitsPerChannel;
        int result;
        if (r <= rates[0]) result = bw[0];
        else if (r >= rates[^1]) result = bw[^1];
        else
        {
            int i = 1;
            while (r > rates[i]) i++;
            double t = (r - rates[i - 1]) / (double)(rates[i] - rates[i - 1]);
            result = (int)(bw[i - 1] + t * (bw[i] - bw[i - 1]));
        }
        return Math.Min(result, (int)(rate * 0.5));
    }

    /// <summary>Samples of the three hops this block needs: previous, current, next, each channel scaled to 16-bit units.</summary>
    public float[] History(int ch) => _hist[ch];

    /// <summary>Maximum bytes of one block (6144 bits per channel is the standard's input buffer limit).</summary>
    public int MaxBytes => 6144 * _ch / 8 + 32;
}
