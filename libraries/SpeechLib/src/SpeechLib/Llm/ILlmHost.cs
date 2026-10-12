namespace SpeechLib.Llm;

/// <summary>Who is asking. One engine serves everyone, so the host schedules by this class.</summary>
public enum LlmPriority
{
    /// <summary>Live sentence-by-sentence translation while recording. Preempts everything else.</summary>
    Live = 0,

    /// <summary>A person is waiting for the answer (chat). Preempts <see cref="Background"/>.</summary>
    Interactive = 1,

    /// <summary>Batch work: saved-transcript translation, titles, summaries. Split into small retryable units.</summary>
    Background = 2,
}

public enum LlmRole
{
    System,
    User,
    Assistant,
}

public sealed record LlmMessage(LlmRole Role, string Text);

/// <summary>Optional sampling hints. Null members keep the model's own sampler.</summary>
public sealed record LlmSampling(double? Temperature = null, double? TopP = null, int? MaxOutputTokens = null);

/// <summary>
/// One generation turn.
/// </summary>
/// <param name="Priority">Scheduling class.</param>
/// <param name="Messages">The full prompt: optional system message, prior turns, and a final user message.</param>
/// <param name="Sampling">Optional sampling hints.</param>
/// <param name="ConversationKey">
/// Non-null for a chat: calls with the same key may continue a live stateful conversation on the engine
/// (follow-up in 0.2–0.6 s instead of re-prefilling the whole history). The caller still passes the whole
/// history in <paramref name="Messages"/>; the host decides whether the live conversation matches and can be
/// continued, or has to be rebuilt by replay. Null = stateless, one-shot.
/// </param>
public sealed record LlmRequest(
    LlmPriority Priority,
    IReadOnlyList<LlmMessage> Messages,
    LlmSampling? Sampling = null,
    string? ConversationKey = null);

public enum LlmHostState
{
    /// <summary>No model installed or the engine failed to load.</summary>
    Unavailable,
    Idle,
    Running,
}

/// <param name="State">Engine state.</param>
/// <param name="RunningPriority">Priority of the request being generated, if any.</param>
/// <param name="QueuedCount">Requests waiting behind it.</param>
/// <param name="Detail">Short human text for a status line (e.g. "Waiting for live translation").</param>
public sealed record LlmHostStatus(LlmHostState State, LlmPriority? RunningPriority, int QueuedCount, string? Detail = null);

/// <summary>The model is not installed, failed to load, or the host was disposed.</summary>
public sealed class LlmUnavailableException(string message, Exception? inner = null) : InvalidOperationException(message, inner);

/// <summary>
/// Single access point to the on-device LLM (Gemma via LiteRT-LM). Owns the one engine of the process and
/// serializes every native call: LiteRT-LM runs sends strictly one after another with no priorities, blocks
/// conversation creation and token counting while something generates, and cannot cancel a prefill.
/// </summary>
/// <remarks>
/// Contract the implementation provides:
/// <list type="bullet">
/// <item>Higher <see cref="LlmPriority"/> preempts a running lower one by cancelling it (about 40 ms during decode).</item>
/// <item>A preempted <see cref="LlmPriority.Background"/> request is transparently re-run from the start
/// once the engine is free (callers send small idempotent units).</item>
/// <item>A preempted <see cref="LlmPriority.Interactive"/> streaming request keeps its enumerator open, yields
/// nothing while preempted, then continues from the text already yielded (no duplicates, no gaps) — or fails
/// with a clear exception if continuing is not possible.</item>
/// <item>Cancelling the caller's token stops generation promptly; the host recovers the engine state.</item>
/// <item>Prefill cannot be interrupted: a <see cref="LlmPriority.Live"/> request that arrives during a prefill
/// waits for it, which <see cref="Status"/> reports.</item>
/// <item>Members are thread-safe.</item>
/// </list>
/// </remarks>
public interface ILlmHost : IAsyncDisposable
{
    /// <summary>The Gemma model file is on disk.</summary>
    bool IsModelInstalled { get; }

    /// <summary>The context window of the engine in tokens (prompt + answer).</summary>
    int ContextTokens { get; }

    LlmHostStatus Status { get; }

    /// <summary>Raised on any thread when <see cref="Status"/> changes.</summary>
    event Action<LlmHostStatus>? StatusChanged;

    /// <summary>Streams the answer. Throws <see cref="LlmUnavailableException"/> when the model is missing.</summary>
    IAsyncEnumerable<string> GenerateStreamAsync(LlmRequest request, CancellationToken cancellationToken = default);

    /// <summary>The whole answer at once.</summary>
    Task<string> GenerateAsync(LlmRequest request, CancellationToken cancellationToken = default);

    /// <summary>Token count of <paramref name="text"/>; exact when the engine is loaded, a conservative estimate otherwise. Never blocks on generation.</summary>
    int CountTokens(string text);

    /// <summary>Drops the live conversation kept for <paramref name="conversationKey"/> (chat closed or evicted).</summary>
    void ReleaseConversation(string conversationKey);
}
