using LocalVoiceAssistant.Maui.Core;
using LocalVoiceAssistant.Maui.Models;

namespace LocalVoiceAssistant.Maui.Plugins;

public sealed class WebSearchPlugin : IAssistantPlugin
{
    private readonly IWebSearchProvider _provider;

    public WebSearchPlugin(IWebSearchProvider provider)
    {
        _provider = provider;
    }

    public string Id => "web-search";
    public string Name => "Web Search";
    public string Description => "Ищет информацию в интернете и возвращает источники.";

    public PluginManifest Manifest => new(
        Id,
        Name,
        "1.0.0",
        Description,
        new HashSet<PluginCapability> { PluginCapability.Search },
        new HashSet<PluginPermission> { PluginPermission.Network });

    public bool CanHandle(string utterance) =>
        utterance.Contains("найди", StringComparison.OrdinalIgnoreCase) ||
        utterance.Contains("поиск", StringComparison.OrdinalIgnoreCase) ||
        utterance.Contains("интернет", StringComparison.OrdinalIgnoreCase) ||
        utterance.Contains("поищи", StringComparison.OrdinalIgnoreCase);

    public async Task<AssistantResponse> ExecuteAsync(
        string utterance,
        CancellationToken cancellationToken)
    {
        var result = await InvokeAsync(
            new ToolInvocation(Id, utterance, DateTimeOffset.UtcNow),
            null,
            cancellationToken).ConfigureAwait(false);
        return result.ToAssistantResponse();
    }

    public async Task<ToolResult> InvokeAsync(
        ToolInvocation invocation,
        IProgress<PluginProgress>? progress,
        CancellationToken cancellationToken)
    {
        var query = ExtractQuery(invocation.Input);
        if (query.Length < 2)
            return ToolResult.Failure("Сформулируйте, что именно нужно найти.", "empty_query");

        try
        {
            var results = await _provider.SearchAsync(query, 5, progress, cancellationToken).ConfigureAwait(false);
            if (results.Count == 0)
                return ToolResult.Failure($"По запросу «{query}» ничего не найдено.", "no_results");

            var message = string.Join(
                Environment.NewLine,
                results.Select((result, index) => $"{index + 1}. {result.Title}\n{result.Snippet}\n{result.Url}"));
            return ToolResult.Success($"Результаты поиска для «{query}»:\n{message}", results);
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (HttpRequestException ex)
        {
            return ToolResult.Failure($"Не удалось выполнить поиск: {ex.Message}", "network_error");
        }
    }

    private static string ExtractQuery(string utterance)
    {
        var query = utterance;
        foreach (var phrase in new[] { "найди", "поищи", "поиск", "в интернете", "в интернетe", "информацию о" })
            query = query.Replace(phrase, string.Empty, StringComparison.OrdinalIgnoreCase);
        return query.Trim(' ', ',', '.', ':', ';', '?', '!');
    }
}
