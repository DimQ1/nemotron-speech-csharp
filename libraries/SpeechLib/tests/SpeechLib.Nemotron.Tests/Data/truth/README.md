# Word-time truth sample (local audio, tracked timings)

`dialogue-en.json` / `dialogue-ru.json` hold the word start/end times (from the Windows SAPI engine's own events) of two short
two-voice dialogues, with the voice and turn of every word. They are tracked.

The matching audio, `dialogue-en.wav` / `dialogue-ru.wav` (16 kHz mono PCM16, SAPI speech), is **not tracked**: the
redistribution terms of the Windows voices are unclear. The four `Dialogue_*_ShouldBeTimedAccurately` tests in
`E2E_NemotronUtteranceTests` use `NemotronTruthFact` and are skipped with that reason when the wavs are missing.
To run them, put the two files next to the json files (they are matched by `.gitignore`, so they never show up as changes).
