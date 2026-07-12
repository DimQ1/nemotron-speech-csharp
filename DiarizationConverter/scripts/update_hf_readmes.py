"""
Update all 3 HuggingFace model READMEs with latest ORT 1.27.1 benchmarks.
Ensures consistent formatting and cross-references across FP32/INT8/INT4 variants.
"""
import os, sys
from pathlib import Path
from huggingface_hub import HfApi

TOKEN = os.environ.get("HUGGINGFACE_TOKEN")
if not TOKEN:
    print("ERROR: HUGGINGFACE_TOKEN not set")
    sys.exit(1)

api = HfApi(token=TOKEN)

# ═══════════════════════════════════════════════════════════════════
# Shared YAML frontmatter parts
# ═══════════════════════════════════════════════════════════════════

HEADER_FP32 = """---
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
- **File size:** 469 MB (single-file ONNX)
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

Benchmarked on 100-file LibriCSS subset (1954.8 s audio). ORT 1.27.1 with `ORT_PARALLEL` + memory optimizations.

## Quantized Variants

| Variant | Size | DER | RTF | Link |
|---|---|---|---|---|
| **INT8** | 129 MB | 21.64% | 0.034 | [sortformer-4spk-v2-onnx-int8-cpu](https://huggingface.co/DimQ1/sortformer-4spk-v2-onnx-int8-cpu) |
| **INT4** | 135 MB | 19.11% | 0.043 | [sortformer-4spk-v2-onnx-int4-cpu](https://huggingface.co/DimQ1/sortformer-4spk-v2-onnx-int4-cpu) |

## Preprocessing

Before inference, convert raw 16 kHz mono audio to log-mel spectrogram:

- Sample rate: 16000 Hz
- Window: Hann 25 ms
- Stride: 10 ms
- FFT size: 512
- Mel bins: 128
- Log scaling

## Usage

### Python (ONNX Runtime)

```python
import onnxruntime as ort
import numpy as np

session = ort.InferenceSession("sortformer.onnx", providers=["CPUExecutionProvider"])

# mel: log-mel spectrogram [1, 128, mel_frames]
mel = np.random.randn(1, 128, 500).astype(np.float32)  # 5s @ 10ms stride
speaker_logits = session.run(None, {"processed_signal": mel})[0]
# shape: [1, 63, 4]
```

### C# (.NET 10, ONNX Runtime)

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

Exported from NeMo to ONNX (opset 21) using [DiarizationConverter](https://github.com/DimQ1/nemotron-speech-csharp/tree/master/DiarizationConverter):

```bash
python scripts/export_fp32_opset21.py
```

## References

- [NVIDIA Sortformer Paper](https://arxiv.org/abs/2501.08131)
- [NeMo Diarization Models](https://github.com/NVIDIA/NeMo)
- [ONNX Runtime](https://onnxruntime.ai/)
"""

HEADER_INT8 = """---
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
- **Quantizable MatMul nodes:** 265 of 352 (87 attention-score MatMuls kept FP32)
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

All 265 constant-weight MatMul nodes replaced with **MatMulNBits (INT8, block_size=64)**.
87 dynamic MatMul nodes (attention scores) remain in FP32.

## Performance (CPU, 12 threads, ORT 1.27.1)

| Metric | Value |
|---|---|
| **RTF**  | 0.034 (29.4× real-time) |
| **DER**  | 21.64% (LibriCSS test set) |
| **Size** | 129 MB (3.6× smaller than FP32) |
| **cos_sim vs FP32** | 0.989–0.999 |

Benchmarked on 100-file LibriCSS subset (1954.8 s audio). ORT 1.27.1 with `ORT_PARALLEL` + memory optimizations.

> 🚀 **INT8 is the fastest variant** — 29.4× real-time on 12-core CPU (66.6 s for 1954.8 s audio).

## Other Variants

| Variant | Size | DER | RTF | Link |
|---|---|---|---|---|
| **FP32** | 469 MB | 17.82% | 0.037 | [sortformer-4spk-v2-onnx-fp32-cpu](https://huggingface.co/DimQ1/sortformer-4spk-v2-onnx-fp32-cpu) |
| **INT4** | 135 MB | 19.11% | 0.043 | [sortformer-4spk-v2-onnx-int4-cpu](https://huggingface.co/DimQ1/sortformer-4spk-v2-onnx-int4-cpu) |

## Requirements

- ONNX Runtime ≥ 1.18 (MatMulNBits support)
- CPU Execution Provider
- `.data` file must be in the same directory as `sortformer.onnx`

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

Quantized from FP32 ONNX using `MatMulNBitsQuantizer` (ORT ≥ 1.18, opset 21):

```bash
python scripts/quantize_nbits.py --int8
```

See [nemotron-speech-csharp/DiarizationConverter](https://github.com/DimQ1/nemotron-speech-csharp/tree/master/DiarizationConverter)

## References

- [NVIDIA Sortformer Paper](https://arxiv.org/abs/2501.08131)
- [ONNX Runtime MatMulNBits](https://onnxruntime.ai/docs/performance/model-optimizations/quantization.html#matmulnbits-quantizer)
"""

HEADER_INT4 = """---
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
- **Quantizable MatMul nodes:** 265 of 352 (87 attention-score MatMuls kept FP32)
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

All 265 constant-weight MatMul nodes replaced with **MatMulNBits (INT4, block_size=32)**.
The finer block_size=32 gives **better accuracy than INT8** despite 4-bit weights.
87 dynamic MatMul nodes (attention scores) remain in FP32.

## Performance (CPU, 12 threads, ORT 1.27.1)

| Metric | Value |
|---|---|
| **RTF**  | 0.043 (23× real-time) |
| **DER**  | 19.11% (LibriCSS test set) |
| **Size** | 135 MB (3.5× smaller than FP32) |
| **cos_sim vs FP32** | 0.996–0.999 |

Benchmarked on 100-file LibriCSS subset (1954.8 s audio). ORT 1.27.1 with `ORT_PARALLEL` + memory optimizations.

> ⚡ **INT4 is more accurate than INT8** (DER 19.11% vs 21.64%) due to finer block_size=32
> quantization granularity. Best accuracy-to-size ratio for production.

## Other Variants

| Variant | Size | DER | RTF | Link |
|---|---|---|---|---|
| **FP32** | 469 MB | 17.82% | 0.037 | [sortformer-4spk-v2-onnx-fp32-cpu](https://huggingface.co/DimQ1/sortformer-4spk-v2-onnx-fp32-cpu) |
| **INT8** | 129 MB | 21.64% | 0.034 | [sortformer-4spk-v2-onnx-int8-cpu](https://huggingface.co/DimQ1/sortformer-4spk-v2-onnx-int8-cpu) |

## Requirements

- ONNX Runtime ≥ 1.18 (MatMulNBits support)
- CPU Execution Provider
- `.data` file must be in the same directory as `sortformer.onnx`

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

Quantized from FP32 ONNX using `MatMulNBitsQuantizer` (ORT ≥ 1.18, opset 21):

```bash
python scripts/quantize_nbits.py --int4
```

See [nemotron-speech-csharp/DiarizationConverter](https://github.com/DimQ1/nemotron-speech-csharp/tree/master/DiarizationConverter)

## References

- [NVIDIA Sortformer Paper](https://arxiv.org/abs/2501.08131)
- [ONNX Runtime MatMulNBits](https://onnxruntime.ai/docs/performance/model-optimizations/quantization.html#matmulnbits-quantizer)
"""

# ═══════════════════════════════════════════════════════════════════
# Upload
# ═══════════════════════════════════════════════════════════════════

REPOS = {
    "DimQ1/sortformer-4spk-v2-onnx-fp32-cpu": HEADER_FP32,
    "DimQ1/sortformer-4spk-v2-onnx-int8-cpu": HEADER_INT8,
    "DimQ1/sortformer-4spk-v2-onnx-int4-cpu": HEADER_INT4,
}

ROOT = Path(__file__).resolve().parent.parent
tmp = ROOT / "models" / "_readme_tmp.md"

for repo_id, text in REPOS.items():
    print(f"\n📝 Updating {repo_id}...")
    tmp.write_text(text, encoding="utf-8")
    api.upload_file(
        path_or_fileobj=str(tmp),
        path_in_repo="README.md",
        repo_id=repo_id,
        token=TOKEN,
    )
    print(f"   ✅ README.md updated")

tmp.unlink(missing_ok=True)
print("\n🎉 All 3 READMEs updated successfully!")
