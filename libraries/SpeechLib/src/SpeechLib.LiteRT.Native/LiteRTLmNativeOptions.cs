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
    /// Native log verbosity. Defaults to <c>Warning</c> so model-load progress is
    /// not drowned out; set to <c>Silent</c> to suppress everything.
    /// </summary>
    public LiteRTLmLogLevel LogLevel { get; init; } = LiteRTLmLogLevel.Warning;

    /// <summary>
    /// Builds the system prompt that instructs the model to translate and to
    /// reply with only the translated text (no JSON envelope, no preamble), so
    /// that streamed deltas can be shown to the user as they arrive.
    /// </summary>
    public string BuildSystemPrompt(string targetLang, string? sourceLang)
    {
        var source = string.IsNullOrWhiteSpace(sourceLang)
            ? "the source language"
            : sourceLang;

        return
            "You are a professional translation engine. " +
            $"Translate the user's message from {source} into {targetLang}. " +
            "Preserve meaning, tone, and formatting. " +
            "Reply with only the translation, with no preamble, no explanation, and no JSON.";
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
