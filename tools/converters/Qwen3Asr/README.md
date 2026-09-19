# Qwen3-ASR ONNX INT4 tooling

This converter packages an existing FP32 Qwen3-ASR ONNX export for the C#
SpeechLib provider. It applies ONNX Runtime `MatMulNBits` RTN quantization to
`decoder_init.onnx` and `decoder_step.onnx` and shares their external weights
as `decoder_weights.int4.data`.

The encoder is deliberately copied as `encoder.int4.onnx` without weight
quantization. It is an FP32 encoder in the shipped CPU package because the
encoder is the dominant audio-side workload and FP16/INT4 conversion was not
validated as an accuracy-preserving CPU optimization. The generated
`config.json` records this as `int4_encoder_storage: fp32`.

## Usage

The input directory must contain the regular FP32 export:

```powershell
python tools/converters/Qwen3Asr/quantize_int4.py `
  --input output/qwen3-asr-1.7b `
  --output output/qwen3-asr-1.7b-int4 `
  --block-size 64 `
  --accuracy-level 4
```

The output retains tokenizer/configuration files and contains:

- `encoder.int4.onnx` (FP32 storage, compatibility name)
- `decoder_init.int4.onnx` and `decoder_step.int4.onnx` (`MatMulNBits` INT4)
- `decoder_weights.int4.data` (shared decoder weights)
- `embed_tokens.bin` and tokenizer files

Requires `onnx` and `onnxruntime` with `MatMulNBitsQuantizer` support. The
converter does not download the original Hugging Face checkpoint or export
the FP32 graphs; those are separate, model-specific steps.

## Block-streaming encoder export

The official Qwen3 encoder uses independent local attention blocks. To avoid
re-encoding an ever-growing audio buffer, specialize the existing encoder to
one complete attention block and let the runtime feed each block once:

```powershell
python tools/converters/Qwen3Asr/export_block_streaming.py `
  --input models/qwen3-asr-1.7b-onnx `
  --output models/qwen3-asr-1.7b-onnx-block-streaming `
  --block-seconds 2 `
  --window-seconds 16 `
  --force `
  --self-contained `
  --verify-equivalence
```

The exporter validates the graph with ONNX Runtime and writes
`encoder_stream.onnx` with a fixed `[1, 128, 200]` mel input and `[1, 26, 2048]`
feature output, plus a manifest. With `--self-contained`, the manifest uses
`base_model_dir: "."` and the output directory contains the encoder, decoder,
tokenizer, configuration, and embedding files together. Each block is encoded
once; the decoder still uses the bounded feature window for quality. This is
honest block streaming, not a fabricated recurrent cache: the trained Qwen
encoder has no cross-block state tensors. `--verify-equivalence` runs both
graphs on the same seeded 200-frame input and reports maximum and mean absolute
error plus cosine similarity.

The output feature count is calculated from the mel frame count using the
encoder's convolutional downsampling formula. For the production 2-second
profile, 200 mel frames produce 26 features. `window-seconds` must be an
integer multiple of `block-seconds` and must contain at least two blocks.

## Parameter and storage report

Generate a report for an existing package:

```powershell
python tools/converters/Qwen3Asr/report_parameters.py `
  --model-dir models/qwen3-asr-1.7b-onnx `
  --output build/wer-reports/qwen3-parameters.md
```

The report separates the dense logical parameter count from on-disk storage.
For INT4 graphs it restores each `MatMulNBits` weight to its dense `K x N`
shape, counts biases and other initializers, and removes the tied embedding
matrix once. This makes the result comparable with the architecture estimate
without confusing packed INT4 bytes with model parameters.