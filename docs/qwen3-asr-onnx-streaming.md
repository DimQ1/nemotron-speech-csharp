# Qwen3-ASR ONNX Streaming

The production package is the self-contained
`models/qwen3-asr-1.7b-onnx-block-streaming` directory. It contains the
specialized encoder, INT4 decoder, tokenizer, configuration, and embedding
files, so it can be selected without mixing files from another model:

```json
{
  "model_type": "qwen3_asr_onnx_streaming",
  "base_model_dir": ".",
  "encoder_file": "encoder_stream.onnx",
  "block_seconds": 2.0,
  "chunk_seconds": 2.0,
  "window_seconds": 16.0,
  "streaming_mode": "block_encoder"
}
```

The provider accepts normal 100 ms input callbacks and buffers them until the
configured 2-second block boundary. It starts with a growing context so text
can appear after the first block, encodes each block once, and runs the
KV-cache decoder over only the latest fixed-size 16-second feature window.
Stable text is committed from the growing prefix and later text is confirmed
by overlap with the next window. Audio older than `window_seconds` is
discarded. A final short tail is decoded by `Flush`; `ResetStreamingState`
starts a new stream.

This is the hybrid buffer strategy used by the other streaming providers: work
and memory are bounded by `window_seconds`, while each Qwen3 decode still sees
full bidirectional context inside that window. It is not a persistent
audio-encoder cache: the published Qwen3-ASR encoder has no audio state
input/output, so strict per-frame stateful inference would require a
causal/stateful encoder export or a separately trained streaming checkpoint.
The official Qwen3-ASR streaming path is a vLLM serving implementation that
re-feeds accumulated audio; it does not expose a recurrent encoder cache.

## Block encoder profile

`tools/converters/Qwen3Asr/export_block_streaming.py` creates an optional
`encoder_stream.onnx` profile from the existing trained encoder. The production
profile fixes one complete Qwen attention block at 200 mel frames (2 seconds)
and emits 26 audio features. The runtime encodes each block once, keeps only
the latest 16 seconds of features, and decodes that bounded feature window.
This removes repeated encoder work while retaining the trained bidirectional
attention inside each block; it is block streaming, not a fabricated recurrent
state cache.

The exporter can compare the fixed graph with the original dynamic graph using
the same seeded mel block. For the production 2-second graph, the check
reported a maximum absolute error of `1.32247806e-7`, mean absolute error of
`1.32147437e-8`, and cosine similarity of `1.000000000000`.

On the local 50-file smoke set, the production block profile measured:

| Profile | Language | WER | RTF | Speed |
| --- | --- | ---: | ---: | ---: |
| Block encoder, 2-second blocks / 16-second window | English | 8.58% | 0.799 | 1.3x |
| Block encoder, 2-second blocks / 16-second window | Russian | 8.37% | 0.980 | 1.0x |

These are smoke measurements; the final 250-file CV17 results are recorded
below. The full reports remain under `build/wer-reports/`.

The specialized `encoder_stream.onnx` graph is an optional 1.270 GB artifact.
The WinUI downloader writes the block manifest after a successful Qwen download.
It selects `encoder_stream.onnx` when that specialized graph is already present
in the model folder; a normal HF download falls back to the regular
`encoder.int4.onnx` with fixed 2-second block inputs. The downloader does not
download the specialized graph unless that artifact and a manifest referring to
it are packaged separately.

## Language selection

In explicit mode, the selected BCP-47 language is normalized to Qwen's
canonical prompt name and sent with every decode, so automatic language
markers cannot override it. In automatic mode, the provider reads the
language marker before `<asr_text>`, requires two matching observations before
locking, and suppresses an unconfirmed marker result. A new stream or a
runtime language change clears the candidate, text aligner, and buffered audio
so output from the previous language cannot leak into the next one.

## Prompt translation in WinUI

When the Qwen3 model is selected and **Translate** is enabled in WinUI, the
target-language selector configures the same Qwen decoder with a system prompt
(`Translate the audio into <language>.`) and the Qwen `<asr_text>` control token.
The translated result is emitted through the normal recognition stream and is
shown in the main text area; no separate LiteRT translation model is required.
Changing the target language applies to the loaded model without reloading it.

This is a prompt-based capability of the current checkpoint rather than a
separate machine-translation model. The local English-to-Russian E2E check
produces Cyrillic output and is covered by
`E2E_Qwen3AsrStreamingTests.StreamingProvider_PromptTranslationUsesTargetLanguage`.
For non-Qwen models, WinUI keeps the existing optional LiteRT translation path.

## Measurement provenance

The current package measurements use the self-contained production package
`models/qwen3-asr-1.7b-onnx-block-streaming` and Common Voice 17 test audio:
250 English files and 250 Russian files, with sibling WAV/reference-text pairs under
`Test-Audio/cv17`. Offline mode transcribes each complete utterance once.
The production block profile uses 2-second encoder blocks and a 16-second
feature context. The fixed context bounds long-running streams. `WER` is word
error rate; `RTF` is compute time divided by audio duration, so lower is faster
and values below 1.0 are faster than real time.

The older results in `work/qwen3-asr-onnx/benchmark_results.md` are a separate
historical LibriSpeech test-other/JFK evaluation. For example, that report lists
3.79% WER for Qwen3-ASR 1.7B FP32 and 4.25% for 1.7B INT4 GPTQ+RTN on 200
LibriSpeech samples. Those numbers are not directly comparable with the local
Common Voice results because the dataset, export, runtime path, and evaluation
set differ.

## Current CV17 production measurements

These CPU measurements use the self-contained production package and the
2-second block encoder with a 16-second feature window. They are full 250-file
Common Voice 17 runs, not the smaller smoke set above.

| Mode | Language | Files | Audio (s) | Errors | WER | RTF | Speed |
| --- | --- | ---: | ---: | ---: | ---: | ---: | ---: |
| Streaming (block encoder) | English | 250 | 1,456.85 | 266 | 11.81% | 1.387 | 0.7x |
| Streaming (block encoder) | Russian | 250 | 1,380.87 | 287 | 13.88% | 1.397 | 0.7x |

The older offline and bounded-warm-up values from earlier experiments are not
listed as current baselines because they used different runtime profiles. The
2-second profile improves time to the first block result and avoids repeated
encoder work, but these full CPU runs are slower than real time on long
utterance sets. The bounded feature window limits memory and decode context;
it does not make the non-stateful encoder a recurrent streaming encoder.

## Why the current design cannot be incremental (graph constraint)

The export has three graphs:

| Graph | Inputs | Outputs |
| --- | --- | --- |
| `encoder_stream.onnx` | `mel` (fixed 200 frames) | `[1, 26, 2048]` audio features |
| `decoder_init.int4.onnx` | `input_ids`, `position_ids`, `audio_features`, `audio_offset` | `logits`, `keys`, `values` |
| `decoder_step.int4.onnx` | `input_embeds`, `position_ids`, `past_keys`, `past_values` | `logits`, `keys`, `values` |

Audio features can only enter through `decoder_init`, and that graph accepts **no
past KV cache**. `decoder_step` continues a decode but can only add one text
token. Therefore every new encoder block forces a fresh prefill of the whole
retained audio window, and the decoder regenerates that window's entire
transcript. Per-block cost is `O(window_audio_tokens + window_transcript_tokens)`,
which is why throughput degrades as the window fills and why the window has to
stay bounded.

## Measured streaming defects and fixes

**Latency.** The text aligner returned nothing for the first decode and only
emitted words once a *second* decode agreed on the prefix, so a 6.3-second
English clip produced no text until 5.65 s (89% of the clip).

Two candidate fixes were implemented and measured:

1. **Revisable partial channel (default).** `Qwen3AsrRecognizer.PartialText`
   (optional member on `IStreamingSpeechRecognizer`) exposes the current window
   hypothesis minus the committed prefix. It is displayed but never committed,
   so the accurate commit policy is untouched. Both desktop apps already render a
   provisional tail this way. Measured on the same clip: first visible text at
   **1.00 s** (audio 2.00 s), and it self-corrects — `Joe Keaton disapproves.`
   becomes `Joe Keaton disapproved of films, and Buster also had reservations`.
   WER is unchanged.
2. **`emit_first_block` (opt-in).** Commits the first block's hypothesis minus a
   two-word holdback. Text still appears at ~1 s, but the append-only transcript
   cannot retract wrong part-window words: measured CV17 WER rises from 9.74% to
   13.23% (English) and 10.53% to 14.83% (Russian). Off by default.

**Throughput knobs** (`streaming_config.json`, all optional):

| Key | Default | Effect |
| --- | --- | --- |
| `decode_every_blocks` | `1` | Encode every block but run the decoder once per N blocks, applied only after the window saturates, so early blocks stay per-block and the first partial stays early. |
| `skip_silent_blocks` | `true` | Blocks below ≈ -46 dBFS RMS are still encoded into the window but not decoded. The next speech block or `Flush` decodes the full window, so text is deferred, never dropped. |
| `intra_op_threads` | `0` | Decoder ONNX Runtime intra-op threads; `0` keeps `max(2, ProcessorCount / 2)`. |
| `encoder_intra_op_threads` | `0` | Encoder ONNX Runtime intra-op threads. The encoder is compute-bound and scales differently from the memory-bound decoder. |
| `emit_first_block` | `false` | See the trade-off above. |

### Measured on CV17 (50 files per language, CPU)

| Configuration | EN WER | EN RTF | RU WER | RU RTF |
| --- | ---: | ---: | ---: | ---: |
| Before this work | 9.74% | 0.933 | 10.53% | 1.166 |
| Revisable partial (default) | 9.74% | **0.808** | 10.53% | **1.051** |
| `emit_first_block: true` | 13.23% | 0.933 | 14.83% | 1.061 |
| `decode_every_blocks: 2` | 9.74% | **0.801** | (not run) | — |
| `intra_op_threads: 19` | 9.74% | 1.739 | — | — |
| `encoder_intra_op_threads: 19` | 9.74% | 1.104 | — | — |

Every throughput knob is WER-neutral because the commit policy is untouched:
only when and where the decoder runs changes. Raising the thread count above the
built-in heuristic is clearly worse on both sessions, which is why encoder and
decoder thread counts are configured separately. `decode_every_blocks: 2`
engages only past window saturation, so a short-utterance set understates its
effect. The remaining floor is the 1.27 GB block encoder, which must run once per
2-second block; on this CPU it alone costs roughly RTF 0.6–0.9.

## Path to genuinely incremental streaming

1. **Re-export `decoder_init` with `past_keys`/`past_values` inputs** (and
   per-block `audio_features`). New blocks are then appended to a live KV cache,
   per-block work becomes `O(new block)`, and the window/overlap/commit
   heuristics disappear. This is the only change that makes the decoder truly
   incremental without retraining.
2. **Continuation prompting with the current graphs.** Feed the committed
   transcript as the assistant prefix next to a short audio window so the
   decoder generates only the new words. Cheap to try, but it needs a quality
   check because the model can drift when the audio no longer covers the text it
   is asked to continue.
3. **Causal/stateful encoder export.** A true streaming encoder would remove the
   block boundary entirely; it requires a conversion (and possibly fine-tuning)
   rather than a runtime change.

Measurement protocol for all of the above:

```powershell
dotnet build build/WerEval/WerEval.csproj -c Release
WerEval qwen3-streaming <modelDir> Test-Audio/cv17/en --max 50 --save <report>.md
WerEval qwen3-streaming <modelDir> Test-Audio/cv17/ru --max 50 --save <report>.md
WerEval qwen3-streaming <modelDir> Test-Audio/cv17/en --max 3 --trace   # latency/stability
```

`--trace` prints every emitted delta with wall-clock time, audio position and
the delta text, which is what exposes latency and commit instability that WER
alone hides.
