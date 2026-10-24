namespace OneAudio;

/// <summary>Fixed configuration of an <see cref="AudioMixer"/>. Everything is preallocated from these numbers at creation.</summary>
public sealed record MixerOptions
{
    /// <summary>Output sample rate in Hz, chosen by the host to match its device. Fixed for the mixer's lifetime. Default 48000.</summary>
    public int SampleRate { get; init; } = 48_000;

    /// <summary>Output channels per frame (1..32), chosen by the host. Fixed for the mixer's lifetime. Default 2.</summary>
    public int Channels { get; init; } = 2;

    /// <summary>Most simultaneously active voices. When all are busy a new voice steals the lowest-priority one or is dropped. Default 64.</summary>
    public int MaxVoices { get; init; } = 64;

    /// <summary>Number of buses (gain groups), indexed from 0. Default 8.</summary>
    public int MaxBuses { get; init; } = 8;

    /// <summary>Capacity of the control-to-render command queue. Commands issued while it is full are dropped and counted. Default 1024.</summary>
    public int CommandCapacity { get; init; } = 1024;

    /// <summary>Capacity of the render-to-control event queue. Events raised while it is full are dropped and counted. Default 256.</summary>
    public int EventCapacity { get; init; } = 256;

    /// <summary>
    /// Largest block the render path processes in one pass, in frames. A longer render call is processed in several
    /// passes (commands are applied between passes). Scratch memory is sized from it. Default 1024.
    /// </summary>
    public int MaxBlockFrames { get; init; } = 1024;

    internal void Validate()
    {
        ArgumentOutOfRangeException.ThrowIfLessThan(SampleRate, 1);
        ArgumentOutOfRangeException.ThrowIfLessThan(Channels, 1);
        ArgumentOutOfRangeException.ThrowIfGreaterThan(Channels, 32);
        ArgumentOutOfRangeException.ThrowIfLessThan(MaxVoices, 1);
        ArgumentOutOfRangeException.ThrowIfGreaterThan(MaxVoices, 4096);
        ArgumentOutOfRangeException.ThrowIfLessThan(MaxBuses, 1);
        ArgumentOutOfRangeException.ThrowIfGreaterThan(MaxBuses, 256);
        ArgumentOutOfRangeException.ThrowIfLessThan(CommandCapacity, 16);
        ArgumentOutOfRangeException.ThrowIfLessThan(EventCapacity, 16);
        ArgumentOutOfRangeException.ThrowIfLessThan(MaxBlockFrames, 16);
        ArgumentOutOfRangeException.ThrowIfGreaterThan(MaxBlockFrames, 65536);
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

    /// <summary>What to play. It must have 1 or 2 channels and the mixer's sample rate.</summary>
    public AudioSource Source { get; init; }

    /// <summary>Linear gain, 1 = unity. Default 1.</summary>
    public float Gain { get; init; } = 1f;

    /// <summary>Position from -1 (left) to +1 (right). Mono sources use a constant-power law; stereo sources are balanced. Default 0.</summary>
    public float Pan { get; init; }

    /// <summary>Repeat clips between <see cref="LoopStartFrame"/> and <see cref="LoopEndFrame"/>. Ignored by streaming sources (loop those at construction). Default false.</summary>
    public bool Loop { get; init; }

    /// <summary>First frame of the loop body. Default 0.</summary>
    public long LoopStartFrame { get; init; }

    /// <summary>Frame after the last frame of the loop body; -1 means the end of the clip. Default -1.</summary>
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
/// choosing (calling <see cref="StreamingSource.Pump"/>); the render thread only reads its ring. An empty ring renders
/// silence and counts an underrun.</description></item>
/// <item><description><b>Output.</b> The mix is summed, not clipped; a limiter effect is separate. Sources must already be at the
/// mixer's sample rate (per-voice resampling is not applied yet). Sources with more than two channels are not accepted.</description></item>
/// </list>
/// </summary>
public sealed class AudioMixer
{
    private struct Command
    {
        public CommandKind Kind;
        public long Id;
        public AudioSource? Source;
        public float Gain;
        public float Pan;
        public bool Loop;
        public long LoopStart;
        public long LoopEnd;
        public int Bus;
        public int Priority;
        public long StartAt;
        public int Frames;
    }

    private enum CommandKind : byte { Play, Stop, StopAll, SetGain, SetPan, SetLoop, SetBusGain }

    private sealed class Voice
    {
        public bool Active;
        public long Id;
        public long Seq;
        public AudioSource? Source;
        public float Gain, GainTarget, GainStep;
        public int RampLeft;
        public bool StopAfterRamp;
        public float Pan;
        public bool Loop;
        public long LoopStart, LoopEnd;
        public int Bus, Priority;
        public long StartAt;
        public long Position;
        public bool InUnderrun;
    }

    private readonly int _sampleRate;
    private readonly int _channels;
    private readonly int _maxBlock;
    private readonly SpscQueue<Command> _commands;
    private readonly SpscQueue<MixerEvent> _events;
    private readonly object _controlLock = new();
    private readonly Voice[] _voices;
    private readonly float[] _busGain;
    private readonly float[] _scratch;

    private long _nextId;
    private long _seq;
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
        _commands = new SpscQueue<Command>(options.CommandCapacity);
        _events = new SpscQueue<MixerEvent>(options.EventCapacity);
        _voices = new Voice[options.MaxVoices];
        for (int i = 0; i < _voices.Length; i++) _voices[i] = new Voice();
        _busGain = new float[options.MaxBuses];
        Array.Fill(_busGain, 1f);
        _scratch = new float[_maxBlock * 2];
    }

    /// <summary>Output sample rate in Hz.</summary>
    public int SampleRate => _sampleRate;

    /// <summary>Output channels per frame.</summary>
    public int Channels => _channels;

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
    /// <exception cref="ArgumentException">The source is missing, has more than two channels, or runs at a different sample rate.</exception>
    /// <exception cref="ArgumentOutOfRangeException">Bus or fade is out of range.</exception>
    public VoiceHandle Play(in PlayParams parameters)
    {
        Command cmd = BuildPlay(in parameters);
        lock (_controlLock)
        {
            cmd.Id = ++_nextId;
            if (!_commands.TryEnqueue(cmd)) { Interlocked.Increment(ref _droppedCommands); return default; }
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
        Command play = BuildPlay(to with { FadeInFrames = frames });
        lock (_controlLock)
        {
            if (_commands.FreeSlots < 2) { Interlocked.Increment(ref _droppedCommands); return default; }
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
    /// <param name="gain">New linear gain (not negative).</param>
    /// <param name="rampFrames">Ramp length in frames; 0 jumps at the next pass.</param>
    /// <returns>False if the command queue was full.</returns>
    public bool SetGain(VoiceHandle voice, float gain, int rampFrames = 0)
    {
        ArgumentOutOfRangeException.ThrowIfNegative(gain);
        ArgumentOutOfRangeException.ThrowIfNegative(rampFrames);
        return Enqueue(new Command { Kind = CommandKind.SetGain, Id = voice.Id, Gain = gain, Frames = rampFrames });
    }

    /// <summary>Changes a voice's pan, -1 (left) to +1 (right). Applied per pass without smoothing.</summary>
    /// <param name="voice">The voice.</param>
    /// <param name="pan">New pan, clamped to [-1, 1].</param>
    /// <returns>False if the command queue was full.</returns>
    public bool SetPan(VoiceHandle voice, float pan) =>
        Enqueue(new Command { Kind = CommandKind.SetPan, Id = voice.Id, Pan = Math.Clamp(pan, -1f, 1f) });

    /// <summary>Changes a clip voice's loop settings.</summary>
    /// <param name="voice">The voice.</param>
    /// <param name="loop">Whether to loop.</param>
    /// <param name="startFrame">Loop body start in clip frames.</param>
    /// <param name="endFrame">Frame after the loop body, or -1 for the clip's end.</param>
    /// <returns>False if the command queue was full.</returns>
    public bool SetLoop(VoiceHandle voice, bool loop, long startFrame = 0, long endFrame = -1) =>
        Enqueue(new Command { Kind = CommandKind.SetLoop, Id = voice.Id, Loop = loop, LoopStart = startFrame, LoopEnd = endFrame });

    /// <summary>Sets a bus's gain; every voice routed to it is multiplied by it. Applied per pass without smoothing.</summary>
    /// <param name="bus">Bus index, 0..MaxBuses-1.</param>
    /// <param name="gain">Linear gain (not negative).</param>
    /// <returns>False if the command queue was full.</returns>
    public bool SetBusGain(int bus, float gain)
    {
        ArgumentOutOfRangeException.ThrowIfNegative(gain);
        if ((uint)bus >= (uint)_busGain.Length) throw new ArgumentOutOfRangeException(nameof(bus));
        return Enqueue(new Command { Kind = CommandKind.SetBusGain, Bus = bus, Gain = gain });
    }

    /// <summary>Takes the next pending event, oldest first. Poll it from the game thread; nothing is called back on the render thread.</summary>
    /// <param name="mixerEvent">The event.</param>
    /// <returns>False when none are pending.</returns>
    public bool TryGetEvent(out MixerEvent mixerEvent)
    {
        lock (_controlLock) return _events.TryDequeue(out mixerEvent);
    }

    private bool Enqueue(in Command cmd)
    {
        lock (_controlLock)
        {
            if (_commands.TryEnqueue(cmd)) return true;
        }
        Interlocked.Increment(ref _droppedCommands);
        return false;
    }

    private Command BuildPlay(in PlayParams p)
    {
        var src = p.Source ?? throw new ArgumentException("A source is required.", nameof(p));
        if (src.Format.Channels is < 1 or > 2) throw new ArgumentException("Voices play mono or stereo sources.", nameof(p));
        if (src.Format.SampleRate != _sampleRate) throw new ArgumentException($"Source rate {src.Format.SampleRate} Hz differs from the mixer's {_sampleRate} Hz; resample it first.", nameof(p));
        if ((uint)p.Bus >= (uint)_busGain.Length) throw new ArgumentOutOfRangeException(nameof(p), "Bus out of range.");
        ArgumentOutOfRangeException.ThrowIfNegative(p.FadeInFrames);
        ArgumentOutOfRangeException.ThrowIfNegative(p.Gain);
        return new Command
        {
            Kind = CommandKind.Play, Source = src, Gain = p.Gain, Pan = Math.Clamp(p.Pan, -1f, 1f), Loop = p.Loop,
            LoopStart = p.LoopStartFrame, LoopEnd = p.LoopEndFrame, Bus = p.Bus, Priority = p.Priority,
            StartAt = p.StartAtFrame, Frames = p.FadeInFrames,
        };
    }

    // ---------------------------------------------------------------- render side

    /// <summary>
    /// Fills <paramref name="output"/> with the next block of the mix. The frame count is implied by the span:
    /// <c>output.Length / Channels</c> whole frames; a trailing partial frame is zeroed. The span is overwritten, not
    /// added to. Call only from the host's audio thread, never concurrently with itself. Allocates nothing, takes
    /// no locks and does not block; it throws nothing for any control input.
    /// </summary>
    /// <param name="output">Interleaved output at <see cref="SampleRate"/> and <see cref="Channels"/>.</param>
    public void Render(Span<float> output)
    {
        output.Clear();
        int ch = _channels;
        int total = output.Length / ch;
        int done = 0;
        while (done < total)
        {
            int n = Math.Min(total - done, _maxBlock);
            var block = output.Slice(done * ch, n * ch);
            long blockStart = _timeline;
            DrainCommands();
            int active = 0;
            for (int i = 0; i < _voices.Length; i++)
            {
                var v = _voices[i];
                if (!v.Active) continue;
                MixVoice(v, block, n, blockStart);
                if (v.Active) active++;
            }
            Volatile.Write(ref _activeVoices, active);
            Volatile.Write(ref _timeline, blockStart + n);
            done += n;
        }
    }

    private void DrainCommands()
    {
        while (_commands.TryDequeue(out var c))
        {
            switch (c.Kind)
            {
                case CommandKind.Play: StartVoice(in c); break;
                case CommandKind.Stop: { var v = Find(c.Id); if (v != null) BeginStop(v, c.Frames); break; }
                case CommandKind.StopAll:
                    foreach (var v in _voices) if (v.Active) BeginStop(v, c.Frames);
                    break;
                case CommandKind.SetGain: { var v = Find(c.Id); if (v != null) Ramp(v, c.Gain, c.Frames); break; }
                case CommandKind.SetPan: { var v = Find(c.Id); if (v != null) v.Pan = c.Pan; break; }
                case CommandKind.SetLoop: { var v = Find(c.Id); if (v != null) { v.Loop = c.Loop; v.LoopStart = c.LoopStart; v.LoopEnd = c.LoopEnd; } break; }
                case CommandKind.SetBusGain: _busGain[c.Bus] = c.Gain; break;
            }
        }
    }

    private Voice? Find(long id)
    {
        foreach (var v in _voices) if (v.Active && v.Id == id) return v;
        return null;
    }

    private static void Ramp(Voice v, float target, int frames)
    {
        if (frames <= 0) { v.Gain = v.GainTarget = target; v.RampLeft = 0; v.GainStep = 0; return; }
        v.GainTarget = target;
        v.RampLeft = frames;
        v.GainStep = (target - v.Gain) / frames;
    }

    private void BeginStop(Voice v, int fadeFrames)
    {
        if (fadeFrames <= 0) { EndVoice(v); return; }
        Ramp(v, 0f, fadeFrames);
        v.StopAfterRamp = true;
    }

    private void StartVoice(in Command c)
    {
        Voice? slot = null;
        Voice? weakest = null;
        foreach (var v in _voices)
        {
            if (!v.Active) { slot = v; break; }
            if (weakest == null || v.Priority < weakest.Priority || (v.Priority == weakest.Priority && v.Seq < weakest.Seq)) weakest = v;
        }
        if (slot == null)
        {
            if (weakest!.Priority > c.Priority)
            {
                Interlocked.Increment(ref _droppedPlays);
                Post(new MixerEvent(MixerEventKind.PlayDropped, c.Id, 0));
                return;
            }
            Interlocked.Increment(ref _stolen);
            Post(new MixerEvent(MixerEventKind.VoiceStolen, weakest.Id, 0));
            weakest.Active = false;
            weakest.Source = null;
            slot = weakest;
        }
        slot.Active = true;
        slot.Id = c.Id;
        slot.Seq = ++_seq;
        slot.Source = c.Source;
        slot.Pan = c.Pan;
        slot.Loop = c.Loop;
        slot.LoopStart = c.LoopStart;
        slot.LoopEnd = c.LoopEnd;
        slot.Bus = c.Bus;
        slot.Priority = c.Priority;
        slot.StartAt = c.StartAt;
        slot.Position = 0;
        slot.InUnderrun = false;
        slot.StopAfterRamp = false;
        slot.RampLeft = 0;
        slot.GainStep = 0;
        if (c.Frames > 0) { slot.Gain = 0f; Ramp(slot, c.Gain, c.Frames); }
        else { slot.Gain = slot.GainTarget = c.Gain; }
    }

    private void EndVoice(Voice v)
    {
        v.Active = false;
        v.Source = null;
        Post(new MixerEvent(MixerEventKind.VoiceEnded, v.Id, 0));
    }

    private void Post(in MixerEvent e)
    {
        if (!_events.TryEnqueue(e)) Interlocked.Increment(ref _droppedEvents);
    }

    private void MixVoice(Voice v, Span<float> block, int frames, long blockStart)
    {
        int off = 0;
        if (v.StartAt > blockStart)
        {
            long wait = v.StartAt - blockStart;
            if (wait >= frames) return;
            off = (int)wait;
        }
        int n = frames - off;
        var src = v.Source!;
        int srcCh = src.Format.Channels;
        int got = src.ReadFrames(ref v.Position, v.Loop, v.LoopStart, v.LoopEnd, _scratch.AsSpan(0, n * srcCh), out bool underrun);

        if (underrun)
        {
            long missing = n - got;
            Volatile.Write(ref _underrunFrames, _underrunFrames + missing);
            if (!v.InUnderrun)
            {
                Volatile.Write(ref _underrunEvents, _underrunEvents + 1);
                Post(new MixerEvent(MixerEventKind.Underrun, v.Id, missing));
            }
            v.InUnderrun = true;
        }
        else if (got == n) v.InUnderrun = false;

        // Pan weights: constant power for mono sources, balance for stereo (the louder side stays at unity).
        float wl, wr;
        float pan = v.Pan;
        if (srcCh == 1)
        {
            double a = (pan + 1.0) * (Math.PI / 4.0);
            wl = (float)Math.Cos(a);
            wr = (float)Math.Sin(a);
        }
        else
        {
            wl = pan > 0f ? 1f - pan : 1f;
            wr = pan < 0f ? 1f + pan : 1f;
        }
        float bus = _busGain[v.Bus];
        wl *= bus;
        wr *= bus;

        int ch = _channels;
        float g = v.Gain;
        int ramp = v.RampLeft;
        var s = _scratch;
        for (int i = 0; i < got; i++)
        {
            if (ramp > 0)
            {
                g += v.GainStep;
                if (--ramp == 0) g = v.GainTarget;
            }
            int o = (off + i) * ch;
            if (ch == 1)
            {
                float m = srcCh == 1 ? s[i] : 0.5f * (s[2 * i] + s[2 * i + 1]);
                block[o] += m * g * (srcCh == 1 ? bus : 0.5f * (wl + wr));
            }
            else if (srcCh == 1)
            {
                float m = s[i] * g;
                block[o] += m * wl;
                block[o + 1] += m * wr;
            }
            else
            {
                block[o] += s[2 * i] * g * wl;
                block[o + 1] += s[2 * i + 1] * g * wr;
            }
        }
        // Frames not produced (underrun) are silence and the ramp does not advance through them.
        v.Gain = g;
        v.RampLeft = ramp;

        if (got < n && !underrun) { EndVoice(v); return; }
        if (v.StopAfterRamp && ramp == 0) EndVoice(v);
    }
}
