# VoiceType.Uno Linux — Implementation Plan

Status date: 2026-08-21. Branch: `feature/uno-platform-linux`.
Scope: complete the Linux-first port of VoiceType on Uno Platform (Skia Desktop head).

## Phase 1 — Core dictation (DONE)

| Item | Status | Location |
|---|---|---|
| XAML UI (start/stop/mute/copy/inject toggle, live transcript) | Done | apps/VoiceType.Uno/src/VoiceType.Uno/MainPage.xaml |
| Settings persistence (settings.json, atomic writes) | Done | apps/VoiceType.Uno/src/VoiceType.Uno/Services/SettingsService.cs |
| Recognition pipeline (lifecycle → capture → partial/final) | Done | apps/VoiceType.Uno/src/VoiceType.Uno/Services/RecognitionService.cs |
| Microphone capture (ALSA default, 16 kHz mono) | Done | apps/VoiceType.Uno/src/VoiceType.Uno/Services/Audio/AlsaAudioSourceFactory.cs |
| PulseAudio capture: Mic / Loopback (@DEFAULT_MONITOR@) / Mix | Done | apps/VoiceType.Uno/src/VoiceType.Uno/Services/Audio/PulseAudioSourceFactory.cs |
| ONNX Runtime GenAI inference (CPU EP) | Done | via SpeechLib.Nemotron (`-p:GpuArch=CPU`) |
| Global hotkeys (XDG GlobalShortcuts portal, Wayland + X11) | Done | libraries/VoiceType.Hotkeys |

## Phase 2 — Text injection (DONE, 2026-08-21)

| Item | Status | Location |
|---|---|---|
| Session detection (Wayland/X11) | Done | Services/Platform/Linux/LinuxSession.cs |
| Clipboard backends (wl-copy / xclip / xsel) | Done | Services/Platform/Linux/LinuxClipboard.cs |
| Keyboard backends (XTest libXtst / ydotool / xdotool) | Done | Services/Platform/Linux/LinuxKeyboard.cs |
| Injector coordinator (clipboard + paste chord, typing fallback) | Done | Services/Platform/Linux/LinuxTextInjector.cs |
| Windows parity injector (SendInput + clipboard) | Done | Services/Platform/WindowsTextInjector.cs |
| Paste chord configurable in Settings (terminals: Ctrl+Shift+V) | Done | AppSettings.PasteChord + SettingsDialog |
| DI registration per-OS | Done | App.xaml.cs |

## Phase 3 — Settings, first-run, help (DONE, 2026-08-21)

| Item | Status | Location |
|---|---|---|
| Settings dialog (engine, audio, behavior) | Done | Presentation/SettingsDialog.xaml |
| Model downloader in settings + auto-download on first run | Done | SettingsDialog.xaml.cs + MainViewModel.InitializeModelAsync |
| FirstRunCompleted flag persisted | Done | AppSettings.FirstRunCompleted |
| Help dialog (hotkeys, injection tools, audio sources, data paths) | Done | Presentation/HelpDialog.xaml + Help button on MainPage |

## Phase 4 — Tray indicator (DONE, 2026-08-21)

| Item | Status | Location |
|---|---|---|
| StatusNotifierItem (AppIndicator) over D-Bus | Done | libraries/VoiceType.Hotkeys/StatusNotifier/StatusNotifierTrayIcon.cs |
| ITrayIndicator abstraction + Linux/Null implementations | Done | Services/Platform/TrayIndicator.cs |
| Recording state → tray status; icon click toggles recording | Done | MainViewModel (tray wiring) |

## Phase 5 — CI smoke test (DONE, 2026-08-21)

| Item | Status | Location |
|---|---|---|
| GitHub Actions ubuntu-latest: build + xvfb startup smoke, PulseAudio null source | Done | .github/workflows/voicetype-uno-linux-smoke.yml |

## Phase 6 — Remaining / future

| Item | Status | Notes |
|---|---|---|
| Audio mixer window | Not ported | WinUI version is NAudio/WASAPI-based; needs a PulseAudio/PipeWire-native mixer — defer until core UX is validated |
| Wayland global shortcuts without portal | Not planned | XDG portal covers GNOME 44+/KDE 5.27+; older compositors fall back to NullGlobalHotkeyService by design |
| Acceptance testing on real hardware | Pending | Hyper-V Ubuntu VM (X11 session) for hotkey/injection acceptance per README section "Manual testing" |

## Verification

- `dotnet build apps/VoiceType.Uno/src/VoiceType.Uno/VoiceType.Uno.csproj -p:GpuArch=CPU` — both heads (net10.0-desktop, net10.0-windows10.0.26100) build clean.
- WSL2 loop: `pwsh tools/scripts/run-voicetype-uno-linux.ps1`.
- Linux host: `dotnet publish ... -c Release -r linux-x64 -f net10.0-desktop -p:GpuArch=CPU` then run under X11/WSLg.
