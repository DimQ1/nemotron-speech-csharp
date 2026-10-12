using System.Runtime.CompilerServices;
using SpeechLib.Llm;

namespace VoiceType.Uno.Tests.Fakes;

/// <summary>
/// Scripted <see cref="ILlmHost"/>: <see cref="Responder"/> maps (request, call number) to the answer, and
/// <see cref="Before"/> runs first (throw to simulate a failure, cancel a token to simulate a pause, delay…).
/// Tokens are counted as one per four characters.
/// </summary>
internal sealed class FakeLlmHost : ILlmHost
{
    private LlmHostStatus _status = new(LlmHostState.Idle, null, 0);
    private int _calls;

    public List<LlmRequest> Requests { get; } = [];

    /// <summary>Answer for a request; call numbers start at 1. Default: "T(" + last user message + ")".</summary>
    public Func<LlmRequest, int, string> Responder { get; set; } = (request, _) => "T(" + request.Messages[^1].Text + ")";

    public Func<LlmRequest, int, CancellationToken, Task>? Before { get; set; }

    public bool IsModelInstalled { get; set; } = true;
    public int ContextTokens => 4096;
    public LlmHostStatus Status => _status;
    public event Action<LlmHostStatus>? StatusChanged;

    public int Calls => _calls;

    public void SetStatus(LlmHostStatus status)
    {
        _status = status;
        StatusChanged?.Invoke(status);
    }

    public async Task<string> GenerateAsync(LlmRequest request, CancellationToken cancellationToken = default)
    {
        if (!IsModelInstalled)
            throw new LlmUnavailableException("not installed");
        int call;
        lock (Requests)
        {
            Requests.Add(request);
            call = ++_calls;
        }
        cancellationToken.ThrowIfCancellationRequested();
        if (Before is not null)
            await Before(request, call, cancellationToken);
        cancellationToken.ThrowIfCancellationRequested();
        return Responder(request, call);
    }

    public async IAsyncEnumerable<string> GenerateStreamAsync(
        LlmRequest request, [EnumeratorCancellation] CancellationToken cancellationToken = default)
    {
        yield return await GenerateAsync(request, cancellationToken);
    }

    public int CountTokens(string text) => (text.Length + 3) / 4;

    public void ReleaseConversation(string conversationKey)
    {
    }

    public ValueTask DisposeAsync() => ValueTask.CompletedTask;
}
