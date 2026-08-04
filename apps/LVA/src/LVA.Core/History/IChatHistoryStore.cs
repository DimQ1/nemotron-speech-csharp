namespace LVA.Core.History;

/// <summary>Entry kind in the interaction history (TZ §17).</summary>
public enum HistoryEntryKind
{
    UserMessage,
    AssistantMessage,
    TaskEvent,
    SystemEvent
}

/// <summary>A single history entry.</summary>
public sealed record HistoryEntry(
    long Id,
    HistoryEntryKind Kind,
    string Text,
    string Channel,            // "voice" | "keyboard" | "popup" | "system"
    string? PluginId,
    DateTimeOffset Timestamp,
    string? Metadata = null);

public sealed record HistoryFilter(
    DateOnly? From = null,
    DateOnly? To = null,
    string? Channel = null,
    string? PluginId = null,
    int Limit = 100);

public enum HistoryFormat { Json, Markdown }

/// <summary>
/// Local interaction history with full-text search (TZ §17.2).
/// Only local storage; background segments are never stored.
/// </summary>
public interface IChatHistoryStore : IDisposable
{
    Task AppendAsync(HistoryEntry entry, CancellationToken ct);

    Task<IReadOnlyList<HistoryEntry>> SearchAsync(string query, HistoryFilter filter, CancellationToken ct);

    Task<IReadOnlyList<HistoryEntry>> GetSessionAsync(DateOnly day, CancellationToken ct);

    Task ExportAsync(string path, HistoryFormat format, CancellationToken ct);

    Task ClearAsync(TimeSpan? olderThan, CancellationToken ct);
}
