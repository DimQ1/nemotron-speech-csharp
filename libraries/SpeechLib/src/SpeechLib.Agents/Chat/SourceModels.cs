namespace SpeechLib.Agents;

/// <summary>
/// One recording the chat can read, as the agent layer sees it (no dependency on the app's stores).
/// </summary>
/// <param name="Alias">Stable short name <c>S1</c>, <c>S2</c>…; never reused inside a chat.</param>
/// <param name="Active">False once the user removed the source: it is listed as "do not cite" and not retrieved.</param>
/// <param name="LoadLines">Segments at the chat's version of the recording; loaded lazily (inactive sources never load).</param>
public sealed record ChatSourceDocument(
    string Alias,
    string RecordingId,
    string VersionId,
    string Title,
    DateTimeOffset? Date,
    double DurationSec,
    bool Active,
    Func<IReadOnlyList<TranscriptLine>> LoadLines);

/// <summary>Where the agent layer asks for the chat's sources before every run (they may have changed since the last turn).</summary>
public interface IChatSourceCatalog
{
    IReadOnlyList<ChatSourceDocument> GetSources();
}

/// <summary>Per-chunk and per-section summaries, cached by the caller so every chat reuses them.</summary>
public interface ISummaryCache
{
    string? Get(string recordingId, string versionId, string key);

    void Set(string recordingId, string versionId, string key, string summary);
}

public sealed class InMemorySummaryCache : ISummaryCache
{
    private readonly Dictionary<string, string> _map = new(StringComparer.Ordinal);

    public int Count
    {
        get { lock (_map) return _map.Count; }
    }

    public string? Get(string recordingId, string versionId, string key)
    {
        lock (_map)
            return _map.GetValueOrDefault(recordingId + "|" + versionId + "|" + key);
    }

    public void Set(string recordingId, string versionId, string key, string summary)
    {
        lock (_map)
            _map[recordingId + "|" + versionId + "|" + key] = summary;
    }
}

/// <summary>
/// Source aliases of one chat. Aliases are handed out in order and are never reused; removing a source keeps
/// its alias (so old answers stay valid), re-adding the same recording gets the same alias back.
/// Rebuilt from the persisted event log, so it never depends on the Agent Framework session format.
/// </summary>
public sealed class SourceAliasRegistry
{
    public sealed record Entry(string Alias, string RecordingId, bool Active);

    private readonly List<Entry> _entries = [];
    private int _next = 1;

    public IReadOnlyList<Entry> Entries => _entries;

    public IEnumerable<Entry> ActiveEntries => _entries.Where(e => e.Active);

    public string? AliasOf(string recordingId) => _entries.Find(e => e.RecordingId == recordingId)?.Alias;

    public Entry? Find(string alias) =>
        _entries.Find(e => string.Equals(e.Alias, alias, StringComparison.OrdinalIgnoreCase));

    /// <summary>Adds a source (or re-activates a removed one) and returns its alias.</summary>
    public string Add(string recordingId)
    {
        var index = _entries.FindIndex(e => e.RecordingId == recordingId);
        if (index >= 0)
        {
            _entries[index] = _entries[index] with { Active = true };
            return _entries[index].Alias;
        }
        var alias = "S" + _next.ToString(System.Globalization.CultureInfo.InvariantCulture);
        _next++;
        _entries.Add(new Entry(alias, recordingId, true));
        return alias;
    }

    public void Remove(string recordingId)
    {
        var index = _entries.FindIndex(e => e.RecordingId == recordingId);
        if (index >= 0)
            _entries[index] = _entries[index] with { Active = false };
    }

    /// <summary>Replays one persisted event (<c>added</c> / <c>removed</c>) with its explicit alias.</summary>
    public void Apply(string kind, string alias, string recordingId)
    {
        if (alias.Length > 1 && alias[0] == 'S' && int.TryParse(alias.AsSpan(1), out var n) && n >= _next)
            _next = n + 1;

        var index = _entries.FindIndex(e => e.RecordingId == recordingId);
        var active = kind == SourceEventKinds.Added;
        if (index >= 0)
            _entries[index] = _entries[index] with { Active = active };
        else
            _entries.Add(new Entry(alias, recordingId, active));
    }
}

public static class SourceEventKinds
{
    public const string Added = "added";
    public const string Removed = "removed";
}
