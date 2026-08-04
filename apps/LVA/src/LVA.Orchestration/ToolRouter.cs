using LVA.Core.Events;
using LVA.Plugins.Abstractions;

namespace LVA.Orchestration;

/// <summary>Result of routing: which plugin should handle the question.</summary>
public sealed record RouteDecision(string PluginId, string Capability, ToolInvocation Invocation);

/// <summary>
/// Rules-based tool router (TZ §5.1, Router.Mode=rules): matches the question
/// context against plugin capability declarations and trigger keywords.
/// Deterministic, fast, testable. local-llm mode is a later opt-in.
/// </summary>
public sealed class ToolRouter
{
    /// <summary>Trigger keywords per capability (rules mode).</summary>
    private static readonly Dictionary<string, string[]> CapabilityTriggers = new(StringComparer.OrdinalIgnoreCase)
    {
        ["web.search"] = new[] { "найди", "поищи", "погугли", "что нового", "новости", "search", "find", "look up", "latest" },
        ["web.summarize"] = new[] { "обобщи", "кратко", "резюмируй", "summarize", "tl;dr" },
        ["web.analyze"] = new[] { "проанализируй", "сравни", "analyze", "compare" }
    };

    /// <summary>
    /// Routes a QuestionReady event to the best-matching plugin capability.
    /// Returns null when no plugin declares a matching capability.
    /// </summary>
    public RouteDecision? Route(QuestionReadyEvent question, IEnumerable<(string PluginId, PluginManifest Manifest)> plugins)
    {
        var lower = question.Context.ToLowerInvariant();

        // Score each capability by trigger matches.
        var scored = new List<(string Capability, int Score)>();
        foreach (var (capability, triggers) in CapabilityTriggers)
        {
            int score = triggers.Count(t => lower.Contains(t, StringComparison.OrdinalIgnoreCase));
            if (score > 0)
                scored.Add((capability, score));
        }

        // Informational-question heuristic: question intent + external-info markers
        // (dates, entities, "what/how much" about the world) → web.search if available.
        if (scored.Count == 0 && question.Intent == "question" && LooksLikeInformationalQuery(lower))
            scored.Add(("web.search", 1));

        foreach (var (capability, _) in scored.OrderByDescending(s => s.Score))
        {
            foreach (var (pluginId, manifest) in plugins)
            {
                if (manifest.Capabilities.Contains(capability, StringComparer.OrdinalIgnoreCase))
                {
                    return new RouteDecision(pluginId, capability,
                        new ToolInvocation(capability, question.Context,
                            new Dictionary<string, string>(question.Slots)));
                }
            }
        }
        return null;
    }

    /// <summary>
    /// Heuristic for informational (world-knowledge) queries vs personal/local ones.
    /// Personal queries ("который час", "моя почта") must not be routed to web search.
    /// </summary>
    private static bool LooksLikeInformationalQuery(string lower)
    {
        string[] personal = { "который час", "сколько времени", "моя ", "мои ", "мой ", "напомни", "будильник", "my " };
        if (personal.Any(p => lower.Contains(p, StringComparison.OrdinalIgnoreCase)))
            return false;

        string[] informational = { "что такое", "кто такой", "почему", "как работает", "сколько стоит",
            "цена", "курс", "погода", "новост", "what is", "who is", "why", "how does", "price", "news" };
        return informational.Any(p => lower.Contains(p, StringComparison.OrdinalIgnoreCase));
    }
}
