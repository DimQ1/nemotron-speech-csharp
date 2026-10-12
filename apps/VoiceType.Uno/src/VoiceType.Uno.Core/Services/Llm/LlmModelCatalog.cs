namespace VoiceType.Uno.Services;

public sealed record LlmModelInfo(string Id, string Title, string RepoId, string FileName, long SizeBytes,
    int ContextTokens, double MinFreeRamGb, bool PhoneSupported, string Tagline, string Description)
{
    public string LocalPath => System.IO.Path.Combine(AppPaths.ModelsDir, FileName);
    public bool IsInstalled => System.IO.File.Exists(LocalPath);
}
public static class LlmModelCatalog
{
    public const string DefaultId = "gemma-4-e2b";

    // Context windows and memory come from docs/research/litert/engine-improvements.md and from measurements on a
    // Core Ultra 7 255HX / 31 GB / RTX 5070 Ti laptop (LiteRT-LM 0.18, window 4096 for E2B, 8192 for the others):
    //   E2B  cpu 3.1 GB private after a 2k chat (3.5 GB at 8192), gpu 4.7 GB private + 1.6 GB VRAM
    //   E4B  cpu 4.9 GB private,                                  gpu 9.4 GB private + 4.3 GB VRAM
    //   12B  cpu ~9 GB private,                                   gpu 16 GB private + 8 GB VRAM (and see below)
    // Private bytes on the GPU backend are mostly driver-mapped (working set stays under 1.4 GB), but MinFreeRamGb
    // is kept conservative so the Models page can warn before a load that would make the machine swap.
    public static IReadOnlyList<LlmModelInfo> All { get; } =
    [
        new LlmModelInfo(
            DefaultId, "Gemma 4 E2B", "litert-community/gemma-4-E2B-it-litert-lm", "gemma-4-E2B-it.litertlm",
            2_588_147_712, 4096, 4.0, true,
            "Fast and light. Good for live translation and short chats.",
            "The default model: 2.6 GB on disk, about 3 GB of memory while loaded. Runs on phones and laptops, " +
            "translates live without lag and answers simple questions about a recording."),
        new LlmModelInfo(
            "gemma-4-e4b", "Gemma 4 E4B", "litert-community/gemma-4-E4B-it-litert-lm", "gemma-4-E4B-it.litertlm",
            3_659_530_240, 8192, 8.0, false,
            "Better answers and translation, twice the reading window. Wants the GPU.",
            "Noticeably more accurate chat answers and summaries of long recordings, and a longer context window " +
            "(8192 tokens). 3.7 GB on disk. Use the GPU backend: on the CPU it generates about 4 words per second " +
            "and needs half a minute to read a long question. Desktop only."),
        new LlmModelInfo(
            "gemma-4-12b", "Gemma 4 12B", "litert-community/gemma-4-12B-it-litert-lm", "gemma-4-12B-it.litertlm",
            6_883_278_368, 8192, 14.0, false,
            "Experimental. Highest quality, very slow on the CPU. Desktop only.",
            "The largest model offered: 6.9 GB on disk, about 9 GB of memory on the CPU (about 8 GB of video memory " +
            "on the GPU). On the CPU it is accurate but needs about two minutes to read a 2000-token question. " +
            "On the GPU the current LiteRT-LM runtime returns garbage for prompts longer than about 500 tokens, " +
            "so chat over a recording does not work there yet."),
    ];

    public static LlmModelInfo Default => All[0];

    public static LlmModelInfo? Find(string? id) =>
        string.IsNullOrWhiteSpace(id)
            ? null
            : All.FirstOrDefault(m => string.Equals(m.Id, id.Trim(), StringComparison.OrdinalIgnoreCase));

    /// <summary>
    /// The model the shared engine should run: the selected one when installed, otherwise the default, otherwise
    /// any installed one; the default (not installed) when none is. Android only ever runs phone-supported models.
    /// </summary>
    public static LlmModelInfo Resolve(string? selectedId)
    {
        var onPhone = OperatingSystem.IsAndroid();
        var candidates = ForDevice(onPhone);
        var selected = Find(selectedId);
        if (selected is not null && candidates.Contains(selected) && selected.IsInstalled)
            return selected;
        if (Default.IsInstalled)
            return Default;
        return candidates.FirstOrDefault(m => m.IsInstalled) ?? Default;
    }

    /// <summary>
    /// Download-queue key of a model. The default model keeps the key it always had, so the first-run banner, the
    /// translate page and the Models page all see one and the same job.
    /// </summary>
    public static string DownloadKey(LlmModelInfo model) =>
        string.Equals(model.Id, DefaultId, StringComparison.Ordinal) ? "translation-model" : "llm-" + model.Id;

    /// <summary>Phones get only the models marked <see cref="LlmModelInfo.PhoneSupported"/>.</summary>
    public static IReadOnlyList<LlmModelInfo> ForDevice(bool isPhone) =>
        isPhone ? All.Where(m => m.PhoneSupported).ToList() : All;
}
