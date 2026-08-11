#!/usr/bin/env bash
# Final test: run VoiceType.Uno with X11 fix and check WSLg RAIL registration
set -euo pipefail

export DISPLAY=:0 XDG_RUNTIME_DIR=/run/user/1000 PULSE_SERVER=/mnt/wslg/PulseServer
mkdir -p /run/user/1000

cd /home/voicetype/voicetype-uno
chmod +x VoiceType.Uno

echo "=== Starting VoiceType.Uno (fixed) ==="
./VoiceType.Uno >/dev/null 2>/dev/null &
PID=$!
echo "PID=$PID"

sleep 8

echo "=== WSLg RAIL registration ==="
grep -i "VoiceType\|rail_shell\|appId\|GetAppidReq" /mnt/wslg/weston.log | tail -10

echo ""
echo "=== Process state ==="
cat /proc/$PID/status 2>/dev/null | grep State || echo "process gone"

echo "=== Window map state ==="
python3 /mnt/e/Learn/nemotron-speech-csharp/tools/scripts/x11-list-windows-safe.py

echo ""
echo "=== Keeping app alive for 5 more seconds ==="
sleep 5
echo "=== Final RAIL log ==="
grep -i "VoiceType\|rail_shell\|appId" /mnt/wslg/weston.log | tail -5

kill $PID 2>/dev/null; wait $PID 2>/dev/null
echo "DONE"
