# GenAI 0.16.0 performance baseline (CPU)
ORT GenAI 0.16.0 offline decode time of the Nemotron streaming models, CPU provider. One warm-up run, then 3 timed runs per clip, aligned feeding (chunk_samples per call), fresh StreamingProcessor+Generator per run, model loaded once per process. seconds = feed+decode+flush wall time of the run (model load excluded). RTF = seconds / audio seconds. cpu = process CPU seconds consumed during a run.
Intel Core Ultra 7 255HX (20 logical cores), 31 GB RAM, Windows 11 26300, shared with other agents: other processes used 10-40 % CPU during the runs (sampled values below), so wall times are upper bounds
threads=12 is what ModelSession picks on this machine (ComputeOptimalIntraThreads(20 logical cores) = max(8, 20-8) = 12); threads=4 is the golden-run setting

| variant | threads | model load s | peak WS MB | clip | audio s | warm-up s | median s | median RTF | best s (of 4) | best RTF | median CPU s |
|---|---|---|---|---|---|---|---|---|---|---|---|
| fp32-c056 | 4 | 4.669 | 2489 | cv17-en-0025 | 9.576 | 5.017 | 4.658 | 0.4864 | 4.551 | 0.4753 | 15.906 |
| fp32-c056 | 4 | 4.669 | 2489 | cv17-ru-0032 | 8.928 | 4.064 | 4.335 | 0.4855 | 3.784 | 0.4238 | 14.172 |
| fp32-c056 | 4 | 4.669 | 2489 | tts-long-en-0001 | 58.295 | 29.104 | 24.651 | 0.4229 | 23.281 | 0.3994 | 83.062 |
| fp32-c056 | 12 | 4.072 | 2494 | cv17-en-0025 | 9.576 | 3.989 | 3.993 | 0.417 | 3.843 | 0.4013 | 29.188 |
| fp32-c056 | 12 | 4.072 | 2494 | cv17-ru-0032 | 8.928 | 3.568 | 3.718 | 0.4164 | 3.542 | 0.3967 | 26.391 |
| fp32-c056 | 12 | 4.072 | 2494 | tts-long-en-0001 | 58.295 | 24.023 | 23.182 | 0.3977 | 22.795 | 0.391 | 166.938 |
| int4-c056 | 4 | 1.815 | 990 | cv17-en-0025 | 9.576 | 2.238 | 2.86 | 0.2986 | 2.238 | 0.2337 | 8.578 |
| int4-c056 | 4 | 1.815 | 990 | cv17-ru-0032 | 8.928 | 2.698 | 2.53 | 0.2834 | 2.485 | 0.2783 | 7.484 |
| int4-c056 | 4 | 1.815 | 990 | tts-long-en-0001 | 58.295 | 17.203 | 17.495 | 0.3001 | 16.875 | 0.2895 | 50.188 |
| int4-c056 | 12 | 1.55 | 994 | cv17-en-0025 | 9.576 | 2.48 | 2.493 | 0.2603 | 2.316 | 0.2419 | 12.547 |
| int4-c056 | 12 | 1.55 | 994 | cv17-ru-0032 | 8.928 | 2.081 | 2.017 | 0.2259 | 1.953 | 0.2188 | 9.844 |
| int4-c056 | 12 | 1.55 | 994 | tts-long-en-0001 | 58.295 | 13.492 | 19.335 | 0.3317 | 13.492 | 0.2314 | 93.938 |
| int4-c112 | 4 | 2.295 | 1003 | cv17-en-0025 | 9.576 | 2.676 | 2.622 | 0.2738 | 2.582 | 0.2696 | 7.75 |
| int4-c112 | 4 | 2.295 | 1003 | cv17-ru-0032 | 8.928 | 2.722 | 2.51 | 0.2811 | 2.423 | 0.2714 | 7.344 |
| int4-c112 | 4 | 2.295 | 1003 | tts-long-en-0001 | 58.295 | 13.868 | 17.711 | 0.3038 | 13.868 | 0.2379 | 49.016 |
| int4-c112 | 12 | 1.435 | 1007 | cv17-en-0025 | 9.576 | 1.976 | 1.99 | 0.2078 | 1.933 | 0.2019 | 11.609 |
| int4-c112 | 12 | 1.435 | 1007 | cv17-ru-0032 | 8.928 | 2.122 | 1.866 | 0.2091 | 1.689 | 0.1892 | 8.953 |
| int4-c112 | 12 | 1.435 | 1007 | tts-long-en-0001 | 58.295 | 11.74 | 12.165 | 0.2087 | 11.74 | 0.2014 | 61 |
