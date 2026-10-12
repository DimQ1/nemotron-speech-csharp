namespace SpeechLib.Llm;

/// <summary>
/// The view of the application's one LLM host that components receive: forwards everything to the real host but
/// ignores <see cref="DisposeAsync"/>. A component (translator, chat, job, test double wiring) that disposes "its"
/// host therefore cannot shut the engine down for everybody else; only the owner that created the real host
/// (the DI container at app shutdown) disposes it.
/// </summary>
public sealed class SharedLlmHost(ILlmHost inner) : ILlmHost
{
    private readonly ILlmHost _inner = inner ?? throw new ArgumentNullException(nameof(inner));

    /// <summary>Number of <see cref="DisposeAsync"/> calls that were ignored (diagnostics).</summary>
    public int IgnoredDisposeCount => Volatile.Read(ref _ignoredDisposeCount);

    /// <summary>Raised with the call stack when a component tries to dispose the shared host.</summary>
    public Action<string>? OnIgnoredDispose { get; init; }

    private int _ignoredDisposeCount;

    public bool IsModelInstalled => _inner.IsModelInstalled;
    public int ContextTokens => _inner.ContextTokens;
    public LlmHostStatus Status => _inner.Status;

    public event Action<LlmHostStatus>? StatusChanged
    {
        add => _inner.StatusChanged += value;
        remove => _inner.StatusChanged -= value;
    }

    public IAsyncEnumerable<string> GenerateStreamAsync(LlmRequest request, CancellationToken cancellationToken = default) =>
        _inner.GenerateStreamAsync(request, cancellationToken);

    public Task<string> GenerateAsync(LlmRequest request, CancellationToken cancellationToken = default) =>
        _inner.GenerateAsync(request, cancellationToken);

    public int CountTokens(string text) => _inner.CountTokens(text);

    public void ReleaseConversation(string conversationKey) => _inner.ReleaseConversation(conversationKey);

    public Task PreloadAsync(CancellationToken cancellationToken = default, LlmPriority priority = LlmPriority.Background) =>
        _inner.PreloadAsync(cancellationToken, priority);

    /// <summary>Ignored: the shared host lives until its owner disposes the real instance.</summary>
    public ValueTask DisposeAsync()
    {
        Interlocked.Increment(ref _ignoredDisposeCount);
        try { OnIgnoredDispose?.Invoke("Dispose of the shared LLM host ignored. Caller:" + Environment.NewLine + Environment.StackTrace); }
        catch
        {
            // Diagnostics only.
        }

        return ValueTask.CompletedTask;
    }
}
