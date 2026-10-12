using System.Globalization;
using System.Text;

namespace VoiceType.Uno.History;

/// <summary>Transcript exporters: <c>.txt</c> with timestamps, <c>.srt</c>, <c>.lrc</c>. Pure, LF line endings.</summary>
public static class TranscriptExport
{
    /// <summary>
    /// <c>[hh:mm:ss] text</c> per segment. With a translation, the translated line follows
    /// each segment (indented under the text) and segments are separated by a blank line.
    /// </summary>
    public static string ToText(TranscriptDocument transcript, TranslationDocument? translation = null)
    {
        ArgumentNullException.ThrowIfNull(transcript);
        var sb = new StringBuilder();
        var first = true;
        foreach (var segment in NonEmpty(transcript))
        {
            if (translation is not null && !first)
                sb.Append('\n');
            first = false;

            var stamp = $"[{FormatClock(segment.Start)}] ";
            sb.Append(stamp).Append(segment.Text.Trim()).Append('\n');
            var translated = translation?.TextFor(segment.Index)?.Trim();
            if (!string.IsNullOrEmpty(translated))
                sb.Append(' ', stamp.Length).Append(translated).Append('\n');
        }
        return sb.ToString();
    }

    /// <summary>SubRip subtitles (video players, VLC); the translation, if given, as a second line.</summary>
    public static string ToSrt(TranscriptDocument transcript, TranslationDocument? translation = null)
    {
        ArgumentNullException.ThrowIfNull(transcript);
        var sb = new StringBuilder();
        var n = 0;
        foreach (var segment in NonEmpty(transcript))
        {
            n++;
            var end = segment.End > segment.Start ? segment.End : segment.Start + 1.0;
            sb.Append(n.ToString(CultureInfo.InvariantCulture)).Append('\n');
            sb.Append(FormatSrtTime(segment.Start)).Append(" --> ").Append(FormatSrtTime(end)).Append('\n');
            sb.Append(segment.Text.Trim()).Append('\n');
            var translated = translation?.TextFor(segment.Index)?.Trim();
            if (!string.IsNullOrEmpty(translated))
                sb.Append(translated).Append('\n');
            sb.Append('\n');
        }
        return sb.ToString();
    }

    /// <summary>
    /// LRC synced lyrics (music players). An empty timed line clears the display after a
    /// segment when the next one starts more than a second later, and after the last one.
    /// </summary>
    public static string ToLrc(TranscriptDocument transcript, string? title = null, double? durationSec = null)
    {
        ArgumentNullException.ThrowIfNull(transcript);
        var sb = new StringBuilder();
        if (!string.IsNullOrWhiteSpace(title))
            sb.Append("[ti:").Append(title.Trim()).Append("]\n");
        if (durationSec is { } duration)
        {
            var total = (int)Math.Floor(duration);
            sb.Append(CultureInfo.InvariantCulture, $"[length:{total / 60:00}:{total % 60:00}]\n");
        }

        var segments = NonEmpty(transcript).ToList();
        for (var i = 0; i < segments.Count; i++)
        {
            var segment = segments[i];
            sb.Append('[').Append(FormatLrcTime(segment.Start)).Append(']').Append(segment.Text.Trim()).Append('\n');
            var isLast = i == segments.Count - 1;
            if (segment.End > segment.Start && (isLast || segments[i + 1].Start - segment.End > 1.0))
                sb.Append('[').Append(FormatLrcTime(segment.End)).Append("]\n");
        }
        return sb.ToString();
    }

    /// <summary><c>hh:mm:ss</c> (hours not capped at 24).</summary>
    public static string FormatClock(double seconds)
    {
        var total = (long)Math.Floor(Math.Max(0, seconds));
        return string.Create(CultureInfo.InvariantCulture, $"{total / 3600:00}:{total / 60 % 60:00}:{total % 60:00}");
    }

    /// <summary><c>mm:ss</c>, or <c>h:mm:ss</c> from one hour — the citation format.</summary>
    public static string FormatShort(double seconds)
    {
        var total = (long)Math.Floor(Math.Max(0, seconds));
        return total >= 3600
            ? string.Create(CultureInfo.InvariantCulture, $"{total / 3600}:{total / 60 % 60:00}:{total % 60:00}")
            : string.Create(CultureInfo.InvariantCulture, $"{total / 60:00}:{total % 60:00}");
    }

    /// <summary><c>hh:mm:ss,mmm</c>.</summary>
    public static string FormatSrtTime(double seconds)
    {
        var ms = (long)Math.Round(Math.Max(0, seconds) * 1000, MidpointRounding.AwayFromZero);
        return string.Create(CultureInfo.InvariantCulture,
            $"{ms / 3_600_000:00}:{ms / 60_000 % 60:00}:{ms / 1000 % 60:00},{ms % 1000:000}");
    }

    /// <summary><c>mm:ss.xx</c> (total minutes, hundredths).</summary>
    public static string FormatLrcTime(double seconds)
    {
        var cs = (long)Math.Round(Math.Max(0, seconds) * 100, MidpointRounding.AwayFromZero);
        return string.Create(CultureInfo.InvariantCulture, $"{cs / 6000:00}:{cs / 100 % 60:00}.{cs % 100:00}");
    }

    private static IEnumerable<TranscriptSegment> NonEmpty(TranscriptDocument transcript) =>
        transcript.Segments.Where(s => !string.IsNullOrWhiteSpace(s.Text));
}

/// <summary>Chat → Markdown document (title, sources with version/model, messages, citations).</summary>
public static class ChatExport
{
    /// <param name="recordings">Manifests of the chat's sources by id; missing ones render as deleted.</param>
    public static string ToMarkdown(ChatRecord chat, IReadOnlyDictionary<string, RecordingManifest> recordings)
    {
        ArgumentNullException.ThrowIfNull(chat);
        ArgumentNullException.ThrowIfNull(recordings);

        var sb = new StringBuilder();
        sb.Append("# ").Append(chat.Title).Append("\n\n");
        sb.Append("Created ").Append(FormatDate(chat.CreatedAt));
        if (chat.UpdatedAt != chat.CreatedAt)
            sb.Append(" · updated ").Append(FormatDate(chat.UpdatedAt));
        sb.Append("\n\n");

        sb.Append("## Sources\n\n");
        foreach (var source in chat.Sources)
        {
            if (recordings.TryGetValue(source.RecordingId, out var manifest))
            {
                var version = manifest.FindVersion(source.VersionId);
                sb.Append("- **").Append(manifest.EffectiveTitle).Append("** — recorded ")
                  .Append(FormatDate(manifest.StartedAt))
                  .Append(", version ").Append(source.VersionId);
                if (version is not null)
                {
                    sb.Append(" (").Append(version.Kind).Append(')');
                    var model = version.Model?.Title ?? version.Model?.Folder;
                    if (!string.IsNullOrWhiteSpace(model))
                        sb.Append(", model ").Append(model);
                }
                sb.Append('\n');
            }
            else
            {
                sb.Append("- *Recording ").Append(source.RecordingId).Append(" (deleted)* — version ")
                  .Append(source.VersionId).Append('\n');
            }
        }
        sb.Append('\n');

        sb.Append("## Conversation\n");
        foreach (var message in chat.Messages.Where(m => m.Role != ChatRoles.System))
        {
            sb.Append("\n### ").Append(RoleName(message.Role)).Append(" · ")
              .Append(message.CreatedAt.ToString("HH:mm", CultureInfo.InvariantCulture)).Append("\n\n");
            sb.Append(message.Text.Trim()).Append('\n');
            if (message.Citations.Count > 0)
            {
                sb.Append("\nSources: ")
                  .Append(string.Join(", ", message.Citations.Select(c => FormatCitation(c, recordings))))
                  .Append('\n');
            }
        }
        return sb.ToString();
    }

    /// <summary><c>[Recording title mm:ss]</c>.</summary>
    public static string FormatCitation(ChatCitation citation, IReadOnlyDictionary<string, RecordingManifest> recordings)
    {
        ArgumentNullException.ThrowIfNull(citation);
        var title = recordings.TryGetValue(citation.RecordingId, out var m)
            ? m.EffectiveTitle
            : "Recording " + citation.RecordingId;
        return $"[{title} {TranscriptExport.FormatShort(citation.StartSec)}]";
    }

    private static string RoleName(string role) => role switch
    {
        ChatRoles.User => "You",
        ChatRoles.Assistant => "Gemma",
        _ => role,
    };

    private static string FormatDate(DateTimeOffset value) =>
        value.ToString("yyyy-MM-dd HH:mm", CultureInfo.InvariantCulture);
}

/// <summary>File helpers for exports (UTF-8 without BOM, atomic, safe names).</summary>
public static class ExportFiles
{
    /// <summary>Replaces characters invalid on any of Windows/Linux/Android; trims and caps length.</summary>
    public static string SanitizeFileName(string name, int maxLength = 80)
    {
        var invalid = new HashSet<char>(Path.GetInvalidFileNameChars()) { '<', '>', ':', '"', '/', '\\', '|', '?', '*' };
        var sb = new StringBuilder(name.Length);
        foreach (var c in name)
            sb.Append(invalid.Contains(c) || char.IsControl(c) ? '_' : c);
        var result = sb.ToString().Trim().TrimEnd('.', ' ');
        if (result.Length > maxLength)
            result = result[..maxLength].TrimEnd('.', ' ');
        return result.Length == 0 ? "recording" : result;
    }

    /// <summary><c>2026-10-12 14-02 Weekly sync</c>.</summary>
    public static string SuggestBaseName(RecordingManifest manifest)
    {
        ArgumentNullException.ThrowIfNull(manifest);
        return SanitizeFileName(
            manifest.StartedAt.ToString("yyyy-MM-dd HH-mm", CultureInfo.InvariantCulture) + " " + manifest.EffectiveTitle);
    }

    public static void WriteText(string path, string content) => HistoryJson.WriteTextAtomic(path, content);

    /// <summary>
    /// Writes <c>&lt;baseName&gt;.srt</c> and <c>&lt;baseName&gt;.lrc</c> next to an exported
    /// audio file of the same base name. Returns the written paths.
    /// </summary>
    public static IReadOnlyList<string> WriteSubtitles(
        string directory, string baseName, TranscriptDocument transcript,
        string? title = null, double? durationSec = null, TranslationDocument? translation = null)
    {
        var srt = Path.Combine(directory, baseName + ".srt");
        var lrc = Path.Combine(directory, baseName + ".lrc");
        WriteText(srt, TranscriptExport.ToSrt(transcript, translation));
        WriteText(lrc, TranscriptExport.ToLrc(transcript, title, durationSec));
        return [srt, lrc];
    }
}
