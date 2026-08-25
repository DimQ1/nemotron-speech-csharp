# VoiceType for Linux

This artifact contains the self-contained VoiceType.Uno installer for 64-bit Ubuntu Linux.
It uses the CPU inference backend and does not require a system-wide .NET installation.

## Requirements

- Ubuntu 24.04 or another compatible Debian-based AMD64 distribution
- X11 or XWayland desktop session
- PulseAudio or PipeWire with PulseAudio compatibility

## Install or update

Open a terminal in the artifact directory and run:

```bash
sudo apt install ./voicetype-uno_*_amd64.deb
```

Launch VoiceType from the application menu or run:

```bash
voicetype-uno
```

Installing a newer package updates the application without removing settings or downloaded models.
User data is stored under `~/.local/share/VoiceType`.

## Optional integration tools

For text injection, install the tools appropriate for your desktop session:

```bash
sudo apt install wl-clipboard ydotool  # Wayland
sudo apt install xclip xdotool         # X11
```

## Remove

```bash
sudo apt remove voicetype-uno
```

Downloaded models and settings remain in `~/.local/share/VoiceType` after removal.
Delete that directory manually only when you also want to remove user data.

## Verify download

```bash
sha256sum -c SHA256SUMS
```
