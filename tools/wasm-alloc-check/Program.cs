using System.Runtime.InteropServices.JavaScript;
using System.Text;
using OneAudio;
using OneAudio.Flac;
using OneAudio.Devices;

namespace OneAudio.WasmAllocCheck;

public static partial class Check
{
    private static int Main() => 0;

    /// <summary>
    /// Runs a mixer through a manual output device on the Mono runtime and returns one line per step plus a final verdict line.
    /// The mixer is first rendered silent (as a game would while it loads), then every step below must read zero bytes allocated
    /// by the render callback. Control calls (Play, SetBusFilter, ...) run between blocks and are not measured.
    /// </summary>
    [JSExport]
    public static string Run()
    {
        var log = new StringBuilder();
        int bad = 0;
        // Two mixers: the second proves that nothing is first-use per instance either.
        for (int round = 0; round < 2; round++)
        {
            log.Append("mixer ").Append(round).Append('\n');
            bad += RunOne(log);
        }

        bad += RunFlac(log);

        log.Append(bad == 0 ? "VERDICT PASS" : "VERDICT FAIL " + bad + " steps allocated").Append('\n');
        log.Append("runtime ").Append(System.Runtime.InteropServices.RuntimeInformation.RuntimeIdentifier).Append(' ')
            .Append(Environment.Version).Append('\n');
        return log.ToString();
    }

    /// <summary>
    /// Encodes a short stereo 16-bit signal with FlacEncoder and with FlacPacketEncoder, then decodes both and checks the STREAMINFO
    /// MD5 (the BCL's MD5 is not available on this platform, so any use of it throws). Returns 1 on failure.
    /// </summary>
    private static int RunFlac(StringBuilder log)
    {
        try
        {
            var format = new AudioFormat(44100, 2, ChannelMask.None, NativeSampleType.PcmSigned16, 16);
            var pcm = new int[2 * 10000];
            for (int i = 0; i < pcm.Length; i++) pcm[i] = (int)(9000 * Math.Sin(i * 0.013) + 3000 * Math.Sin(i * 0.31));

            var ms = new MemoryStream();
            using (var enc = new FlacEncoder(ms, format, leaveOpen: true)) { enc.WriteInteger(pcm); enc.Finish(); }
            ms.Position = 0;
            var dec = FlacDecoder.Open(ms);
            log.Append("flac encoder: ").Append(ms.Length).Append(" bytes, md5 ").Append(dec.VerifyMd5()).Append('\n');
            if (dec.VerifyMd5() != true) return 1;

            var file = new MemoryStream();
            using (var enc = new FlacPacketEncoder(format))
            {
                var packets = new List<byte[]>();
                for (int pos = 0; pos < pcm.Length;)
                {
                    int n = enc.WriteInteger(pcm.AsSpan(pos)) * 2;
                    pos += n;
                    if (enc.TryReadPacket(out var p)) packets.Add(p.Data.ToArray());
                }
                enc.Finish();
                if (enc.TryReadPacket(out var last)) packets.Add(last.Data.ToArray());
                file.Write("fLaC"u8);
                file.Write(enc.GetStreamInfoBlock());
                foreach (var p in packets) file.Write(p);
            }
            file.Position = 0;
            var dec2 = FlacDecoder.Open(file);
            bool? ok = dec2.VerifyMd5();
            log.Append("flac packet encoder: ").Append(file.Length).Append(" bytes, md5 ").Append(ok).Append('\n');
            return ok == true ? 0 : 1;
        }
        catch (Exception e)
        {
            log.Append("flac FAILED: ").Append(e.GetType().Name).Append(": ").Append(e.Message).Append('\n');
            return 1;
        }
    }

    private static AudioClip Tone(int rate, int channels, int frames)
    {
        var s = new float[frames * channels];
        for (int i = 0; i < frames; i++)
            for (int c = 0; c < channels; c++)
                s[i * channels + c] = 0.3f * MathF.Sin(i * 0.05f * (c + 1));
        return new AudioClip(new AudioFormat(rate, channels), s);
    }

    private static int RunOne(StringBuilder log)
    {
        var started = System.Diagnostics.Stopwatch.StartNew();
        var mixer = new AudioMixer(new MixerOptions
        {
            MaxVoices = 4, MaxBuses = 3, MaxReverbs = 1, MaxBusMeters = 1, LimiterLookaheadMilliseconds = 2, MaxSourceChannels = 6,
        });
        log.Append("  mixer creation (includes the warm-up on the first): ").Append(started.ElapsedMilliseconds).Append(" ms\n");
        var options = new DeviceStreamOptions { SampleRate = 48000, Channels = 2, MeasureRenderAllocations = true, AllocationWarmupBlocks = 48 };
        using var device = AudioOutputDevice.Open(mixer, options, new ManualBackend());
        device.Start();

        var mono = Tone(48000, 1, 20000);
        var stereo = Tone(48000, 2, 20000);
        var surround = Tone(48000, 6, 8000);
        var slow = Tone(24000, 2, 20000);
        var linear = Tone(44100, 1, 20000);

        int bad = 0;
        long last = 0;
        void Step(string name, int blocks = 12)
        {
            device.Advance(256 * blocks);
            long now = device.Counters.RenderAllocatedBytes;
            log.Append("  ").Append(name).Append(": ").Append(now - last).Append(" B\n");
            if (now != last) bad++;
            last = now;
        }

        // Silence first: the warm-up blocks plus a few hundred more, with nothing playing.
        device.Advance(256 * 400);
        last = device.Counters.RenderAllocatedBytes;
        Step("silent");

        var v1 = mixer.Play(new PlayParams(mono) { Pan = -0.5f });
        Step("play mono");
        var v2 = mixer.Play(new PlayParams(stereo) { Gain = 0.5f, Bus = 1 });
        Step("play stereo on bus 1");
        mixer.SetGain(v1, 0.2f, 512); mixer.SetPan(v1, 0.7f, 512); mixer.SetGain(v2, 0.8f);
        Step("gain and pan ramps");
        var v3 = mixer.Play(new PlayParams(surround) { Bus = 2 });
        Step("play 5.1 downmix");
        var v4 = mixer.Play(new PlayParams(slow) { Resampling = ResamplerQuality.PolyphaseFast });
        Step("play resampled (polyphase)");
        var v5 = mixer.Play(new PlayParams(linear) { Resampling = ResamplerQuality.Linear, VariablePitch = true, Loop = true, LoopStartFrame = 100, LoopEndFrame = 5000 });
        mixer.SetPitch(v5, 1.5f, 2048);
        Step("play looped, variable pitch");
        mixer.SetLoop(v5, false);
        Step("loop off");
        mixer.Crossfade(v1, new PlayParams(mono), 1024);
        Step("crossfade");
        mixer.Stop(v2, 512);
        mixer.Stop(v3);
        Step("stop with fade, stop now");
        mixer.Play(new PlayParams(mono) { StartAtFrame = mixer.TimelineFrames + 700 });
        Step("delayed start");
        for (int i = 0; i < 6; i++) mixer.Play(new PlayParams(stereo) { Priority = i });
        Step("voice stealing");
        mixer.SetBusGain(1, 0.5f, 256); mixer.SetBusMute(2, true); mixer.SetBusOutput(1, 2); mixer.SetMasterGain(0.9f);
        Step("bus gain, mute, route, master gain");
        mixer.SetBusFilter(1, FilterType.LowPass, 4000f); mixer.SetBusFilter(MixerMasterBus, FilterType.HighPass, 80f);
        Step("bus filters");
        mixer.SetVoiceFilter(v4, FilterType.LowPass, 3000f);
        Step("voice filter");
        mixer.SetBusReverb(0, new ReverbSettings());
        Step("reverb");
        mixer.SetDucking(0, 1);
        Step("ducking");
        mixer.EnableBusMeter(0);
        Step("bus meter");
        mixer.SetLimiter(new LimiterSettings());
        Step("limiter");
        mixer.ClearBusReverb(0); mixer.ClearDucking(0); mixer.ClearBusFilter(1, FilterType.LowPass);
        mixer.StopAll(256);
        {
            // A few thousand blocks of steady playback, in case something allocates after a call count rather than at first use.
            Step("drain", 20);
            mixer.Play(new PlayParams(mono) { Loop = true });
            mixer.Play(new PlayParams(stereo) { Loop = true, Bus = 1 });
            mixer.Play(new PlayParams(slow) { Loop = true, Resampling = ResamplerQuality.PolyphaseFast });
            mixer.SetBusReverb(0, new ReverbSettings());
            mixer.SetDucking(0, 1);
            for (int i = 0; i < 40; i++) Step("soak " + i * 100, 100);
        }
        Step("clear effects, stop all");
        while (mixer.TryGetEvent(out _)) { }
        Step("drain to silence", 100);
        return bad;
    }

    private const int MixerMasterBus = AudioMixer.MasterBus;
}
