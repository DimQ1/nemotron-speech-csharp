---
name: "NemotronSpeech CLI Project Instructions"
description: "Use when working on the NemotronSpeech CLI, ONNX Runtime GenAI configuration, model sessions, CLI parsing, or CPU/GPU execution providers."
applyTo: "apps/NemotronSpeech/**"
---

# NemotronSpeech CLI Rules

## Architecture

- Keep `NemotronSpeech` focused on command-line parsing and application orchestration.
- Keep ONNX Runtime GenAI lifecycle and execution-provider configuration in `SpeechLib.Nemotron`.
- Keep provider-neutral contracts and streaming orchestration in `SpeechLib`.
- The CLI uses `SpeechLib.Audio.NAudio2`; do not mix NAudio 2 and NAudio 3 references in one project.

## CLI behavior

- Preserve the positional model path and file/live capture syntax.
- Keep `cpu`, `cuda`, `dml`, `tensorrt`, `NvTensorRtRtx`, and `follow_config` provider names compatible.
- `--use_vad` requires an explicit `true` or `false` value.
- Keep language values as BCP-47 codes or supported numeric IDs and resolve them through `LanguageMapper`.
- Report usage errors as actionable console messages without swallowing unexpected exceptions.

## CPU execution

- CPU tuning is owned by `SpeechLib.Common.GetConfig`, not by the CLI loop.
- The default CPU configuration computes an `intra_op_num_threads` heuristic, sets `inter_op_num_threads=1`, enables `session.force_spinning_stop=1`, and uses `ORT_SEQUENTIAL` graph execution.
- Keep `ModelSession` constructor defaults backward-compatible when adding runtime options.
- Treat VAD as input processing. It does not stop capture or guarantee that every model inference is skipped, so do not document it as a general CPU off switch.
- Use `ModelSessionCpuThreadBenchmark` for controlled thread, VAD, and execution-mode comparisons.

## Build and test

```powershell
dotnet build NemotronSpeech.slnx -c Release -p:GpuArch=CPU
dotnet test apps/VoiceType/tests/VoiceType.Tests/VoiceType.Tests.csproj --filter "FullyQualifiedName~Unit_"
```

Use `GpuArch=Blackwell` only with the ORT-Nightly feed and `GpuArch=DML` for DirectML builds. Do not commit `bin`, `obj`, benchmark artifacts, downloaded models, or generated transcripts.

Preserve nullable reference types, implicit usings, file-scoped namespaces, and the existing record-based `AppOptions` parsing style.
