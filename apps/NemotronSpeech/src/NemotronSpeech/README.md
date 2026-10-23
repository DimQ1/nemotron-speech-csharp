# NemotronSpeech 🎙️

Command-line front end for the streaming recognizers of `SpeechLib.Providers`: [NVIDIA Nemotron 3.5 ASR](https://huggingface.co/nvidia/nemotron-3.5-asr-streaming-multilingual-0.6B) (0.6B params, streaming, multilingual) and Parakeet TDT, on plain ONNX Runtime.

## Engine

The model folder selects the recognizer: a Parakeet TDT ONNX export (`config.json`) streams through `ParakeetTdtRecognizer`;
a Nemotron folder (`genai_config.json`, `vocab.txt`, encoder / decoder / joiner graphs) goes through `RecognizerFactory` to
`NemotronUtteranceRecognizer`, which owns the whole decode path on plain ONNX Runtime: log-mel front end, cache-aware
encoder, greedy RNN-T loop, vocabulary, utterances and word times, input gain control included. `--word-timestamps` in file
mode prints the recognizer's word times (calibrated token frames, not an estimate).

**Behaviour change for the CLI:** it used to drive the bare GenAI `ModelSession`, which had no gain control (only the apps wrapped it
in one). It now builds the recognizer through `RecognizerFactory`, so the automatic gain control is **on**: a quiet microphone or file
that the old CLI transcribed to nothing (Nemotron has no input normalisation and is silent at a -40 dBFS peak) is now transcribed, and
transcripts of quiet audio differ from the old CLI's. `tools/WerEval` deliberately stays on the raw recognizer, without gain control.

ONNX Runtime GenAI is no longer used. The config file keeps its GenAI-era name `genai_config.json` because the published model
folders carry it; the product reads it as plain JSON (`NemotronModelConfig`).

```
NemotronUtteranceRecognizer         utterances, word times, gain control
└── NemotronRnntRecognizer          buffering, silence drop, reset, language, token positions
    ├── NemotronStreamingMel        log-mel front end (float32 KISS FFT)
    ├── NemotronRnntEngine          encoder / decoder / joiner InferenceSessions, cache buffers
    ├── RnntGreedyDecoder           greedy RNN-T search
    └── NemotronVocabulary          vocab.txt, language tags
```

## Structure

```
NemotronSpeech/
├── AppOptions.cs             # CLI argument parser
├── Program.cs                # Entry point (console app)
└── NemotronSpeech.csproj     # CLI referencing SpeechLib.Providers

SpeechLib.Providers/
├── RecognizerFactory.cs      # picks the recognizer for a model folder
├── Nemotron/                 # the Nemotron recognizer (see above)
├── ParakeetTdtRecognizer.cs  # Parakeet TDT streaming recognizer
├── LanguageMapper.cs         # BCP-47 -> lang_id
├── ExecutionProviderSelector.cs  # CPU, optional WebGPU for Parakeet/Qwen3/VibeVoice; unknown providers fall back to CPU
└── Nemotron/NemotronProviderPolicy.cs  # the Nemotron rules: encoder on WebGPU only on a discrete GPU, else CPU
```

## Execution Provider

CPU-only builds: `SpeechLib.Providers` references the plain `Microsoft.ML.OnnxRuntime` package; the former `GpuArch` switch and
its CUDA / DirectML / Blackwell variants were removed. Provider names from older settings (`cuda`, `dml`, `follow_config`, ...)
run on the CPU. `webgpu`, `webgpu:hp` (CLI: third argument, or library callers through `RecognizerFactoryOptions.ExecutionProvider`) uses the optional WebGPU
plugin execution provider; for Nemotron that means **the encoder only, on a discrete graphics card** (2.3x faster, far less CPU time), while the
prediction network and the joint stay on the CPU. Everything else falls back to the CPU with one warning that names the reason: `webgpu:lp` (integrated
graphics are slower than the CPU), no plugin, no discrete card, a crash marker of an earlier run, a second WebGPU recognizer in the process, a failed
session. An adapter index and raw provider options are accepted and ignored. The CLI prints `EP effective:` after the model is loaded. Details:
[docs/research/nemotron-webgpu.md](../../../../docs/research/nemotron-webgpu.md).

```powershell
dotnet build -c Release
```

## CPU Execution Tuning

For `cpu`, the Nemotron engine configures its three sessions (encoder, decoder, joiner) for real-time use:

- `intra_op_num_threads` uses a logical-core heuristic unless `NemotronRecognizerOptions.CpuThreads` is set.
- `inter_op_num_threads=1` limits graph-level contention.
- `allow_spinning=0` (`OrtCpuTuning`) avoids keeping worker threads busy after work ends.
- `execution_mode=ORT_SEQUENTIAL` is the default (`NemotronRecognizerOptions.SequentialExecution`).

This improves predictability for real-time CPU inference, but it does not make model
inference free. VAD is input processing: it can affect which audio reaches the model,
but it does not stop capture or guarantee that every inference pass is skipped.

Compare thread counts, VAD, and sequential versus parallel execution with:

```powershell
dotnet run --project tools/BenchmarkSuite1 -c Release -- --filter "*NemotronRnntCpuThreadBenchmark*"
```

The benchmark requires `models/asr/nemotron-3.5-asr-streaming-0.6b-onnx-int4-c056-cpu` and `Test-Audio/sample-0.mp3` and writes
per-case diagnostics to the ignored `build/benchmark-results/` directory. `NemotronRnntCpuBenchmark` compares the INT4 / FP32 and
c056 / c112 variants the same way.

## CLI Arguments

```
NemotronSpeech <model_path> <audio_file|--mic|--loopback|--mix> [ep] [--language <code>] [--use_vad true|false]
```

| Argument | Description |
|----------|-------------|
| `model_path` | Path to the model folder: a Nemotron export (`genai_config.json`) or a Parakeet TDT ONNX export (`config.json`, e.g. `.../parakeet-tdt-0.6b-v3-onnx/fp32`) |
| `audio_file` | WAV/MP3 file to transcribe |
| `--mic` | Live microphone capture |
| `--loopback` | System audio loopback capture |
| `--mix` | Mic + loopback mixed |
| `ep` | Execution provider: `cpu`; the names of the removed GPU builds (`cuda`, `dml`, `tensorrt`, `follow_config`, the default) run on the CPU as well |
| `--language` / `-l` | BCP-47 code (e.g. `ru`, `en-US`, `auto`) |
| `--use_vad` | Enable Silero VAD (`true`/`false`) |

## Examples

```powershell
# Russian, microphone, VAD on, CPU
dotnet run -- "models/asr/nemotron-3.5-asr-streaming-0.6b-onnx-int4-c056-cpu" --mic cpu --language ru --use_vad true

# English, audio file
dotnet run -- "models/asr/nemotron-3.5-asr-streaming-0.6b-onnx-int4-c056-cpu" "meeting.wav" cpu --language en

# Auto-detect language, loopback
dotnet run -- "models/asr/nemotron-3.5-asr-streaming-0.6b-onnx-int4-c056-cpu" --loopback cpu --language auto
```

## The Recognizer as a Library

`NemotronRnntRecognizer` implements `IStreamingSpeechRecognizer` from SpeechLib, so it can be used in any SpeechLib pipeline:

```csharp
using var recognizer = new NemotronRnntRecognizer(
    "models/asr/nemotron-3.5-asr-streaming-0.6b-onnx-int4-c056-cpu",   // model folder
    "cpu",                                                              // execution provider
    "11",                                                               // lang_id (see LanguageMapper; null = model default)
    useVad: true);                                                      // drop silent chunks (Silero)

// The audio is fed in pieces of any size; null = no chunk was encoded by the call
var text = recognizer.ProcessAudio(chunk);
var rest = recognizer.Flush();

// Per-token positions of the last call: chunk, frame and symbol on the stream timeline
foreach (var token in recognizer.LastTokens) { /* token.Id, token.GlobalFrame, ... */ }
```

For utterances, word times and the gain control use `NemotronUtteranceRecognizer` (or `RecognizerFactory.Create`, which builds it for a
Nemotron folder).

After an exception inside `ProcessAudio` or `Flush` the recognizer is `IsFaulted` (its stream is undefined) and refuses further audio
until `ResetStreamingState()` is called; NaN or infinite input samples are replaced by 0 (`NonFiniteSamples` counts them).

## Runtime Options

- **VAD**: `recognizer.TrySetVad(true)` — Silero chunk gate with the model folder's `vad` block (`silero_vad.onnx`)
- **Language**: `recognizer.TrySetLanguage("ru")` — set at runtime (multilingual models only)
- **Single-language models**: detected automatically (no `lang_id` input in the encoder)
- **Repetition penalty / beam count**: accepted and ignored (`TrySetSearchOptions`): the transducer searches greedily, as it did under GenAI

## Dependencies

| Package | Version |
|---------|---------|
| Microsoft.ML.OnnxRuntime | 1.30.0 |
| SpeechLib / SpeechLib.Providers | Project reference |
| System.CommandLine | 2.0.1 |
