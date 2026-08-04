using System.Net;
using System.Text.RegularExpressions;
using LocalVoiceAssistant.Maui.Core;

namespace LocalVoiceAssistant.Maui.Plugins;

public interface IWebSearchProvider
{
    string Name { get; }
    Task<IReadOnlyList<SearchResult>> SearchAsync(
        string query,
        int limit,
        IProgress<PluginProgress>? progress,
        CancellationToken cancellationToken);
}

public sealed class DuckDuckGoSearchProvider : IWebSearchProvider
{
    private static readonly Regex ResultRegex = new(
        "<a[^>]*class=\"result__a\"[^>]*href=\"(?<url>[^\"]+)\"[^>]*>(?<title>.*?)</a>.*?<a[^>]*class=\"result__snippet\"[^>]*>(?<snippet>.*?)</a>",
        RegexOptions.Compiled | RegexOptions.IgnoreCase | RegexOptions.Singleline);

    private readonly HttpClient _httpClient;

    public DuckDuckGoSearchProvider(HttpClient? httpClient = null)
    {
        _httpClient = httpClient ?? new HttpClient();
        _httpClient.DefaultRequestHeaders.UserAgent.ParseAdd("LocalVoiceAssistant/1.0");
        _httpClient.DefaultRequestHeaders.AcceptLanguage.ParseAdd("ru-RU,ru;q=0.9,en;q=0.7");
    }

    public string Name => "DuckDuckGo HTML";

    public async Task<IReadOnlyList<SearchResult>> SearchAsync(
        string query,
        int limit,
        IProgress<PluginProgress>? progress,
        CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(query))
            return [];

        progress?.Report(new PluginProgress(0.1, $"Ищу «{query}» в интернете..."));
        var endpoint = $"https://html.duckduckgo.com/html/?q={Uri.EscapeDataString(query)}";
        using var response = await _httpClient.GetAsync(endpoint, cancellationToken).ConfigureAwait(false);
        response.EnsureSuccessStatusCode();
        var html = await response.Content.ReadAsStringAsync(cancellationToken).ConfigureAwait(false);

        var results = ResultRegex.Matches(html)
            .Cast<Match>()
            .Take(Math.Clamp(limit, 1, 10))
            .Select(match => new SearchResult(
                WebUtility.HtmlDecode(StripTags(match.Groups["title"].Value)).Trim(),
                WebUtility.HtmlDecode(match.Groups["url"].Value).Trim(),
                WebUtility.HtmlDecode(StripTags(match.Groups["snippet"].Value)).Trim(),
                Name))
            .Where(result => result.Title.Length > 0 && Uri.TryCreate(result.Url, UriKind.Absolute, out _))
            .ToArray();

        progress?.Report(new PluginProgress(1, $"Получено результатов: {results.Length}."));
        return results;
    }

    private static string StripTags(string value) => Regex.Replace(value, "<.*?>", string.Empty);
}

public sealed class BingWebSearchProvider : IWebSearchProvider
{
    private readonly HttpClient _httpClient;
    private readonly string _apiKey;

    public BingWebSearchProvider(string apiKey, HttpClient? httpClient = null)
    {
        _apiKey = apiKey;
        _httpClient = httpClient ?? new HttpClient();
    }

    public string Name => "Bing Web Search API";

    public async Task<IReadOnlyList<SearchResult>> SearchAsync(
        string query,
        int limit,
        IProgress<PluginProgress>? progress,
        CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(_apiKey))
            throw new InvalidOperationException("Bing Search API key is not configured.");

        using var request = new HttpRequestMessage(
            HttpMethod.Get,
            $"https://api.bing.microsoft.com/v7.0/search?q={Uri.EscapeDataString(query)}&count={Math.Clamp(limit, 1, 10)}&mkt=ru-RU");
        request.Headers.Add("Ocp-Apim-Subscription-Key", _apiKey);
        progress?.Report(new PluginProgress(0.2, "Запрашиваю Bing Web Search API..."));
        using var response = await _httpClient.SendAsync(request, cancellationToken).ConfigureAwait(false);
        response.EnsureSuccessStatusCode();
        await using var stream = await response.Content.ReadAsStreamAsync(cancellationToken).ConfigureAwait(false);
        using var document = await System.Text.Json.JsonDocument.ParseAsync(stream, cancellationToken: cancellationToken).ConfigureAwait(false);

        var results = document.RootElement
            .GetProperty("webPages")
            .GetProperty("value")
            .EnumerateArray()
            .Take(Math.Clamp(limit, 1, 10))
            .Select(item => new SearchResult(
                item.GetProperty("name").GetString() ?? string.Empty,
                item.GetProperty("url").GetString() ?? string.Empty,
                item.GetProperty("snippet").GetString() ?? string.Empty,
                Name))
            .ToArray();

        progress?.Report(new PluginProgress(1, $"Получено результатов: {results.Length}."));
        return results;
    }
}
