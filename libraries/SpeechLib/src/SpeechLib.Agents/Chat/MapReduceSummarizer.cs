using System.Security.Cryptography;
using System.Text;
using SpeechLib.Llm;

namespace SpeechLib.Agents;

/// <summary>One line of a digest: the summary of the part of the recording that starts at <see cref="Start"/>.</summary>
public sealed record DigestItem(double Start, string Text);

public sealed record Digest(IReadOnlyList<DigestItem> Items, int Tokens)
{
    public string Render(string alias) =>
        string.Join("\n", Items.Select(i => $"[{alias} {TimeText.Format(i.Start)}] {i.Text}"));
}

/// <summary>
/// Map-reduce summaries of one long recording. Map: every ~400-token chunk becomes 2-3 sentences. Reduce: runs of
/// summaries are merged until the whole digest fits its token target. Each unit is one small generation (chunk
/// ~400 tokens + prompt ~60; merges ≤ ~600 tokens of input), so a Background-style preemption by live
/// translation only wastes one small unit. Every summary is cached by content hash and prompt version.
/// </summary>
public sealed class MapReduceSummarizer(ILlmHost host, ISummaryCache cache, LlmPriority priority = LlmPriority.Interactive)
{
    public const int ChunkSummaryMaxTokens = 90;
    public const int GroupSummaryMaxTokens = 130;
    private const int GroupInputTokens = 560;

    private readonly LlmSampling _sampling = new(Temperature: 0.1, TopP: 0.5);

    /// <summary>
    /// The digest of <paramref name="source"/> in at most about <paramref name="targetTokens"/> tokens.
    /// With <paramref name="allowGenerate"/> false only cached summaries are used and null is returned when
    /// any needed unit is missing.
    /// </summary>
    public async Task<Digest?> GetDigestAsync(
        ChatSourceDocument source,
        IReadOnlyList<TranscriptChunk> chunks,
        int targetTokens,
        bool allowGenerate,
        IProgress<string>? progress,
        CancellationToken cancellationToken)
    {
        if (chunks.Count == 0)
            return new Digest([], 0);

        // Map.
        var items = new List<DigestItem>();
        for (var i = 0; i < chunks.Count; i++)
        {
            var chunk = chunks[i];
            var text = chunk.PlainText;
            var key = $"{ChatPrompts.SummaryPromptVersion}:c:{Hash(text)}";
            var summary = cache.Get(source.RecordingId, source.VersionId, key);
            if (summary is null)
            {
                if (!allowGenerate)
                    return null;
                progress?.Report($"Summarising part {i + 1} of {chunks.Count}…");
                summary = await GenerateAsync(ChatPrompts.ChunkSummaryPrompt(text), ChunkSummaryMaxTokens, text, cancellationToken)
                    .ConfigureAwait(false);
                cache.Set(source.RecordingId, source.VersionId, key, summary);
            }
            items.Add(new DigestItem(chunk.Start, summary));
        }

        // Reduce until it fits.
        var level = 0;
        while (Measure(items, source.Alias) > targetTokens && items.Count > 1)
        {
            level++;
            var groups = GroupItems(items);
            if (groups.Count == items.Count)
                break; // cannot merge further

            var merged = new List<DigestItem>();
            for (var g = 0; g < groups.Count; g++)
            {
                var group = groups[g];
                if (group.Count == 1)
                {
                    merged.Add(group[0]);
                    continue;
                }
                var joined = string.Join("\n", group.Select(x => x.Text));
                var key = $"{ChatPrompts.SummaryPromptVersion}:g:{Hash(joined)}";
                var summary = cache.Get(source.RecordingId, source.VersionId, key);
                if (summary is null)
                {
                    if (!allowGenerate)
                        return null;
                    progress?.Report(level == 1
                        ? $"Combining summaries ({g + 1} of {groups.Count})…"
                        : $"Combining summaries, level {level} ({g + 1} of {groups.Count})…");
                    summary = await GenerateAsync(ChatPrompts.GroupSummaryPrompt(group.Select(x => x.Text)), GroupSummaryMaxTokens, joined, cancellationToken)
                        .ConfigureAwait(false);
                    cache.Set(source.RecordingId, source.VersionId, key, summary);
                }
                merged.Add(new DigestItem(group[0].Start, summary));
            }
            items = merged;
        }

        // Last resort: one item that is still too long is cut at a sentence end.
        if (items.Count == 1 && Measure(items, source.Alias) > targetTokens)
            items[0] = items[0] with { Text = CutToTokens(items[0].Text, Math.Max(20, targetTokens - 10)) };

        return new Digest(items, Measure(items, source.Alias));
    }

    private int Measure(IReadOnlyList<DigestItem> items, string alias) =>
        host.CountTokens(new Digest(items, 0).Render(alias));

    private List<List<DigestItem>> GroupItems(List<DigestItem> items)
    {
        var groups = new List<List<DigestItem>>();
        var current = new List<DigestItem>();
        var tokens = 0;
        foreach (var item in items)
        {
            var t = host.CountTokens(item.Text) + 4;
            if (current.Count >= 2 && tokens + t > GroupInputTokens)
            {
                groups.Add(current);
                current = [];
                tokens = 0;
            }
            current.Add(item);
            tokens += t;
        }
        if (current.Count > 0)
        {
            // A lone trailing item joins the previous group when that stays small.
            if (current.Count == 1 && groups.Count > 0 && groups[^1].Sum(x => host.CountTokens(x.Text) + 4) + host.CountTokens(current[0].Text) + 4 <= GroupInputTokens + 120)
                groups[^1].Add(current[0]);
            else
                groups.Add(current);
        }
        return groups;
    }

    private async Task<string> GenerateAsync(string prompt, int maxTokens, string fallbackSource, CancellationToken ct)
    {
        var request = new LlmRequest(priority, [new LlmMessage(LlmRole.User, prompt)], _sampling with { MaxOutputTokens = maxTokens });
        var answer = await host.GenerateAsync(request, ct).ConfigureAwait(false);
        return CleanSummary(answer, fallbackSource);
    }

    internal static string CleanSummary(string answer, string fallbackSource)
    {
        var text = answer.Replace("\r", " ").Replace('\n', ' ').Trim();
        foreach (var prefix in new[] { "Summary:", "Summary of the part:", "Резюме:", "Краткое содержание:" })
        {
            if (text.StartsWith(prefix, StringComparison.OrdinalIgnoreCase))
                text = text[prefix.Length..].Trim();
        }
        text = text.Replace("**", "").Replace("  ", " ");
        if (text.Length == 0)
            text = fallbackSource.Length > 300 ? fallbackSource[..300].TrimEnd() + "…" : fallbackSource;
        return text;
    }

    private string CutToTokens(string text, int maxTokens)
    {
        if (host.CountTokens(text) <= maxTokens)
            return text;
        var sentences = text.Split(['.', '!', '?'], StringSplitOptions.RemoveEmptyEntries);
        var sb = new StringBuilder();
        foreach (var s in sentences)
        {
            var next = sb.Length == 0 ? s.Trim() + "." : sb + " " + s.Trim() + ".";
            if (host.CountTokens(next) > maxTokens)
                break;
            sb.Clear().Append(next);
        }
        if (sb.Length > 0)
            return sb.ToString();
        var cut = text.Length;
        while (cut > 10 && host.CountTokens(text[..cut]) > maxTokens)
            cut = cut * 3 / 4;
        return text[..cut].TrimEnd() + "…";
    }

    private static string Hash(string text) =>
        Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(text)), 0, 8).ToLowerInvariant();
}
