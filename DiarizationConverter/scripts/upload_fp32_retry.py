"""Retry FP32 upload with large-file settings."""
import os, sys, time
from pathlib import Path
from huggingface_hub import HfApi, create_repo, upload_file

TOKEN = os.environ.get("HUGGINGFACE_TOKEN")
if not TOKEN:
    print("ERROR: HUGGINGFACE_TOKEN not set")
    sys.exit(1)

REPO_ID = "DimQ1/sortformer-4spk-v2-onnx-fp32-cpu"
ROOT = Path(__file__).resolve().parent.parent
MODEL_PATH = ROOT / "models" / "sortformer_fp32" / "sortformer.onnx"

api = HfApi(token=TOKEN)

# Ensure repo exists
create_repo(REPO_ID, exist_ok=True, token=TOKEN)
print(f"Repo: https://huggingface.co/{REPO_ID}")

# Upload the README first
README_TEXT = """---
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

## Performance (CPU, 12 threads, ORT 1.27.1)

| Metric | Value |
|---|---|
| **RTF**  | 0.037 (27× real-time) |
| **DER**  | 17.82% (LibriCSS test set) |
| **Size** | 469 MB |

## Usage

### Python (ONNX Runtime)

```python
import onnxruntime as ort
import numpy as np

session = ort.InferenceSession("sortformer.onnx", providers=["CPUExecutionProvider"])
mel = np.random.randn(1, 128, 500).astype(np.float32)  # 5s @ 10ms stride
speaker_logits = session.run(None, {"processed_signal": mel})[0]
# shape: [1, 63, 4]
```

### C# (.NET 10)

```csharp
using var session = new InferenceSession("sortformer.onnx");
var inputs = new List<NamedOnnxValue>
{
    NamedOnnxValue.CreateFromTensor("processed_signal", melTensor),
};
using var results = session.Run(inputs);
var logits = results.First().AsTensor<float>();
```

## Conversion

Exported from NeMo to ONNX using [DiarizationConverter](https://github.com/DimQ1/nemotron-speech-csharp/tree/master/DiarizationConverter):

```bash
python scripts/export_fp32_opset21.py
```

## Quantized Variants

| Variant | Size | DER | RTF | Link |
|---|---|---|---|---|
| **INT8** | 129 MB | 21.64% | 0.034 | [sortformer-4spk-v2-onnx-int8-cpu](https://huggingface.co/DimQ1/sortformer-4spk-v2-onnx-int8-cpu) |
| **INT4** | 135 MB | 19.11% | 0.043 | [sortformer-4spk-v2-onnx-int4-cpu](https://huggingface.co/DimQ1/sortformer-4spk-v2-onnx-int4-cpu) |

## References

- [NVIDIA Sortformer Paper](https://arxiv.org/abs/2501.08131)
- [NeMo Diarization Models](https://github.com/NVIDIA/NeMo)
- [ONNX Runtime](https://onnxruntime.ai/)
"""

# Upload README
readme_path = ROOT / "models" / "sortformer_fp32" / "README_UPLOAD.md"
readme_path.write_text(README_TEXT, encoding="utf-8")
upload_file(
    path_or_fileobj=str(readme_path),
    path_in_repo="README.md",
    repo_id=REPO_ID,
    token=TOKEN,
)
print("✅ README.md uploaded")
readme_path.unlink()

# Upload model with large-file support
size_mb = MODEL_PATH.stat().st_size / (1024 * 1024)
print(f"⬆️  sortformer.onnx ({size_mb:.1f} MB)...", end=" ", flush=True)

t_start = time.perf_counter()
try:
    # Use upload with commit API for large files
    api.upload_file(
        path_or_fileobj=str(MODEL_PATH),
        path_in_repo="sortformer.onnx",
        repo_id=REPO_ID,
        token=TOKEN,
        # HuggingFace Hub auto-detects large files and uses chunked upload
    )
    elapsed = time.perf_counter() - t_start
    speed = size_mb / elapsed if elapsed > 0 else 0
    print(f"done ({elapsed:.0f}s, {speed:.1f} MB/s)")
    print(f"\n✅ FP32 upload complete!")
    print(f"   https://huggingface.co/{REPO_ID}")
except Exception as e:
    print(f"FAILED: {e}")
    print("\nTrying alternative: huggingface-cli upload...")
    import subprocess
    result = subprocess.run(
        ["huggingface-cli", "upload", REPO_ID, str(MODEL_PATH), "sortformer.onnx"],
        capture_output=True, text=True, env={**os.environ, "HF_TOKEN": TOKEN}
    )
    print(result.stdout[-500:] if result.stdout else "")
    if result.returncode != 0:
        print(result.stderr[-500:] if result.stderr else "")
        sys.exit(1)
    print("\n✅ FP32 upload complete via CLI!")
