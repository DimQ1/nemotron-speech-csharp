using System.Text.Json.Serialization;

namespace LVA.Plugins.WebSearch.Agents;

/// <summary>Structured output of the Analyst agent (TZ §6.3.2).</summary>
public sealed record SearchReport(
    [property: JsonPropertyName("summary")] string Summary,
    [property: JsonPropertyName("keyFacts")] string[] KeyFacts,
    [property: JsonPropertyName("sources")] string[] Sources,
    [property: JsonPropertyName("confidence")] string Confidence,
    [property: JsonPropertyName("freshness")] string Freshness);
