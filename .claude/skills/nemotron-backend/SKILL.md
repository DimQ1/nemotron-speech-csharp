---
name: nemotron-backend
description: "Use when: working on NemotronSpeech backend — ONNX Runtime GenAI inference, audio capture pipeline, multi-GPU builds, CLI parsing, or streaming ASR processing in C#/.NET."
---

# NemotronSpeech Backend Patterns

## Project Overview

.NET 10 console app for real-time multilingual speech recognition via NVIDIA Nemotron 3.5 ASR (0.6B params) using ONNX Runtime GenAI. Follows **KISS + SOLID** principles with a lock-free audio pipeline.

## Architecture Layers

```
CLI (Program.cs)
  └─► AppOptions (parsed record)
        └─► LanguageMapper (BCP-47 → numeric lang_id)
        └─► ModelSession (ONNX GenAI lifecycle wrapper)
              └─► Transcriber (orchestrator)
                    ├── RunFile()  — batch file transcription
                    └── RunLive()  — streaming capture + inference
                          └─► IAudioSource implementations
                                ├── MicAudioSource      (WaveInEvent, 16kHz mono)
                                ├── LoopbackAudioSource  (WasapiLoopbackCapture + resample)
                                └── MixAudioSource       (mic + loopback threads)
```

---

## Pattern 1: ONNX GenAI Model Lifecycle

### File: `ModelSession.cs`

**Wrapper pattern** — single `sealed class : IDisposable` that owns the full ONNX GenAI lifecycle:

| Step | Type | Purpose |
|------|------|---------|
| 1 | `Config` | Load `genai_config.json`, set execution provider |
| 2 | `Model` | Instantiate model from config |
| 3 | `StreamingProcessor` | Accumulate audio chunks, emit encoded tensors when ready |
| 4 | `Tokenizer` + `TokenizerStream` | Decode output tokens → text |
| 5 | `GeneratorParams` + `Generator` | Autoregressive token generation from encoded inputs |

**Disposal order** (reverse construction):
```csharp
_generator → _genParams → _tokenizerStream → _tokenizer → _processor → _model → _config
```

**Key rules:**
- `StreamingProcessor.Process(float[] chunk)` returns `NamedTensors?` — null means "not enough audio yet"
- `StreamingProcessor.Flush()` drains remaining buffered audio
- Single-language models: no `lang_id` input in encoder — detect via `genai_config.json` introspection
- VAD (`Silero VAD`) set via `processor.SetOption("use_vad", "true")` — graceful fallback if unsupported

### genai_config.json Structure

```json
{
  "model": {
    "sample_rate": 16000,
    "chunk_samples": 2560,
    "encoder": {
      "inputs": {
        "audio": { "shape": [1, 2560] },
        "lang_id": { "shape": [1] }
      }
    }
  }
}
```

- `sample_rate` — expected audio sample rate (Hz)
- `chunk_samples` — samples per processing chunk
- `encoder.inputs.lang_id` — **presence determines `IsSingleLanguage`**

---

## Pattern 2: Streaming Audio Pipeline (Lock-Free)

### File: `Transcriber.cs`, `AudioSource.cs`

**ConcurrentQueue batching pattern** — avoids per-sample atomic operations:

```csharp
// WAS: ~16000 Enqueue/sec (per-sample) — lock contention
// NOW: ~10 Enqueue/sec (per-batch) — batch at source
public sealed class ConcurrentQueueWrapper
{
    private readonly ConcurrentQueue<float[]> _queue = new();
    public void Enqueue(float[] batch) => _queue.Enqueue(batch);
    public bool TryDequeue(out float[] batch) => _queue.TryDequeue(out batch!);
    public bool IsEmpty => _queue.IsEmpty;
}
```

**Producer-consumer with signal:**
- Producer thread captures audio → batches `float[]` → `Enqueue()` → `ManualResetEventSlim.Set()`
- Consumer loop: `TryDequeue()` each batch → `ProcessAudio()` → optional `DecodeTokens()`
- 1.5s silence timeout after last audio to auto-stop
- Warmup: feed silent chunk to prime the processor/JIT

**IAudioSource interface:**
```csharp
public interface IAudioSource : IDisposable
{
    int SourceSampleRate { get; }
    void Start(ConcurrentQueueWrapper buffer, ManualResetEventSlim signal, ref bool isRunning);
}
```

---

## Pattern 3: Multi-GPU Build Configuration

### File: `NemotronSpeech.csproj`

**MSBuild `Choose/When` pattern** for conditional NuGet packages per GPU architecture:

```xml
<PropertyGroup>
    <GpuArch Condition="'$(GpuArch)' == ''">Standard</GpuArch>
</PropertyGroup>

<Choose>
    <When Condition="'$(GpuArch)' == 'Blackwell'">
        <!-- Nightly ORT GenAI + CUDA 13 for RTX 50 -->
        <PackageReference Include="Microsoft.ML.OnnxRuntimeGenAI.Cuda" Version="0.15.0-dev-*" />
    </When>
    <When Condition="'$(GpuArch)' == 'CPU'">
        <!-- CPU-only, no CUDA -->
        <PackageReference Include="Microsoft.ML.OnnxRuntimeGenAI" Version="0.14.1" />
    </When>
    <When Condition="'$(GpuArch)' == 'DML'">
        <!-- DirectML (any GPU via DirectX) -->
        <PackageReference Include="Microsoft.ML.OnnxRuntimeGenAI.DirectML" Version="0.14.1" />
    </When>
    <Otherwise>
        <!-- Standard: RTX 20/30/40, CUDA 12 -->
        <PackageReference Include="Microsoft.ML.OnnxRuntimeGenAI.Cuda" Version="0.14.1" />
    </Otherwise>
</Choose>
```

**Build commands:**
| Command | Target |
|---------|--------|
| `dotnet build -c Release` | RTX 20/30/40 |
| `dotnet build -c Release -p:GpuArch=Blackwell` | RTX 50 |
| `dotnet build -c Release -p:GpuArch=CPU` | CPU only |
| `dotnet build -c Release -p:GpuArch=DML` | DirectML |

**NuGet feeds** (`nuget.config`):
- Standard: `api.nuget.org`
- Nightly: `https://aiinfra.pkgs.visualstudio.com/PublicPackages/_packaging/ORT-Nightly/nuget/v3/index.json`

---

## Pattern 4: Execution Provider Strategy

### File: `Common/Common.cs`

**Provider selection rules:**
- `"follow_config"` — use what `genai_config.json` specifies (default)
- `"cpu"` — clear all providers, no GPU
- `"cuda"` — clear providers, append CUDA
- `"dml"` — **keep CPU fallback**, append DML (different from others!)

```csharp
if (ep == "dml")
{
    config.AppendProvider(ep);        // keep defaults
}
else
{
    config.ClearProviders();
    if (ep != "cpu")
        config.AppendProvider(ep);
}
```

---

## Pattern 5: CLI Parsing with Record + Pattern Matching

### File: `AppOptions.cs`

**Immutable record with `with` expressions:**

```csharp
public sealed record AppOptions
{
    public string ModelPath { get; init; } = "";
    public string? AudioFile { get; init; }
    public string ExecutionProvider { get; init; } = "follow_config";
    public string LanguageArg { get; init; } = "";
    public bool UseVad { get; init; }
    public CaptureMode Mode { get; init; } = CaptureMode.File;
    public bool IsLive => Mode != CaptureMode.File;
}

public enum CaptureMode { File, Mic, Loopback, Mix }
```

**Parsing approach** — manual loop with `switch` over args, no external CLI library dependency in parsing:

Key rules:
- `--mic`, `--loopback`, `--mix` set `CaptureMode`
- `--language` / `-l` with next arg
- `--use_vad true` with next arg
- Known EP names (`cpu`, `cuda`, `dml`, `follow_config`) treated as execution provider
- Non-flag, non-EP args treated as audio file path (first match)

---

## Pattern 6: Language Resolution

### File: `LanguageMapper.cs`

**Static dictionary with fallback chain:**
1. Null/empty → `null` (auto-detect)
2. Integer string (0-127) → pass through as `lang_id`
3. BCP-47 code → lookup in `Dictionary<string, int>` (case-insensitive)
4. Unknown → warning + `null` (auto-detect)

**Special codes:** `"auto"` → 101

---

## Pattern 7: Audio Format Conversion

### File: `AudioUtils.cs`

**Three-stage pipeline:**
1. **Raw bytes → float[]** — handle 8/16/32-bit, stereo→mono downmix
2. **Resample** — linear interpolation with optional gain
3. **File load** — NAudio `AudioFileReader` → `StereoToMonoSampleProvider` → `WdlResamplingSampleProvider`

**Stereo→mono:** `(L + R) * 0.5` (average, not sum)

---

## Code Quality Rules

### Always
- `sealed` classes by default (no inheritance expected)
- `IDisposable` for any native resource wrapper — deterministic cleanup
- `Nullable=enable` + `ImplicitUsings=enable` in `.csproj`
- `AllowUnsafeBlocks=true` for performance-critical paths
- File-scoped namespaces (`namespace X;`)
- Primary constructors for DI-ready classes

### Never
- Don't lock on hot paths — use `ConcurrentQueue` + batch enqueue
- Don't hardcode model paths — resolve relative to `AppContext.BaseDirectory`
- Don't throw on optional features (VAD, lang_id) — catch + warn
- Don't expose mutable state on `ModelSession` — only init-time properties

### Error Handling
- Graceful degradation: VAD failure → continue without VAD
- Lang_id failure → continue with auto-detect
- Warmup failure → best-effort, catch and ignore
- `ArgumentException` / `DirectoryNotFoundException` → usage message to stderr

---

## Typical Development Workflow

1. Build for target GPU: `dotnet build NemotronSpeech -c Release [-p:GpuArch=...]`
2. Copy CUDA/cuDNN DLLs to output `runtimes\win-x64\native\`
3. Export/download ONNX model to `models-onnx/<variant>/`
4. Run: `dotnet run --project NemotronSpeech -c Release --no-build -- "<model_path>" <mode> [ep] [--language <code>]`
