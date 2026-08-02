# SpeechLib

SpeechLib is the provider-neutral contract layer for streaming speech recognition in .NET.
It contains recognizer and audio-source interfaces, bounded batched queues, capture lifecycle state, decorators, and the live transcription orchestrator. Model runtimes and audio device libraries are separate providers.

## Projects

| Project | Responsibility | Platform |
| --- | --- | --- |
| `SpeechLib` | Core contracts and allocation-conscious streaming infrastructure | `net10.0` |
| `SpeechLib.Nemotron` | NVIDIA Nemotron ONNX Runtime GenAI recognizer | `net10.0` |
| `SpeechLib.Audio.NAudio2` | Stable NAudio 2.3.0 capture and file utilities | `net10.0` |
| `SpeechLib.Audio.NAudio3` | NAudio 3.0.0-preview.19 capture alternative | `net10.0-windows7.0` |

The core project has no NAudio or ONNX Runtime package reference. Applications choose the providers they need through project references.

## Core contracts

```csharp
public interface IStreamingSpeechRecognizer : IDisposable
{
    int SampleRate { get; }
    int ChunkSamples { get; }
    string? ProcessAudio(float[] chunk);
    string? Flush();
}

public interface IAudioSource : IDisposable
{
    int SourceSampleRate { get; }
    void Start(
        ConcurrentQueueWrapper buffer,
        ManualResetEventSlim signal,
        CaptureState state);
}

public interface IAudioSourceFactory
{
    IAudioSource Create(CaptureMode mode, int sampleRate);
}
```

`LiveTranscriber.Run` accepts any `IAudioSource` and `IStreamingSpeechRecognizer`. It waits for capture termination, drains final batches, flushes the recognizer, and disposes the source.

## Selecting an audio provider

The stable provider is the existing application default:

```csharp
using SpeechLib;
using SpeechLib.Audio;
using SpeechLib.Models;

IAudioSourceFactory factory = new NAudio2AudioSourceFactory();
var source = factory.Create(CaptureMode.Mic, recognizer.SampleRate);
LiveTranscriber.Run(source, "Microphone", recognizer);
```

To try the NAudio 3 preview provider, reference `SpeechLib.Audio.NAudio3` instead and use its factory:

```csharp
using SpeechLib;
using SpeechLib.Audio;
using SpeechLib.Models;

IAudioSourceFactory factory = new NAudio3AudioSourceFactory();
var source = factory.Create(CaptureMode.Loopback, recognizer.SampleRate);
LiveTranscriber.Run(source, "System audio (loopback)", recognizer);
```

Both providers preserve the same batched `float[]` contract. NAudio 3 is a preview API and, like the current device-capture implementation, is Windows-only. The core contracts remain usable from other platforms when an application supplies its own `IAudioSource`.

## Resource and throughput design

- Audio is enqueued in batches rather than one sample at a time.
- `ConcurrentQueueWrapper` retains at most 64 batches by default and drops the oldest batch when the consumer falls behind.
- Provider ring buffers are bounded to five seconds in the stable provider and two seconds in the NAudio 3 provider.
- Capture waits are interruptible through `CaptureState`; shutdown does not depend on a polling sleep.
- The live runner waits for the capture thread, drains final batches, and only then flushes the recognizer.
- NAudio 3 copies callback data once into its bounded provider buffer and uses `ArrayPool<float>` for temporary drain arrays.

## File mode

`SpeechLib.Audio.NAudio2` contains the NAudio-backed file loader and the legacy `Transcriber.RunFile` orchestration. A different provider can implement file decoding separately while reusing `IStreamingSpeechRecognizer`.

## Build

```powershell
dotnet build SpeechLib\SpeechLib.csproj
dotnet build SpeechLib.Audio.NAudio2\SpeechLib.Audio.NAudio2.csproj
dotnet build SpeechLib.Audio.NAudio3\SpeechLib.Audio.NAudio3.csproj
```
