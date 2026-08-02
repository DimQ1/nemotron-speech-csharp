# SpeechLib.Audio.NAudio3

Windows-only audio capture provider built against `NAudio 3.0.0-preview.19`.

## Usage

Reference both `SpeechLib` and `SpeechLib.Audio.NAudio3`, then select the preview factory explicitly:

```csharp
using SpeechLib;
using SpeechLib.Audio;
using SpeechLib.Models;

IAudioSourceFactory factory = new NAudio3AudioSourceFactory();
var source = factory.Create(CaptureMode.Mic, recognizer.SampleRate);
LiveTranscriber.Run(source, "Microphone", recognizer);
```

The provider supports microphone, WASAPI loopback, and mixed capture. It targets `net10.0-windows7.0` because NAudio 3 preview packages use Windows-specific APIs.

## Resource behavior

- Callback input is copied once into a two-second bounded `BufferedWaveProvider`.
- Capture output is published as `float[]` batches, matching the stable provider contract.
- Temporary drain arrays use `ArrayPool<float>`.
- `Dispose()` requests capture shutdown through `CaptureState`.

The NAudio 3 package is prerelease and should be evaluated separately from the stable NAudio 2 provider before being made the application default.
