# nvidia/NVIDIA-Nemotron-3.5-ASR-Streaming-Multilingual-0.6b

Olive recipes for [nvidia/NVIDIA-Nemotron-3.5-ASR-Streaming-Multilingual-0.6b](https://huggingface.co/nvidia/NVIDIA-Nemotron-3.5-ASR-Streaming-Multilingual-0.6b),
a 0.6B-parameter streaming multilingual ASR model covering 100+ languages.

## Performance

Multilingual evaluation is in progress on **FLEURS**, **Common Voice**,
**Multilingual LibriSpeech (MLS)**, and **VoxPopuli**. Per-language WER
numbers for the INT4 ONNX build will be published here once the matrix is
complete.

## Recipes

- [`src/`](./src) — ONNX export + INT4 / INT8 quantization for CPU and CUDA
  execution providers, plus homogeneous FP16 conversion for TensorRT RTX.

### TensorRT RTX FP16

GenAI 0.15 supports homogeneous FP16 I/O for Nemotron. Generate a separate
CUDA artifact with:

```powershell
python converter/src/optimize.py --encoder-precision fp16 --execution-provider cuda
```

The output is `converter/src/build/onnx_models_fp16_cuda/`. The pipeline
converts all three Nemotron components and validates their floating-point I/O;
it does not alter the existing INT4/INT8 artifacts.

See the README inside each subfolder for setup and run instructions.

## Inference

Streaming inference is supported via [`onnxruntime-genai`](https://github.com/microsoft/onnxruntime-genai),
with a per-utterance `--language` flag (or `auto`) that selects the encoder
prompt token. Example:

```bash
python onnxruntime-genai/examples/python/nemotron_speech.py \
    --model_path src/build/onnx_models_int4 \
    --audio_file path/to/audio.wav \
    --language de \
    -e cpu
```
