using System.Runtime.CompilerServices;
using SpeechLib.Agents;
using SpeechLib.Llm;

namespace SpeechLib.Agents.Tests;

/// <summary>Scripted <see cref="ILlmHost"/>: records every request, answers from a function.</summary>
public sealed class FakeLlmHost : ILlmHost
{
    private readonly object _gate = new();

    public List<LlmRequest> Requests { get; } = [];
    public Func<LlmRequest, string> Responder { get; set; } = _ => "OK";
    public bool Installed { get; set; } = true;
    public int ContextTokensValue { get; set; } = 4096;
    public List<string> Released { get; } = [];

    /// <summary>When set, the stream stops after this many pieces and waits for cancellation.</summary>
    public int? HangAfterPieces { get; set; }

    public bool IsModelInstalled => Installed;
    public int ContextTokens => ContextTokensValue;
    public LlmHostStatus Status => new(Installed ? LlmHostState.Idle : LlmHostState.Unavailable, null, 0);
    public event Action<LlmHostStatus>? StatusChanged { add { } remove { } }

    /// <summary>Roughly one token per 3.2 characters.</summary>
    public int CountTokens(string text) => (int)Math.Ceiling(text.Length / 3.2);

    public void ReleaseConversation(string conversationKey)
    {
        lock (_gate)
            Released.Add(conversationKey);
    }

    public async IAsyncEnumerable<string> GenerateStreamAsync(LlmRequest request, [EnumeratorCancellation] CancellationToken cancellationToken = default)
    {
        if (!Installed)
            throw new LlmUnavailableException("The Gemma model is not installed.");
        lock (_gate)
            Requests.Add(request);
        var answer = Responder(request);
        var pieces = answer.Split(' ').Select((w, i) => i == 0 ? w : " " + w).ToList();
        var n = 0;
        foreach (var piece in pieces)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (HangAfterPieces is { } limit && n >= limit)
            {
                await Task.Delay(Timeout.Infinite, cancellationToken);
            }
            await Task.Yield();
            n++;
            yield return piece;
        }
    }

    public async Task<string> GenerateAsync(LlmRequest request, CancellationToken cancellationToken = default)
    {
        var sb = new System.Text.StringBuilder();
        await foreach (var piece in GenerateStreamAsync(request, cancellationToken))
            sb.Append(piece);
        return sb.ToString();
    }

    public ValueTask DisposeAsync() => ValueTask.CompletedTask;
}

public sealed class FakeCatalog : IChatSourceCatalog
{
    public List<ChatSourceDocument> Sources { get; } = [];

    public IReadOnlyList<ChatSourceDocument> GetSources() => Sources.ToList();
}

public static class Transcripts
{
    private static readonly string[] s_en =
    [
        "Welcome everyone to the weekly sync about the budget and hiring plan.",
        "Cloud costs are five percent over budget this quarter because of the new analytics cluster.",
        "Maria will send the updated forecast to finance by Friday.",
        "We decided to move the next meeting to ten o'clock on Monday.",
        "Does anyone know whether QA has the build yet?",
        "I think so, if QA gets the build by Friday we can release next week.",
        "The hiring plan needs two more backend engineers before summer.",
        "Support tickets about login errors doubled after the last release.",
        "Peter agreed to investigate the login errors and report on Wednesday.",
        "Let us close here, thank you all for joining.",
    ];

    private static readonly string[] s_ru =
    [
        "Добрый день, обсуждаем сроки поставки оборудования для нового склада.",
        "Поставщик сообщил, что поставка сдвигается на две недели из-за задержки на таможне.",
        "Ёлки и гирлянды для офиса заказали отдельно, их привезут в пятницу.",
        "Иван должен подтвердить новую дату до среды.",
        "Кто отвечает за приёмку товара на складе?",
        "Приёмку организует Ольга, она подготовит список проверок.",
        "Мы договорились созвониться снова в понедельник утром.",
    ];

    public static List<TranscriptLine> English(int repeat = 1, double secondsPerLine = 9)
    {
        var lines = new List<TranscriptLine>();
        var t = 0.0;
        for (var r = 0; r < repeat; r++)
        {
            for (var i = 0; i < s_en.Length; i++)
            {
                // Later repeats get a distinct tail so BM25 has something to rank.
                var text = r == 0 ? s_en[i] : s_en[i] + $" (part {r + 1}, item {i + 1}, topic{r * 100 + i})";
                lines.Add(new TranscriptLine(t, t + secondsPerLine - 0.5, text));
                t += secondsPerLine;
            }
        }
        return lines;
    }

    public static List<TranscriptLine> Russian(double secondsPerLine = 8)
    {
        var lines = new List<TranscriptLine>();
        var t = 0.0;
        foreach (var text in s_ru)
        {
            lines.Add(new TranscriptLine(t, t + secondsPerLine - 0.5, text));
            t += secondsPerLine;
        }
        return lines;
    }

    public static ChatSourceDocument Source(string alias, string recordingId, IReadOnlyList<TranscriptLine> lines, bool active = true, string? title = null) =>
        new(alias, recordingId, "v1", title ?? "Recording " + alias, new DateTimeOffset(2026, 10, 12, 12, 34, 0, TimeSpan.Zero),
            lines.Count == 0 ? 0 : lines[^1].End, active, () => lines);
}
