# VoiceType.Uno

Cross-platform port of the VoiceType.WinUI dictation app, built on **Uno Platform** (Skia Desktop).
Primary target platform: **Linux (Ubuntu, X11)**. Windows/macOS desktop work through the same Skia shell.

## Status

| Feature | Status |
|---|---|
| XAML UI (start/stop/mute/copy/inject/translate toggles, live transcript) | ✅ Ported |
| Settings persistence (settings.json, atomic writes) | ✅ Ported |
| Recognition pipeline (model lifecycle → capture loop → partial/final results) | ✅ Ported |
| Microphone capture on Linux (PulseAudio-compatible server: ALSA `default` and Pulse sources, 16 kHz mono) | ✅ Implemented |
| ONNX Runtime GenAI model inference (CPU EP) | ✅ Via SpeechLib.Nemotron (`-p:GpuArch=CPU`) |
| Global hotkeys on Linux (Wayland + X11) | ✅ `VoiceType.Hotkeys` library — XDG GlobalShortcuts portal (xdg-desktop-portal ≥ 1.18, GNOME 44+/KDE Plasma 5.27+); Null fallback on older compositors |
| Text injection on Linux | ✅ `LinuxTextInjector` — clipboard (wl-copy / xclip / xsel) + synthetic paste chord (XTest on X11, ydotool on Wayland, xdotool fallback); paste chord configurable in Settings |
| Text injection on Windows (WinUI 3 head) | ✅ `WindowsTextInjector` — SendInput + user32 clipboard (parity with VoiceType.WinUI) |
| Loopback ("WhatYouHear") capture on Linux | ✅ `PulseAudioSourceFactory` — `@DEFAULT_MONITOR@`; Mic / Loopback / Mix modes |
| Live translation (LiteRT-LM, Gemma 4) | ✅ In-process native backend via SpeechLib.LiteRT.Native (LiteRtLmSharp natives ship for win-x64 **and linux-x64** — no sidecar/server needed); HTTP backend (SpeechLib.LiteRT) as fallback when the model is not downloaded |
| Model downloader (auto-download on first run + button in Settings) | ✅ Ported |
| Settings dialog (engine, audio, translation, behavior) | ✅ Ported |
| Help dialog (hotkeys, injection tools, audio sources, data paths) | ✅ Added |
| Taskbar/tray recording indicator | ✅ Linux: StatusNotifierItem (AppIndicator) via `VoiceType.Hotkeys` D-Bus; icon click toggles recording |
| Linux CI smoke test | ✅ `.github/workflows/voicetype-uno-linux-smoke.yml` (xvfb + PulseAudio null source) |
| Audio mixer window | ⬜ Not ported (WinUI version is NAudio/WASAPI-based) |

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
                       ├─ VoiceType.Hotkeys      (IGlobalHotkeyService; XDG GlobalShortcuts
                       │                          portal backend on Linux, Null fallback)
                       └─ VoiceType.Uno
                            ├─ Services/          (platform-neutral: AppPaths, Settings, Recognition)
                            ├─ Services/Audio/    (AlsaAudioSourceFactory — Linux capture)
                            ├─ Services/Platform/ (IPlatformTextInjector + Null)
                            └─ Presentation/      (MainViewModel, MVVM via CommunityToolkit.Mvvm)
```

### Global hotkeys on Linux

Implemented in the standalone [VoiceType.Hotkeys](../../libraries/VoiceType.Hotkeys/README.md)
library via the **XDG GlobalShortcuts portal** (D-Bus):

- Works on **Wayland and X11** sessions with xdg-desktop-portal ≥ 1.18
  (Ubuntu 24.04+ GNOME, KDE Plasma 5.27+).
- First `RegisterAsync` triggers the compositor consent dialog once; the binding
  persists in the portal afterwards.
- On older/minimal compositors `TryCreateAsync` returns null and the app falls back
  to `NullGlobalHotkeyService` — hotkeys show as unavailable instead of failing silently.
- Chord format: `"Ctrl+Shift+Space"` (modifiers `Ctrl/Shift/Alt/Super` + key token).

Set the chord in Settings (`ToggleHotkey`); presses toggle recording via
`IGlobalHotkeyService.HotkeyPressed`.

## Build & run

The single project has two target frameworks:

| TFM | Head | Purpose |
|---|---|---|
| `net10.0-desktop` | Skia | **Primary** — Linux (X11); also runs on Windows/macOS for quick UI checks |
| `net10.0-windows10.0.26100` | WinAppSDK (WinUI 3) | Windows parity testing: native WinUI look, unpackaged (no MSIX identity clash with VoiceType.WinUI) |

```powershell
# Build both heads
dotnet build apps/VoiceType.Uno/src/VoiceType.Uno/VoiceType.Uno.csproj -p:GpuArch=CPU

# Run Skia desktop head on Windows (closest to the Linux build)
dotnet run --project apps/VoiceType.Uno/src/VoiceType.Uno/VoiceType.Uno.csproj -f net10.0-desktop -p:GpuArch=CPU

# Run WinUI 3 head on Windows (native controls)
dotnet run --project apps/VoiceType.Uno/src/VoiceType.Uno/VoiceType.Uno.csproj -f net10.0-windows10.0.26100 -p:GpuArch=CPU

# Ubuntu (X11 session)
sudo apt install libasound2 libfontconfig1
dotnet publish apps/VoiceType.Uno/src/VoiceType.Uno/VoiceType.Uno.csproj -c Release -r linux-x64 -f net10.0-desktop -p:GpuArch=CPU
```

Wayland sessions: hotkeys and text injection require the XDG GlobalShortcuts / RemoteDesktop portals — not implemented yet; run under X11 (XWayland) for now.

## Manual testing of the Linux build from a Windows machine

Linux-only functionality (ALSA capture, X11 hotkeys/injection, XDG paths) cannot run on Windows at all — it needs a real Linux userspace. Options, ordered by fidelity:

| # | Approach | Fidelity | Effort | Notes |
|---|---|---|---|---|
| 1 | **WSL2 + WSLg** (Windows 11) | High | Low | GUI Linux apps render via built-in Wayland/RDP. Mic via PulseAudio server or USBIPD. X11-specific code (XGrabKey/XTest) needs an X session — WSLg provides XWayland |
| 2 | **Hyper-V VM + Ubuntu (X11 session)** | Highest | Medium | Full GNOME/X11, real ALSA via Hyper-V audio or USB passthrough. Best for hotkeys/injection testing |
| 3 | **Docker + X11 socket mount** | Medium | Low | `docker run -e DISPLAY -v /tmp/.X11-unix:/tmp/.X11-unix` — works only on a Linux/WSL2 host with an X server (VcXsrv on Windows possible but flaky for input hooks) |
| 4 | **CI smoke test (GitHub Actions `ubuntu-latest`)** | Build/runtime smoke | Low | `xvfb-run` for a virtual X display; mic can be faked with ALSA `null`/`file` plugin. No real audio, no real hotkeys — build + startup validation only |

Recommended loop:

1. **WSL2 (WSLg)** for daily iteration — `dotnet publish -r linux-x64` on Windows, run the binary inside WSL.
2. **Hyper-V Ubuntu VM with X11** (`sudo apt install xserver-xorg`, log into "Ubuntu on Xorg" session) for hotkey/text-injection acceptance testing.
3. **CI** (`xvfb-run` + ALSA null device) to keep the Linux build green.

Quick WSL2 setup:

```powershell
# On Windows: attach a USB microphone to WSL (one-time, admin)
usbipd list
usbipd bind --busid <BUSID>
usbipd attach --wsl --busid <BUSID>
```

```bash
# In WSL2 Ubuntu
sudo apt install libasound2 libfontconfig1 pulseaudio
arecord -l                       # verify the mic is visible
./VoiceType.Uno                  # run the published binary; WSLg opens the window
```
