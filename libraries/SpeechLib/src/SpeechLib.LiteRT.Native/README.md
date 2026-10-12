# SpeechLib.LiteRT.Native

In-process translation with a Gemma 4 model in `.litertlm` format, loaded through
LiteRT-LM via the [LiteRtLmSharp](https://github.com/DimQ1/LiteRtLmSharp) fork
(1.4.0, native LiteRT-LM v0.18.0 — the fork's build, which carries the engine-usage improvements this
repository uses; upstream's own 1.3.0 is a different build without them). No sidecar server: the model
runs inside the application process.

## Restoring the packages

The fork's 1.4.0 is not on nuget.org — upstream's 1.3.0 there has the same native version but not the
improvements — so its `.nupkg` files are fetched once from the fork's public `v1.4.0` release into a
local folder feed that the root
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

## How a sentence is translated

`LiteRTLmNativeTranslator` keeps one engine and opens a fresh conversation per request:

- system prompt from `SpeechLib.Translation.TranslationPrompt` (shared with the HTTP backend):
  translate a live speech transcript as-is, reply with the translation only;
- **an explicit `Greedy` sampler** (`LiteRTLmNativeOptions.Greedy`, default on). The CPU backend
  does not implement it: the first send of the first conversation fails with
  `UNIMPLEMENTED: Sampler type: 3` (reported as `LiteRtSamplerNotSupportedException` in
  LiteRtLmSharp 1.4.0), the translator retries once on a fresh conversation without an explicit
  sampler — the model file's own sampler (top-k 40, top-p 0.95, temperature 1.0) — and stops
  asking for Greedy for the life of the engine. Measured on native v0.18.0: it is a missing
  sampler, not memory pressure, and once a send has completed on the engine the runtime accepts
  and silently ignores later sampler parameters. `LiteRtSamplerParams.NearGreedy` (top-p 0.5,
  temperature 0.1) is the supported profile closest to greedy if Greedy is ever dropped — its
  translation quality and latency have not been measured yet;
- **no-repeat n-gram** of 8 tokens on the reply (`NoRepeatNgramSize`) stops decode loops;
- when the request carries the previous sentence and its translation
  (`TranslationRequest.PreviousSource/PreviousTranslation`, supplied by
  `LiveTranslationSession`), they are replayed as a prior user/model turn so pronouns,
  terminology and register stay consistent across sentences.

Sample timing on a 20-core laptop CPU, one 15-word English sentence into Russian:
about 1.2 s with greedy decoding, 2.5 s with the default sampler.

## Usage

```csharp
using SpeechLib.LiteRT.Native;
using SpeechLib.Translation;

using var translator = new LiteRTLmNativeTranslator(new LiteRTLmNativeOptions
{
    ModelPath = @"C:\models\gemma-4-E2B-it.litertlm",
    Backend = "cpu",            // or "gpu" (WebGPU delegate)
});

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
- Only one engine may be alive per process; the session replaces the engine under its
  decode gate when the backend or prompt changes.
