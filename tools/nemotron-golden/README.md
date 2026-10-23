# Nemotron golden oracle: how it was made and how to regenerate it

The Nemotron recognizer used to run on **ONNX Runtime GenAI 0.16.0**. Before that dependency was deleted, the token streams
GenAI produced for a fixed set of audio clips were recorded. They are the permanent parity oracle of the plain ONNX Runtime
recognizer (`libraries/SpeechLib/src/SpeechLib.Providers/Nemotron`): a change to the mel front end, the encoder loop, the
RNN-T search or the silence drop must not change a single token against them.

| what | where | in git |
|---|---|---|
| the recorded token streams (JSON) | `libraries/SpeechLib/tests/SpeechLib.Nemotron.Tests/Data/golden-full/` (data README there) | yes, 0.9 MB, 267 files |
| a small slice of them that always runs | `.../Data/golden/` (47 files, 49 KB) | yes |
| the programs and scripts that produced them | this folder | yes |
| the audio the goldens refer to | not in git (see "Audio" below) | **no**, about 19 MB generated + Common Voice + SAPI clips |
| the GenAI packages the harness needs | nuget.org | removed from the product |

**This folder is not built.** It is not part of `NemotronSpeech.slnx`, no project references it, and `genai_oracle.cs` needs
the `Microsoft.ML.OnnxRuntimeGenAI` 0.16.0 NuGet package, which the product no longer uses. `tools/scripts/check-no-genai.ps1`
and `Unit_AssemblyLayeringTests` look at restore state, build output and product assemblies, so keeping this folder does not
trip them. Do not add it to the solution and do not copy the package reference into a product project.

## How the oracle was produced

* ONNX Runtime GenAI **0.16.0** (managed `Microsoft.ML.OnnxRuntimeGenAI` 0.16.0, native `onnxruntime-genai.dll` 0.16.0) on top of
  ONNX Runtime **1.30.0.20260909.8** (commit `f2c39fe`, the same ORT the repository used for Parakeet), CPU provider, Windows x64
  (UCRT math library; this matters for the mel front end, see the data README), .NET 10 file-based app, SDK 11.0.100-preview.5.
* Models: `models/asr/nemotron-3.5-asr-streaming-0.6b-onnx-{int4,fp32}-{c056,c112}-cpu` (chunk 8960 / 17920 samples).
* Session options exactly as the apps applied them (the former `GenAiSessionConfig`): `intra_op_num_threads` 4 for the golden runs
  (1 and 12 were compared and gave identical token ids), `inter_op_num_threads` 1, thread spinning off, `ORT_SEQUENTIAL`,
  `config.ClearProviders()`, `use_vad=false` except for the VAD scenarios. `repetition_penalty`, `num_beams`, `do_sample` are
  ignored by the transducer path.
* One fresh `StreamingProcessor` + `Generator` + `TokenizerStream` per clip and per feeding pattern. Language through
  `Generator.SetRuntimeOption("lang_id", id)` (en `"0"`, ru `"11"`, auto `"101"` only for `gen-concat-mix-auto`).
* Every clip was fed aligned (one chunk per call), in 3200-sample pieces and in an irregular cycle; the three patterns gave
  identical token ids in all 420 comparisons.

`genai_oracle.cs` has three commands: `golden` (token streams), `vad` (native `use_vad=true` scenarios: dropped chunks and tokens)
and `perf` (wall time of the GenAI path; the numbers are in `perf/genai-perf-baseline.md`, taken on a shared machine, so treat them as
upper bounds).

## Audio

The goldens name their audio in `Data/golden-full/clips.json` (id, root, relative path, sha256 of the file and of the PCM data).
Two roots: `TestAudio` (the gitignored `Test-Audio` folder) and `golden` (the folder you generate here).

| clips | source | reproducible |
|---|---|---|
| `cv17-{en,ru}-0001..0040` and the two quiet ones (`cv17-en-0086`, `cv17-ru-0069`) | `python tools/eval/download_cv_test.py --lang en --count 250` (and `ru`) into `Test-Audio/cv17`; Common Voice 17 test clips, CC0 | yes, from the HF mirror; sha256 in `clips.json` |
| `cv17hard-en-0051`, `cv17long-en-0001`, `cv17longnorm-en-0001`, `cv17short-en-0001`, `tts-long-en-0001`, `tts-ru-ru-0001/0002` | the maintainer's local `Test-Audio/{cv17-hard,cv17-long,cv17-long-norm,cv17-short,tts-long,tts-ru}` folders | **no**: not produced by any script in the repository. These rows (17 of the 210) are skipped when the folders are absent |
| `gen-*` (concatenations with room tone, exact-length edge cases, quiet copies, silence, VAD scenarios) | `gen_audio.cs` | **bit exact**: integer arithmetic on int16 samples of the Common Voice clips, room tone from a fixed-seed xorshift32 stream; sha256 of every output in `generated_audio.json` |
| `gen-vad-a4-exact` | `gen_a4_exact.cs` (`System.Random(7)` + Box-Muller, float32) | yes on .NET (the `Random(7)` sequence is part of its contract) |
| `sample-0-mp3` | `Mp3Decode` (the repository's `AudioUtils.LoadFile`: Media Foundation + `StreamingResampler`) of `Test-Audio/sample-0.mp3` | Windows only (Media Foundation decodes ~69 ms more than an ideal resampler) |
| `truth-{en,ru}-0001..0010` (SAPI text-to-speech clips of the word-timing study) | Windows voices Zira / Mark / Irina / Pavel | **no**: depends on the installed voices. These rows (20 on int4-c056, 6 on int4-c112) are skipped when the clips are absent |

So in a fresh clone with the download script, `gen_audio.cs` and `Mp3Decode` done, 43 of the 210 clip rows and 2 of the 48 VAD rows
(`tts-ru-ru-0001`) are skipped; the other 167 clip rows and 46 VAD rows replay.

```
# generated audio (sha256 per file in generated_audio.json)
dotnet run tools/nemotron-golden/gen_audio.cs -- <repo>/Test-Audio <golden-audio>
dotnet run tools/nemotron-golden/gen_a4_exact.cs -- <repo>/Test-Audio <golden-audio>/gen-vad-a4-exact.f32.wav
dotnet run --project tools/nemotron-golden/Mp3Decode -c Release -p:RepoRoot=<repo> -- <repo>/Test-Audio/sample-0.mp3 <golden-audio>/sample-0.f32.wav
```

## Replaying the full set against the current recognizer

```
export NEMOTRON_FULL_GOLDEN=1                     # the complete oracle in Data/golden-full (PowerShell: $env:NEMOTRON_FULL_GOLDEN='1')
export NEMOTRON_GOLDEN_AUDIO=<golden-audio>       # the generated wavs, sample-0.f32.wav, optional truth/ clips
export SPEECHLIB_TEST_AUDIO=<repo>/Test-Audio     # Common Voice clips (found automatically when Test-Audio is in the checkout)
dotnet test libraries/SpeechLib/tests/SpeechLib.Nemotron.Tests -c Release
```

The four model folders are found under `models/asr` (or `NEMOTRON_MODEL_DIR_{INT4,FP32}_{C056,C112}`). With every model and all
audio present the run is 950+ tests and about an hour. A row whose model or audio is missing is reported as a **skipped test
with the reason**, never dropped; the test `E2E_NemotronGoldenInventoryTests` fails when `NEMOTRON_FULL_GOLDEN` or
`NEMOTRON_GOLDEN_DIR` is set but no row can run. A golden file that cannot be read fails `Unit_GoldenFilesTests` and the discovery of
the theories.

## Regenerating the goldens themselves (only with GenAI 0.16.0 available)

You normally never need this: the JSON is the oracle. To record a new golden (a new language, `auto`, another clip) you need the
package from nuget.org (the file-based app restores it through its `#:package` line), the model folders and the audio:

```
M=<repo>/models/asr; R="--root TestAudio=<repo>/Test-Audio --root golden=<golden-audio>"
python tools/nemotron-golden/make_clips_manifest.py <repo>/Test-Audio <golden-audio> <out>/nemotron            # clips.json + vad_scenarios.json
dotnet run tools/nemotron-golden/genai_oracle.cs -- golden --model $M/nemotron-3.5-asr-streaming-0.6b-onnx-int4-c056-cpu --variant int4-c056 \
    --clips <out>/nemotron/clips.json --set primary --out <out>/nemotron/int4-c056 $R --threads 4
#   --variant int4-c112 --set c112 | --variant fp32-c056 --set fp32 | --variant fp32-c112 --set fp32   (--only id,id for single clips)
dotnet run tools/nemotron-golden/genai_oracle.cs -- vad --model ... --variant int4-c056 --scenarios <out>/nemotron/vad_scenarios.json --out <out>/nemotron/vad/int4-c056 $R
python tools/nemotron-golden/check_goldens.py <out>/nemotron '' <out>/nemotron/index.json                         # consistency + index
python tools/nemotron-golden/compare_threads.py <out>/nemotron <out>/nemotron/checks/threads-sensitivity.json     # after --threads 1 / 12 runs into checks/threads{1,12}/<variant>
```

`dotnet run x.cs` from the same path while another instance runs the same file fails to relink the dll: copy the file for parallel
runs. One process per variant needs about 1 GB (INT4) or 2.6 GB (FP32).

## How the harness itself was validated

* Identical to an earlier, independent oracle run for the 19 overlapping files (10 on c056, 9 on c112), token for token.
* Identical to the real `ModelSession` class of the pre-migration repository (`ModelSessionCheck`, needs a checkout before the GenAI
  removal such as commit `389560c`, `-p:RepoRoot=<that checkout>`; the result is in `Data/golden-full/checks/modelsession-fidelity-int4-c056.txt`): 16 clips, per-chunk text and token
  counts, and the text through the `IStreamingSpeechRecognizer` interface with 3200-sample batches.
* The VAD scenario on the earlier VAD experiment's input reproduces its logged dropped chunks (c056: 18-30 of 46).
* Every JSON passes `check_goldens.py` (text equals joined pieces, token count equals ids, ids below 13087, consecutive chunk
  indexes, chunk count equals ceil(samples / chunk), all feeding patterns identical).
