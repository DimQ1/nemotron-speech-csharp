using System.Text;
using System.Text.RegularExpressions;
using SpeechLib.Llm;

namespace VoiceType.Uno.History;

/// <summary>
/// Writes a short title from a transcript (design §5.7, §8): one Background request of
/// ≈ 500–700 prompt tokens (the beginning of the recording and, for a long one, its end).
/// </summary>
public sealed partial class AutoTitleGenerator
{
    /// <summary>Bump when the prompt or the cleaning changes: stored in <see cref="AutoTitleInfo.PromptVersion"/>.</summary>
    public const int PromptVersion = 1;

    public const int MaxWords = 10;
    public const int MaxChars = 80;

    /// <summary>Transcript tokens taken from the start / the end of a long recording.</summary>
    public const int HeadTokens = 420;
    public const int TailTokens = 140;

    private const string SystemPrompt =
        "You write titles for voice recordings. Read the transcript and reply with a title of 3 to 8 words " +
        "that says what the recording is about. Write the title in the same language as the transcript. " +
        "Reply with the title only: no quotes, no label, no final period, no explanation.";

    private readonly ILlmHost _host;

    public AutoTitleGenerator(ILlmHost host) => _host = host;

    /// <summary>The title, or null when the transcript is empty or the model gave nothing usable.</summary>
    public async Task<string?> GenerateAsync(IReadOnlyList<TranscriptSegment> segments, CancellationToken cancellationToken)
    {
        var excerpt = BuildExcerpt(segments, _host.CountTokens);
        if (excerpt.Length == 0)
            return null;

        var request = new LlmRequest(
            LlmPriority.Background,
            [new LlmMessage(LlmRole.System, SystemPrompt), new LlmMessage(LlmRole.User, "Transcript:\n" + excerpt + "\n\nTitle:")],
            new LlmSampling(Temperature: 0.2, TopP: 0.5, MaxOutputTokens: 40));

        for (var attempt = 1; ; attempt++)
        {
            try
            {
                return Clean(await _host.GenerateAsync(request, cancellationToken).ConfigureAwait(false));
            }
            catch (LlmUnavailableException)
            {
                throw;
            }
            catch (Exception) when (!cancellationToken.IsCancellationRequested && attempt < 3)
            {
                await Task.Delay(TimeSpan.FromMilliseconds(200), cancellationToken).ConfigureAwait(false);
            }
        }
    }

    /// <summary>The start of the transcript up to <see cref="HeadTokens"/>; a long one also gets "[…]" and its last <see cref="TailTokens"/>.</summary>
    public static string BuildExcerpt(IReadOnlyList<TranscriptSegment> segments, Func<string, int> countTokens)
    {
        var texts = segments.Select(s => s.Text?.Trim() ?? "").Where(t => t.Length > 0).ToList();
        if (texts.Count == 0)
            return "";

        var head = new StringBuilder();
        var used = 0;
        var next = 0;
        for (; next < texts.Count; next++)
        {
            var tokens = countTokens(texts[next]);
            if (used + tokens > HeadTokens && next > 0)
                break;
            used += tokens;
            if (head.Length > 0)
                head.Append(' ');
            // One very long segment is cut by words so the prompt stays within its budget.
            head.Append(tokens > HeadTokens ? CutWords(texts[next], HeadTokens, tokens) : texts[next]);
        }
        if (next >= texts.Count)
            return head.ToString();

        var tail = new List<string>();
        used = 0;
        for (var i = texts.Count - 1; i >= next; i--)
        {
            var tokens = countTokens(texts[i]);
            if (used + tokens > TailTokens)
                break;
            used += tokens;
            tail.Insert(0, texts[i]);
        }
        return tail.Count == 0 ? head.ToString() : head + " […] " + string.Join(" ", tail);
    }

    private static string CutWords(string text, int budgetTokens, int totalTokens)
    {
        var keep = (int)((long)text.Length * budgetTokens / totalTokens);
        if (keep >= text.Length)
            return text;
        var cut = text.LastIndexOf(' ', keep);
        return text[..(cut > 0 ? cut : keep)];
    }

    /// <summary>
    /// First line of the answer without labels, markdown, wrapping quotes and a final period,
    /// at most <see cref="MaxWords"/> words / <see cref="MaxChars"/> characters; null when nothing is left.
    /// </summary>
    public static string? Clean(string? raw)
    {
        if (string.IsNullOrWhiteSpace(raw))
            return null;

        var line = raw.Trim().Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .FirstOrDefault(l => l.Any(char.IsLetterOrDigit));
        if (line is null)
            return null;

        line = SpecialTokens().Replace(line, "");
        line = Label().Replace(line, "");
        line = line.Trim('*', '#', '_', '`', ' ', '\t').TrimEnd('.', '。', ' ');
        line = Unquote(line);
        line = line.TrimEnd('.', '。', ' ');
        line = Whitespace().Replace(line, " ").Trim();
        if (line.Length == 0 || !line.Any(char.IsLetterOrDigit))
            return null;

        var words = line.Split(' ', StringSplitOptions.RemoveEmptyEntries);
        if (words.Length > MaxWords)
            line = string.Join(" ", words.Take(MaxWords)).TrimEnd(',', ';', ':', '-', '—') + "…";
        if (line.Length > MaxChars)
        {
            var cut = line.LastIndexOf(' ', MaxChars - 1);
            line = line[..(cut > 0 ? cut : MaxChars - 1)].TrimEnd(',', ';', ':', '-', '—') + "…";
        }
        return line;
    }

    private static string Unquote(string value)
    {
        value = value.Trim();
        if (value.Length < 2)
            return value;
        var wrapped = (value[0], value[^1]) is ('"', '"') or ('“', '”') or ('«', '»') or ('\'', '\'') or ('`', '`');
        return wrapped && !value[1..^1].Contains(value[0]) ? value[1..^1].Trim() : value;
    }

    [GeneratedRegex(@"^\s*(?:title|заголовок|название|titel|titre|título)\s*[:：-]\s*", RegexOptions.IgnoreCase)]
    private static partial Regex Label();

    [GeneratedRegex(@"<\|?(?:unk|pad|endoftext|eos|bos|start_of_turn|end_of_turn)\|?>")]
    private static partial Regex SpecialTokens();

    [GeneratedRegex(@"\s+")]
    private static partial Regex Whitespace();
}
