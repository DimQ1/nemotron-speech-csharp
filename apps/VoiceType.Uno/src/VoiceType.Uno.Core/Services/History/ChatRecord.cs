using System.Text.Json;
using System.Text.Json.Serialization;

namespace VoiceType.Uno.History;

/// <summary>Values of <see cref="ChatMessage.Role"/>.</summary>
public static class ChatRoles
{
    public const string User = "user";
    public const string Assistant = "assistant";
    public const string System = "system";
}

/// <summary>
/// <c>&lt;ChatsDir&gt;/&lt;chatId&gt;/chat.json</c>: a top-level chat over 1..N recordings.
/// <see cref="Messages"/> is the display log; <see cref="AgentSession"/> is the opaque
/// serialized Microsoft Agent Framework session, stored verbatim.
/// </summary>
public sealed class ChatRecord
{
    public const int CurrentSchema = 1;

    public int Schema { get; set; } = CurrentSchema;
    public string Id { get; set; } = "";
    public string Title { get; set; } = "";
    public List<ChatSource> Sources { get; set; } = [];
    public DateTimeOffset CreatedAt { get; set; }
    public DateTimeOffset UpdatedAt { get; set; }
    public List<ChatMessage> Messages { get; set; } = [];

    /// <summary>Opaque Agent Framework session blob; never interpreted here.</summary>
    public JsonElement? AgentSession { get; set; }

    [JsonExtensionData]
    public Dictionary<string, JsonElement>? ExtensionData { get; set; }

    public bool Includes(string recordingId) => Sources.Exists(s => s.RecordingId == recordingId);
}

public sealed class ChatSource
{
    public ChatSource() { }

    public ChatSource(string recordingId, string versionId, DateTimeOffset addedAt)
    {
        RecordingId = recordingId;
        VersionId = versionId;
        AddedAt = addedAt;
    }

    public string RecordingId { get; set; } = "";

    /// <summary>Transcript version the chat answers from (does not follow the current version).</summary>
    public string VersionId { get; set; } = "";
    public DateTimeOffset AddedAt { get; set; }

    [JsonExtensionData]
    public Dictionary<string, JsonElement>? ExtensionData { get; set; }
}

public sealed class ChatMessage
{
    public string Id { get; set; } = "";

    /// <summary>One of <see cref="ChatRoles"/>.</summary>
    public string Role { get; set; } = ChatRoles.User;
    public string Text { get; set; } = "";
    public DateTimeOffset CreatedAt { get; set; }
    public List<ChatCitation> Citations { get; set; } = [];

    /// <summary>The answer was stopped (Stop pressed or the engine failed): the text is partial.</summary>
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingDefault)]
    public bool Incomplete { get; set; }

    [JsonExtensionData]
    public Dictionary<string, JsonElement>? ExtensionData { get; set; }
}

/// <summary>A reference from an answer into a recording, rendered as <c>[Title mm:ss]</c>.</summary>
public sealed class ChatCitation
{
    public string RecordingId { get; set; } = "";
    public string? VersionId { get; set; }
    public double StartSec { get; set; }
    public double? EndSec { get; set; }
    public string? Quote { get; set; }

    [JsonExtensionData]
    public Dictionary<string, JsonElement>? ExtensionData { get; set; }
}
