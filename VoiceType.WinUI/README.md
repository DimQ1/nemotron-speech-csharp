# VoiceType.WinUI

WinUI 3 packaged desktop application for real-time Nemotron speech recognition.
It provides a compact dictation window, model setup, global hotkeys, text injection,
session persistence, and microphone, loopback, or mixed audio capture.

## Architecture

- `SpeechLib` contains provider-neutral recognition and capture contracts.
- `SpeechLib.Nemotron` owns ONNX Runtime GenAI model sessions.
- `SpeechLib.Audio.NAudio3` owns the NAudio 3 preview capture implementation.
- `VoiceType.WinUI` owns WinUI views, MVVM state, settings, text injection, and MSIX packaging.

The WinUI application intentionally uses `NAudio 3.0.0-preview.19`. The stable NAudio 2
provider remains available for the CLI compatibility path.

## Build

Use the full solution for a CPU build:

```powershell
dotnet build NemotronSpeech.slnx -c Release -p:GpuArch=CPU
```

For a fast local iteration build:

```powershell
dotnet build VoiceType.WinUI/VoiceType.WinUI.csproj -c Debug -p:GpuArch=CPU
```

The project supports the same `GpuArch` values as the solution: `CPU`, `DML`,
`Standard`, and `Blackwell`.

## Local MSIX package (dev)

For local testing, always use the dev signing flow:

```powershell
Set-Location VoiceType.WinUI
.\build-dev.ps1
.\install-dev.ps1
```

Notes:

- `build-dev.ps1` reuses or creates a dev code-signing certificate in `CurrentUser\\My`
  with the same Publisher as `Package.appxmanifest`, then publishes a signed MSIX.
- `install-dev.ps1` trusts the generated `.cer` in `CurrentUser\\TrustedPeople` and installs
  the package with dependency MSIX files.
- If package contents changed, bump `Package.appxmanifest` version before install.

`build-store-release.ps1` remains for Store/release packaging scenarios.

The package identity and version are taken from `Package.appxmanifest`. Bump the
manifest version when installing a package with changed contents over an existing
installation. Application data is preserved under the paths managed by `AppPaths`.

## Runtime notes

- CPU sessions use the configured ONNX intra-op thread heuristic and explicitly use
  sequential graph execution by default.
- VAD processes audio but does not stop the capture device or bypass all model work.
- The NAudio 3 source keeps capture buffers bounded and exposes shared volume controls
  for the mixer UI.
- MP3 recording is created only when `SaveAudioMp3` is enabled.

## Related files

## Folder structure

- `Assets/` - app icons, package visual assets, generated Store images.
- `Interfaces/` - service and abstraction contracts used by DI and MVVM.
- `Messages/` - lightweight message/event payloads exchanged in UI flow.
- `Models/` - settings/data models and DTOs for persistence/runtime state.
- `Serialization/` - JSON and state serialization helpers.
- `Services/` - recognition orchestration, hotkeys, injection, app paths, window/taskbar integration.
- `ViewModels/` - MVVM state, commands, and feature-level UI logic.
- `Views/` - WinUI windows and XAML views.
- `Properties/` - publish profiles and packaging metadata.
- `build-dev.ps1` / `install-dev.ps1` / `build-store-release.ps1` - local and Store packaging scripts.
- `Package.appxmanifest` - package identity/version/capabilities for MSIX.

- `Services/RecognitionService.cs` - model and audio provider composition.
- `ViewModels/MainViewModel.cs` - recording and recognition state.
- `Services/AppPaths.cs` - application data locations.
- `VoiceType.WinUI.csproj` - WinUI, provider, and MSIX configuration.
- `Package.appxmanifest` - package identity and version.
