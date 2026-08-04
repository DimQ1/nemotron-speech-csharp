using LocalVoiceAssistant.Maui.Models;

namespace LocalVoiceAssistant.Maui.Core;

public enum PluginCapability
{
    Search,
    Browser,
    Utility,
    Notifications,
    System
}

public enum PluginPermission
{
    Network,
    Microphone,
    Notifications,
    FileSystem
}

public sealed record PluginManifest(
    string Id,
    string Name,
    string Version,
    string Description,
    IReadOnlySet<PluginCapability> Capabilities,
    IReadOnlySet<PluginPermission> Permissions);

public sealed record ToolInvocation(
    string PluginId,
    string Input,
    DateTimeOffset CreatedAt,
    IReadOnlyDictionary<string, string>? Arguments = null);

public sealed record PluginProgress(double? Percent, string Message);

public sealed record ToolResult(
    bool IsSuccess,
    string Message,
    string? ErrorCode = null,
    IReadOnlyList<SearchResult>? SearchResults = null,
    bool ShouldStop = false)
{
    public static ToolResult Success(string message, IReadOnlyList<SearchResult>? searchResults = null) =>
        new(true, message, SearchResults: searchResults);

    public static ToolResult Failure(string message, string errorCode) =>
        new(false, message, errorCode);

    public AssistantResponse ToAssistantResponse() => new(Message, ShouldStop, !IsSuccess);
}

public sealed record SearchResult(
    string Title,
    string Url,
    string Snippet,
    string? Source = null);

public sealed record ActivationDecision(
    bool IsActivated,
    double SemanticScore,
    bool HasLexicalMarker,
    string CleanText,
    string? Marker);

public sealed record RuntimeSnapshot(
    RuntimeState State,
    string StatusText,
    string? ActivePluginId,
    double? Progress,
    string? ProgressText);

public enum RuntimeState
{
    Idle,
    Listening,
    Processing,
    WaitingForQuestion,
    Executing,
    Error,
    Stopped
}

public sealed record ConversationEntry(
    Guid Id,
    string Role,
    string Text,
    DateTimeOffset Timestamp,
    string? PluginId = null,
    bool IsError = false);

public sealed record TaskCard(
    Guid Id,
    string Title,
    string Status,
    double Progress,
    DateTimeOffset UpdatedAt,
    string? Details = null);
