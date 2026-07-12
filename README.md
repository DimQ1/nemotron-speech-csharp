# Nemotron ASR .NET 🎙️

Real-time multilingual speech recognition using [NVIDIA Nemotron 3.5 ASR](https://huggingface.co/nvidia/nemotron-3.5-asr-streaming-multilingual-0.6B) (0.6B params) via ONNX Runtime GenAI in C#.

| Feature | Details |
|---------|---------|
| **Languages** | 100+ (auto-detect or BCP-47 code) |
| **Modes** | File, microphone, system loopback, mic+loopback mix |
| **VAD** | Silero VAD — cuts CPU from 58% → 7% in silence |
| **Providers** | CUDA, CPU, DirectML — switchable at runtime |
| **Architecture** | KISS + SOLID, lock-free audio pipeline |

---

## Solution Structure

```
nemotron-speech-csharp/
├── NemotronSpeech.slnx           # .NET 10 solution file
├── SpeechLib/                    # 📚 Speech recognition abstraction library
├── NemotronSpeech/               # 🎙️ Nemotron ONNX GenAI recognizer (CLI + engine)
├── VoiceType/                    # 🖥️ WPF desktop app (streaming dictation)
├── converter/                    # 🐍 Python model converter (NeMo → ONNX)
├── models-onnx/                  # 🧠 Pre-converted ONNX models (CPU, CUDA, DML, QNN)
├── models-original/              # 📦 Original NeMo model
└── Test-Audio/                   # 🎵 Test audio files
```

## Projects

| Project | Type | Description |
|---------|------|-------------|
| [**SpeechLib**](SpeechLib/README.md) | .NET 10 Library | Interfaces (`IStreamingSpeechRecognizer`, `IAudioSource`), audio utilities, live capture sources, language mapper, transcriber orchestrator |
| [**NemotronSpeech**](NemotronSpeech/README.md) | .NET 10 Console App | ONNX Runtime GenAI implementation of `IStreamingSpeechRecognizer` for NVIDIA Nemotron 3.5 ASR. Supports CUDA / CPU / DirectML. |
| [**VoiceType**](VoiceType/README.md) | .NET 10 WPF App | Desktop speech-to-text with global hotkeys, text injection into any app, session recording, post-processing pipeline, MP3 audio saving |

## Quick Start

### Prerequisites
- [.NET 10 SDK](https://dotnet.microsoft.com/download/dotnet/10.0)
- Windows 10/11
- Microphone

### CPU only (any machine)
```powershell
dotnet build NemotronSpeech.slnx -c Release -p:GpuArch=CPU
dotnet run --project VoiceType -c Release
```

### RTX 20 / 30 / 40 (CUDA)
```powershell
dotnet build NemotronSpeech.slnx -c Release
dotnet run --project VoiceType -c Release
```

### RTX 50 (Blackwell)
```powershell
dotnet build NemotronSpeech.slnx -c Release -p:GpuArch=Blackwell
dotnet run --project VoiceType -c Release
```

## Build Configurations

| Command | Target GPU | ORT GenAI | CUDA |
|---------|------------|-----------|------|
| `dotnet build -c Release` | RTX 20/30/40, GTX 16 | 0.14.1 stable | 12.x |
| `dotnet build -c Release -p:GpuArch=Blackwell` | RTX 50 (Blackwell) | nightly | 13.x |
| `dotnet build -c Release -p:GpuArch=CPU` | No GPU | 0.14.1 CPU | — |
| `dotnet build -c Release -p:GpuArch=DML` | Any GPU (DirectX) | 0.14.1 DML | — |

## Dependencies Graph

```mermaid
graph TD
    SL[SpeechLib] --> |NAudio| NA[NAudio 2.2.1]
    NS[NemotronSpeech] --> |ONNX GenAI| ORT[Microsoft.ML.OnnxRuntimeGenAI]
    NS --> SL
    VT[VoiceType WPF] --> SL
    VT --> NS
    VT --> |MP3| LAME[NAudio.Lame]
```

## CLI Usage (NemotronSpeech)

```powershell
# Microphone with VAD, Russian
dotnet run --project NemotronSpeech -c Release -- "models-onnx/cpu" --mic cpu --language ru --use_vad true

# Audio file
dotnet run --project NemotronSpeech -c Release -- "models-onnx/cpu" "audio.wav" cpu --language en

# Audio file with word-level timestamps
dotnet run --project NemotronSpeech -c Release -- "models-onnx/cpu" "audio.wav" cpu --word-timestamps

# System audio loopback
dotnet run --project NemotronSpeech -c Release -- "models-onnx/cpu" --loopback cpu
```

### Word Timestamps (`--word-timestamps`)

File-mode only. Outputs each word with its `[start → end]` time in seconds:

```
============================================================
  Perhaps he made up to the party afterwards and took her...
============================================================

  Word timings (25 words):
------------------------------------------------------------
  [0.56s -> 1.00s] Perhaps
  [1.00s -> 1.12s] he
  [1.12s -> 1.40s] made
  ...
```

| Aspect | Detail |
|--------|--------|
| **Granularity** | ~560ms per chunk, refined by token-count weighting |
| **Punctuation** | Merged into preceding word (no standalone `.` or `,` entries) |
| **Language tags** | `<en-US>`, `<de-DE>` etc. filtered from timing output |

## Speaker Diarization

Speaker diarization ("who spoke when") is available in the [VoiceType](VoiceType/README.md) batch transcription window. Uses NVIDIA Sortformer 4spk-v2 exported to ONNX.

### Models (HuggingFace)

| Model | Size | Speed | Repo |
|-------|------|-------|------|
| Sortformer FP32 | 470 MB | 16× real-time | [`DimQ1/sortformer-4spk-v2-onnx-fp32-cpu`](https://huggingface.co/DimQ1/sortformer-4spk-v2-onnx-fp32-cpu) |
| Sortformer INT8 | 129 MB | 29× real-time | [`DimQ1/sortformer-4spk-v2-onnx-int8-cpu`](https://huggingface.co/DimQ1/sortformer-4spk-v2-onnx-int8-cpu) |
| Sortformer INT4 | 73 MB | 33× real-time | [`DimQ1/sortformer-4spk-v2-onnx-int4-cpu`](https://huggingface.co/DimQ1/sortformer-4spk-v2-onnx-int4-cpu) |

### Test Dataset

100 real speech samples (LibriSpeech test-clean) for diarization evaluation:

→ [`DimQ1/sortformer-diarization-test-set`](https://huggingface.co/datasets/DimQ1/sortformer-diarization-test-set)

```python
from huggingface_hub import snapshot_download
dataset_path = snapshot_download("DimQ1/sortformer-diarization-test-set", repo_type="dataset")
```

### Similar Datasets for Diarization

| Dataset | Description | Source |
|---------|-------------|--------|
| **LibriSpeech** | 1000h English read speech | [openslr.org/12](https://www.openslr.org/12) |
| **VoxCeleb 1&2** | 7000+ celebrity speakers | [robots.ox.ac.uk/~vgg/data/voxceleb](https://www.robots.ox.ac.uk/~vgg/data/voxceleb/) |
| **AMI Corpus** | 100h meeting recordings | [groups.inf.ed.ac.uk/ami/corpus](https://groups.inf.ed.ac.uk/ami/corpus/) |
| **CALLHOME** | Multilingual telephone speech | [catalog.ldc.upenn.edu/LDC97S42](https://catalog.ldc.upenn.edu/LDC97S42) |
| **DIHARD III** | Challenging diarization benchmark | [dihardchallenge.github.io/dihard3](https://dihardchallenge.github.io/dihard3/) |
| **MUSAN** | Music/speech/noise for augmentation | [openslr.org/17](https://www.openslr.org/17/) |
| **Time distribution** | Weighted by estimated token count per word (Phase 2) |
| **Model** | `SpeechLib/Models/WordTiming.cs` — `Word`, `StartSeconds`, `EndSeconds` |
| **CLI flag** | `--word-timestamps` (ignored in live/mic mode) |

## Model Conversion

See [converter/README.md](converter/README.md) for Python model conversion (NeMo → ONNX). Available presets:

| Variant | Encoder | Size | Target |
|---------|---------|------|--------|
| `gpu-cuda` | INT8 | ~1021 MB | NVIDIA GPU |
| `cpu` | INT4 | ~757 MB | Any CPU |
| `gpu-dml` | INT8 | ~1021 MB | DirectML GPU |

---

### 📖 Detailed Documentation
- [SpeechLib README](SpeechLib/README.md) — library architecture & extensibility
- [NemotronSpeech README](NemotronSpeech/README.md) — model setup, GPU configs, CLI args
- [VoiceType README](VoiceType/README.md) — desktop app features & settings

### Language Codes (common)
`en` `ru` `zh` `de` `fr` `es` `ja` `ko` `hi` `ar` `pt` `it` `nl` `pl` `tr` `uk` `sv` `da` `fi` `no` `cs` `hu` `ro` `el` `th` `vi` `he` `auto`

---

## Performance

Measured on Ryzen 9 + RTX 5070 Ti Laptop (Blackwell, 20 cores):

| Mode | CPU idle | CPU speech | GPU | VRAM | Tokens |
|------|----------|------------|-----|------|--------|
| CUDA | 64% | 64% | 15% | 668 MB | ~1.1s |
| CUDA + VAD | 64% | 70% | 15% | 668 MB | ~1.1s |
| CPU | 58% | 58% | — | — | ~1.1s |
| **CPU + VAD** ✅ | **7%** | 25% | — | — | ~1.1s |

> ORT spawns one spin-wait thread per CPU core (~20 threads). The 60%+ "CPU usage" is idle spin, not real work. VAD skips inference on silence → average CPU drops to 7%.

---

## Architecture

```
Mic/Loopback ──→ NAudio WASAPI ──→ ConcurrentQueue<float[]> (lock-free, batched)
                                          │
                                          ▼
                                   StreamingProcessor
                                     │          │
                                Silero VAD     Encoder (INT4/INT8)
                                     │          │
                                     ▼          ▼
                                  Generator ← Joint (RNNT)
                                     │
                                     ▼
                                TokenizerStream → Console
```

**Files (SOLID):**

| File | Responsibility |
|------|---------------|
| `Program.cs` | Entry point, DI wiring |
| `AppOptions.cs` | CLI parsing |
| `LanguageMapper.cs` | BCP-47 → lang_id |
| `ModelSession.cs` | ORT model lifecycle |
| `WordTiming.cs` | Word + start/end time record |
| `AudioSource.cs` | `IAudioSource` + Mic/Loopback/Mix |
| `AudioUtils.cs` | Convert, Resample, LoadFile |
| `Transcriber.cs` | RunFile, RunLive orchestration, word timing |

---

## License

MIT — see [LICENSE](converter/LICENSE)
