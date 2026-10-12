namespace VoiceType.Uno.History;

/// <summary>Events of one chat turn, in order: Status* → Delta* → Completed | Failed.</summary>
public abstract record ChatEvent
{
    /// <summary>"Reading 3 recordings…", "Waiting for live translation…", "Summarising part 2 of 5…".</summary>
    public sealed record Status(string Text) : ChatEvent;

    /// <summary>The next piece of the answer text (append).</summary>
    public sealed record Delta(string Text) : ChatEvent;

    /// <summary>The finished assistant message, already saved to the chat, with parsed citations.</summary>
    public sealed record Completed(ChatMessage Message) : ChatEvent;

    public sealed record Failed(string Error) : ChatEvent;
}

/// <summary>A one-tap question shown above the composer.</summary>
public sealed record QuickPrompt(string Id, string Label, string Prompt);

/// <summary>
/// Chat over one or more recordings (Gemma on the device). Messages and the Microsoft Agent Framework
/// session are persisted through <see cref="ChatStore"/>; sources can be added and removed at any time.
/// </summary>
public interface IChatService
{
    /// <summary>The model is installed (otherwise the UI offers the download instead).</summary>
    bool IsAvailable { get; }

    IReadOnlyList<QuickPrompt> QuickPrompts { get; }

    /// <summary>Creates a chat over the given recordings (each at its current version) and returns it.</summary>
    ChatRecord CreateChat(IEnumerable<string> recordingIds, string? title = null);

    /// <summary>
    /// Sends a user message and streams the answer. The user message is saved first; the assistant message
    /// is saved when <see cref="ChatEvent.Completed"/> is produced. Cancelling keeps the partial text as a
    /// saved, marked-incomplete assistant message.
    /// </summary>
    IAsyncEnumerable<ChatEvent> SendAsync(string chatId, string userText, CancellationToken cancellationToken = default);

    /// <summary>Adds a recording to the chat; the next answer reads it too (a source-change event is stored in the thread).</summary>
    ChatRecord AddSource(string chatId, string recordingId);

    ChatRecord RemoveSource(string chatId, string recordingId);

    /// <summary>Frees the live engine conversation of this chat (closed page / eviction).</summary>
    void CloseChat(string chatId);
}
