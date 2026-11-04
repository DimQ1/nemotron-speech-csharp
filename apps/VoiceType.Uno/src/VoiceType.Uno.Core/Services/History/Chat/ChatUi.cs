using System.Globalization;
using System.Text.Json;
using VoiceType.Uno.Localization;

namespace VoiceType.Uno.History;

/// <summary>
/// Pure presentation logic of the chat screens (design §4.4): texts, grouping of the thread,
/// citation chip labels, the "outdated source" notices and the layout thresholds. No UI types,
/// so it is unit-tested without the Uno runtime.
/// </summary>
public static class ChatUiLayout
{
    /// <summary>Width from which the Chats list and the open chat sit side by side.</summary>
    public const double MasterDetailWidth = 840;

    /// <summary>Source chips need at least this width (otherwise one summary row).</summary>
    public const double ChipsMinWidth = 600;

    /// <summary>Source chips need at least this height (otherwise one summary row).</summary>
    public const double ChipsMinHeight = 480;

    public static bool UseSourceChips(double width, double height) =>
        width >= ChipsMinWidth && height >= ChipsMinHeight;

    public static bool IsMasterDetail(double width) => width >= MasterDetailWidth;

    /// <summary>Low windows (landscape phones, keyboard open): 48 px header, no source area.</summary>
    public static bool IsShort(double height) => height > 0 && height < ChipsMinHeight;
}

/// <summary>A citation chip: <c>Weekly sync 03:20</c>.</summary>
/// <param name="Label">Recording title (shortened) and time.</param>
/// <param name="RecordingExists">False once the recording was deleted: the chip is shown but cannot open anything.</param>
public sealed record ChatCitationChip(
    string Label, string RecordingId, string? VersionId, double StartSec, bool RecordingExists, string AutomationName);

public enum ChatThreadKind
{
    User,
    Assistant,
    /// <summary>Centered event line: sources added or removed.</summary>
    Event,
}

/// <summary>One line of the conversation as the screen shows it.</summary>
/// <param name="ShowRoleLabel">First message of a run of the same sender: shows the name ("You" / "Gemma").</param>
/// <param name="ShowTime">Last message of a run: shows the time under the bubble.</param>
public sealed record ChatThreadItem(
    string Id,
    ChatThreadKind Kind,
    string Text,
    string TimeText,
    bool ShowRoleLabel,
    bool ShowTime,
    bool IsIncomplete,
    IReadOnlyList<ChatCitationChip> Citations)
{
    public bool IsUser => Kind == ChatThreadKind.User;
    public bool IsAssistant => Kind == ChatThreadKind.Assistant;
    public bool IsEvent => Kind == ChatThreadKind.Event;
}

/// <summary>A source whose recording has a newer current version (or is gone).</summary>
public sealed record ChatOutdatedNotice(string RecordingId, string Text, bool CanUseCurrent);

public static class ChatUiFormat
{
    public static string Subtitle => Loc.T("Chat.Subtitle");

    /// <summary>Longest recording title shown inside a citation chip.</summary>
    public const int CitationTitleMax = 24;

    public static string SourcesSummary(int count) =>
        count == 0 ? Loc.T("Chat.Sources.None") : Loc.Plural("Chat.Sources.Count", count);

    /// <summary><c>Weekly sync v2, Supplier call v1, +2</c> (first names, then a count).</summary>
    public static string SourcesLine(IReadOnlyList<(string Title, string VersionId)> sources, int maxNames = 2)
    {
        ArgumentNullException.ThrowIfNull(sources);
        if (sources.Count == 0)
            return "";
        var names = sources.Take(maxNames).Select(s => s.Title + " " + s.VersionId);
        var text = string.Join(", ", names);
        return sources.Count > maxNames ? text + ", +" + (sources.Count - maxNames).ToString(CultureInfo.InvariantCulture) : text;
    }

    /// <summary><c>3 recordings · 12 messages · on this device</c> under the chat title.</summary>
    public static string HeaderSubtitle(ChatRecord chat)
    {
        ArgumentNullException.ThrowIfNull(chat);
        return HistoryUiFormat.Join(SourcesSummary(chat.Sources.Count),
            Loc.Plural("Chat.Messages.Count", CountMessages(chat)), Loc.T("Chat.Header.OnDevice"));
    }

    /// <summary>Chats list header: <c>5 chats · Gemma 4 E2B on this device</c>.</summary>
    public static string ListSubtitle(int chats) =>
        chats == 0 ? Loc.F("Chat.List.SubtitleEmpty", Subtitle) : Loc.Plural("Chat.List.Subtitle", chats, Subtitle);

    /// <summary>Menu entry second line.</summary>
    public static string MenuSummary(int chats) =>
        chats == 0 ? Loc.T("Chat.MainMenu.Empty") : Loc.Plural("Chat.MainMenu.Count", chats);

    /// <summary>
    /// Label of a quick prompt in the interface language. The labels come from SpeechLib (<c>ChatPrompts</c>, English);
    /// the known ids are translated here, an unknown one keeps its label. The prompt text sent to Gemma stays English.
    /// </summary>
    public static string QuickPromptLabel(string id, string label) => id switch
    {
        "summary" => Loc.T("Chat.QuickPrompt.Summary"),
        "actions" => Loc.T("Chat.QuickPrompt.Actions"),
        "decisions" => Loc.T("Chat.QuickPrompt.Decisions"),
        "questions" => Loc.T("Chat.QuickPrompt.Questions"),
        "speakers" => Loc.T("Chat.QuickPrompt.Speakers"),
        _ => label,
    };

    /// <summary>User and assistant messages (events are not counted).</summary>
    public static int CountMessages(ChatRecord chat) => chat.Messages.Count(m => m.Role != ChatRoles.System);

    /// <summary>
    /// Line under the sources: what the model reads. One source: <c>Reads transcript v2 · 12:34</c>.
    /// </summary>
    public static string ContextLine(IReadOnlyList<(string VersionId, double DurationSeconds)> sources) =>
        sources.Count switch
        {
            0 => Loc.T("Chat.Context.AddRecording"),
            1 => HistoryUiFormat.Join(Loc.F("Chat.Context.ReadsTranscript", sources[0].VersionId),
                sources[0].DurationSeconds > 0 ? HistoryUiFormat.Duration(sources[0].DurationSeconds) : null),
            _ => Loc.T("Chat.Context.ReadsSummaries"),
        };

    /// <summary><c>Weekly sync 03:20</c>; a deleted recording reads <c>Deleted recording 03:20</c>.</summary>
    public static string CitationLabel(ChatCitation citation, Func<string, string?> titleOf, bool singleRecording = false, string? speakerOverride = null)
    {
        ArgumentNullException.ThrowIfNull(citation);
        ArgumentNullException.ThrowIfNull(titleOf);
        var title = titleOf(citation.RecordingId);
        title = string.IsNullOrWhiteSpace(title) ? Loc.T("Chat.Citation.DeletedRecording") : Shorten(title.Trim(), CitationTitleMax);
        var time = HistoryUiFormat.SegmentTime(citation.StartSec);
        if (string.IsNullOrWhiteSpace(speakerOverride ?? citation.Speaker))
            return title + " " + time;
        // With speakers: "Weekly sync · Anna 03:20", and just "Anna 03:20" in a chat about one recording.
        var speaker = Shorten((speakerOverride ?? citation.Speaker)!.Trim(), 24);
        return singleRecording ? speaker + " " + time : title + " · " + speaker + " " + time;
    }

    public static string Shorten(string text, int max) =>
        text.Length <= max ? text : text[..Math.Max(1, max - 1)].TrimEnd() + "…";

    /// <summary>
    /// Chat list row meta: <c>3 recordings · 12 messages · 15:40</c> or
    /// <c>Weekly sync · 12 messages · 15:40</c> for a single source.
    /// </summary>
    public static string RowMeta(ChatRecord chat, Func<string, string?> titleOf, DateTimeOffset now)
    {
        ArgumentNullException.ThrowIfNull(chat);
        ArgumentNullException.ThrowIfNull(titleOf);
        var who = chat.Sources.Count switch
        {
            0 => Loc.T("Chat.Sources.None"),
            1 => Shorten(titleOf(chat.Sources[0].RecordingId)?.Trim() is { Length: > 0 } t ? t : Loc.T("Chat.Citation.DeletedRecording"), 28),
            var n => Loc.Plural("Chat.Sources.Count", n),
        };
        return HistoryUiFormat.Join(who, Loc.Plural("Chat.Messages.Count", CountMessages(chat)),
            WhenText(chat.UpdatedAt, now));
    }

    /// <summary><c>15:40</c> today, <c>Yesterday</c>, <c>12 Oct</c>.</summary>
    public static string WhenText(DateTimeOffset value, DateTimeOffset now)
    {
        var local = value.ToOffset(now.Offset);
        return HistoryUiRows.GroupOf(value, now) switch
        {
            HistoryDateGroup.Today => local.ToString("HH:mm", Loc.Culture),
            HistoryDateGroup.Yesterday => Loc.T("Chat.When.Yesterday"),
            _ => local.ToString(local.Year == now.Year ? "d MMM" : "d MMM yyyy", Loc.Culture),
        };
    }

    public static string DisplayTitle(ChatRecord chat) =>
        string.IsNullOrWhiteSpace(chat.Title) ? Loc.T("Chat.Title.Default") : chat.Title.Trim();

    /// <summary>Last answer (or question) of a chat on one line, for the list.</summary>
    public static string LastLine(ChatRecord chat, int maxChars = 140)
    {
        ArgumentNullException.ThrowIfNull(chat);
        var last = chat.Messages.LastOrDefault(m => m.Role == ChatRoles.Assistant && !string.IsNullOrWhiteSpace(m.Text))
                   ?? chat.Messages.LastOrDefault(m => m.Role == ChatRoles.User && !string.IsNullOrWhiteSpace(m.Text));
        if (last is null)
            return "";
        // Plain text: the markdown marks (**bold**, bullets, headings) are not shown in a list row.
        var plain = string.Join(' ', ChatMarkdown.Parse(last.Text).Select(b => string.Concat(b.Runs.Select(r => r.Text))));
        var line = string.Join(' ', plain.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries));
        return Shorten(line, maxChars);
    }

    /// <summary>
    /// A cancelled, truncated or failed answer. Stored by the chat service as
    /// <c>"finish": "cancelled" | "length" | "error"</c> (or <c>"incomplete": true</c>) in the message's
    /// extension data.
    /// </summary>
    public static bool IsIncomplete(ChatMessage message)
    {
        ArgumentNullException.ThrowIfNull(message);
        if (message.ExtensionData is not { } data)
            return false;
        if (data.TryGetValue("incomplete", out var flag) && flag.ValueKind == JsonValueKind.True)
            return true;
        return data.TryGetValue("finish", out var finish) && finish.ValueKind == JsonValueKind.String
               && finish.GetString() is "cancelled" or "length" or "error";
    }

    /// <summary>"Answer stopped" / "Answer cut off" text of the marker under an incomplete answer.</summary>
    public static string IncompleteText(ChatMessage message)
    {
        if (message.ExtensionData is { } data && data.TryGetValue("finish", out var finish)
            && finish.ValueKind == JsonValueKind.String)
        {
            return finish.GetString() switch
            {
                "length" => Loc.T("Chat.Incomplete.Length"),
                "error" => Loc.T("Chat.Incomplete.Error"),
                _ => Loc.T("Chat.Incomplete.Stopped"),
            };
        }
        return Loc.T("Chat.Incomplete.Stopped");
    }

    /// <summary>Case- and diacritics-insensitive filter: every word must be in the title, a message or a source name.</summary>
    public static IReadOnlyList<ChatRecord> Filter(
        IEnumerable<ChatRecord> chats, string? query, Func<string, string?> titleOf)
    {
        ArgumentNullException.ThrowIfNull(chats);
        var terms = TextTokens.SplitWords(TextNormalizer.Normalize(query));
        if (terms.Count == 0)
            return chats.ToList();
        var result = new List<ChatRecord>();
        foreach (var chat in chats)
        {
            var haystack = TextNormalizer.Normalize(string.Join('\n',
                chat.Messages.Where(m => m.Role != ChatRoles.System).Select(m => m.Text)
                    .Prepend(chat.Title)
                    .Concat(chat.Sources.Select(s => titleOf(s.RecordingId) ?? ""))));
            if (terms.All(t => haystack.Contains(t, StringComparison.Ordinal)))
                result.Add(chat);
        }
        return result;
    }
}

/// <summary>Builds the thread of a chat: groups runs of the same sender and resolves citation chips.</summary>
public static class ChatUiThread
{
    public static IReadOnlyList<ChatThreadItem> Build(ChatRecord chat, Func<string, string?> titleOf,
        Func<ChatCitation, string?>? speakerOf = null)
    {
        ArgumentNullException.ThrowIfNull(chat);
        ArgumentNullException.ThrowIfNull(titleOf);

        var items = new List<ChatThreadItem>(chat.Messages.Count);
        var messages = chat.Messages;
        for (var i = 0; i < messages.Count; i++)
        {
            var m = messages[i];
            var kind = KindOf(m.Role);
            var previous = i > 0 ? KindOf(messages[i - 1].Role) : (ChatThreadKind?)null;
            var next = i + 1 < messages.Count ? KindOf(messages[i + 1].Role) : (ChatThreadKind?)null;
            var isEvent = kind == ChatThreadKind.Event;
            items.Add(new ChatThreadItem(
                Id: string.IsNullOrEmpty(m.Id) ? "i" + i.ToString(CultureInfo.InvariantCulture) : m.Id,
                Kind: kind,
                Text: (isEvent ? ChatSourceEvents.DisplayText(m) : m.Text).Trim(),
                TimeText: m.CreatedAt == default ? "" : m.CreatedAt.ToLocalTime().ToString("HH:mm", Loc.Culture),
                ShowRoleLabel: !isEvent && previous != kind,
                ShowTime: !isEvent && next != kind,
                IsIncomplete: kind == ChatThreadKind.Assistant && ChatUiFormat.IsIncomplete(m),
                Citations: kind == ChatThreadKind.Assistant ? Chips(m.Citations, titleOf, speakerOf: speakerOf) : []));
        }
        return items;
    }

    /// <param name="speakerOf">Name of who said the cited passage, looked up at render time when the citation has none.</param>
    public static IReadOnlyList<ChatCitationChip> Chips(IEnumerable<ChatCitation> citations, Func<string, string?> titleOf, bool singleRecording = false,
        Func<ChatCitation, string?>? speakerOf = null)
    {
        var chips = new List<ChatCitationChip>();
        var seen = new HashSet<string>(StringComparer.Ordinal);
        foreach (var c in citations)
        {
            var label = ChatUiFormat.CitationLabel(c, titleOf, singleRecording, speakerOf?.Invoke(c));
            if (!seen.Add(c.RecordingId + "|" + label))
                continue;
            var exists = titleOf(c.RecordingId) is not null;
            chips.Add(new ChatCitationChip(label, c.RecordingId, c.VersionId, c.StartSec, exists,
                exists ? Loc.F("Chat.Citation.OpenName", label) : Loc.F("Chat.Citation.DeletedName", label)));
        }
        return chips;
    }

    /// <summary>"Weekly sync: answers use v1, current is v2" for every source that moved on.</summary>
    public static IReadOnlyList<ChatOutdatedNotice> OutdatedNotices(
        ChatRecord chat, Func<string, string?> currentVersionOf, Func<string, string?> titleOf)
    {
        ArgumentNullException.ThrowIfNull(chat);
        var notices = new List<ChatOutdatedNotice>();
        foreach (var o in ChatStore.GetOutdatedSources(chat, currentVersionOf))
        {
            var title = titleOf(o.RecordingId);
            if (o.RecordingMissing)
                continue; // a deleted recording is dropped from the sources by the store; nothing to update
            notices.Add(new ChatOutdatedNotice(o.RecordingId,
                Loc.F("Chat.Outdated.Notice", title ?? o.RecordingId, o.ChatVersionId, o.CurrentVersionId),
                CanUseCurrent: true));
        }
        return notices;
    }

    private static ChatThreadKind KindOf(string role) => role switch
    {
        ChatRoles.User => ChatThreadKind.User,
        ChatRoles.Assistant => ChatThreadKind.Assistant,
        _ => ChatThreadKind.Event,
    };
}

public enum ChatMdBlockKind
{
    Paragraph,
    Bullet,
    Heading,
}

public enum ChatMdStyle
{
    Plain,
    Bold,
    Italic,
    Code,
}

public sealed record ChatMdRun(string Text, ChatMdStyle Style);

public sealed record ChatMdBlock(ChatMdBlockKind Kind, IReadOnlyList<ChatMdRun> Runs);

/// <summary>
/// The markdown subset of answers: paragraphs, <c>-</c>/<c>*</c>/<c>1.</c> lists, <c>#</c> headings,
/// <c>**bold**</c>, <c>*italic*</c> and <c>`code`</c>. Everything else stays plain text.
/// Citations like <c>[S1 03:20]</c> were already cut out by the service and arrive as chips.
/// </summary>
public static class ChatMarkdown
{
    public static IReadOnlyList<ChatMdBlock> Parse(string? text)
    {
        var blocks = new List<ChatMdBlock>();
        if (string.IsNullOrWhiteSpace(text))
            return blocks;

        var paragraph = new List<string>();
        void FlushParagraph()
        {
            if (paragraph.Count == 0)
                return;
            blocks.Add(new ChatMdBlock(ChatMdBlockKind.Paragraph, Inline(string.Join(' ', paragraph))));
            paragraph.Clear();
        }

        foreach (var raw in text.Replace("\r\n", "\n").Split('\n'))
        {
            var line = raw.Trim();
            if (line.Length == 0)
            {
                FlushParagraph();
                continue;
            }
            if (line.StartsWith('#'))
            {
                FlushParagraph();
                blocks.Add(new ChatMdBlock(ChatMdBlockKind.Heading, Inline(line.TrimStart('#').Trim())));
            }
            else if (BulletText(line) is { } bullet)
            {
                FlushParagraph();
                blocks.Add(new ChatMdBlock(ChatMdBlockKind.Bullet, Inline(bullet)));
            }
            else
            {
                paragraph.Add(line);
            }
        }
        FlushParagraph();
        return blocks;
    }

    private static string? BulletText(string line)
    {
        if (line.Length > 2 && (line[0] is '-' or '*' or '•') && line[1] == ' ')
            return line[2..].Trim();
        var i = 0;
        while (i < line.Length && char.IsDigit(line[i]))
            i++;
        return i > 0 && i < line.Length - 1 && line[i] == '.' && line[i + 1] == ' ' ? line[(i + 2)..].Trim() : null;
    }

    public static IReadOnlyList<ChatMdRun> Inline(string text)
    {
        var runs = new List<ChatMdRun>();
        var plain = new System.Text.StringBuilder();
        void Flush()
        {
            if (plain.Length > 0)
            {
                runs.Add(new ChatMdRun(plain.ToString(), ChatMdStyle.Plain));
                plain.Clear();
            }
        }

        var i = 0;
        while (i < text.Length)
        {
            if (TryDelimited(text, i, "**", out var bold, out var end1))
            {
                Flush();
                runs.Add(new ChatMdRun(bold, ChatMdStyle.Bold));
                i = end1;
            }
            else if (TryDelimited(text, i, "`", out var code, out var end2))
            {
                Flush();
                runs.Add(new ChatMdRun(code, ChatMdStyle.Code));
                i = end2;
            }
            else if (TryDelimited(text, i, "*", out var italic, out var end3))
            {
                Flush();
                runs.Add(new ChatMdRun(italic, ChatMdStyle.Italic));
                i = end3;
            }
            else
            {
                plain.Append(text[i]);
                i++;
            }
        }
        Flush();
        return runs;
    }

    private static bool TryDelimited(string text, int start, string marker, out string inner, out int end)
    {
        inner = "";
        end = start;
        if (string.CompareOrdinal(text, start, marker, 0, marker.Length) != 0)
            return false;
        var close = text.IndexOf(marker, start + marker.Length, StringComparison.Ordinal);
        if (close <= start + marker.Length)
            return false;
        inner = text[(start + marker.Length)..close];
        if (inner.Length == 0 || char.IsWhiteSpace(inner[0]) || char.IsWhiteSpace(inner[^1]))
            return false;
        end = close + marker.Length;
        return true;
    }
}
