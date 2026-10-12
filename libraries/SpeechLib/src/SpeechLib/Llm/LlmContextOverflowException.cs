namespace SpeechLib.Llm;

/// <summary>
/// The prompt (history plus the new message) plus the answer does not fit the engine's context window.
/// The caller should send less: fewer retrieved fragments, an older history trimmed, a smaller unit.
/// </summary>
public sealed class LlmContextOverflowException(string message, int tokenCount, int maxTokens, Exception? inner = null)
    : InvalidOperationException(message, inner)
{
    /// <summary>Tokens the conversation held (or would hold) when the engine refused.</summary>
    public int TokenCount { get; } = tokenCount;

    /// <summary>The engine's context window.</summary>
    public int MaxTokens { get; } = maxTokens;
}
