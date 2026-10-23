# FastConformer encoder repack (Nemotron streaming, Parakeet TDT)

Makes the INT4 model folders of **Nemotron 3.5 ASR streaming** and **Parakeet TDT 0.6B v3** smaller by repacking
their encoder graph, without re-exporting the model (no PyTorch, no NeMo, no Olive): item C3 of
[`docs/research/onnxruntime-genai-ideas.md`](../../../docs/research/onnxruntime-genai-ideas.md).

Why: Olive's k-quant pass and onnxruntime's `MatMulNBitsQuantizer` quantise `MatMul` only. The 48 pointwise
(kernel 1) `Conv` layers of the conformer convolution modules (`pointwise_conv1` 2048x1024, `pointwise_conv2`
1024x1024 per layer) therefore stay FP32 in the shipped INT4 encoders: 302 MB of the ~690 MB of encoder weights.
The Nemotron streaming encoders also carry a 41 MB FP32 relative-position table of which one Slice reads 125 (c056)
or 139 (c112) rows, and recompute the 24 `linear_pos` projections of those constant rows on every chunk (ORT folds
the Slice when it loads the model, not the MatMulNBits after it).

## What the tool does

`repack_encoder.py` takes a model folder (shipped INT4 or FP32) and writes a new folder:

1. **Constant folds** (`--fold-pos full`, the default): the position-table Slice becomes the rows it reads, and every
   MatMul / MatMulNBits whose activation input is then a constant (the 24 `linear_pos` projections) becomes a
   constant. A MatMulNBits is evaluated with the same ORT CPU kernel the encoder uses at run time, so the fold is
   exact. `--fold-pos table` folds the Slice only, `none` keeps both. Parakeet has no constant table (its positions
   depend on the input length): nothing is folded there.
2. **Pointwise Conv -> MatMul**: each 1-D pointwise Conv becomes `Transpose -> MatMul [Cin, Cout] -> Transpose`;
   the transposes that undo the ones the exporter put around the conv module (`LayerNorm -> Transpose -> Conv` and
   `Conv -> Transpose -> Add`) are then cancelled, so the graph has the same number of transposes as before. The 1x1
   Conv2d layers of the subsampling stack (0.26 MB each) are kept unless `--include-2d`.
3. **Equivalence gate before quantising**: the rewritten graph (pointwise weights still FP32, folds applied) and the
   source encoder are run on real audio (Nemotron: chunk by chunk with the caches fed back, as `NemotronRnntEngine`
   does; Parakeet: whole utterances through the folder's own `nemo128.onnx`), and the relative RMS difference of all
   encoder outputs must stay under `--max-rel-rms` (default 1e-3) or nothing is written.
4. **INT4**: every FP32 MatMul with a constant 2-D weight becomes `MatMulNBits` (for a shipped INT4 source that is
   only the new pointwise weights; for an FP32 source it is the whole encoder). `--recipe match` (default) uses the
   scheme of the source graph's existing MatMulNBits: k-quant with zero points, block 32, `accuracy_level` 4 for
   Nemotron; symmetric, block 32, no `accuracy_level` for Parakeet. Other recipes: see below.
5. **Folder**: the new encoder (same file layout as the source: `encoder.onnx` + `encoder.onnx.data` for Nemotron,
   one `encoder-model.onnx` for the Parakeet INT4 folder) plus every other file of the source folder copied
   unchanged, and `repack-info.json` with the recipe, the graph census before / after, the equivalence numbers,
   the quantisation error of each new weight and the sizes.

### INT4 recipes (`quant_recipes.py`)

| `--recipe`    | zero points | implementation                                    | notes |
|---------------|-------------|---------------------------------------------------|-------|
| `k_quant`     | yes         | onnxruntime's INC `quant_tensor_k_quant_cpu`      | llama.cpp-style scale/min search per block; the scheme of the shipped Nemotron encoders (Olive `OnnxKQuantQuantization`) |
| `default_sym` | no          | onnxruntime C++ `quantize_matmul_4bits` (MLAS)    | `MatMulNBitsQuantizer` default; the scheme of the shipped Parakeet INT4 encoder |
| `default_asym`| yes         | onnxruntime C++ `quantize_matmul_4bits` (MLAS)    | min/max per block |
| `rtn_sym`     | no          | onnxruntime's INC `quant_tensor(sym)`             | scale = max abs / 7.5 (coarser) |
| `rtn_asym`    | yes         | onnxruntime's INC `quant_tensor(asym)`            | min/max per block |

`--block-size` (default: the source graph's, else 32) and `--accuracy-level` (default: the source graph's; 0 omits
the attribute) apply to the new MatMulNBits only; the existing ones of a shipped INT4 source are copied byte for byte.
HQQ (needs torch, float zero points) and upstream's `k_quant_mixed` (keeps attention and the first / last layers FP32,
i.e. a larger model) are not offered.

## Setup

From the repository root (Windows; on Linux / macOS use `venv/bin/python`):

```powershell
python -m venv work/encoder-repack-venv
work/encoder-repack-venv/Scripts/python -m pip install -r tools/converters/EncoderRepack/requirements.txt
```

`requirements.txt` pins onnxruntime 1.30.0, the version the C# recognizers use (`Microsoft.ML.OnnxRuntime`).

## Run

```powershell
$py = "work/encoder-repack-venv/Scripts/python"
$tool = "tools/converters/EncoderRepack"

# Nemotron c056 INT4: folds + pointwise INT4 with the scheme of the graph (k-quant, block 32, accuracy_level 4)
& $py $tool/repack_encoder.py `
    --model-dir models/asr/nemotron-3.5-asr-streaming-0.6b-onnx-int4-c056-cpu `
    --out-dir work/repacked/nemotron-3.5-asr-streaming-0.6b-onnx-int4-c056-cpu `
    --verify-audio Test-Audio/cv17/en Test-Audio/cv17/ru

# Parakeet TDT INT4 (symmetric, block 32, no accuracy_level, as the rest of its graph)
& $py $tool/repack_encoder.py `
    --model-dir models/asr/parakeet-tdt-0.6b-v3-onnx-int4 `
    --out-dir work/repacked/parakeet-tdt-0.6b-v3-onnx-int4 `
    --verify-audio Test-Audio/cv17/en Test-Audio/cv17/ru

# Folds only (exact: the encoder outputs stay bit-identical), or another recipe / block size
& $py $tool/repack_encoder.py --model-dir <folder> --out-dir <new folder> --no-pointwise --verify-audio Test-Audio/cv17/en
& $py $tool/repack_encoder.py --model-dir <folder> --out-dir <new folder> --recipe default_sym --verify-audio Test-Audio/cv17/en

# Whole-encoder re-quantisation from the FP32 folder (every MatMul + the pointwise convs), e.g. block 64
& $py $tool/repack_encoder.py `
    --model-dir models/asr/nemotron-3.5-asr-streaming-0.6b-onnx-fp32-c056-cpu `
    --out-dir work/repacked/nemotron-c056-kq64 --recipe k_quant --block-size 64 --accuracy-level 4 `
    --verify-audio Test-Audio/cv17/en
```

Memory: the encoder is read tensor by tensor (an FP32 source never sits in memory as a whole); the equivalence check
runs the source and the rewritten encoder one after the other (peak about one encoder session, ~1 GB for INT4,
~2.6 GB for FP32). Conversion of an INT4 folder takes 1-3 minutes.

### Helpers

- `encoder_check.py --reference <folder> --candidate <folder> --audio <wav or folder>...`: the comparison the gate
  uses, for any two folders of the same model (max / relative RMS difference, cosine, bit identity).
- `compare_recipes.py --fp32 <FP32 folder> [--shipped <INT4 folder>] [--pointwise]`: weight-level A/B of the recipes
  (relative RMS error of every weight after quantise + dequantise, block 32 vs 64) and a check whether a recipe
  reproduces the shipped INT4 encoder byte for byte.
- `bench_encoder.py --models <folder>... --audio <wav>`: interleaved encoder-only timing (ms per call, rotating order)
  and the session's private memory, with the apps' session options.

## Using a repacked folder

The C# recognizers load any folder, so a repacked folder is used like a shipped one, e.g.
`tools/WerEval nemotron <folder> Test-Audio/cv17 --streaming` or by pointing the apps' model path at it. Measure WER
before publishing anything: INT4 pointwise weights are not free (the repack report in the C3 entry of
`docs/research/onnxruntime-genai-ideas.md` has the numbers). `repack-info.json` is extra metadata; the recognizers
ignore it.
