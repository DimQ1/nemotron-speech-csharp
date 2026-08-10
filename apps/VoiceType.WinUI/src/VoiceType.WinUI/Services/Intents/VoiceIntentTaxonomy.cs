using System.Text.Json;
using System.Text.Json.Serialization;

namespace VoiceType.WinUI.Services.Intents;

public sealed record VoiceIntentDefinition(
    [property: JsonPropertyName("id")] string Id,
    [property: JsonPropertyName("exemplars")] string[] Exemplars,
    [property: JsonPropertyName("lexicalTriggers")] string[]? LexicalTriggers = null);

public sealed class VoiceIntentTaxonomy
{
    public IReadOnlyList<VoiceIntentDefinition> Intents { get; }

    public VoiceIntentTaxonomy(IReadOnlyList<VoiceIntentDefinition> intents) => Intents = intents;

    public VoiceIntentDefinition? Find(string id) =>
        Intents.FirstOrDefault(intent => string.Equals(intent.Id, id, StringComparison.OrdinalIgnoreCase));

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
            catch
            {
                // Invalid custom taxonomy falls back to the built-in commands.
            }
        }

        return Default;
    }

    public static VoiceIntentTaxonomy Default { get; } = new(new[]
    {
        new VoiceIntentDefinition(VoiceIntentIds.StartTyping,
            new[] { "начни печатать", "начни печать", "начать печатать текст", "начать печать", "начни ввод", "начать ввод", "включи печать", "включить печать", "печатай", "режим печати", "можешь начать набирать текст", "давай снова вводить текст", "продолжай диктовку в поле",
                    "start typing", "begin typing", "type this", "start dictation", "enable typing" },
            new[] { "начни печатать", "начни печать", "начать печатать", "начать печать", "начни ввод", "начать ввод", "включи печать", "включить печать", "печатай текст",
                    "start typing", "begin typing", "start dictation" }),
        new VoiceIntentDefinition(VoiceIntentIds.StopTyping,
            new[] { "стоп печать", "прекрати печатать", "выключи печать", "хватит печатать", "останови ввод", "останови печать", "остановить печать", "перестань вводить текст", "больше не печатай", "поставь диктовку на паузу",
                    "stop typing", "stop dictation", "stop writing", "disable typing", "end typing" },
            new[] { "стоп печать", "прекрати печатать", "выключи печать", "останови печать", "остановить печать", "стоп диктовка",
                    "stop typing", "stop dictation" }),
        new VoiceIntentDefinition(VoiceIntentIds.SwitchTarget,
            new[] { "набери в", "напечатай в приложении", "переключись в", "печатай в окне", "введи в", "отправь следующий текст в", "продиктуй это в", "запиши фразу в",
                    "type this in", "type into", "switch to", "write in", "print in" },
            new[] { "набери в", "напечатай в", "переключись в", "печатай в", "введи в",
                    "type this in", "type into", "switch to", "write in" }),
        new VoiceIntentDefinition(VoiceIntentIds.Cancel,
            new[] { "отмена", "не надо", "забудь", "отбой", "стой",
                    "cancel", "never mind", "forget it", "abort", "stop that" },
            new[] { "отмена", "не надо", "забудь", "отбой", "cancel", "never mind", "forget it" }),
    });
}