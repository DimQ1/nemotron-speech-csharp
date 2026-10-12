using System.Text;
using System.Text.RegularExpressions;
using SpeechLib.Llm;
using SpeechLib.Translation;

namespace SpeechLib.Agents;

/// <summary>One line of a digest: the summary of the part of the recording that starts at <see cref="Start"/>.</summary>
public sealed record DigestItem(double Start, string Text);

public sealed partial record Digest(IReadOnlyList<DigestItem> Items, int Tokens)
{
    /// <summary>
    /// <c>[S1 03:20] text</c> per item. Stored summaries carry bare <c>[mm:ss]</c> markers (they belong to the
    /// recording, not to a chat); here they get the chat's alias so they are citable.
    /// </summary>
    public string Render(string alias) =>
        string.Join("\n", Items.Select(i => $"[{alias} {TimeText.Format(i.Start)}] {BareMarker().Replace(i.Text, $"[{alias} $1]")}"));

    [GeneratedRegex(@"\[(\d{1,2}:\d{2}(?::\d{2})?)\]")]
    private static partial Regex BareMarker();
}

/// <summary>Progress of <see cref="MapReduceSummarizer.EnsureAsync"/>: units done of the total (chunks + the reduce).</summary>
public sealed record SummaryUnitProgress(int Done, int Total);

/// <summary>
/// The single map-reduce summary pipeline of a recording, shared by the Background <c>summarize</c> job and by
/// chat. Map: every <see cref="SummaryDefaults.ChunkTokens"/>-token chunk becomes 2-4 sentences, saved to the
/// <see cref="ISummaryStore"/> the moment it is done. Reduce: the chunk summaries are merged in batches into one
/// summary of the whole recording, also saved. Every unit is one small generation (about 650 prompt tokens), so
/// a preemption by live translation wastes at most one unit, and a cancelled run resumes at the next missing
/// unit, whoever runs it. The job runs it at <see cref="LlmPriority.Background"/>, chat at Interactive.
/// </summary>
public sealed class MapReduceSummarizer(ILlmHost host, ISummaryStore store, LlmPriority priority = LlmPriority.Interactive)
{
    private const int Attempts = 3;

    /// <summary>Pause between attempts of a failed unit (tests set it to zero).</summary>
    public TimeSpan RetryDelay { get; init; } = TimeSpan.FromMilliseconds(200);

    /// <summary>
    /// Makes every summary of the target exist: missing chunk summaries (one unit each), then the final summary.
    /// Present ones are reused, so a finished target costs no generation at all.
    /// </summary>
    /// <param name="onUnit">Called after every unit this run generated.</param>
    public async Task EnsureAsync(SummaryTarget target, Action<SummaryUnitProgress>? onUnit, CancellationToken cancellationToken)
    {
        var chunks = store.OpenChunks(target, host.CountTokens);
        var summaries = await MapAsync(target, chunks, null, i => onUnit?.Invoke(new SummaryUnitProgress(i + 1, chunks.Count + 1)), cancellationToken)
            .ConfigureAwait(false);
        if (chunks.Count > 0 && store.LoadSummary(target) is null)
            await ReduceAndSaveAsync(target, summaries, null, cancellationToken).ConfigureAwait(false);
    }

    /// <summary>
    /// The digest of <paramref name="source"/> in at most about <paramref name="targetTokens"/> tokens: the chunk
    /// summaries when they fit, else the summary of the whole recording. Stored summaries are used first; with
    /// <paramref name="allowGenerate"/> the missing units are generated and stored (status text goes to
    /// <paramref name="progress"/>), without it null is returned when anything is missing.
    /// </summary>
    public async Task<Digest?> GetDigestAsync(
        ChatSourceDocument source,
        int targetTokens,
        bool allowGenerate,
        IProgress<string>? progress,
        CancellationToken cancellationToken)
    {
        var target = SummaryTarget.From(source);
        var chunks = store.OpenChunks(target, host.CountTokens);
        if (chunks.Count == 0)
            return new Digest([], 0);

        var summaries = chunks.Select(c => c.Summary).ToList();
        if (summaries.All(s => !string.IsNullOrWhiteSpace(s)) && TryItems(chunks, summaries!, source.Alias, targetTokens) is { } fitting)
            return fitting;

        var whole = store.LoadSummary(target);
        if (whole is null)
        {
            if (!allowGenerate)
                return null;
            var mapped = await MapAsync(target, chunks, (i, n) => progress?.Report($"Summarising part {i + 1} of {n}…"), null, cancellationToken)
                .ConfigureAwait(false);
            if (TryItems(chunks, mapped, source.Alias, targetTokens) is { } fits)
                return fits;
            whole = await ReduceAndSaveAsync(target, mapped, progress, cancellationToken).ConfigureAwait(false);
        }

        var items = new List<DigestItem> { new(chunks[0].Start, whole) };
        if (Measure(items, source.Alias) > targetTokens)
            items[0] = items[0] with { Text = CutToTokens(whole, Math.Max(20, targetTokens - 10)) };
        return new Digest(items, Measure(items, source.Alias));
    }

    private Digest? TryItems(IReadOnlyList<SummaryChunk> chunks, IReadOnlyList<string?> summaries, string alias, int targetTokens)
    {
        var items = new List<DigestItem>();
        for (var i = 0; i < chunks.Count; i++)
        {
            if (string.IsNullOrWhiteSpace(summaries[i]))
                return null;
            items.Add(new DigestItem(chunks[i].Start, summaries[i]!));
        }
        var tokens = Measure(items, alias);
        return tokens <= targetTokens || chunks.Count == 1 ? new Digest(items, tokens) : null;
    }

    /// <summary>Summaries of every chunk, generating (and saving) the missing ones one by one.</summary>
    private async Task<List<string>> MapAsync(
        SummaryTarget target,
        IReadOnlyList<SummaryChunk> chunks,
        Action<int, int>? starting,
        Action<int>? generated,
        CancellationToken cancellationToken)
    {
        var result = new List<string>(chunks.Count);
        for (var i = 0; i < chunks.Count; i++)
        {
            var summary = chunks[i].Summary;
            if (string.IsNullOrWhiteSpace(summary))
            {
                // Another run (the job, or a chat) may have finished this chunk meanwhile.
                summary = store.GetChunkSummary(target, i);
                if (string.IsNullOrWhiteSpace(summary))
                {
                    cancellationToken.ThrowIfCancellationRequested();
                    starting?.Invoke(i, chunks.Count);
                    summary = await GenerateAsync(SummaryDefaults.MapPrompt, chunks[i].Text, SummaryDefaults.MapMaxOutputTokens, cancellationToken)
                        .ConfigureAwait(false);
                    store.SaveChunkSummary(target, i, summary);
                    generated?.Invoke(i);
                }
            }
            result.Add(summary!);
        }
        return result;
    }

    private async Task<string> ReduceAndSaveAsync(SummaryTarget target, List<string> pieces, IProgress<string>? progress, CancellationToken cancellationToken)
    {
        progress?.Report("Combining summaries…");
        var text = await ReduceAsync(pieces, cancellationToken).ConfigureAwait(false);
        store.SaveSummary(target, text, pieces.Count);
        return text;
    }

    /// <summary>Reduces partial summaries in batches of <see cref="SummaryDefaults.ReduceBatchTokens"/> until one is left.</summary>
    private async Task<string> ReduceAsync(List<string> pieces, CancellationToken cancellationToken)
    {
        if (pieces.Count == 0)
            return "";
        while (pieces.Count > 1)
        {
            var next = new List<string>();
            var batch = new List<string>();
            var tokens = 0;

            async Task FlushAsync()
            {
                if (batch.Count == 0)
                    return;
                next.Add(batch.Count == 1
                    ? batch[0]
                    : await GenerateAsync(SummaryDefaults.ReducePrompt, string.Join("\n", batch), SummaryDefaults.ReduceMaxOutputTokens, cancellationToken)
                        .ConfigureAwait(false));
                batch.Clear();
                tokens = 0;
            }

            foreach (var piece in pieces)
            {
                var cost = host.CountTokens(piece);
                if (batch.Count > 0 && tokens + cost > SummaryDefaults.ReduceBatchTokens)
                    await FlushAsync().ConfigureAwait(false);
                batch.Add(piece);
                tokens += cost;
            }
            await FlushAsync().ConfigureAwait(false);
            if (next.Count >= pieces.Count)
                break; // nothing merged (each piece alone fills a batch): stop instead of looping
            pieces = next;
        }
        return string.Join(" ", pieces);
    }

    private int Measure(IReadOnlyList<DigestItem> items, string alias) =>
        host.CountTokens(new Digest(items, 0).Render(alias));

    private async Task<string> GenerateAsync(string system, string user, int maxOutputTokens, CancellationToken cancellationToken)
    {
        var request = new LlmRequest(priority,
            [new LlmMessage(LlmRole.System, system), new LlmMessage(LlmRole.User, user)],
            new LlmSampling(Temperature: 0.2, TopP: 0.5, MaxOutputTokens: maxOutputTokens));
        for (var attempt = 1; ; attempt++)
        {
            try
            {
                var text = TranslationOutputCleaner.Clean(await host.GenerateAsync(request, cancellationToken).ConfigureAwait(false));
                text = text.Replace("**", "");
                if (text.Length == 0)
                    throw new InvalidOperationException("Gemma returned an empty summary.");
                return text;
            }
            catch (LlmUnavailableException)
            {
                throw;
            }
            catch (Exception) when (!cancellationToken.IsCancellationRequested && attempt < Attempts)
            {
                await Task.Delay(RetryDelay, cancellationToken).ConfigureAwait(false);
            }
        }
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
}
