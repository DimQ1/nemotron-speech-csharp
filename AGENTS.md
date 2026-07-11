# Nemotron ASR .NET — AI Agent Instructions

## Build Commands

```powershell
# CPU-only (any machine)
dotnet build NemotronSpeech.slnx -c Release -p:GpuArch=CPU

# CUDA Standard (RTX 20/30/40)
dotnet build NemotronSpeech.slnx -c Release

# CUDA Blackwell (RTX 50, needs ORT-Nightly feed)
dotnet build NemotronSpeech.slnx -c Release -p:GpuArch=Blackwell

# DirectML (any GPU via DirectX)
dotnet build NemotronSpeech.slnx -c Release -p:GpuArch=DML
```

**Debug build:** omit `-c Release`. **Always use `NemotronSpeech.slnx`** — it includes all 4 projects.

## Test Commands

```powershell
# All tests (including E2E which needs HuggingFace network)
dotnet test NemotronSpeech.slnx

# Unit tests only (no network, fast)
dotnet test VoiceType.Tests/VoiceType.Tests.csproj --filter "FullyQualifiedName~Unit_"

# E2E tests only (real HuggingFace API)
dotnet test VoiceType.Tests/VoiceType.Tests.csproj --filter "FullyQualifiedName~E2E_"

# Word-timestamp tests (unit + E2E regression, needs model + sample-0.mp3 for E2E)
dotnet test VoiceType.Tests/VoiceType.Tests.csproj -c Release --filter "FullyQualifiedName~WordTimings"
```

- **Framework:** xUnit 2.9.0
- **Naming:** `{Type}_{ClassName}Tests.cs`, methods: `MethodName_ShouldExpectedBehavior`
- **Moq** is available (4.20.72) but not yet used in existing tests

## Architecture

> See [README.md](README.md) for full overview. Key points for agents:

```
SpeechLib (net10.0, Library)
  └─ Interfaces, audio sources, LanguageMapper, Transcriber

NemotronSpeech (net10.0, Exe)
  └─ ORT GenAI wrapper, CLI, multi-GPU builds

VoiceType (net10.0-windows, WPF WinExe)
  └─ Desktop dictation app: MVVM, hotkeys, text injection

VoiceType.Tests (net10.0-windows, xUnit)
  └─ Tests referencing VoiceType
```

- **VoiceType depends on NemotronSpeech** — ONNX Runtime GenAI is pulled transitively
- **NemotronSpeech GPU builds** use MSBuild `<Choose>/<When>` with `GpuArch` property and conditional `<PackageReference>`
- **NuGet config** at `NemotronSpeech/nuget.config` — adds ORT-Nightly feed for Blackwell

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

## File Map

| Area | Key Files |
|---|---|
| **Audio pipeline** | `SpeechLib/Audio/ConcurrentQueueWrapper.cs`, `SpeechLib/Transcriber.cs` |
| **CLI entry** | `NemotronSpeech/Program.cs`, `NemotronSpeech/AppOptions.cs` |
| **ONNX GenAI** | `NemotronSpeech/ModelSession.cs` |
| **Word timestamps** | `SpeechLib/Models/WordTiming.cs`, `SpeechLib/Transcriber.cs` (AddWordTimings) |
| **WPF main VM** | `VoiceType/ViewModels/MainViewModel.cs` |
| **Downloader** | `VoiceType/Services/ModelDownloaderService.cs`, `VoiceType/ViewModels/ModelDownloaderViewModel.cs` |
| **Text injection** | `VoiceType/Services/TextInjector.cs` |
| **Commands** | `VoiceType/ViewModels/Commands.cs` |
| **App startup** | `VoiceType/App.xaml.cs` |
| **Tests** | `VoiceType.Tests/Unit_WordTimingsTests.cs`, `VoiceType.Tests/E2E_WordTimingsRegressionTests.cs` |
| **Baseline** | `VoiceType.Tests/Data/sample-0-wordtimings-baseline.txt` |

## Related Docs

- [README.md](README.md) — project overview, models, demo
- [converter/README.md](converter/README.md) — NeMo → ONNX conversion (Python)
- [Doc/nemotron-3.5-asr-timestamps-analysis.md](Doc/nemotron-3.5-asr-timestamps-analysis.md) — model timestamp capabilities (RNN-T durations, frame rate, token-level alignment)
- `.claude/skills/nemotron-backend/SKILL.md` — Claude-specific backend patterns
- `.claude/skills/nemotron-ui/SKILL.md` — Claude-specific UI patterns
