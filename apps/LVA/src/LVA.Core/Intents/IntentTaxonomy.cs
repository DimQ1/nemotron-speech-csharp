using System.Text.Json;
using System.Text.Json.Serialization;

namespace LVA.Core.Intents;

/// <summary>Canonical intent ids (TZ §4.4).</summary>
public static class IntentIds
{
    public const string Addressing = "addressing";
    public const string Question = "question";
    public const string Execute = "execute";
    public const string Cancel = "cancel";
    public const string Background = "background";
}

/// <summary>A single intent definition with user-editable exemplar phrases.</summary>
public sealed record IntentDefinition(
    [property: JsonPropertyName("id")] string Id,
    [property: JsonPropertyName("exemplars")] string[] Exemplars,
    [property: JsonPropertyName("lexicalTriggers")] string[]? LexicalTriggers = null);

/// <summary>
/// Intent taxonomy loaded from intents.json; users may edit exemplars to
/// customize how they address the assistant (TZ §4.4).
/// </summary>
public sealed class IntentTaxonomy
{
    public IReadOnlyList<IntentDefinition> Intents { get; }

    public IntentTaxonomy(IReadOnlyList<IntentDefinition> intents)
    {
        Intents = intents;
    }

    public IntentDefinition? Find(string id)
        => Intents.FirstOrDefault(i => string.Equals(i.Id, id, StringComparison.OrdinalIgnoreCase));

    public static IntentTaxonomy Load(string path)
    {
        var json = File.ReadAllText(path);
        var intents = JsonSerializer.Deserialize<List<IntentDefinition>>(json)
            ?? throw new InvalidDataException($"Failed to parse intents file: {path}");
        return new IntentTaxonomy(intents);
    }

    /// <summary>Default taxonomy used when no intents.json exists yet.</summary>
    public static IntentTaxonomy Default { get; } = new(new[]
    {
        new IntentDefinition(IntentIds.Addressing,
            new[] { "ассистент", "слушай", "эй ассистент", "assistant", "hey assistant", "listen up" },
            new[] { "ассистент", "слушай", "assistant", "hey" }),
        new IntentDefinition(IntentIds.Question,
            new[] { "как сделать", "почему", "сколько", "что такое", "когда будет", "how to", "why", "what is", "how many" }),
        new IntentDefinition(IntentIds.Execute,
            new[] { "выполни", "теперь ответь", "давай сделай", "запускай", "do it", "go ahead", "answer now" },
            new[] { "выполни", "ответь", "сделай", "do it", "go ahead" }),
        new IntentDefinition(IntentIds.Cancel,
            new[] { "не надо", "отмена", "забудь", "отбой", "cancel", "never mind", "forget it" },
            new[] { "отмена", "не надо", "забудь", "cancel", "never mind" }),
        new IntentDefinition(IntentIds.Background,
            new[] { "разговор третьих лиц", "телевизор", "радио" })
    });
}
