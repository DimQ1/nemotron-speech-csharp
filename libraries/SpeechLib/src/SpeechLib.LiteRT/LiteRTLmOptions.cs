namespace SpeechLib.LiteRT;

/// <summary>
/// Configuration for a LiteRT-LM translation server exposing an
/// OpenAI-compatible <c>/v1/chat/completions</c> endpoint (gemma-translator topology).
/// </summary>
public sealed class LiteRTLmOptions
{
    /// <summary>Base URL of the LiteRT-LM server, e.g. <c>http://localhost:9379</c>.</summary>
    public string BaseUrl { get; init; } = "http://localhost:9379";

    /// <summary>Path appended to <see cref="BaseUrl"/> for chat completions.</summary>
    public string Endpoint { get; init; } = "/v1/chat/completions";

    /// <summary>Model name accepted by the server.</summary>
    public string Model { get; init; } = "gemma-4-E2B-it";

    /// <summary>Sampling temperature; 0 selects greedy decoding.</summary>
    public float Temperature { get; init; } = 0f;

    /// <summary>Maximum tokens to generate for a translation.</summary>
    public int MaxTokens { get; init; } = 256;

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
