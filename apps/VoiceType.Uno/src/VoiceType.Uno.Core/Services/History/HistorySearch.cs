namespace VoiceType.Uno.History;

[Flags]
public enum SearchField
{
    None = 0,
    Title = 1,
    Transcript = 2,
    Translation = 4,
    Chat = 8,
}

/// <summary>A segment of the current transcript (for hit snippets / seeking).</summary>
public sealed record SearchSegment(int Index, double Start, string Text, string Normalized);

/// <summary>
/// Everything search and filters need about one recording, with normalised text
/// precomputed. Build with <see cref="HistorySearch.CreateDocument"/>; cache it as the
/// history index.
/// </summary>
public sealed class HistorySearchDocument
{
    public required string RecordingId { get; init; }
    public required DateTimeOffset StartedAt { get; init; }
    public required string Title { get; init; }
    public string? ModelFolder { get; init; }
    public string? ModelRepoId { get; init; }
    public string? ModelTitle { get; init; }
    public IReadOnlyList<string> Languages { get; init; } = [];
    public IReadOnlyList<string> TranslationLanguages { get; init; } = [];
    public bool HasAudio { get; init; }
    public int ChatCount { get; init; }
    public IReadOnlyList<SearchSegment> Segments { get; init; } = [];

    internal string NormalizedTitle { get; init; } = "";
    internal string NormalizedTranscript { get; init; } = "";
    internal string NormalizedTranslations { get; init; } = "";
    internal string NormalizedChats { get; init; } = "";

    public bool HasTranslation => TranslationLanguages.Count > 0;
    public bool HasChat => ChatCount > 0;
}

/// <summary>Filters; null means "any".</summary>
public sealed record HistoryFilter
{
    public static readonly HistoryFilter None = new();

    /// <summary>Inclusive lower bound on the start time.</summary>
    public DateTimeOffset? From { get; init; }

    /// <summary>Exclusive upper bound on the start time.</summary>
    public DateTimeOffset? To { get; init; }

    /// <summary>Model folder, catalog repo id or title of the current version.</summary>
    public string? Model { get; init; }

    /// <summary>Requested or detected recognition language.</summary>
    public string? Language { get; init; }
    public bool? HasTranslation { get; init; }
    public bool? HasChat { get; init; }
    public bool? HasAudio { get; init; }
}

public sealed record SearchHit(HistorySearchDocument Document, SearchField MatchedIn, SearchSegment? Segment);

/// <summary>
/// Substring search over normalised text (case-folded, diacritics stripped, ё→е):
/// every query word must occur in the title, current transcript, translations or chat
/// messages. Results are newest first.
/// </summary>
public static class HistorySearch
{
    public static HistorySearchDocument CreateDocument(
        RecordingManifest manifest,
        TranscriptDocument? currentTranscript,
        IEnumerable<TranslationDocument>? translations = null,
        IEnumerable<ChatRecord>? chats = null)
    {
        ArgumentNullException.ThrowIfNull(manifest);

        var translationList = translations?.ToList() ?? [];
        var chatList = chats?.ToList() ?? [];
        var current = manifest.Current;
        var languages = new[] { current?.Language?.Requested, current?.Language?.Detected }
            .Where(l => !string.IsNullOrWhiteSpace(l) && l != "auto")
            .Select(l => l!)
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToList();
        var segments = currentTranscript?.Segments
            .Select(s => new SearchSegment(s.Index, s.Start, s.Text, TextNormalizer.Normalize(s.Text)))
            .ToList() ?? [];

        return new HistorySearchDocument
        {
            RecordingId = manifest.Id,
            StartedAt = manifest.StartedAt,
            Title = manifest.EffectiveTitle,
            ModelFolder = current?.Model?.Folder,
            ModelRepoId = current?.Model?.CatalogRepoId,
            ModelTitle = current?.Model?.Title,
            Languages = languages,
            TranslationLanguages = manifest.Translations
                .Where(t => t.Status != TranslationStatuses.Failed)
                .Select(t => t.TargetLanguage)
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .ToList(),
            HasAudio = manifest.HasAudio,
            ChatCount = chatList.Count,
            Segments = segments,
            NormalizedTitle = TextNormalizer.Normalize(manifest.EffectiveTitle),
            NormalizedTranscript = currentTranscript is null
                ? TextNormalizer.Normalize(current?.Excerpt)
                : TextNormalizer.Normalize(currentTranscript.Text.Length > 0
                    ? currentTranscript.Text
                    : TranscriptDocument.JoinSegments(currentTranscript.Segments)),
            NormalizedTranslations = TextNormalizer.Normalize(string.Join("\n",
                translationList.SelectMany(t => t.Segments).Select(s => s.Text))),
            NormalizedChats = TextNormalizer.Normalize(string.Join("\n",
                chatList.SelectMany(c => c.Messages.Select(m => m.Text).Prepend(c.Title)))),
        };
    }

    /// <summary>Loads every recording (current transcript, translations, chats) into search documents.</summary>
    public static IReadOnlyList<HistorySearchDocument> BuildIndex(RecordingStore store, ChatStore? chatStore = null)
    {
        ArgumentNullException.ThrowIfNull(store);
        var chats = chatStore?.LoadAll().Chats ?? [];
        var documents = new List<HistorySearchDocument>();
        foreach (var entry in store.LoadAll().Recordings)
        {
            TranscriptDocument? transcript = null;
            var translations = new List<TranslationDocument>();
            try
            {
                if (entry.Manifest.CurrentVersion is { } current)
                    transcript = store.LoadTranscript(entry.Id, current);
                foreach (var t in entry.Manifest.Translations)
                    translations.Add(store.LoadTranslation(entry.Id, t.Id));
            }
            catch (Exception ex) when (ex is IOException or System.Text.Json.JsonException or KeyNotFoundException)
            {
                // Index what we have; a broken file must not hide the recording.
            }
            documents.Add(CreateDocument(entry.Manifest, transcript, translations, chats.Where(c => c.Includes(entry.Id))));
        }
        return documents;
    }

    public static IReadOnlyList<SearchHit> Search(
        IEnumerable<HistorySearchDocument> documents, string? query, HistoryFilter? filter = null)
    {
        ArgumentNullException.ThrowIfNull(documents);
        filter ??= HistoryFilter.None;
        var terms = TextTokens.SplitWords(TextNormalizer.Normalize(query));

        var hits = new List<SearchHit>();
        foreach (var doc in documents)
        {
            if (!Matches(doc, filter))
                continue;
            if (terms.Count == 0)
            {
                hits.Add(new SearchHit(doc, SearchField.None, null));
                continue;
            }

            var matchedIn = SearchField.None;
            var allFound = true;
            foreach (var term in terms)
            {
                var fields = FieldsContaining(doc, term);
                if (fields == SearchField.None)
                {
                    allFound = false;
                    break;
                }
                matchedIn |= fields;
            }
            if (!allFound)
                continue;

            var segment = doc.Segments.FirstOrDefault(s => s.Normalized.Contains(terms[0], StringComparison.Ordinal));
            hits.Add(new SearchHit(doc, matchedIn, segment));
        }

        hits.Sort((a, b) => b.Document.StartedAt.CompareTo(a.Document.StartedAt));
        return hits;
    }

    private static SearchField FieldsContaining(HistorySearchDocument doc, string term)
    {
        var fields = SearchField.None;
        if (doc.NormalizedTitle.Contains(term, StringComparison.Ordinal)) fields |= SearchField.Title;
        if (doc.NormalizedTranscript.Contains(term, StringComparison.Ordinal)) fields |= SearchField.Transcript;
        if (doc.NormalizedTranslations.Contains(term, StringComparison.Ordinal)) fields |= SearchField.Translation;
        if (doc.NormalizedChats.Contains(term, StringComparison.Ordinal)) fields |= SearchField.Chat;
        return fields;
    }

    private static bool Matches(HistorySearchDocument doc, HistoryFilter f)
    {
        if (f.From is { } from && doc.StartedAt < from) return false;
        if (f.To is { } to && doc.StartedAt >= to) return false;
        if (f.HasAudio is { } audio && doc.HasAudio != audio) return false;
        if (f.HasChat is { } chat && doc.HasChat != chat) return false;
        if (f.HasTranslation is { } tr && doc.HasTranslation != tr) return false;
        if (!string.IsNullOrWhiteSpace(f.Language)
            && !doc.Languages.Contains(f.Language, StringComparer.OrdinalIgnoreCase))
            return false;
        if (!string.IsNullOrWhiteSpace(f.Model)
            && !string.Equals(doc.ModelFolder, f.Model, StringComparison.OrdinalIgnoreCase)
            && !string.Equals(doc.ModelRepoId, f.Model, StringComparison.OrdinalIgnoreCase)
            && !string.Equals(doc.ModelTitle, f.Model, StringComparison.OrdinalIgnoreCase))
            return false;
        return true;
    }
}
