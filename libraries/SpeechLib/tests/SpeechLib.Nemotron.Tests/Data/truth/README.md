# Word-time truth sample

`dialogue-en` / `dialogue-ru` are two short two-voice dialogues synthesised with the Windows SAPI voices.

* `dialogue-*.wav` — 16 kHz mono PCM16 audio.
* `dialogue-*.json` — start/end time of every word (taken from the SAPI engine's own word events) with the voice and the turn of the word.

The four `Dialogue_*_ShouldBeTimedAccurately` tests in `E2E_NemotronUtteranceTests` score the word times of the
recognizer against these json files. The maintainer confirmed on 2026-10-23 that the audio may live in the repository
without restrictions, so all four files are tracked.
