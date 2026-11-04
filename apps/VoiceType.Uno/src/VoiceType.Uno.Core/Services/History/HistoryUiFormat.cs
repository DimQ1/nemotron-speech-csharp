using System.Globalization;
using System.Text.Json;
using VoiceType.Uno.Localization;
using VoiceType.Uno.Services;

namespace VoiceType.Uno.History;

/// <summary>
/// Display strings of the History screens (list rows, recording header, version cards).
/// Pure (texts from <see cref="Loc"/>, dates and numbers in <see cref="Loc.Culture"/>), so the screens are unit-tested.
/// </summary>
public static class HistoryUiFormat
{
    /// <summary><c>0:48</c>, <c>12:34</c>, <c>1:24:05</c> — the duration pill.</summary>
    public static string Duration(double seconds)
    {
        var total = (long)Math.Round(Math.Max(0, seconds), MidpointRounding.AwayFromZero);
        return total >= 3600
            ? string.Create(CultureInfo.InvariantCulture, $"{total / 3600}:{total / 60 % 60:00}:{total % 60:00}")
            : string.Create(CultureInfo.InvariantCulture, $"{total / 60}:{total % 60:00}");
    }

    /// <summary>Segment time column: <c>00:41</c>, <c>1:02:05</c>.</summary>
    public static string SegmentTime(double seconds) => TranscriptExport.FormatShort(seconds);

    /// <summary>Length of a recording: audio duration, else wall time, else 0.</summary>
    public static double DurationSeconds(RecordingManifest manifest)
    {
        ArgumentNullException.ThrowIfNull(manifest);
        if (manifest.Audio is { DurationSec: > 0 } audio)
            return audio.DurationSec;
        if (manifest.EndedAt is { } ended && ended > manifest.StartedAt)
            return (ended - manifest.StartedAt).TotalSeconds;
        return 0;
    }

    /// <summary>
    /// Short model name for rows and pickers: first word of the catalog title plus the
    /// variant (<c>Parakeet FP32</c>, <c>Nemotron INT4</c>); the folder when there is no title.
    /// </summary>
    public static string ShortModel(ModelInfo? model)
    {
        if (model is null)
            return "";
        var family = FirstWord(model.Title) ?? FirstWord(model.Family) ?? model.Folder ?? "";
        var variant = !string.IsNullOrWhiteSpace(model.Variant) ? model.Variant!.Trim()
            : !string.IsNullOrWhiteSpace(model.Precision) ? model.Precision!.Trim().ToUpperInvariant()
            : null;
        if (string.IsNullOrWhiteSpace(model.Title) && string.IsNullOrWhiteSpace(model.Family))
            return model.Folder ?? "";
        return variant is null || family.Contains(variant, StringComparison.OrdinalIgnoreCase)
            ? family
            : family + " " + variant;
    }

    /// <summary>Full model title for version cards; folder as a fallback.</summary>
    public static string LongModel(ModelInfo? model)
    {
        if (model is null)
            return Loc.T("History.Model.Unknown");
        // Versions saved before the default model had a title carry its folder name as the title.
        var folder = model.Folder;
        if (folder is { Length: > 0 } && (string.IsNullOrWhiteSpace(model.Title) || string.Equals(model.Title, folder, StringComparison.OrdinalIgnoreCase)))
            return KnownModels.Describe(folder);
        return model.Title is { Length: > 0 } title ? title : Loc.T("History.Model.Unknown");
    }

    /// <summary>Upper-case language code of a version (detected, else requested unless auto); empty when unknown.</summary>
    public static string LanguageCode(LanguageInfo? language)
    {
        var code = Usable(language?.Detected) ?? Usable(language?.Requested);
        return code?.ToUpperInvariant() ?? "";
    }

    /// <summary><c>Auto → EN</c>, <c>RU</c>, <c>Auto</c>.</summary>
    public static string LanguageDetail(LanguageInfo? language)
    {
        var requested = language?.Requested;
        var detected = Usable(language?.Detected);
        var isAuto = string.IsNullOrWhiteSpace(requested) || string.Equals(requested, "auto", StringComparison.OrdinalIgnoreCase);
        if (isAuto)
            return detected is null ? Loc.T("History.Language.Auto") : Loc.F("History.Language.AutoDetected", detected.ToUpperInvariant());
        return requested!.ToUpperInvariant();
    }

    /// <summary>
    /// Second line of a row: <c>14:02 · 12:34 · Parakeet FP32 · EN</c> for today and
    /// yesterday, <c>Sep 28 · 1:24:05 · …</c> for older recordings.
    /// </summary>
    public static string RowMeta(RecordingManifest manifest, DateTimeOffset now)
    {
        ArgumentNullException.ThrowIfNull(manifest);
        var started = manifest.StartedAt.ToOffset(now.Offset);
        var group = HistoryUiRows.GroupOf(manifest.StartedAt, now);
        var when = group == HistoryDateGroup.Earlier
            ? Loc.F(started.Year == now.Year ? "History.Row.DateThisYear" : "History.Row.DateOtherYear", started)
            : Loc.F("History.Row.Time", started);
        return Join(when, Duration(DurationSeconds(manifest)), ShortModel(manifest.Current?.Model),
            LanguageCode(manifest.Current?.Language));
    }

    /// <summary>Recording header subtitle: <c>Mon 12 Oct, 14:02 · 12:34 · EN</c>.</summary>
    public static string HeaderSubtitle(RecordingManifest manifest, DateTimeOffset now)
    {
        ArgumentNullException.ThrowIfNull(manifest);
        var started = manifest.StartedAt.ToOffset(now.Offset);
        var date = Loc.F(started.Year == now.Year ? "History.Header.DateThisYear" : "History.Header.DateOtherYear", started);
        return Join(date, Duration(DurationSeconds(manifest)), LanguageCode(manifest.Current?.Language));
    }

    /// <summary>Version picker and diff pickers: <c>v2 · Parakeet FP32</c>, <c>v3 · Edited</c>, <c>v1 · Live</c>.</summary>
    public static string VersionShort(TranscriptVersion version)
    {
        ArgumentNullException.ThrowIfNull(version);
        if (version.Kind == VersionKinds.Edited)
            return Loc.F("History.Version.ShortEdited", version.Id);
        var model = ShortModel(version.Model);
        if (model.Length > 0)
            return version.Id + " · " + model;
        return version.Kind == VersionKinds.Live ? Loc.F("History.Version.ShortLive", version.Id) : version.Id;
    }

    /// <summary>Narrow diff pickers: <c>v1 · Live</c>, <c>v2 · FP32</c>, <c>v3 · Edited</c>.</summary>
    public static string VersionTiny(TranscriptVersion version)
    {
        ArgumentNullException.ThrowIfNull(version);
        var detail = version.Kind switch
        {
            VersionKinds.Live => Loc.T("History.Version.Live"),
            VersionKinds.Edited => Loc.T("History.Version.Edited"),
            _ => !string.IsNullOrWhiteSpace(version.Model?.Variant) ? version.Model!.Variant!.Trim()
                : !string.IsNullOrWhiteSpace(version.Model?.Precision) ? version.Model!.Precision!.Trim().ToUpperInvariant()
                : Loc.T("History.Version.Rerun"),
        };
        return version.Id + " · " + detail;
    }

    /// <summary>Version card title: <c>v2 · Parakeet TDT 0.6B v3 — full precision (FP32)</c>.</summary>
    public static string VersionTitle(TranscriptVersion version)
    {
        ArgumentNullException.ThrowIfNull(version);
        return version.Id + " · " + LongModel(version.Model);
    }

    /// <summary><c>Recorded live</c>, <c>Re-recognized</c>, <c>Edited from v2</c>.</summary>
    public static string VersionKind(TranscriptVersion version)
    {
        ArgumentNullException.ThrowIfNull(version);
        return version.Kind switch
        {
            VersionKinds.Live => Loc.T(version.Edited ? "History.Version.KindLiveTyped" : "History.Version.KindLive"),
            VersionKinds.Rerecognized => Loc.T("History.Version.KindRerecognized"),
            VersionKinds.Edited => version.Parent is { Length: > 0 } parent
                ? Loc.F("History.Version.KindEditedFrom", parent)
                : Loc.T("History.Version.KindEdited"),
            _ => version.Kind,
        };
    }

    /// <summary><c>on</c> / <c>off</c> / empty from <c>settings.useVad</c>.</summary>
    public static string Vad(TranscriptVersion version)
    {
        ArgumentNullException.ThrowIfNull(version);
        if (version.Settings is null || !version.Settings.TryGetValue("useVad", out var value))
            return "";
        return value.ValueKind switch
        {
            JsonValueKind.True => Loc.T("History.Vad.On"),
            JsonValueKind.False => Loc.T("History.Vad.Off"),
            _ => "",
        };
    }

    /// <summary><c>12 Oct 15:10</c> (year added when not the current one).</summary>
    public static string ShortDateTime(DateTimeOffset value, DateTimeOffset now)
    {
        var local = value.ToOffset(now.Offset);
        return Loc.F(local.Year == now.Year ? "History.Date.ShortThisYear" : "History.Date.ShortOtherYear", local);
    }

    /// <summary><c>980 KB</c>, <c>41 MB</c>, <c>1.2 GB</c> (binary units, like the storage settings).</summary>
    public static string Bytes(long bytes)
    {
        const double kb = 1024, mb = kb * 1024, gb = mb * 1024;
        if (bytes <= 0)
            return Loc.F("History.Size.MB", 0.ToString(Loc.Culture));
        if (bytes >= gb)
            return Loc.F("History.Size.GB", (bytes / gb).ToString(bytes >= 10 * gb ? "0" : "0.#", Loc.Culture));
        if (bytes >= mb)
            return Loc.F("History.Size.MB", (bytes / mb).ToString(bytes >= 10 * mb ? "0" : "0.#", Loc.Culture));
        return Loc.F("History.Size.KB", Math.Max(1, Math.Round(bytes / kb)).ToString(Loc.Culture));
    }

    /// <summary><c>0.5 GB</c>, <c>2 GB</c>: a size given in gigabytes (the storage cap choices).</summary>
    public static string Gigabytes(double gb) => Loc.F("History.Size.GB", gb.ToString("0.#", Loc.Culture));

    /// <summary>
    /// <c>1 recording</c> / <c>24 recordings</c>: an ENGLISH-only plural of an English noun, kept for callers that have not
    /// moved to <see cref="Loc.Plural"/> yet. New interface texts use <see cref="Loc.Plural"/> with a key.
    /// </summary>
    public static string Count(int n, string singular, string? plural = null) =>
        n.ToString(CultureInfo.InvariantCulture) + " " + (n == 1 ? singular : plural ?? singular + "s");

    /// <summary>History header subtitle: <c>24 recordings · 1.2 GB</c>.</summary>
    public static string ListSubtitle(int recordings, long totalBytes) =>
        recordings == 0 ? Loc.T("History.List.NoRecordingsYet") : Loc.Plural("History.List.Subtitle", recordings, Bytes(totalBytes));

    /// <summary>Joins the non-empty parts with <c> · </c>.</summary>
    public static string Join(params string?[] parts) =>
        string.Join(" · ", parts.Where(p => !string.IsNullOrWhiteSpace(p)));

    private static string? Usable(string? code) =>
        string.IsNullOrWhiteSpace(code) || string.Equals(code, "auto", StringComparison.OrdinalIgnoreCase) ? null : code.Trim();

    private static string? FirstWord(string? text)
    {
        var words = TextTokens.SplitWords(text);
        return words.Count == 0 ? null : words[0];
    }
}
