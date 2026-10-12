# Nemotron-3-Diarization → ONNX (feasibility spike)

[nvidia/Nemotron-3-Diarization](https://huggingface.co/nvidia/Nemotron-3-Diarization)
(Streaming Sortformer, up to 8 speakers, 100 M params, OpenMDW-1.1, not gated) exported to ONNX for
native use from C# with plain **Microsoft.ML.OnnxRuntime** (CPU EP, no GenAI), plus parity, DER and
real-time measurements. Status: **spike**. Nothing here is wired into `apps/` or `libraries/`.

| File | Purpose |
|---|---|
| `export_onnx.py` | HF Transformers model → `diar_step.onnx` (fp32), `diar_step.int8.onnx`, `diar_frontend.onnx`, `diar_config.json` |
| `diar_common.py` | front-end contract, NumPy port of the AOSC/FIFO state (`AoscState`), chunk schedule, ONNX driver, HF reference runners |
| `run_backends.py` | runs HF PyTorch / ONNX fp32 / ONNX int8 over a folder of wavs, saves the 10 ms probability matrices |
| `parity.py` | front-end parity, plus ONNX-vs-PyTorch probability and segment parity |
| `make_testset.py` | synthetic 2–4 speaker conversations from `Test-Audio/cv17/{en,ru}`, with RTTM |
| `prepare_ami.py` | 2 AMI test meetings (first 10 min) with the model card's forced-alignment RTTMs |
| `eval_der.py` | DER (pyannote.metrics, collar 0.25 s and 0 s, overlap scored) per backend × mode |
| [`../../DiarBench/`](../../DiarBench) | C# console on ORT **1.30.0** (same version as `SpeechLib.Providers`). It runs the same pipeline (front-end + step + AOSC in C#), dumps the probabilities for parity, and measures RTF, latency and memory. `bench.ps1` runs the benchmark matrix. |

## TL;DR

* **The export works and matches PyTorch.**
  * The step graph is stateless. The data-dependent speaker-cache logic lives in the host (C#, about 200 lines).
  * ONNX fp32 matches HF PyTorch to about 1e-5 in probability, with identical segments.
  * C# matches the Python ONNX driver to about 4e-6.
* **int8 (dynamic, MatMul weights) cuts the model from 398 to 105 MB.**
  * RAM drops from about 590 to about 270 MB.
  * Inference is 2–3× faster.
  * DER is unchanged within ±0.5 abs on both test sets.
* **Desktop: real-time is easy with int8.**
  * `low_latency` (1.04 s buffer) costs RTF 0.19 on 4 threads.
  * The NeMo-Speech.cpp-style `ncpp_v3_streaming` (1.12 s buffer, FIFO 80) costs RTF 0.10 on 4 threads, with the same DER.
  * `ultra_low_latency` (0.32 s) is not worth it: fp32 is not real-time on 4 threads, and int8 costs about 0.6 cores for no DER gain.
* **Phone (Kirin 980): estimated only, no device was attached.**
  * Live diarization at 1.04 s latency is not realistic alongside streaming ASR.
  * An `eco_2s` int8 configuration (2.08 s buffer) or an offline post-pass after recording (about 0.03–0.05 RTF) is realistic.

## Environment

```powershell
# Python 3.12, CPU only. The venv lives under the gitignored models/ folder. A venv in the
# %TEMP% scratch folder fails because of MAX_PATH while pip-installing transformers.
python -m venv models/_diar-venv
models/_diar-venv/Scripts/python -m pip install torch --index-url https://download.pytorch.org/whl/cpu
models/_diar-venv/Scripts/python -m pip install -r tools/converters/NemotronDiarization/requirements.txt
```

Versions used: torch 2.14.1+cpu, transformers 5.19.0.dev0 (git main; `nemotron3_diarization` is not in a
release yet), onnx 1.23.2, onnxruntime 1.31.0 (Python), Microsoft.ML.OnnxRuntime 1.30.0 (C#).
Reusing the global Python failed: its torch dev build reports no version metadata, which breaks the transformers import.

Weights: `config.json`, `processor_config.json` and `model.safetensors` (397 MB) from the HF repo, placed in
`models/nemotron-3-diarization-hf/`. Override the path with the `DIAR_HF_DIR` environment variable.

## Commands

Run from `tools/converters/NemotronDiarization`. `$S` is any scratch folder.

```powershell
$py = "../../../models/_diar-venv/Scripts/python"
& $py export_onnx.py                               # -> models/nemotron-3-diarization-onnx/ (DIAR_ONNX_DIR)
& $py make_testset.py --out $S/testset --n 12      # needs Test-Audio/cv17 (tools/eval/download_cv_test.py)
& $py prepare_ami.py  --out $S/ami                 # downloads 2 AMI Mix-Headset wavs (~60 MB) + RTTMs
& $py run_backends.py --audio $S/testset --out $S/runs --backends hf,onnx_fp32,onnx_int8 --threads 8
& $py parity.py       --audio $S/testset --runs $S/runs
& $py eval_der.py     --audio $S/testset --runs $S/runs
# C# parity dump + benchmark (run on an idle machine)
dotnet run -c Release --project ../../DiarBench -- --wav $S/testset/conv10_en_4spk.wav --mode low_latency --step diar_step.int8.onnx --threads 4 --dump $S/cs.f32
pwsh -File ../../DiarBench/bench.ps1 -Wav $S/testset/conv10_en_4spk.wav -Threads "4,8"
```

## Exported artifacts (`models/nemotron-3-diarization-onnx/`, gitignored)

| File | Size | Content |
|---|---|---|
| `diar_step.onnx` | 397.8 MB | fp32, opset 17, TorchScript exporter (`dynamo=False`) |
| `diar_step.int8.onnx` | 104.7 MB | `quantize_dynamic` QInt8, per-channel, MatMul only (190 MatMulInteger + 128 DynamicQuantizeLinear). The 62 attention MatMuls stay fp32. |
| `diar_frontend.onnx` | 1.2 MB | STFT as a strided Conv1d over a windowed real-DFT basis, then mel matmul and log. Uses no STFT op, so it runs on any execution provider. |
| `diar_config.json` | 13 KB | front-end spec, AOSC hyper-parameters, mode geometries, the learned 512-d `silence_embed` |

Offline (30.4 s) and every streaming mode use **the same graph**. Only the sizes you feed differ, and the host loop drives them.

## Architecture recap: why the graph is stateless

The model is a 31-layer RoPE transformer (hidden 512). Its input layer stacks 8 mel frames and projects the 1024 values to 512, giving an 80 ms frame. The head is Linear 512→192, then a sub-pixel Conv1d that upsamples to 10 ms, then a 2-layer classifier over 8 speakers with a sigmoid.

Streaming state is the Arrival-Order Speaker Cache (AOSC, 264 frames) plus a FIFO of recent frames. Every step re-encodes `[cache | fifo | chunk | lookahead]` with full bidirectional attention, and positions restart at 0 on every step. There is no KV cache to carry. The cache *update* is data-dependent: it pops from the FIFO and compresses the cache through score/top-k selection. So the update stays in the host:

* `AoscState` in `diar_common.py` ports `Nemotron3DiarizationSpeakerCache` line by line.
* `tools/DiarBench/AoscState.cs` is the C# port of the same logic.

This is the same split NeMo-Speech.cpp uses (`run_chunk` returns `chunk_embs` and `preds`, and `AoscState::update` runs on the host).

### `diar_step.onnx` I/O (batch fixed to 1, all float32)

| Name | Shape | Meaning |
|---|---|---|
| in `features` | `[1, 8·Ne, 128]` | log-mel frames of the chunk **plus** look-ahead. The caller zero-pads to a multiple of 8, exactly like HF feature stacking pads the last group. |
| in `cache_embeds` | `[1, Nc, 512]` | speaker-cache rows followed by FIFO rows (`AoscState.CopyCached`). Nc may be 0 on the first step. |
| out `chunk_embeds` | `[1, Ne, 512]` | projected input frames. Push the first `n_chunk` rows (not the look-ahead) into the FIFO. |
| out `probs` | `[1, 8·(Nc+Ne), 8]` | sigmoid speaker activity at 10 ms over the whole input. Emit rows `[8·Nc, 8·(Nc+n_chunk))`; on the last chunk, trim to the real mel count. |
| out `frame_probs` | `[1, Nc+Ne, 8]` | `probs` averaged over 8 frames (80 ms). This is the only input of the cache update. |

### Host loop for one step

C# reference: `DiarBench/Program.cs` `RunStreaming`.

```
cached = state.cached()                       # Nc = cache + fifo rows
ce, p10, p80 = step(features, cached)
state.update(ce[:n_chunk], p80)               # FIFO push, pop >= update_period when FIFO > fifo_len,
                                              # compress cache to 264 rows when it overflows
emit p10[8*Nc : 8*(Nc+n_chunk)]
```

AOSC constants: spkcache 264, 1 silence slot per speaker (filled with `silence_embed`), score threshold 0.25, latest-frames boost 0.05, strong boost on the top 24 frames per speaker, weak boost on the top 48, and at least 16 positive frames per speaker. Top-k ties are broken by the **lower index**, using a stable sort in both NumPy and C#. `torch.topk`'s tie order is implementation-defined; see Parity.

### Streaming configurations (80 ms encoder frames)

| Mode | chunk | right ctx | fifo | update period | Buffer latency | Hop | Cached frames (steady) | GFLOP per audio-s |
|---|---|---|---|---|---|---|---|---|
| offline (model card / `config.json`) | 340 | 40 | 40 | 300 | 30.4 s | 27.2 s | ≤304 | ~6 |
| `low_latency` (default) | 9 | 4 | 264 | 222 | 1.04 s | 0.72 s | 270–522 | ~134 |
| `very_low_latency` | 6 | 2 | 264 | 222 | 0.64 s | 0.48 s | 264–528 | ~199 |
| `ultra_low_latency` | 3 | 1 | 264 | 222 | 0.32 s | 0.24 s | 264–528 | ~394 |
| `ncpp_v3_streaming` (NeMo-Speech.cpp `v3-streaming`) | 13 | 1 | 80 | 40 | 1.12 s | 1.04 s | 273–344 | ~71 |
| `eco_2s` (spike proposal) | 24 | 2 | 48 | 48 | 2.08 s | 1.92 s | 264–312 | ~36 |

The cost of a step barely depends on the chunk length, because the cache (264 rows) and FIFO rows dominate the sequence. **The per-audio-second cost is set by the hop.**

### Chunk schedule

This schedule is identical to the HF processor's.

* **Chunk 0:** covers mel `[0, F)` with `F = (chunk + rc)·8`.
* **Chunk k ≥ 1:** covers `[k·chunk·8, k·chunk·8 + F)`. It runs once the audio reaches sample `k·chunk·8·160 − 256 + F·160 + 400`.
* **At end of stream:** one last chunk covers `[K·chunk·8, floor(N/160))`. It has no look-ahead; every frame is scored and the output is trimmed to the mel count.

Each chunk overlaps the next by `rc·8` mel frames (the look-ahead), so compute each mel frame once and cache it.

## Front-end spec

The mel front-end stays in ONNX (`diar_frontend.onnx`). Pre-emphasis and padding happen in the host.

| Parameter | Value |
|---|---|
| sample rate | 16 kHz mono float |
| pre-emphasis | `y[0]=x[0]; y[n]=x[n]−0.97·x[n−1]`, applied once to the continuous stream |
| centering | 256 zeros **after** pre-emphasis on the left. Mel frame k = `padded[160k : 160k+512]`. |
| window | Hann 400, **symmetric** (`periodic=False`), centered in the 512 FFT (56 zeros on each side) |
| STFT | n_fft 512, hop 160, power spectrum (\|X\|²) |
| mel | 128 bins, librosa **slaney** scale + slaney norm, fmin 0, fmax 8000 |
| log | `ln(mel + 2^-24)`. No mean/variance normalization. |
| valid frames | `floor(N/160)`. Frames that read past the end of the audio read zeros; this matches HF's last-chunk padding. |
| HF offline quirk | the offline extractor returns `floor(N/160)+1` frames, with the last one set to **0.0**. For bit parity in offline mode, DiarBench appends that zero frame. |

Verified against the HF `NemotronAsrStreamingFeatureExtractor` on two files:

* **ONNX front-end vs HF:** max |Δlog-mel| = 2.5e-4 and 7.8e-4. The log-mel range is −16.6…1.9.
* **Independent NumPy FFT reference vs HF:** 2.2e-4 and 7.1e-4. The residual is float32 STFT rounding in HF itself.
* **Streaming chunks:** every HF streaming chunk (98 / 294 / 129 / 386 chunks in `low_latency` / `ultra_low_latency`) equals the matching slice of one full-utterance mel within the same 7.8e-4. So the streaming host needs only the incremental front-end above.

Cost in C# is 0.3–1.3 ms per step.

## Parity

### ONNX vs HF PyTorch

4 synthetic files (en/ru, 2–4 speakers, 70–130 s each, 38k 10 ms frames per mode), HF `sdpa` vs the ONNX driver with the ONNX front-end:

| Backend | Mode | max \|Δp\| | mean \|Δp\| | Frames with a flipped 0.5 decision | Identical segment lists |
|---|---|---|---|---|---|
| onnx_fp32 | offline | 9.5e-6 | 5.5e-8 | 0 / 38165 | 4/4 |
| onnx_fp32 | low_latency | 3.3e-2 ¹ | 2.0e-5 | 13 / 38161 (0.03 %) | 2/4 ¹ |
| onnx_fp32 | very_low_latency | 1.1e-5 | 4.5e-8 | 0 / 38161 | 4/4 |
| onnx_fp32 | ultra_low_latency | 9.4e-6 | 4.3e-8 | 0 / 38161 | 4/4 |
| onnx_int8 | offline | 0.20 | 1.1e-3 | 70 (0.18 %) | 0/4 |
| onnx_int8 | low_latency | 0.68 | 1.9e-3 | 966 (2.5 %) | 0/4 |
| onnx_int8 | very_low_latency | 0.21 | 9.5e-4 | 620 (1.6 %) | 0/4 |
| onnx_int8 | ultra_low_latency | 0.15 | 6.5e-4 | 197 (0.5 %) | 0/4 |

¹ The table spans 4 files. Three of them are within 6e-6. On `conv05_ru_4spk`, the outputs diverge from step 78, which is the second cache compression. I checked the cause:

* The NumPy `_compress` reproduces HF's `_compress` **bit-exactly on identical inputs**. The state also matches to 1e-3 right before that step.
* The flip comes from the score ranking. Several frames' compression scores are equal to within float noise, so a 1e-6 difference in the step outputs selects different cache frames. After that, the trajectories diverge.

This is inherent to the algorithm: it also happens between a PyTorch build that uses different kernels and the original. It is harmless: DER is 9.46 % on both sides. The near-ties are not exact float ties; the HF and NumPy compressions agree on identical input.

int8 errors are larger than fp32 errors, but DER is the same (next section). Most flips sit at segment edges near p ≈ 0.5.

### C# (ORT 1.30) vs Python (ORT 1.31) ONNX driver

The Python driver uses the same stable tie-break as C#.

| Model | Modes × files | Result |
|---|---|---|
| fp32 | offline, low, ultra, eco_2s × 2 files | max \|Δp\| ≤ 3.6e-6, 0 flipped frames, identical segments everywhere. Offline is bit-exact. |
| int8 | same | bit-exact in 5 of 8 cases. `low_latency` conv05 differs by 0.07 with no flipped frames. `ultra_low_latency` differs by 0.07–0.18, with 2 and 81 flipped frames. `DynamicQuantizeLinear` + MLAS int8 kernels differ between ORT 1.30 and 1.31, and the AOSC amplifies the difference. |

## Accuracy (DER)

All numbers here use the raw 0.5 threshold per speaker, with no onset/offset hysteresis or padding. NeMo-Speech.cpp applies tuned post-processing, which would lower FA and miss.

### Synthetic CV17 conversations

The set has 12 files (6 en + 6 ru, 2/3/4 speakers, 71–181 s, 26.5 min total) built by `make_testset.py`, seed 0.

* **Speaker identity:** the CV17 rows API exposes the anonymous `client_id`. The builder matches it to `Test-Audio/cv17/*/NNNN.txt` by sentence, so different speakers are **guaranteed to be different `client_id`s**. That is not the same as different people, but is close enough. In the 250 en files every speaker has only one clip, so repeated turns reuse the **same** clip. In ru, 18 speakers have 2 or more clips.
* **Conversation structure:** turns of 1–3 clips, pauses of 0.2–1.0 s, and about 15 % of turn changes overlapping by 0.3–1.2 s. Overlap is 0–3.7 % of speech.
* **Reference:** an energy VAD per clip (gaps under 0.3 s merged, 50 ms pad).
  * **Miss** of about 8.5 % is mostly reference noise: missed frames have a median energy 30 dB below detected frames. It is the same for every system, so compare **confusion**.
  * A first version labelled whole clips; its miss was 15 %. Those RTTMs are kept as `.rttm_clip`.

Collar 0.25 s, overlap scored, 12 files:

| Mode | Backend | DER | Miss | FA | Conf | DER en | DER ru | DER collar 0 | spk-count acc |
|---|---|---|---|---|---|---|---|---|---|
| offline | onnx_fp32 | 10.81 | 8.45 | 0.38 | 1.99 | 14.63 | 7.33 | 14.70 | 83 % |
| offline | onnx_int8 | 10.82 | 8.41 | 0.40 | 2.01 | 14.70 | 7.29 | 14.71 | 83 % |
| low_latency | onnx_fp32 | 9.98 | 8.78 | 0.37 | 0.83 | 12.83 | 7.38 | 13.84 | 100 % |
| low_latency | onnx_int8 | 10.51 | 9.01 | 0.30 | 1.20 | 14.01 | 7.32 | 14.36 | 100 % |
| very_low_latency | onnx_fp32 | 10.83 | 8.78 | 0.33 | 1.72 | 14.47 | 7.51 | 14.69 | 92 % |
| very_low_latency | onnx_int8 | 10.60 | 8.90 | 0.30 | 1.40 | 14.06 | 7.45 | 14.46 | 100 % |
| ultra_low_latency | onnx_fp32 | 10.82 | 8.82 | 0.22 | 1.78 | 14.23 | 7.71 | 14.60 | 92 % |
| ultra_low_latency | onnx_int8 | 10.79 | 8.77 | 0.21 | 1.81 | 14.20 | 7.68 | 14.58 | 100 % |
| ncpp_v3_streaming | onnx_fp32 | 10.19 | 8.73 | 0.27 | 1.19 | 13.00 | 7.63 | 14.03 | 92 % |
| ncpp_v3_streaming | onnx_int8 | 10.12 | 8.80 | 0.38 | 0.94 | 12.83 | 7.64 | 13.94 | 100 % |
| eco_2s | onnx_fp32 | 9.97 | 8.63 | 0.51 | 0.84 | 12.59 | 7.58 | 13.84 | 92 % |
| eco_2s | onnx_int8 | 9.65 | 8.63 | 0.18 | 0.84 | 11.95 | 7.56 | 13.55 | 92 % |

HF PyTorch, scored on the 4-file parity subset, gives the same DER as ONNX fp32 to two decimals in every mode: offline 12.79, low 9.46, very-low 12.55, ultra 12.53. Speaker count accuracy counts a speaker when it is active for at least 1 s.

The DER table above was produced before the stable tie-break, with an arbitrary order (`argpartition`). Only the selection among near-tied frames changes, so these numbers are representative. They were not re-run.

### AMI test, Mix-Headset, first 10 min of ES2004a and IS1009a

Reference: the forced-alignment RTTMs the model card uses (nttcslab-sp). 4 speakers each.

| Mode | Backend | DER c=0.25 | Miss | FA | Conf | DER c=0 |
|---|---|---|---|---|---|---|
| offline | fp32 / int8 | 13.15 / 12.95 | 5.8 | 2.3 | 4.9 | 17.58 / 17.48 |
| low_latency | fp32 / int8 | 13.69 / 14.07 | 5.4–5.6 | 2.4–2.5 | 5.9 | 18.11 / 18.52 |
| ncpp_v3_streaming | fp32 / int8 | 14.41 / 13.41 | 5.6–5.9 | 2.3–2.4 | 5.4–6.2 | 18.46 / 17.97 |
| eco_2s | fp32 / int8 | 16.07 / 14.82 | 6.5 | 2.6 | 5.8–6.9 | 20.19 / 19.24 |

Compare with the model card's full AMI-test MHM numbers at collar 0: 9.25 % (30.4 s) and 9.48 % (1.04 s). Our numbers are higher for three reasons:

* only 2 × 10-minute crops;
* no post-processing;
* both meetings start with dense multi-party backchannel.

The ordering between modes is consistent. `eco_2s` costs about 1–2 abs DER on real meetings. With pyannote "only_words" labels, miss rose to 24 %, so the reference choice matters a lot.

## Performance (C#, ORT 1.30 CPU)

Test machine: Intel Core Ultra 7 255HX (8P + 12E, no SMT), 32 GB RAM, Windows 11. Workload: `conv10_en_4spk` (70.5 s), `GraphOptimizationLevel.ORT_ENABLE_ALL`, run on an otherwise idle machine.

"Sustained" is the mean step time once the cache is full, divided by the hop. The worst steps are the ones with the most cached frames.

| Model | Threads | low_latency | very_low | ultra_low | ncpp_v3 | eco_2s | offline (whole file) |
|---|---|---|---|---|---|---|---|
| fp32 | 1 | 756 ms → **1.05** | – | – | 600 ms → 0.58 | 584 ms → 0.30 | – |
| fp32 | 2 | 481 ms → 0.67 | – | – | 360 ms → 0.35 | 343 ms → 0.18 | – |
| fp32 | 4 | 297 ms → **0.41** (p95 388) | 302 → 0.63 | 296 → **1.23** ✗ | 248 → 0.24 | 229 → 0.12 | RTF 0.013 |
| fp32 | 8 | 317 → 0.44 | 308 → 0.64 | 297 → 1.24 ✗ | 255 → 0.25 | 225 → 0.12 | RTF 0.009 |
| int8 | 1 | 339 ms → 0.47 | – | – | 298 → 0.29 | 265 → 0.14 | – |
| int8 | 2 | 219 → 0.30 | – | – | 172 → 0.17 | 137 → 0.071 | – |
| int8 | 4 | 134 ms → **0.19** (p95 183) | 138 → 0.29 | 141 → 0.59 | 106 → **0.10** | 92 → 0.048 | RTF 0.0067 |
| int8 | 8 | 180 → 0.25 | 165 → 0.34 | 94 → 0.39 | 76 → 0.073 | 66 → 0.034 | RTF 0.0052 |

Other measurements:

* **Load time:**
  * fp32: 1.1–1.2 s for both sessions; the working set right after load is 471 MB.
  * int8: 0.83–0.90 s; 194 MB.
* **Peak working set:**
  * fp32: 550–610 MB.
  * int8: 260–295 MB.
* **Host work per step:**
  * front-end: 0.3–1.3 ms;
  * AOSC update: 0.1–0.6 ms mean, with a maximum of about 6 ms on a compression step;
  * GC heap: under 60 MB.
* **Thread count:** 8 threads are not faster than 4 on this hybrid CPU, because the E-cores join in. Use 4 intra-op threads.
* **Latency:** the end-to-end speaker-label delay is the buffer latency plus one step's compute. For example, `low_latency` int8 gives 1.04 s + ~0.13 s.

### Phone estimate (Huawei P30 Pro, Kirin 980)

**Not measured:** `adb devices` showed no device during the spike.

The estimate scales from the measured 1–2 thread desktop numbers:

* **Desktop single-thread throughput:** one desktop P-core sustains about 125 GFLOPS fp32 in this graph.
* **Phone throughput:** the 4 big Kirin 980 cores are 2×A76 at 2.6 GHz plus 2×A76 at 1.92 GHz. Their NEON fp32 peak is about 145 GFLOPS, and realistic MLAS throughput is about 50–70 GFLOPS (thermals included).
* **Scale factor:** that is about **0.4–0.55 × one desktop thread**.
* **int8:** the A76 has SDOT, so int8 is assumed to scale like on desktop (about 2.2×).

| Mode | Est. phone fp32 RTF (4 big cores) | Est. phone int8 RTF | Verdict |
|---|---|---|---|
| low_latency | 1.9–2.6 | 0.85–1.2 | ✗ (no headroom left for ASR) |
| ncpp_v3_streaming | 1.05–1.45 | 0.5–0.7 | ✗ / marginal |
| eco_2s | 0.55–0.75 | **0.25–0.35** | ✓ if ASR fits in the remaining budget |
| offline post-pass | ~0.03 | **~0.015** | ✓ (10 min of audio in about 10–20 s) |

Memory on the phone (int8): about 270 MB resident plus the model file. That is acceptable on a P30 Pro (8 GB), but not small next to Parakeet.

## Verdict

**Desktop (win-x64; linux-x64 should behave the same, untested): feasible.**

* **Recommendation:** int8, 4 intra-op threads.
* **Mode:** `low_latency` (1.04 s, sustained about 0.75 core). Alternatively `ncpp_v3_streaming` (1.12 s, about 0.4 core): it had the same or better DER on the synthetic set and was within ±1 on AMI.
* **Combined budget:** with Parakeet streaming on the other cores, the total budget fits a 20-core laptop comfortably. The combined load was not measured.
* **Default:**
  * **On**, for multi-party capture: loopback/mix meetings and the transcript-history view.
  * **Off**, for single-speaker dictation. There, diarization adds cost and no value.
* **`ultra_low_latency`:** do not ship it. It costs 3× more per audio second than `low_latency`, has no DER benefit, and fp32 is not real-time on 4 threads.

**Phone (android-arm64): live diarization at about 1 s latency is not feasible next to streaming ASR on a Kirin 980.**

* **Default:** **Off**.
* **Opt-in A:** `eco_2s` int8 (2.08 s buffer; speaker labels arrive about 2 s late and are fine for a transcript view).
* **Opt-in B (preferred):** run the offline configuration as a post-pass when a recording stops. It is about 30× cheaper per audio second than `low_latency` and more accurate.
* **Before any phone work:** run DiarBench (or an equivalent) on the device.

## C# integration notes

* **Runtime:** plain `Microsoft.ML.OnnxRuntime` `InferenceSession` (CPU). No GenAI is needed. Route EP selection through `ExecutionProviderSelector.Apply`, like the recognizers do.
  * One session (about 105 MB for int8) can serve many streams.
  * `AoscState`, the mel cache and the probability timeline are per stream.
  * The batch dimension is fixed to 1.
* **Two ORT pitfalls found in the spike:**
  1. **Do not pass `new RunOptions()` inline to `session.Run(...)`.** The GC can finalize it while the native Run is still executing, which causes an AccessViolation after a few hundred steps. Use one long-lived `RunOptions`.
  2. **An empty cache (Nc = 0) needs a native tensor.** `CreateTensorValueFromMemory` on a zero-length array pins a null pointer, so use `OrtValue.CreateAllocatedTensorValue(OrtAllocator.DefaultInstance, Float, [1,0,512])` instead.
* **Port as is:** `tools/DiarBench/AoscState.cs` (stable tie-break) and the incremental front-end (`Frontend.Padded` / `Extend`) can be ported as they are.
* **Speaker labels → ASR words:**
  * The ASR integration guide (`ASR_INTEGRATION_GUIDE.md`) pairs the model with a *multitalker* ASR, which runs one ASR stream per speaker. That is too expensive here, and the guide itself notes that single-speaker ASR plus diarization is "reasonable when speakers rarely overlap".
  * For our single-stream Parakeet TDT, attach labels by **time**. Parakeet's encoder frame is also 80 ms, so a token's frame index × 0.08 s maps directly onto the diarization timeline.
  * Per word `[t0, t1)`, take the mean of the 10 ms `probs` over an onset-anchored window. NeMo-Speech.cpp uses `[t0, min(t1, t0 + anchor))`, because punctuation emissions stretch word ends into the next turn. Pick the argmax speaker.
  * If no channel is above 0.5, inherit the previous word's speaker.
  * Words newer than the diarization frontier (buffer latency) get a provisional label: extrapolate the last emitted frame and revise it when the next chunk arrives.
  * Speaker indices follow arrival order and are stable within a session. They are not identities.
  * **Prerequisite:** `ParakeetTdtRecognizer` emits text only today. It must expose per-token absolute frame or time stamps (it already tracks them internally: `LastTokenEnd`, frame boundaries).
* **Post-processing:** for display, add per-speaker onset/offset hysteresis and minimum on/off durations. NeMo-Speech.cpp's defaults are onset 0.641, offset 0.561, pads 0.229 / 0.079 s and minimum durations 0.511 / 0.296 s. They are not tuned here.
* **Distribution:** the model would need an entry in `ModelCatalog` (`diar_step.int8.onnx` + `diar_frontend.onnx` + `diar_config.json`, about 106 MB). The OpenMDW-1.1 license must be shown.

## What is NOT verified

* **Hardware and platforms:**
  * Anything on a phone: no device was connected.
  * linux-x64 and android-arm64 ORT builds (only win-x64 was run).
  * NNAPI, XNNPACK, QNN or GPU EPs.
* **Real-time conditions:**
  * Running concurrently with Parakeet streaming ASR, so the combined CPU budget and contention are unmeasured.
  * Live microphone or loopback audio: everything ran from wav files, simulating streaming.
* **Coverage:**
  * More than 4 speakers.
  * Sessions longer than about 10 min.
  * Noisy or far-field audio. The AMI files are headset mixes.
  * Speaker-count accuracy beyond the small sets above.
* **Model and kernel variants:**
  * Static or QDQ int8, fp16, int4.
  * ORT transformer fusions (the RoPE-in-attention prevents the standard fusions).
  * Int8 numerics of ARM kernels.
* **Post-processing and labelling:** no tuned post-processing, and no word-level speaker attribution against an ASR transcript.
* **Reference parity:** exact parity with NeMo (`.nemo`) or NeMo-Speech.cpp (GGUF). Parity is against the HF Transformers port only, which NVIDIA's model card lists as supported.

## Production library

The spike pipeline now lives in `libraries/SpeechLib/src/SpeechLib.Diarization` (streaming and offline diarizers, speaker-cache port, word assignment, model locator),
tested in `libraries/SpeechLib/tests/SpeechLib.Diarization.Tests`. `tools/DiarizationEval` runs it over the synthetic test set (DER, RTF, memory, parity against DiarBench dumps).
To publish the ONNX files see `publish_hf.md`.
