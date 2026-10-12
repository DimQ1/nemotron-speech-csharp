namespace VoiceType.Uno.History;

public enum HistoryUiSort
{
    NewestFirst,
    OldestFirst,
    LongestFirst,
}

/// <summary>An active filter shown as a removable chip under the search box.</summary>
public sealed record HistoryUiFilterChip(string Key, string Label);

/// <summary>A choice of the Language / Speech model pickers of the filter sheet.</summary>
public sealed record HistoryUiChoice(string? Value, string Label);

/// <summary>
/// State of the Filter sheet: "only" switches, language, model and sort. Translates to
/// the Core <see cref="HistoryFilter"/> plus the Inject and sort rules the search does
/// not know about.
/// </summary>
public sealed record HistoryUiFilterOptions
{
    public static readonly HistoryUiFilterOptions Default = new();

    public const string WithTranslationKey = "translation";
    public const string InChatKey = "chat";
    public const string WithAudioKey = "audio";
    public const string InjectOnlyKey = "inject";
    public const string LanguageKey = "language";
    public const string ModelKey = "model";
    public const string SortKey = "sort";

    public bool WithTranslation { get; init; }
    public bool InChat { get; init; }
    public bool WithAudio { get; init; }
    public bool InjectOnly { get; init; }

    /// <summary>Language code (case-insensitive); null = any.</summary>
    public string? Language { get; init; }

    /// <summary>Model folder; null = any.</summary>
    public string? Model { get; init; }

    /// <summary>Label of <see cref="Model"/> for the chip.</summary>
    public string? ModelLabel { get; init; }

    public HistoryUiSort Sort { get; init; } = HistoryUiSort.NewestFirst;

    public bool IsActive => Chips().Count > 0;

    public HistoryFilter ToSearchFilter() => new()
    {
        HasTranslation = WithTranslation ? true : null,
        HasChat = InChat ? true : null,
        HasAudio = WithAudio ? true : null,
        Language = string.IsNullOrWhiteSpace(Language) ? null : Language,
        Model = string.IsNullOrWhiteSpace(Model) ? null : Model,
    };

    public IReadOnlyList<HistoryUiFilterChip> Chips()
    {
        var chips = new List<HistoryUiFilterChip>();
        if (WithTranslation) chips.Add(new(WithTranslationKey, "With translation"));
        if (InChat) chips.Add(new(InChatKey, "Used in a chat"));
        if (WithAudio) chips.Add(new(WithAudioKey, "Audio available"));
        if (InjectOnly) chips.Add(new(InjectOnlyKey, "Inject dictations"));
        if (!string.IsNullOrWhiteSpace(Language)) chips.Add(new(LanguageKey, "Language: " + Language.ToUpperInvariant()));
        if (!string.IsNullOrWhiteSpace(Model)) chips.Add(new(ModelKey, "Model: " + (ModelLabel ?? Model)));
        if (Sort != HistoryUiSort.NewestFirst) chips.Add(new(SortKey, SortLabel(Sort)));
        return chips;
    }

    /// <summary>The options with the chip <paramref name="key"/> removed.</summary>
    public HistoryUiFilterOptions Without(string key) => key switch
    {
        WithTranslationKey => this with { WithTranslation = false },
        InChatKey => this with { InChat = false },
        WithAudioKey => this with { WithAudio = false },
        InjectOnlyKey => this with { InjectOnly = false },
        LanguageKey => this with { Language = null },
        ModelKey => this with { Model = null, ModelLabel = null },
        SortKey => this with { Sort = HistoryUiSort.NewestFirst },
        _ => this,
    };

    public static string SortLabel(HistoryUiSort sort) => sort switch
    {
        HistoryUiSort.OldestFirst => "Oldest first",
        HistoryUiSort.LongestFirst => "Longest first",
        _ => "Newest first",
    };
}

/// <summary>Search + filter + sort of the History list.</summary>
public static class HistoryUiQuery
{
    /// <summary>
    /// Runs <see cref="HistorySearch.Search"/> with the sheet's filter, then applies the
    /// Inject rule and the sort. <paramref name="manifests"/> maps recording id → manifest;
    /// hits without a manifest are dropped.
    /// </summary>
    public static IReadOnlyList<SearchHit> Run(
        IEnumerable<HistorySearchDocument> documents,
        IReadOnlyDictionary<string, RecordingManifest> manifests,
        string? query,
        HistoryUiFilterOptions options)
    {
        ArgumentNullException.ThrowIfNull(documents);
        ArgumentNullException.ThrowIfNull(manifests);
        ArgumentNullException.ThrowIfNull(options);

        var hits = HistorySearch.Search(documents, query, options.ToSearchFilter())
            .Where(h => manifests.TryGetValue(h.Document.RecordingId, out var m)
                        && (!options.InjectOnly || m.Mode.InjectText))
            .ToList();

        return options.Sort switch
        {
            HistoryUiSort.OldestFirst => hits.OrderBy(h => h.Document.StartedAt).ToList(),
            HistoryUiSort.LongestFirst => hits
                .OrderByDescending(h => HistoryUiFormat.DurationSeconds(manifests[h.Document.RecordingId]))
                .ThenByDescending(h => h.Document.StartedAt)
                .ToList(),
            _ => hits,
        };
    }

    /// <summary>Languages present in the history (upper-case codes, sorted), with "Any language" first.</summary>
    public static IReadOnlyList<HistoryUiChoice> LanguageChoices(IEnumerable<HistorySearchDocument> documents) =>
        documents
            .SelectMany(d => d.Languages)
            .Select(l => l.Trim().ToLowerInvariant())
            .Where(l => l.Length > 0)
            .Distinct()
            .Order(StringComparer.Ordinal)
            .Select(l => new HistoryUiChoice(l, l.ToUpperInvariant()))
            .Prepend(new HistoryUiChoice(null, "Any language"))
            .ToList();

    /// <summary>Model folders present in the history with their short names, "Any model" first.</summary>
    public static IReadOnlyList<HistoryUiChoice> ModelChoices(IEnumerable<RecordingManifest> manifests) =>
        manifests
            .Select(m => m.Current?.Model)
            .Where(m => !string.IsNullOrWhiteSpace(m?.Folder))
            .GroupBy(m => m!.Folder!, StringComparer.OrdinalIgnoreCase)
            .Select(g => new HistoryUiChoice(g.Key, HistoryUiFormat.ShortModel(g.First())))
            .OrderBy(c => c.Label, StringComparer.OrdinalIgnoreCase)
            .Prepend(new HistoryUiChoice(null, "Any model"))
            .ToList();
}
