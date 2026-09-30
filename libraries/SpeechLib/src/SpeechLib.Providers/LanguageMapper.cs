namespace SpeechLib;

/// <summary>Maps BCP-47 language codes to numeric lang_id for multilingual speech models.</summary>
public static class LanguageMapper
{
    private static readonly Dictionary<string, int> Map = new(StringComparer.OrdinalIgnoreCase)
    {
        {"en", 0}, {"en-US", 0}, {"en-GB", 1},
        {"es", 3}, {"es-ES", 2}, {"es-US", 3},
        {"zh", 4}, {"zh-CN", 4}, {"zh-TW", 5},
        {"hi", 6}, {"hi-IN", 6},
        {"ar", 7}, {"ar-AR", 7},
        {"fr", 8}, {"fr-FR", 8}, {"fr-CA", 100},
        {"de", 9}, {"de-DE", 9},
        {"ja", 10}, {"ja-JP", 10},
        {"ru", 11}, {"ru-RU", 11},
        {"pt", 13}, {"pt-BR", 12}, {"pt-PT", 13},
        {"ko", 14}, {"ko-KR", 14},
        {"it", 15}, {"it-IT", 15},
        {"nl", 16}, {"nl-NL", 16},
        {"pl", 17}, {"pl-PL", 17},
        {"tr", 18}, {"tr-TR", 18},
        {"uk", 19}, {"uk-UA", 19},
        {"ro", 20}, {"ro-RO", 20},
        {"el", 21}, {"el-GR", 21},
        {"cs", 22}, {"cs-CZ", 22},
        {"hu", 23}, {"hu-HU", 23},
        {"sv", 24}, {"sv-SE", 24},
        {"da", 25}, {"da-DK", 25},
        {"fi", 26}, {"fi-FI", 26},
        {"no", 27}, {"no-NO", 27},
        {"sk", 28}, {"sk-SK", 28},
        {"hr", 29}, {"hr-HR", 29},
        {"bg", 30}, {"bg-BG", 30},
        {"lt", 31}, {"lt-LT", 31},
        {"th", 32}, {"th-TH", 32},
        {"vi", 33}, {"vi-VN", 33},
        {"et", 60}, {"et-EE", 60},
        {"lv", 61}, {"lv-LV", 61},
        {"sl", 62}, {"sl-SI", 62},
        {"he", 64}, {"he-IL", 64},
        {"mt", 102}, {"mt-MT", 102},
        {"nb", 103}, {"nb-NO", 103},
        {"nn", 104}, {"nn-NO", 104},
        {"auto", 101},
    };

    /// <summary>Resolve language code/name/number to lang_id string. Empty => auto.</summary>
    public static string? Resolve(string? langArg)
    {
        if (string.IsNullOrEmpty(langArg)) return null;
        if (int.TryParse(langArg, out int n) && n >= 0 && n < 128) return n.ToString();
        if (Map.TryGetValue(langArg, out int id)) return id.ToString();
        return null;
    }
}
