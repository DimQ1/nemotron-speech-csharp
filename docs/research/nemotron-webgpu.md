# Nemotron on WebGPU or CPU

Status 2026-10-23, plain-ONNX-Runtime Nemotron recognizer (`libraries/SpeechLib/src/SpeechLib.Providers/Nemotron`).
Measurements: one machine, Windows 11, Core Ultra 7 255HX (20 logical cores), Intel Graphics (integrated) + NVIDIA GeForce RTX 5070 Ti
Laptop GPU (discrete), ONNX Runtime 1.30.0 + `Microsoft.ML.OnnxRuntime.EP.WebGpu` 0.4.0. The older evaluation of the GenAI path (all three
graphs on WebGPU, 2026-09-28) is `docs/research/onnx/webgpu-ep-evaluation.md`; its Nemotron rows no longer apply.

**CPU is the verified default.** WebGPU is an opt-in that runs the **encoder only**, on a **discrete** graphics card. It is faster
(RTF 0.065 against 0.148 on this laptop) and takes the work off the processor (0.14 against 0.84 CPU seconds per audio second), but a 20-core
CPU is already 6x faster than real time, so the gain is CPU headroom and battery, not usability. The same measurements say that the
recognizer should not use the GPU anywhere else, and that the integrated GPU is slower than the CPU.

## What the recognizer does with a request

`NemotronProviderPolicy` decides, `NemotronSessions` creates the sessions, the engine reports the result in
`NemotronRnntRecognizer.ExecutionProvider` / `NemotronUtteranceRecognizer.ExecutionProvider` (`Requested`, `Effective`, `Adapter`, `Reason`).
The prediction network and the joint are **always** CPU-only. Rows are checked in this order, the first that applies wins:

| Request (stored setting) | Condition | Encoder runs on | Reason reported |
|---|---|---|---|
| `cpu`, `follow_config`, empty | always | CPU | none |
| `cuda`, `dml`, `tensorrt`, any other text | always (the ORT package has no such provider) | CPU | "runs on the CPU or on WebGPU only" (no warning: stale settings degrade quietly, as before) |
| `webgpu:lp`, `webgpu,power=low-power` | always, the plugin is not even asked | CPU | integrated graphics run Nemotron about 2x slower than the CPU (warning) |
| any `webgpu*` | plugin missing or no adapter (including ONNX Runtime older than 1.24.4, Android, iOS) | CPU | the plugin's own message (warning) |
| any `webgpu*` | adapters found but none reports `Discrete=1` (integrated only, or the plugin does not say) | CPU | "no discrete graphics card found (adapters: ...)" (warning) |
| any `webgpu*` | a stale crash marker of an earlier run (see below) | CPU | "process N ended while it was using WebGPU ..., until it is re-enabled" (warning) |
| any `webgpu*` | another recognizer of this process holds WebGPU, or WebGPU failed earlier in this process | CPU | "another recognizer in this process already uses WebGPU" / "WebGPU failed earlier in this process (...)" (warning) |
| `webgpu`, `webgpu:hp`, `webgpu:high` | a discrete adapter, nothing above applies | **WebGPU** on the discrete adapter, `powerPreference=high-performance` | none |
| `webgpu:<index>`, `webgpu,layout=...,capture=...,int64=...,cache=...,maxssb=...,forcecpu=...,power=<other>` | as the row before | **WebGPU**, index and options ignored | "ignored for Nemotron: the adapter index, the provider options" (warning) |
| any WebGPU encoder | its session cannot be created (`EP_FAIL`), or its first (warm-up) run throws | CPU, the encoder is created again | one warning with the exception text; WebGPU is closed for the rest of the process |
| any WebGPU encoder | a later run throws | the chunk fails, the stream is faulted (`IsFaulted`); at the next `ResetStreamingState()` the encoder moves to CPU | warning at the reset; WebGPU is closed for the rest of the process |

Stored values stay valid: `cpu`, `webgpu:hp`, `webgpu:lp`, `follow_config` (the lists of both apps), `webgpu`, `webgpu:<index>` and raw
options of earlier experiments are all parsed. Which adapter runs is decided by Dawn from the power preference, not by the device index
(measured: `webgpu`, `webgpu:hp` and index 0, which is the Intel adapter, all ran on the NVIDIA card; the order of the EP device list is not
stable between runs or machines), so the policy hands the discrete device to ONNX Runtime and asks for `high-performance`. On a machine with
two discrete cards the card that runs is Dawn's choice and unmeasured.

## Why: the measurements (W1, `work/nemotron-migration/reports/W1-webgpu.md`)

INT4 c056 (560 ms chunks), 42 clips, interleaved runs on a quiet machine:

| Configuration | RTF | chunk p95 | process CPU s per audio s | clip texts equal to the all-CPU run |
|---|---|---|---|---|
| all CPU | 0.148 to 0.154 | 89 to 95 ms | 0.84 | 126/126 |
| **encoder WebGPU, prediction net + joint CPU** | **0.065 to 0.068** | **45 to 47 ms** | **0.14** | 102/126 |
| all three graphs on WebGPU | 0.071 | 52 ms | 0.14 | 102/126 |
| encoder CPU, prediction net + joint WebGPU | 0.173 | 126 ms | | 126/126 |
| encoder on the integrated GPU (`webgpu:lp`, paced) | 0.32 (chunk 176 to 180 ms) | 199 ms | | |

Per call the prediction net takes 0.26 ms on the CPU and 1.80 ms on WebGPU, the joint 0.24 and 0.49 ms; the encoder takes 72 ms on the CPU and 26 ms
on the discrete GPU (about 1750 dispatches per chunk: launch bound, no option of the plugin gains more than 10 percent, `enableInt64=1` crashes the
process, `enableGraphCapture=1` is rejected at session creation). Quality over 500 Common Voice clips (en + ru): INT4 c056 WER 18.98 % on the CPU,
19.46 % on WebGPU (+0.49 pp, 15 to 18 % of the clips differ by a word or a mark); c112 +0.07 pp; **fp32 gives the same text on WebGPU and CPU on
200 of 200 clips**. The GPU's float arithmetic is not the problem; INT4 is a different approximation on each device. The model precision matters
more than the device (fp32 is 3.8 pp better than INT4 on both).

CPU threads: the three graphs share one thread count. One thread for the two tiny graphs was measured here (3 interleaved rounds x 40 clips,
INT4 c056): RTF 0.166 to 0.168 against 0.151 to 0.158 with the shared count, text identical, so it stays as it was.

### Checked again with the shipped code (this machine, `E2E_NemotronWebGpuTests`, child processes)

* INT4 c056, 24 clips (12 en + 12 ru): WER 27.92 % on the CPU, 28.43 % on WebGPU (+0.51 pp, +1 word), 4 of 24 texts differ.
* fp32 c056, the same 24 clips: identical text on all 24 (WER 20.81 % both).
* `WerEval nemotron --streaming --ep webgpu:hp` over 12 clips: RTF 0.064 (CPU about 0.15). The crash-marker folder is empty after the clean exit.

## Failure handling and its limits

* **Guarded session creation.** The plugin accepts any options when the provider is appended and fails only in `new InferenceSession`
  (`OnnxRuntimeException ... CreateProvider ... EP_FAIL`: a bad option, a driver that cannot create the device, no D3D12, out of video memory).
  A CPU session that fails still throws.
* **Warm-up.** After creating a WebGPU encoder the engine runs it once on silence and resets, while the model loads. The first run builds the
  pipelines: 0.4 to 0.6 s with a warm driver shader cache, 2.7 s the first time, 45 s on a cold integrated GPU. Doing it in the first chunk of
  speech would be a visible stall. The reset leaves the stream untouched (tests: the tokens equal the CPU engine's from chunk 0).
* **Crash marker** (`WebGpuCrashMarker`). A native fault cannot be caught, so a file `active-<pid>.marker` in
  `%LOCALAPPDATA%\SpeechLib\webgpu` (`SPEECHLIB_WEBGPU_STATE_DIR` moves it) is written before the WebGPU session is created and removed after the
  warm-up run and three chunks went through, or when the recognizer is disposed cleanly. A marker whose process no longer exists (the start
  time in the file guards against a reused id) means that process died while it was starting on WebGPU. From then on every start runs the
  CPU, with the reason in `ExecutionProvider.Reason` and one warning, until the file is deleted, or one start sets `SPEECHLIB_WEBGPU_RETRY=1`
  (it clears the stale markers first), or an app calls `NemotronRnntRecognizer.ClearWebGpuCrashMarker()` (for a Settings button; the apps do not call it yet). The window of protection is the start (creation, warm-up,
  the first chunks): a crash after thousands of runs, or a process that is killed while recording on WebGPU, is not marked. The marker of a live
  process, also of another one using the GPU at the same time, is not stale.
* **One WebGPU recognizer per process** (`WebGpuProcessGate`). Two recognizers with a WebGPU session each, on two threads of one process, crashed or
  hung 3 of 3 times; two processes are fine. The History offline job, the second engine of `TranscribeWords` and benchmarks therefore run the CPU
  while a live recognizer holds the gate. A recognizer that is never disposed gives the gate back when it is collected.
* The engine keeps one `RunOptions` and host-resident tensors (the measured safe pattern; the engine ran 11 hours of audio without a crash).

## How the plugin reaches the product

`SpeechLib.Providers.csproj` has a `PackageReference` to `Microsoft.ML.OnnxRuntime.EP.WebGpu` 0.4.0. It flows transitively into every project that
references `SpeechLib.Providers`, and the SDK copies its `runtimes/<rid>/native` assets into each output; a post-build target
(`CopyWebGpuPluginNativeAssets`) copies the win-x64 files into the Providers output folder as well. Found in the build outputs of this worktree:

| Output | WebGPU plugin files present |
|---|---|
| `NemotronSpeech` CLI, `WerEval`, test projects | `runtimes/win-x64/native` and `runtimes/win-arm64/native` |
| `VoiceType.Uno` desktop head and WinAppSDK head (win-x64) | next to the executable (`onnxruntime_providers_webgpu.dll`, `dxcompiler.dll`, `dxil.dll`) |
| `VoiceType.WinUI` (win-x64) | output root and `AppX` folder |
| `VoiceType.Uno` Android head | none: the package has no android runtime, a `webgpu*` request finds no plugin and runs the CPU |

Size of the payload per platform (NuGet package, uncompressed): win-x64 29.0 MB (`onnxruntime_providers_webgpu.dll` 10.4 + `dxcompiler.dll` 17.2 +
`dxil.dll` 1.4), win-arm64 33.8 MB, linux-x64 15.7 MB, linux-arm64 13.2 MB, osx-arm64 14.3 MB (about 11 percent of the 267 MB Uno desktop output
on Windows). Linux additionally needs a system Vulkan loader (`libvulkan.so.1`); without it registration fails and the request runs the CPU.
`WebGpuExecutionProvider` looks for the library through `SPEECHLIB_WEBGPU_PLUGIN`, `runtimes/<rid>/native` and the application folder.

Packaging options (not decided here, this is a product decision): keep it as it is; restrict the package reference to the platforms where WebGPU
was verified (win-x64, perhaps win-arm64) so that Linux and macOS packages do not carry 14 to 16 MB of unverified code; drop the reference and ship
the plugin as an optional download that is registered from a path (the loader already accepts a path); or remove WebGPU from the product, since
on this laptop it is a CPU-headroom feature and not a usability one.

## Not verified

Other GPUs (AMD, Intel Arc, older NVIDIA, desktops with two discrete cards, integrated-only machines other than this Intel Graphics); the
`Discrete` metadata on Linux and macOS (the policy treats a missing entry as "not discrete", so those platforms run the CPU until somebody
checks); Linux/Vulkan, macOS/Metal and Windows ARM64 (the plugin ships there, nothing was run); Android and iOS (no plugin); device lost / TDR (never induced:
it resets the display driver, so a lost device is treated as an unknown that either throws, which moves the encoder to the CPU, or crashes, which
leaves the marker); packaged (MSIX) apps with the setting selected; runs longer than about 3 hours on one configuration; the effect of the app's own
GPU use on latency (a GPU that idles between chunks drops its clock: paced p95 chunk time 171 ms against 88 ms on the CPU, still far below the 560 ms
chunk period); any change of the plugin or ONNX Runtime version (re-run `E2E_NemotronWebGpuTests` and the crash cases after a bump).

## Tests

* `Unit_NemotronProviderPolicyTests`, `Unit_NemotronSessionsTests`, `Unit_WebGpuProcessGateTests`, `Unit_WebGpuCrashMarkerTests`: no GPU, plugin or model
  (fake adapters, marker, gate and session factory; a hand-built one-operator ONNX model for the session set).
* `E2E_NemotronProviderEngineTests`: the real INT4 model with a fake WebGPU (an adapter list that reports a discrete card and a factory that builds
  CPU sessions): provider split, warm-up, marker, the move to the CPU after a failed run (`RunOptions.Terminate`), second engine, stale marker.
* `E2E_NemotronWebGpuTests`: the real plugin on a discrete GPU, **always in a child process** (`WerEval`, 6 minute limit, own marker folder; a crash is a failed
  test with the exit code). Skipped with the reason when `WerEval` is not built, the model or audio is missing, the plugin does not load or no adapter
  is discrete; the fp32 test also needs the fp32 model and 6 GB of free memory.
