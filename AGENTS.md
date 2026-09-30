# Nemotron ASR .NET — AI Agent Instructions

## Build Commands

```powershell
# CPU only — ONNX Runtime GenAI is referenced in its CPU flavour
dotnet build NemotronSpeech.slnx -c Release
```

**Debug build:** omit `-c Release`. **Always use `NemotronSpeech.slnx`** to build the complete solution graph.

> The `GpuArch` property and the CUDA / DirectML / Blackwell variants were removed.
> Passing `-p:GpuArch=...` is now ignored.

## Test Commands

```powershell
# All tests (including E2E which needs HuggingFace network)
dotnet test NemotronSpeech.slnx

# Unit tests only (no network, fast)
dotnet test apps/VoiceType/tests/VoiceType.Tests/VoiceType.Tests.csproj --filter "FullyQualifiedName~Unit_"

# E2E tests only (real HuggingFace API)
dotnet test apps/VoiceType/tests/VoiceType.Tests/VoiceType.Tests.csproj --filter "FullyQualifiedName~E2E_"

# Word-timestamp tests (unit + E2E regression, needs model + sample-0.mp3 for E2E)
dotnet test apps/VoiceType/tests/VoiceType.Tests/VoiceType.Tests.csproj -c Release --filter "FullyQualifiedName~WordTimings"

# VoiceType.Uno core tests (no UI, no network — settings store, model discovery,
# transcript coordination, download formatting, audio mixdown)
dotnet test apps/VoiceType.Uno/tests/VoiceType.Uno.Tests/VoiceType.Uno.Tests.csproj
```

- **Framework:** xUnit 2.9.0
- **Naming:** `{Type}_{ClassName}Tests.cs`, methods: `MethodName_ShouldExpectedBehavior`
- **Moq** is available (4.20.72) but not yet used in existing tests

## Test Data (WER evaluation)

WER/RTF is measured with the `tools/WerEval` harness against Common Voice test
audio. The audio is **not committed** — `Test-Audio/` is gitignored. Download
it on demand:

```powershell
python tools/eval/download_cv_test.py --lang ru --count 250
python tools/eval/download_cv_test.py --lang en --count 250
```

- **Source:** Hugging Face mirror `fixie-ai/common_voice_17_0` (`config=ru|en`,
  `split=test`). The official `mozilla-foundation/common_voice_11_0` repo was
  removed. Fetched via the datasets-server REST API
  (`https://datasets-server.huggingface.co/rows`) — no `datasets`/torchcodec
  needed; MP3 is decoded with `soundfile` and resampled to 16 kHz mono.
- **Layout:** `Test-Audio/cv17/{ru,en}/NNNN.wav` (16 kHz PCM16 mono) +
  `NNNN.txt` (reference transcript), one pair per utterance.
- The WER harness expects sibling `*.wav` + `*.txt` pairs and scores
  ru/en separately (see `tools/WerEval/Program.cs`).

## Architecture

> See [README.md](README.md) for full overview. Key points for agents:

```
SpeechLib (net10.0, Library)
  └─ Interfaces, audio sources, LanguageMapper, Transcriber

NemotronSpeech (net10.0, Exe)
  └─ ORT GenAI wrapper, CLI, multi-GPU builds

VoiceType (net10.0-windows, WPF WinExe)
  └─ Desktop dictation app: MVVM, hotkeys, text injection

VoiceType.Uno.Core (net10.0, Library)
  └─ UI-free core of the Uno app: SettingsStore (single settings writer),
     ModelPathResolver, TranscriptCoordinator, download formatting

VoiceType.Uno (net10.0-windows / net10.0-desktop / android, Exe)
  └─ Uno heads over VoiceType.Uno.Core; UI project, so it has no test project —
     pure logic belongs in VoiceType.Uno.Core

VoiceType.Tests (net10.0-windows, xUnit)
  └─ Tests referencing VoiceType
```

- **VoiceType.Uno has no test project on purpose** — its TFMs (`net10.0-desktop`)
  cannot be referenced from a plain test project. Put testable logic in
  `VoiceType.Uno.Core` and test it from `apps/VoiceType.Uno/tests/VoiceType.Uno.Tests`.

- **VoiceType depends on NemotronSpeech** — ONNX Runtime GenAI is pulled transitively
- **CPU-only builds** — `SpeechLib.Providers` references `Microsoft.ML.OnnxRuntimeGenAI` (CPU) unconditionally; there is no `GpuArch` / GPU build configuration
- **NuGet config** at [`nuget.config`](nuget.config) — single `nuget.org` source

## Key Conventions

| Convention | Detail |
|---|---|
| **Nullable** | `<Nullable>enable</Nullable>` in all 4 projects |
| **ImplicitUsings** | `enable` everywhere |
| **File-scoped namespaces** | `namespace VoiceType.Services;` |
| **Private fields** | `_camelCase` — `_isRunning`, `_recognizer` |
| **Interfaces** | `I` prefix in `Interfaces/` folder. Default interface methods for optional features (e.g. `LastTokenCount => 0`). |
| **MVVM** | Manual `INotifyPropertyChanged` + custom `RelayCommand`/`AsyncRelayCommand` in `ViewModels/Commands.cs` |
| **Services** | `sealed class : IDisposable` or `static class` in `Services/` |
| **WPF dispatcher** | All UI updates via `Application.Current.Dispatcher.Invoke()` in ViewModels |
| **Events vs callbacks** | Services use `event Action<T>?` pattern; ViewModels subscribe and dispatch to UI |
| **DecodeResult** | `ModelSession.DecodeTokens()` returns `DecodeResult(Text, TokenCount)` — a `readonly record struct`. Interface impls discard `.TokenCount` via `.Text`.

## Critical Pitfalls

### ⚠️ XAML TwoWay Bindings on Read-Only Properties

WPF defaults to **TwoWay** binding. Computed properties (no setter) must use **`Mode=OneWay`**:

```xml
<!-- CORRECT -->
<Run Text="{Binding SizeDisplay, Mode=OneWay}"/>
<Run Text="{Binding Files.Count, Mode=OneWay, StringFormat={}{0} files}"/>

<!-- WRONG — causes infinite DISPATCHER EXCEPTION loop -->
<Run Text="{Binding SizeDisplay}"/>
```

Always check: does the bound property have a `set`? If not → `Mode=OneWay`.

### ⚠️ `AsyncRelayCommand` Silently Swallows Exceptions

`AsyncRelayCommand.Execute` catches all exceptions and writes to `Debug.WriteLine`. If a ViewModel method throws, the error is only visible in a debugger. ViewModels should have their own try/catch to set UI status.

### ⚠️ WPF Test Project Must NOT Use `UseWPF>true`

`VoiceType.Tests.csproj` targets `net10.0-windows` but does **not** set `<UseWPF>true</UseWPF>`. The test project references `VoiceType` which brings WPF assemblies transitively. Adding `UseWPF>true` to the test project breaks xUnit integration.

### ⚠️ `HfFolder.Files` Is Init-Only

```csharp
public List<HfFile> Files { get; init; } = new();
```

Cannot be reassigned after construction. Use `Clear()` + `AddRange()` instead.

### ⚠️ `Run.Text` DataContext Inheritance

`Run` elements inside a `DataTemplate` inherit DataContext from the parent. Bindings like `{Binding SizeDisplay}` resolve against the `HfFolder` item, not the ViewModel.

### ⚠️ Stale GPU Execution-Provider Settings Must Degrade to CPU

GPU builds are gone, but a persisted `ExecutionProvider` value of `"cuda"`/`"dml"` can still be read at startup. `SessionOptions.AppendExecutionProvider_*` throws when the native provider library is absent, which fails session creation outright. Always route provider selection through `SpeechLib.ExecutionProviderSelector.Apply(options, requestedName)` (in `libraries/SpeechLib/src/SpeechLib.Providers/ExecutionProviderSelector.cs`), which validates against `OrtEnv.Instance().GetAvailableProviders()` and falls back to CPU. All three recognizers (Parakeet TDT, VibeVoice, Qwen3) use it.

## File Map

| Area | Key Files |
|---|---|
| **Audio pipeline** | `libraries/SpeechLib/src/SpeechLib/Audio/ConcurrentQueueWrapper.cs`, `libraries/SpeechLib/src/SpeechLib/LiveTranscriber.cs` |
| **Live translation** | `libraries/SpeechLib/src/SpeechLib/Translation/LiveTranslationSession.cs` (shared coordinator: sentence buffering, revisable feed, drafts, stable prefix, previous-sentence context, memo, output cleaning), `SpeechLib.LiteRT.Native` (in-process Gemma 4 via LiteRtLmSharp, greedy + no-repeat n-gram), `SpeechLib.LiteRT` (OpenAI-compatible HTTP server); app adapters `apps/VoiceType.WinUI/.../Services/TranslationService.cs`, `apps/VoiceType.Uno/.../Services/TranslationService.cs` |
| **Parakeet streaming** | `libraries/SpeechLib/src/SpeechLib.Providers/ParakeetTdtRecognizer.cs` (`Step`: one encoder pass per 0.32 s, commit + preview from a state copy, boundary moves only past emitted tokens, 2 s onset context for the first word, fresh decoder state after each pause, silence gate skips encoder work in pauses; `SPEECHLIB_PARAKEET_TRACE=1` dumps per-step diagnostics), `tools/WerEval` (`--streaming --chunk --right --left --silence --onset --no-preview`, preview WER + first-text latency) |
| **Live capture (Windows)** | `libraries/SpeechLib/src/SpeechLib.Audio.NAudio3/Audio/NAudio3AudioSource.cs` (WASAPI mic/loopback/mix), `libraries/SpeechLib/src/SpeechLib/Audio/StreamingResampler.cs` (anti-aliased rate conversion), `PcmSampleDecoder.cs`, `CaptureMixer.cs` |
| **CLI entry** | `apps/NemotronSpeech/src/NemotronSpeech/Program.cs`, `apps/NemotronSpeech/src/NemotronSpeech/AppOptions.cs` |
| **ONNX GenAI** | `libraries/SpeechLib/src/SpeechLib/ModelSession.cs` |
| **Word timestamps** | `libraries/SpeechLib/src/SpeechLib/Models/WordTiming.cs`, `libraries/SpeechLib/src/SpeechLib.Audio.NAudio3/Transcriber.cs` (AddWordTimings) |
| **WPF main VM** | `apps/VoiceType/src/VoiceType/ViewModels/MainViewModel.cs` |
| **Downloader** | `libraries/SpeechLib/src/SpeechLib.ModelDownload/ModelDownloadManager.cs` (parallel jobs, byte progress per job and total, resume, de-dup by key), `HuggingFaceClient.cs`, `ModelCatalog.cs` (Parakeet INT4 recommended); WinUI `Services/DownloadCenter.cs` (app-wide singleton, toast + in-app notice per finished model), `ViewModels/ModelDownloaderViewModel.cs`, `Views/ModelDownloaderWindow.xaml` |
| **Text injection** | `apps/VoiceType/src/VoiceType/Services/TextInjector.cs` |
| **Commands** | `apps/VoiceType/src/VoiceType/ViewModels/Commands.cs` |
| **App startup** | `apps/VoiceType/src/VoiceType/App.xaml.cs` |
| **Tests** | `apps/VoiceType/tests/VoiceType.Tests/Unit_WordTimingsTests.cs`, `apps/VoiceType/tests/VoiceType.Tests/E2E_WordTimingsRegressionTests.cs` |
| **Baseline** | `apps/VoiceType/tests/VoiceType.Tests/Data/sample-0-wordtimings-baseline.txt` |

## Related Docs

- [README.md](README.md) — project overview, models, demo
- [tools/converters/NemotronAsr/README.md](tools/converters/NemotronAsr/README.md) — NeMo → ONNX conversion (Python)
- [docs/research/asr/nemotron-3.5-asr-timestamps-analysis.md](docs/research/asr/nemotron-3.5-asr-timestamps-analysis.md) — model timestamp capabilities (RNN-T durations, frame rate, token-level alignment)
- `.claude/skills/nemotron-backend/SKILL.md` — Claude-specific backend patterns
- `.claude/skills/nemotron-ui/SKILL.md` — Claude-specific UI patterns
