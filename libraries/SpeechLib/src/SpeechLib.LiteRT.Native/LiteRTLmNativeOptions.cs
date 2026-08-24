using LiteRtLmSharp;

namespace SpeechLib.LiteRT.Native;

/// <summary>
/// Configuration for the in-process LiteRT-LM translator: it loads a Gemma 4
/// model in <c>.litertlm</c> format directly (via LiteRtLmSharp, which pins the
/// LiteRT-LM C API to native v0.14.0), without an HTTP server.
/// </summary>
public sealed class LiteRTLmNativeOptions
{
    /// <summary>Path to the <c>.litertlm</c> model file.</summary>
    public required string ModelPath { get; init; }

    /// <summary>Compute backend: <c>"cpu"</c> (default) or <c>"gpu"</c>.</summary>
    public string Backend { get; init; } = "cpu";

    /// <summary>
    /// Number of CPU threads. 0 (default) lets the LiteRT runtime pick its own
    /// default (all available cores).
    /// </summary>
    public int NumThreads { get; init; }

    /// <summary>Maximum tokens to generate for a translation.</summary>
    public int MaxTokens { get; init; } = 256;

    /// <summary>
    /// Model context window (max tokens). The translations are short ASR
    /// sentences (a few hundred chars at most), so a large window is mostly
    /// padding that only costs memory and prefill time on CPU. Kept modest at
    /// 2048 so the constant per-call overhead stays low.
    /// </summary>
    public int MaxContextTokens { get; init; } = 2048;

    /// <summary>
    /// Model weight-cache location passed to the engine. This controls the
    /// XNNPack weight cache (persisted weights so a repeated model load is
    /// faster), not the per-call prompt prefix. <see cref="LiteRtCache.InMemory"/>
    /// is not enabled in this LiteRT build and logs "in-memory cache is not
    /// enabled" errors, so keep the default, or point <see cref="LiteRtCache.Directory"/>
    /// at a writable folder if you want a disk-backed weight cache.
    /// </summary>
    public LiteRtCache Cache { get; init; } = LiteRtCache.Default;

    /// <summary>
    /// Native log verbosity. Defaults to <c>Warning</c> so model-load progress is
    /// not drowned out; set to <c>Silent</c> to suppress everything.
    /// </summary>
    public LiteRTLmLogLevel LogLevel { get; init; } = LiteRTLmLogLevel.Warning;

    /// <summary>
    /// Optional extra system-prompt text appended to the built-in translation
    /// instruction (terminology, style, casing, etc.). Empty by default.
    /// </summary>
    public string AdditionalSystemPrompt { get; init; } = "";

    /// <summary>
    /// Builds the system prompt. A plain instruction to translate the source text
    /// into the target language, preserving meaning/tone/formatting and replying
    /// with only the translation. <c>AdditionalSystemPrompt</c> is appended so
    /// users can add their own rules.
    /// </summary>
    public string BuildSystemPrompt(string targetLang, string? sourceLang)
    {
        var source = string.IsNullOrWhiteSpace(sourceLang)
            ? "the source language"
            : sourceLang;

        var prompt =
            "You are a professional translation engine. " +
            $"Translate the user's message from {source} into {targetLang}. " +
            "Preserve meaning, tone, and formatting. " +
            "Reply with only the translation, with no preamble, no explanation, and no JSON.";

        return string.IsNullOrWhiteSpace(AdditionalSystemPrompt)
            ? prompt
            : prompt + "\n\nAdditional instructions:\n" + AdditionalSystemPrompt;
    }
}

/// <summary>Log severity exposed on the managed side.</summary>
public enum LiteRTLmLogLevel
{
    Verbose = 0,
    Debug = 1,
    Info = 2,
    Warning = 3,
    Error = 4,
    Fatal = 5,
    Silent = 1000,
}
