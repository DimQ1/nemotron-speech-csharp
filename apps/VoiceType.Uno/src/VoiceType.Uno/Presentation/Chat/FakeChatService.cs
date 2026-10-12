#if DEBUG
using System.Runtime.CompilerServices;
using System.Text.Json;
using VoiceType.Uno.History;

namespace VoiceType.Uno.Presentation.Chat;

/// <summary>
/// DEBUG-only stand-in for <see cref="IChatService"/> so the chat screens run before (or without) the
/// real service: it persists through <see cref="ChatStore"/> like the real one and streams a scripted
/// answer that cites the first sources. <c>VOICETYPE_FAKE_CHAT=missing</c> reports the model as not
/// installed; a question containing "error" fails; one containing "long" streams for a while.
/// </summary>
internal sealed class FakeChatService : IChatService
{
    private readonly ChatStore _chats;
    private readonly RecordingStore _recordings;

    public FakeChatService(ChatStore chats, RecordingStore recordings)
    {
        _chats = chats;
        _recordings = recordings;
    }

    public bool IsAvailable =>
        !string.Equals(Environment.GetEnvironmentVariable("VOICETYPE_FAKE_CHAT"), "missing", StringComparison.OrdinalIgnoreCase);

    public IReadOnlyList<QuickPrompt> QuickPrompts { get; } =
    [
        new("summary", "Summary", "Summarise the recording in a few sentences."),
        new("actions", "Action items", "List the action items with who owns them."),
        new("decisions", "Key decisions", "What was decided?"),
        new("questions", "Open questions", "Which questions stayed open?"),
        new("compare", "Compare the recordings", "Compare the recordings: what changed between them?"),
    ];

    public ChatRecord CreateChat(IEnumerable<string> recordingIds, string? title = null)
    {
        var sources = new List<(string, string)>();
        foreach (var id in recordingIds)
        {
            var version = _recordings.Get(id)?.Manifest.CurrentVersion
                          ?? throw new KeyNotFoundException($"Recording {id} not found.");
            sources.Add((id, version));
        }
        return _chats.Create(title, sources);
    }

    public async IAsyncEnumerable<ChatEvent> SendAsync(
        string chatId, string userText, [EnumeratorCancellation] CancellationToken cancellationToken = default)
    {
        var chat = _chats.AppendMessage(chatId, new ChatMessage { Role = ChatRoles.User, Text = userText });
        yield return new ChatEvent.Status($"Reading {HistoryUiFormat.Count(chat.Sources.Count, "recording")}…");
        await Task.Delay(700, CancellationToken.None);

        if (userText.Contains("error", StringComparison.OrdinalIgnoreCase))
        {
            yield return new ChatEvent.Failed("The model stopped unexpectedly (fake error).");
            yield break;
        }

        var answer = userText.Contains("long", StringComparison.OrdinalIgnoreCase)
            ? string.Join("\n\n", Enumerable.Range(1, 12).Select(i =>
                $"**Point {i}.** The team discussed the release plan, the supplier schedule and what could slip, and agreed to review it again next week."))
            : "**Summary.** The release is on track for the 24th, but two risks could move it:\n\n"
              + "- the supplier delivery is promised for the 20th and is not confirmed yet\n"
              + "- the *security review* still has to finish\n\n"
              + "Maria will confirm the delivery date by Friday.";

        var builder = new System.Text.StringBuilder();
        var cancelled = false;
        foreach (var word in answer.Split(' '))
        {
            if (cancellationToken.IsCancellationRequested)
            {
                cancelled = true;
                break;
            }
            builder.Append(word).Append(' ');
            yield return new ChatEvent.Delta(word + " ");
            try
            {
                await Task.Delay(45, cancellationToken);
            }
            catch (OperationCanceledException)
            {
                cancelled = true;
                break;
            }
        }

        var message = new ChatMessage { Role = ChatRoles.Assistant, Text = builder.ToString().Trim() };
        if (cancelled)
        {
            message.ExtensionData = new() { ["finish"] = JsonSerializer.SerializeToElement("cancelled") };
        }
        else
        {
            var seconds = new[] { 200d, 725d };
            for (var i = 0; i < Math.Min(2, chat.Sources.Count); i++)
                message.Citations.Add(new ChatCitation
                {
                    RecordingId = chat.Sources[i].RecordingId,
                    VersionId = chat.Sources[i].VersionId,
                    StartSec = seconds[i],
                });
        }
        var saved = _chats.AppendMessage(chatId, message);
        if (cancelled)
            yield break;
        yield return new ChatEvent.Completed(saved.Messages[^1]);
    }

    public ChatRecord AddSource(string chatId, string recordingId)
    {
        var version = _recordings.Get(recordingId)?.Manifest.CurrentVersion
                      ?? throw new KeyNotFoundException($"Recording {recordingId} not found.");
        var before = _chats.Get(chatId);
        var chat = _chats.AddSource(chatId, recordingId, version);
        if (before is not null && before.Includes(recordingId))
            return chat; // moved to the current version, no event
        var title = _recordings.Get(recordingId)?.Manifest.EffectiveTitle ?? recordingId;
        return _chats.AppendMessage(chatId, new ChatMessage
        {
            Role = ChatRoles.System,
            Text = $"{title} added · Gemma now reads {HistoryUiFormat.Count(chat.Sources.Count, "recording")}",
        });
    }

    public ChatRecord RemoveSource(string chatId, string recordingId)
    {
        var title = _recordings.Get(recordingId)?.Manifest.EffectiveTitle ?? recordingId;
        var chat = _chats.RemoveSource(chatId, recordingId);
        return _chats.AppendMessage(chatId, new ChatMessage
        {
            Role = ChatRoles.System,
            Text = $"{title} removed · Gemma now reads {HistoryUiFormat.Count(chat.Sources.Count, "recording")}",
        });
    }

    public void CloseChat(string chatId)
    {
    }
}
#endif
