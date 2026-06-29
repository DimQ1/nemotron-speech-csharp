# SpeechLib 📚

Abstraction library for pluggable streaming speech recognition engines in .NET.

## Purpose

SpeechLib provides interfaces and shared utilities so that **any** speech recognition engine can be plugged into the same pipeline. Audio capture, resampling, language mapping, and transcription orchestration are all generic — no model-specific code.

## Architecture

```
SpeechLib/
├── Interfaces/
│   ├── IStreamingSpeechRecognizer.cs   # Core recognition engine abstraction
│   └── IAudioSource.cs                 # Audio capture source abstraction
├── Audio/
│   ├── AudioUtils.cs                   # Byte→float32 conversion, resampling, file loader
│   ├── ConcurrentQueueWrapper.cs       # Lock-free batch audio queue
│   ├── MicAudioSource.cs               # Microphone capture (16kHz mono)
│   ├── LoopbackAudioSource.cs          # System audio loopback (WasapiLoopbackCapture)
│   └── MixAudioSource.cs               # Mic + loopback mixed
├── Models/
│   ├── SpeechRecognitionOptions.cs      # Engine configuration record
│   └── CaptureMode.cs                  # File / Mic / Loopback / Mix enum
├── LanguageMapper.cs                   # BCP-47 → numeric lang_id (100+ languages)
├── Transcriber.cs                      # Orchestrator: RunFile() / RunLive() / CreateAudioSource()
└── SpeechLib.csproj                    # .NET 10, NAudio 2.2.1
```

## Key Interface

```csharp
public interface IStreamingSpeechRecognizer : IDisposable
{
    int SampleRate { get; }          // Expected sample rate (Hz)
    int ChunkSamples { get; }        // Samples per processing chunk

    string? ProcessAudio(float[] chunk);  // Feed audio → returns new text (or null)
    string? Flush();                      // Finalise → returns remaining text
}
```

### Implementing a New Engine

```csharp
public class WhisperRecognizer : IStreamingSpeechRecognizer
{
    public int SampleRate => 16000;
    public int ChunkSamples => 2560;

    public string? ProcessAudio(float[] chunk)
    {
        // Feed chunk to Whisper ONNX model
        // Return decoded text when available
    }

    public string? Flush() { /* final flush */ }
    public void Dispose() { /* cleanup */ }
}
```

Then use it with the existing `Transcriber`:

```csharp
using var recognizer = new WhisperRecognizer();
Transcriber.RunFile("audio.wav", recognizer);
Transcriber.RunLive(new MicAudioSource(), "Microphone", recognizer);
```

## Audio Sources

| Source | Sample Rate | Description |
|--------|-------------|-------------|
| `MicAudioSource` | 16 kHz | Windows WaveIn (auto-resampled from device) |
| `LoopbackAudioSource` | Configurable | System audio via WASAPI loopback |
| `MixAudioSource` | Configurable | Mic + System audio mixed |

## Language Mapper

Supports 100+ languages via BCP-47 codes or numeric IDs:

```csharp
LanguageMapper.Resolve("ru")    → "11"
LanguageMapper.Resolve("en-US") → "0"
LanguageMapper.Resolve("ja")    → "10"
LanguageMapper.Resolve("auto")  → "101"
```

## Dependencies

| Package | Version |
|---------|---------|
| NAudio | 2.2.1 |
| .NET | 10.0 |
