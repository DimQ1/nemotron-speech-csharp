# SpeechLib.LiteRT.Native

In-process translation with a Gemma 4 model in `.litertlm` format, loaded through
LiteRT-LM via the [LiteRtLmSharp](https://github.com/OrihuelaConde/LiteRtLmSharp)
binding (1.2.0, native LiteRT-LM v0.16.0). No sidecar server: the model runs inside
the application process.

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
- **greedy decoding** (`LiteRTLmNativeOptions.Greedy`, default on): the model file's own
  sampler (top-k 40, top-p 0.95, temperature 1.0) made every draft pass differ and cost
  about twice the decode time;
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
