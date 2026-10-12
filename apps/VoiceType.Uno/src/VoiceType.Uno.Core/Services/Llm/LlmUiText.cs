namespace VoiceType.Uno.Services;

/// <summary>Wording of the language-model UI (Models page, banners, side menu), testable without a UI.</summary>
public static class LlmUiText
{
    public const string TabLabel = "Gemma";

    public const string ComputeTitle = "Compute";

    public const string ComputeHint =
        "GPU is much faster for the 12B model; the first load compiles shaders and takes a while.";

    public const string ComputeUnavailableHint = "The language model runs on the CPU on this device.";

    public const string SharedModelNote =
        "Live translation, chat, titles and saved-transcript translation all share the one selected model. "
        + "A larger model answers better but live translation becomes slower — switch back to E2B if live translation lags.";

    public static string Loading(string title) => $"Loading {title}…";

    public static string ShortName(LlmModelInfo model) =>
        model.Title.StartsWith("Gemma 4 ", StringComparison.Ordinal) ? model.Title["Gemma 4 ".Length..] : model.Title;

    /// <summary>Subtitle under "Live translation" in the side menu.</summary>
    public static string SideMenuSubtitle(string modelTitle) => $"{modelTitle}, on-device";

    public static string BannerMissing(string modelTitle) =>
        $"{modelTitle} is not downloaded — translation will use the HTTP server fallback or stay off.";

    public static string BannerPartial(string modelTitle) =>
        $"{modelTitle} download is incomplete — re-download it to finish.";

    public static string ChatMissingDetail(LlmModelInfo model) =>
        $"Chat runs on this device. Download the {model.Title} model (about {DownloadProgressFormatter.FormatBytes(model.SizeBytes)}) to ask questions.";

    public static string ChatMissingTitle(string modelTitle, bool downloading) =>
        downloading ? $"Downloading {modelTitle}…" : $"{modelTitle} is not installed";

    public static string DeleteConsequence(LlmModelInfo model) =>
        $"The {DownloadProgressFormatter.FormatBytes(model.SizeBytes)} model file is removed from this device. You can download it again later.";

    /// <summary>Inline banner for a failed switch (not enough memory, load failure).</summary>
    public static string SwitchError(LlmModelInfo model, Exception error)
    {
        var oom = error is OutOfMemoryException
            || error.Message.Contains("memory", StringComparison.OrdinalIgnoreCase);
        return oom
            ? $"Not enough memory to load {model.Title} (needs ≈{model.MinFreeRamGb:0.#} GB free). Close other apps or pick a smaller model."
            : $"Could not load {model.Title}: {error.Message}";
    }
}
