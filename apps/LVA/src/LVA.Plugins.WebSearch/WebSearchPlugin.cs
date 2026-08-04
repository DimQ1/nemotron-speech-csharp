using System.Text;
using LVA.Plugins.Abstractions;
using LVA.Plugins.WebSearch.Agents;
using LVA.Plugins.WebSearch.Search;

namespace LVA.Plugins.WebSearch;

/// <summary>
/// Plugin №1 (TZ §6.3): internet search + external-LLM analysis via
/// Microsoft Agent Framework, hidden behind ISearchAgent. Degrades to a
/// sources-only extractive answer when LLM/network is unavailable.
/// </summary>
public sealed class WebSearchPlugin : IAssistantPlugin
{
    public PluginManifest Manifest { get; } = new(
        Id: "lva.websearch",
        Version: "1.0.0",
        Capabilities: new[] { "web.search", "web.summarize", "web.analyze" },
        Permissions: PluginPermissions.InternetAccess | PluginPermissions.CloudModels,
        Settings: new PluginSettingsDefinition(
            "Веб-поиск",
            new[]
            {
                new PluginSettingField("SearchProvider", "Провайдер поиска", "Brave"),
                new PluginSettingField("SearchApiKey", "Ключ API поиска", Secret: true, Hint: "Brave Search API key"),
                new PluginSettingField("MaxPages", "Максимум страниц", "5"),
                new PluginSettingField("Llm.Provider", "Провайдер LLM"),
                new PluginSettingField("Llm.Endpoint", "Endpoint LLM"),
                new PluginSettingField("Llm.Model", "Модель LLM", "gpt-4o-mini"),
                new PluginSettingField("Llm.ApiKey", "Ключ API LLM", Secret: true)
            }));

    private IPluginContext? _context;
    private ISearchAgent? _agent;
    private ISearchProvider? _searchProvider;
    private IFetchService? _fetchService;
    private int _maxPages = 5;
    private bool _fallbackToLocal = true;

    /// <summary>Test seam: inject a custom agent (mock IChatClient scenarios).</summary>
    public void OverrideAgent(ISearchAgent agent) => _agent = agent;

    public async Task InitAsync(IPluginContext context, CancellationToken ct)
    {
        _context = context;

        var s = context.Settings;
        _maxPages = s.TryGetValue("MaxPages", out var mp) && int.TryParse(mp, out var v) ? v : 5;
        _fallbackToLocal = !s.TryGetValue("FallbackToLocalSummary", out var fb) || fb != "false";

        var http = context.HttpFactory.CreateClient("lva.websearch");
        _fetchService = new FetchService(http);

        // Search provider (Brave default; Bing/SearXNG — later additions behind ISearchProvider).
        var providerName = s.GetValueOrDefault("SearchProvider", "Brave");
        var searchKey = await context.Secrets.GetAsync(s.GetValueOrDefault("SearchApiKeySecret", "websearch.search.apikey"), ct);
        if (searchKey is not null && providerName == "Brave")
            _searchProvider = new BraveSearchProvider(http, searchKey);

        // LLM client (optional — plugin degrades without it).
        var llmProvider = s.GetValueOrDefault("Llm.Provider", "");
        if (!string.IsNullOrEmpty(llmProvider))
        {
            var llmKey = await context.Secrets.GetAsync(s.GetValueOrDefault("Llm.ApiKeySecret", "websearch.llm.apikey"), ct);
            if (llmKey is not null)
            {
                var settings = new LlmSettings(
                    llmProvider,
                    s.GetValueOrDefault("Llm.Endpoint", ""),
                    s.GetValueOrDefault("Llm.Model", "gpt-4o-mini"),
                    s.GetValueOrDefault("Llm.ApiKeySecret", "websearch.llm.apikey"));
                _agent = new MafSearchAgent(LlmClientFactory.Create(settings, llmKey), _maxPages);
            }
        }
    }

    public async Task<ToolResult> ExecuteAsync(ToolInvocation call, IProgress<TaskProgress> progress, CancellationToken ct)
    {
        if (_context is null)
            return ToolResult.Fail("плагин не инициализирован");

        if (_searchProvider is null)
            return ToolResult.Fail("поиск не сконфигурирован (нет ключа провайдера)");

        try
        {
            if (_agent is not null)
            {
                var report = await _agent.ResearchAsync(
                    call.Context,
                    (q, n, c) => _searchProvider.SearchAsync(q, n, c),
                    (url, c) => _fetchService!.FetchTextAsync(url, 8000, c),
                    progress, ct);

                return ToolResult.Ok(RenderMarkdown(report), report.Sources);
            }

            // Degradation: no LLM configured — return search results with an explicit marker (TZ §6.3.2).
            if (!_fallbackToLocal)
                return ToolResult.Fail("внешняя LLM не сконфигурирована");

            progress.Report(new TaskProgress(30, "ищу в интернете"));
            var hits = await _searchProvider.SearchAsync(call.Context, _maxPages, ct);
            if (hits.Count == 0)
                return ToolResult.Fail("ничего не найдено");

            var sb = new StringBuilder()
                .AppendLine("**Обобщено локально, без внешнего анализа.** Найденные источники:")
                .AppendLine();
            foreach (var h in hits)
                sb.AppendLine($"- [{h.Title}]({h.Url}) — {h.Snippet}");

            return ToolResult.Ok(sb.ToString(), hits.Select(h => h.Url).ToArray());
        }
        catch (OperationCanceledException) { throw; }
        catch (Exception ex)
        {
            return ToolResult.Fail($"ошибка поиска: {ex.Message}");
        }
    }

    private static string RenderMarkdown(Agents.SearchReport report)
    {
        var sb = new StringBuilder()
            .AppendLine(report.Summary)
            .AppendLine()
            .AppendLine("**Ключевые факты:**");
        foreach (var f in report.KeyFacts)
            sb.AppendLine($"- {f}");
        sb.AppendLine()
            .AppendLine($"_Уверенность: {report.Confidence}; свежесть: {report.Freshness}_")
            .AppendLine()
            .AppendLine("**Источники:**");
        foreach (var src in report.Sources)
            sb.AppendLine($"- {src}");
        return sb.ToString();
    }
}
