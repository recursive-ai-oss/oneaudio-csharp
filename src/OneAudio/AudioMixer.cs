using OneAudio.Effects;

namespace OneAudio;

/// <summary>
/// Fixed configuration of an <see cref="AudioMixer"/>. Everything is preallocated from these numbers at creation.
/// <para>
/// <b>Advice for hosts that render in real time.</b> The render path never allocates, but the rest of the process can pause it
/// by triggering a garbage collection or by competing for the CPU. Create everything that costs memory up front, at startup or
/// on a loading screen: the mixer itself (all buses, effects, the limiter and the reserved converters, see
/// <see cref="PreallocatedConverters"/>), clips, and streaming sources. A converter that is not reserved is built inside
/// <see cref="AudioMixer.Play"/> and a polyphase one is about a third of a megabyte, so avoid first uses of a new converter kind on busy
/// frames. Avoid large allocations (anything over about 85 KB lands on the large object heap and can start a full collection)
/// on busy frames, and consider setting <c>GCSettings.LatencyMode = GCLatencyMode.SustainedLowLatency</c> (or using
/// <c>GC.TryStartNoGCRegion</c> around critical sections) so that blocking full collections are avoided while the game runs.
/// Run <see cref="AudioMixer.Render"/> on a dedicated audio thread, never on the game's frame thread.
/// </para>
/// </summary>
public sealed record MixerOptions
{
    /// <summary>Output sample rate in Hz, chosen by the host to match its device. Fixed for the mixer's lifetime. Default 48000.</summary>
    public int SampleRate { get; init; } = 48_000;

    /// <summary>Output channels per frame (1..32), chosen by the host. Fixed for the mixer's lifetime. Default 2.</summary>
    public int Channels { get; init; } = 2;

    /// <summary>
    /// Speaker layout of the output, used to map multichannel sources (see <see cref="AudioMixer"/>). When
    /// <see cref="ChannelMask.None"/> or when its bit count differs from <see cref="Channels"/>, the conventional layout for the
    /// channel count is used (mono, stereo, quad, 5.1, 7.1); other counts are mapped by channel index. Default <see cref="ChannelMask.None"/>.
    /// </summary>
    public ChannelMask OutputChannelMask { get; init; } = ChannelMask.None;

    /// <summary>Most simultaneously active voices. When all are busy a new voice steals the lowest-priority one or is dropped. Default 64.</summary>
    public int MaxVoices { get; init; } = 64;

    /// <summary>Number of buses (gain groups and sub-mixes), indexed from 0. Default 8.</summary>
    public int MaxBuses { get; init; } = 8;

    /// <summary>Most channels a source may have (1..32). Per-voice scratch is sized from it. Default 8, enough for 7.1.</summary>
    public int MaxSourceChannels { get; init; } = 8;

    /// <summary>Capacity of the control-to-render command queue. Commands issued while it is full are dropped and counted. Default 1024.</summary>
    public int CommandCapacity { get; init; } = 1024;

    /// <summary>Capacity of the render-to-control event queue. Events raised while it is full are dropped and counted. Default 256.</summary>
    public int EventCapacity { get; init; } = 256;

    /// <summary>
    /// Largest block the render path processes in one pass, in frames. A longer render call is processed in several
    /// passes (commands are applied between passes). Scratch memory is sized from it. Default 1024.
    /// </summary>
    public int MaxBlockFrames { get; init; } = 1024;

    /// <summary>
    /// Length of the linear ramp, in milliseconds, applied to pan, bus gain, bus mute and master gain changes that do not
    /// name their own ramp, so that a change is not heard as a click. 0 makes them jump at the next pass. Default 10.
    /// </summary>
    public double SmoothingMilliseconds { get; init; } = 10.0;

    /// <summary>
    /// Replaces the built-in converters (<see cref="AudioResampler"/>) with your own. Called on the control thread with the
    /// source's channel count and the quality; it must return a converter of exactly that channel count whose ratio range covers
    /// <see cref="ConverterMinRatio"/> to <see cref="ConverterMaxRatio"/>. Default null: use the library's resamplers.
    /// </summary>
    public Func<int, ResamplerQuality, IResampler>? ResamplerFactory { get; init; }

    /// <summary>
    /// Lowest conversion ratio (output rate / input rate, including pitch) a voice may reach. A voice needs a converter when its
    /// source's rate differs from <see cref="SampleRate"/> or it is pitched; its ratio is <c>SampleRate / (sourceRate * pitch)</c>.
    /// The polyphase filter widens as the ratio falls, so converter memory scales with <c>1 / min(1, ConverterMinRatio)</c>:
    /// a <see cref="ResamplerQuality.Polyphase"/> converter takes about 0.3 MB at 1.0, 0.6 MB at 0.5 (the default) and 1.2 MB at 0.25
    /// (<see cref="ResamplerQuality.PolyphaseFast"/> is smaller, <see cref="ResamplerQuality.Linear"/> negligible). Play rejects a
    /// voice whose starting ratio is outside this range, and <see cref="AudioMixer.SetPitch"/> clamps to it. Range 1/64 to
    /// <see cref="ConverterMaxRatio"/>. Default 0.5: a 96 kHz source at pitch 1, or a 48 kHz source up to an octave higher.
    /// </summary>
    public double ConverterMinRatio { get; init; } = 0.5;

    /// <summary>Highest conversion ratio a voice may reach (heavy slow-down of a low-rate source). Costs no memory. Range <see cref="ConverterMinRatio"/> to 64. Default 16.</summary>
    public double ConverterMaxRatio { get; init; } = 16.0;

    /// <summary>
    /// Converters built when the mixer is created, so the first voices that need one do not pay for it (the shared filter tables
    /// are built here, never on a render or play call). Beyond these, converters are created on demand inside <c>Play</c> on the
    /// control thread, up to <see cref="MaxConvertedVoices"/>, and recycled afterwards. Memory is as described at
    /// <see cref="ConverterMinRatio"/>, per converter. The default holds 4 mono and 4 stereo <see cref="ResamplerQuality.Linear"/>,
    /// 2 mono and 2 stereo <see cref="ResamplerQuality.PolyphaseFast"/> and 1 mono and 1 stereo <see cref="ResamplerQuality.Polyphase"/>
    /// converter, about 2.5 MB with the default ratio range, trimmed to <see cref="MaxConvertedVoices"/> (and to
    /// <see cref="MaxSourceChannels"/>). Null selects that default; an empty list reserves nothing; an explicit list must fit
    /// within <see cref="MaxConvertedVoices"/>.
    /// </summary>
    public IReadOnlyList<ConverterReservation>? PreallocatedConverters { get; init; }

    internal static readonly ConverterReservation[] DefaultReservations =
    [
        new(1, ResamplerQuality.Linear, 4), new(2, ResamplerQuality.Linear, 4),
        new(1, ResamplerQuality.PolyphaseFast, 2), new(2, ResamplerQuality.PolyphaseFast, 2),
        new(1, ResamplerQuality.Polyphase, 1), new(2, ResamplerQuality.Polyphase, 1),
    ];

    internal List<ConverterReservation> ResolveReservations()
    {
        int cap = MaxConvertedVoices < 0 ? MaxVoices : MaxConvertedVoices;
        var result = new List<ConverterReservation>();
        if (PreallocatedConverters != null)
        {
            long total = 0;
            foreach (var r in PreallocatedConverters)
            {
                if (r == null || r.Channels < 1 || r.Channels > MaxSourceChannels || r.Count < 0 || !Enum.IsDefined(r.Quality))
                    throw new ArgumentOutOfRangeException(nameof(PreallocatedConverters), "Invalid converter reservation.");
                total += r.Count;
                result.Add(r);
            }
            if (total > cap) throw new ArgumentOutOfRangeException(nameof(PreallocatedConverters), "More converters reserved than MaxConvertedVoices allows.");
            return result;
        }
        int left = cap;
        foreach (var r in DefaultReservations)
        {
            if (r.Channels > MaxSourceChannels || left <= 0) continue;
            int n = Math.Min(r.Count, left);
            left -= n;
            result.Add(r with { Count = n });
        }
        return result;
    }

    /// <summary>
    /// Most converters that may exist at once, which bounds converter memory. A <c>Play</c> that needs a converter when all are
    /// in use (idle ones of other kinds are dropped to make room) returns an invalid handle and counts in <see cref="AudioMixer.DroppedConverterRequests"/>. -1 means
    /// <see cref="MaxVoices"/> (no voice is ever refused). Default -1.
    /// </summary>
    public int MaxConvertedVoices { get; init; } = -1;

    /// <summary>
    /// Most expensive ratio changes the render thread performs per pass across all voices. Moving a polyphase voice's ratio by more
    /// than a few percent while it is below 1 makes the converter redesign its filter bank (about 0.3 ms, no allocation); a pitch bend
    /// on many voices could otherwise do dozens of those in one callback. Over-budget voices keep their previous ratio for that pass and
    /// are retried in turn on the next ones, so their pitch lags by at most a few percent. Linear converters are never limited.
    /// Default 2 (about 0.6 ms worst case per pass); <see cref="int.MaxValue"/> disables the limit.
    /// </summary>
    public int RatioRedesignsPerPass { get; init; } = 2;

    /// <summary>
    /// Reverbs that may be active at once across all buses and the master output (<see cref="AudioMixer.SetBusReverb"/>). Each is built
    /// when the mixer is created and takes about 0.3 MB for stereo at 48 kHz (eight delay lines of 0.13 MB in all, plus a pre-delay line per channel of 0.2 s rounded up to a
    /// power of two). 0 builds none. Range 0 to 16. Default 2.
    /// </summary>
    public int MaxReverbs { get; init; } = 2;

    /// <summary>
    /// Lookahead of the master limiter (<see cref="AudioMixer.SetLimiter"/>), in milliseconds, 0 to 100. A limiter needs to see a peak
    /// before it plays, so the whole output is delayed by this much (<see cref="AudioMixer.LatencyFrames"/>), whether or not the limiter is
    /// switched on; the latency is therefore fixed for the mixer's lifetime. 0 builds no limiter and adds no latency. A few milliseconds (5)
    /// is typical: shorter lookahead makes a harder, more audible attack. Default 0.
    /// </summary>
    public double LimiterLookaheadMilliseconds { get; init; }

    internal void Validate()
    {
        ArgumentOutOfRangeException.ThrowIfLessThan(SampleRate, 1);
        ArgumentOutOfRangeException.ThrowIfLessThan(Channels, 1);
        ArgumentOutOfRangeException.ThrowIfGreaterThan(Channels, 32);
        ArgumentOutOfRangeException.ThrowIfLessThan(MaxVoices, 1);
        ArgumentOutOfRangeException.ThrowIfGreaterThan(MaxVoices, 4096);
        ArgumentOutOfRangeException.ThrowIfLessThan(MaxBuses, 1);
        ArgumentOutOfRangeException.ThrowIfGreaterThan(MaxBuses, 256);
        ArgumentOutOfRangeException.ThrowIfLessThan(MaxSourceChannels, 1);
        ArgumentOutOfRangeException.ThrowIfGreaterThan(MaxSourceChannels, 32);
        ArgumentOutOfRangeException.ThrowIfLessThan(CommandCapacity, 16);
        ArgumentOutOfRangeException.ThrowIfLessThan(EventCapacity, 16);
        ArgumentOutOfRangeException.ThrowIfLessThan(MaxBlockFrames, 16);
        ArgumentOutOfRangeException.ThrowIfGreaterThan(MaxBlockFrames, 65536);
        if (!double.IsFinite(SmoothingMilliseconds) || SmoothingMilliseconds < 0 || SmoothingMilliseconds > 1000)
            throw new ArgumentOutOfRangeException(nameof(SmoothingMilliseconds));
        if (!(ConverterMinRatio >= 1.0 / 64 && ConverterMinRatio <= ConverterMaxRatio && ConverterMaxRatio <= 64))
            throw new ArgumentOutOfRangeException(nameof(ConverterMinRatio), "Converter ratios must satisfy 1/64 <= min <= max <= 64.");
        ArgumentOutOfRangeException.ThrowIfLessThan(MaxConvertedVoices, -1);
        ArgumentOutOfRangeException.ThrowIfNegative(RatioRedesignsPerPass);
        ArgumentOutOfRangeException.ThrowIfNegative(MaxReverbs);
        ArgumentOutOfRangeException.ThrowIfGreaterThan(MaxReverbs, 16);
        if (!double.IsFinite(LimiterLookaheadMilliseconds) || LimiterLookaheadMilliseconds < 0 || LimiterLookaheadMilliseconds > 100)
            throw new ArgumentOutOfRangeException(nameof(LimiterLookaheadMilliseconds));
        ResolveReservations();
    }
}

/// <summary>A number of converters of one kind to build when the mixer is created.</summary>
/// <param name="Channels">Channel count of the sources they will convert (1 to <see cref="MixerOptions.MaxSourceChannels"/>).</param>
/// <param name="Quality">Converter quality.</param>
/// <param name="Count">How many to build.</param>
public sealed record ConverterReservation(int Channels, ResamplerQuality Quality, int Count);

/// <summary>Identifies one playing instance of a sound. Handles are never reused; a stale handle is simply ignored by control calls.</summary>
/// <param name="Id">Unique id, positive for a real voice.</param>
public readonly record struct VoiceHandle(long Id)
{
    /// <summary>False for <c>default</c>, which <see cref="AudioMixer.Play"/> returns when it could not enqueue the request.</summary>
    public bool IsValid => Id > 0;
}

/// <summary>Everything that describes how a voice starts. Create with the source, set the rest with <c>init</c>.</summary>
public readonly record struct PlayParams
{
    /// <summary>Starts a description for <paramref name="source"/> with default settings.</summary>
    /// <param name="source">What to play.</param>
    public PlayParams(AudioSource source) => Source = source;

    /// <summary>
    /// What to play: up to <see cref="MixerOptions.MaxSourceChannels"/> channels. A rate other than the mixer's needs
    /// <see cref="MixerOptions.ResamplerFactory"/>.
    /// </summary>
    public AudioSource Source { get; init; }

    /// <summary>Linear gain, 1 = unity. Default 1.</summary>
    public float Gain { get; init; } = 1f;

    /// <summary>
    /// Position from -1 (left) to +1 (right). Mono sources use a constant-power law across the front pair; other sources
    /// are balanced. Default 0.
    /// </summary>
    public float Pan { get; init; }

    /// <summary>Repeat clips between <see cref="LoopStartFrame"/> and <see cref="LoopEndFrame"/>. Ignored by streaming sources (loop those at construction). Default false.</summary>
    public bool Loop { get; init; }

    /// <summary>First frame of the loop body, in source frames. Default 0.</summary>
    public long LoopStartFrame { get; init; }

    /// <summary>Frame after the last frame of the loop body, in source frames; -1 means the end of the clip. Default -1.</summary>
    public long LoopEndFrame { get; init; } = -1;

    /// <summary>Bus the voice feeds, 0..MaxBuses-1. Default 0.</summary>
    public int Bus { get; init; }

    /// <summary>Priority for voice stealing; higher survives. Default 0.</summary>
    public int Priority { get; init; }

    /// <summary>
    /// Mixer timeline frame (see <see cref="AudioMixer.TimelineFrames"/>) at which the first source frame is heard,
    /// exact to the frame. 0, or any time already past, starts at the beginning of the next render pass.
    /// </summary>
    public long StartAtFrame { get; init; }

    /// <summary>Length of a linear fade from silence to <see cref="Gain"/>, in frames. 0 = no fade. Default 0.</summary>
    public int FadeInFrames { get; init; }

    /// <summary>
    /// Playback speed multiplier, which also shifts pitch: 2 plays an octave up at twice the speed. Range
    /// <see cref="AudioMixer.MinPitch"/> to <see cref="AudioMixer.MaxPitch"/>, and the resulting conversion ratio must lie within
    /// <see cref="MixerOptions.ConverterMinRatio"/> to <see cref="MixerOptions.ConverterMaxRatio"/>. Default 1.
    /// </summary>
    public float Pitch { get; init; } = 1f;

    /// <summary>
    /// Reserve a converter even though the voice starts at pitch 1 and the source's rate matches, so that
    /// <see cref="AudioMixer.SetPitch"/> can change the pitch later. The converter then runs for the whole voice (a ratio of 1 is
    /// still filtered, with a 0.95 x Nyquist cutoff) so that the pitch can move without a start-up transient. Without it (and
    /// without a rate mismatch or <see cref="Pitch"/> other than 1) <see cref="AudioMixer.SetPitch"/> has no effect on the voice. Default false.
    /// </summary>
    public bool VariablePitch { get; init; }

    /// <summary>
    /// Quality of the converter used for rate conversion and pitch. Null (the default) chooses: <see cref="ResamplerQuality.PolyphaseFast"/>
    /// when only the sample rate differs, <see cref="ResamplerQuality.Linear"/> for pitched voices (<see cref="Pitch"/> other than 1 or
    /// <see cref="VariablePitch"/>), which is cheap and can bend freely. <see cref="ResamplerQuality.Polyphase"/> has the best rejection and
    /// the highest cost; polyphase voices that bend pitch below ratio 1 are subject to <see cref="MixerOptions.RatioRedesignsPerPass"/>.
    /// Converters add look-ahead latency that the converter compensates for; a voice's end is delivered through the converter's flush.
    /// </summary>
    public ResamplerQuality? Resampling { get; init; }
}

/// <summary>What a <see cref="MixerEvent"/> reports.</summary>
public enum MixerEventKind
{
    /// <summary>A voice finished: its source ended, or a stop (with fade) completed. <see cref="MixerEvent.Value"/> is 0.</summary>
    VoiceEnded = 0,
    /// <summary>A voice was cut off to make room for a higher or equal priority one. <see cref="MixerEvent.Value"/> is 0.</summary>
    VoiceStolen = 1,
    /// <summary>A play request was dropped because every voice outranked it. <see cref="MixerEvent.VoiceId"/> is the request's handle.</summary>
    PlayDropped = 2,
    /// <summary>A streaming voice ran out of decoded data; <see cref="MixerEvent.Value"/> is the frames of silence rendered in that pass. Raised once when an underrun starts, not on every pass of a long one.</summary>
    Underrun = 3,
}

/// <summary>A notification from the render thread, delivered by polling <see cref="AudioMixer.TryGetEvent"/>.</summary>
/// <param name="Kind">What happened.</param>
/// <param name="VoiceId">Id of the voice (<see cref="VoiceHandle.Id"/>).</param>
/// <param name="Value">Kind-specific detail.</param>
public readonly record struct MixerEvent(MixerEventKind Kind, long VoiceId, long Value);

/// <summary>
/// The real-time mixer. The host's audio thread calls <see cref="Render"/> to pull interleaved output; the game
/// thread starts, stops and adjusts voices through the control methods at any time.
/// <list type="bullet">
/// <item><description><b>Threads.</b> One render thread calls <see cref="Render"/> (it is not reentrant). Control
/// methods may be called from any threads; they serialize among themselves with a short lock that the render thread
/// never touches.</description></item>
/// <item><description><b>Commands.</b> Control calls write a command into a preallocated single-producer single-consumer
/// ring; <see cref="Render"/> applies pending commands at the start of each pass of at most
/// <see cref="MixerOptions.MaxBlockFrames"/> frames. Effects of a control call are therefore heard at the next pass,
/// except starts with <see cref="PlayParams.StartAtFrame"/>, which are frame-exact. If the ring is full the call
/// returns false (or an invalid handle) and <see cref="DroppedCommands"/> counts it.</description></item>
/// <item><description><b>Render path.</b> No allocation, locks, blocking, exceptions, I/O or callbacks. All voices,
/// buses and queues are created in the constructor. Notifications go the other way through a second ring, polled
/// with <see cref="TryGetEvent"/>.</description></item>
/// <item><description><b>Streaming.</b> A <see cref="StreamingSource"/> is decoded ahead by a thread of the host's
/// choosing (see <see cref="StreamingPump"/>); the render thread only reads its ring. An empty ring renders
/// silence and counts an underrun.</description></item>
/// <item><description><b>Sources and layouts.</b> A source has 1 to <see cref="MixerOptions.MaxSourceChannels"/> channels and any
/// sample rate. Its channels are mapped onto the output by speaker position (<see cref="AudioFormat.ChannelMask"/> and
/// <see cref="MixerOptions.OutputChannelMask"/>): a mono source is panned across the output's front pair with a constant-power law,
/// speakers the output has go straight through, missing speakers are folded into the nearest ones (centre to the front pair at -3 dB,
/// surrounds and sides to each other or to the front pair at -3 dB) and LFE is dropped when the output has none. Layouts without a
/// convention map by channel index. A source whose rate differs from the mixer's, or a pitched voice, is converted through an
/// <see cref="IResampler"/> from <see cref="MixerOptions.ResamplerFactory"/>.</description></item>
/// <item><description><b>Buses.</b> Every voice feeds one bus; each bus feeds the master output or another bus
/// (<see cref="SetBusOutput"/>), so sub-mixes sum into their parent. Loops in the routing are rejected. A bus can duck another
/// (<see cref="SetDucking"/>). Pan, bus gain and mute changes ramp over <see cref="MixerOptions.SmoothingMilliseconds"/>.</description></item>
/// <item><description><b>Effects.</b> Every bus, and the master output, has a low-pass and a high-pass filter
/// (<see cref="SetBusFilter"/>) and can host one of <see cref="MixerOptions.MaxReverbs"/> reverbs (<see cref="SetBusReverb"/>);
/// each voice has its own filter pair (<see cref="SetVoiceFilter"/>). On a bus the order is filters, reverb, then the bus gain, so a
/// muted bus is silent including its reverb tail, and ducking and the detector see the processed bus. Parameter changes glide.
/// A master limiter (<see cref="SetLimiter"/>) is available when the mixer is created with a lookahead.</description></item>
/// <item><description><b>Output.</b> The mix is summed, not clipped, unless the master limiter is on.</description></item>
/// </list>
/// </summary>
public sealed partial class AudioMixer
{
    /// <summary>Lowest accepted <see cref="PlayParams.Pitch"/> (four octaves down).</summary>
    public const float MinPitch = 1f / 16f;

    /// <summary>Highest accepted <see cref="PlayParams.Pitch"/> (four octaves up).</summary>
    public const float MaxPitch = 16f;

    private struct Command
    {
        public CommandKind Kind;
        public long Id;
        public AudioSource? Source;
        public IResampler? Resampler;
        public float Gain;
        public float Pan;
        public float Pitch;
        public bool Flag;
        public bool Loop;
        public long LoopStart;
        public long LoopEnd;
        public int Bus;
        public int Aux;
        public int Priority;
        public long StartAt;
        public int Frames;
        public float F0, F1, F2, F3;
        public ReverbSettings? Reverb;
        public LimiterSettings? Limiter;
    }

    private enum CommandKind : byte
    {
        Play, Stop, StopAll, SetGain, SetPan, SetLoop, SetBusGain, SetPitch, SetBusMute, SetBusRoute, SetDuck, SetMasterGain,
        SetBusFilter, SetVoiceFilter, SetBusReverb, SetLimiter,
    }

    private readonly int _sampleRate;
    private readonly int _channels;
    private readonly int _maxBlock;
    private readonly int _maxSrcChannels;
    private readonly int _smoothFrames;
    private readonly ChannelMask _outMask;
    private readonly Func<int, ResamplerQuality, IResampler> _resamplerFactory;
    private readonly double _minRatio, _maxRatio;
    private readonly int _maxConverters;
    private readonly int _redesignBudget;
    private int _converterCount;
    private long _droppedConverters;
    private readonly SpscQueue<Command> _commands;
    private readonly SpscQueue<MixerEvent> _events;
    private readonly SpscQueue<IResampler> _retired;
    private readonly object _controlLock = new();

    // Control-side mirror of the bus graph, guarded by _controlLock, for cycle rejection.
    private readonly int[] _ctlParent;
    private readonly int[] _ctlDuckSource;
    private readonly Dictionary<int, Stack<IResampler>> _pool = [];
    private readonly int[] _ctlReverbSlot;      // per bus (index bus + 1, so 0 is the master): the reverb slot it holds, or -1
    private readonly bool[] _ctlReverbBusy;
    private readonly ReverbProcessor[] _reverbs;
    private readonly LimiterProcessor? _limiter;
    private int _limiterGainBits = BitConverter.SingleToInt32Bits(1f);

    private long _nextId;
    private long _timeline;
    private int _activeVoices;
    private long _underrunEvents, _underrunFrames, _droppedCommands, _droppedPlays, _droppedEvents, _stolen;

    /// <summary>Creates a mixer and preallocates everything it will ever use.</summary>
    /// <param name="options">Rate, channels and caps; defaults are 48 kHz stereo.</param>
    /// <exception cref="ArgumentOutOfRangeException">An option is out of range.</exception>
    public AudioMixer(MixerOptions? options = null)
    {
        options ??= new MixerOptions();
        options.Validate();
        _sampleRate = options.SampleRate;
        _channels = options.Channels;
        _maxBlock = options.MaxBlockFrames;
        _maxSrcChannels = options.MaxSourceChannels;
        _smoothFrames = (int)Math.Round(options.SmoothingMilliseconds * 0.001 * options.SampleRate);
        _outMask = options.OutputChannelMask;
        _minRatio = options.ConverterMinRatio;
        _maxRatio = options.ConverterMaxRatio;
        _maxConverters = options.MaxConvertedVoices < 0 ? options.MaxVoices : options.MaxConvertedVoices;
        _redesignBudget = options.RatioRedesignsPerPass;
        var converterOptions = new ResamplerOptions { MinRatio = _minRatio, MaxRatio = _maxRatio };
        _resamplerFactory = options.ResamplerFactory ?? ((ch, q) => AudioResampler.Create(ch, q, 1.0, converterOptions));
        _commands = new SpscQueue<Command>(options.CommandCapacity);
        _events = new SpscQueue<MixerEvent>(options.EventCapacity);
        _retired = new SpscQueue<IResampler>(options.MaxVoices * 2 + 16);
        _ctlParent = new int[options.MaxBuses];
        _ctlDuckSource = new int[options.MaxBuses];
        Array.Fill(_ctlParent, -1);
        Array.Fill(_ctlDuckSource, -1);
        _voiceCount = options.MaxVoices;
        _voices = new Voice[_voiceCount];
        for (int i = 0; i < _voices.Length; i++) _voices[i] = new Voice(_channels, _maxSrcChannels, _sampleRate);
        _buses = new Bus[options.MaxBuses];
        for (int i = 0; i < _buses.Length; i++) _buses[i] = new Bus { Buf = new float[_channels * _maxBlock], Fx = new EffectChain(_channels, _sampleRate) };
        _master = new Bus { Buf = new float[_channels * _maxBlock], Fx = new EffectChain(_channels, _sampleRate) };
        _reverbs = new ReverbProcessor[options.MaxReverbs];
        for (int i = 0; i < _reverbs.Length; i++) _reverbs[i] = new ReverbProcessor(_channels, _sampleRate);
        _ctlReverbSlot = new int[options.MaxBuses + 1];
        Array.Fill(_ctlReverbSlot, -1);
        _ctlReverbBusy = new bool[options.MaxReverbs];
        if (options.LimiterLookaheadMilliseconds > 0)
        {
            int look = Math.Max(1, (int)Math.Round(options.LimiterLookaheadMilliseconds * 0.001 * _sampleRate));
            _limiter = new LimiterProcessor(look, _channels, _maxBlock);
            _limiter.Configure(false, 1f, 0.1f, _sampleRate);
        }
        _order = new int[_buses.Length];
        _indegree = new int[_buses.Length];
        _placed = new bool[_buses.Length];
        _srcPlanar = new float[_maxSrcChannels * _maxBlock];
        _inter = new float[_maxSrcChannels * _maxBlock];
        _inCapacity = ResampleChunk + CarryCapacity;
        _inScratch = new float[_inCapacity * _maxSrcChannels];
        _curve = new float[_maxBlock];
        _detectorRelease = (float)Math.Exp(-1.0 / (DetectorReleaseSeconds * _sampleRate));
        // Reserved converters are built now, which also builds the shared filter tables outside any render or play call.
        foreach (var r in options.ResolveReservations())
            for (int i = 0; i < r.Count; i++) PoolPush(CreateConverter(r.Channels, r.Quality));
    
    }

    /// <summary>Play requests refused because every converter allowed by <see cref="MixerOptions.MaxConvertedVoices"/> was in use. Safe to read from any thread.</summary>
    public long DroppedConverterRequests => Volatile.Read(ref _droppedConverters);

    /// <summary>
    /// Delay, in frames, between the mix and the output that the master limiter's lookahead adds (see
    /// <see cref="MixerOptions.LimiterLookaheadMilliseconds"/>); 0 when the mixer has no limiter. Constant for the mixer's lifetime.
    /// Voice start times (<see cref="PlayParams.StartAtFrame"/>) and <see cref="TimelineFrames"/> refer to the mix, so what is heard
    /// lags them by this many frames.
    /// </summary>
    public int LatencyFrames => _limiter?.LookaheadFrames ?? 0;

    /// <summary>True when the mixer was created with a limiter (<see cref="MixerOptions.LimiterLookaheadMilliseconds"/> above 0).</summary>
    public bool HasLimiter => _limiter != null;

    /// <summary>
    /// Gain reduction of the master limiter in the last render pass, in dB (0 or negative; 0 when the limiter is off or idle): the
    /// deepest point of the gain curve in that pass. Safe to read from any thread.
    /// </summary>
    public float LimiterGainReductionDb
    {
        get
        {
            float g = BitConverter.Int32BitsToSingle(Volatile.Read(ref _limiterGainBits));
            return g >= 1f ? 0f : 20f * MathF.Log10(Math.Max(g, 1e-6f));
        }
    }

    /// <summary>Output sample rate in Hz.</summary>
    public int SampleRate => _sampleRate;

    /// <summary>Output channels per frame.</summary>
    public int Channels => _channels;

    /// <summary>Number of buses, from <see cref="MixerOptions.MaxBuses"/>.</summary>
    public int BusCount => _ctlParent.Length;

    /// <summary>Frames rendered so far; the mixer's clock for <see cref="PlayParams.StartAtFrame"/>. Safe to read from any thread.</summary>
    public long TimelineFrames => Volatile.Read(ref _timeline);

    /// <summary>Voices that were active at the end of the last render pass. Safe to read from any thread.</summary>
    public int ActiveVoices => Volatile.Read(ref _activeVoices);

    /// <summary>Total streaming underruns across all voices (render passes that found a ring short). Safe to read from any thread.</summary>
    public long UnderrunEvents => Volatile.Read(ref _underrunEvents);

    /// <summary>Total frames of silence rendered because streaming data was missing. Safe to read from any thread.</summary>
    public long UnderrunFrames => Volatile.Read(ref _underrunFrames);

    /// <summary>Control calls dropped because the command queue was full. Safe to read from any thread.</summary>
    public long DroppedCommands => Volatile.Read(ref _droppedCommands);

    /// <summary>Play requests dropped at render time because every voice had higher priority. Safe to read from any thread.</summary>
    public long DroppedPlays => Volatile.Read(ref _droppedPlays);

    /// <summary>Events lost because the event queue was full. Safe to read from any thread.</summary>
    public long DroppedEvents => Volatile.Read(ref _droppedEvents);

    /// <summary>Voices cut off by voice-cap stealing. Safe to read from any thread.</summary>
    public long StolenVoices => Volatile.Read(ref _stolen);

    // ---------------------------------------------------------------- control side

    /// <summary>Starts a voice.</summary>
    /// <param name="parameters">What to play and how.</param>
    /// <returns>A handle, or <c>default</c> (invalid) if the command queue was full.</returns>
    /// <exception cref="ArgumentException">The source is missing or has too many channels.</exception>
    /// <exception cref="ArgumentOutOfRangeException">Bus, fade, gain or pitch is out of range, or the conversion ratio (from the
    /// source's rate and the pitch) is outside <see cref="MixerOptions.ConverterMinRatio"/> to <see cref="MixerOptions.ConverterMaxRatio"/>.</exception>
    public VoiceHandle Play(in PlayParams parameters)
    {
        Command cmd = BuildPlay(in parameters, out int channels, out ResamplerQuality quality, out double ratio);
        lock (_controlLock)
        {
            if (ratio > 0)
            {
                cmd.Resampler = AcquireResampler(channels, quality, ratio);
                if (cmd.Resampler == null) { Interlocked.Increment(ref _droppedConverters); return default; }
            }
            cmd.Id = ++_nextId;
            if (!_commands.TryEnqueue(cmd))
            {
                Interlocked.Increment(ref _droppedCommands);
                Release(cmd.Resampler);
                return default;
            }
            return new VoiceHandle(cmd.Id);
        }
    }

    /// <summary>
    /// Starts <paramref name="to"/> with a fade in of <paramref name="frames"/> and fades <paramref name="from"/> out over the
    /// same length, in the same render pass, for a click-free hand-over. Both succeed or neither is queued.
    /// </summary>
    /// <param name="from">The voice to fade out; a stale handle just means there is nothing to fade.</param>
    /// <param name="to">The new voice; its <see cref="PlayParams.FadeInFrames"/> is replaced by <paramref name="frames"/>.</param>
    /// <param name="frames">Fade length in frames, at least 1.</param>
    /// <returns>The new voice's handle, or <c>default</c> if the queue was full.</returns>
    public VoiceHandle Crossfade(VoiceHandle from, in PlayParams to, int frames)
    {
        ArgumentOutOfRangeException.ThrowIfLessThan(frames, 1);
        Command play = BuildPlay(to with { FadeInFrames = frames }, out int channels, out ResamplerQuality quality, out double ratio);
        lock (_controlLock)
        {
            if (_commands.FreeSlots < 2) { Interlocked.Increment(ref _droppedCommands); return default; }
            if (ratio > 0)
            {
                play.Resampler = AcquireResampler(channels, quality, ratio);
                if (play.Resampler == null) { Interlocked.Increment(ref _droppedConverters); return default; }
            }
            play.Id = ++_nextId;
            _commands.TryEnqueue(play);
            _commands.TryEnqueue(new Command { Kind = CommandKind.Stop, Id = from.Id, Frames = frames });
            return new VoiceHandle(play.Id);
        }
    }

    /// <summary>Stops a voice, optionally fading out. The voice then raises <see cref="MixerEventKind.VoiceEnded"/>.</summary>
    /// <param name="voice">The voice.</param>
    /// <param name="fadeOutFrames">Linear fade length in frames; 0 stops at the next pass.</param>
    /// <returns>False if the command queue was full.</returns>
    public bool Stop(VoiceHandle voice, int fadeOutFrames = 0)
    {
        ArgumentOutOfRangeException.ThrowIfNegative(fadeOutFrames);
        return Enqueue(new Command { Kind = CommandKind.Stop, Id = voice.Id, Frames = fadeOutFrames });
    }

    /// <summary>Stops every voice, optionally fading out.</summary>
    /// <param name="fadeOutFrames">Fade length in frames; 0 stops at the next pass.</param>
    /// <returns>False if the command queue was full.</returns>
    public bool StopAll(int fadeOutFrames = 0)
    {
        ArgumentOutOfRangeException.ThrowIfNegative(fadeOutFrames);
        return Enqueue(new Command { Kind = CommandKind.StopAll, Frames = fadeOutFrames });
    }

    /// <summary>Changes a voice's gain, optionally ramping linearly.</summary>
    /// <param name="voice">The voice.</param>
    /// <param name="gain">New linear gain (finite, not negative).</param>
    /// <param name="rampFrames">Ramp length in frames; 0 jumps at the next pass.</param>
    /// <returns>False if the command queue was full.</returns>
    public bool SetGain(VoiceHandle voice, float gain, int rampFrames = 0)
    {
        CheckGain(gain);
        ArgumentOutOfRangeException.ThrowIfNegative(rampFrames);
        return Enqueue(new Command { Kind = CommandKind.SetGain, Id = voice.Id, Gain = gain, Frames = rampFrames });
    }

    /// <summary>
    /// Changes a voice's pan, -1 (left) to +1 (right), ramping over <see cref="MixerOptions.SmoothingMilliseconds"/>
    /// so the move is not heard as a click.
    /// </summary>
    /// <param name="voice">The voice.</param>
    /// <param name="pan">New pan, clamped to [-1, 1].</param>
    /// <returns>False if the command queue was full.</returns>
    public bool SetPan(VoiceHandle voice, float pan) => SetPan(voice, pan, -1);

    /// <summary>Changes a voice's pan with an explicit ramp.</summary>
    /// <param name="voice">The voice.</param>
    /// <param name="pan">New pan, clamped to [-1, 1].</param>
    /// <param name="rampFrames">Ramp length in frames; 0 jumps at the next pass; negative uses <see cref="MixerOptions.SmoothingMilliseconds"/>.</param>
    /// <returns>False if the command queue was full.</returns>
    public bool SetPan(VoiceHandle voice, float pan, int rampFrames)
    {
        CheckFinite(pan, nameof(pan));
        return Enqueue(new Command { Kind = CommandKind.SetPan, Id = voice.Id, Pan = Math.Clamp(pan, -1f, 1f), Frames = rampFrames });
    }

    /// <summary>
    /// Changes a voice's playback speed and pitch. It only takes effect on a voice that reserved a converter at
    /// <see cref="Play"/> (a rate mismatch, a <see cref="PlayParams.Pitch"/> other than 1 or <see cref="PlayParams.VariablePitch"/>);
    /// otherwise it is ignored.
    /// </summary>
    /// <param name="voice">The voice.</param>
    /// <param name="pitch">Speed multiplier from <see cref="MinPitch"/> to <see cref="MaxPitch"/>; 1 is the source's own speed.</param>
    /// <param name="rampFrames">Glide length in frames, applied in steps at pass boundaries; 0 jumps at the next pass.</param>
    /// <returns>False if the command queue was full.</returns>
    /// <exception cref="ArgumentOutOfRangeException">The pitch or ramp is out of range.</exception>
    public bool SetPitch(VoiceHandle voice, float pitch, int rampFrames = 0)
    {
        CheckPitch(pitch);
        ArgumentOutOfRangeException.ThrowIfNegative(rampFrames);
        return Enqueue(new Command { Kind = CommandKind.SetPitch, Id = voice.Id, Pitch = pitch, Frames = rampFrames });
    }

    /// <summary>Changes a clip voice's loop settings.</summary>
    /// <param name="voice">The voice.</param>
    /// <param name="loop">Whether to loop.</param>
    /// <param name="startFrame">Loop body start in clip frames.</param>
    /// <param name="endFrame">Frame after the loop body, or -1 for the clip's end.</param>
    /// <returns>False if the command queue was full.</returns>
    public bool SetLoop(VoiceHandle voice, bool loop, long startFrame = 0, long endFrame = -1) =>
        Enqueue(new Command { Kind = CommandKind.SetLoop, Id = voice.Id, Loop = loop, LoopStart = startFrame, LoopEnd = endFrame });

    /// <summary>
    /// Sets a bus's gain; everything routed to it is multiplied by it. The change ramps over
    /// <see cref="MixerOptions.SmoothingMilliseconds"/>.
    /// </summary>
    /// <param name="bus">Bus index, 0..MaxBuses-1.</param>
    /// <param name="gain">Linear gain (finite, not negative).</param>
    /// <returns>False if the command queue was full.</returns>
    public bool SetBusGain(int bus, float gain) => SetBusGain(bus, gain, -1);

    /// <summary>Sets a bus's gain with an explicit ramp.</summary>
    /// <param name="bus">Bus index, 0..MaxBuses-1.</param>
    /// <param name="gain">Linear gain (finite, not negative).</param>
    /// <param name="rampFrames">Ramp length in frames; 0 jumps at the next pass; negative uses <see cref="MixerOptions.SmoothingMilliseconds"/>.</param>
    /// <returns>False if the command queue was full.</returns>
    public bool SetBusGain(int bus, float gain, int rampFrames)
    {
        CheckGain(gain);
        CheckBus(bus);
        return Enqueue(new Command { Kind = CommandKind.SetBusGain, Bus = bus, Gain = gain, Frames = rampFrames });
    }

    /// <summary>Mutes or unmutes a bus without disturbing its gain setting. The change ramps like <see cref="SetBusGain(int, float)"/>.</summary>
    /// <param name="bus">Bus index, 0..MaxBuses-1.</param>
    /// <param name="muted">True to silence the bus and everything routed through it.</param>
    /// <returns>False if the command queue was full.</returns>
    public bool SetBusMute(int bus, bool muted)
    {
        CheckBus(bus);
        return Enqueue(new Command { Kind = CommandKind.SetBusMute, Bus = bus, Flag = muted, Frames = -1 });
    }

    /// <summary>Sets the gain of the final output (after every bus), ramping over <see cref="MixerOptions.SmoothingMilliseconds"/>.</summary>
    /// <param name="gain">Linear gain (finite, not negative).</param>
    /// <returns>False if the command queue was full.</returns>
    public bool SetMasterGain(float gain)
    {
        CheckGain(gain);
        return Enqueue(new Command { Kind = CommandKind.SetMasterGain, Gain = gain, Frames = -1 });
    }

    /// <summary>
    /// Routes a bus into another bus, or to the master output. Every bus starts routed to the master output.
    /// </summary>
    /// <param name="bus">The bus whose output is being routed, 0..MaxBuses-1.</param>
    /// <param name="outputBus">The receiving bus, or -1 for the master output.</param>
    /// <returns>False if the command queue was full (the routing is then unchanged).</returns>
    /// <exception cref="ArgumentOutOfRangeException">A bus index is out of range.</exception>
    /// <exception cref="ArgumentException">The route would make a bus feed itself, directly or through other buses or ducking links.</exception>
    public bool SetBusOutput(int bus, int outputBus)
    {
        CheckBus(bus);
        if (outputBus != -1) CheckBus(outputBus, nameof(outputBus));
        lock (_controlLock)
        {
            int old = _ctlParent[bus];
            if (outputBus >= 0 && (outputBus == bus || Reaches(outputBus, bus)))
                throw new ArgumentException("The route would create a loop in the bus graph.", nameof(outputBus));
            _ctlParent[bus] = outputBus;
            if (_commands.TryEnqueue(new Command { Kind = CommandKind.SetBusRoute, Bus = bus, Aux = outputBus })) return true;
            _ctlParent[bus] = old;
        }
        Interlocked.Increment(ref _droppedCommands);
        return false;
    }

    /// <summary>
    /// Makes <paramref name="targetBus"/> duck: its gain is reduced while <paramref name="sourceBus"/> is loud, for example
    /// music under dialogue. One source per target; a later call replaces the earlier one. The source is processed before the target.
    /// </summary>
    /// <param name="targetBus">The bus that is turned down.</param>
    /// <param name="sourceBus">The bus whose level is watched.</param>
    /// <param name="settings">Threshold, depth and timing; null uses the defaults of <see cref="DuckingSettings"/>.
    /// <see cref="DuckingSettings.None"/> removes the link, exactly like <see cref="ClearDucking"/>.</param>
    /// <returns>False if the command queue was full (nothing changed).</returns>
    /// <exception cref="ArgumentOutOfRangeException">A bus index or a setting is out of range.</exception>
    /// <exception cref="ArgumentException">Both are the same bus, or the link would make the target feed the source, directly or indirectly.</exception>
    public bool SetDucking(int targetBus, int sourceBus, DuckingSettings? settings = null)
    {
        CheckBus(targetBus, nameof(targetBus));
        CheckBus(sourceBus, nameof(sourceBus));
        settings ??= new DuckingSettings();
        if (!settings.Enabled) return ClearDucking(targetBus);
        settings.Validate();
        double rate = _sampleRate;
        var cmd = new Command
        {
            Kind = CommandKind.SetDuck, Bus = targetBus, Aux = sourceBus,
            F0 = (float)Math.Pow(10.0, settings.ThresholdDb / 20.0),
            F1 = (float)Math.Pow(10.0, -settings.ReductionDb / 20.0),
            F2 = TimeConstant(settings.AttackMs, rate),
            F3 = TimeConstant(settings.ReleaseMs, rate),
        };
        lock (_controlLock)
        {
            int old = _ctlDuckSource[targetBus];
            if (sourceBus == targetBus || Reaches(targetBus, sourceBus))
                throw new ArgumentException("The ducking link would create a loop in the bus graph.", nameof(sourceBus));
            _ctlDuckSource[targetBus] = sourceBus;
            if (_commands.TryEnqueue(cmd)) return true;
            _ctlDuckSource[targetBus] = old;
        }
        Interlocked.Increment(ref _droppedCommands);
        return false;
    }

    /// <summary>Removes ducking from a bus. Its gain returns to unity with the release time it had.</summary>
    /// <param name="targetBus">The bus.</param>
    /// <returns>False if the command queue was full.</returns>
    public bool ClearDucking(int targetBus)
    {
        CheckBus(targetBus, nameof(targetBus));
        lock (_controlLock)
        {
            int old = _ctlDuckSource[targetBus];
            _ctlDuckSource[targetBus] = -1;
            if (_commands.TryEnqueue(new Command { Kind = CommandKind.SetDuck, Bus = targetBus, Aux = -1 })) return true;
            _ctlDuckSource[targetBus] = old;
        }
        Interlocked.Increment(ref _droppedCommands);
        return false;
    }

    // ---------------------------------------------------------------- effects

    /// <summary>The bus index that names the master output in the effect calls.</summary>
    public const int MasterBus = -1;

    /// <summary>Q of a filter that has none given: a Butterworth response (maximally flat, -3 dB at the cutoff), 1/sqrt(2).</summary>
    public const float DefaultFilterQ = 0.70710678f;

    /// <summary>
    /// Turns on, or retunes, a low-pass or high-pass filter (second order, 12 dB per octave) on a bus or on the master output. Each
    /// bus has one filter of each type, so a band-pass is a low-pass and a high-pass together. The filter is the pre-warped bilinear
    /// image of the analog prototype 1 / (s^2 + s/Q + 1), so the response follows the analog curve closely below a quarter of the sample rate.
    /// Cutoff and Q glide (the cutoff on a logarithmic scale) over <paramref name="rampFrames"/>, so sweeps make no zipper noise, and
    /// stay stable under any sequence of changes. A filter that was off glides in from the transparent setting unless <paramref name="rampFrames"/> is 0.
    /// </summary>
    /// <param name="bus">Bus index, 0..MaxBuses-1, or <see cref="MasterBus"/>.</param>
    /// <param name="type">Which filter.</param>
    /// <param name="cutoffHz">Cutoff in Hz (finite, above 0); clamped to 1 Hz up to 0.49 times the sample rate.</param>
    /// <param name="q">Resonance (finite, above 0); clamped to 0.1 to 24. 0.707 is flat, higher values peak at the cutoff. Default <see cref="DefaultFilterQ"/>.</param>
    /// <param name="rampFrames">Glide length in frames; 0 jumps at the next pass; negative uses <see cref="MixerOptions.SmoothingMilliseconds"/>.</param>
    /// <returns>False if the command queue was full.</returns>
    /// <exception cref="ArgumentOutOfRangeException">The bus, cutoff or Q is out of range.</exception>
    public bool SetBusFilter(int bus, FilterType type, float cutoffHz, float q = DefaultFilterQ, int rampFrames = -1)
    {
        CheckEffectBus(bus);
        CheckFilter(type, cutoffHz, q);
        return Enqueue(new Command { Kind = CommandKind.SetBusFilter, Bus = bus, Aux = (int)type, F0 = cutoffHz, F1 = q, Flag = true, Frames = rampFrames });
    }

    /// <summary>Turns a bus filter off. It glides to the transparent setting first (unless <paramref name="rampFrames"/> is 0), then stops costing anything.</summary>
    /// <param name="bus">Bus index, 0..MaxBuses-1, or <see cref="MasterBus"/>.</param>
    /// <param name="type">Which filter.</param>
    /// <param name="rampFrames">Glide length in frames; 0 switches off at the next pass; negative uses <see cref="MixerOptions.SmoothingMilliseconds"/>.</param>
    /// <returns>False if the command queue was full.</returns>
    public bool ClearBusFilter(int bus, FilterType type, int rampFrames = -1)
    {
        CheckEffectBus(bus);
        if (!Enum.IsDefined(type)) throw new ArgumentOutOfRangeException(nameof(type));
        return Enqueue(new Command { Kind = CommandKind.SetBusFilter, Bus = bus, Aux = (int)type, Flag = false, Frames = rampFrames });
    }

    /// <summary>
    /// Like <see cref="SetBusFilter"/> for one voice, applied to the voice's own channels before its gain and pan; the typical use is
    /// a muffling low-pass for a sound behind a wall. With <paramref name="rampFrames"/> 0 right after <see cref="Play"/> the voice starts filtered.
    /// A stale handle is ignored.
    /// </summary>
    /// <param name="voice">The voice.</param>
    /// <param name="type">Which filter.</param>
    /// <param name="cutoffHz">Cutoff in Hz (finite, above 0); clamped to 1 Hz up to 0.49 times the sample rate.</param>
    /// <param name="q">Resonance (finite, above 0); clamped to 0.1 to 24. Default <see cref="DefaultFilterQ"/>.</param>
    /// <param name="rampFrames">Glide length in frames; 0 jumps at the next pass; negative uses <see cref="MixerOptions.SmoothingMilliseconds"/>.</param>
    /// <returns>False if the command queue was full.</returns>
    /// <exception cref="ArgumentOutOfRangeException">The cutoff or Q is out of range.</exception>
    public bool SetVoiceFilter(VoiceHandle voice, FilterType type, float cutoffHz, float q = DefaultFilterQ, int rampFrames = -1)
    {
        CheckFilter(type, cutoffHz, q);
        return Enqueue(new Command { Kind = CommandKind.SetVoiceFilter, Id = voice.Id, Aux = (int)type, F0 = cutoffHz, F1 = q, Flag = true, Frames = rampFrames });
    }

    /// <summary>Turns a voice filter off, like <see cref="ClearBusFilter"/>.</summary>
    /// <param name="voice">The voice.</param>
    /// <param name="type">Which filter.</param>
    /// <param name="rampFrames">Glide length in frames; 0 switches off at the next pass; negative uses <see cref="MixerOptions.SmoothingMilliseconds"/>.</param>
    /// <returns>False if the command queue was full.</returns>
    public bool ClearVoiceFilter(VoiceHandle voice, FilterType type, int rampFrames = -1)
    {
        if (!Enum.IsDefined(type)) throw new ArgumentOutOfRangeException(nameof(type));
        return Enqueue(new Command { Kind = CommandKind.SetVoiceFilter, Id = voice.Id, Aux = (int)type, Flag = false, Frames = rampFrames });
    }

    /// <summary>
    /// Puts a reverb on a bus or on the master output, or retunes the one there. It is an insert: the bus's signal goes in and
    /// <c>Dry * signal + Wet * reverb</c> comes out, after the bus filters and before the bus gain. The reverb works on all of the mixer's
    /// output channels, with a different decorrelation for each. The tail keeps ringing after the bus's voices stop and the reverb
    /// goes idle (costing nothing) once it has decayed below about -120 dB. At most <see cref="MixerOptions.MaxReverbs"/> buses can
    /// have one at once. Changes glide over <paramref name="rampFrames"/>; a changing <see cref="ReverbSettings.RoomSize"/> bends the
    /// tail's pitch while it moves.
    /// </summary>
    /// <param name="bus">Bus index, 0..MaxBuses-1, or <see cref="MasterBus"/>.</param>
    /// <param name="settings">The reverb's parameters.</param>
    /// <param name="rampFrames">Glide length in frames for a change to a running reverb; 0 jumps at the next pass; negative uses <see cref="MixerOptions.SmoothingMilliseconds"/>. A reverb that is just being added starts with these settings at once.</param>
    /// <returns>False if the command queue was full, or if every reverb is in use by other buses (nothing changed).</returns>
    /// <exception cref="ArgumentOutOfRangeException">The bus or a setting is out of range.</exception>
    public bool SetBusReverb(int bus, ReverbSettings settings, int rampFrames = -1)
    {
        CheckEffectBus(bus);
        ArgumentNullException.ThrowIfNull(settings);
        settings.Validate();
        lock (_controlLock)
        {
            int slot = _ctlReverbSlot[bus + 1];
            bool added = slot < 0;
            if (added)
            {
                slot = Array.IndexOf(_ctlReverbBusy, false);
                if (slot < 0) return false;
            }
            if (_commands.TryEnqueue(new Command { Kind = CommandKind.SetBusReverb, Bus = bus, Aux = slot, Flag = true, Reverb = settings, Frames = rampFrames }))
            {
                _ctlReverbSlot[bus + 1] = slot;
                _ctlReverbBusy[slot] = true;
                return true;
            }
        }
        Interlocked.Increment(ref _droppedCommands);
        return false;
    }

    /// <summary>Removes the reverb from a bus. The tail stops at once; to fade it out, set <see cref="ReverbSettings.Wet"/> to 0 first and remove it later.</summary>
    /// <param name="bus">Bus index, 0..MaxBuses-1, or <see cref="MasterBus"/>.</param>
    /// <returns>False if the command queue was full.</returns>
    public bool ClearBusReverb(int bus)
    {
        CheckEffectBus(bus);
        lock (_controlLock)
        {
            int slot = _ctlReverbSlot[bus + 1];
            if (slot < 0) return true;
            if (_commands.TryEnqueue(new Command { Kind = CommandKind.SetBusReverb, Bus = bus, Aux = slot, Flag = false }))
            {
                _ctlReverbSlot[bus + 1] = -1;
                _ctlReverbBusy[slot] = false;
                return true;
            }
        }
        Interlocked.Increment(ref _droppedCommands);
        return false;
    }

    /// <summary>
    /// Switches the master limiter on, or retunes it, or (with null) off. The limiter is a lookahead brickwall: no output sample
    /// exceeds <see cref="LimiterSettings.CeilingDb"/>, it does not touch audio below that level other than delaying it by
    /// <see cref="LatencyFrames"/>, and it reduces the gain gradually over the lookahead before a peak arrives. It limits sample
    /// peaks, not inter-sample peaks. Switching it off lets the gain recover with the release time; the delay stays. A ceiling lowered
    /// while loud audio is already in the delay line is enforced by a final clamp for up to one lookahead.
    /// </summary>
    /// <param name="settings">Ceiling and release, or null to switch the limiter off.</param>
    /// <returns>False if the mixer has no limiter (<see cref="HasLimiter"/>) or the command queue was full.</returns>
    /// <exception cref="ArgumentOutOfRangeException">A setting is out of range.</exception>
    public bool SetLimiter(LimiterSettings? settings)
    {
        if (_limiter == null) return false;
        settings?.Validate();
        return Enqueue(new Command { Kind = CommandKind.SetLimiter, Limiter = settings, Flag = settings != null });
    }

    private void CheckEffectBus(int bus)
    {
        if (bus < -1 || bus >= _ctlParent.Length) throw new ArgumentOutOfRangeException(nameof(bus), "Bus out of range.");
    }

    private static void CheckFilter(FilterType type, float cutoffHz, float q)
    {
        if (!Enum.IsDefined(type)) throw new ArgumentOutOfRangeException(nameof(type));
        if (!float.IsFinite(cutoffHz) || cutoffHz <= 0f) throw new ArgumentOutOfRangeException(nameof(cutoffHz), "The cutoff must be finite and above 0.");
        if (!float.IsFinite(q) || q <= 0f) throw new ArgumentOutOfRangeException(nameof(q), "Q must be finite and above 0.");
    }

    /// <summary>Takes the next pending event, oldest first. Poll it from the game thread; nothing is called back on the render thread.</summary>
    /// <param name="mixerEvent">The event.</param>
    /// <returns>False when none are pending.</returns>
    public bool TryGetEvent(out MixerEvent mixerEvent)
    {
        lock (_controlLock) return _events.TryDequeue(out mixerEvent);
    }

    private static float TimeConstant(float ms, double rate) =>
        ms <= 0f ? 0f : (float)Math.Exp(-1.0 / (ms * 0.001 * rate));

    private bool Enqueue(in Command cmd)
    {
        lock (_controlLock)
        {
            if (_commands.TryEnqueue(cmd)) return true;
        }
        Interlocked.Increment(ref _droppedCommands);
        return false;
    }

    private static void CheckGain(float gain)
    {
        if (!float.IsFinite(gain) || gain < 0f) throw new ArgumentOutOfRangeException(nameof(gain), "Gain must be finite and not negative.");
    }

    private static void CheckFinite(float value, string name)
    {
        if (!float.IsFinite(value)) throw new ArgumentOutOfRangeException(name, "Value must be finite.");
    }

    private static void CheckPitch(float pitch)
    {
        if (!(pitch >= MinPitch && pitch <= MaxPitch)) throw new ArgumentOutOfRangeException(nameof(pitch), $"Pitch must be within {MinPitch} to {MaxPitch}.");
    }

    private void CheckBus(int bus, string name = "bus")
    {
        if ((uint)bus >= (uint)_ctlParent.Length) throw new ArgumentOutOfRangeException(name, "Bus out of range.");
    }

    // True when a signal path leads from 'from' to 'to' through routing and ducking links. Control thread, under the lock.
    private bool Reaches(int from, int to)
    {
        var seen = new bool[_ctlParent.Length];
        var stack = new Stack<int>();
        stack.Push(from);
        while (stack.Count > 0)
        {
            int x = stack.Pop();
            if (x == to) return true;
            if (seen[x]) continue;
            seen[x] = true;
            if (_ctlParent[x] >= 0) stack.Push(_ctlParent[x]);
            for (int t = 0; t < _ctlDuckSource.Length; t++)
                if (_ctlDuckSource[t] == x) stack.Push(t);
        }
        return false;
    }

    private Command BuildPlay(in PlayParams p, out int channels, out ResamplerQuality quality, out double ratio)
    {
        var src = p.Source ?? throw new ArgumentException("A source is required.", nameof(p));
        channels = src.Format.Channels;
        quality = ResamplerQuality.Linear;
        if (channels < 1 || channels > _maxSrcChannels) throw new ArgumentException($"Sources may have 1 to {_maxSrcChannels} channels.", nameof(p));
        if ((uint)p.Bus >= (uint)_ctlParent.Length) throw new ArgumentOutOfRangeException(nameof(p), "Bus out of range.");
        ArgumentOutOfRangeException.ThrowIfNegative(p.FadeInFrames);
        CheckGain(p.Gain);
        CheckFinite(p.Pan, nameof(p));
        CheckPitch(p.Pitch);
        bool needsConverter = src.Format.SampleRate != _sampleRate || p.Pitch != 1f || p.VariablePitch;
        ratio = 0;
        if (needsConverter)
        {
            ratio = _sampleRate / ((double)src.Format.SampleRate * p.Pitch);
            if (!(ratio >= _minRatio && ratio <= _maxRatio))
                throw new ArgumentOutOfRangeException(nameof(p), $"The conversion ratio {ratio:F4} (mixer rate / source rate / pitch) is outside the converter range {_minRatio} to {_maxRatio}; widen MixerOptions.ConverterMinRatio/ConverterMaxRatio or change the pitch.");
            bool pitched = p.Pitch != 1f || p.VariablePitch;
            quality = p.Resampling ?? (pitched ? ResamplerQuality.Linear : ResamplerQuality.PolyphaseFast);
            if (!Enum.IsDefined(quality)) throw new ArgumentOutOfRangeException(nameof(p), "Unknown resampler quality.");
        }
        return new Command
        {
            Kind = CommandKind.Play, Source = src, Gain = p.Gain, Pan = Math.Clamp(p.Pan, -1f, 1f), Loop = p.Loop,
            LoopStart = p.LoopStartFrame, LoopEnd = p.LoopEndFrame, Bus = p.Bus, Priority = p.Priority,
            StartAt = p.StartAtFrame, Frames = p.FadeInFrames, Pitch = p.Pitch, Flag = p.VariablePitch,
        };
    }

    // Converters are created on the control thread and recycled: the render thread hands retired ones back through a ring.
    // Returns null when every converter the mixer may own is in use.
    private IResampler? AcquireResampler(int channels, ResamplerQuality quality, double ratio)
    {
        while (_retired.TryDequeue(out var back)) PoolPush(back);
        int key = channels * 4 + (int)quality;
        IResampler rs;
        if (_pool.TryGetValue(key, out var stack) && stack.Count > 0)
        {
            rs = stack.Pop();
            rs.Reset();
        }
        else
        {
            if (_converterCount >= _maxConverters && !EvictIdleConverter()) return null;
            rs = CreateConverter(channels, quality);
        }
        try { rs.SetRatio(ratio); }
        catch (ArgumentOutOfRangeException) { PoolPush(rs); throw; }
        return rs;
    }

    // At the cap, an idle converter of another kind is dropped to make room, so reserved converters never lock the cap against
    // the kinds the game actually uses.
    private bool EvictIdleConverter()
    {
        foreach (var stack in _pool.Values)
        {
            if (stack.Count == 0) continue;
            stack.Pop();
            _converterCount--;
            return true;
        }
        return false;
    }

    private IResampler CreateConverter(int channels, ResamplerQuality quality)
    {
        var rs = _resamplerFactory(channels, quality)
            ?? throw new InvalidOperationException("The resampler factory returned null.");
        if (rs.Channels != channels)
            throw new InvalidOperationException($"The resampler factory returned a {rs.Channels}-channel converter for {channels} channels.");
        _converterCount++;
        return rs;
    }

    private void Release(IResampler? rs)
    {
        if (rs != null) PoolPush(rs);
    }

    private void PoolPush(IResampler rs)
    {
        int key = rs.Channels * 4 + (int)rs.Quality;
        if (!_pool.TryGetValue(key, out var stack)) _pool[key] = stack = new Stack<IResampler>();
        stack.Push(rs);
    }
}
