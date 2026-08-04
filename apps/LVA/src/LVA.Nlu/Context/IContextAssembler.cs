using LVA.Core.Context;

namespace LVA.Nlu.Context;

/// <summary>
/// L3 context assembler (TZ §4.3): on QuestionReady, selects only relevant
/// segments from the buffer via dense (+sparse) scoring against the aggregated
/// question. Swap via configuration; without L3, context = concatenation.
/// </summary>
public interface IContextAssembler : IDisposable
{
    string ModelId { get; }

    /// <summary>
    /// Returns buffer segments relevant to the aggregated question
    /// (dense cosine + hybrid weighting).
    /// </summary>
    Task<IReadOnlyList<ContextSegment>> SelectRelevantAsync(
        string question, IReadOnlyList<ContextSegment> segments, CancellationToken ct);
}
