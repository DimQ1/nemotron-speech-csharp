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

VoiceType types dictated text straight into the focused window and leaves the clipboard alone.

- **X11 / XWayland**: nothing to install. Typing uses libXtst (installed with the package), any keyboard layout,
  Unicode included.
- **Wayland (native windows)**: `sudo apt install ydotool`, with the `ydotoold` daemon running and your user in the
  `input` group. ydotool types ASCII only, so text with other characters (for example Russian) is pasted
  through the clipboard instead; for that install `wl-clipboard`.

Settings › Typing can switch the whole app to **Paste from the clipboard** (for apps that ignore typed
keys; needs `xclip` or `xsel` on X11, `wl-clipboard` on Wayland):

```bash
sudo apt install wl-clipboard ydotool  # Wayland
sudo apt install xclip                 # X11, only for Paste mode
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
