using VoiceType.Uno.Localization;

namespace VoiceType.Uno.Services;

public enum LlmUiChipKind { Plain, Recommended, InUse, Warning }

public sealed record LlmUiChip(string Text, LlmUiChipKind Kind);

/// <summary>The one primary button of a language-model card.</summary>
public enum LlmUiAction { Download, Cancel, Use, InUse }

/// <summary>Everything a language-model card shows, computed from the service state (no UI types).</summary>
public sealed record LlmUiCard(
    LlmModelInfo Model,
    LlmModelState State,
    IReadOnlyList<LlmUiChip> Chips,
    string StatusText,
    string RamText,
    bool RamLow,
    LlmUiAction Action,
    string ActionText,
    bool ActionVisible,
    bool ActionEnabled,
    bool ShowProgress,
    double Percent,
    bool CanDelete);

public static class LlmUiCardBuilder
{
    /// <summary>
    /// The cards for this device in catalog order (E2B first). Phones get only the models
    /// that run there.
    /// </summary>
    public static IReadOnlyList<LlmModelInfo> ModelsFor(bool isPhone, IReadOnlyList<LlmModelInfo>? catalog = null) =>
        (catalog ?? LlmModelCatalog.All).Where(m => !isPhone || m.PhoneSupported).ToArray();

    /// <summary>
    /// Builds one card. <paramref name="loadingModelId"/> is the model that is being switched to
    /// right now (all actions wait for it); <paramref name="availableGb"/> is the free RAM.
    /// </summary>
    public static LlmUiCard Build(
        LlmModelInfo model,
        LlmModelState state,
        double? percent,
        double? availableGb,
        string? loadingModelId = null,
        string defaultModelId = "gemma-4-e2b",
        bool isPhone = false)
    {
        var isActive = state == LlmModelState.Active;
        var loading = loadingModelId is not null;
        var isLoadingThis = string.Equals(loadingModelId, model.Id, StringComparison.OrdinalIgnoreCase);

        // An active model is already in memory, so "free now" would blame it for its own use.
        var fit = isActive ? LlmUiRamFit.Fits : LlmUiRamCheck.Evaluate(model.MinFreeRamGb, availableGb);
        var low = fit == LlmUiRamFit.Low;

        var chips = new List<LlmUiChip>
        {
            new(DownloadProgressFormatter.FormatBytes(model.SizeBytes), LlmUiChipKind.Plain),
            new(LlmUiText.ContextChip(model.ContextTokens), LlmUiChipKind.Plain),
        };
        if (string.Equals(model.Id, defaultModelId, StringComparison.OrdinalIgnoreCase))
            chips.Add(new(Loc.T("Models.Chip.Recommended"), LlmUiChipKind.Recommended));
        if (!model.PhoneSupported && !isPhone)
            chips.Add(new(Loc.T("Models.Chip.DesktopOnly"), LlmUiChipKind.Plain));
        if (isActive)
            chips.Add(new(Loc.T("Models.Chip.InUse"), LlmUiChipKind.InUse));
        else if (state == LlmModelState.Installed)
            chips.Add(new(Loc.T("Models.Chip.Installed"), LlmUiChipKind.Plain));
        if (low)
            chips.Add(new(Loc.T("Models.Chip.LowMemory"), LlmUiChipKind.Warning));

        var pct = Math.Clamp(percent ?? 0, 0, 100);
        var status = isLoadingThis
            ? LlmUiText.Loading(model.Title)
            : state switch
            {
                LlmModelState.Downloading => Loc.T("Models.Card.Status.Downloading"),
                LlmModelState.Active => Loc.T("Models.Llm.Status.Active"),
                LlmModelState.Installed => Loc.T("Models.Card.Status.Installed"),
                _ => Loc.T("Models.Card.Status.NotDownloaded"),
            };

        var (action, text) = state switch
        {
            LlmModelState.Downloading => (LlmUiAction.Cancel, Loc.T("Models.Card.Action.Cancel")),
            LlmModelState.Installed => (LlmUiAction.Use, isLoadingThis ? Loc.T("Models.Card.Action.Loading") : Loc.T("Models.Card.Action.Use")),
            LlmModelState.Active => (LlmUiAction.InUse, Loc.T("Models.Card.Action.InUse")),
            _ => (LlmUiAction.Download, Loc.T("Models.Card.Action.Download")),
        };

        return new LlmUiCard(
            model, state, chips, status,
            // The warning line already names the free RAM when it is too low.
            LlmUiRamCheck.Describe(model.MinFreeRamGb, isActive || low ? null : availableGb),
            low, action, text,
            ActionVisible: action != LlmUiAction.InUse,
            // Cancel stays usable while another model loads; starting or switching waits.
            ActionEnabled: action == LlmUiAction.Cancel || !loading,
            ShowProgress: state == LlmModelState.Downloading,
            Percent: pct,
            CanDelete: state == LlmModelState.Installed && !loading);
    }

    /// <summary>"4K" / "8K" / "128K" for a context window in tokens.</summary>
    public static string FormatContext(int tokens) =>
        tokens >= 1024 ? $"{tokens / 1024}K" : tokens.ToString();
}
