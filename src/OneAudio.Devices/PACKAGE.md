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
