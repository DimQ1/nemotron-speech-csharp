# Diarization ONNX Converter

Convert NVIDIA NeMo **Sortformer 4spk-v2** (117.7M params) speaker diarization model to ONNX format at 3 precision levels: FP32, INT8, INT4. Includes a 100-file LibriCSS test dataset and a C# test app for evaluation.

**Published on HuggingFace:** [DimQ1/sortformer-4spk-v2-onnx-*](https://huggingface.co/DimQ1)

## Benchmark Results (ORT 1.27.1, CPU 12 threads, 100 files / 1954.8 s audio)

| Precision | Size | RTF | Speed | DER |
|---|---|---|---|---|
| **FP32** | 469 MB | 0.037 | 27× real-time | 17.82% |
| **INT8** | 129 MB | 0.034 | 29× real-time | 21.64% |
| **INT4** | 135 MB | 0.043 | 23× real-time | 19.11% |

> INT4 uses finer block_size=32 → **better accuracy than INT8** (19.11% vs 21.64% DER).

## Prerequisites

- Python 3.10+ with `pip`
- .NET 10 SDK
- ~10 GB free disk space (models + dataset)

## Quick Start

```powershell
# 1. Install Python dependencies
cd DiarizationConverter
pip install -r requirements.txt

# 2. Download Sortformer model from HuggingFace
python scripts/download_model.py

# 3. Export to ONNX FP32 (opset 21)
python scripts/export_fp32_opset21.py

# 4. Quantize to INT8/INT4 (MatMulNBits, real weight compression)
python scripts/quantize_nbits.py          # both INT8 + INT4
python scripts/quantize_nbits.py --int8   # INT8 only
python scripts/quantize_nbits.py --int4   # INT4 only

# 5. Download 100-file LibriCSS test dataset
python scripts/download_dataset.py

# 6. Verify ONNX models work
python scripts/verify_onnx.py

# 7. Run C# tests
dotnet run --project DiarizationTest -- --model fp32 --mode batch --metrics
dotnet run --project DiarizationTest -- --model int8 --mode batch --metrics
dotnet run --project DiarizationTest -- --model int4 --mode batch --metrics

# 8. GPU (DirectML) — requires ORT DirectML package
dotnet build DiarizationTest/DiarizationTest.csproj -c Release -p:GpuArch=DML
dotnet run --project DiarizationTest -- --model fp32 --mode batch --provider dml
```

## Scripts

| Script | Purpose |
|---|---|
| `download_model.py` | Download `nvidia/diar_streaming_sortformer_4spk-v2` from HuggingFace |
| `export_fp32_opset21.py` | Convert PyTorch → ONNX FP32 (opset 21, MatMulNBits-ready) |
| `quantize_nbits.py` | Quantize FP32 → INT4/INT8 via MatMulNBitsQuantizer (real compression) |
| `download_dataset.py` | Download 100 test audio files + RTTM labels (LibriCSS) |
| `verify_onnx.py` | Validate all 3 models, run inference on dataset |
| `upload_to_hf.py` | Upload models + READMEs to HuggingFace |
| `update_hf_readmes.py` | Update HuggingFace model cards (benchmarks, cross-refs) |

## HuggingFace Models

| Variant | Repo | Size | DER | RTF |
|---|---|---|---|---|
| **FP32** | [sortformer-4spk-v2-onnx-fp32-cpu](https://huggingface.co/DimQ1/sortformer-4spk-v2-onnx-fp32-cpu) | 469 MB | 17.82% | 0.037 |
| **INT8** | [sortformer-4spk-v2-onnx-int8-cpu](https://huggingface.co/DimQ1/sortformer-4spk-v2-onnx-int8-cpu) | 129 MB | 21.64% | 0.034 |
| **INT4** | [sortformer-4spk-v2-onnx-int4-cpu](https://huggingface.co/DimQ1/sortformer-4spk-v2-onnx-int4-cpu) | 135 MB | 19.11% | 0.043 |

## C# Test App

```
Usage: dotnet run -- [options]

Options:
  --model <fp32|int8|int4>    Model precision (required)
  --audio <path>              Single audio file to diarize
  --mode <single|batch>       single=one file, batch=all dataset files
  --dataset <path>            Dataset directory (default: ../dataset)
  --metrics                   Print DER metrics
  --provider <cpu|dml>        Execution provider (default: cpu)
  --help                      Show help
```

## Model Architecture

```
Audio (16 kHz mono) → STFT → 128 mel bins
  → ConformerEncoder (17 layers, d=512)
    → SortformerModules (4 speakers)
      → TransformerEncoder (18 layers, d=192)
        → Sigmoid → [B, diar_frames, 4] speaker logits
```

- **Parameters:** 117.7 M
- **Input:** `processed_signal` [B, 128, mel_frames] — log-mel spectrogram
- **Output:** `speaker_logits` [B, diar_frames, 4] — per-frame sigmoid probabilities
- **Diar frame rate:** 80 ms (10 ms mel stride / 8× subsampling)
- **Mel preprocessing:** 16kHz mono → Hann 25ms, stride 10ms, 512-FFT → 128 mel bins → log

## Quantization Details

| Property | INT4 | INT8 |
|---|---|---|
| **Method** | MatMulNBits | MatMulNBits |
| **Block size** | 32 | 64 |
| **Weight bits** | 4 | 8 |
| **Symmetric** | Yes | Yes |
| **Quantized nodes** | 265 / 352 | 265 / 352 |
| **Kept FP32** | 87 (attention) | 87 (attention) |
| **ORT required** | ≥ 1.18 | ≥ 1.18 |
| **ONNX opset** | 21 | 21 |

## Evaluation Metric

**DER (Diarization Error Rate)**: weighted sum of Missed Speech + False Alarm + Speaker Confusion errors, measured against RTTM ground truth.

## Compatibility

- Same .NET stack as NemotronSpeech: `net10.0`, `Microsoft.ML.OnnxRuntime`
- CPU: ORT 1.27.1 with `ORT_PARALLEL` + memory optimizations
- GPU: DirectML via ORT 1.24.4 (set `-p:GpuArch=DML` at build)
- DML + CPU fallback supported for unsupported ops
- NAudio for audio loading (same as SpeechLib)
