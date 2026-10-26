# Nemotron ASR .NET — AI Agent Instructions

## Build Commands

```powershell
# CPU only — plain ONNX Runtime (ONNX Runtime GenAI was removed; there are no GPU build flavours)
dotnet build NemotronSpeech.slnx -c Release
```

**Debug build:** omit `-c Release`. **Always use `NemotronSpeech.slnx`** to build the solution graph. The three VoiceType.Uno projects (`VoiceType.Uno`, `VoiceType.Uno.Core`, `VoiceType.Uno.Tests`) are **not** in the solution: build and test them on their own (the Uno desktop head: `dotnet build apps/VoiceType.Uno/src/VoiceType.Uno/VoiceType.Uno.csproj -c Release -f net10.0-desktop -p:RuntimeIdentifier=win-x64`; do a Release restore first, a Debug restore followed by `--no-restore -c Release` fails with UNOB0019).

> The `GpuArch` property and the CUDA / DirectML / Blackwell variants were removed.
> Passing `-p:GpuArch=...` is now ignored.

## Test Commands

```powershell
# Every test project of the solution. The model-backed E2E_ classes run when their models are found (in the checkout or in the
# main checkout of a worktree): minutes to an hour; without models they skip themselves. VoiceType.Uno.Tests is NOT in the solution.
dotnet test NemotronSpeech.slnx

# What CI runs (docs/ci.md): per test project, without any model-backed class
dotnet test libraries/SpeechLib/tests/SpeechLib.Tests/SpeechLib.Tests.csproj -c Release --filter "FullyQualifiedName!~.E2E_&FullyQualifiedName!~.Real_"

# Model-backed tests of one project only (needs the models / Test-Audio, see the File Map row "Nemotron" for the env vars)
dotnet test libraries/SpeechLib/tests/SpeechLib.Nemotron.Tests/SpeechLib.Nemotron.Tests.csproj -c Release --filter "FullyQualifiedName~E2E_"

# Word timings: Parakeet decoder times and the Uno History re-timing; the Nemotron word timer is covered by Unit_NemotronSnapTests / Unit_NemotronUtteranceTests
dotnet test libraries/SpeechLib/tests/SpeechLib.ParakeetTdt.Tests/SpeechLib.ParakeetTdt.Tests.csproj -c Release --filter "FullyQualifiedName~WordTiming"
dotnet test apps/VoiceType.Uno/tests/VoiceType.Uno.Tests/VoiceType.Uno.Tests.csproj -c Release --filter "FullyQualifiedName~WordTimings"

# VoiceType.Uno core tests (no UI, no network — settings store, model discovery,
# transcript coordination, download formatting, audio mixdown)
dotnet test apps/VoiceType.Uno/tests/VoiceType.Uno.Tests/VoiceType.Uno.Tests.csproj

# Nemotron recognizer (plain ONNX Runtime): Unit_ without models; E2E_ skip (one skipped test per row, with the reason)
# without models/audio; NEMOTRON_FULL_GOLDEN=1 adds the complete GenAI oracle in Data/golden-full (about an hour, see the
# File Map row "Nemotron" and tools/nemotron-golden/README.md)
dotnet test libraries/SpeechLib/tests/SpeechLib.Nemotron.Tests/SpeechLib.Nemotron.Tests.csproj -c Release

# No ONNX Runtime GenAI package or binary in the restore state or in the output of the CLI, WinUI and Uno desktop heads
# (build the heads first)
pwsh tools/scripts/check-no-genai.ps1
```

- **Framework:** xUnit 2.9.0 (no mocking library; the tests use hand-written fakes)
- **Naming:** `{Type}_{ClassName}Tests.cs`, methods: `MethodName_ShouldExpectedBehavior`; `Unit_` = no models, `E2E_` = needs models/audio/network, `Real_` = needs a real Gemma model (these two are what CI excludes)
- **CI:** `.github/workflows/ci.yml` (build + the model-free tests, about 2,000 in 9 projects, on Windows and Linux; the no-GenAI guard on Windows), the package workflows and releases are described in [docs/ci.md](docs/ci.md). Before the first restore a fresh clone needs the LiteRtLmSharp folder feed: `pwsh tools/scripts/fetch-litertlmsharp-feed.ps1` (`nuget-feed/litertlmsharp`, not in git)

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
  └─ Interfaces, audio sources, Transcriber, UtteranceText, shared AGC / Silero VAD

SpeechLib.Providers (net10.0, Library)
  └─ Recognizers on plain ONNX Runtime (Nemotron, Parakeet TDT, Qwen3-ASR, VibeVoice),
     RecognizerFactory, LanguageMapper, ExecutionProviderSelector

NemotronSpeech (net10.0, Exe)
  └─ CLI over RecognizerFactory

VoiceType.WinUI (net10.0-windows10.0.26100.0, WinUI 3 WinExe, MSIX)
  └─ Packaged Windows dictation app: CommunityToolkit.Mvvm view models, hotkeys, text injection

VoiceType.Uno.Core (net10.0, Library)
  └─ UI-free core of the Uno app: SettingsStore (single settings writer),
     ModelPathResolver, TranscriptCoordinator, download formatting

VoiceType.Uno (net10.0-windows / net10.0-desktop / android, Exe)
  └─ Uno heads over VoiceType.Uno.Core; UI project, so it has no test project —
     pure logic belongs in VoiceType.Uno.Core

Tests (net10.0, xUnit)
  └─ libraries/SpeechLib/tests/SpeechLib*.Tests (libraries, recognizers) and
     apps/VoiceType.Uno/tests/VoiceType.Uno.Tests (VoiceType.Uno.Core)
```

- **VoiceType.Uno has no test project on purpose** — its TFMs (`net10.0-desktop`)
  cannot be referenced from a plain test project. Put testable logic in
  `VoiceType.Uno.Core` and test it from `apps/VoiceType.Uno/tests/VoiceType.Uno.Tests`.

- **Apps get ONNX Runtime through `SpeechLib.Providers`** — plain `Microsoft.ML.OnnxRuntime` (pinned in lockstep in `SpeechLib`, `SpeechLib.Providers` and the WinUI head) is the only inference runtime; ONNX Runtime GenAI was removed and `tools/scripts/check-no-genai.ps1` (plus `Unit_AssemblyLayeringTests`) fails when a GenAI package or binary comes back
- **CPU-only builds** — `SpeechLib.Providers` references `Microsoft.ML.OnnxRuntime` (CPU) unconditionally; there is no `GpuArch` / GPU build configuration. The only GPU path is the optional WebGPU **plugin** EP (NuGet `Microsoft.ML.OnnxRuntime.EP.WebGpu`, a `PackageReference` of `SpeechLib.Providers` that flows into every head's output; `WebGpuExecutionProvider` registers it at run time). See the execution-provider pitfall for what Nemotron does with it
- **NuGet config** at [`nuget.config`](nuget.config) — `nuget.org` plus the local folder feed `nuget-feed/litertlmsharp` that `packageSourceMapping` reserves for `LiteRtLmSharp*` (filled by `tools/scripts/fetch-litertlmsharp-feed.ps1`; it must run before any restore, CI does it in `.github/actions/setup-build`)

## Key Conventions

| Convention | Detail |
|---|---|
| **Nullable** | `<Nullable>enable</Nullable>` in every project |
| **ImplicitUsings** | `enable` everywhere |
| **File-scoped namespaces** | `namespace VoiceType.WinUI.Services;` |
| **Private fields** | `_camelCase` — `_isRunning`, `_recognizer` |
| **Interfaces** | `I` prefix in `Interfaces/` folders (`SpeechLib`, the WinUI app). Default interface methods for optional features (e.g. `LastTokenCount => 0`). |
| **MVVM** | CommunityToolkit.Mvvm source generators (`ObservableObject`, `[ObservableProperty]`, `[RelayCommand]`) in the WinUI and Uno view models |
| **Services** | `sealed class : IDisposable` or `static class` in `Services/` |
| **UI thread** | UI updates raised from services, capture or recognition callbacks go through the dispatcher queue (`DispatcherQueue.TryEnqueue` in the WinUI and Uno apps) |
| **Events vs callbacks** | Services use `event Action<T>?` pattern; ViewModels subscribe and dispatch to UI |

## Critical Pitfalls

### ⚠️ WinUI `{x:Bind}` Defaults to OneTime

In the WinUI head `{x:Bind}` is compiled and its default mode is **OneTime**: a value that changes while the window is open needs `Mode=OneWay`, and `Mode=TwoWay` only works for a property with a setter (the compiler rejects a read-only target):

```xml
<TextBlock Text="{x:Bind ViewModel.MicLevelDb, Mode=OneWay}"/>
<ComboBox SelectedItem="{x:Bind ViewModel.AudioSource, Mode=TwoWay}"/>
```

(The pitfalls of the removed WPF app - TwoWay `{Binding}` defaults on read-only properties, `Run.Text` DataContext inheritance, the custom `AsyncRelayCommand` that swallowed exceptions, `UseWPF` in the test project, `HfFolder.Files` - went away with the app, commit `be92e25`.)

### ⚠️ Execution Providers: Stale Settings, WebGPU, and the Nemotron Policy

GPU builds are gone, but a persisted `ExecutionProvider` value of `"cuda"`/`"dml"` can still be read at startup. `SessionOptions.AppendExecutionProvider_*` throws when the native provider library is absent, which fails session creation outright. **Parakeet TDT, VibeVoice, Qwen3 and the diarizer** therefore route provider selection through `SpeechLib.ExecutionProviderSelector.Apply(options, requestedName)` (`libraries/SpeechLib/src/SpeechLib.Providers/ExecutionProviderSelector.cs`), which validates against `OrtEnv.Instance().GetAvailableProviders()`, sends `webgpu*` to `WebGpuExecutionProvider` and falls back to CPU. `RecognizerFactory` additionally forces CPU for Qwen3 (the native WebGPU EP fail-fasts the process on its graphs).

**Nemotron does not use the selector.** `NemotronProviderPolicy` + `NemotronSessions` (in `Providers/Nemotron`) give each of the three graphs its own options, from measurements on an RTX 5070 Ti laptop (`docs/research/nemotron-webgpu.md`):

- Only the **encoder** may run on WebGPU (2.3x faster, 5 to 10x less CPU time). The prediction network and the joint are always CPU-only (7x and 2x slower per call on WebGPU, same text). CPU thread count stays shared (1 thread for the two small graphs measured 8 % slower).
- WebGPU is used only on a **discrete** adapter (`WebGpuDeviceInfo.IsDiscrete`, from the plugin's `Discrete` metadata; unknown = not discrete). `webgpu:lp` and "no discrete adapter" are CPU (the Intel iGPU measured 2.2x slower than the CPU). Stored values `cpu`, `webgpu`, `webgpu:hp`, `webgpu:lp`, `follow_config`, `cuda`, `dml` and anything else stay valid; an adapter index and raw provider options are still parsed but ignored (`enableInt64` crashes, `enableGraphCapture` is rejected, the index has no effect on which adapter runs). `NemotronRnntRecognizer.ExecutionProvider` / `NemotronUtteranceRecognizer.ExecutionProvider` report `Requested`, `Effective`, `Adapter` and the `Reason` for a fallback.
- **The plugin appends its provider without checking**: `AppendExecutionProvider` succeeds and `new InferenceSession` throws `EP_FAIL` later. `NemotronSessions` therefore creates the encoder in a try/catch and retries CPU-only with one warning; a failed first run (the warm-up run on silence, done while the model loads) does the same; a run that fails later faults the stream and the next `ResetStreamingState()` moves the encoder to the CPU. After any such failure WebGPU is closed for the rest of the process.
- **At most one WebGPU recognizer per process** (`WebGpuProcessGate`): two on two threads crashed 3 of 3 times (0xC0000409, 0xC0000374, hang). The History offline job and the second engine of `TranscribeWords` get the CPU automatically.
- **Crash-loop breaker** (`WebGpuCrashMarker`): `%LOCALAPPDATA%\SpeechLib\webgpu\active-<pid>.marker` (folder override `SPEECHLIB_WEBGPU_STATE_DIR`) is written before the WebGPU session exists and removed after the warm-up run and three chunks, or on a clean dispose. A marker whose process is gone means the process died while starting on WebGPU: every later start runs the CPU, with the reason, until the file is deleted, one start has `SPEECHLIB_WEBGPU_RETRY=1`, or an app calls `NemotronRnntRecognizer.ClearWebGpuCrashMarker()` (not wired into the apps yet).
- **Never touch the real WebGPU EP in the test process.** A plugin or driver fault ends the process (0xC0000409 / 0xC0000374 / 0xC0000005 / hang). Tests use fakes (`WebGpuRig`) or a child process (`E2E_NemotronWebGpuTests`: `WerEval` with a time limit, crash = failed test with the exit code). Do not induce device loss: it resets the display driver.

## File Map

| Area | Key Files |
|---|---|
| **Audio pipeline** | `libraries/SpeechLib/src/SpeechLib/Audio/ConcurrentQueueWrapper.cs`, `libraries/SpeechLib/src/SpeechLib/LiveTranscriber.cs` |
| **Live translation** | `libraries/SpeechLib/src/SpeechLib/Translation/LiveTranslationSession.cs` (shared coordinator: sentence buffering, revisable feed, drafts, stable prefix, previous-sentence context, memo, output cleaning), `SpeechLib.LiteRT.Native` (in-process Gemma 4 via LiteRtLmSharp, greedy + no-repeat n-gram), `SpeechLib.LiteRT` (OpenAI-compatible HTTP server); app adapters `apps/VoiceType.WinUI/.../Services/TranslationService.cs`, `apps/VoiceType.Uno/.../Services/TranslationService.cs` |
| **Parakeet streaming** | `libraries/SpeechLib/src/SpeechLib.Providers/ParakeetTdtRecognizer.cs` (`Step`: one encoder pass per 0.32 s, commit + preview from a state copy, boundary moves only past emitted tokens, 2 s onset context for the first word, fresh decoder state after each pause, silence gate skips encoder work in pauses; `SPEECHLIB_PARAKEET_TRACE=1` dumps per-step diagnostics), `tools/WerEval` (`--streaming --chunk --right --left --silence --onset --no-preview`, preview WER + first-text latency) |
| **Live capture (Windows)** | `libraries/SpeechLib/src/SpeechLib.Audio.NAudio3/Audio/NAudio3AudioSource.cs` (WASAPI mic/loopback/mix), `libraries/SpeechLib/src/SpeechLib/Audio/StreamingResampler.cs` (anti-aliased rate conversion), `PcmSampleDecoder.cs`, `CaptureMixer.cs` |
| **Live capture (macOS)** | Swift helper `apps/VoiceType.Uno/src/VoiceType.Uno/Packaging/macOS/voicetype-audio.swift` (`mic` = AVAudioEngine, `system` = Core Audio process tap, macOS 14.2+; stdout Int32 rate + Float32 mono, rate-change marker `AD DE C0 7F`; stops on stdin EOF) inside VoiceType.app; reader `VoiceType.Uno.Core/Services/Audio/HelperAudioSource.cs` (`MacAudioSourceFactory`, resample + mix, helper's stderr line = the user-facing error). Bundle: `build-macos-app.sh` (publish `-r osx-arm64`, helper via `swiftc`, `Info.plist` usage texts, ad-hoc signature of every file, .dmg + .zip); CI job `macos` in `uno-publish.yml`; details `docs/ci.md` section macOS. Tests: `HelperAudioSourceTests` |
| **Text injection (Uno)** | `apps/VoiceType.Uno/src/VoiceType.Uno/Services/Platform/`: text is TYPED into the focused window, never pasted by default. `WindowsTextInjector` (SendInput Unicode key events, Enter/Tab as real VKs, 64-char chunks; clipboard only for `CopyToClipboard`), `Linux/LinuxTextInjector` (keyboard first; clipboard + paste chord only when `ILinuxKeyboard.CanType` is false, i.e. ydotool + non-ASCII) and `Linux/LinuxKeyboard.cs` (`XTestKeyboard`: each char -> keysym bound to an unused scratch keycode as `{ks,ks}` so layout/Shift/Caps do not matter; pool is reclaimed after a crash and unbound at exit; held modifiers are released first). `MainViewModel.InjectOffUiThread` chains calls so sentences arrive in order. LIVE typing: `RecognitionService.CommittedResult` (finished utterances + `StreamingResult.Stable` of the current one, raised on every ~0.3 s step like WinUI's `CommittedResult`; `FinalResult` fires only once AFTER Stop, and utterance finals only after a pause, so neither may be the injection trigger) -> `MainViewModel.TypeStableText` -> Core `LiveTextFeed` (SpeechLib `IncrementalTextTyper`) -> injector (the pipelines that produce the stable text, `UtterancePipeline`/`PlainPipeline`, live in Core `Services/RecognitionPipelines.cs`; `UtterancePipeline.StableText` is composed from tag-free Finals and joined through `UtteranceText`, because a language tag inside a Final would make the text differ from what was typed and the typer would drop the closing mark); `FinalResult` only types the missing rest. Nothing is typed (and skipped text is never typed later) while focus is inside VoiceType (`IPlatformTextInjector.IsFocusedWindowForeign`: Windows foreground PID, X11 `_NET_ACTIVE_WINDOW` + `_NET_WM_PID`) - a typed space would press the focused button. Tests: `apps/VoiceType.Uno/tests/VoiceType.Uno.Tests/Platform/` (Linux sources are compiled into the test project via `Link`). WinUI app: `apps/VoiceType.WinUI/.../Services/TextInjection/` (`SendInputStrategy` default, `ClipboardStrategy` opt-in) |
| **Speaker diarization** | `libraries/SpeechLib/src/SpeechLib.Diarization` (Nemotron-3-Diarization ONNX: `OfflineDiarizer`, `StreamingDiarizer`, `SpeakerAssigner`, `DiarizationModelLocator`; published at huggingface.co/DimQ1/nemotron-3-diarization-onnx; folders `nemotron-3-diarization-onnx-int8|-fp32` under `<models root>/diarization/`); Uno core `apps/VoiceType.Uno/src/VoiceType.Uno.Core/Services/History/Speakers/` (`SpeakerService`, `ReprocessChain`, `DiarizeJobWorker`, layers in the recording manifest); UI in `Presentation/History/Speakers*`, DEBUG `FakeSpeakerService` |
| **Settings (Uno)** | `apps/VoiceType.Uno/src/VoiceType.Uno/Presentation/Settings/`: `SettingsPage` (section list on its own under 840 px, list 300 px + section <= 720 px from 840 px; replaces the old `SettingsDialog`), one `*Section` view per section built in code from `SettingsRows` (look of Settings > History), `HotkeysSection` (key-chip recorder, global hotkeys are released while it listens: `MainViewModel.SetHotkeysSuspendedAsync`; failed registrations: `MainViewModel.FailedHotkeys`), `StorageSection` + `FolderCard` + `StorageFolderUi` (folder pick, nested check, move question, `AppRestart`); every control applies at once through `SettingsApplier` -> Core `DeferredSettingsApplier` -> `MainViewModel.ApplySettingsAsync` (a compute change waits for Stop while recording). Pure logic in Core `Services/Settings/`: `SettingsText` (human option names <-> stored values, section list per platform, hub subtitles), `StorageFolderText`, `StorageOverview` (summary, free space per drive), `HotkeyText`, `SettingsDefaults`, `RestartCommand`. Design `docs/design/voicetype-uno-settings` (status line there lists deviations). DEBUG hooks: `VOICETYPE_SETTINGS_PICK=<folder>` replaces the system folder picker, `VOICETYPE_SETTINGS_ANDROID=1` previews the phone layout on the desktop head; UIA: `docs/design/voicetype-uno-mobile/uia-layout-check.ps1 -Scenario settings-storage -Seed -ScrollSweep` |
| **Audio source chip (Uno main screen)** | Shows what is captured and switches it without going to Settings (same `AppSettings.AudioSource` as Settings > Audio). Logic in Core: `Services/Settings/SettingsText.cs` (`AudioSourceChipTitle`, `AudioSourceGlyphOf`, `HasAudioSourceChoice`, `AudioSourceChipName`), placement `MainLayout.SourceChip` (`Row` under the top bar from 560 px page height, `TopBar` icon below that or with the keyboard open, `None` on Android where only the microphone works). UI: `MainPage.xaml` (`SourceChip`, `SourceChipCompact`), `MainPage.xaml.cs` (`BuildSourceChip`, `CreateSourceMenu`), `Presentation/Controls/AudioSourceIcons.cs`, `MainViewModel.AudioSource` / `SelectAudioSource`. **Switching works on the fly** (Settings > Audio too): `MainViewModel.SwitchLiveSourceAsync` -> `RecognitionService.SwitchSourceAsync` -> Core `Services/SwitchableCapture.cs` (stops the old `IAudioSource`, queues a marker batch, starts the new one on the SAME queue; `IsActive` stays true during the switch; a source that fails within 0.8 s is dropped and the previous one restarted = `CaptureSwitchOutcome.Refused`, the choice is taken back and the reason shown). The decode loop turns the marker into `IRecognitionTap.OnSourceChanged`; History keeps `audio.sourceChanges` (`SourceChangeLog`). Tests: `SwitchableCaptureTests` (fake sources in `Fakes/FakeAudioSources.cs`), `SourceChangeLogTests`. Design: `docs/design/voicetype-uno-mobile/README.md` (chip section), mockup `?src=..&srcmenu=1`, `render.ps1` scenarios `*source*`. |
| **Storage moves (Uno)** | Core `Services/Settings/FolderMover.cs` (rename on one drive, never overwrite or delete, locked items stay, `Revert` makes a failed move all-or-nothing), `RecordingStorageMigrator` (`PendingSessionsPath`, `SessionsStorageNote`; run in `App.PrepareModelStorage` before the history store opens), `DataLocation` (`data-location.txt` pointer in the DEFAULT data root read first by `AppPaths.ResolveDataRoot`; `data-location.pending.txt` = move on restart, may copy across drives because app data is small; models/recordings inside the old root are pinned into `ModelsRootPath`/`SessionsPath` first; run first in `App.OnLaunched`; DEBUG `VOICETYPE_DATA_ROOT` keeps priority, skips the move and moves the pointer files into the sandbox), `ModelStorageMigrator` (models, partial moves allowed). "Restart now": `AppRestart` + `RestartCommand` (new process waits for the old one via `VOICETYPE_RESTART_PARENT_PID`). Tests: `apps/VoiceType.Uno/tests/VoiceType.Uno.Tests/Settings/` |
| **Model storage (Uno)** | ONE models root (`settings.ModelsRootPath`, else `AppPaths.ModelsDir`) with `asr/`, `llm/`, `diarization/` sub-folders; single source of truth `apps/VoiceType.Uno/src/VoiceType.Uno.Core/Services/ModelStorageLayout.cs` (never build model paths by hand; `ModelStorageLayout.Current` follows the live setting), `ModelStorageMigrator.cs` (start-up sort of the legacy flat layout, pending move to a new root), `ModelStorageSummary.cs`/`ModelStorageText.cs` (Settings/Models storage block), `Services/Speakers/DiarizationModelCatalog.cs` + `DiarizationModelService.cs` (INT8/FP32 download, validate, switch), UI `Presentation/Controls/ModelStorageBlock`, `ModelStorageUi.cs`, `SpeakerModelsViewModel.cs`; design note `docs/design/voicetype-uno-mobile/README.md` section 12 |
| **CLI entry** | `apps/NemotronSpeech/src/NemotronSpeech/Program.cs`, `apps/NemotronSpeech/src/NemotronSpeech/AppOptions.cs` |
| **Nemotron (plain ONNX Runtime)** | `libraries/SpeechLib/src/SpeechLib.Providers/Nemotron/`, the only Nemotron engine (ONNX Runtime GenAI was removed; its token streams live on as the golden oracle). `NemotronRnntRecognizer` (`IStreamingSpeechRecognizer`: buffering, silence drop, reset, language, per-token chunk/frame position on the stream timeline; an exception inside `ProcessAudio`/`Flush` leaves the stream undefined, so the recognizer is `IsFaulted` and every call throws until `ResetStreamingState()`; NaN/Inf input samples become 0 and are counted in `NonFiniteSamples`; `TrySetSearchOptions` (beam count, repetition penalty) is accepted and ignored by the transducer, as it was with GenAI, although Settings still show it; two stream policies in `NemotronRecognizerOptions`, both OFF by default = GenAI parity, the token oracle needs them off: `PauseState` (`NemotronPauseStateOptions`, item A1: `Keep` default, `Fresh`, `DualBranch` = after `PauseSeconds` of gate-silent chunks the next `DecisionChunks` (3) are decoded by the kept state and by a fresh one primed with the end of the pause, the fresh one wins with `FreshLead` (2) letter tokens more, the window's text is held and reported by the deciding call, a fresh winner keeps the `. <en-US>` closers of the kept state; uses the drop rule's Silero gate or one of its own; `ResetModelState()` = fresh model state on the same clock; `PauseDecided` diagnostics) and `FlushClosingChunks` (item A2: zero chunks after the padded flush remainder, the utterance layer takes them out of the clock); the apps set both in one place, Uno `Services/NemotronStreamPolicy.cs` via `RecognizerFactoryOptions.NemotronPauseState` / `NemotronFlushClosingChunks`, measurements in `docs/research/nemotron-pure-ort-followups.md`) over `NemotronRnntEngine` (encoder/decoder/joiner `InferenceSession`s from `NemotronSessions`: one provider per graph, guarded WebGPU creation, crash marker, process gate, see the execution-provider pitfall; the caches of a stream live in a `NemotronStreamState`, up to two branches, `SelectBranch` is bit-exact; ping-pong cache buffers, shapes read from the graphs), `RnntGreedyDecoder` + `NemotronOnnxStepModel` (greedy RNN-T loop, prediction-network reuse, batched joint: one joint run for the remaining frames of a chunk per prediction output, rows bit-identical to one-frame runs, `NemotronRecognizerOptions.BatchedJoint = false` is GenAI's frame-by-frame loop for A/B; blank penalty), `NemotronStreamingMel` + `DlibKissRealFft` (log-mel front end; the float32 KISS FFT keeps the mel bit-identical to the one GenAI fed the encoder **on Windows x64**, the platform of the oracle; other platforms use their own `logf`/`cosf`: on Linux x64 the mel differs by up to 7e-5 and the tokens of the 8 INT4 clips tested were still identical, arm64/macOS/Android/iOS are unmeasured, so the strict mel test is `[WindowsX64Theory]` and a tolerance test runs everywhere), `NemotronSilenceGate` (native-VAD drop rule + Silero chunk gate), `NemotronModelConfig` (typed `genai_config.json`: the file name is inherited from the GenAI export format and still marks a Nemotron folder, see `ModelFolderScanner`/`ModelPathResolver`) + `NemotronVocabulary` (`vocab.txt`, language tags, word starts). `NemotronUtteranceRecognizer`: utterances, `ITimedUtteranceRecognizer`, gain control inside (`NemotronUtteranceOptions.AutoGain` = `SpeechLib.Audio.AutomaticGainControl`: syllable-gated against a 2 s noise floor, the first syllable boosted on probation, room tone never lifted above -50 dBFS, never changed in a pause; unit tests use speech-like syllables over room tone, a steady sine is not speech for it), `KeepLanguageTagInFinal`, word times from `NemotronWordTimer` + `NemotronLagTable` + `NemotronEnergyTracker` (`NemotronWordTimingOptions`). `RecognizerFactory` builds it for a Nemotron folder and returns the recognizer itself, no decorator. Consumers must join utterance texts with `UtteranceText.Join/Append(prev, final, result.FinalContinuesPrevious)` (a closing mark or the rest of a word can lead the next utterance), warm up with `RecognizerWarmup.Run` (reset AFTER the silent chunk), and ask for `KeepLanguageTagInFinal = false` when the text is typed into another app. Tests `libraries/SpeechLib/tests/SpeechLib.Nemotron.Tests`: `Unit_` (fakes, no model) and `E2E_` (skipped without models/audio): `E2E_NemotronParityTests` / `E2E_NemotronVadTests` replay the golden JSON recorded from GenAI 0.16.0 token for token (a small set in `Data/golden` always; the complete oracle of 210 clip x variant runs and 48 VAD runs is tracked in `Data/golden-full` and replayed with `NEMOTRON_FULL_GOLDEN=1`; it is irreplaceable now that GenAI is gone, README there). The harness that recorded it, the generator of its audio and the regeneration steps are in `tools/nemotron-golden` (not built, needs the removed GenAI package); the audio itself (19 MB of generated clips, Common Voice, SAPI clips) is not in git. A row whose model or audio is missing is a skipped test with the reason, an unreadable golden fails (`Unit_GoldenFilesTests`), `E2E_NemotronGoldenInventoryTests` fails when an opted-in set cannot run. `E2E_NemotronStreamControlTests` covers what the oracle cannot (auto language, a language change mid-stream, 1 and 12 threads, a model folder that fails to load, NaN input), `E2E_NemotronBatchedJointTests` (every batch size of the joint bit for bit against one-frame runs, per model folder), `Unit_NemotronPauseStateTests` + `E2E_NemotronPauseStateTests` (the stream policies: branch switching bit-exact, model-state reset on the clock, a dropped sentence recovered, a hesitation kept, batching independence, the late last word given back by a closing chunk), `E2E_NemotronUtteranceTests`, `E2E_NemotronFactoryTests`, `E2E_NemotronCleanFrontEndTests`, `E2E_NemotronProviderEngineTests` (the real model with a fake WebGPU: provider split, warm-up, marker, move to CPU) and `E2E_NemotronWebGpuTests` (the real WebGPU on a discrete GPU, always in a child process through `WerEval`: WER within 0.6 pp of the CPU for INT4, identical text for fp32; skipped with the reason without the plugin or a discrete GPU). Env vars: `NEMOTRON_MODEL_DIR_{INT4,FP32}_{C056,C112}` (or `NEMOTRON_MODEL_DIR` + `NEMOTRON_MODEL_VARIANT`, or `NEMOTRON_MODELS_ROOT`) for the model folders, `SPEECHLIB_TEST_AUDIO` for `Test-Audio`, `NEMOTRON_FULL_GOLDEN=1` for the tracked full golden set, `NEMOTRON_GOLDEN_DIR` for another golden folder, `NEMOTRON_GOLDEN_AUDIO` for the generated clips of the goldens (`tools/nemotron-golden`), `NEMOTRON_TEST_THREADS` (default 4). CLI: `apps/NemotronSpeech`; benchmarks `tools/BenchmarkSuite1` (`NemotronRnntCpuBenchmark`, `NemotronRnntCpuThreadBenchmark`) |
| **Word timestamps** | `libraries/SpeechLib/src/SpeechLib/Models/WordTiming.cs` (`WordTiming`, `TimedWordBuilder`: tokens to words), `Models/TokenTimingRefiner.cs`; recognizers with real times implement `ITimedUtteranceRecognizer` (Nemotron: `Nemotron/NemotronWordTimer.cs`, calibrated lag + speech-onset snap, accuracy in `docs/research/asr/nemotron-3.5-asr-timestamps-analysis.md`; Parakeet decoder times), `libraries/SpeechLib/src/SpeechLib.Audio.NAudio3/Transcriber.cs` takes `TranscribeWords` from such a recognizer and falls back to the length-weighted `AddWordTimings` guess otherwise |
| **WinUI main VM** | `apps/VoiceType.WinUI/src/VoiceType.WinUI/ViewModels/MainViewModel.cs` (Uno: `apps/VoiceType.Uno/src/VoiceType.Uno/Presentation/MainViewModel.cs`) |
| **Downloader** | `libraries/SpeechLib/src/SpeechLib.ModelDownload/ModelDownloadManager.cs` (parallel jobs, byte progress per job and total, resume, de-dup by key), `HuggingFaceClient.cs`, `ModelCatalog.cs` (Parakeet INT4 recommended); WinUI `Services/DownloadCenter.cs` (app-wide singleton, toast + in-app notice per finished model), `ViewModels/ModelDownloaderViewModel.cs`, `Views/ModelDownloaderWindow.xaml` |
| **Text injection (WinUI)** | `apps/VoiceType.WinUI/src/VoiceType.WinUI/Services/TextInjector.cs`, `Services/TextInjection/` (see also the Uno row above) |
| **App startup** | `apps/VoiceType.WinUI/src/VoiceType.WinUI/App.xaml.cs`, `apps/VoiceType.Uno/src/VoiceType.Uno/App.xaml.cs` |
| **Word-timing tests** | `libraries/SpeechLib/tests/SpeechLib.ParakeetTdt.Tests/Unit_ParakeetWordTimingTests.cs`, `libraries/SpeechLib/tests/SpeechLib.Nemotron.Tests/Unit_NemotronSnapTests.cs`, `apps/VoiceType.Uno/tests/VoiceType.Uno.Tests/History/DecoderWordTimingsTests.cs`; the WER / word-time harnesses are `tools/WerEval` and `tools/WordTimingEval` |
| **CI/CD** | `.github/workflows/` (`ci.yml` build + model-free tests + no-GenAI guard; `build-voicetype-uno.yml`, `uno-publish.yml` (reusable), `release-voicetype-uno*.yml` (tags `uno-v*`, `uno-android-v*`), `voicetype-uno-linux-smoke.yml`, `build-voicetype-winui-msix.yml` (manual)), `.github/actions/setup-build` (SDK + LiteRtLmSharp feed), `tools/scripts/fetch-litertlmsharp-feed.ps1`, `tools/scripts/check-no-genai.ps1`; full description in [docs/ci.md](docs/ci.md) |

## Related Docs

- [README.md](README.md) — project overview, models, demo
- [docs/ci.md](docs/ci.md) — GitHub Actions pipelines: what runs when, tests in CI, releases, Android signing, running the same checks locally, open decisions
- [tools/converters/NemotronAsr/README.md](tools/converters/NemotronAsr/README.md) — NeMo → ONNX conversion (Python)
- [docs/research/asr/nemotron-3.5-asr-timestamps-analysis.md](docs/research/asr/nemotron-3.5-asr-timestamps-analysis.md) — model timestamp capabilities (RNN-T durations, frame rate, token-level alignment) and the implementation status of the plain-ONNX-Runtime route
- `work/onnxruntime-genai` (the maintainer's local full clone of microsoft/onnxruntime-genai, gitignored and therefore absent from a fresh checkout: `git clone https://github.com/microsoft/onnxruntime-genai work/onnxruntime-genai`; read-only, refresh with `git -C work/onnxruntime-genai fetch --all --tags`) — the standing source of recipes and ideas for the plain-ORT recognizers: before designing or tuning a recognizer (Nemotron, Parakeet TDT, Qwen3, VibeVoice, a new model) look there for the same model type or technique (Nemotron RNN-T `src/models/nemotron_speech.cpp` + `preprocessing/nemotron_streaming_processor.cpp`, Moonshine/Whisper streaming, model builders and quantisation recipes under `src/python/py/models/`, unmerged PRs such as #2591 Nemotron word timestamps). Its filtered, verified backlog: [docs/research/onnxruntime-genai-ideas.md](docs/research/onnxruntime-genai-ideas.md)
- `.claude/skills/nemotron-backend/SKILL.md` — Claude-specific backend patterns
- `.claude/skills/nemotron-ui/SKILL.md` — Claude-specific UI patterns
