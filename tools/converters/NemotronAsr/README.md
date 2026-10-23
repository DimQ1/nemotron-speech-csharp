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

GenAI 0.15 supports homogeneous FP16 I/O for Nemotron. (The product's own recognizer on plain
ONNX Runtime, `SpeechLib.Providers/Nemotron`, rejects float16 graphs with `NotSupportedException`, so this artifact
is only for running the model with the upstream GenAI example.) Generate a separate
CUDA artifact with:

```powershell
python tools/converters/NemotronAsr/src/optimize.py --encoder-precision fp16 --execution-provider cuda
```

The output is `tools/converters/NemotronAsr/src/build/onnx_models_fp16_cuda/`. The pipeline
converts all three Nemotron components and validates their floating-point I/O;
it does not alter the existing INT4/INT8 artifacts.

See the README inside each subfolder for setup and run instructions.

## Inference

The product (the C# apps and CLI of this repository) no longer uses ONNX Runtime GenAI: its Nemotron recognizer
(`libraries/SpeechLib/src/SpeechLib.Providers/Nemotron`) runs the exported folders on plain ONNX Runtime and reads
`genai_config.json` as a plain JSON manifest (the file name is inherited from the GenAI export format).

Standalone streaming inference of the exported model is also possible with the upstream
[`onnxruntime-genai`](https://github.com/microsoft/onnxruntime-genai) Python example, kept here as a reference only,
with a per-utterance `--language` flag (or `auto`) that selects the encoder
prompt token. Example:

```bash
python onnxruntime-genai/examples/python/nemotron_speech.py \
    --model_path src/build/onnx_models_int4 \
    --audio_file path/to/audio.wav \
    --language de \
    -e cpu
```
