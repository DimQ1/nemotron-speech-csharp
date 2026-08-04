namespace LVA.Core.Events;

/// <summary>Lifecycle status of an assistant task executed by a plugin.</summary>
public enum AssistantTaskStatus
{
    Started,
    InProgress,
    Completed,
    Failed,
    Cancelled
}

/// <summary>A unit of work dispatched to a plugin.</summary>
public sealed record AssistantTask(
    Guid Id,
    string PluginId,
    string Description,
    AssistantTaskStatus Status,
    DateTimeOffset StartedAt);

/// <summary>Intermediate progress reported by a plugin.</summary>
public sealed record TaskProgressEvent(Guid TaskId, int Percent, string Message, DateTimeOffset Timestamp);

/// <summary>Terminal result of a plugin execution.</summary>
public sealed record ToolResultEvent(
    Guid TaskId,
    bool Success,
    string? ResultMarkdown,
    string? Error,
    IReadOnlyList<string> Sources,
    DateTimeOffset Timestamp);
