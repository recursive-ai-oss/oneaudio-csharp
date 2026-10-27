# RecursiveAI.OneAudio.Devices

Audio device playback and capture for [RecursiveAI.OneAudio](https://www.nuget.org/packages/RecursiveAI.OneAudio): WASAPI on Windows, Core Audio on macOS and iOS, ALSA on Linux, over the operating system's own libraries, with no dependencies beyond the .NET base class library.

```csharp
using OneAudio;

using var mixer = Audio.CreateMixer();                 // 48 kHz stereo
using var output = Audio.Play(mixer);                  // default device, follows default changes
mixer.Play(Audio.Load("shot.ogg"));
Console.WriteLine(output.Counters);                    // frames written and played, peak, underruns, losses, reopens
```

See the repository's `docs/api.md` for the full device API.

## Windows (WASAPI)

Shared mode (the default) converts any rate and channel count through the Windows mixer; its engine period is 10 ms by default (Microsoft's documented figure). `ShareMode.Exclusive` owns the device for lower latency, bounded by the driver's minimum period (a few milliseconds typically, as documented by Microsoft), but needs a format the hardware accepts directly: float32 or 32/24/16-bit integer at exactly your rate and channel count. When it cannot be granted the open is refused with a reason in `LastError`. A machine with no audio endpoint is the same as any missing device: the output runs silently at real-time pace and retries.
