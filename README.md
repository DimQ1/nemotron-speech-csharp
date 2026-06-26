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

## Quick Start (your GPU)

### RTX 5070 / 5080 / 5090 (Blackwell)
```powershell
# 1. Build with Blackwell config (nightly ORT + CUDA 13)
dotnet build NemotronSpeech -c Release -p:GpuArch=Blackwell

# 2. Copy CUDA 13 + cuDNN 9 DLLs to bin\Release\net10.0\runtimes\win-x64\native\
#    (see DLL Dependencies section below)

# 3. Download converted model (or convert yourself — see Model Conversion)

# 4. Run (CUDA + VAD)
dotnet run --project NemotronSpeech -c Release --no-build -- `
  "models-onnx\gpu-cuda" cuda --mic --language auto --use_vad true
```

### RTX 20 / 30 / 40 (Turing, Ampere, Ada)
```powershell
# Standard build — no -p:GpuArch needed
dotnet build NemotronSpeech -c Release

# Copy CUDA 12 + cuDNN 9 DLLs, then run:
dotnet run --project NemotronSpeech -c Release --no-build -- `
  "models-onnx\gpu-cuda" cuda --mic --language auto --use_vad true
```

### CPU only (any machine)
```powershell
dotnet build NemotronSpeech -c Release -p:GpuArch=CPU
dotnet run --project NemotronSpeech -c Release --no-build -- `
  "models-onnx\cpu" cpu --mic --language ru --use_vad true
```

---

## Build Configurations

| Command | Target GPU | ORT GenAI | CUDA |
|---------|------------|-----------|------|
| `dotnet build -c Release` | RTX 20/30/40, GTX 16 | 0.14.1 stable | 12.x |
| `dotnet build -c Release -p:GpuArch=Blackwell` | RTX 50 (Blackwell) | nightly | 13.x |
| `dotnet build -c Release -p:GpuArch=CPU` | No GPU | 0.14.1 CPU | — |

The `GpuArch` property switches the NuGet package reference in `NemotronSpeech.csproj`:
- **Standard** → `Microsoft.ML.OnnxRuntimeGenAI.Cuda` 0.14.1
- **Blackwell** → `Microsoft.ML.OnnxRuntimeGenAI.Cuda` nightly (sm_120 support)
- **CPU** → `Microsoft.ML.OnnxRuntimeGenAI` (no CUDA)

---

## DLL Dependencies (GPU builds only)

After building, you MUST copy CUDA + cuDNN DLLs to the output folder:

```
bin\Release\net10.0\runtimes\win-x64\native\
├── onnxruntime.dll
├── onnxruntime_providers_cuda.dll
├── onnxruntime-genai-cuda.dll
├── cudart64_1X.dll        ← from CUDA Toolkit
├── cublas64_1X.dll        ← from CUDA Toolkit
├── cublasLt64_1X.dll      ← from CUDA Toolkit
├── cufft64_1X.dll         ← from CUDA Toolkit
├── cusparse64_1X.dll      ← from CUDA Toolkit
├── cudnn64_9.dll          ← from cuDNN
├── cudnn_ops64_9.dll      ← from cuDNN
└── ... (all cudnn_*64_9.dll)
```

### Standard (CUDA 12)
From: [CUDA Toolkit 12.6](https://developer.nvidia.com/cuda-12-6-0-download-archive) + [cuDNN 9.x for CUDA 12](https://developer.nvidia.com/cudnn)

### Blackwell (CUDA 13)
From: [CUDA Toolkit 13.x](https://developer.nvidia.com/cuda-downloads) + cuDNN 9.x for CUDA 13

**Tip:** Automate DLL copy in `.csproj`:
```xml
<Target Name="CopyCudaDlls" AfterTargets="Build">
  <ItemGroup>
    <CudaDlls Include="C:\Program Files\NVIDIA GPU Computing Toolkit\CUDA\v13.3\bin\*.dll" />
  </ItemGroup>
  <Copy SourceFiles="@(CudaDlls)" DestinationFolder="$(OutputPath)runtimes\win-x64\native\" />
</Target>
```

---

## Model Conversion

### Prerequisites
```powershell
# Python 3.11+
python -m venv .venv
.\.venv\Scripts\Activate.ps1

# Install deps
pip install olive-ai nemo-toolkit==2.7.3 torch --index-url https://download.pytorch.org/whl/cu128
pip install onnxruntime-genai-cuda  # or onnxruntime-genai for CPU
```

### Download Original Model
```powershell
# ~1.2 GB from HuggingFace
huggingface-cli download nvidia/nemotron-3.5-asr-streaming-multilingual-0.6B `
  --local-dir models-original/nemotron
```

### Convert
```powershell
# === CUDA INT8 (all NVIDIA GPUs) ===
python converter/src/optimize.py `
  --model models-original/nemotron `
  --out models-onnx/gpu-cuda `
  --ep cuda

# === RTX 50 (Blackwell) — apply torch patch first ===
# cupy lacks sm_120 kernels; patch uses torch for k-quant instead
python converter/src/patch_olive_torch.py
python converter/src/optimize.py `
  --model models-original/nemotron `
  --out models-onnx/gpu-cuda `
  --ep cuda

# === CPU INT4 (smallest, ~757 MB) ===
python converter/src/optimize.py `
  --model models-original/nemotron `
  --out models-onnx/cpu `
  --ep cpu

# === DirectML INT8 (Windows only) ===
python converter/src/optimize.py `
  --model models-original/nemotron `
  --out models-onnx/gpu-dml `
  --ep dml
```

**Output sizes:**

| Variant | Encoder | Size | Target |
|---------|---------|------|--------|
| `gpu-cuda` | INT8 | ~1021 MB | NVIDIA GPU |
| `cpu` | INT4 | ~757 MB | Any CPU |
| `gpu-dml` | INT8 | ~1021 MB | DirectML GPU |

### Custom Chunk Size

Edit `converter/src/nemotron_model_load.py`:
```python
CHUNK_SIZE = 0.56  # seconds (0.2 = 200ms fast, 1.12 = 1120ms accurate)
```
Smaller = lower latency, slightly lower accuracy. Re-convert after changing.

---

## Usage

```
NemotronSpeech <model_path> <audio_file|--mic|--loopback|--mix> [ep] [--language <code>] [--use_vad true]
```

| Arg | Description |
|-----|-------------|
| `model_path` | Path to ONNX model (contains `genai_config.json`) |
| `audio_file` | Audio file: `.wav`, `.mp3`, `.flac`, etc. |
| `--mic` | Live microphone capture |
| `--loopback` | System audio capture (speakers output) |
| `--mix` | Mic + system audio mixed together |
| `ep` | `cpu`, `cuda`, `dml`, or `follow_config` (default) |
| `--language` / `-l` | BCP-47 code or `auto` for auto-detect |
| `--use_vad true` | Enable Silero VAD (recommended) |

### Examples
```powershell
# File mode, auto-detect language, CUDA
dotnet run --no-build -- "models-onnx\gpu-cuda" cuda audio.wav --language auto

# Mic, Russian, CPU + VAD (~7% CPU idle)
dotnet run --no-build -- "models-onnx\cpu" cpu --mic --language ru --use_vad true

# Loopback, English, CUDA
dotnet run --no-build -- "models-onnx\gpu-cuda" cuda --loopback --language en

# Mix mode (mic + speakers), auto language
dotnet run --no-build -- "models-onnx\gpu-cuda" --mix --language auto
```

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
| `AudioSource.cs` | `IAudioSource` + Mic/Loopback/Mix |
| `AudioUtils.cs` | Convert, Resample, LoadFile |
| `Transcriber.cs` | RunFile, RunLive orchestration |

---

## License

MIT — see [LICENSE](converter/LICENSE)
