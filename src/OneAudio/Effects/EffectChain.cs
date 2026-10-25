namespace OneAudio.Effects;

/// <summary>The insert effects of one bus, in processing order: high-pass, low-pass, then the reverb if a slot is attached. Render thread only.</summary>
internal sealed class EffectChain
{
    public readonly FilterStage HighPass, LowPass;
    public ReverbProcessor? Reverb;

    public EffectChain(int channels, int sampleRate)
    {
        HighPass = new FilterStage(true, channels, sampleRate);
        LowPass = new FilterStage(false, channels, sampleRate);
    }

    public bool Needed => HighPass.Active || LowPass.Active || Reverb != null;

    /// <summary>True while the chain can still produce sound from silence (filter ringing, reverb tail).</summary>
    public bool Ringing => (HighPass.Active && HighPass.Ringing) || (LowPass.Active && LowPass.Ringing) || (Reverb != null && Reverb.Ringing);

    public FilterStage Filter(FilterType type) => type == FilterType.HighPass ? HighPass : LowPass;

    public void Process(float[] buf, int stride, int channels, int frames)
    {
        HighPass.Process(buf, stride, channels, frames);
        LowPass.Process(buf, stride, channels, frames);
        Reverb?.Process(buf, stride, frames);
    }

    public void Reset()
    {
        HighPass.Reset();
        LowPass.Reset();
        Reverb?.Reset();
    }
}
