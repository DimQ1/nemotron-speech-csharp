using System.IO;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace VoiceType.Services.Intents;

/// <summary>A single voice-command intent with exemplar phrases and lexical triggers.</summary>
public sealed record VoiceIntentDefinition(
    [property: JsonPropertyName("id")] string Id,
    [property: JsonPropertyName("exemplars")] string[] Exemplars,
    [property: JsonPropertyName("lexicalTriggers")] string[]? LexicalTriggers = null);

/// <summary>
/// Voice-command taxonomy. Loaded from an optional JSON file (user-editable
/// exemplars); otherwise <see cref="Default"/> is used.
/// </summary>
public sealed class VoiceIntentTaxonomy
{
    public IReadOnlyList<VoiceIntentDefinition> Intents { get; }

    public VoiceIntentTaxonomy(IReadOnlyList<VoiceIntentDefinition> intents) => Intents = intents;

    public VoiceIntentDefinition? Find(string id)
        => Intents.FirstOrDefault(i => string.Equals(i.Id, id, StringComparison.OrdinalIgnoreCase));

    /// <summary>Load from JSON; falls back to <see cref="Default"/> when missing/invalid.</summary>
    public static VoiceIntentTaxonomy Load(string? path)
    {
        if (!string.IsNullOrWhiteSpace(path) && File.Exists(path))
        {
            try
            {
                var intents = JsonSerializer.Deserialize<List<VoiceIntentDefinition>>(File.ReadAllText(path));
                if (intents is { Count: > 0 })
                    return new VoiceIntentTaxonomy(intents);
            }
            catch { /* fall back to default */ }
        }
        return Default;
    }

    /// <summary>Built-in RU/EN taxonomy for the dictation-control commands.</summary>
    public static VoiceIntentTaxonomy Default { get; } = new(new[]
    {
        new VoiceIntentDefinition(VoiceIntentIds.StartTyping,
            new[] { "начни печатать", "начать печатать текст", "включи печать", "печатай", "режим печати",
                    "start typing", "begin typing", "type this", "start dictation", "enable typing" },
            new[] { "начни печатать", "начать печатать", "включи печать", "печатай текст",
                    "start typing", "begin typing", "start dictation" }),
        new VoiceIntentDefinition(VoiceIntentIds.StopTyping,
            new[] { "стоп печать", "прекрати печатать", "выключи печать", "хватит печатать", "останови ввод",
                    "stop typing", "stop dictation", "stop writing", "disable typing", "end typing" },
            new[] { "стоп печать", "прекрати печатать", "выключи печать", "стоп диктовка",
                    "stop typing", "stop dictation" }),
        new VoiceIntentDefinition(VoiceIntentIds.SwitchTarget,
            new[] { "набери в", "напечатай в приложении", "переключись в", "печатай в окне", "введи в",
                    "type this in", "type into", "switch to", "write in", "print in" },
            new[] { "набери в", "напечатай в", "переключись в", "печатай в", "введи в",
                    "type this in", "type into", "switch to", "write in" }),
        new VoiceIntentDefinition(VoiceIntentIds.Cancel,
            new[] { "отмена", "не надо", "забудь", "отбой", "стой",
                    "cancel", "never mind", "forget it", "abort", "stop that" },
            new[] { "отмена", "не надо", "забудь", "отбой", "cancel", "never mind", "forget it" }),
    });
}
