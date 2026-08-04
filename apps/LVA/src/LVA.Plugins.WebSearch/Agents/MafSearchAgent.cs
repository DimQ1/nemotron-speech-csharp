using System.ComponentModel;
using System.Text.Json;
using LVA.Plugins.Abstractions;
using LVA.Plugins.WebSearch.Search;
using Microsoft.Agents.AI;
using Microsoft.Extensions.AI;

namespace LVA.Plugins.WebSearch.Agents;

/// <summary>
/// MAF-based search agent (TZ §6.3.2): QueryPlanner → search/fetch tools →
/// Researcher → Analyst producing a structured SearchReport.
/// All LLM calls go through the injected IChatClient — mockable in tests.
/// </summary>
public sealed class MafSearchAgent : ISearchAgent
{
    private readonly IChatClient _chatClient;
    private readonly int _maxPages;

    public MafSearchAgent(IChatClient chatClient, int maxPages = 5)
    {
        _chatClient = chatClient;
        _maxPages = maxPages;
    }

    public async Task<SearchReport> ResearchAsync(
        string questionContext,
        Func<string, int, CancellationToken, Task<IReadOnlyList<SearchHit>>> searchFn,
        Func<string, CancellationToken, Task<string?>> fetchFn,
        IProgress<TaskProgress> progress,
        CancellationToken ct)
    {
        // 1) Query planning: 1–3 search queries from the question context.
        progress.Report(new TaskProgress(10, "формирую запрос"));
        var queries = await PlanQueriesAsync(questionContext, ct);

        // 2) Search + fetch top pages.
        progress.Report(new TaskProgress(25, "ищу в интернете"));
        var hits = new List<SearchHit>();
        foreach (var q in queries)
        {
            ct.ThrowIfCancellationRequested();
            hits.AddRange(await searchFn(q, _maxPages, ct));
        }
        var uniqueHits = hits.GroupBy(h => h.Url).Select(g => g.First()).Take(_maxPages).ToArray();

        progress.Report(new TaskProgress(45, $"читаю {uniqueHits.Length} источников"));
        var pages = new List<(SearchHit Hit, string Text)>();
        foreach (var hit in uniqueHits)
        {
            ct.ThrowIfCancellationRequested();
            var text = await fetchFn(hit.Url, ct);
            if (!string.IsNullOrWhiteSpace(text))
                pages.Add((hit, text));
        }

        if (pages.Count == 0)
            throw new InvalidOperationException("Не удалось загрузить ни одного источника.");

        // 3) Analyst: structured report from sources.
        progress.Report(new TaskProgress(70, "анализирую (LLM)"));
        var report = await AnalyzeAsync(questionContext, pages, ct);
        progress.Report(new TaskProgress(95, "готово"));
        return report;
    }

    private async Task<string[]> PlanQueriesAsync(string questionContext, CancellationToken ct)
    {
        var planner = _chatClient.AsAIAgent(
            name: "QueryPlanner",
            instructions:
                "Ты планировщик поисковых запросов. По контексту вопроса пользователя " +
                "сформируй 1–3 коротких поисковых запроса для поиска актуальной информации. " +
                "Ответ — ТОЛЬКО JSON-массив строк, без пояснений. Язык запросов — язык вопроса.");

        var response = await planner.RunAsync(questionContext, cancellationToken: ct);
        var text = response.Text?.Trim() ?? "";

        try
        {
            var start = text.IndexOf('[');
            var end = text.LastIndexOf(']');
            if (start >= 0 && end > start)
            {
                var queries = JsonSerializer.Deserialize<string[]>(text[start..(end + 1)]);
                if (queries is { Length: > 0 })
                    return queries.Take(3).ToArray();
            }
        }
        catch (JsonException) { /* fall through */ }

        return new[] { questionContext }; // fallback: search the raw question
    }

    private async Task<SearchReport> AnalyzeAsync(
        string questionContext,
        IReadOnlyList<(SearchHit Hit, string Text)> pages,
        CancellationToken ct)
    {
        var analyst = _chatClient.AsAIAgent(
            name: "Analyst",
            instructions:
                "Ты аналитик. На основе предоставленных источников ответь на вопрос пользователя. " +
                "Ответ — ТОЛЬКО JSON со схемой: {\"summary\": string, \"keyFacts\": string[], " +
                "\"sources\": string[] (URL), \"confidence\": \"high|medium|low\", " +
                "\"freshness\": \"today|recent|dated|unknown\"}. " +
                "Каждый факт должен опираться на источник; sources обязателен и не пуст. " +
                "Язык ответа — язык вопроса.");

        var sourcesBlock = string.Join("\n\n", pages.Select((p, i) =>
            $"[Источник {i + 1}] {p.Hit.Title}\nURL: {p.Hit.Url}\n{p.Text[..Math.Min(p.Text.Length, 3000)]}"));

        var prompt = $"Вопрос: {questionContext}\n\nИсточники:\n{sourcesBlock}";
        var response = await analyst.RunAsync(prompt, cancellationToken: ct);
        var text = response.Text?.Trim() ?? "";

        var start = text.IndexOf('{');
        var end = text.LastIndexOf('}');
        if (start < 0 || end <= start)
            throw new InvalidOperationException("LLM не вернула JSON-отчёт.");

        var report = JsonSerializer.Deserialize<SearchReport>(text[start..(end + 1)],
            new JsonSerializerOptions { PropertyNameCaseInsensitive = true });

        if (report is null || string.IsNullOrWhiteSpace(report.Summary))
            throw new InvalidOperationException("Пустой отчёт аналитика.");

        // Post-validation per TZ §6.3.4 rule 4: answers without sources are rejected.
        if (report.Sources is not { Length: > 0 })
            throw new InvalidOperationException("Отчёт без источников отклонён пост-валидацией.");

        return report;
    }
}
