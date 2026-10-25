using OneAudio.Effects;
using OneAudio.Kernels;
using OneAudio.Loudness;
using OneAudio.Mixing;

namespace OneAudio;

public sealed partial class AudioMixer
{
    // Input frames offered beyond the strict need, so a converter with a few frames of look-ahead never starves.
    private const int ResampleMargin = 16;
    // Input a converter left unconsumed is carried to the next pass in a small per-voice buffer.
    private const int CarryCapacity = 64;
    // Most input offered to a converter in one call: the converters append 256 frames at a time.
    private const int ResampleChunk = 256;
    // Detector release: a fixed ~5 ms peak decay so ducking does not chatter on waveform zero crossings.
    private const double DetectorReleaseSeconds = 0.005;

    private sealed class Voice
    {
        public bool Active;
        public long Id;
        public long Seq;
        public AudioSource? Source;
        public int SrcCh;
        public ChannelMask SrcMask;
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

        // Channel matrix [output * SrcCh + input], with a linear ramp from Mat to MatTarget over MatLeft frames.
        public readonly float[] Mat, MatTarget, MatStep;
        public int MatLeft;

        // Resampling: null when the source is at the mixer's rate and the voice cannot be pitched.
        public IResampler? Resampler;
        public double RateRatio; // mixer rate / source rate
        public float Pitch, PitchTarget, PitchStep;
        public int PitchLeft;
        public readonly float[] Carry;
        public int CarryFrames;
        public bool SourceEnded, Flushing;
        public bool Engaged;          // the converter is in use (see ApplyPitches); false while the effective ratio is exactly 1
        public double AppliedRatio;   // the ratio last given to the converter
        public double DesignScale;    // our estimate of the converter's filter design point, for the redesign budget
        public bool Heavy;            // polyphase: ratio moves can trigger a filter redesign
        public readonly FilterStage LowPass, HighPass;

        public Voice(int outChannels, int maxSrcChannels, int sampleRate)
        {
            LowPass = new FilterStage(false, maxSrcChannels, sampleRate);
            HighPass = new FilterStage(true, maxSrcChannels, sampleRate);
            Mat = new float[outChannels * maxSrcChannels];
            MatTarget = new float[outChannels * maxSrcChannels];
            MatStep = new float[outChannels * maxSrcChannels];
            Carry = new float[CarryCapacity * maxSrcChannels];
        }
    }

    private sealed class Bus
    {
        public float[] Buf = [];
        public bool Touched;
        public int Parent = -1;
        public float Level = 1f;     // the gain the host asked for
        public bool Muted;
        public float Gain = 1f;      // current gain, moving toward GainTarget
        public float GainTarget = 1f;
        public float GainStep;
        public int GainLeft;
        public int DuckSource = -1;
        public float DuckThreshold, DuckDepth = 1f, DuckAttack, DuckRelease;
        public float DuckEnv, DuckGain = 1f;
        public EffectChain Fx = null!;
        public MeterTap? Meter;
    }

    // Render-side state of one loudness meter slot. The published value is what control threads read.
    private sealed class MeterTap
    {
        public readonly LoudnessMeter Meter;
        public readonly SeqLockSlot<MeterPublication> Published = new();
        public long Generation;
        private readonly int _rate;
        private double _integrated = double.NegativeInfinity;
        private long _nextIntegratedAt;

        public MeterTap(LoudnessMeter meter, int rate)
        {
            Meter = meter;
            _rate = rate;
        }

        public void Start(long generation)
        {
            Generation = generation;
            Meter.Reset();
            _integrated = double.NegativeInfinity;
            _nextIntegratedAt = Boundary(1);
            Published.Write(new MeterPublication(generation, LoudnessSnapshot.Empty));
        }

        // The gated blocks the integrated loudness is made of arrive every 100 ms, so the (costly) gated readout is refreshed only then.
        private long Boundary(long hop) => hop * _rate / 10;

        public void Feed(ReadOnlySpan<float> interleaved)
        {
            var m = Meter;
            m.Process(interleaved);
            long frames = m.FramesProcessed;
            if (frames >= _nextIntegratedAt)
            {
                _integrated = m.IntegratedLufs;
                long hop = frames * 10 / _rate;
                while (Boundary(hop + 1) <= frames) hop++;
                while (hop > 0 && Boundary(hop) > frames) hop--;
                _nextIntegratedAt = Boundary(hop + 1);
            }
            Published.Write(new MeterPublication(Generation, new LoudnessSnapshot(
                m.MomentaryLufs, m.ShortTermLufs, _integrated, m.TruePeakDbtp, m.MaxMomentaryLufs, m.MaxShortTermLufs, frames)));
        }
    }

    private readonly record struct MeterPublication(long Generation, LoudnessSnapshot Snapshot);

    private readonly Voice[] _voices;
    private readonly int _voiceCount;
    private readonly Bus[] _buses;
    private readonly Bus _master;
    private readonly int[] _order;
    private readonly int[] _indegree;
    private readonly bool[] _placed;
    private int _orderCount;
    private bool _graphDirty = true;
    private readonly float[] _srcPlanar;   // decoded voice audio, planar, stride _maxBlock
    private readonly float[] _inter;       // interleaved staging after the converter
    private readonly float[] _inScratch;   // converter input
    private readonly int _inCapacity;      // frames
    private readonly float[] _curve;
    private readonly float _detectorRelease;
    private long _seq;
    private int _passFrames;
    private int _rotate;

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
            if (_graphDirty) RebuildOrder();
            _passFrames = n;
            foreach (var b in _buses) b.Touched = false;
            _master.Touched = false;

            ApplyPitches(n, blockStart);
            int active = 0;
            for (int i = 0; i < _voices.Length; i++)
            {
                var v = _voices[i];
                if (!v.Active) continue;
                MixVoice(v, n, blockStart);
                if (v.Active) active++;
            }
            for (int i = 0; i < _orderCount; i++) ProcessBus(_buses[_order[i]], n);
            FinishMaster(block, n);
            if (_master.Meter != null) _master.Meter.Feed(block);

            Volatile.Write(ref _activeVoices, active);
            Volatile.Write(ref _timeline, blockStart + n);
            done += n;
        }
    }

    // Processing order: a bus comes after every bus routed into it and after the source of its ducking link.
    // The control side has already rejected loops; a loop that slipped through would leave its buses unprocessed.
    private void RebuildOrder()
    {
        int count = _buses.Length;
        for (int b = 0; b < count; b++) { _indegree[b] = 0; _placed[b] = false; }
        for (int b = 0; b < count; b++)
        {
            if (_buses[b].Parent >= 0) _indegree[_buses[b].Parent]++;
            if (_buses[b].DuckSource >= 0) _indegree[b]++;
        }
        int placed = 0;
        bool progress = true;
        while (placed < count && progress)
        {
            progress = false;
            for (int b = 0; b < count; b++)
            {
                if (_placed[b] || _indegree[b] != 0) continue;
                _placed[b] = true;
                _order[placed++] = b;
                if (_buses[b].Parent >= 0) _indegree[_buses[b].Parent]--;
                for (int t = 0; t < count; t++)
                    if (_buses[t].DuckSource == b) _indegree[t]--;
                progress = true;
            }
        }
        _orderCount = placed;
        _graphDirty = false;
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
                case CommandKind.SetPan: { var v = Find(c.Id); if (v != null) { v.Pan = c.Pan; Remap(v, c.Frames < 0 ? _smoothFrames : c.Frames); } break; }
                case CommandKind.SetLoop: { var v = Find(c.Id); if (v != null) { v.Loop = c.Loop; v.LoopStart = c.LoopStart; v.LoopEnd = c.LoopEnd; } break; }
                case CommandKind.SetPitch: { var v = Find(c.Id); if (v != null) SetVoicePitch(v, c.Pitch, c.Frames); break; }
                case CommandKind.SetBusGain:
                    _buses[c.Bus].Level = c.Gain;
                    RetargetBus(_buses[c.Bus], c.Frames);
                    break;
                case CommandKind.SetBusMute:
                    _buses[c.Bus].Muted = c.Flag;
                    RetargetBus(_buses[c.Bus], c.Frames);
                    break;
                case CommandKind.SetMasterGain:
                    _master.Level = c.Gain;
                    RetargetBus(_master, c.Frames);
                    break;
                case CommandKind.SetBusRoute:
                    _buses[c.Bus].Parent = c.Aux;
                    _graphDirty = true;
                    break;
                case CommandKind.SetBusFilter:
                {
                    var fx = (c.Bus < 0 ? _master : _buses[c.Bus]).Fx;
                    var stage = fx.Filter((FilterType)c.Aux);
                    int ramp = c.Frames < 0 ? _smoothFrames : c.Frames;
                    if (c.Flag) stage.Set(c.F0, c.F1, ramp); else stage.Clear(ramp);
                    break;
                }
                case CommandKind.SetVoiceFilter:
                {
                    var v = Find(c.Id);
                    if (v != null)
                    {
                        var stage = (FilterType)c.Aux == FilterType.HighPass ? v.HighPass : v.LowPass;
                        int ramp = c.Frames < 0 ? _smoothFrames : c.Frames;
                        if (c.Flag) stage.Set(c.F0, c.F1, ramp); else stage.Clear(ramp);
                    }
                    break;
                }
                case CommandKind.SetBusReverb:
                {
                    var fx = (c.Bus < 0 ? _master : _buses[c.Bus]).Fx;
                    var proc = _reverbs[c.Aux];
                    if (!c.Flag) { if (fx.Reverb == proc) fx.Reverb = null; break; }
                    if (fx.Reverb != proc)
                    {
                        // A slot handed to a bus starts clean and with these settings, never with another bus's tail.
                        proc.Reset();
                        proc.Configure(c.Reverb!, 0);
                        fx.Reverb = proc;
                    }
                    else proc.Configure(c.Reverb!, c.Frames < 0 ? _smoothFrames : c.Frames);
                    break;
                }
                case CommandKind.SetBusMeter:
                {
                    var b = c.Bus < 0 ? _master : _buses[c.Bus];
                    var tap = _meterTaps[c.Aux];
                    if (!c.Flag) { if (b.Meter == tap) b.Meter = null; break; }
                    b.Meter = tap;
                    tap.Start(c.Id);
                    break;
                }
                case CommandKind.SetLimiter:
                    if (c.Flag)
                        _limiter!.Configure(true, (float)Math.Pow(10.0, c.Limiter!.CeilingDb / 20.0), c.Limiter.ReleaseMilliseconds * 0.001f, _sampleRate);
                    else
                        _limiter!.Configure(false, 1f, 0.1f, _sampleRate);
                    break;
                case CommandKind.SetDuck:
                {
                    var b = _buses[c.Bus];
                    b.DuckSource = c.Aux;
                    if (c.Aux >= 0)
                    {
                        b.DuckThreshold = c.F0; b.DuckDepth = c.F1; b.DuckAttack = c.F2; b.DuckRelease = c.F3;
                        b.DuckEnv = 0f;
                    }
                    _graphDirty = true;
                    break;
                }
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

    private void RetargetBus(Bus b, int frames)
    {
        if (frames < 0) frames = _smoothFrames;
        float target = b.Muted ? 0f : b.Level;
        b.GainTarget = target;
        if (frames <= 0) { b.Gain = target; b.GainLeft = 0; b.GainStep = 0; return; }
        b.GainLeft = frames;
        b.GainStep = (target - b.Gain) / frames;
    }

    private void BeginStop(Voice v, int fadeFrames)
    {
        if (fadeFrames <= 0) { EndVoice(v); return; }
        Ramp(v, 0f, fadeFrames);
        v.StopAfterRamp = true;
    }

    // Recomputes the channel matrix for the voice's pan; ramps from the current coefficients over 'frames' (snaps if 0).
    private void Remap(Voice v, int frames)
    {
        int len = _channels * v.SrcCh;
        ChannelMapper.Build(v.MatTarget, v.SrcCh, v.SrcMask, _channels, _outMask, v.Pan);
        if (frames <= 0)
        {
            v.MatTarget.AsSpan(0, len).CopyTo(v.Mat);
            Array.Clear(v.MatStep, 0, len);
            v.MatLeft = 0;
            return;
        }
        for (int i = 0; i < len; i++) v.MatStep[i] = (v.MatTarget[i] - v.Mat[i]) / frames;
        v.MatLeft = frames;
    }

    private static void SetVoicePitch(Voice v, float pitch, int frames)
    {
        if (v.Resampler == null) return;
        v.PitchTarget = pitch;
        if (frames <= 0) { v.Pitch = pitch; v.PitchLeft = 0; v.PitchStep = 0; return; }
        v.PitchLeft = frames;
        v.PitchStep = (pitch - v.Pitch) / frames;
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
                Retire(c.Resampler);
                return;
            }
            Interlocked.Increment(ref _stolen);
            Post(new MixerEvent(MixerEventKind.VoiceStolen, weakest.Id, 0));
            Retire(weakest.Resampler);
            weakest.Resampler = null;
            weakest.Active = false;
            weakest.Source = null;
            slot = weakest;
        }
        var src = c.Source!;
        slot.Active = true;
        slot.Id = c.Id;
        slot.Seq = ++_seq;
        slot.Source = src;
        slot.SrcCh = src.Format.Channels;
        slot.SrcMask = src.Format.ChannelMask;
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
        slot.LowPass.Clear(0);
        slot.HighPass.Clear(0);
        slot.Resampler = c.Resampler;
        slot.RateRatio = _sampleRate / (double)src.Format.SampleRate;
        slot.Pitch = slot.PitchTarget = c.Pitch;
        if (c.Resampler != null)
        {
            double ratio = Math.Clamp(slot.RateRatio / c.Pitch, _minRatio, _maxRatio);
            slot.AppliedRatio = c.Resampler.Ratio;
            slot.Engaged = c.Flag || ratio != 1.0;
            slot.Heavy = c.Resampler.Quality != ResamplerQuality.Linear;
            slot.DesignScale = Math.Max(Math.Min(1.0, _minRatio), Math.Min(1.0, slot.AppliedRatio));
        }
        slot.PitchLeft = 0;
        slot.PitchStep = 0;
        slot.CarryFrames = 0;
        slot.SourceEnded = false;
        slot.Flushing = false;
        Remap(slot, 0);
        if (c.Frames > 0) { slot.Gain = 0f; Ramp(slot, c.Gain, c.Frames); }
        else { slot.Gain = slot.GainTarget = c.Gain; }
    }

    private void EndVoice(Voice v)
    {
        v.Active = false;
        v.Source = null;
        Retire(v.Resampler);
        v.Resampler = null;
        Post(new MixerEvent(MixerEventKind.VoiceEnded, v.Id, 0));
    }

    // Hands a converter back to the control side for reuse; if the ring is full it is simply left to the collector.
    private void Retire(IResampler? rs)
    {
        if (rs != null) _retired.TryEnqueue(rs);
    }

    private void Post(in MixerEvent e)
    {
        if (!_events.TryEnqueue(e)) Interlocked.Increment(ref _droppedEvents);
    }

    private Span<float> Row(float[] buf, int channel, int start, int count) => buf.AsSpan(channel * _maxBlock + start, count);

    private void EnsureCleared(Bus b)
    {
        if (b.Touched) return;
        b.Touched = true;
        int n = _passFrames;
        for (int c = 0; c < _channels; c++) Row(b.Buf, c, 0, n).Clear();
    }

    private void MixVoice(Voice v, int frames, long blockStart)
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

        int got = v.Resampler == null || !v.Engaged
            ? ReadDirect(v, src, n, out bool underrun, out bool finished)
            : ReadResampled(v, src, n, out underrun, out finished);

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

        if (got > 0)
        {
            if (v.LowPass.Active || v.HighPass.Active)
            {
                v.HighPass.Process(_srcPlanar, _maxBlock, v.SrcCh, got);
                v.LowPass.Process(_srcPlanar, _maxBlock, v.SrcCh, got);
            }
            MixToBus(v, got, off);
        }
        // Frames not produced (underrun) are silence and the ramps do not advance through them.

        if (finished) { EndVoice(v); return; }
        if (v.StopAfterRamp && v.RampLeft == 0) EndVoice(v);
    }

    // Runs at the start of every pass, before any voice is mixed. Advances pitch glides and gives each converter its ratio.
    // Moving a polyphase converter's ratio can make it redesign its filter bank (~0.3 ms), so those moves share a per-pass
    // budget; voices that miss out keep their ratio for this pass. The start index rotates so no voice is starved.
    private void ApplyPitches(int frames, long blockStart)
    {
        int budget = _redesignBudget;
        int count = _voices.Length;
        int first = _rotate;
        _rotate = first + 1 == count ? 0 : first + 1;
        double sMin = Math.Min(1.0, _minRatio);
        for (int k = 0; k < count; k++)
        {
            int i = first + k;
            if (i >= count) i -= count;
            var v = _voices[i];
            var rs = v.Resampler;
            if (!v.Active || rs == null || v.StartAt >= blockStart + frames) continue;

            if (v.PitchLeft > 0)
            {
                int step = Math.Min(v.PitchLeft, frames);
                v.Pitch += v.PitchStep * step;
                v.PitchLeft -= step;
                if (v.PitchLeft == 0) v.Pitch = v.PitchTarget;
            }
            double ratio = Math.Clamp(v.RateRatio / v.Pitch, _minRatio, _maxRatio);
            if (ratio == v.AppliedRatio) continue;
            if (!v.Engaged)
            {
                if (ratio == 1.0) { v.AppliedRatio = 1.0; continue; }
                // First non-unity ratio of a voice that started without conversion: start the converter from a clean state.
                rs.Reset();
                v.Engaged = true;
            }

            // Mirrors the converter's rule for keeping its current filter: it stays while the ratio's cutoff is within 5% above
            // the design point and never below it. This is only used to budget; a wrong guess costs nothing but accuracy of the budget.
            double sNow = Math.Min(1.0, ratio), d = v.DesignScale;
            bool cheap = !v.Heavy || (sNow >= d && (sNow <= d * 1.05 || d >= 1.0));
            if (!cheap)
            {
                if (budget <= 0) continue;
                if (budget != int.MaxValue) budget--;
                v.DesignScale = sNow < d ? Math.Max(sMin, sNow * 0.98) : sNow;
            }
            // A custom converter may refuse a ratio; the voice then keeps its previous speed.
            try { rs.SetRatio(ratio); v.AppliedRatio = ratio; }
            catch (ArgumentOutOfRangeException) { v.AppliedRatio = rs.Ratio; }
        }
    }

    // Source at the mixer's rate, no converter: read straight into planar rows.
    private int ReadDirect(Voice v, AudioSource src, int n, out bool underrun, out bool finished)
    {
        int sc = v.SrcCh;
        int got;
        if (sc == 1)
            got = src.ReadFrames(ref v.Position, v.Loop, v.LoopStart, v.LoopEnd, _srcPlanar.AsSpan(0, n), out underrun);
        else
        {
            got = src.ReadFrames(ref v.Position, v.Loop, v.LoopStart, v.LoopEnd, _inter.AsSpan(0, n * sc), out underrun);
            MixKernels.Deinterleave(_inter, sc, got, _srcPlanar, _maxBlock);
        }
        finished = got < n && !underrun;
        return got;
    }

    // Source needs conversion: feed the converter from the source in chunks no larger than the converter appends at once
    // (so it consumes each chunk whole), after first taking whatever output it can already make from what it holds.
    // A converter that leaves input unconsumed anyway has it carried to the next call in a small per-voice buffer.
    private int ReadResampled(Voice v, AudioSource src, int n, out bool underrun, out bool finished)
    {
        var rs = v.Resampler!;
        int sc = v.SrcCh;
        underrun = false;
        finished = false;

        // (Not while flushing: Process has no end-of-input limit and would run on past the end of the source.)
        int produced = 0;
        if (!v.Flushing) rs.Process(ReadOnlySpan<float>.Empty, _inter.AsSpan(0, n * sc), out _, out produced);

        for (int guard = 0; produced < n && guard < 256; guard++)
        {
            if (v.Flushing)
            {
                int w = rs.Flush(_inter.AsSpan(produced * sc, (n - produced) * sc));
                if (w == 0) { finished = true; break; }
                produced += w;
                continue;
            }

            int want = n - produced;
            double ratio = rs.Ratio;
            int needIn = (int)Math.Min(ResampleChunk, Math.Ceiling(want / ratio) + ResampleMargin);
            int have = v.CarryFrames;
            if (have > 0) v.Carry.AsSpan(0, have * sc).CopyTo(_inScratch);
            bool starved = false;
            if (!v.SourceEnded && have < needIn)
            {
                int ask = needIn - have;
                int read = src.ReadFrames(ref v.Position, v.Loop, v.LoopStart, v.LoopEnd, _inScratch.AsSpan(have * sc, ask * sc), out bool u);
                if (read < ask)
                {
                    if (u) starved = true;
                    else v.SourceEnded = true;
                }
                have += read;
            }

            rs.Process(_inScratch.AsSpan(0, have * sc), _inter.AsSpan(produced * sc, want * sc), out int used, out int wrote);
            used = Math.Clamp(used, 0, have);
            int left = have - used;
            if (left > CarryCapacity) left = CarryCapacity; // a converter that leaves this much behind is misbehaving; drop the excess
            _inScratch.AsSpan(used * sc, left * sc).CopyTo(v.Carry);
            v.CarryFrames = left;
            produced += wrote;

            if (starved) { underrun = true; break; }
            if (wrote == 0 && used == 0)
            {
                if (v.SourceEnded) v.Flushing = true;
                else break;
            }
        }

        MixKernels.Deinterleave(_inter, sc, produced, _srcPlanar, _maxBlock);
        return produced;
    }

    // Applies the voice gain and channel matrix to the planar frames [0, got) and accumulates into its bus at 'off'.
    private void MixToBus(Voice v, int got, int off)
    {
        int sc = v.SrcCh;
        int oc = _channels;
        float fold = 1f;

        if (v.RampLeft > 0)
        {
            int k = Math.Min(v.RampLeft, got);
            float step = v.GainStep;
            for (int c = 0; c < sc; c++)
            {
                MixKernels.ScaleRamp(Row(_srcPlanar, c, 0, k), v.Gain + step, step);
                if (k < got) MixKernels.Scale(Row(_srcPlanar, c, k, got - k), v.RampLeft <= got ? v.GainTarget : v.Gain + step * k);
            }
            if (v.RampLeft <= got) { v.Gain = v.GainTarget; v.RampLeft = 0; }
            else { v.Gain += step * got; v.RampLeft -= got; }
        }
        else fold = v.Gain;

        var bus = _buses[v.Bus];
        EnsureCleared(bus);

        int left = v.MatLeft;
        int ramp = Math.Min(left, got);
        bool whole = left <= got;
        for (int i = 0; i < sc; i++)
        {
            for (int o = 0; o < oc; o++)
            {
                int idx = o * sc + i;
                float c0 = v.Mat[idx];
                float st = v.MatStep[idx];
                float end = whole ? v.MatTarget[idx] : c0 + st * got;
                if (c0 == 0f && st == 0f && end == 0f) continue;
                var dst = Row(bus.Buf, o, off, got);
                var s = Row(_srcPlanar, i, 0, got);
                if (ramp > 0)
                {
                    if (st != 0f) MixKernels.AddScaledRamp(dst[..ramp], s[..ramp], (c0 + st) * fold, st * fold);
                    else if (c0 != 0f) MixKernels.AddScaled(dst[..ramp], s[..ramp], c0 * fold);
                }
                if (ramp < got && end != 0f) MixKernels.AddScaled(dst[ramp..], s[ramp..], end * fold);
                v.Mat[idx] = end;
            }
        }
        if (left > 0) { v.MatLeft = whole ? 0 : left - got; if (whole) Array.Clear(v.MatStep, 0, oc * sc); }
    }

    // Applies a bus's gain in place, then adds it into its parent (or the master). Untouched buses only advance their ramps.
    private void ProcessBus(Bus b, int n)
    {
        bool ducking = b.DuckSource >= 0 || b.DuckGain < 1f;
        if (ducking) ComputeDuck(b, n);
        RunEffects(b, n);
        bool audible = ApplyGain(b, n);
        if (b.Meter != null) FeedBusMeter(b, n, audible);
        if (!audible) return;

        int oc = _channels;
        var parent = b.Parent >= 0 ? _buses[b.Parent] : _master;
        EnsureCleared(parent);
        for (int c = 0; c < oc; c++)
        {
            if (ducking) MixKernels.AddCurve(Row(parent.Buf, c, 0, n), Row(b.Buf, c, 0, n), _curve.AsSpan(0, n));
            else MixKernels.AddScaled(Row(parent.Buf, c, 0, n), Row(b.Buf, c, 0, n), 1f);
        }
    }

    // Feeds a bus's meter with what leaves the bus: its buffer after effects and gain, or silence when nothing audible remains.
    private void FeedBusMeter(Bus b, int n, bool audible)
    {
        var scratch = _meterScratch.AsSpan(0, n * _channels);
        if (audible) MixKernels.Interleave(b.Buf, _maxBlock, _channels, n, scratch);
        else scratch.Clear();
        b.Meter!.Feed(scratch);
    }

    // True when the bus's gain is and stays zero over this pass, so nothing it holds is audible.
    private static bool GainSilent(Bus b, int n)
    {
        int left = b.GainLeft;
        float end = left > n ? b.Gain + b.GainStep * n : left > 0 ? b.GainTarget : b.Gain;
        return b.Gain == 0f && end == 0f;
    }

    // Runs the bus's inserts in place. A bus without input still runs while an effect rings (a reverb tail); a bus that is
    // silenced by its gain does not, and its effects are cleared so that they do not wake up later with an old tail.
    private void RunEffects(Bus b, int n)
    {
        var fx = b.Fx;
        if (!fx.Needed) return;
        if (GainSilent(b, n))
        {
            if (fx.Ringing) fx.Reset();
            return;
        }
        if (!b.Touched)
        {
            if (!fx.Ringing) return;
            EnsureCleared(b);
        }
        fx.Process(b.Buf, _maxBlock, _channels, n);
    }

    // Scales the bus's buffer by its (possibly ramping) gain and advances the ramp. Returns whether anything audible remains.
    private bool ApplyGain(Bus b, int n)
    {
        int oc = _channels;
        int left = b.GainLeft;
        float g0 = b.Gain;
        float end = left > n ? g0 + b.GainStep * n : left > 0 ? b.GainTarget : g0;
        bool audible = b.Touched && !(g0 == 0f && end == 0f);

        if (audible)
        {
            if (left > 0)
            {
                int k = Math.Min(left, n);
                for (int c = 0; c < oc; c++)
                {
                    MixKernels.ScaleRamp(Row(b.Buf, c, 0, k), g0 + b.GainStep, b.GainStep);
                    if (k < n) MixKernels.Scale(Row(b.Buf, c, k, n - k), end);
                }
            }
            else if (g0 != 1f)
            {
                for (int c = 0; c < oc; c++) MixKernels.Scale(Row(b.Buf, c, 0, n), g0);
            }
        }

        if (left > 0)
        {
            b.Gain = end;
            b.GainLeft = left > n ? left - n : 0;
            if (left <= n) b.GainStep = 0f;
        }
        return audible;
    }

    // Follows the source bus's level (already gain-adjusted, since it is processed first) and writes the target's
    // duck gain for each frame into _curve.
    private void ComputeDuck(Bus t, int n)
    {
        Bus? s = t.DuckSource >= 0 ? _buses[t.DuckSource] : null;
        bool live = s != null && s.Touched;
        float env = t.DuckEnv, g = t.DuckGain;
        float thr = t.DuckThreshold, depth = t.DuckDepth, atk = t.DuckAttack, rel = t.DuckRelease;
        float detRel = _detectorRelease;
        int oc = _channels;
        var curve = _curve;
        for (int f = 0; f < n; f++)
        {
            float peak = 0f;
            if (live)
                for (int c = 0; c < oc; c++) peak = Math.Max(peak, Math.Abs(s!.Buf[c * _maxBlock + f]));
            env = peak > env ? peak : env * detRel;
            float target = s != null && env > thr ? depth : 1f;
            float coef = target < g ? atk : rel;
            g = target + (g - target) * coef;
            curve[f] = g;
        }
        t.DuckEnv = env;
        if (s == null && g > 0.99999f) g = 1f; // fully released after the link was removed
        t.DuckGain = g;
    }

    private void FinishMaster(Span<float> block, int n)
    {
        RunEffects(_master, n);
        bool audible = ApplyGain(_master, n);
        var limiter = _limiter;
        if (limiter != null)
        {
            // The limiter's delay line still holds audio after the mix goes quiet, so it keeps running until it has drained.
            if (!audible)
            {
                if (limiter.Idle) return;
                _master.Touched = true;
                for (int c = 0; c < _channels; c++) Row(_master.Buf, c, 0, n).Clear();
            }
            limiter.Process(_master.Buf, _maxBlock, _channels, n);
            Volatile.Write(ref _limiterGainBits, BitConverter.SingleToInt32Bits(limiter.MinGain));
            MixKernels.Interleave(_master.Buf, _maxBlock, _channels, n, block);
        }
        else if (audible) MixKernels.Interleave(_master.Buf, _maxBlock, _channels, n, block);
    }
}
