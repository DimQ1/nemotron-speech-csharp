namespace VoiceType.Uno.Services;

public sealed record LlmModelInfo(string Id, string Title, string RepoId, string FileName, long SizeBytes,
    int ContextTokens, double MinFreeRamGb, bool PhoneSupported, string Tagline, string Description)
{
    public string LocalPath => System.IO.Path.Combine(AppPaths.ModelsDir, FileName);
    public bool IsInstalled => System.IO.File.Exists(LocalPath);
}

// STUB (W2): W1 replaces this file with the real catalog at merge.
public static class LlmModelCatalog
{
    private static readonly LlmModelInfo[] s_all =
    {
        new("gemma-4-e2b", "Gemma 4 E2B", "litert-community/gemma-4-E2B-it-litert-lm", "gemma-4-E2B-it.litertlm",
            2_590_000_000L, 4096, 4, true,
            "Small and fast, works on every device",
            "Good for live translation, short chats and titles. The default."),
        new("gemma-4-e4b", "Gemma 4 E4B", "litert-community/gemma-4-E4B-it-litert-lm", "gemma-4-E4B-it.litertlm",
            3_660_000_000L, 8192, 6, false,
            "Better answers and translations",
            "Noticeably stronger than E2B on long transcripts; live translation is slower."),
        new("gemma-4-12b", "Gemma 4 12B", "litert-community/gemma-4-12B-it-litert-lm", "gemma-4-12B-it.litertlm",
            6_880_000_000L, 8192, 12, false,
            "Best quality, needs a strong computer",
            "The most accurate answers and translations. Use the GPU for acceptable speed."),
    };

    public static IReadOnlyList<LlmModelInfo> All => s_all;

    public static LlmModelInfo Default => s_all[0];

    public static LlmModelInfo? Find(string? id) =>
        s_all.FirstOrDefault(m => string.Equals(m.Id, id, StringComparison.OrdinalIgnoreCase));

    public static IReadOnlyList<LlmModelInfo> ForDevice(bool isPhone) =>
        isPhone ? s_all.Where(m => m.PhoneSupported).ToArray() : s_all;
}
