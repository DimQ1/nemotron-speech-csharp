# Golden token streams that always run (47 files)

A small slice of the oracle recorded from ONNX Runtime GenAI 0.16.0 (Windows x64, 4 threads): Common Voice 17 clips `en0001..0010`,
`ru0001..0010` (INT4 c056), a few for INT4 c112 and FP32, and 11 native-VAD scenarios (`*_vad-*.json`, INT4 only). The file name is
`<variant>_<clip>.json`; the audio is `cv17/<lang>/<number>.wav` of `Test-Audio` (`python tools/eval/download_cv_test.py`) or generated
scenario audio described in the file's `scenario` member (room tone from a fixed-seed generator, see `GoldenCase.SyntheticAudio`).

The complete oracle (210 clip runs and 48 VAD runs, all four model variants) is in `../golden-full` and is replayed with
`NEMOTRON_FULL_GOLDEN=1`. How both were recorded, how to rebuild the audio that is not in the repository and how to record new goldens:
`tools/nemotron-golden/README.md`. The file format is in `GoldenCase.cs` and `../golden-full/README.md`.

Every `*.json` in this folder is read as a golden: a file that cannot be read fails `Unit_GoldenFilesTests`. Do not put manifests or other
JSON here.
