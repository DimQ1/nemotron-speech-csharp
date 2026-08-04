using System.Net.Http.Json;
using System.Text.Json.Serialization;

namespace LVA.Plugins.WebSearch.Search;

/// <summary>Brave Search API provider.</summary>
public sealed class BraveSearchProvider : ISearchProvider
{
    private readonly HttpClient _http;
    private readonly string _apiKey;

    public string ProviderId => "Brave";

    public BraveSearchProvider(HttpClient http, string apiKey)
    {
        _http = http;
        _apiKey = apiKey;
    }

    public async Task<IReadOnlyList<SearchHit>> SearchAsync(string query, int maxResults, CancellationToken ct)
    {
        using var request = new HttpRequestMessage(HttpMethod.Get,
            $"https://api.search.brave.com/res/v1/web/search?q={Uri.EscapeDataString(query)}&count={maxResults}");
        request.Headers.Add("X-Subscription-Token", _apiKey);
        request.Headers.Add("Accept", "application/json");

        using var response = await _http.SendAsync(request, ct);
        response.EnsureSuccessStatusCode();

        var payload = await response.Content.ReadFromJsonAsync<BraveResponse>(ct);
        return payload?.Web?.Results?
            .Select(r => new SearchHit(r.Title ?? "", r.Url ?? "", r.Description ?? ""))
            .ToArray() ?? Array.Empty<SearchHit>();
    }

    private sealed class BraveResponse
    {
        [JsonPropertyName("web")] public BraveWeb? Web { get; set; }
    }

    private sealed class BraveWeb
    {
        [JsonPropertyName("results")] public BraveResult[]? Results { get; set; }
    }

    private sealed class BraveResult
    {
        [JsonPropertyName("title")] public string? Title { get; set; }
        [JsonPropertyName("url")] public string? Url { get; set; }
        [JsonPropertyName("description")] public string? Description { get; set; }
    }
}
