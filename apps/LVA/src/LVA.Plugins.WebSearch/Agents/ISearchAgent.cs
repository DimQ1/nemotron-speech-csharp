using LVA.Plugins.Abstractions;

namespace LVA.Plugins.WebSearch.Agents;

/// <summary>
/// Search+analysis orchestration behind an interface (TZ §6.3.2):
/// MAF is hidden inside the plugin; swapping the framework touches nothing else.
/// </summary>
public interface ISearchAgent
{
    Task<SearchReport> ResearchAsync(
        string questionContext,
        Func<string, int, CancellationToken, Task<IReadOnlyList<Search.SearchHit>>> searchFn,
        Func<string, CancellationToken, Task<string?>> fetchFn,
        IProgress<TaskProgress> progress,
        CancellationToken ct);
}
