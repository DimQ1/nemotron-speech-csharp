#if DEBUG
using System.Runtime.CompilerServices;
using SpeechLib.Llm;

namespace VoiceType.Uno.Services.Jobs;

/// <summary>
/// DEBUG-only scripted <see cref="ILlmHost"/> (VOICETYPE_FAKE_LLM=1) for checking the translate / title / summary
/// screens without loading the 2.4 GB model: every request takes <c>VOICETYPE_FAKE_LLM_DELAY_MS</c> (default 700 ms)
/// and answers with a marked copy of the user text. Never registered over a real host.
/// </summary>
internal sealed class DebugFakeLlmHost : ILlmHost
{
    private readonly int _delayMs = int.TryParse(Environment.GetEnvironmentVariable("VOICETYPE_FAKE_LLM_DELAY_MS"), out var d) ? d : 700;

    public bool IsModelInstalled => true;
    public int ContextTokens => 4096;
    public LlmHostStatus Status { get; } = new(LlmHostState.Idle, null, 0);
    public event Action<LlmHostStatus>? StatusChanged
    {
        add { }
        remove { }
    }

    public async Task<string> GenerateAsync(LlmRequest request, CancellationToken cancellationToken = default)
    {
        await Task.Delay(_delayMs, cancellationToken).ConfigureAwait(false);
        var system = request.Messages[0].Text;
        var user = request.Messages[^1].Text;
        if (system.Contains("translation engine", StringComparison.Ordinal))
        {
            var target = system.Contains("into Russian", StringComparison.Ordinal) ? "RU" : system.Contains("into German", StringComparison.Ordinal) ? "DE" : "XX";
            return $"[{target}] {user}";
        }
        if (system.Contains("titles for voice recordings", StringComparison.Ordinal))
            return "Gemma title: " + string.Join(' ', user.Replace("Transcript:", "", StringComparison.Ordinal).Split(' ', StringSplitOptions.RemoveEmptyEntries).Take(4));
        return "Summary of this part [00:00].";
    }

    public async IAsyncEnumerable<string> GenerateStreamAsync(
        LlmRequest request, [EnumeratorCancellation] CancellationToken cancellationToken = default)
    {
        yield return await GenerateAsync(request, cancellationToken).ConfigureAwait(false);
    }

    public int CountTokens(string text) => (text.Length + 3) / 4;

    public void ReleaseConversation(string conversationKey)
    {
    }

    public ValueTask DisposeAsync() => ValueTask.CompletedTask;
}
#endif
