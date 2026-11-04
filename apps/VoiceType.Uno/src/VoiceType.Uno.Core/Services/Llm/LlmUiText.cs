using VoiceType.Uno.Localization;

namespace VoiceType.Uno.Services;

/// <summary>Wording of the language-model UI (Models page, banners, side menu), testable without a UI.</summary>
public static class LlmUiText
{
    public const string TabLabel = "Gemma";

    public static string ComputeTitle => Loc.T("Models.Llm.ComputeTitle");

    public static string ComputeHint => Loc.T("Models.Llm.ComputeHint");

    public static string ComputeUnavailableHint => Loc.T("Models.Llm.ComputeUnavailableHint");

    public static string SharedModelNote => Loc.T("Models.Llm.SharedModelNote");

    public static string Loading(string title) => Loc.F("Models.Llm.Loading", title);

    /// <summary>The model's tagline in the current language (the catalog keeps the English one).</summary>
    public static string TaglineOf(LlmModelInfo model) => Lookup("Models.Llm." + KeyOf(model) + ".Tagline", model.Tagline);

    /// <summary>The model's description in the current language (the catalog keeps the English one).</summary>
    public static string DescriptionOf(LlmModelInfo model) => Lookup("Models.Llm." + KeyOf(model) + ".Description", model.Description);

    public static string ShortName(LlmModelInfo model) =>
        model.Title.StartsWith("Gemma 4 ", StringComparison.Ordinal) ? model.Title["Gemma 4 ".Length..] : model.Title;

    /// <summary>Subtitle under "Live translation" in the side menu.</summary>
    public static string SideMenuSubtitle(string modelTitle) => Loc.F("Models.Llm.SideMenuSubtitle", modelTitle);

    public static string BannerMissing(string modelTitle) =>
        Loc.F("Models.Llm.BannerMissing", modelTitle);

    public static string BannerPartial(string modelTitle) =>
        Loc.F("Models.Llm.BannerPartial", modelTitle);

    public static string ChatMissingDetail(LlmModelInfo model) =>
        Loc.F("Models.Llm.ChatMissingDetail", model.Title, DownloadProgressFormatter.FormatBytes(model.SizeBytes));

    public static string ChatMissingTitle(string modelTitle, bool downloading) =>
        downloading ? Loc.F("Models.Llm.ChatDownloading", modelTitle) : Loc.F("Models.Llm.ChatNotInstalled", modelTitle);

    public static string DeleteConsequence(LlmModelInfo model) =>
        Loc.F("Models.Llm.DeleteConsequence", DownloadProgressFormatter.FormatBytes(model.SizeBytes));

    /// <summary>Inline banner for a failed switch (not enough memory, load failure).</summary>
    public static string SwitchError(LlmModelInfo model, Exception error)
    {
        var oom = error is OutOfMemoryException
            || error.Message.Contains("memory", StringComparison.OrdinalIgnoreCase);
        return oom
            ? Loc.F("Models.Llm.SwitchErrorMemory", model.Title, model.MinFreeRamGb)
            : Loc.F("Models.Llm.SwitchError", model.Title, error.Message);
    }

    /// <summary>"Switching to GPU…" while the compute backend changes.</summary>
    public static string SwitchingCompute(string label) => Loc.F("Models.Llm.SwitchingCompute", label);

    /// <summary>The red line under a card when the free RAM is below the model's need.</summary>
    public static string RamWarning(double freeGb) => Loc.F("Models.Llm.RamWarning", freeGb);

    /// <summary>Accessible name of a card's "⋯" button.</summary>
    public static string MoreActionsFor(string title) => Loc.F("Models.Card.MoreActionsFor", title);

    /// <summary>Accessible name of a card's primary button: "Download Gemma 4 E2B".</summary>
    public static string ActionFor(string action, string title) => Loc.F("Models.Card.ActionFor", action, title);

    /// <summary>The "8K context" chip.</summary>
    public static string ContextChip(int tokens) => Loc.F("Models.Llm.ContextChip", LlmUiCardBuilder.FormatContext(tokens));

    /// <summary>Key segment of a model id: characters outside <c>[A-Za-z0-9_.]</c> become <c>_</c>.</summary>
    private static string KeyOf(LlmModelInfo model) =>
        new(model.Id.Select(c => char.IsAsciiLetterOrDigit(c) || c is '_' or '.' ? c : '_').ToArray());

    private static string Lookup(string key, string fallback)
    {
        var text = Loc.T(key);
        return text == key ? fallback : text;
    }
}
