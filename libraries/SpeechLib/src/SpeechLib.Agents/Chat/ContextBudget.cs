namespace SpeechLib.Agents;

/// <summary>
/// How the engine window is shared between the parts of one turn:
/// system (instructions + source list) + context (excerpts) + history window + question + answer.
/// The numbers are caps, not reservations: every part is measured with the real token counter and
/// <see cref="TotalFor"/> is what <see cref="LlmChatClient"/> checks again before sending.
/// </summary>
/// <param name="WindowTokens">The engine's context window (prompt + answer).</param>
/// <param name="AnswerTokens">Reserved for the answer (<c>MaxOutputTokens</c>).</param>
/// <param name="HistoryTokens">Cap for earlier turns kept in the prompt.</param>
/// <param name="MaxContextTokens">Cap for retrieved transcript text.</param>
/// <param name="OverheadTokens">Chat-template tokens and counting error.</param>
public sealed record ContextBudget(int WindowTokens, int AnswerTokens, int HistoryTokens, int MaxContextTokens, int OverheadTokens)
{
    /// <summary>The defaults for a window: 4096 → answer 600, history 800, context ≤ 1900.</summary>
    public static ContextBudget ForWindow(int windowTokens)
    {
        var window = Math.Max(1024, windowTokens);
        if (window <= 4096)
        {
            // 4096: 600 + 600 + 1900 + system ~350 + question ~60 + overhead 48 = 3558, ~540 spare for miscounting.
            var scale = window / 4096.0;
            return new ContextBudget(window, (int)(600 * scale), (int)(600 * scale), (int)(1900 * scale), 48);
        }
        return new ContextBudget(window, 800, 1600, Math.Min(4500, window / 2 - 200), 64);
    }

    /// <summary>Tokens left for transcript excerpts given the measured system message and question.</summary>
    public int ContextTokensFor(int systemTokens, int questionTokens)
    {
        var free = WindowTokens - AnswerTokens - HistoryTokens - systemTokens - questionTokens - OverheadTokens;
        return Math.Clamp(free, 0, MaxContextTokens);
    }

    /// <summary>Prompt tokens plus the answer reservation; must be ≤ <see cref="WindowTokens"/>.</summary>
    public int TotalFor(int promptTokens) => promptTokens + AnswerTokens + OverheadTokens;
}
