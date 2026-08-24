# SpeechLib.LiteRT.Native

In-process translation for the NemotronSpeech pipeline: loads a Gemma 4 model in
`.litertlm` format directly through the LiteRT-LM C API — **no HTTP server** is
required.

## Engine

The wrapper is built on [LiteRtLmSharp](https://github.com/OrihuelaConde/LiteRtLmSharp),
which pins the LiteRT-LM C API to **native v0.14.0**.

> **Why not the upstream prebuilt?** The official LiteRT-LM v0.16.0 C API
> prebuilt heap-corrupts (`0xC0000374`) on plain-chat CPU decode with Gemma 4
> (upstream issue **#2149**). LiteRtLmSharp's self-built v0.14.0 natives are
> validated on Windows x64 CPU/GPU and ship via NuGet
> (`LiteRtLmSharp.runtime.win-x64`), so no DLLs are vendored in this repo.

## Model

Use the LiteRT-LM CPU variant of Gemma 4 E2B:

- Hugging Face: [`litert-community/gemma-4-E2B-it-litert-lm`](https://huggingface.co/litert-community/gemma-4-E2B-it-litert-lm)
- Local file (this repo convention): `models/gemma-4-E2B-it.litertlm` (~2.6 GB)

The C API has no NVIDIA CUDA support; the `gpu` backend refers to the WebGPU
delegate, which is not available on most desktops. **Use `cpu`.**

## Usage

```csharp
using SpeechLib;
using SpeechLib.LiteRT.Native;

using ITextTranslator translator = new LiteRTLmNativeTranslator(new LiteRTLmNativeOptions
{
    ModelPath = @"models\gemma-4-E2B-it.litertlm",
    Backend = "cpu",        // "cpu" | "gpu"
    NumThreads = 4,         // 0 = library default
    MaxTokens = 256,
    LogLevel = LiteRTLmLogLevel.Warning,
});

// Blocking
string? ru = await translator.TranslateAsync("Hello world", "ru");

// Streaming (yields token deltas as they are decoded)
await foreach (var token in translator.TranslateStreamAsync("Hello world", "ru"))
    Console.Write(token);
```

## CLI

From the repo root:

```powershell
dotnet build NemotronSpeech.slnx -c Release -p:GpuArch=CPU
dotnet apps\NemotronSpeech\src\NemotronSpeech\bin\Release\net10.0\NemotronSpeech.dll `
  <asr-model> <audio.wav> cpu `
  --translate ru --translate-backend native `
  --litert-model-path models\gemma-4-E2B-it.litertlm
```
