# VoiceType.Uno

Cross-platform port of the VoiceType.WinUI dictation app, built on **Uno Platform** (Skia Desktop).
Primary target platform: **Linux (Ubuntu, X11)**. Windows/macOS desktop work through the same Skia shell.

## Status

| Feature | Status |
|---|---|
| XAML UI (start/stop/mute/copy/inject toggle, live transcript) | ✅ Ported |
| Settings persistence (settings.json, atomic writes) | ✅ Ported |
| Recognition pipeline (model lifecycle → capture loop → partial/final results) | ✅ Ported |
| Microphone capture on Linux (ALSA `default` device, 16 kHz mono) | ✅ Implemented |
| ONNX Runtime GenAI model inference (CPU EP) | ✅ Via SpeechLib.Nemotron (`-p:GpuArch=CPU`) |
| Global hotkeys on Linux | ⬜ Stub (`IPlatformHotkeyService` → X11 `XGrabKey` / Wayland portal TODO) |
| Text injection on Linux | ⬜ Stub (`IPlatformTextInjector` → XTest / `ydotool` / clipboard TODO) |
| Loopback ("WhatYouHear") capture on Linux | ⬜ Needs PulseAudio/PipeWire monitor source |
| Audio mixer window | ⬜ Not ported (WinUI version is NAudio/WASAPI-based) |
| Model downloader, first-run wizard, settings window, help window | ⬜ Not ported yet |
| Taskbar/tray recording indicator | ⬜ Linux: needs StatusNotifierItem (AppIndicator) |

## Architecture

The port deliberately fixes the main tech-debt findings of the WinUI app:

- **Platform code behind abstractions** — `IPlatformHotkeyService`, `IPlatformTextInjector`,
  `IAudioSourceFactory` (SpeechLib contract). No `user32.dll` P/Invoke in services.
- **No duplicated services** — `SettingsService`, `AppPaths`, `RecognitionService` exist once,
  platform-neutral (`Environment.SpecialFolder.LocalApplicationData` → `~/.local/share/VoiceType` on Linux).
- **Null Object pattern** for unimplemented platform backends (`NullPlatformServices`)
  instead of null checks / `OperatingSystem.IsWindows()` scattered through ViewModels.

```
SpeechLib (contracts) ─┬─ SpeechLib.Nemotron (ONNX GenAI model session, CPU EP on Linux)
                       └─ VoiceType.Uno
                            ├─ Services/          (platform-neutral: AppPaths, Settings, Recognition)
                            ├─ Services/Audio/    (AlsaAudioSourceFactory — Linux capture)
                            ├─ Services/Platform/ (IPlatformHotkeyService, IPlatformTextInjector + Null)
                            └─ Presentation/      (MainViewModel, MVVM via CommunityToolkit.Mvvm)
```

## Build & run

```powershell
# Windows host (Skia shell)
dotnet build apps/VoiceType.Uno/src/VoiceType.Uno/VoiceType.Uno.csproj -p:GpuArch=CPU
dotnet run --project apps/VoiceType.Uno/src/VoiceType.Uno/VoiceType.Uno.csproj -p:GpuArch=CPU

# Ubuntu (X11 session)
sudo apt install libasound2 libfontconfig1
dotnet publish apps/VoiceType.Uno/src/VoiceType.Uno/VoiceType.Uno.csproj -c Release -r linux-x64 -p:GpuArch=CPU
```

Wayland sessions: hotkeys and text injection require the XDG GlobalShortcuts / RemoteDesktop portals — not implemented yet; run under X11 (XWayland) for now.
