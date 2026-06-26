# Nemotron ASR .NET
Real-time speech recognition using NVIDIA Nemotron 3.5 ASR (0.6B) via ONNX Runtime GenAI in C#.

## Features
- **100+ languages** — auto-detect or specify via BCP-47 code
- **Live capture** — microphone, loopback, or mixed
- **File mode** — transcribe pre-recorded audio
- **VAD (Voice Activity Detection)** — reduces CPU from 58% to 7% in silence
- **CUDA / CPU / DirectML** — switch provider at runtime

## Quick Start

```powershell
# Clone
git clone https://github.com/YOUR_USER/nemotron-speech-csharp.git
cd nemotron-speech-csharp

# Download converted model (from HuggingFace or convert yourself)
# Place in models-onnx/gpu-cuda/ or models-onnx/cpu/

# Run (CPU + VAD, ~7% CPU)
dotnet run --project NemotronSpeech -c Release -- "models-onnx/gpu-cuda" --mic --language ru --use_vad true

# Run (CUDA GPU)
dotnet run --project NemotronSpeech -c Release -- "models-onnx/gpu-cuda" cuda --mic --language auto
```

## Usage

```
NemotronSpeech <model_path> <audio_file|--mic|--loopback|--mix> [ep] [--language <code>] [--use_vad true]
```

| Arg | Description |
|-----|-------------|
| `model_path` | Path to ONNX model directory |
| `audio_file` | Path to audio file (WAV, MP3, etc.) |
| `--mic` | Live microphone capture |
| `--loopback` | System audio capture |
| `--mix` | Mic + system audio mixed |
| `ep` | `cpu`, `cuda`, `dml`, or `follow_config` (default) |
| `--language` / `-l` | BCP-47 code (`ru`, `en`, `de`, `auto`, etc.) |
| `--use_vad true` | Enable Silero VAD |

## Model Conversion

See `converter/` for Python scripts to convert NeMo → ONNX via Olive.

```powershell
# Install deps
pip install olive-ai nemo-toolkit onnxruntime-genai-cuda

# Convert for CPU (INT4)
python converter/src/optimize.py --model models-original/nemotron --out models-onnx/cpu --ep cpu

# Convert for CUDA (INT8)
python converter/src/optimize.py --model models-original/nemotron --out models-onnx/gpu-cuda --ep cuda
```

## Hardware

| Provider | Min GPU | VRAM | Notes |
|----------|---------|------|-------|
| CUDA | RTX 20+ | 1 GB | INT8 encoder |
| CPU | Any x64 | N/A | INT4, ~58% CPU without VAD |
| DirectML | Any DX12 GPU | 1 GB | Windows only |

## Architecture

```
Audio → NAudio (WASAPI) → ConcurrentQueue<float[]> → StreamingProcessor → Generator → TokenizerStream → Text
                                                              ↑
                                                         Silero VAD
```

Solves: KISS, SOLID, low-allocation audio pipeline, lock-free producer-consumer.