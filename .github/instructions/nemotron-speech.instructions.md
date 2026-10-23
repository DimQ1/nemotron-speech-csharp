---
name: "NemotronSpeech CLI Project Instructions"
description: "Use when working on the NemotronSpeech CLI, the Nemotron recognizer on ONNX Runtime, CLI parsing, or CPU execution providers."
applyTo: "apps/NemotronSpeech/**"
---

# NemotronSpeech CLI Rules

## Architecture

- Keep `NemotronSpeech` focused on command-line parsing and application orchestration.
- Keep the recognizers (session lifecycle, execution-provider configuration) in `SpeechLib.Providers`: `RecognizerFactory` builds them, the Nemotron one lives in `SpeechLib.Providers/Nemotron`. ONNX Runtime GenAI was removed; do not reintroduce it (`tools/scripts/check-no-genai.ps1`).
- Keep provider-neutral contracts and streaming orchestration in `SpeechLib`.
- The CLI picks the engine from the model folder: `config.json` with `model_type: nemo-conformer-tdt` → `ParakeetTdtRecognizer` (real streaming, see `Program.CreateParakeet`), otherwise a Nemotron model built through `RecognizerFactory` (`NemotronUtteranceRecognizer`, plain ONNX Runtime). Keep that switch in `Program.cs`; the engines themselves live in `SpeechLib.Providers`.
- The CLI uses `SpeechLib.Audio.NAudio3` (the only capture provider); NAudio 2 was removed — do not reintroduce it.

## CLI behavior

- Preserve the positional model path and file/live capture syntax.
- Keep `cpu`, `cuda`, `dml`, `tensorrt`, `NvTensorRtRtx`, and `follow_config` provider names compatible.
- `--use_vad` requires an explicit `true` or `false` value.
- Keep language values as BCP-47 codes or supported numeric IDs and resolve them through `LanguageMapper`.
- Report usage errors as actionable console messages without swallowing unexpected exceptions.

## CPU execution

- CPU tuning is owned by the recognizers (`NemotronRnntEngine`, `OrtCpuTuning`), not by the CLI loop.
- The default CPU configuration computes an `intra_op_num_threads` heuristic, sets `inter_op_num_threads=1`, disables thread spinning (`allow_spinning=0`), and uses `ORT_SEQUENTIAL` graph execution.
- Keep the constructor defaults of `NemotronRnntRecognizer` / `NemotronRecognizerOptions` backward-compatible when adding runtime options.
- Treat VAD as input processing. It does not stop capture or guarantee that every model inference is skipped, so do not document it as a general CPU off switch.
- Use `NemotronRnntCpuThreadBenchmark` (`tools/BenchmarkSuite1`) for controlled thread, VAD, and execution-mode comparisons.

## Build and test

```powershell
dotnet build NemotronSpeech.slnx -c Release
dotnet test libraries/SpeechLib/tests/SpeechLib.Nemotron.Tests/SpeechLib.Nemotron.Tests.csproj --filter "FullyQualifiedName~Unit_"
```

Builds are CPU-only (plain `Microsoft.ML.OnnxRuntime`); the `GpuArch` switch and its CUDA / DirectML variants were removed. Do not commit `bin`, `obj`, benchmark artifacts, downloaded models, or generated transcripts.

Preserve nullable reference types, implicit usings, file-scoped namespaces, and the existing record-based `AppOptions` parsing style.
