#!/usr/bin/env bash
# Test-run VoiceType.Uno with diagnostics
set -euo pipefail

export DISPLAY=:0
export XDG_RUNTIME_DIR=/run/user/0
export PULSE_SERVER=/mnt/wslg/PulseServer
export DOTNET_ENVIRONMENT=Development
export UNO_PLATFORM=SKIA_X11

cd /root/voicetype-uno
chmod +x VoiceType.Uno

echo "=== Starting VoiceType.Uno ==="
./VoiceType.Uno &
PID=$!
echo "PID=$PID"

sleep 4

echo "=== Process state ==="
cat "/proc/$PID/status" | grep -E "State|Threads" || echo "process gone"

echo "=== Socket FDs ==="
ls -la "/proc/$PID/fd" 2>/dev/null | grep -c "socket" || echo "0 sockets"

echo "=== Check if libSkiaSharp in memory ==="
cat "/proc/$PID/maps" 2>/dev/null | grep -i "libSkiaSharp\|libX11" | head -3 || echo "maps not found"

echo "=== X11 window test (python) ==="
python3 -c "
import ctypes
libx11 = ctypes.CDLL('libX11.so.6')
libx11.XOpenDisplay.restype = ctypes.c_void_p
d = libx11.XOpenDisplay(None)
print(f'XOpenDisplay: 0x{d:x}' if d else 'XOpenDisplay: FAILED')
" 2>&1 || echo "python test failed"

echo "=== Killing app ==="
kill $PID 2>/dev/null || true
wait $PID 2>/dev/null || true
echo "=== DONE ==="
