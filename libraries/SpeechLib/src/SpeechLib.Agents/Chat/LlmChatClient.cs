using System.Runtime.CompilerServices;
using Microsoft.Extensions.AI;
using SpeechLib.Llm;

namespace SpeechLib.Agents;

/// <summary>
/// <see cref="IChatClient"/> over <see cref="ILlmHost"/>: the Agent Framework (or any other caller) talks to
/// the single on-device engine through the host's queue. One instance per chat: it carries that chat's
/// conversation key. Maps system/user/assistant roles, <see cref="ChatOptions.Instructions"/> (as the system
/// message), temperature / top-p / max output tokens, and cancellation. The LiteRtLmSharp Extensions.AI
/// client is not used (not in the feed, and it would bypass the host's priorities).
/// </summary>
/// <remarks>
/// Before sending, the prompt is checked against the window: while prompt + answer reservation would not fit,
/// the oldest earlier turns are dropped, then the long message is cut. So a turn never overruns the window,
/// whatever the history or context providers produced.
/// </remarks>
public sealed class LlmChatClient : IChatClient
{
    public const string ModelId = "gemma-4-E2B-it";

    private readonly ILlmHost _host;
    private readonly ContextBudget? _budget;

    /// <param name="host">The engine host.</param>
    /// <param name="conversationKey">Passed to <see cref="LlmRequest.ConversationKey"/>; null = stateless calls.</param>
    /// <param name="priority">Scheduling class; a person is waiting, so <see cref="LlmPriority.Interactive"/>.</param>
    /// <param name="budget">When set, the window guard uses it; otherwise one derived from <see cref="ILlmHost.ContextTokens"/>.</param>
    public LlmChatClient(ILlmHost host, string? conversationKey = null, LlmPriority priority = LlmPriority.Interactive, ContextBudget? budget = null)
    {
        _host = host ?? throw new ArgumentNullException(nameof(host));
        ConversationKey = conversationKey;
        Priority = priority;
        _budget = budget;
    }

    public string? ConversationKey { get; }

    public LlmPriority Priority { get; }

    public ChatClientMetadata Metadata { get; } = new("SpeechLib.Llm", null, ModelId);

    public async Task<ChatResponse> GetResponseAsync(IEnumerable<ChatMessage> messages, ChatOptions? options = null, CancellationToken cancellationToken = default)
    {
        var request = BuildRequest(messages, options);
        var text = await _host.GenerateAsync(request, cancellationToken).ConfigureAwait(false);
        return new ChatResponse(new ChatMessage(ChatRole.Assistant, text)) { ModelId = ModelId, FinishReason = ChatFinishReason.Stop };
    }

    public async IAsyncEnumerable<ChatResponseUpdate> GetStreamingResponseAsync(
        IEnumerable<ChatMessage> messages,
        ChatOptions? options = null,
        [EnumeratorCancellation] CancellationToken cancellationToken = default)
    {
        var request = BuildRequest(messages, options);
        await foreach (var piece in _host.GenerateStreamAsync(request, cancellationToken).ConfigureAwait(false))
        {
            if (piece.Length == 0)
                continue;
            yield return new ChatResponseUpdate(ChatRole.Assistant, piece) { ModelId = ModelId };
        }
        yield return new ChatResponseUpdate { Role = ChatRole.Assistant, FinishReason = ChatFinishReason.Stop, ModelId = ModelId };
    }

    public object? GetService(Type serviceType, object? serviceKey = null)
    {
        ArgumentNullException.ThrowIfNull(serviceType);
        if (serviceKey is null && serviceType.IsInstanceOfType(this))
            return this;
        if (serviceKey is null && serviceType == typeof(ChatClientMetadata))
            return Metadata;
        return null;
    }

    public void Dispose()
    {
    }

    /// <summary>The request the host will get for these messages (public for diagnostics and tests).</summary>
    public LlmRequest BuildRequest(IEnumerable<ChatMessage> messages, ChatOptions? options)
    {
        ArgumentNullException.ThrowIfNull(messages);

        var list = new List<LlmMessage>();
        if (!string.IsNullOrWhiteSpace(options?.Instructions))
            list.Add(new LlmMessage(LlmRole.System, options!.Instructions!));

        foreach (var message in messages)
        {
            var text = message.Text;
            if (string.IsNullOrWhiteSpace(text))
                continue;
            var role = message.Role == ChatRole.System ? LlmRole.System
                : message.Role == ChatRole.Assistant ? LlmRole.Assistant
                : LlmRole.User;
            list.Add(new LlmMessage(role, text));
        }

        list = Normalize(list);
        var sampling = BuildSampling(options);
        var window = _budget ?? ContextBudget.ForWindow(_host.ContextTokens > 0 ? _host.ContextTokens : 4096);
        list = FitToWindow(list, window, sampling?.MaxOutputTokens ?? window.AnswerTokens);
        return new LlmRequest(Priority, list, sampling, ConversationKey);
    }

    private static LlmSampling? BuildSampling(ChatOptions? options)
    {
        if (options is null)
            return null;
        if (options.Temperature is null && options.TopP is null && options.MaxOutputTokens is null)
            return null;
        return new LlmSampling(options.Temperature, options.TopP, options.MaxOutputTokens);
    }

    /// <summary>One system message at the front, consecutive same-role messages merged (alternating turns).</summary>
    private static List<LlmMessage> Normalize(List<LlmMessage> input)
    {
        var system = input.Where(m => m.Role == LlmRole.System).Select(m => m.Text).ToList();
        var result = new List<LlmMessage>();
        if (system.Count > 0)
            result.Add(new LlmMessage(LlmRole.System, string.Join("\n\n", system)));

        foreach (var m in input.Where(m => m.Role != LlmRole.System))
        {
            if (result.Count > 0 && result[^1].Role == m.Role && m.Role != LlmRole.System)
                result[^1] = new LlmMessage(m.Role, result[^1].Text + "\n\n" + m.Text);
            else
                result.Add(m);
        }

        // The conversation must start with a user turn (after the system message).
        var firstTurn = result.FindIndex(m => m.Role != LlmRole.System);
        while (firstTurn >= 0 && result[firstTurn].Role == LlmRole.Assistant)
        {
            result.RemoveAt(firstTurn);
            firstTurn = result.FindIndex(m => m.Role != LlmRole.System);
        }
        return result;
    }

    private List<LlmMessage> FitToWindow(List<LlmMessage> messages, ContextBudget budget, int answerTokens)
    {
        int Total(List<LlmMessage> l) => l.Sum(m => _host.CountTokens(m.Text) + 6) + answerTokens + budget.OverheadTokens;

        var limit = budget.WindowTokens;
        if (Total(messages) <= limit)
            return messages;

        // 1. Drop the oldest earlier user+assistant pairs (everything before the final user message).
        var result = new List<LlmMessage>(messages);
        while (Total(result) > limit)
        {
            var firstTurn = result.FindIndex(m => m.Role != LlmRole.System);
            var last = result.Count - 1;
            if (firstTurn < 0 || firstTurn >= last)
                break;
            result.RemoveAt(firstTurn);
            while (firstTurn < result.Count - 1 && result[firstTurn].Role == LlmRole.Assistant)
                result.RemoveAt(firstTurn);
        }

        // 2. Still too long: cut the biggest message from its middle-to-end proportionally.
        var guard = 0;
        while (Total(result) > limit && guard++ < 8)
        {
            var biggest = 0;
            var biggestTokens = -1;
            for (var i = 0; i < result.Count; i++)
            {
                var t = _host.CountTokens(result[i].Text);
                if (t > biggestTokens)
                {
                    biggest = i;
                    biggestTokens = t;
                }
            }
            var excess = Total(result) - limit;
            var keepRatio = Math.Clamp(1.0 - (double)(excess + 16) / Math.Max(1, biggestTokens), 0.2, 0.95);
            var text = result[biggest].Text;
            var keepChars = (int)(text.Length * keepRatio);
            var cut = result[biggest].Role == LlmRole.User
                ? "…\n" + text[^keepChars..]   // keep the end: the question is there
                : text[..keepChars] + "…";
            result[biggest] = new LlmMessage(result[biggest].Role, cut);
        }
        return result;
    }
}
