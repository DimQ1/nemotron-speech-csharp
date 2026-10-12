using System.Security.Cryptography;
using System.Text.Json;

namespace VoiceType.Uno.History;

public sealed record ChatLoadResult(IReadOnlyList<ChatRecord> Chats, IReadOnlyList<HistoryLoadError> Errors);

/// <summary>A chat source whose recording now has a different current version (or is gone).</summary>
public sealed record OutdatedSource(string RecordingId, string ChatVersionId, string? CurrentVersionId)
{
    public bool RecordingMissing => CurrentVersionId is null;
}

/// <summary>
/// Single writer of the top-level chats: <c>&lt;ChatsDir&gt;/&lt;chatId&gt;/chat.json</c>.
/// A chat references 1..N recordings by (recording, version). Atomic writes,
/// load-modify-write under one lock; returned records are copies.
/// </summary>
public sealed class ChatStore
{
    public const string ChatFileName = "chat.json";

    private readonly Lock _gate = new();
    private readonly TimeProvider _time;

    public ChatStore(string chatsDir, TimeProvider? timeProvider = null)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(chatsDir);
        ChatsDir = chatsDir;
        _time = timeProvider ?? TimeProvider.System;
    }

    public string ChatsDir { get; }

    /// <summary>Raised (outside the lock) with the chat id after any change.</summary>
    public event Action<string>? Changed;

    public ChatRecord Create(string? title, IEnumerable<(string RecordingId, string VersionId)> sources)
    {
        ArgumentNullException.ThrowIfNull(sources);
        var list = sources.ToList();
        if (list.Count == 0)
            throw new ArgumentException("A chat needs at least one recording.", nameof(sources));

        ChatRecord chat;
        lock (_gate)
        {
            var now = _time.GetLocalNow();
            chat = new ChatRecord
            {
                Id = NewId(),
                Title = string.IsNullOrWhiteSpace(title) ? "Chat " + now.ToString("yyyy-MM-dd HH:mm", System.Globalization.CultureInfo.InvariantCulture) : title.Trim(),
                CreatedAt = now,
                UpdatedAt = now,
            };
            foreach (var (recordingId, versionId) in list)
            {
                if (!chat.Includes(recordingId))
                    chat.Sources.Add(new ChatSource(recordingId, versionId, now));
            }
            Write(chat);
            chat = Copy(chat);
        }
        Changed?.Invoke(chat.Id);
        return chat;
    }

    public ChatRecord Create(string? title, string recordingId, string versionId) =>
        Create(title, [(recordingId, versionId)]);

    public ChatRecord? Get(string chatId)
    {
        lock (_gate)
        {
            var path = FilePath(chatId);
            if (!File.Exists(path))
                return null;
            try
            {
                return HistoryJson.Read(path, HistoryJsonContext.Default.ChatRecord);
            }
            catch (Exception ex) when (ex is JsonException or IOException)
            {
                return null;
            }
        }
    }

    /// <summary>All chats, most recently updated first; corrupted ones are reported.</summary>
    public ChatLoadResult LoadAll()
    {
        var chats = new List<ChatRecord>();
        var errors = new List<HistoryLoadError>();
        if (!Directory.Exists(ChatsDir))
            return new ChatLoadResult(chats, errors);

        lock (_gate)
        {
            foreach (var folder in Directory.EnumerateDirectories(ChatsDir))
            {
                var path = Path.Combine(folder, ChatFileName);
                try
                {
                    if (!File.Exists(path))
                    {
                        errors.Add(new HistoryLoadError(folder, "chat.json is missing."));
                        continue;
                    }
                    var chat = HistoryJson.Read(path, HistoryJsonContext.Default.ChatRecord);
                    if (string.IsNullOrEmpty(chat.Id))
                        chat.Id = Path.GetFileName(folder);
                    chats.Add(chat);
                }
                catch (Exception ex) when (ex is JsonException or IOException or UnauthorizedAccessException)
                {
                    errors.Add(new HistoryLoadError(folder, ex.Message));
                }
            }
        }
        chats.Sort((a, b) => b.UpdatedAt.CompareTo(a.UpdatedAt));
        return new ChatLoadResult(chats, errors);
    }

    /// <summary>Chats that include <paramref name="recordingId"/> (recording detail page).</summary>
    public IReadOnlyList<ChatRecord> ListIncluding(string recordingId) =>
        LoadAll().Chats.Where(c => c.Includes(recordingId)).ToList();

    /// <summary>Chats answering from exactly this version (confirmation before deleting it).</summary>
    public IReadOnlyList<ChatRecord> ListReferencing(string recordingId, string versionId) =>
        LoadAll().Chats
            .Where(c => c.Sources.Exists(s => s.RecordingId == recordingId && s.VersionId == versionId))
            .ToList();

    /// <summary>Adds a recording, or moves an existing source to <paramref name="versionId"/>.</summary>
    public ChatRecord AddSource(string chatId, string recordingId, string versionId) =>
        Mutate(chatId, (c, now) =>
        {
            var existing = c.Sources.Find(s => s.RecordingId == recordingId);
            if (existing is null)
                c.Sources.Add(new ChatSource(recordingId, versionId, now));
            else
                existing.VersionId = versionId;
        });

    /// <exception cref="InvalidOperationException">Removing the last source (a chat needs 1..N).</exception>
    public ChatRecord RemoveSource(string chatId, string recordingId) =>
        Mutate(chatId, (c, _) =>
        {
            var index = c.Sources.FindIndex(s => s.RecordingId == recordingId);
            if (index < 0)
                return;
            if (c.Sources.Count == 1)
                throw new InvalidOperationException("A chat needs at least one recording; delete the chat instead.");
            c.Sources.RemoveAt(index);
        });

    /// <summary>Appends to the display log; assigns an id and timestamp when missing.</summary>
    public ChatRecord AppendMessage(string chatId, ChatMessage message)
    {
        ArgumentNullException.ThrowIfNull(message);
        var copy = HistoryJson.Clone(message, HistoryJsonContext.Default.ChatMessage);
        return Mutate(chatId, (c, now) =>
        {
            if (string.IsNullOrEmpty(copy.Id))
                copy.Id = "m" + (c.Messages.Count + 1).ToString(System.Globalization.CultureInfo.InvariantCulture);
            if (copy.CreatedAt == default)
                copy.CreatedAt = now;
            c.Messages.Add(copy);
        });
    }

    /// <summary>Stores the serialized Agent Framework session verbatim (null clears it).</summary>
    public ChatRecord SaveAgentSession(string chatId, JsonElement? session) =>
        Mutate(chatId, (c, _) => c.AgentSession = session?.Clone());

    public ChatRecord Rename(string chatId, string title)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(title);
        return Mutate(chatId, (c, _) => c.Title = title.Trim());
    }

    public void Delete(string chatId)
    {
        lock (_gate)
        {
            var folder = Path.Combine(ChatsDir, chatId);
            if (Directory.Exists(folder))
                Directory.Delete(folder, recursive: true);
        }
        Changed?.Invoke(chatId);
    }

    /// <summary>
    /// Removes a deleted recording from every chat. Chats left with no source are
    /// deleted when <paramref name="deleteEmptyChats"/> (otherwise kept as read-only logs).
    /// Returns the ids of affected chats.
    /// </summary>
    public IReadOnlyList<string> RemoveRecordingFromAll(string recordingId, bool deleteEmptyChats = false)
    {
        var affected = new List<string>();
        lock (_gate)
        {
            foreach (var chat in LoadAll().Chats.Where(c => c.Includes(recordingId)))
            {
                chat.Sources.RemoveAll(s => s.RecordingId == recordingId);
                if (chat.Sources.Count == 0 && deleteEmptyChats)
                    Directory.Delete(Path.Combine(ChatsDir, chat.Id), recursive: true);
                else
                {
                    chat.UpdatedAt = _time.GetLocalNow();
                    Write(chat);
                }
                affected.Add(chat.Id);
            }
        }
        foreach (var id in affected)
            Changed?.Invoke(id);
        return affected;
    }

    /// <summary>
    /// Sources whose recording has moved its current version away from the one the
    /// chat answers from (shown as "outdated · use vN"). Computed, never stored.
    /// </summary>
    /// <param name="currentVersionOf">Current version of a recording, or null when it no longer exists.</param>
    public static IReadOnlyList<OutdatedSource> GetOutdatedSources(ChatRecord chat, Func<string, string?> currentVersionOf)
    {
        ArgumentNullException.ThrowIfNull(chat);
        ArgumentNullException.ThrowIfNull(currentVersionOf);
        var result = new List<OutdatedSource>();
        foreach (var source in chat.Sources)
        {
            var current = currentVersionOf(source.RecordingId);
            if (current != source.VersionId)
                result.Add(new OutdatedSource(source.RecordingId, source.VersionId, current));
        }
        return result;
    }

    private ChatRecord Mutate(string chatId, Action<ChatRecord, DateTimeOffset> mutate)
    {
        ChatRecord copy;
        lock (_gate)
        {
            var path = FilePath(chatId);
            if (!File.Exists(path))
                throw new KeyNotFoundException($"Chat {chatId} not found.");
            var chat = HistoryJson.Read(path, HistoryJsonContext.Default.ChatRecord);
            var now = _time.GetLocalNow();
            mutate(chat, now);
            chat.UpdatedAt = now;
            Write(chat);
            copy = Copy(chat);
        }
        Changed?.Invoke(chatId);
        return copy;
    }

    private string FilePath(string chatId)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(chatId);
        if (chatId.IndexOfAny(Path.GetInvalidFileNameChars()) >= 0 || chatId.Contains(".."))
            throw new ArgumentException("Invalid chat id.", nameof(chatId));
        return Path.Combine(ChatsDir, chatId, ChatFileName);
    }

    private void Write(ChatRecord chat) =>
        HistoryJson.WriteAtomic(FilePath(chat.Id), chat, HistoryJsonContext.Default.ChatRecord);

    private static ChatRecord Copy(ChatRecord chat) => HistoryJson.Clone(chat, HistoryJsonContext.Default.ChatRecord);

    private string NewId()
    {
        while (true)
        {
            var id = "c" + Convert.ToHexString(RandomNumberGenerator.GetBytes(4)).ToLowerInvariant();
            if (!Directory.Exists(Path.Combine(ChatsDir, id)))
                return id;
        }
    }
}
