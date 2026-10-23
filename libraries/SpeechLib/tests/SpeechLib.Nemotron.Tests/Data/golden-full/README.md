# Golden token streams of ONNX Runtime GenAI 0.16.0 (the full set)

The complete parity oracle of the Nemotron recognizer on plain ONNX Runtime: the token ids ORT GenAI 0.16.0 produced, chunk by
chunk, for 131 + 53 + 13 + 13 clip runs (INT4 / FP32, 560 ms / 1120 ms chunks) and 48 native-VAD runs. GenAI is no longer part of the
product, so **these files cannot be regenerated from the repository's code**: they are the oracle. How they were made, how to
rebuild their audio and how to record new ones: [`tools/nemotron-golden/README.md`](../../../../../../tools/nemotron-golden/README.md).

They are replayed only on request (`NEMOTRON_FULL_GOLDEN=1`), about an hour with all models and audio; the small slice in `Data/golden`
(47 files) runs in every `E2E_` run. The loader (`GoldenCase.cs`) reads every `*.json` below this folder except the three manifests and
`checks/`; an unreadable golden is an error, and a row whose model or audio is missing is a skipped test that names what is missing.

```
golden-full/
  clips.json                  the exact clip list: id, language id, audio root (TestAudio | golden) + relative path, set membership, sha256 of file and PCM data, duration
  vad_scenarios.json          the native-VAD scenarios (audio, feeding granularities)
  index.json                  fingerprint per variant+clip: token count, chunk count, sha256 prefix of the id stream, decoded text
  int4-c056/<clip>.json       131 clips (the primary set)
  int4-c112/<clip>.json        53 clips
  fp32-c056/<clip>.json        13 clips
  fp32-c112/<clip>.json        13 clips
  vad/<variant>/<scenario>.feed<N>.json    native use_vad=true runs (int4-c056, int4-c112); N = samples per Process() call (0 = chunk aligned)
  checks/                     evidence, not goldens: GenAI feeding quirks (quirks.<variant>.json), thread sensitivity, ModelSession fidelity, consistency
```

## Platform of the oracle

Recorded on Windows x64 with the UCRT math library. The recognizer reproduces every token of this set there, and its mel front end is bit
for bit the one GenAI fed the encoder (`Unit_NemotronStreamingMelTests`, Windows x64 only). On other platforms the mel is computed with that
platform's `logf`/`cosf`: on Linux x64 (glibc 2.39) 6 to 12 percent of the mel floats of the three dumped clips differ in the last bits, at most
7e-5 (73 ulp), and the tokens and text of 8 INT4 c056 clips were identical to these goldens. Replaying the whole set on another platform is the way
to learn whether a near-tie token flips there; it has not been done beyond those clips, and arm64, macOS, Android and iOS are unmeasured.

## What is in a golden

* Versions: managed `Microsoft.ML.OnnxRuntimeGenAI` 0.16.0, `onnxruntime-genai.dll` 0.16.0, `onnxruntime.dll` 1.30.0.20260909.8.
* Models: `nemotron-3.5-asr-streaming-0.6b-onnx-{int4,fp32}-{c056,c112}-cpu` (chunk_samples 8960 / 17920), CPU provider.
* Session options as the apps applied them: `intra_op_num_threads` 4, `inter_op_num_threads` 1, spinning off, ORT_SEQUENTIAL, `use_vad=false`
  (except the VAD runs). Token ids are identical for 1, 4 and 12 threads (checked on 16 clips).
* Language through `lang_id`: en `"0"`, ru `"11"`, auto `"101"` (only `gen-concat-mix-auto`); `langId` is stored in every JSON.
* One fresh stream per clip and per feeding pattern.

Per variant+clip JSON:

```
schema, variant, model, clip, audioPath ("<root>/<relative path>", root TestAudio | golden), chunkSamples, audioSamples, langId, vad=false, threads,
seconds        GenAI wall time of the aligned run (a loaded machine: NOT a performance number)
text           concatenation of the TokenizerStream pieces of the whole run (leading space of the first word, <en-US>/<ru-RU> tags included)
tokenCount
feedBatches    run-length list [[batchSamples, count], ...] of the Process() calls of the aligned run
flushPaddedSamples   zeros GenAI appended in Flush() (0 for an exact multiple of chunkSamples: Flush returns nothing)
chunks[]       one entry per Process() call, then (if Flush returned a chunk) {"feed":"flush", ...}
                 feed = call index, processed = Process() returned a chunk, chunk = chunk index (-1 for the flush entry), ids, pieces
otherFeedPatterns.{ragged3200, irregular}   feedBatches, identicalToAligned (always true: 420 of 420 comparisons)
allCallsConsistentWithBuffer
```

VAD files (`vad/<variant>/<scenario>.feed<N>.json`) hold `droppedChunkIndexes`, `chunksConsumed`, `chunksEmitted`, `chunkCalls[]` (every Process()
call that consumed a chunk, `processed=false` = dropped), `flush` and `text`. The rule behind them: Silero per whole chunk with the model's own `vad`
block (threshold 0.3, silence 3360 ms, prefix 560 ms), a counter of consecutive silent chunks, drop when the counter reaches
`max(prefix chunks, silence chunks)` (the 6th silent chunk on c056, the 3rd on c112), any speech chunk resets it, the VAD scores every consumed chunk
including dropped ones, `Flush()` never runs it. A dropped chunk is erased before the mel extractor, so the tokens after a drop are not those of the VAD-off run.

## Clips

| group | ids | notes |
|---|---|---|
| Common Voice 17 | `cv17-en-0001..0040`, `cv17-ru-0001..0040` | on int4-c056 en/0003 0005 0007 0008 0024 0028 and ru/0015 produce no tokens (very quiet or speech from sample 0) |
| quiet | `cv17-en-0024` (-55 dBFS peak, 0 tokens), `cv17-en-0086`, `cv17-ru-0069`, `gen-quiet-30dB-{en0001,ru0001}` | raw recognizer, no gain control |
| hard / long / short / TTS | `cv17hard-en-0051`, `cv17long-en-0001` (98.9 s), `cv17longnorm-en-0001`, `cv17short-en-0001`, `tts-long-en-0001` (58.3 s), `tts-ru-ru-0001/2` | private extras of the maintainer's Test-Audio |
| mp3 | `sample-0-mp3` | `sample-0.mp3` decoded through the repository's loader to float32 (161 786 samples) |
| TTS truth | `truth-{en,ru}-0001..0010` | SAPI clips of the word-timing study, not in the repository |
| generated | `gen-concat-en-gaps`, `gen-concat-ru-gaps`, `gen-concat-mix-auto` (80 s, en/ru alternating, `lang_id 101`), edge lengths (`gen-exact-*`: 8960, 17920, 53760 +-1 samples, `gen-short-0p3s-en`), silence (`gen-silence-*`), VAD inputs (`gen-vad-*`) | `tools/nemotron-golden/gen_audio.cs`, bit exact |

Awkward cases are in on purpose: 12 clips with zero tokens on int4-c056, 17 with language tags in the text, chunks with up to 16 tokens, remainders,
exact multiples. Compare per variant only (int4 and fp32 texts differ in 5 of 13 shared clips on c056; c056 and c112 in 24 of 53).

## GenAI feeding quirks (`checks/quirks.<variant>.json`)

* `Process()` consumes at most one chunk per call; the rest stays buffered.
* `Flush()` zero-pads the buffer to one chunk and silently drops everything beyond it, so the goldens always feed calls of at most one chunk. The plain-ORT
  recognizer encodes every complete chunk and is better than GenAI here; parity exists for the chunk-sized grid.
* `Flush()` with an empty buffer returns null (no padding chunk after an exact multiple); one sample more adds a flush chunk with 8959 zeros, padded in the raw audio domain.
