using SpeechLib.Translation;

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

    /// <summary>Builds the system prompt (see <see cref="TranslationPrompt"/>).</summary>
    public string BuildSystemPrompt(string targetLang, string? sourceLang) =>
        TranslationPrompt.BuildSystemPrompt(targetLang, sourceLang, AdditionalSystemPrompt);
}
