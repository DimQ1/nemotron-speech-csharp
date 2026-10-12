using System.Text.Json;
using System.Text.Json.Serialization;
using SpeechLib.Agents;

namespace VoiceType.Uno.History;

/// <summary>
/// Source-change events kept in the chat's display log (<see cref="ChatRecord.Messages"/>, role
/// <see cref="ChatRoles.System"/>). They are the durable record of which alias belongs to which recording, so
/// aliases (S1, S2…) stay stable and are never reused no matter what happens to the Agent Framework session.
/// </summary>
public static class ChatSourceEvents
{
    public const string EventKey = "event";
    public const string AddedKind = "sourceAdded";
    public const string RemovedKind = "sourceRemoved";
    public const string VersionChangedKind = "sourceVersionChanged";

    /// <summary>A system message the UI can show as a line in the thread ("Added “Weekly sync” as S2").</summary>
    public static ChatMessage Create(string kind, string alias, string recordingId, string versionId, string title, DateTimeOffset now)
    {
        var text = kind switch
        {
            AddedKind => $"Added “{title}” as {alias}",
            RemovedKind => $"Removed “{title}” ({alias})",
            _ => $"{alias} “{title}” now reads transcript {versionId}",
        };
        return new ChatMessage
        {
            Role = ChatRoles.System,
            Text = text,
            CreatedAt = now,
            ExtensionData = new Dictionary<string, JsonElement>
            {
                [EventKey] = JsonSerializer.SerializeToElement(kind, ChatJsonContext.Default.String),
                ["alias"] = JsonSerializer.SerializeToElement(alias, ChatJsonContext.Default.String),
                ["recordingId"] = JsonSerializer.SerializeToElement(recordingId, ChatJsonContext.Default.String),
                ["versionId"] = JsonSerializer.SerializeToElement(versionId, ChatJsonContext.Default.String),
            },
        };
    }

    public static bool TryRead(ChatMessage message, out string kind, out string alias, out string recordingId)
    {
        kind = alias = recordingId = "";
        if (message.Role != ChatRoles.System || message.ExtensionData is not { } data)
            return false;
        if (!TryString(data, EventKey, out kind) || !TryString(data, "alias", out alias) || !TryString(data, "recordingId", out recordingId))
            return false;
        return true;
    }

    private static bool TryString(Dictionary<string, JsonElement> data, string key, out string value)
    {
        value = "";
        if (!data.TryGetValue(key, out var element) || element.ValueKind != JsonValueKind.String)
            return false;
        value = element.GetString() ?? "";
        return value.Length > 0;
    }

    /// <summary>
    /// Replays the log into the alias registry, then reconciles with <see cref="ChatRecord.Sources"/>: a source
    /// without an alias gets the next one (returned in <paramref name="unlogged"/> so the caller can persist it);
    /// an alias whose recording left the chat (for example the recording was deleted) becomes removed.
    /// </summary>
    public static SourceAliasRegistry BuildRegistry(ChatRecord chat, out List<string> unlogged)
    {
        var registry = new SourceAliasRegistry();
        foreach (var message in chat.Messages)
        {
            if (!TryRead(message, out var kind, out var alias, out var recordingId))
                continue;
            if (kind == AddedKind)
                registry.Apply(SourceEventKinds.Added, alias, recordingId);
            else if (kind == RemovedKind)
                registry.Apply(SourceEventKinds.Removed, alias, recordingId);
        }

        unlogged = [];
        foreach (var source in chat.Sources)
        {
            var existing = registry.Entries.FirstOrDefault(e => e.RecordingId == source.RecordingId);
            if (existing is null || !existing.Active)
            {
                registry.Add(source.RecordingId);
                unlogged.Add(source.RecordingId);
            }
        }
        foreach (var entry in registry.Entries.Where(e => e.Active).ToList())
        {
            if (!chat.Sources.Exists(s => s.RecordingId == entry.RecordingId))
                registry.Remove(entry.RecordingId);
        }
        return registry;
    }
}

/// <summary>Serialization metadata for the small JSON values the chat service writes itself.</summary>
[JsonSerializable(typeof(string))]
[JsonSerializable(typeof(Dictionary<string, string>))]
internal partial class ChatJsonContext : JsonSerializerContext;
