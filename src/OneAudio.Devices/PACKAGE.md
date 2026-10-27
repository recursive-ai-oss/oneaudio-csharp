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

## Apple notes

- macOS: playback and capture on any Core Audio device, rate and channel conversion handled for you, and the stream follows default-device changes and removal. The system picks the hardware buffer size unless you set `PeriodFrames` or `BufferFrames`.
- Microphone: if the user denied access, opening an input fails with a message saying so. A command-line program without an app bundle and a microphone usage description may be unable to ask and receive silence.
- iOS: set `DeviceStreamOptions.AppleSession` to choose the audio-session category (`Ambient` by default). Interruptions pause the stream and resume it afterwards; the app needs a microphone usage description in its Info.plist to record. NativeAOT output exists for devices (`ios-arm64`); the simulator runs the Mono build.
