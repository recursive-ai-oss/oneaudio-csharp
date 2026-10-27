# RecursiveAI.OneAudio.Devices

Audio device playback and capture for [RecursiveAI.OneAudio](https://www.nuget.org/packages/RecursiveAI.OneAudio): WASAPI on Windows, Core Audio on macOS and iOS, ALSA on Linux, AAudio on Android, over the operating system's own libraries, with no dependencies beyond the .NET base class library.

```csharp
using OneAudio;

using var mixer = Audio.CreateMixer();                 // 48 kHz stereo
using var output = Audio.Play(mixer);                  // default device, follows default changes
mixer.Play(Audio.Load("shot.ogg"));
Console.WriteLine(output.Counters);                    // frames written and played, peak, underruns, losses, reopens
```

The full device API is described in [docs/api.md](https://github.com/recursive-ai-oss/oneaudio-csharp/blob/main/docs/api.md).

## Apple notes

- macOS: playback and capture on any Core Audio device, rate and channel conversion handled for you, and the stream follows default-device changes and removal. The system picks the hardware buffer size unless you set `PeriodFrames` or `BufferFrames`.
- Microphone: if the user denied access, opening an input fails with a message saying so. A command-line program without an app bundle and a microphone usage description may be unable to ask and receive silence.
- iOS: set `DeviceStreamOptions.AppleSession` to choose the audio-session category (`Ambient` by default). Interruptions pause the stream and resume it afterwards; the app needs a microphone usage description in its Info.plist to record. NativeAOT output exists for devices (`ios-arm64`); the simulator runs the Mono build.

## Windows (WASAPI)

Shared mode (the default) converts any rate and channel count through the Windows mixer; its engine period is 10 ms by default (Microsoft's documented figure). `ShareMode.Exclusive` owns the device for lower latency, bounded by the driver's minimum period (a few milliseconds typically, as documented by Microsoft), but needs a format the hardware accepts directly: float32 or 32/24/16-bit integer at exactly your rate and channel count. When it cannot be granted the open is refused with a reason in `LastError`. A machine with no audio endpoint is the same as any missing device: the output runs silently at real-time pace and retries.
