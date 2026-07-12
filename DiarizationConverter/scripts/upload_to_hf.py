"""
Upload Sortformer diarization ONNX models to HuggingFace.

Creates 3 repos under DimQ1/:
  - sortformer-4spk-v2-onnx-fp32-cpu
  - sortformer-4spk-v2-onnx-int8-cpu
  - sortformer-4spk-v2-onnx-int4-cpu

Requirements:
  - HUGGINGFACE_TOKEN or HF_TOKEN environment variable
  - huggingface_hub installed
"""

import os
import sys
import time
from pathlib import Path
from huggingface_hub import HfApi, create_repo, upload_file

ROOT = Path(__file__).resolve().parent.parent
MODELS_DIR = ROOT / "models"

# ── Repo definitions ─────────────────────────────────────────────────
REPOS = [
    {
        "repo_id": "DimQ1/sortformer-4spk-v2-onnx-fp32-cpu",
        "model_dir": MODELS_DIR / "sortformer_fp32",
        "label": "FP32",
        "files": ["sortformer.onnx"],
        "card": "README.md",
    },
    {
        "repo_id": "DimQ1/sortformer-4spk-v2-onnx-int8-cpu",
        "model_dir": MODELS_DIR / "sortformer_int8",
        "label": "INT8",
        "files": ["sortformer.onnx", "sortformer.data"],
        "card": "README.md",
    },
    {
        "repo_id": "DimQ1/sortformer-4spk-v2-onnx-int4-cpu",
        "model_dir": MODELS_DIR / "sortformer_int4",
        "label": "INT4",
        "files": ["sortformer.onnx", "sortformer.data"],
        "card": "README.md",
    },
]

# ── Shared model card content (JSON-safe) ────────────────────────────
CARD_FP32 = """---
license: cc-by-4.0
language:
  - en
  - multilingual
tags:
  - onnx
  - onnxruntime
  - sortformer
  - speaker-diarization
  - nemo
  - fp32
inference: false
library_name: onnxruntime
datasets:
  - libricss
pipeline_tag: audio-classification
---

# Sortformer 4spk-v2 — ONNX FP32 (CPU)

FP32 ONNX export of NVIDIA's **Sortformer 4spk-v2** speaker diarization model
for CPU inference via ONNX Runtime.

## Model Details

- **Base model:** [nvidia/diar_streaming_sortformer_4spk-v2](https://huggingface.co/nvidia/diar_streaming_sortformer_4spk-v2)
- **Format:** ONNX opset 21, FP32
- **Parameters:** 117.7 M
- **File size:** 469 MB (single-file)
- **Input:** `processed_signal` — log-mel spectrogram [B, 128, mel_frames]
- **Output:** `speaker_logits` — per-frame sigmoid probabilities [B, diar_frames, 4]
- **Diar frame rate:** 80 ms (10 ms mel stride / 8× subsampling)

## Architecture

```
Audio (16 kHz mono) → STFT → 128 mel bins
  → ConformerEncoder (17 layers, d=512)
    → SortformerModules (4 speakers)
      → TransformerEncoder (18 layers, d=192)
        → Sigmoid → [B, diar_frames, 4] speaker logits
```

## Performance (CPU, 12 threads)

| Metric | Value |
|---|---|
| **RTF**  | 0.037 (27× real-time) |
| **DER**  | 17.82% (LibriCSS test set) |
| **Size** | 469 MB |

## Preprocessing

Before inference, convert raw 16 kHz mono audio to log-mel spectrogram:

- Sample rate: 16000 Hz
- Window: Hann 25 ms
- Stride: 10 ms
- FFT size: 512
- Mel bins: 128
- Log scaling

See the companion repo for a C# MelSpectrogram implementation.

## Usage

### Python (ONNX Runtime)

```python
import onnxruntime as ort
import numpy as np

session = ort.InferenceSession("sortformer.onnx", providers=["CPUExecutionProvider"])

# mel: log-mel spectrogram [1, 128, mel_frames]
speaker_logits = session.run(None, {"processed_signal": mel})[0]
# speaker_logits shape: [1, diar_frames, 4]
```

### C# (ONNX Runtime)

```csharp
using var session = new InferenceSession("sortformer.onnx");
var inputs = new List<NamedOnnxValue>
{
    NamedOnnxValue.CreateFromTensor("processed_signal", melTensor),
};
using var results = session.Run(inputs);
var logits = results.First().AsTensor<float>();
// logits shape: [1, diar_frames, 4]
```

## Conversion

This model was exported from NeMo to ONNX using the scripts in the companion
repository: [nemotron-speech-csharp/DiarizationConverter](https://github.com/DimQ1/nemotron-speech-csharp/tree/master/DiarizationConverter)

```bash
python scripts/export_fp32_opset21.py
```

## References

- [NVIDIA Sortformer Paper](https://arxiv.org/abs/2501.08131)
- [NeMo Diarization Models](https://github.com/NVIDIA/NeMo)
- [ONNX Runtime](https://onnxruntime.ai/)
"""

CARD_INT8 = """---
license: cc-by-4.0
language:
  - en
  - multilingual
tags:
  - onnx
  - onnxruntime
  - sortformer
  - speaker-diarization
  - nemo
  - int8
  - quantized
  - matmul-nbits
inference: false
library_name: onnxruntime
datasets:
  - libricss
pipeline_tag: audio-classification
---

# Sortformer 4spk-v2 — ONNX INT8 (CPU, MatMulNBits)

INT8 quantized ONNX model of NVIDIA's **Sortformer 4spk-v2** speaker diarization.
Uses **MatMulNBits** (ORT ≥ 1.18) for real 8-bit weight compression.

## Model Details

- **Base model:** [nvidia/diar_streaming_sortformer_4spk-v2](https://huggingface.co/nvidia/diar_streaming_sortformer_4spk-v2)
- **Format:** ONNX opset 21, INT8 MatMulNBits (block_size=64, symmetric)
- **Parameters:** 117.7 M (weights: 8-bit)
- **File size:** 129 MB total (21 MB ONNX + 109 MB external data)
- **Quantizable MatMul nodes:** 265 (of 352)
- **Input:** `processed_signal` — log-mel spectrogram [B, 128, mel_frames]
- **Output:** `speaker_logits` — per-frame sigmoid probabilities [B, diar_frames, 4]

## Architecture

```
Audio (16 kHz mono) → STFT → 128 mel bins
  → ConformerEncoder (17 layers, d=512)
    → SortformerModules (4 speakers)
      → TransformerEncoder (18 layers, d=192)
        → Sigmoid → [B, diar_frames, 4] speaker logits
```

All 265 constant-weight MatMul nodes replaced with **MatMulNBits (INT8, block_size=64)**.
87 dynamic MatMul nodes (attention scores) remain in FP32.

## Performance (CPU, 12 threads)

| Metric | Value |
|---|---|
| **RTF**  | 0.034 (29.4× real-time) |
| **DER**  | 21.64% (LibriCSS test set) |
| **Size** | 129 MB (3.6× smaller than FP32) |
| **cos_sim vs FP32** | 0.989–0.999 |

## Requirements

- ONNX Runtime ≥ 1.18 (MatMulNBits support)
- CPU Execution Provider

## Usage

### Python

```python
import onnxruntime as ort

session = ort.InferenceSession(
    "sortformer.onnx",
    providers=["CPUExecutionProvider"],
)
speaker_logits = session.run(None, {"processed_signal": mel})[0]
```

The `.data` file must be in the same directory as `sortformer.onnx`.

### C#

```csharp
using var session = new InferenceSession("sortformer.onnx");
var inputs = new List<NamedOnnxValue>
{
    NamedOnnxValue.CreateFromTensor("processed_signal", melTensor),
};
using var results = session.Run(inputs);
```

## Conversion

Quantized from FP32 ONNX using `MatMulNBitsQuantizer` (ORT 1.23+):

```bash
python scripts/quantize_nbits.py --int8
```

See [nemotron-speech-csharp/DiarizationConverter](https://github.com/DimQ1/nemotron-speech-csharp/tree/master/DiarizationConverter)

## References

- [NVIDIA Sortformer Paper](https://arxiv.org/abs/2501.08131)
- [ONNX Runtime MatMulNBits](https://onnxruntime.ai/docs/performance/model-optimizations/quantization.html#matmulnbits-quantizer)
"""

CARD_INT4 = """---
license: cc-by-4.0
language:
  - en
  - multilingual
tags:
  - onnx
  - onnxruntime
  - sortformer
  - speaker-diarization
  - nemo
  - int4
  - quantized
  - matmul-nbits
inference: false
library_name: onnxruntime
datasets:
  - libricss
pipeline_tag: audio-classification
---

# Sortformer 4spk-v2 — ONNX INT4 (CPU, MatMulNBits)

INT4 quantized ONNX model of NVIDIA's **Sortformer 4spk-v2** speaker diarization.
Uses **MatMulNBits** (ORT ≥ 1.18) for 4-bit weight compression with the **best accuracy-to-size ratio**.

## Model Details

- **Base model:** [nvidia/diar_streaming_sortformer_4spk-v2](https://huggingface.co/nvidia/diar_streaming_sortformer_4spk-v2)
- **Format:** ONNX opset 21, INT4 MatMulNBits (block_size=32, symmetric)
- **Parameters:** 117.7 M (weights: 4-bit)
- **File size:** 135 MB total (21 MB ONNX + 115 MB external data)
- **Quantizable MatMul nodes:** 265 (of 352)
- **Input:** `processed_signal` — log-mel spectrogram [B, 128, mel_frames]
- **Output:** `speaker_logits` — per-frame sigmoid probabilities [B, diar_frames, 4]

## Architecture

```
Audio (16 kHz mono) → STFT → 128 mel bins
  → ConformerEncoder (17 layers, d=512)
    → SortformerModules (4 speakers)
      → TransformerEncoder (18 layers, d=192)
        → Sigmoid → [B, diar_frames, 4] speaker logits
```

All 265 constant-weight MatMul nodes replaced with **MatMulNBits (INT4, block_size=32)**.
The finer block_size=32 gives **better accuracy than INT8** despite 4-bit weights.
87 dynamic MatMul nodes (attention scores) remain in FP32.

## Performance (CPU, 12 threads)

| Metric | Value |
|---|---|
| **RTF**  | 0.043 (23× real-time) |
| **DER**  | 19.11% (LibriCSS test set) |
| **Size** | 135 MB (3.5× smaller than FP32) |
| **cos_sim vs FP32** | 0.996–0.999 |

> ⚡ **INT4 is more accurate than INT8** (DER 19.11% vs 21.64%) due to finer block_size=32
> quantization granularity. Best choice for production.

## Requirements

- ONNX Runtime ≥ 1.18 (MatMulNBits support)
- CPU Execution Provider

## Usage

### Python

```python
import onnxruntime as ort

session = ort.InferenceSession(
    "sortformer.onnx",
    providers=["CPUExecutionProvider"],
)
speaker_logits = session.run(None, {"processed_signal": mel})[0]
```

The `.data` file must be in the same directory as `sortformer.onnx`.

### C#

```csharp
using var session = new InferenceSession("sortformer.onnx");
var inputs = new List<NamedOnnxValue>
{
    NamedOnnxValue.CreateFromTensor("processed_signal", melTensor),
};
using var results = session.Run(inputs);
```

## Conversion

Quantized from FP32 ONNX using `MatMulNBitsQuantizer` (ORT 1.23+):

```bash
python scripts/quantize_nbits.py --int4
```

See [nemotron-speech-csharp/DiarizationConverter](https://github.com/DimQ1/nemotron-speech-csharp/tree/master/DiarizationConverter)

## References

- [NVIDIA Sortformer Paper](https://arxiv.org/abs/2501.08131)
- [ONNX Runtime MatMulNBits](https://onnxruntime.ai/docs/performance/model-optimizations/quantization.html#matmulnbits-quantizer)
"""

CARDS = {
    "fp32": CARD_FP32,
    "int8": CARD_INT8,
    "int4": CARD_INT4,
}


def upload_repo(api: HfApi, repo_id: str, model_dir: Path, files: list[str], label: str, readme_text: str):
    """Create/update HF repo and upload all files."""
    print(f"\n{'=' * 60}")
    print(f"  {label}: {repo_id}")
    print(f"{'=' * 60}")

    # Create repo (idempotent)
    try:
        create_repo(repo_id, exist_ok=True, token=os.environ.get("HUGGINGFACE_TOKEN"))
        print(f"  ✅ Repo ready: https://huggingface.co/{repo_id}")
    except Exception as e:
        print(f"  ⚠️  Repo creation: {e}")

    # Upload README first
    readme_path = model_dir / "README_UPLOAD.md"
    readme_path.write_text(readme_text, encoding="utf-8")
    try:
        upload_file(
            path_or_fileobj=str(readme_path),
            path_in_repo="README.md",
            repo_id=repo_id,
            token=os.environ.get("HUGGINGFACE_TOKEN"),
        )
        print(f"  ✅ README.md uploaded")
    except Exception as e:
        print(f"  ❌ README.md: {e}")
    readme_path.unlink()

    # Upload model files
    for filename in files:
        filepath = model_dir / filename
        if not filepath.exists():
            print(f"  ❌ {filename}: NOT FOUND at {filepath}")
            continue

        size_mb = filepath.stat().st_size / (1024 * 1024)
        print(f"  ⬆️  {filename} ({size_mb:.1f} MB)...", end=" ", flush=True)

        t_start = time.perf_counter()
        try:
            upload_file(
                path_or_fileobj=str(filepath),
                path_in_repo=filename,
                repo_id=repo_id,
                token=os.environ.get("HUGGINGFACE_TOKEN"),
            )
            elapsed = time.perf_counter() - t_start
            speed = size_mb / elapsed if elapsed > 0 else 0
            print(f"done ({elapsed:.0f}s, {speed:.1f} MB/s)")
        except Exception as e:
            print(f"FAILED: {e}")
            return False

    print(f"  ✅ {label} upload complete")
    return True


def main():
    token = os.environ.get("HUGGINGFACE_TOKEN")
    if not token:
        print("ERROR: HUGGINGFACE_TOKEN not set.")
        print("Run: $env:HUGGINGFACE_TOKEN='hf_...'")
        sys.exit(1)

    api = HfApi(token=token)

    # Verify user
    try:
        who = api.whoami()
        print(f"Authenticated as: {who.get('name', 'unknown')}")
    except Exception as e:
        print(f"Auth check failed: {e}")
        sys.exit(1)

    success = True
    for repo_spec in REPOS:
        label = repo_spec["label"].lower()
        card_text = CARDS.get(label, CARD_FP32)
        ok = upload_repo(
            api=api,
            repo_id=repo_spec["repo_id"],
            model_dir=repo_spec["model_dir"],
            files=repo_spec["files"],
            label=repo_spec["label"],
            readme_text=card_text,
        )
        if not ok:
            success = False

    if success:
        print(f"\n{'=' * 60}")
        print(f"✅ All models published!")
        for r in REPOS:
            print(f"   https://huggingface.co/{r['repo_id']}")
        print(f"{'=' * 60}")
    else:
        print(f"\n⚠️  Some uploads failed. Check errors above.")
        sys.exit(1)


if __name__ == "__main__":
    main()
