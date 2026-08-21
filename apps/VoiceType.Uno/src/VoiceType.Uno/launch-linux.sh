#!/usr/bin/env bash
# Launcher for VoiceType.Uno on Linux (WSL2/WSLg or native X11).
# Starts the app + a companion process that detects the X11 window,
# maps it (XMapWindow), and sets WM_CLASS so WSLg RAIL / window managers
# recognize and display it.
set -euo pipefail

# Locate app binary
SCRIPT_DIR="$(cd "$(dirname "${BASH_SOURCE[0]}")" && pwd)"
APP_DIR="${SCRIPT_DIR}"
if [ ! -x "${APP_DIR}/VoiceType.Uno" ]; then
    echo "VoiceType.Uno not found in ${APP_DIR}" >&2
    exit 1
fi

# Environment
export DISPLAY="${DISPLAY:-:0}"
export XDG_RUNTIME_DIR="${XDG_RUNTIME_DIR:-/run/user/$(id -u)}"
mkdir -p "$XDG_RUNTIME_DIR"
export PULSE_SERVER="${PULSE_SERVER:-/mnt/wslg/PulseServer}"

echo "Launching VoiceType.Uno (DISPLAY=$DISPLAY)..."

# Start the window fixer companion (Python3) in the background.
# Published bundles carry the fixer beside the app; source-tree runs use the
# repository copy as a fallback.
FIXER_SCRIPT="${SCRIPT_DIR}/x11-window-fixer.py"
if [ ! -f "$FIXER_SCRIPT" ]; then
    FIXER_SCRIPT="${SCRIPT_DIR}/../../../../tools/scripts/x11-window-fixer.py"
fi
if [ -f "$FIXER_SCRIPT" ]; then
    python3 "$FIXER_SCRIPT" &
    FIXER_PID=$!
    echo "Window fixer PID=$FIXER_PID"
else
    echo "Window fixer script not found at $FIXER_SCRIPT — window may not appear" >&2
    FIXER_PID=""
fi

# Run the app (foreground)
cd "$APP_DIR"
"${APP_DIR}/VoiceType.Uno"
APP_EXIT=$?

# Clean up
[ -n "$FIXER_PID" ] && kill $FIXER_PID 2>/dev/null || true
exit $APP_EXIT
