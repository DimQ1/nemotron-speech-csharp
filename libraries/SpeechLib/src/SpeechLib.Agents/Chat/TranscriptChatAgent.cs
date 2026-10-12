using System.Runtime.CompilerServices;
using System.Text.Json;
using Microsoft.Agents.AI;
using Microsoft.Extensions.AI;
using SpeechLib.Llm;

namespace SpeechLib.Agents;

/// <summary>A turn of the display log, used to rebuild a session when the stored one cannot be read.</summary>
public sealed record ChatTurn(bool IsUser, string Text);

/// <summary>
/// One chat's agent: a Microsoft Agent Framework <see cref="ChatClientAgent"/> over <see cref="LlmChatClient"/>
/// with <see cref="TranscriptContextProvider"/> for the transcripts and a token-window history reducer.
/// Hides every Agent Framework type from callers: sessions are opaque <see cref="TranscriptChatSession"/>
/// objects serialized to <see cref="JsonElement"/>.
/// </summary>
public sealed class TranscriptChatAgent : IDisposable
{
    private readonly AIAgent _agent;
    private readonly InMemoryChatHistoryProvider _history;
    private readonly LlmChatClient _client;
    private readonly ILlmHost _host;

    public TranscriptChatAgent(
        ILlmHost host,
        IChatSourceCatalog catalog,
        string conversationKey,
        ISummaryStore? summaryStore = null,
        ContextBudget? budget = null)
    {
        _host = host ?? throw new ArgumentNullException(nameof(host));
        ConversationKey = conversationKey;
        var effective = () => budget ?? ContextBudget.ForWindow(host.ContextTokens > 0 ? host.ContextTokens : 4096);

        _client = new LlmChatClient(host, conversationKey, LlmPriority.Interactive, budget);
        var summarizer = new MapReduceSummarizer(host, summaryStore ?? new InMemorySummaryStore(), LlmPriority.Interactive);
        var builder = new TranscriptContextBuilder(host.CountTokens, summarizer);

        _history = new InMemoryChatHistoryProvider(new InMemoryChatHistoryProviderOptions
        {
            ChatReducer = new TokenWindowChatReducer(host.CountTokens, () => effective().HistoryTokens),
            // Reduce only what is sent to the model; the stored session keeps the whole conversation.
            ReducerTriggerEvent = InMemoryChatHistoryProviderOptions.ChatReducerTriggerEvent.BeforeMessagesRetrieval,
            // Store only what the person typed: the provider's transcript messages must not enter the session.
            StorageInputRequestMessageFilter = messages =>
                messages.Where(m => m.GetAgentRequestMessageSourceType() == AgentRequestMessageSourceType.External),
        });

        Context = new TranscriptContextProvider(catalog, builder, host.CountTokens, effective, PreviousQuestion);

        _agent = new ChatClientAgent(_client, new ChatClientAgentOptions
        {
            Name = "TranscriptAnalyst",
            Description = "Answers questions about recordings from their transcripts.",
            ChatOptions = new ChatOptions { MaxOutputTokens = effective().AnswerTokens, Temperature = 0.2f, TopP = 0.5f },
            ChatHistoryProvider = _history,
            AIContextProviders = [Context],
            UseProvidedChatClientAsIs = true,
        });
    }

    public string ConversationKey { get; }

    public TranscriptContextProvider Context { get; }

    public async Task<TranscriptChatSession> CreateSessionAsync(CancellationToken cancellationToken = default) =>
        new(await _agent.CreateSessionAsync(cancellationToken).ConfigureAwait(false));

    public async Task<JsonElement> SerializeAsync(TranscriptChatSession session, CancellationToken cancellationToken = default) =>
        await _agent.SerializeSessionAsync(session.Inner, null, cancellationToken).ConfigureAwait(false);

    /// <summary>Restores a session; null when the stored blob is not readable by this library version.</summary>
    public async Task<TranscriptChatSession?> TryDeserializeAsync(JsonElement stored, CancellationToken cancellationToken = default)
    {
        try
        {
            return new TranscriptChatSession(await _agent.DeserializeSessionAsync(stored, null, cancellationToken).ConfigureAwait(false));
        }
        catch (Exception ex) when (ex is JsonException or InvalidOperationException or NotSupportedException or ArgumentException or KeyNotFoundException)
        {
            return null;
        }
    }

    /// <summary>A fresh session whose history is the given earlier turns (the display log is the source of truth).</summary>
    public async Task<TranscriptChatSession> RebuildSessionAsync(IEnumerable<ChatTurn> turns, CancellationToken cancellationToken = default)
    {
        var session = await CreateSessionAsync(cancellationToken).ConfigureAwait(false);
        var messages = turns
            .Where(t => !string.IsNullOrWhiteSpace(t.Text))
            .Select(t => new ChatMessage(t.IsUser ? ChatRole.User : ChatRole.Assistant, t.Text))
            .ToList();
        _history.SetMessages(session.Inner, messages);
        return session;
    }

    /// <summary>Streams the answer text. The user text and the answer are added to the session history on completion.</summary>
    public async IAsyncEnumerable<string> RunStreamingAsync(
        TranscriptChatSession session,
        string userText,
        IProgress<string>? status = null,
        [EnumeratorCancellation] CancellationToken cancellationToken = default)
    {
        Context.Status = status;
        try
        {
            await foreach (var update in _agent.RunStreamingAsync(userText, session.Inner, cancellationToken: cancellationToken).ConfigureAwait(false))
            {
                var text = update.Text;
                if (!string.IsNullOrEmpty(text))
                    yield return text;
            }
        }
        finally
        {
            Context.Status = null;
        }
    }

    /// <summary>Replaces the text of the newest assistant message of the session (the answer after citation clean-up).</summary>
    public void ReplaceLastAnswer(TranscriptChatSession session, string text)
    {
        var messages = _history.GetMessages(session.Inner);
        var index = messages.FindLastIndex(m => m.Role == ChatRole.Assistant);
        if (index < 0)
            return;
        messages[index] = new ChatMessage(ChatRole.Assistant, text);
        _history.SetMessages(session.Inner, messages);
    }

    /// <summary>The persisted conversation as the library holds it (user and assistant text only).</summary>
    public IReadOnlyList<ChatTurn> GetHistory(TranscriptChatSession session) =>
        _history.GetMessages(session.Inner).Select(m => new ChatTurn(m.Role == ChatRole.User, m.Text)).ToList();

    public void Dispose()
    {
        _client.Dispose();
        _host.ReleaseConversation(ConversationKey);
    }

    private string? PreviousQuestion(AgentSession? session)
    {
        if (session is null)
            return null;
        return _history.GetMessages(session).LastOrDefault(m => m.Role == ChatRole.User)?.Text;
    }
}

/// <summary>Opaque handle for a conversation's Agent Framework session.</summary>
public sealed class TranscriptChatSession
{
    internal TranscriptChatSession(AgentSession inner) => Inner = inner;

    internal AgentSession Inner { get; }
}

/// <summary>Keeps the newest turns that fit <c>maxTokens</c>; the window always starts with a user message.</summary>
public sealed class TokenWindowChatReducer(Func<string, int> countTokens, Func<int> maxTokens) : IChatReducer
{
    public Task<IEnumerable<ChatMessage>> ReduceAsync(IEnumerable<ChatMessage> messages, CancellationToken cancellationToken)
    {
        var list = messages.ToList();
        var limit = maxTokens();
        var kept = new List<ChatMessage>();
        var total = 0;
        for (var i = list.Count - 1; i >= 0; i--)
        {
            var t = countTokens(list[i].Text) + 6;
            if (total + t > limit)
                break;
            total += t;
            kept.Add(list[i]);
        }
        kept.Reverse();
        while (kept.Count > 0 && kept[0].Role != ChatRole.User)
            kept.RemoveAt(0);
        return Task.FromResult<IEnumerable<ChatMessage>>(kept);
    }
}
