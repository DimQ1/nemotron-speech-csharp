# VoiceType for macOS

`VoiceType.Uno-macos-arm64.dmg` (or the `.zip` with the same app) holds VoiceType.app for Macs with Apple silicon
(M1 or newer). It is self-contained: no .NET installation is needed. Speech recognition runs on the CPU, on the Mac.

## Requirements

- Apple silicon (arm64)
- macOS 14 Sonoma or newer; capturing what the Mac plays (System audio, Mic + system) needs macOS 14.2 or newer

## Install

1. Open the `.dmg` and drag **VoiceType** to **Applications** (or double-click the `.zip` in Finder and move the app
   to Applications; unpack it with Finder or `ditto -x -k`, not with `unzip`, which drops parts of the signature).
2. Open VoiceType. The app is signed ad hoc, not by an identified developer, so the first start is refused:
   - macOS 15 and newer: **System Settings › Privacy & Security**, scroll to *"VoiceType" was blocked*, click
     **Open Anyway** and confirm.
   - macOS 14: right-click VoiceType in Applications, choose **Open** and confirm.

   The same can be done in Terminal: `xattr -dr com.apple.quarantine /Applications/VoiceType.app`

## Permissions

macOS asks the first time a recording uses a source:

- **Microphone**: allow it in the dialog, or later in **System Settings › Privacy & Security › Microphone**.
- **System audio** (System audio, Mic + system): **System Settings › Privacy & Security › Screen & System Audio
  Recording**, section *System Audio Recording Only*: turn on VoiceType.

When a permission is missing, the recording stops with a message that says which switch to turn on. Each new version
of the app is a new ad-hoc signature, so macOS may ask again after an update.

## What works on macOS

- Live dictation from the microphone, from what the Mac plays, or both mixed; the source can be switched while
  recording.
- Models, history, speakers, translation and chat as on the other desktops.
- Not yet: typing the text into other apps (use Copy), global hotkeys, playing recordings back in History.

## Data

Settings, downloaded models and recordings are kept in `~/Library/Application Support/VoiceType`. Deleting the app
leaves them there; delete that folder to remove them as well.

## Verify download

```bash
shasum -a 256 -c SHA256SUMS
```
