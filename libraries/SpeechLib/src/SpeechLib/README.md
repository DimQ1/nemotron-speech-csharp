# SpeechLib

SpeechLib is the provider-neutral contract layer for streaming speech recognition in .NET.
It contains recognizer and audio-source interfaces, bounded batched queues, capture lifecycle state, decorators, the live transcription orchestrator, and the portable capture DSP (PCM decoding, anti-aliased resampling, channel mixing). Model runtimes and audio device libraries are separate providers.

## Projects

| Project | Responsibility | Platform |
| --- | --- | --- |
| `SpeechLib` | Core contracts, streaming infrastructure and capture DSP | `net10.0` |
| `SpeechLib.Providers` | Nemotron / Parakeet / Qwen3 / VibeVoice recognizers over ONNX Runtime | `net10.0` |
| `SpeechLib.Audio.NAudio3` | WASAPI capture (mic, loopback, mix) and file loading via NAudio 3.0.1 | `net10.0` (Windows at runtime) |

The core project has no NAudio package reference (only plain ONNX Runtime for the Silero VAD gate). Applications choose the providers they need through project references.

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

On Windows, reference `SpeechLib.Audio.NAudio3` and use its factory:

```csharp
using SpeechLib;
using SpeechLib.Audio;
using SpeechLib.Models;

IAudioSourceFactory factory = new NAudio3AudioSourceFactory();
var source = factory.Create(CaptureMode.Loopback, recognizer.SampleRate);
LiveTranscriber.Run(source, "System audio (loopback)", recognizer);
```

The core contracts remain usable from other platforms when an application supplies its own `IAudioSource` (VoiceType.Uno ships PulseAudio, ALSA and Android sources).

## Capture DSP

Platform sources share three portable building blocks from `SpeechLib.Audio`:

- `PcmSampleDecoder` — interleaved 8/16/24/32-bit PCM or float32 → mono float.
- `StreamingResampler` — stateful windowed-sinc (Kaiser) polyphase rate converter. It keeps filter history and the exact rational read position between calls, so audio can be fed in arbitrary block sizes without clicks, dropped samples or long-term drift, and content above the output Nyquist is attenuated instead of aliasing into the speech band.
- `CaptureMixer` — sums two channels with per-channel gain and hard-limits the result to ±1.

## Resource and throughput design

- Audio is enqueued in batches rather than one sample at a time.
- `ConcurrentQueueWrapper` retains at most 64 batches by default and drops the oldest batch when the consumer falls behind; `DroppedBatches` reports how many were lost.
- The NAudio 3 provider bounds its per-device ring buffer to two seconds and drains it every 50 ms.
- Capture waits are interruptible through `CaptureState`; shutdown does not depend on a polling sleep.
- The live runner waits for the capture thread, drains final batches, and only then flushes the recognizer.

## File mode

`SpeechLib.Audio.NAudio3` contains `AudioUtils.LoadFile` and the `Transcriber.RunFile` orchestration. The core remains independent of file and device codecs.

## Build

```powershell
dotnet build SpeechLib\SpeechLib.csproj
dotnet build SpeechLib.Audio.NAudio3\SpeechLib.Audio.NAudio3.csproj
```
