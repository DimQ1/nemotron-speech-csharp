# SpeechLib.LiteRT.Native

In-process translation with a Gemma 4 model in `.litertlm` format, loaded through
LiteRT-LM via the [LiteRtLmSharp](https://github.com/DimQ1/LiteRtLmSharp) fork
(1.3.0, native LiteRT-LM v0.18.0 — published on the fork's GitHub Packages feed; upstream
publishes only up to 1.2.0 / native v0.16.0 on nuget.org). No sidecar server: the model runs
inside the application process.

## Restoring the packages

LiteRtLmSharp 1.3.0 is not on nuget.org (upstream publishes only up to 1.2.0), so its `.nupkg` files are
fetched once from the fork's public `v1.3.0` release into a local folder feed that the root
[nuget.config](../../../../nuget.config) maps the `LiteRtLmSharp*` packages to — no token and no GitHub
CLI involved:

```powershell
pwsh tools/scripts/fetch-litertlmsharp-feed.ps1   # once per clone (CI does the same before building)
dotnet build NemotronSpeech.slnx -c Release
```

The packages are also published to the fork's GitHub Packages feed
(`https://nuget.pkg.github.com/DimQ1/index.json`); consuming that feed directly instead needs a token
with the `read:packages` scope, because GitHub requires an authenticated read even for public packages.
See [nuget-feed/litertlmsharp/README.md](../../../../nuget-feed/litertlmsharp/README.md) for both paths.

To fall back to the released engine, pin the four `LiteRtLmSharp*` references in
[SpeechLib.LiteRT.Native.csproj](SpeechLib.LiteRT.Native.csproj) back to `1.2.0` (native v0.16.0) — that
version restores from nuget.org, and the mapping still allows it.

## Model

- Hugging Face: `litert-community/gemma-4-E2B-it-litert-lm`, file `gemma-4-E2B-it.litertlm`
  (≈2.6 GB). The apps download it into their `Models/Translation` folder.
- The repo also carries `gemma-4-E2B-it-gpu.litertlm` (GPU-tuned weights) and device
  variants; the apps use the plain file for both `cpu` and `gpu` backends.
- Larger Gemma 4 LiteRT-LM bundles exist (E4B, 12B, 26B-A4B, 31B) but are impractical on
  CPU for live translation.

## The engine host (`GemmaEngineHost : ILlmHost`)

The process has exactly **one** Gemma engine and `GemmaEngineHost` is its only owner. Live translation, chat,
saved-transcript translation and titles all go through `SpeechLib.Llm.ILlmHost`; nothing else touches LiteRtLmSharp.
Design numbers are from `docs/research/litert/engine-improvements.md`; the real-model measurements are
under *Measured* below.

- **Lazy engine.** The model loads at the first request (`MaxNumTokens` 4096, about 3.2 GB, CPU, path from
  `GemmaEngineHostOptions.ModelPath`), and is unloaded after `IdleUnloadAfter` (default 10 min) and on dispose;
  the next request reloads it (2.4-2.9 s measured with a warm file cache, about 6 s cold). `IsModelInstalled`,
  `ContextTokens`, `Status` / `StatusChanged` (state, running priority, queue length, a "waiting for a cancel point"
  detail while a higher class waits for a prefill) are available at any time without blocking.
- **One native operation in flight.** A single worker loop is the "lease": `CreateConversation`, every send, resume,
  disposal and the engine load run there, because the runtime serializes sends without priorities and blocks
  `CreateConversation` / `TokenCount` while anything generates. `CountTokens` uses the tokenizer (does not block);
  `GetConversationTokenCount(key)` reads `TokenCountCached`.
- **Priority queue.** `Live` > `Interactive` > `Background`, FIFO inside a class (a preempted request keeps its place
  ahead of newer requests of its class).
- **Preemption by cancellation.** A higher class arriving while a lower one generates cancels it through its token
  (about 40 ms during decode; 3-112 ms measured between "queued" and "live running"). A prefill cannot be cancelled,
  so the higher request waits for it and `Status.Detail` says so.
  - *Background* is re-run from its start once the engine is free. Its text is delivered whole at the end (a
    half-streamed attempt would show duplicates), so callers send small idempotent units (500-800 prompt tokens).
  - *Interactive* keeps its enumerator open and yields nothing while preempted. It continues from the text already
    yielded: `conversation.Snapshot(partial)` + `LiteRtConversation.Resume()` on a fresh conversation (history plus
    the partial reply as a model turn), then one extra user turn (`ContinuationPrompt`, "Continue your previous
    answer exactly where it stopped...") streams the rest. Measured coherence: usable, not guaranteed. The model often
    restarts the unfinished sentence ("The cycle begins in" + "The cycle begins in the evaporator") or opens with an
    ellipsis, so `ContinuationJoiner` holds back the first characters, strips the repeated overlap and the ellipsis,
    and streams the rest untouched; the meaning right after the cut can still drift a little. Preempted before the
    first text, the answer simply restarts. If the snapshot cannot be taken the request fails with a clear
    `InvalidOperationException` (never silent duplicates or gaps). Two other continuation prompts were tried and
    rejected: they made the model wrap up with one short sentence instead of continuing.
  - A cancelled `LiteRtConversation` is dead: the host always drops it and, for a chat, rebuilds or resumes it.
- **Stateful chats.** A request with a `ConversationKey` keeps a live `LiteRtConversation` per key (LRU, at most
  `MaxLiveConversations` = 3, about 37 MB each). The caller still sends its whole history; the host compares it
  (roles and trimmed texts, plus the system prompt) with what the live conversation holds: a match sends only the
  new user turn (follow-up 0.5-1.1 s), otherwise the conversation is rebuilt by replay (4.7-7.5 s for a 1 k-token
  history). Conversations are dropped on `ReleaseConversation`, LRU eviction, `IsContextFull`, any
  failure/cancellation, and engine unload.
- **Stateless requests** (no key: live translation, background units) use a fresh conversation per request
  (13 ms to create, memory does not grow).
- **Samplers.** No explicit sampler by default: the model's own (top-k 40 / top-p 0.95 / temperature 1.0). Only when
  `LlmSampling.Temperature/TopP` are set does the host ask for a top-p sampler; if the backend refuses
  (`LiteRtSamplerNotSupportedException` or a failed first send) it retries **once** on a fresh conversation without
  it and stops asking for the life of the host. Greedy/TopK are not implemented on CPU, so the host never asks for
  them.
- **Errors.** `LiteRtContextOverflowException` becomes `SpeechLib.Llm.LlmContextOverflowException` (token count and
  window); a native error after the caller's token fired becomes `OperationCanceledException`; a missing model or a
  failed load is `LlmUnavailableException` (a later request retries the load); everything queued fails with
  `LlmUnavailableException` when the host is disposed, which also releases every conversation and the engine.
- **Testability.** The native types sit behind `IGemmaEngine` / `IGemmaConversation` (`Engine/IGemmaEngine.cs`,
  adapter `NativeGemmaEngine`), so `libraries/SpeechLib/tests/SpeechLib.LiteRT.Native.Tests` drives the scheduler with
  a fake engine that also asserts no two native operations overlap.

### Live translation (`LiteRTLmNativeTranslator`)

A thin `ITextTranslator` client of the host, every request at `Live` priority: system prompt from
`SpeechLib.Translation.TranslationPrompt`, the previous source/translation pair as a prior turn (so pronouns,
terminology and register stay consistent), then the sentence; a no-repeat 8-gram on Live replies stops decode loops.

- **Shared host** (`new LiteRTLmNativeTranslator(host, options)`): used by the Uno app; creating or disposing the
  translator is cheap and never touches the engine.
- **Own host** (`new LiteRTLmNativeTranslator(options)`): the WinUI app and the CLI; the translator creates a private
  `GemmaEngineHost`, loads it before returning, and disposes it with itself. Only one engine may be alive per
  process, so an app that also has a shared host must use the shared form.
- **Coalescing.** A newer revision of a sentence (its text extends the older one) cancels the older request while it
  has not produced anything yet.
- `LiteRTLmNativeOptions.Greedy` is kept for source compatibility and has no effect (the CPU backend never
  implemented Greedy; the model's own sampler applied all along).

Sample timing, 20-core laptop CPU: one 15-word English sentence into Russian, first chunk 0.9-1.2 s and about
2.2-3.3 s in total (the 95-token system prompt is part of that: with a 4-word prompt the first chunk is 0.55-0.65 s).

### Measured on the real model

`Real_GemmaEngineHostTests` (Core Ultra 7 255HX, gemma-4-E2B-it, CPU, 4096 tokens; other processes were loading
models on the same machine during some runs):

| Check | Result |
|---|---|
| Engine load (warm cache) / cold first load | 2.3-2.9 s / 5.9 s; private bytes 3.13 GB, 3.22 GB after the first send |
| Live translation alone, first chunk | 0.93-1.19 s (total 2.2-3.3 s) |
| Live translation during a chat decode | first chunk 0.95-1.36 s (about +0.1-0.2 s over alone); chat cancelled and live running 3-112 ms after queuing |
| `LiveTranslationSession` over the shared host while a chat generates | two sentences translated in 3.7 s |
| Chat follow-up, stateful vs replay (1 k-token history) | 0.49-0.68 s vs 4.7-5.1 s (busier machine: 0.9-1.1 s vs 7.1-7.6 s) |
| Background unit (450 prompt tokens, 300 reply tokens) preempted at +3 s by Live | live first chunk 1.1-1.2 s; the unit is re-run whole and its text delivered once |
| Soak: 100 mixed requests (live, chat with 4 keys, background; five overlapping) | 0 failures; private bytes 3.22 GB to 3.30-3.33 GB, flat from the 25th request (the delta is the 3 live conversations) |
| Idle unload (6 s configured) | unloaded after 6.0 s; private bytes 3.2 GB to 2.2-2.7 GB (the native allocator keeps part); reload 2.7-3.1 s and back to 3.22 GB, no growth over load/unload cycles |

Run them with `SPEECHLIB_GEMMA_MODEL=<path to gemma-4-E2B-it.litertlm> dotnet test
libraries/SpeechLib/tests/SpeechLib.LiteRT.Native.Tests -c Release --filter "FullyQualifiedName~Real_"` (without the
variable they are skipped); `SPEECHLIB_GEMMA_REPORT=<file>` also appends the numbers to a file.

## Usage

```csharp
using SpeechLib.LiteRT.Native;
using SpeechLib.Translation;

// An app shares ONE host between live translation, chat and background jobs:
await using var host = new GemmaEngineHost(new GemmaEngineHostOptions { ModelPath = @"C:\models\gemma-4-E2B-it.litertlm" });
using var translator = new LiteRTLmNativeTranslator(host, new LiteRTLmNativeOptions());

// ...or let the translator own a private host (CLI, single-feature tools):
// using var translator = new LiteRTLmNativeTranslator(new LiteRTLmNativeOptions { ModelPath = @"C:\models\gemma-4-E2B-it.litertlm" });

var text = await translator.TranslateAsync(new TranslationRequest("Hello world.", "Russian"));

// Live transcripts: let the shared session handle sentence splitting, drafts and context.
await using var session = new LiveTranslationSession(_ => Task.FromResult<ITextTranslator>(translator))
{
    TargetLanguage = "Russian",
};
session.TranslationChanged += display => Console.WriteLine(display);
session.Feed("hello world this is a live");
```

CLI: `NemotronSpeech <model> --mic --translate ru --translate-backend native --litert-model-path <gemma.litertlm>`.

## Platform notes

- Windows x64: no VC++ Redistributable needed since LiteRtLmSharp 1.2.0 (static CRT).
- Linux x64: the runtime needs the system Vulkan loader (`libvulkan1`), even for the CPU
  backend; the Debian package declares the dependency.
- Android arm64: native package available; not validated in this repository.
- Only one engine may be alive per process (`GemmaEngineHost` is that owner). Changing the translation prompt or the
  target language only replaces the cheap translator; the engine stays loaded in the host.
