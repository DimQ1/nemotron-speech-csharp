# Publishing the diarization ONNX files to Hugging Face (not done yet)

Nothing here has been run. The ONNX export lives in the gitignored `models/nemotron-3-diarization-onnx/`.
This page lists exactly what to upload and how the app's catalog will consume it.

## Files to upload

From `models/nemotron-3-diarization-onnx/`:

| File | Bytes | Needed by |
|---|---|---|
| `diar_frontend.onnx` | 1,186,102 | both variants (log-mel front-end) |
| `diar_config.json` | 13,184 | both variants (geometry, AOSC constants, silence embedding) |
| `diar_step.int8.onnx` | 104,653,656 | int8 variant (recommended, default) |
| `diar_step.onnx` | 397,841,111 | fp32 variant (reference) |

Download sizes: int8 variant 105,852,942 bytes, fp32 variant 399,040,397 bytes (`DiarizationModelLocator.Int8DownloadBytes` / `Fp32DownloadBytes`).

## Repository layout

Same convention as `DimQ1/parakeet-tdt-0.6b-v3-onnx`: one repo, one sub-folder per precision, each folder self-contained.
`ModelDescriptor.SubfolderName` then yields the local folder names the library already looks for
(`{repoName}-{QuantizationFolder}`).

```
<user>/nemotron-3-diarization-onnx            # repo id: replace <user>
  README.md                                    # model card, see below
  LICENSE                                      # OpenMDW-1.1 text, copied from nvidia/Nemotron-3-Diarization
  int8/diar_step.int8.onnx
  int8/diar_frontend.onnx
  int8/diar_config.json
  fp32/diar_step.onnx
  fp32/diar_frontend.onnx
  fp32/diar_config.json
```

Local folders after download: `nemotron-3-diarization-onnx-int8` and `nemotron-3-diarization-onnx-fp32`.
`DiarizationModelLocator.Find` also accepts the bare `nemotron-3-diarization-onnx` folder (everything in one folder, what the export script writes).

## Commands (run by the user, after `huggingface-cli login`)

PowerShell, from the repo root. The staging folder keeps the two variants separate without duplicating the export on disk
permanently (it is 106 MB + 399 MB + 2 x 1.2 MB of copies, delete it afterwards).

```powershell
$src = "models/nemotron-3-diarization-onnx"
$stage = "models/_diar-hf-stage"
New-Item -ItemType Directory -Force "$stage/int8", "$stage/fp32" | Out-Null
Copy-Item "$src/diar_step.int8.onnx", "$src/diar_frontend.onnx", "$src/diar_config.json" "$stage/int8/"
Copy-Item "$src/diar_step.onnx",      "$src/diar_frontend.onnx", "$src/diar_config.json" "$stage/fp32/"
# README.md and LICENSE go next to the folders (see "Model card" below)

huggingface-cli repo create nemotron-3-diarization-onnx --type model
huggingface-cli upload <user>/nemotron-3-diarization-onnx $stage . --repo-type model `
    --commit-message "ONNX export of nvidia/Nemotron-3-Diarization (fp32 + dynamic int8)"
```

Single-command alternative without staging (uploads only the files, but flat: the app would need a flat-layout catalog entry):

```powershell
huggingface-cli upload <user>/nemotron-3-diarization-onnx models/nemotron-3-diarization-onnx . `
    --include "diar_frontend.onnx" "diar_config.json" "diar_step.int8.onnx" "diar_step.onnx"
```

## Model card must say

* Derived from [nvidia/Nemotron-3-Diarization](https://huggingface.co/nvidia/Nemotron-3-Diarization) (HF Transformers implementation), license **OpenMDW-1.1**; keep the license file and the attribution.
* Streaming Sortformer, up to 8 speakers, 16 kHz mono input, 10 ms output frames, sigmoid activity per speaker.
* Three graphs per variant: `diar_frontend.onnx` (log-mel, no STFT op), `diar_step.*.onnx` (stateless transformer step), `diar_config.json`; the speaker-cache (AOSC) update is host code (`SpeechLib.Diarization`).
* int8 is dynamic QInt8 MatMul quantization; DER within +-0.5 abs of fp32 on the spike's test sets (see `README.md` in this folder).
* How it was converted: `tools/converters/NemotronDiarization/export_onnx.py`, torch 2.14.1, transformers 5.19.0.dev0, onnx 1.23.2, opset 17.
* Tested with ONNX Runtime 1.30 CPU (win-x64). Not verified on phones.

## What the model catalog needs (do not edit `ModelCatalog` until the repo exists)

`ModelCatalog.Models` is a list of ASR `ModelDescriptor`s with WER/speed research metrics, which do not apply here. Suggested minimal change,
for whoever wires the UI (Models, Speakers tab):

1. A separate list, for example `ModelCatalog.DiarizationModels`, or a `ModelKind { Asr, Diarization }` on `ModelDescriptor` so the ASR pickers keep ignoring it.
2. Two entries with the same `RepoId` (`<user>/nemotron-3-diarization-onnx`):
   * `QuantizationFolder: "int8"`, `Precision: ModelPrecision.Int8`, `SizeBytes: DiarizationModelLocator.Int8DownloadBytes` (105,852,942), `IsRecommended: true`.
   * `QuantizationFolder: "fp32"`, `Precision: ModelPrecision.Fp32`, `SizeBytes: DiarizationModelLocator.Fp32DownloadBytes` (399,040,397).
   * `Research`: none (use a diarization metrics record with DER on the synthetic CV17 set, 10.5 / 10.0 for low_latency int8 / fp32, or leave it out); `ContextWindow: null`.
3. The download manager fetches every file of the sub-folder, so no file list is needed; after the download, `DiarizationModelLocator.Validate(folder)` checks presence and sizes and throws a descriptive `DiarizationModelException`.
4. Show the license (`DiarizationModelLocator.License`, `UpstreamRepo`) in the card, as the model card requires.
5. Folder resolution in the app: `DiarizationModelLocator.Find(new[] { modelsRoot })` returns the descriptor (precision chosen automatically, int8 first) or null when nothing is installed.
