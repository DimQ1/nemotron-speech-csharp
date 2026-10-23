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
`RecognizerFactory`). Nemotron has no input normalisation and returns nothing at a -40 dBFS peak, so the gain is needed; its design
is crude.

| # | limitation | evidence | candidate | prio |
|---|---|---|---|---|
| G1 | **Room tone above -60 dBFS is boosted up to 20x** and the model then misses the following sentences. The first 10 ms frame above the gate sets the level at once, so a stream that starts in a -54 dBFS room tone (the S0 tone) is amplified by +26 dB; probe on a 4-sentence fixture started at 5.7 s: with gain only "You are so rude" comes out, the raw model gets "Should be alright you're going to the football game tonight. You are so rude". Microphones with a floor below -60 dBFS RMS are not affected. | `work/.../I3a.md` section 2.3; identical in the old GenAI chain | speech-likelihood before the first boost (several loud frames, or the Silero gate that exists anyway), a gate relative to the peak or a hysteresis, a boost ceiling tied to the measured noise floor, a slower first-frame rule | H |
| G2 | **A resumed offline run starts with a fresh gain control.** `OfflineTranscriber` resumes a job from a checkpoint with a new recognizer; resuming in front of a sentence whose first frame is a quiet click boosts the sentence 20x. Probe: resume at 6.54 s of the 0.5 s fixture lost two sentences with the factory recognizer; 6.3 s lost one even without gain (a fresh model is chaotic about the first 0.5 s of room tone). | `work/.../I4a.md` section F4; the resume E2E (`E2E_NemotronRecognitionTests`, Uno) therefore uses the recognizer without gain | carry the gain state in the checkpoint, or prime the resumed run with the audio before the resume point; with G1 a gate that needs several loud frames | M |
| G3 | **The gain state is deliberately not reset by `ResetStreamingState()`** (the level describes the microphone; the old decorator never reset it). A result therefore depends on what the recognizer heard before; tests and `OfflineRecognitionJob` use one recognizer per run, `TranscribeWords` runs on a fresh gain. | `work/.../I3a.md` D1 and 2.3, `work/.../I4a.md` F3; `Unit_NemotronUtteranceTests` (AutoGain) | an explicit `ResetGain()` and a decision which callers use it | L |
| G4 | The speech level and its gate are measured on 10 ms frames with fixed constants (target -24 dBFS RMS, +26 dB maximum, -60 dBFS gate); a clip at -55 dBFS peak (`cv17/en/0024.wav`) cannot be rescued (needs +40 dB). | same as G1 | none planned | L |

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
  (20 + 6 rows; the SAPI dialogue audio `Data/truth/dialogue-*.wav` is deliberately NOT tracked in git: the redistribution terms of the Windows voices are unclear, so only the word-time json files are tracked and the four timing tests skip without the wavs) and the maintainer's private
  `Test-Audio` extras (17 rows). They are skipped tests with a reason in a fresh clone (`tools/nemotron-golden/README.md`). Pending decision.

## 2. Next-round candidates from the optimisation backlog

Details, evidence and validation plans are in [onnxruntime-genai-ideas.md](onnxruntime-genai-ideas.md); only what changes after the
migration is noted here.

| id | what | verified benefit | what is needed first | prio |
|---|---|---|---|---|
| A1 | **Pause-state policy**: decide per pause whether the next speech keeps the stale encoder / decoder state or starts fresh (dual branch), instead of GenAI's frozen state | English next-utterance WER 41-48 % to 26-29 % with a fresh state; a plain reset costs 2-8 points when the speech continues the sentence; the offline selector keeps 83-90 % of the gain | an explicit stream-state object in the engine and a model-state-only reset that keeps the clock. **It needs its own goldens**: the GenAI goldens contain pauses and will differ once A1 is on, so the GenAI-parity behaviour must stay a test setting and the policy gets a new (post-GenAI) oracle. Re-measure `NemotronLagTable` and the snap parameters with the policy on | H as a spike |
| A2 | **Closing chunk at `Flush`**: one extra zero chunk before the padded remainder | -1.7 (EN) / -0.8 (RU) WER points when the stream ends right after speech; 8 % of EN clips lose a late last word | an option `FlushClosingChunks` (0 = parity) turned on at the apps' Flush call sites (Stop path, `OfflineTranscriber`); cost 0.1 s at Stop | M |
| A3 | **Batched joint** over the remaining frames of a chunk in `RnntGreedyDecoder` | 5-8 % RTF (0.168 to about 0.155), more on silence-heavy audio; rows were bit-identical (280 of 280, python ORT 1.24.4) | bit-identity on ORT 1.30 in C# and on ARM64; the golden replays are the gate | M |
| C3 | **Repack the FastConformer encoder**: pointwise `Conv` to `MatMul` to INT4 | encoder data 690 to about 436 MB, about 5 % encoder time, mostly a phone-RAM item | conv-weight INT4 quality unmeasured (reject above +0.3 WER), the repo converter does not reproduce the shipped k-quant model | M |

Other items of the backlog that touch Nemotron: A6 (silence policy v2: stop encoding after K silent chunks with a pre-roll ring), C7 (device-resident
Nemotron for a GPU EP, together with P4), B-group items for the other recognizers.

## 3. Pending decisions

1. **WebGPU for Nemotron** (P4): supported for the encoder on a discrete GPU, CPU otherwise (decided and implemented, `docs/research/nemotron-webgpu.md`). Open: show the effective provider in Settings, the packaging of the plugin per platform, other GPUs and Linux.
2. **Which file stays the model-folder manifest** (ideas doc, open question 11): `genai_config.json` is read as plain JSON and marks a Nemotron folder
   (`IsNemotronModel`, `ModelFolderScanner`); renaming it needs the published model folders re-exported.
3. **Gain control redesign** (G1, G2): a gate that needs evidence of speech before the first boost; decides whether the quiet-microphone rescue and
   noisy rooms can both be served.
4. **Portable-libm policy** (P1): one oracle for all platforms or a Windows oracle plus a tolerance elsewhere.
5. **A1 on or off by default** and the matching new golden set (section 2).
6. **The SAPI truth sample** (`Data/truth/dialogue-*.wav`, untracked since the publication of the branch) and the 43 golden rows without public audio: replace by a redistributable sample (a permissively licensed recording or another TTS engine with a clear licence) or keep it local-only.
7. **Dead settings** (1.4): hide the repetition penalty and drop it from `IRuntimeConfigurable`, or keep it.
