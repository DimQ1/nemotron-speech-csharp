#!/usr/bin/env bash
export DISPLAY=:0
cd /root/voicetype-uno
./VoiceType.Uno >/dev/null 2>/dev/null &
PID=$!
sleep 6

python3 /mnt/e/Learn/nemotron-speech-csharp/tools/scripts/x11-list-windows-safe.py 2>/dev/null

echo "---"
cat /proc/$PID/status 2>/dev/null | grep State
kill $PID 2>/dev/null
wait $PID 2>/dev/null
echo DONE
