#!/usr/bin/env bash
# Test: run app and check crash cause
set -euo pipefail

export DISPLAY=:0 XDG_RUNTIME_DIR=/run/user/1000 PULSE_SERVER=/mnt/wslg/PulseServer
mkdir -p /run/user/1000

cd /home/voicetype/voicetype-uno
chmod +x VoiceType.Uno

echo "=== Running with crash capture ==="
./VoiceType.Uno 2>/tmp/voicetype-crash.log &
PID=$!
echo "PID=$PID"

sleep 6

echo "=== Stderr output ==="
cat /tmp/voicetype-crash.log 2>/dev/null || echo "(empty)"

echo "=== Stdout ==="
# check if app wrote anything to stdout (we redirected to devnull before, now capture)
ls -la /proc/$PID 2>/dev/null && echo "still alive" || echo "DIED"

echo "=== Check dmesg for segfault ==="
dmesg 2>/dev/null | grep -i "VoiceType\|segfault" | tail -3 || echo "no segfault"

kill $PID 2>/dev/null; wait $PID 2>/dev/null || true
echo "DONE"
