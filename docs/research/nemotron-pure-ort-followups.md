# Nemotron on plain ONNX Runtime: known limitations and next-round candidates

Status 2026-10-22, branch `feat/nemotron-pure-ort`, written when the migration was closed (engine, utterance and word-timing layer,
integration, GenAI removal, three independent reviews and their fixes). The recognizer and its layer are described in
`AGENTS.md` (File Map, row "Nemotron"); the parity oracle in `tools/nemotron-golden/README.md`; the optimisation ideas in
[onnxruntime-genai-ideas.md](onnxruntime-genai-ideas.md) (its ids A1, A2, A3, C3 are used below).

Evidence pointers: paths without a prefix are in this repository. `work/nemotron-migration/reports/<id>.md` are the maintainer's
local, gitignored session reports (I1 engine, I2 / I2b utterance and word times, I3a / I3b integration, I4a / I4b review fixes,
R1 / R2 / R3 the reviews); a fresh clone does not have them, the numbers quoted here are copied from them.
Priority: H = fix before this reaches many users, M = next round, L = when convenient.

## 1. Known limitations

### 1.1 Gain control (the quiet-microphone rescue has side effects)

`SpeechLib/Audio/AutomaticGainControl.cs`, used inside `NemotronUtteranceRecognizer` (`NemotronUtteranceOptions.AutoGain`, on in
`RecognizerFactory`). Nemotron has no input normalisation and returns nothing for some clips at a -40 dBFS peak, so the gain is
needed. G1 and G2 were fixed in the optimisation round of 2026-10-23 (branch `opt/g1-gain-gate`, report
`work/opt-round/G1/REPORT.md`): the rule is now syllable-gated (see the class remarks).

| # | limitation | evidence | candidate | prio |
|---|---|---|---|---|
| G1 | **FIXED (2026-10-23).** Was: room tone above -60 dBFS boosted up to 20x (the first 10 ms frame above a fixed -60 dBFS gate set the level, every frame above it moved it), so a stream that starts in the -54 dBFS S0 tone was amplified by +26 dB in every pause; the 4-sentence fixture started at 5.7 s gave only "You are so rude". Now: noise floor = 10th percentile of the 10 ms levels of the last 2 s; speech = syllable-shaped bursts (start 12 dB above the floor, hold 6 dB, end by an 8 dB dip within 1 s; clicks < 5 frames and steady sounds never count); the first burst is boosted from its third frame on probation (taken back for a click or a steady sound; the model drops an utterance whose level jumps by 15+ dB after its first word, measured); louder speech lowers the gain at once, quieter syllables raise it only when they have ended, never in a pause; the floor is never lifted above -50 dBFS (`AutomaticGainControlOptions.NoiseCeilingRms`); target -30 dBFS instead of -24 (the old target raised a quarter of the Common Voice recordings). Measured, INT4 c056, fresh gain per recording, WER raw / old / new: quiet sets (284 clips: -30/-40/-46 dBFS peak, gen-quiet, a quiet microphone in a -66 dBFS room) 24.22 / 22.10 / 20.52 %; cv17 150 en + 150 ru at their recorded level 19.05 / 20.84 / 19.48 %; 72 noise-bed fixtures (-60..-42 dBFS white/pink/real room tone, 2-10 s lead, VAD on) 22.97 / 23.95 / 22.97 %; I3a start 5.7 s: the three sentences again. INT4 c112: quiet 22.60 / 19.73 / 20.57 % (the old rule is ahead on this set only), normal 17.69 / 19.28 / 18.82 %, noise 22.81 / 23.18 / 22.61 %. Residual: a click or breath before the first word that passes as a syllable still raises the room tone before the speech (bounded by the ceiling) and the first 10-30 ms of the word; Russian normal clips stay +1.2 pp above raw (14 words of 1216, all such starts), English -0.2 pp. | `work/.../I3a.md` section 2.3; `work/opt-round/G1/REPORT.md`; `Unit_AutomaticGainControlTests` | Silero as a second opinion on the first syllable (it scores -46 dBFS speech 0.82 and never fires on room tone, measured) if the start artefacts matter | done |
| G2 | **PARTLY FIXED by G1 (2026-10-23).** A resumed offline run starts with a fresh gain control. With the G1 rule a room tone or a click no longer sets the level, and the resume E2E (`E2E_NemotronRecognitionTests`, Uno) now runs the factory recognizer with gain on the 3/1.5/2.5 s conversation and on a new noisy-room variant (-54 dBFS room tone): every checkpoint resumes without losing a sentence. Still open: the 0.5 s fixture resumed at 6.54 s loses two sentences with a fresh gain (passes without gain), so that one test keeps the recognizer without gain. | `work/.../I4a.md` section F4; `work/opt-round/G1/REPORT.md` | carry the gain state in the checkpoint, or prime the resumed run's gain with the audio before the resume point (an `AutomaticGainControl` fed and discarded) | M |
| G3 | **The gain state is deliberately not reset by `ResetStreamingState()`** (the level describes the microphone; the old decorator never reset it). A result therefore depends on what the recognizer heard before; tests and `OfflineRecognitionJob` use one recognizer per run, `TranscribeWords` runs on a fresh gain. | `work/.../I3a.md` D1 and 2.3, `work/.../I4a.md` F3; `Unit_NemotronUtteranceTests` (AutoGain) | an explicit `ResetGain()` and a decision which callers use it | L |
| G4 | The speech level is measured on 10 ms frames with fixed constants (since G1: target -30 dBFS RMS, +26 dB maximum, gate relative to the noise floor but never below -70 dBFS); clips scaled to a -46 dBFS peak are rescued (cv17 en/ru 40 + 40: raw 37.83 / 18.54 %, gain 24.34 / 13.07 %), a clip at -55 dBFS peak whose speech frames lie under -70 dBFS (`cv17/en/0024.wav`, speech p90 -79 dBFS) is not (needs +45 dB). | same as G1 | none planned | L |

Fixed in the last round (for context): NaN or infinite samples no longer poison the gain state for good (`AutomaticGainControl`
and `NemotronRnntRecognizer` replace them by 0; `NonFiniteSamples` counts them).

### 1.2 Word times and utterances

Layer: `NemotronUtteranceRecognizer`, `NemotronWordTimer`, `NemotronLagTable`, `NemotronEnergyTracker`.

| # | limitation | evidence | candidate | prio |
|---|---|---|---|---|
| W1 | **The last word of an utterance, emitted late together with the tag of the next turn, can take that turn's onset.** Dialogue set g06 en: `stripe.` 532 ms late and attributed to the next speaker (1 word; speaker accuracy 99.86 / 99.71 % with a perfect diarizer). The shipped snap lets a word move up to 0.35 s onto an onset. | `work/.../I4a.md` section F1 ("OPEN"), `Unit_NemotronSnapTests`, dialogue cases of `E2E_NemotronUtteranceTests` | "the last word of an utterance does not snap beyond its own calibrated end" (cuts legitimate single-token snaps of up to 0.35 s; not tried) | L-M |
| W2 | **The emission lag is larger and more scattered on real English crowd-sourced speech than on the TTS the table was fitted on**: Common Voice en median 382 ms (IQR 226) against 224 ms, so displayed starts of poor recordings are about 170 ms late (median abs error 181 ms, p90 429 ms). Russian Common Voice and AMI meetings match the table. Speaker assignment with the real diarizer is unaffected (99.2 % en, 97.2 % ru, AMI 96.7 %). | `work/.../I2b.md` sections 7 and 12 | recording-quality dependent lag (a per-clip estimate), or fit against real speech once a reference that is not itself a model exists | M |
| W3 | **Russian lag depends on the speaking rate** (381 ms at SAPI rate -2, 264 ms at +4); the table has one number per language. | `work/.../I2b.md` section 12 | a speaking-rate estimate from the token stream | L-M |
| W4 | **Languages other than en and ru use conservative late-biased defaults** (150 / 104 ms for 560 ms models, 110 / 64 ms for 1120 ms), never measured. | `NemotronWordTimingOptions.LanguageOverrides`; `work/.../I2b.md` section 12 | measure per language that the product promotes; Thai returns no tokens at all | L-M |
| W5 | **The end-of-utterance gap of 1.04 s was measured on TTS only** (no hesitations: gaps between letter tokens of one utterance are >= 13 frames in 0.3 % of the cases). Real speech may need Parakeet-style tuning (`StopHistoryEouSeconds`). | `work/.../I2b.md` section 12 | WerEval-style run on conversational real speech with hesitations | M |
| W6 | **The snap gate (off below a 21 dB speech-to-noise difference) was validated on pink noise and reverb (RT60 0.35 s) only**, 24 + 24 + 24 units at three noise levels; babble and music are untested; the gate edge between SNR 10 and 15 dB is sharp. Without snapping the times are the calibrated lag (start median 75-103 ms at SNR 10 to 5 dB). | `work/.../I2b.md` section 6, `work/.../I4a.md` final tables (SNR 15: p90 +4 ms, the one statistic that got worse) | degraded sets with babble / music; a soft gate | M |
| W7 | **A checkpoint lags the newest Final by one segment.** The closing mark the model writes together with the next speech cannot be in a checkpoint, so `OfflineTranscriber` puts the resume point in the silence in front of the segment just committed; a resumed job redoes one utterance. Recognizers without word times keep the end-of-batch rule. | `work/.../I4a.md` section F4, `OfflineTranscriberTests` (Checkpoint_*), `E2E_NemotronRecognitionTests.AssertResumesFromEveryCheckpoint` | none: a design cost, 1 utterance of recompute | L |
| W8 | The first utterance of a recording has a short energy history (>= 20 frames needed, 60 s ring), so early words can be snapped against a floor estimated from little audio. | `work/.../I2b.md` section 12 | pre-roll from the warm-up audio | L |
| W9 | A Final can start with a mark or a word piece (19 % of sentence ends on TTS, 0.2 % of the words of real speech); every consumer must join through `UtteranceText.Join(..., FinalContinuesPrevious)`. `LiveSegmentTimer` has only the text, so a segment starting with a word piece (`s are`) stays a rare cosmetic defect. | `work/.../I2b.md` sections 10 and 12 | none | L |

### 1.3 Platforms and CI

| # | limitation | evidence | candidate | prio |
|---|---|---|---|---|
| P1 | **Bit identity with GenAI is a Windows x64 statement.** The mel front end calls the platform's `logf` / `cosf`. Measured on Linux x64 (glibc 2.39): 6 to 12 percent of the mel floats of the three dumped clips differ, at most 6.96e-5 (73 ulp), and the tokens and text of 8 INT4 c056 clips were identical to the goldens. Not measured: the full golden set on any non-Windows platform, Linux arm64, macOS, Android, iOS, Windows ARM64. The strict mel test is `[WindowsX64Theory]`, a tolerance test (2e-4) runs everywhere. | `work/.../R1-review.md` F1, `work/.../R3-review.md` M1, `work/.../I4b.md`; `Unit_NemotronStreamingMelTests` | replay `NEMOTRON_FULL_GOLDEN=1` on Linux x64 and arm64 to learn whether near-tie tokens flip; if they do, either accept a per-platform oracle or switch the front end to the portable `(float)Math.Log(double)` formulation and re-baseline once (about 0.04 % of values differ from the Windows oracle) | M |
| P2 | **CI is written but has never run.** `.github/workflows/ci.yml` builds and runs the model-free unit tests of the nine test projects (about 2,000, filter `FullyQualifiedName!~.E2E_&FullyQualifiedName!~.Real_`) on Windows and Linux and runs `tools/scripts/check-no-genai.ps1` on Windows; before this change the only `dotnet test` steps pointed at `VoiceType.Tests`, which no longer existed. Not covered: every model-backed `E2E_` test (about 730), the full golden replay, Android on a device, iOS. Nothing is known about the Linux results until the first run. | `docs/ci.md`, `work/.../R1-review.md` F9, `work/.../R3-review.md` minor 1 and 8 | run it on a draft pull request and fix what the Linux leg shows; a self-hosted runner with the models for the `E2E_` suite | M |
| P3 | **`dotnet test NemotronSpeech.slnx` runs the model E2E suite** (1 to 3 GB of models, minutes to an hour) whenever `models/asr/nemotron-...` exists in the checkout or the main checkout of a worktree. | `work/.../R3-review.md` minor 9 | a category or an env opt-in for `E2E_` | L |
| P4 | **WebGPU for Nemotron: measured and decided, one machine only.** The encoder runs on WebGPU on a discrete GPU (2.3x faster than the CPU, text equal for fp32, +0.07 to +0.5 pp WER for INT4); the prediction net and joint stay on the CPU; `lp` and integrated graphics are CPU; creation failures, crashes at start and a second WebGPU recognizer are handled (`docs/research/nemotron-webgpu.md`). Still open: other GPUs, Linux/Vulkan, macOS, Windows ARM64, device lost, packaged apps, and device-resident caches through IO binding (about 3 ms of 28 ms per chunk, not done). | `docs/research/nemotron-webgpu.md`, `work/.../W1-webgpu.md`, `work/.../I5.md` | verify on a second vendor and on Linux before widening; a Settings line that shows `ExecutionProvider.Effective`/`Reason`; decide whether the plugin stays in every head (size and platforms in the doc) | M |

### 1.4 Smaller facts

* **A second engine for `TranscribeWords` while a stream is open.** The History reprocess during an active recording loads a second
  model (about 1 GB for INT4); on a phone that doubles the footprint (`NemotronUtteranceRecognizer.TranscribeWords`). Worth a line in the
  Android notes or a refusal while recording (R3 minor 10). L-M.
* **Dead settings.** Repetition penalty and beam count do nothing (the transducer is greedy, as it was under GenAI) but Settings, History
  metadata and `IRuntimeConfigurable.TrySetSearchOptions` still carry them; the Uno row now says so. Decide: hide the control and drop the
  parameter from the interface, or keep it for old files (R3 minor 5 and 11). L.
* **The fault policy is "faulted until reset".** A failed chunk leaves mel state, caches and decoder at different positions, and the text of
  the chunks that did succeed has gone out with the exception, so the engine is not made transactional. Retrying a failed chunk would need a
  snapshot of the mel state (256 floats and one sample) and a way to hand out the earlier text (`NemotronRnntRecognizer.IsFaulted`). L.
* **The model config reader is more lenient than upstream's on paths:** upstream (`ValidateConfigPath`) rejects absolute paths and `..` in the file
  names of the config; `NemotronModelConfig` does not. Only matters for untrusted model folders. L.
* **Test coverage that cannot be closed without GenAI:** a token oracle for the automatic language (101) and for a mid-stream language change
  (`E2E_NemotronStreamControlTests` asserts sensible text and a consistent stream instead); other languages than en / ru; VAD goldens for
  FP32 (INT4 only, as recorded); the 10-symbols-per-frame cap never fires on real data (scripted-model unit test only).
* **43 of the 210 golden rows (and 2 of 48 VAD rows) need audio that is not in the repository** and cannot be regenerated: the SAPI truth clips
  (20 + 6 rows; the two `Data/truth/dialogue-*.wav` SAPI samples are the exception and ARE tracked: the maintainer confirmed on 2026-10-23 that there are no redistribution restrictions) and the maintainer's private
  `Test-Audio` extras (17 rows). They are skipped tests with a reason in a fresh clone (`tools/nemotron-golden/README.md`). Pending decision.

## 2. Next-round candidates from the optimisation backlog

Details, evidence and validation plans are in [onnxruntime-genai-ideas.md](onnxruntime-genai-ideas.md); only what changes after the
migration is noted here.

| id | what | verified benefit | what is needed first | prio |
|---|---|---|---|---|
| A1 | **Pause-state policy**: decide per pause whether the next speech keeps the stale encoder / decoder state or starts fresh (dual branch), instead of GenAI's frozen state | **BUILT 2026-10-26, off by default** (`NemotronRecognizerOptions.PauseState`, see 2.1). Dual branch (3 chunks, lead 2), new sentence after a pause: B-region WER EN 28.3 to 23.7 % (c056), 25.3 to 19.9 % (c112); hesitations and Russian within 0.2 points; long English recordings 25.9 to 25.6 % (c056), 29.1 to 25.9 % (c112) | the apps' default (Uno `NemotronStreamPolicy.PauseState`; proposed: on for English, off for Russian until natural-dictation data); its own golden set (the GenAI goldens keep the policy off); the lag table needs no change (2.1) | H |
| A2 | **Closing chunk at `Flush`**: one extra zero chunk after the padded remainder | **BUILT 2026-10-26, off by default** (`NemotronRecognizerOptions.FlushClosingChunks`). One closing chunk: clips cut 100 ms after the speech EN 26.5 to 25.0 %, RU 15.6 to 14.2 % (c056; c112 -0.9 / -0.5, fp32 -1.3 / -1.2 points), 26 of 200 files better, none worse; uncut CV17 clips 5 of 500 better, none worse | turn it on in the apps (Uno `NemotronStreamPolicy.FlushClosingChunks = 1`); cost one encoder pass at Stop | M (decide) |
| A3 | **Batched joint** over the remaining frames of a chunk in `RnntGreedyDecoder`. **DONE 2026-10-23** (`BatchedJoint`, default on; `false` = GenAI's per-frame loop) | measured in C# (ORT 1.30.0, 4 threads, 80 CV17 clips, interleaved A/B, loaded box): wall time -5.3 % INT4 c056, -7.8 % INT4 c112, -3.5 % FP32 c056; joint time per chunk about halved (share 11.4 to 5.6 %, 15.3 to 7.7 %, 8.4 to 4.3 %); identical texts and WER. Rows bit-identical to one-frame runs for every batch size on all four folders (`E2E_NemotronBatchedJointTests`), full golden oracle 389 of 389 | open: ARM64 (Android, macOS) and other ORT versions: run `E2E_NemotronBatchedJointTests` there; re-measure the speed on a quiet machine | done |
| C3 | **Repack the FastConformer encoder**: pointwise `Conv` to `MatMul` to INT4 | encoder data 690 to about 436 MB, about 5 % encoder time, mostly a phone-RAM item | conv-weight INT4 quality unmeasured (reject above +0.3 WER), the repo converter does not reproduce the shipped k-quant model | M |

### 2.1 A1 / A2 as built (2026-10-26)

* **A1** `NemotronPauseStateOptions { Policy = Keep | Fresh | DualBranch, PauseSeconds 1.1, DecisionChunks 3, PrerollSeconds 1.1, FreshLead 2 }` on `NemotronRecognizerOptions.PauseState`; the model state of a stream is a `NemotronStreamState`, the engine holds two (`SelectBranch`, bit-exact); `ResetModelState()` keeps the clock. Numbers: [onnxruntime-genai-ideas.md](onnxruntime-genai-ideas.md) item A1. What the apps would need to turn it on: set `NemotronStreamPolicy.PauseState` (Uno head); accept that the first words after a pause appear 2 chunks later (1.12 s c056, 2.24 s c112) and that a pause costs about 5 extra encoder passes; a new golden set for the policy (the GenAI goldens contain pauses and stay a `Keep` test). Word times: SAPI truth groups through the History path with the policy on, first words after a pause unchanged, all-word start median 54 to 57 ms (EN), Russian and the dialogue set identical, so `NemotronLagTable` and the snap stay as they are (c112 not re-measured).
* **A2** `NemotronRecognizerOptions.FlushClosingChunks` (0 = parity), taken out of the clock by the utterance layer; `RecognizerFactoryOptions.NemotronFlushClosingChunks`; Uno `NemotronStreamPolicy.FlushClosingChunks` (still 0, proposed 1: it never made a file worse in 1,400 measured files on three variants).

Other items of the backlog that touch Nemotron: A6 (silence policy v2: stop encoding after K silent chunks with a pre-roll ring), C7 (device-resident
Nemotron for a GPU EP, together with P4), B-group items for the other recognizers.

## 3. Pending decisions

1. **WebGPU for Nemotron** (P4): supported for the encoder on a discrete GPU, CPU otherwise (decided and implemented, `docs/research/nemotron-webgpu.md`). Open: show the effective provider in Settings, the packaging of the plugin per platform, other GPUs and Linux.
2. **Which file stays the model-folder manifest** (ideas doc, open question 11): `genai_config.json` is read as plain JSON and marks a Nemotron folder
   (`IsNemotronModel`, `ModelFolderScanner`); renaming it needs the published model folders re-exported.
3. **Gain control redesign** (G1, G2): done on 2026-10-23, the syllable-gated rule replaced the old one as the only rule (default on through
   `RecognizerFactory`, no switch back): the quiet-microphone rescue and noisy rooms are both served (section 1.1). Open: whether a second opinion
   from Silero on the first syllable is worth a second model in the audio path (G1 residual).
4. **Portable-libm policy** (P1): one oracle for all platforms or a Windows oracle plus a tolerance elsewhere.
5. **A1 on or off by default** and the matching new golden set (section 2, 2.1): built and measured, proposed on for English (lead 2, 3 chunks), off for Russian. **A2**: proposed on (1 closing chunk) in the apps.
6. **The 43 golden rows without public audio:** keep as skipped rows, add their audio to the repository (the SAPI clips could go in now that the SAPI redistribution question is settled, see section 1) or drop. The two `Data/truth` dialogue wavs are tracked.
7. **Dead settings** (1.4): hide the repetition penalty and drop it from `IRuntimeConfigurable`, or keep it.
