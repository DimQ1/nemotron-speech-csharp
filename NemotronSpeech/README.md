# NemotronSpeech 🎙️

ONNX Runtime GenAI implementation of `IStreamingSpeechRecognizer` for [NVIDIA Nemotron 3.5 ASR](https://huggingface.co/nvidia/nemotron-3.5-asr-streaming-multilingual-0.6B) (0.6B params, streaming, multilingual).

## Overview

This project provides both a **CLI tool** and a **library-class recognizer** (`ModelSession`) that wraps the ONNX Runtime GenAI pipeline:

```
ModelSession
├── Config          # ONNX GenAI config (model path, EP)
├── Model           # Loaded ONNX model
├── StreamingProcessor  # Audio → NamedTensors
├── Tokenizer       # Tokenizer + TokenizerStream
├── GeneratorParams # Decoding parameters
└── Generator       # NamedTensors → tokens → text
```

## Structure

```
NemotronSpeech/
├── ModelSession.cs           # IStreamingSpeechRecognizer implementation (ONNX GenAI)
├── AppOptions.cs             # CLI argument parser
├── Program.cs                # Entry point (console app)
├── Common/
│   └── Common.cs             # ORT GenAI config/setup helpers
└── NemotronSpeech.csproj     # Multi-GPU build (Cpu/Cuda/Dml/Blackwell)
```

## GPU Architecture Selection

```powershell
# CPU only
dotnet build -c Release -p:GpuArch=CPU

# RTX 20/30/40 (default)
dotnet build -c Release

# RTX 50 (Blackwell, nightly ORT)
dotnet build -c Release -p:GpuArch=Blackwell

# DirectML (any GPU via DirectX)
dotnet build -c Release -p:GpuArch=DML
```

## CLI Arguments

```
NemotronSpeech <model_path> <audio_file|--mic|--loopback|--mix> [ep] [--language <code>] [--use_vad true|false]
```

| Argument | Description |
|----------|-------------|
| `model_path` | Path to ONNX model folder (`models-onnx/cpu`) |
| `audio_file` | WAV/MP3 file to transcribe |
| `--mic` | Live microphone capture |
| `--loopback` | System audio loopback capture |
| `--mix` | Mic + loopback mixed |
| `ep` | Execution provider: `cpu`, `cuda`, `dml`, `follow_config` |
| `--language` / `-l` | BCP-47 code (e.g. `ru`, `en-US`, `auto`) |
| `--use_vad` | Enable Silero VAD (`true`/`false`) |

## Examples

```powershell
# Russian, microphone, VAD on, CPU
dotnet run -- "models-onnx/cpu" --mic cpu --language ru --use_vad true

# English, audio file, CUDA
dotnet run -- "models-onnx/gpu-cuda" "meeting.wav" cuda --language en

# Auto-detect language, loopback, DML
dotnet run -- "models-onnx/dml" --loopback dml --language auto
```

## ModelSession as a Library

`ModelSession` implements `IStreamingSpeechRecognizer` from SpeechLib, so it can be used in any SpeechLib pipeline:

```csharp
using var session = new ModelSession(
    "models-onnx/cpu",       // model path
    "cpu",                   // execution provider
    "ru",                    // language (null = single-lang model)
    useVad: true             // enable Silero VAD
);

// Direct usage
var inputs = session.ProcessAudio(chunk);
if (inputs is not null)
{
    session.SetInputs(inputs);
    var text = session.DecodeTokens();
}

// Via IStreamingSpeechRecognizer
var text = ((IStreamingSpeechRecognizer)session).ProcessAudio(chunk);
```

## Runtime Options

- **VAD**: `_processor.SetOption("use_vad", "true")` — Silero Voice Activity Detection
- **Language**: `_generator.SetRuntimeOption("lang_id", "11")` — set at runtime
- **Single-language models**: Detected automatically (no lang_id input in encoder)

## Dependencies

| Package | Version |
|---------|---------|
| Microsoft.ML.OnnxRuntimeGenAI | 0.14.1 (Standard/CPU/DML) |
| Microsoft.ML.OnnxRuntimeGenAI.Cuda | 0.14.1 (Standard) |
| Microsoft.ML.OnnxRuntimeGenAI.Cuda | 0.15.0-dev (Blackwell) |
| Microsoft.ML.OnnxRuntimeGenAI.DirectML | 0.14.1 (DML) |
| SpeechLib | Project reference |
| System.CommandLine | 2.0.1 |
