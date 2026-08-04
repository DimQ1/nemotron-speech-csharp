# Local Models

This directory is the single repository-local location for model files. Model binaries are intentionally ignored by Git; download or generate them locally.

```text
models/
  asr/
    nemotron-3.5/
      source/                 # Original NeMo model and conversion metadata
      onnx/                   # Runtime ONNX variants: cpu-int4, cpu-int8, gpu-cuda, etc.
```

## Provisioning

- Generate or place Nemotron ONNX variants in `asr/nemotron-3.5/onnx/`.
- Place the original NeMo export input in `asr/nemotron-3.5/source/`.

LVA models (VAD/NLU embeddings) and their download tooling were moved to the separate LVA repository.

Converter caches and intermediate outputs remain under the corresponding tool's `build/` directory or `.olive-cache/`; they are not canonical runtime models.
