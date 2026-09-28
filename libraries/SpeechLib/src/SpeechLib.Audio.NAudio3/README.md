# SpeechLib.Audio.NAudio3

Windows audio capture provider built against `NAudio.Core` / `NAudio.Wasapi` 3.0.1.
It is the only capture provider in the repository and is used by the CLI, VoiceType.WinUI
and the Windows head of VoiceType.Uno.

## Usage

```csharp
using SpeechLib;
using SpeechLib.Audio;
using SpeechLib.Models;

IAudioSourceFactory factory = new NAudio3AudioSourceFactory();
var source = factory.Create(CaptureMode.Mic, recognizer.SampleRate);
LiveTranscriber.Run(source, "Microphone", recognizer);
```

The provider supports microphone, WASAPI loopback, and mixed capture. The project targets
the portable `net10.0` TFM (NAudio 3 packages are `net9.0`), but the WASAPI classes only
work on Windows; `Transcriber.CreateAudioSource` throws `PlatformNotSupportedException`
elsewhere.

It also supplies `AudioUtils.LoadFile` and the `Transcriber.RunFile` orchestration used by
the CLI's file mode. WAV files are parsed portably; other containers (MP3, M4A, …) are
decoded through Media Foundation on Windows.

## Capture pipeline

- Each device is opened in its shared-mode mix format (typically 48 kHz float stereo).
  The microphone uses event-driven WASAPI (10 ms packets); loopback stays in polling
  mode because loopback streams do not signal the WASAPI event.
- Callback data is copied once into a two-second bounded `BufferedWaveProvider`.
- Every 50 ms the drain thread decodes PCM/float to mono (`PcmSampleDecoder`), converts
  to the recognizer rate with the stateful anti-aliased `StreamingResampler`, sums the
  channels with the user gains (`CaptureMixer`, hard-limited to ±1) and publishes one
  `float[]` batch.
- A device that stops on its own (unplugged, format change) fails the session with an
  actionable error in Mic/Loopback mode and degrades to the remaining source in Mix mode.
- The source exposes per-channel and master `AudioLevelMeter`s plus mic/loopback gain
  for mixer UIs, resolved through `AudioMixerRegistry`.
- `Dispose()` requests capture shutdown through `CaptureState`.

Hardware capture should still be validated on the target Windows devices.
