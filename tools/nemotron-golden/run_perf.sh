#!/bin/bash
cd <repo>/tools/nemotron-golden
M=<repo>/models/asr
R="--root TestAudio=<repo>/Test-Audio --root golden=<golden>/audio"
IDS=cv17-en-0025,cv17-ru-0032,tts-long-en-0001
load() { powershell -NoProfile -c "\$s=1..4|%{(Get-CimInstance Win32_Processor).LoadPercentage;Start-Sleep -Milliseconds 500}; \$s -join ','"; }
for v in int4-c056 int4-c112 fp32-c056; do
  for t in 4 12; do
    echo "== $v threads=$t start $(date +%T) other-load-before=[$(load)] availMB=$(powershell -NoProfile -c "(Get-Counter '\Memory\Available MBytes').CounterSamples[0].CookedValue")"
    dotnet run genai_oracle.cs -- perf --model $M/nemotron-3.5-asr-streaming-0.6b-onnx-$v-cpu --variant $v --clips <golden>/nemotron/clips.json --ids $IDS --reps 3 --out <golden>/perf/genai-perf-$v-t$t.json $R --threads $t 2>&1 | grep -v "CPU config"
    echo "== $v threads=$t end $(date +%T) other-load-after=[$(load)]"
  done
done
echo "== perf all done $(date)"
