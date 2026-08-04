namespace LVA.Plugins.WebSearch.Search;

/// <summary>A single web search hit.</summary>
public sealed record SearchHit(string Title, string Url, string Snippet);

/// <summary>Search provider abstraction (Brave | Bing | Tavily | SearXNG).</summary>
public interface ISearchProvider
{
    string ProviderId { get; }
    Task<IReadOnlyList<SearchHit>> SearchAsync(string query, int maxResults, CancellationToken ct);
}

/// <summary>Fetches a page and extracts readable text.</summary>
public interface IFetchService
{
    /// <summary>Returns extracted page text (truncated), or null on failure.</summary>
    Task<string?> FetchTextAsync(string url, int maxChars, CancellationToken ct);
}
