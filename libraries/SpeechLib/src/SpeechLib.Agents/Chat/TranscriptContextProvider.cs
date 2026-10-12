using Microsoft.Agents.AI;
using Microsoft.Extensions.AI;

namespace SpeechLib.Agents;

/// <summary>
/// Injects the transcript context for every run: the system message (rules + the chat's current source list)
/// and a user-role message with the retrieved excerpts that sits in front of the question. The messages come
/// from the provider, not from the caller, so the Agent Framework does not store them in the session history:
/// transcripts never enter the persisted conversation and the serialized session stays small. The sources are
/// read from <see cref="IChatSourceCatalog"/> on every run, so adding or removing a recording between turns
/// takes effect on the next answer without touching the session.
/// </summary>
public sealed class TranscriptContextProvider : AIContextProvider
{
    private readonly IChatSourceCatalog _catalog;
    private readonly TranscriptContextBuilder _builder;
    private readonly Func<string, int> _count;
    private readonly Func<ContextBudget> _budget;
    private readonly Func<AgentSession?, string?>? _previousQuestion;

    public TranscriptContextProvider(
        IChatSourceCatalog catalog,
        TranscriptContextBuilder builder,
        Func<string, int> countTokens,
        Func<ContextBudget> budget,
        Func<AgentSession?, string?>? previousQuestion = null)
    {
        _catalog = catalog ?? throw new ArgumentNullException(nameof(catalog));
        _builder = builder ?? throw new ArgumentNullException(nameof(builder));
        _count = countTokens ?? throw new ArgumentNullException(nameof(countTokens));
        _budget = budget ?? throw new ArgumentNullException(nameof(budget));
        _previousQuestion = previousQuestion;
    }

    /// <summary>Where "Summarising part 2 of 5…" goes. Set by the owner before a run.</summary>
    public IProgress<string>? Status { get; set; }

    /// <summary>The context that was built by the last run (diagnostics and tests).</summary>
    public BuiltContext? LastContext { get; private set; }

    /// <summary>
    /// The base merge appends provider messages after the caller's: the excerpts must come before the question,
    /// so the provider's messages are moved in front of the first caller (External) message.
    /// </summary>
    protected override async ValueTask<AIContext> InvokingCoreAsync(InvokingContext context, CancellationToken cancellationToken = default)
    {
        var merged = await base.InvokingCoreAsync(context, cancellationToken).ConfigureAwait(false);
        if (merged.Messages is null)
            return merged;

        var all = merged.Messages.ToList();
        var mine = all.Where(m => m.GetAgentRequestMessageSourceType() == AgentRequestMessageSourceType.AIContextProvider).ToList();
        if (mine.Count == 0)
            return merged;
        var rest = all.Where(m => !mine.Contains(m)).ToList();
        var firstExternal = rest.FindIndex(m => m.GetAgentRequestMessageSourceType() == AgentRequestMessageSourceType.External);
        rest.InsertRange(firstExternal < 0 ? rest.Count : firstExternal, mine);
        merged.Messages = rest;
        return merged;
    }

    protected override async ValueTask<AIContext> ProvideAIContextAsync(InvokingContext context, CancellationToken cancellationToken = default)
    {
        var sources = _catalog.GetSources();
        var question = context.AIContext.Messages?
            .LastOrDefault(m => m.Role == ChatRole.User)?.Text ?? "";

        var system = ChatPrompts.BuildSystemMessage(sources);
        var budget = _budget();
        var contextTokens = budget.ContextTokensFor(_count(system), _count(question));

        var built = await _builder
            .BuildAsync(sources, question, _previousQuestion?.Invoke(context.Session), contextTokens, Status, cancellationToken)
            .ConfigureAwait(false);
        LastContext = built;

        return new AIContext
        {
            Instructions = system,
            Messages = [new ChatMessage(ChatRole.User, built.Text)],
        };
    }
}
