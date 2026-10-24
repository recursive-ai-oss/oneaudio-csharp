namespace OneAudio;

/// <summary>Fixed configuration of an <see cref="AudioMixer"/>. Everything is preallocated from these numbers at creation.</summary>
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
    /// Creates the sample-rate converters voices use when a source's rate differs from <see cref="SampleRate"/> or a voice
    /// is pitched. Called on the control thread (inside <see cref="AudioMixer.Play"/>), never on the render thread, with the
    /// source's channel count and the voice's requested quality; it must return a converter of exactly that channel count.
    /// Converters are recycled between voices. When null, only sources at the mixer's rate and unpitched voices can play.
    /// Default null.
    /// </summary>
    public Func<int, ResamplerQuality, IResampler>? ResamplerFactory { get; init; }

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
    }
}

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
    /// <see cref="AudioMixer.MinPitch"/> to <see cref="AudioMixer.MaxPitch"/>. A value other than 1 needs
    /// <see cref="MixerOptions.ResamplerFactory"/>. Default 1.
    /// </summary>
    public float Pitch { get; init; } = 1f;

    /// <summary>
    /// Reserve a converter even though the voice starts at pitch 1 and the source's rate matches, so that
    /// <see cref="AudioMixer.SetPitch"/> can change the pitch later. Without it (and without a rate mismatch or
    /// <see cref="Pitch"/> other than 1) <see cref="AudioMixer.SetPitch"/> has no effect on the voice. Default false.
    /// </summary>
    public bool VariablePitch { get; init; }

    /// <summary>
    /// Quality of the converter used for rate conversion and pitch. <see cref="ResamplerQuality.Linear"/> is cheap and
    /// suits effects; <see cref="ResamplerQuality.Polyphase"/> is band-limited and suits music and voice-over, at a latency of
    /// its filter delay. Default <see cref="ResamplerQuality.Linear"/>.
    /// </summary>
    public ResamplerQuality Resampling { get; init; }
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
/// <item><description><b>Output.</b> The mix is summed, not clipped; a limiter effect is separate.</description></item>
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
    }

    private enum CommandKind : byte
    {
        Play, Stop, StopAll, SetGain, SetPan, SetLoop, SetBusGain, SetPitch, SetBusMute, SetBusRoute, SetDuck, SetMasterGain,
    }

    private readonly int _sampleRate;
    private readonly int _channels;
    private readonly int _maxBlock;
    private readonly int _maxSrcChannels;
    private readonly int _smoothFrames;
    private readonly ChannelMask _outMask;
    private readonly Func<int, ResamplerQuality, IResampler>? _resamplerFactory;
    private readonly SpscQueue<Command> _commands;
    private readonly SpscQueue<MixerEvent> _events;
    private readonly SpscQueue<IResampler> _retired;
    private readonly object _controlLock = new();

    // Control-side mirror of the bus graph, guarded by _controlLock, for cycle rejection.
    private readonly int[] _ctlParent;
    private readonly int[] _ctlDuckSource;
    private readonly Dictionary<int, Stack<IResampler>> _pool = [];

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
        _resamplerFactory = options.ResamplerFactory;
        _commands = new SpscQueue<Command>(options.CommandCapacity);
        _events = new SpscQueue<MixerEvent>(options.EventCapacity);
        _retired = new SpscQueue<IResampler>(options.MaxVoices * 2 + 16);
        _ctlParent = new int[options.MaxBuses];
        _ctlDuckSource = new int[options.MaxBuses];
        Array.Fill(_ctlParent, -1);
        Array.Fill(_ctlDuckSource, -1);
        _voiceCount = options.MaxVoices;
        _voices = new Voice[_voiceCount];
        for (int i = 0; i < _voices.Length; i++) _voices[i] = new Voice(_channels, _maxSrcChannels);
        _buses = new Bus[options.MaxBuses];
        for (int i = 0; i < _buses.Length; i++) _buses[i] = new Bus { Buf = new float[_channels * _maxBlock] };
        _master = new Bus { Buf = new float[_channels * _maxBlock] };
        _order = new int[_buses.Length];
        _indegree = new int[_buses.Length];
        _placed = new bool[_buses.Length];
        _srcPlanar = new float[_maxSrcChannels * _maxBlock];
        _inter = new float[_maxSrcChannels * _maxBlock];
        _inCapacity = _maxBlock * 8 + ResampleMargin;
        _inScratch = new float[_inCapacity * _maxSrcChannels];
        _curve = new float[_maxBlock];
        _detectorRelease = (float)Math.Exp(-1.0 / (DetectorReleaseSeconds * _sampleRate));
    
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
    /// <exception cref="ArgumentException">The source is missing or has too many channels, or needs a converter and no
    /// <see cref="MixerOptions.ResamplerFactory"/> is configured.</exception>
    /// <exception cref="ArgumentOutOfRangeException">Bus, fade, gain or pitch is out of range.</exception>
    public VoiceHandle Play(in PlayParams parameters)
    {
        Command cmd = BuildPlay(in parameters, out int channels, out ResamplerQuality quality, out double ratio);
        lock (_controlLock)
        {
            if (ratio > 0) cmd.Resampler = AcquireResampler(channels, quality, ratio);
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
            if (ratio > 0) play.Resampler = AcquireResampler(channels, quality, ratio);
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
    /// <param name="settings">Threshold, depth and timing; null uses the defaults of <see cref="DuckingSettings"/>.</param>
    /// <returns>False if the command queue was full (nothing changed).</returns>
    /// <exception cref="ArgumentOutOfRangeException">A bus index or a setting is out of range.</exception>
    /// <exception cref="ArgumentException">Both are the same bus, or the link would make the target feed the source, directly or indirectly.</exception>
    public bool SetDucking(int targetBus, int sourceBus, DuckingSettings? settings = null)
    {
        CheckBus(targetBus, nameof(targetBus));
        CheckBus(sourceBus, nameof(sourceBus));
        settings ??= new DuckingSettings();
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
        quality = p.Resampling;
        if (channels < 1 || channels > _maxSrcChannels) throw new ArgumentException($"Sources may have 1 to {_maxSrcChannels} channels.", nameof(p));
        if ((uint)p.Bus >= (uint)_ctlParent.Length) throw new ArgumentOutOfRangeException(nameof(p), "Bus out of range.");
        if (!Enum.IsDefined(p.Resampling)) throw new ArgumentOutOfRangeException(nameof(p), "Unknown resampler quality.");
        ArgumentOutOfRangeException.ThrowIfNegative(p.FadeInFrames);
        CheckGain(p.Gain);
        CheckFinite(p.Pan, nameof(p));
        CheckPitch(p.Pitch);
        bool needsConverter = src.Format.SampleRate != _sampleRate || p.Pitch != 1f || p.VariablePitch;
        ratio = 0;
        if (needsConverter)
        {
            if (_resamplerFactory == null)
                throw new ArgumentException($"Source rate {src.Format.SampleRate} Hz or the pitch needs a converter, but the mixer runs at {_sampleRate} Hz with no ResamplerFactory configured.", nameof(p));
            ratio = _sampleRate / ((double)src.Format.SampleRate * p.Pitch);
        }
        return new Command
        {
            Kind = CommandKind.Play, Source = src, Gain = p.Gain, Pan = Math.Clamp(p.Pan, -1f, 1f), Loop = p.Loop,
            LoopStart = p.LoopStartFrame, LoopEnd = p.LoopEndFrame, Bus = p.Bus, Priority = p.Priority,
            StartAt = p.StartAtFrame, Frames = p.FadeInFrames, Pitch = p.Pitch,
        };
    }

    // Converters are created on the control thread and recycled: the render thread hands retired ones back through a ring.
    private IResampler AcquireResampler(int channels, ResamplerQuality quality, double ratio)
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
            rs = _resamplerFactory!(channels, quality)
                ?? throw new InvalidOperationException("The resampler factory returned null.");
            if (rs.Channels != channels)
                throw new InvalidOperationException($"The resampler factory returned a {rs.Channels}-channel converter for {channels} channels.");
        }
        try { rs.SetRatio(ratio); }
        catch (ArgumentOutOfRangeException) { PoolPush(rs); throw; }
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
        if (stack.Count < 2 * _voiceCount) stack.Push(rs);
    }
}
