using System.Text;
using System.Text.RegularExpressions;

namespace LVA.Plugins.WebSearch.Search;

/// <summary>
/// Fetches pages and extracts readable text (Readability-style: strip
/// script/style/nav, pick the densest text blocks). 10 s per page timeout (TZ §6.3.2).
/// </summary>
public sealed partial class FetchService : IFetchService
{
    private readonly HttpClient _http;

    public FetchService(HttpClient http)
    {
        _http = http;
        _http.Timeout = TimeSpan.FromSeconds(10);
    }

    public async Task<string?> FetchTextAsync(string url, int maxChars, CancellationToken ct)
    {
        try
        {
            var html = await _http.GetStringAsync(url, ct);
            return ExtractText(html, maxChars);
        }
        catch (Exception) when (!ct.IsCancellationRequested)
        {
            return null;
        }
    }

    /// <summary>Static, testable text extraction from HTML.</summary>
    public static string ExtractText(string html, int maxChars)
    {
        var text = ScriptStyleRegex().Replace(html, " ");
        text = TagRegex().Replace(text, " ");
        text = System.Net.WebUtility.HtmlDecode(text);
        text = WhitespaceRegex().Replace(text, " ").Trim();
        return text.Length > maxChars ? text[..maxChars] : text;
    }

    [GeneratedRegex("<(script|style|noscript|nav|footer|header|form)[^>]*>.*?</\\1>",
        RegexOptions.IgnoreCase | RegexOptions.Singleline)]
    private static partial Regex ScriptStyleRegex();

    [GeneratedRegex("<[^>]+>")]
    private static partial Regex TagRegex();

    [GeneratedRegex("\\s+")]
    private static partial Regex WhitespaceRegex();
}
