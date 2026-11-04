using VoiceType.Uno.Localization;

namespace VoiceType.Uno.History;

/// <summary>Date groups of the History list (local date of the start time).</summary>
public enum HistoryDateGroup
{
    Today,
    Yesterday,
    Earlier,
}

/// <summary>Look of a row chip.</summary>
public enum HistoryBadgeKind
{
    /// <summary>Outlined chip (chats, versions, Inject).</summary>
    Neutral,
    /// <summary>Tinted chip (translation).</summary>
    Accent,
    /// <summary>Caution chip (No audio, recovered).</summary>
    Warning,
}

public sealed record HistoryUiBadge(string Text, HistoryBadgeKind Kind, string AutomationName);

/// <summary>Everything one History row shows.</summary>
public sealed record HistoryUiRow(
    string Id,
    string Title,
    string DurationText,
    string MetaText,
    IReadOnlyList<HistoryUiBadge> Badges,
    string? Snippet,
    DateTimeOffset StartedAt,
    double DurationSeconds,
    bool HasAudio,
    long AudioBytes)
{
    /// <summary>Screen-reader name of the row button: title, meta and chips.</summary>
    public string AutomationName =>
        HistoryUiFormat.Join([Title, MetaText, .. Badges.Select(b => b.AutomationName)]);
}

public sealed record HistoryUiGroup(HistoryDateGroup Group, string Label, IReadOnlyList<HistoryUiRow> Rows);

/// <summary>Builds the rows and date groups of the History list.</summary>
public static class HistoryUiRows
{
    public static HistoryDateGroup GroupOf(DateTimeOffset startedAt, DateTimeOffset now)
    {
        var day = startedAt.ToOffset(now.Offset).Date;
        var today = now.Date;
        if (day >= today)
            return HistoryDateGroup.Today;
        return day == today.AddDays(-1) ? HistoryDateGroup.Yesterday : HistoryDateGroup.Earlier;
    }

    public static string GroupLabel(HistoryDateGroup group) => group switch
    {
        HistoryDateGroup.Today => Loc.T("History.Group.Today"),
        HistoryDateGroup.Yesterday => Loc.T("History.Group.Yesterday"),
        _ => Loc.T("History.Group.Earlier"),
    };

    /// <summary>
    /// Chips of a row: <c>→ RU</c> per translation language, <c>2 chats</c>, the current
    /// version id when there is more than one version, <c>Inject</c>, <c>No audio</c>,
    /// <c>Recovered</c>.
    /// </summary>
    public static IReadOnlyList<HistoryUiBadge> Badges(RecordingManifest manifest, int chatCount)
    {
        ArgumentNullException.ThrowIfNull(manifest);
        var badges = new List<HistoryUiBadge>();

        foreach (var language in manifest.Translations
                     .Where(t => t.Status != TranslationStatuses.Failed && !string.IsNullOrWhiteSpace(t.TargetLanguage))
                     .Select(t => t.TargetLanguage.Trim().ToUpperInvariant())
                     .Distinct())
            badges.Add(new HistoryUiBadge("→ " + language, HistoryBadgeKind.Accent, Loc.F("History.Row.TranslatedTo", language)));

        if (chatCount > 0)
            badges.Add(new HistoryUiBadge(Loc.Plural("History.Row.Chats", chatCount), HistoryBadgeKind.Neutral,
                Loc.Plural("History.Row.UsedInChats", chatCount)));

        if (manifest.Versions.Count > 1 && manifest.CurrentVersion is { } current)
            badges.Add(new HistoryUiBadge(current, HistoryBadgeKind.Neutral,
                Loc.F("History.Row.CurrentVersion", current, manifest.Versions.Count)));

        if (manifest.Mode.InjectText)
            badges.Add(new HistoryUiBadge(Loc.T("History.Row.Inject"), HistoryBadgeKind.Neutral, Loc.T("History.Row.InjectName")));

        if (!manifest.HasAudio)
            badges.Add(new HistoryUiBadge(Loc.T("History.Row.NoAudio"), HistoryBadgeKind.Warning, Loc.T("History.Row.NoAudio")));

        if (manifest.State is RecordingStates.Recovered or RecordingStates.Incomplete)
            badges.Add(new HistoryUiBadge(
                Loc.T(manifest.State == RecordingStates.Recovered ? "History.Row.Recovered" : "History.Row.Incomplete"),
                HistoryBadgeKind.Warning, Loc.T("History.Row.RecoveredName")));

        return badges;
    }

    /// <summary>Snippet line for a search hit (where the words were found).</summary>
    public static string? Snippet(SearchHit? hit, int maxChars = 90)
    {
        if (hit is null || hit.MatchedIn == SearchField.None)
            return null;
        if (hit.MatchedIn.HasFlag(SearchField.Transcript) && hit.Segment is { } segment)
        {
            var text = segment.Text.Trim();
            if (text.Length > maxChars)
                text = text[..maxChars].TrimEnd() + "…";
            return HistoryUiFormat.SegmentTime(segment.Start) + "  " + text;
        }
        if (hit.MatchedIn.HasFlag(SearchField.Title))
            return null;
        if (hit.MatchedIn.HasFlag(SearchField.Translation))
            return Loc.T("History.Row.FoundInTranslation");
        return hit.MatchedIn.HasFlag(SearchField.Chat) ? Loc.T("History.Row.FoundInChat") : null;
    }

    public static HistoryUiRow Build(
        RecordingManifest manifest, int chatCount, DateTimeOffset now, SearchHit? hit = null, long audioBytes = 0)
    {
        ArgumentNullException.ThrowIfNull(manifest);
        var duration = HistoryUiFormat.DurationSeconds(manifest);
        return new HistoryUiRow(
            manifest.Id,
            manifest.EffectiveTitle,
            HistoryUiFormat.Duration(duration),
            HistoryUiFormat.RowMeta(manifest, now),
            Badges(manifest, chatCount),
            Snippet(hit),
            manifest.StartedAt,
            duration,
            manifest.HasAudio,
            manifest.HasAudio ? Math.Max(audioBytes, manifest.Audio?.Bytes ?? 0) : 0);
    }

    /// <summary>
    /// Splits rows into Today / Yesterday / Earlier, keeping the given order inside each
    /// group. With a sort other than newest first, one unlabelled group keeps that order.
    /// </summary>
    public static IReadOnlyList<HistoryUiGroup> Group(IEnumerable<HistoryUiRow> rows, DateTimeOffset now, bool byDate = true)
    {
        ArgumentNullException.ThrowIfNull(rows);
        var list = rows.ToList();
        if (list.Count == 0)
            return [];
        if (!byDate)
            return [new HistoryUiGroup(HistoryDateGroup.Earlier, "", list)];

        return list
            .GroupBy(r => GroupOf(r.StartedAt, now))
            .OrderBy(g => g.Key)
            .Select(g => new HistoryUiGroup(g.Key, GroupLabel(g.Key), g.ToList()))
            .ToList();
    }

    /// <summary>Selection header subtitle: <c>41 MB of audio</c> / <c>No audio</c>.</summary>
    public static string SelectionSubtitle(IEnumerable<HistoryUiRow> selected)
    {
        var bytes = selected.Sum(r => r.AudioBytes);
        return bytes > 0 ? Loc.F("History.Selection.AudioBytes", HistoryUiFormat.Bytes(bytes)) : Loc.T("History.Selection.NoAudio");
    }
}
