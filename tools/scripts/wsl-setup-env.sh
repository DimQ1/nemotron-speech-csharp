#!/usr/bin/env bash
# Setup VoiceType.Uno runtime dependencies inside WSL2 Ubuntu 24.04.
# - ALSA (microphone capture backend)
# - fontconfig + fonts (Skia rendering)
# - xdg-desktop-portal (GlobalShortcuts D-Bus interface; WSLg session)
set -euo pipefail

export DEBIAN_FRONTEND=noninteractive

apt-get update -qq
apt-get install -y -qq \
  libasound2t64 \
  alsa-utils \
  libfontconfig1 \
  fonts-liberation \
  libice6 libsm6 libx11-6 libxext6 libxrender1 \
  xdg-desktop-portal \
  dbus-x11

echo "--- versions ---"
whoami
echo "DISPLAY=${DISPLAY:-<unset>}"
echo "WAYLAND_DISPLAY=${WAYLAND_DISPLAY:-<unset>}"
echo "PULSE_SERVER=${PULSE_SERVER:-<unset>}"
ls /mnt/wslg 2>/dev/null || echo "/mnt/wslg missing"
dpkg -l xdg-desktop-portal | tail -1
arecord -l 2>&1 | head -5 || true
echo "--- done ---"
