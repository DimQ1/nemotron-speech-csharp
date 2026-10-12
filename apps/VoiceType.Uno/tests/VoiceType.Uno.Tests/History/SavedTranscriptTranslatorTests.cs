using SpeechLib.Llm;
using VoiceType.Uno.History;
using VoiceType.Uno.Tests.Fakes;
using Xunit;

namespace VoiceType.Uno.Tests.History;

public class SavedTranscriptTranslatorTests
{
    private static readonly SavedTranslationOptions Fast = new() { RetryDelay = TimeSpan.Zero };

    private static List<TranscriptSegment> Segments(params string[] texts) =>
        texts.Select((t, i) => new TranscriptSegment { Index = i, Start = i * 5, End = i * 5 + 4, Text = t }).ToList();

    private static List<TranscriptSegment> Many(int count, string language = "en") =>
        Enumerable.Range(0, count).Select(i => new TranscriptSegment
        {
            Index = i,
            Start = i * 4,
            End = i * 4 + 3,
            Text = language == "ru" ? $"Это предложение номер {i}, и в нём есть слова." : $"This is sentence number {i} and it has words.",
        }).ToList();

    private static string UserText(LlmRequest request) => request.Messages[^1].Text;

    [Fact]
    public async Task TranslateAsync_ShouldTranslateEverySentenceAtBackgroundPriorityWithPromptAndSampling()
    {
        var host = new FakeLlmHost { Responder = (r, _) => "RU:" + UserText(r) };
        var translator = new SavedTranscriptTranslator(host, Fast);

        var doc = await translator.TranslateAsync(Segments("Hello there.", "How are you?"), "ru", "en", null, null, CancellationToken.None);

        Assert.Equal(2, host.Requests.Count);
        Assert.All(host.Requests, r => Assert.Equal(LlmPriority.Background, r.Priority));
        Assert.Equal(LlmRole.System, host.Requests[0].Messages[0].Role);
        Assert.Contains("into Russian", host.Requests[0].Messages[0].Text);
        Assert.Contains("from English", host.Requests[0].Messages[0].Text);
        Assert.NotNull(host.Requests[0].Sampling?.MaxOutputTokens);
        Assert.Equal(["RU:Hello there.", "RU:How are you?"], doc.Segments.Select(s => s.Text));
        Assert.Equal(2, doc.SentencesDone);
        Assert.Equal(2, doc.SentencesTotal);
    }

    [Fact]
    public async Task TranslateAsync_ShouldGivePreviousPairAsContextFromSecondSentence()
    {
        var host = new FakeLlmHost { Responder = (r, _) => "RU:" + UserText(r) };

        await new SavedTranscriptTranslator(host, Fast)
            .TranslateAsync(Segments("First sentence.", "Second sentence."), "ru", "en", null, null, CancellationToken.None);

        Assert.Equal(2, host.Requests[0].Messages.Count); // system + sentence
        var second = host.Requests[1].Messages;
        Assert.Equal(4, second.Count);
        Assert.Equal((LlmRole.User, "First sentence."), (second[1].Role, second[1].Text));
        Assert.Equal((LlmRole.Assistant, "RU:First sentence."), (second[2].Role, second[2].Text));
        Assert.Equal("Second sentence.", second[3].Text);
    }

    [Fact]
    public async Task TranslateAsync_ShouldKeepSegmentTimingMapForSentencesSpanningAndSharingSegments()
    {
        var host = new FakeLlmHost { Responder = (r, _) => "[" + UserText(r) + "]" };
        var segments = Segments("We need two more", "people by March. Then we hire.", "Good.");

        var doc = await new SavedTranscriptTranslator(host, Fast).TranslateAsync(segments, "de", null, null, null, CancellationToken.None);

        Assert.Equal(3, doc.SentencesTotal);
        Assert.Equal([0, 1, 2], doc.Segments.Select(s => s.Index));
        Assert.Equal("[We need two more people by March.]", doc.Segments[0].Text);
        Assert.Equal("[Then we hire.]", doc.Segments[1].Text);
        Assert.Equal("[Good.]", doc.Segments[2].Text);
        Assert.Equal([0, 1, 2], doc.Sentences!.Select(s => s.Segment));
    }

    [Fact]
    public async Task TranslateAsync_ShouldLeaveContinuationSegmentEmptyNotMissing()
    {
        var host = new FakeLlmHost();
        var segments = Segments("one two", "three four.", "Next one.");

        var doc = await new SavedTranscriptTranslator(host, Fast).TranslateAsync(segments, "ru", null, null, null, CancellationToken.None);

        Assert.Equal(3, doc.Segments.Count);
        Assert.Equal("", doc.Segments[1].Text);
        Assert.NotEqual("", doc.Segments[0].Text);
    }

    [Fact]
    public async Task TranslateAsync_ShouldSaveAfterEachUnitWithGrowingPartialDocument()
    {
        var host = new FakeLlmHost();
        var saves = new List<(TranslationDocument Doc, SavedTranslationProgress Progress)>();

        var doc = await new SavedTranscriptTranslator(host, Fast).TranslateAsync(
            Many(30), "ru", "en", null, (d, p) => saves.Add((d, p)), CancellationToken.None);

        Assert.True(saves.Count > 3 && saves.Count < 30, $"{saves.Count} units");
        Assert.Equal(30, saves[^1].Progress.SentencesDone);
        Assert.True(saves[^1].Progress.IsComplete);
        Assert.Equal(saves.Select(s => s.Progress.SentencesDone).Order(), saves.Select(s => s.Progress.SentencesDone));
        var first = saves[0];
        Assert.False(first.Progress.IsComplete);
        Assert.Equal(first.Progress.SentencesDone, first.Doc.Sentences!.Count);
        // Partial document: only segments up to the first untranslated sentence, all with text.
        Assert.True(first.Doc.Segments.Count <= first.Progress.SentencesDone + 1);
        Assert.Equal(30, doc.Segments.Count);
    }

    [Fact]
    public async Task TranslateAsync_ShouldResumeFromPartialDocumentWithoutRetranslatingDoneSentences()
    {
        var segments = Many(20);
        var firstHost = new FakeLlmHost();
        TranslationDocument? partial = null;
        using var cts = new CancellationTokenSource();
        firstHost.Before = (_, call, _) =>
        {
            if (call == 9)
                cts.Cancel();
            return Task.CompletedTask;
        };
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => new SavedTranscriptTranslator(firstHost, Fast)
            .TranslateAsync(segments, "ru", "en", null, (d, _) => partial = d, cts.Token));
        Assert.NotNull(partial);
        var done = partial!.SentencesDone;
        Assert.InRange(done, 1, 19);

        var secondHost = new FakeLlmHost { Responder = (r, _) => "T(" + UserText(r) + ")" };
        var final = await new SavedTranscriptTranslator(secondHost, Fast)
            .TranslateAsync(segments, "ru", "en", partial, null, CancellationToken.None);

        Assert.Equal(20 - done, secondHost.Requests.Count);
        Assert.Equal(20, final.SentencesDone);
        Assert.Equal(partial.Sentences!.Select(s => s.Text), final.Sentences!.Take(done).Select(s => s.Text));
        // The first request after resume still sees the last done pair as context.
        Assert.Equal(partial.Sentences![done - 1].Text, secondHost.Requests[0].Messages[2].Text);
    }

    [Fact]
    public async Task TranslateAsync_ShouldKeepSentencesFinishedInsideCancelledUnit()
    {
        var host = new FakeLlmHost();
        TranslationDocument? saved = null;
        using var cts = new CancellationTokenSource();
        host.Before = (_, call, _) =>
        {
            if (call == 2) // second sentence of the first unit
                cts.Cancel();
            return Task.CompletedTask;
        };

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => new SavedTranscriptTranslator(host, Fast)
            .TranslateAsync(Many(12), "ru", "en", null, (d, _) => saved = d, cts.Token));

        Assert.NotNull(saved);
        Assert.Equal(1, saved!.SentencesDone);
    }

    [Fact]
    public async Task TranslateAsync_ShouldRestartWhenPartialDoesNotMatchPlan()
    {
        var segments = Many(6);
        var stale = new TranslationDocument
        {
            SentencesDone = 2,
            SentencesTotal = 99, // plan changed
            Sentences = [new() { Segment = 0, Text = "x" }, new() { Segment = 1, Text = "y" }],
        };
        var host = new FakeLlmHost();

        var doc = await new SavedTranscriptTranslator(host, Fast).TranslateAsync(segments, "ru", "en", stale, null, CancellationToken.None);

        Assert.Equal(6, host.Requests.Count);
        Assert.Equal(6, doc.SentencesDone);
    }

    [Fact]
    public async Task TranslateAsync_ShouldRerunRequestWhenHostCancelsItWithoutJobCancellation()
    {
        // Contract: the host re-runs a preempted Background request itself. If it surfaces the preemption as an
        // exception instead, the translator asks again and loses no more than that request.
        var host = new FakeLlmHost();
        host.Before = (_, call, _) => call == 2 ? throw new OperationCanceledException("preempted by live translation") : Task.CompletedTask;

        var doc = await new SavedTranscriptTranslator(host, Fast).TranslateAsync(Many(3), "ru", "en", null, null, CancellationToken.None);

        Assert.Equal(4, host.Requests.Count);
        Assert.Equal(3, doc.SentencesDone);
    }

    [Fact]
    public async Task TranslateAsync_ShouldRetryTransientFailureThenSucceed()
    {
        var host = new FakeLlmHost();
        host.Before = (_, call, _) => call <= 2 ? throw new InvalidOperationException("engine hiccup") : Task.CompletedTask;

        var doc = await new SavedTranscriptTranslator(host, Fast).TranslateAsync(Many(1), "ru", "en", null, null, CancellationToken.None);

        Assert.Equal(3, host.Requests.Count);
        Assert.Equal(1, doc.SentencesDone);
    }

    [Fact]
    public async Task TranslateAsync_ShouldFailAfterAttemptsButSaveFinishedWork()
    {
        var host = new FakeLlmHost();
        host.Before = (_, call, _) => call >= 3 ? throw new InvalidOperationException("broken") : Task.CompletedTask;
        TranslationDocument? saved = null;

        await Assert.ThrowsAsync<InvalidOperationException>(() => new SavedTranscriptTranslator(host, Fast)
            .TranslateAsync(Many(6), "ru", "en", null, (d, _) => saved = d, CancellationToken.None));

        Assert.Equal(2, saved!.SentencesDone);
        Assert.Equal(2 + 3, host.Requests.Count); // two sentences + three attempts of the failing one
    }

    [Fact]
    public async Task TranslateAsync_ShouldNotRetryWhenModelIsMissing()
    {
        var host = new FakeLlmHost { IsModelInstalled = false };

        await Assert.ThrowsAsync<LlmUnavailableException>(() => new SavedTranscriptTranslator(host, Fast)
            .TranslateAsync(Many(2), "ru", "en", null, null, CancellationToken.None));

        Assert.Empty(host.Requests);
    }

    [Fact]
    public async Task TranslateAsync_ShouldCleanLabelsQuotesAndFences()
    {
        var host = new FakeLlmHost();
        var answers = new Queue<string>(["Перевод: «Привет, мир.»", "```\nСпасибо.\n```", "{\"translation\": \"Пока.\"}"]);
        host.Responder = (_, _) => answers.Dequeue();

        var doc = await new SavedTranscriptTranslator(host, Fast)
            .TranslateAsync(Segments("Hello, world.", "Thanks.", "Bye."), "ru", "en", null, null, CancellationToken.None);

        Assert.Equal(["Привет, мир.", "Спасибо.", "Пока."], doc.Segments.Select(s => s.Text));
    }

    [Fact]
    public async Task TranslateAsync_ShouldStripEchoOfPreviousContext()
    {
        var host = new FakeLlmHost();
        var call = 0;
        host.Responder = (_, _) => ++call == 1 ? "Привет." : "Привет. Как дела?";

        var doc = await new SavedTranscriptTranslator(host, Fast)
            .TranslateAsync(Segments("Hello.", "How are you?"), "ru", "en", null, null, CancellationToken.None);

        Assert.Equal("Как дела?", doc.Segments[1].Text);
    }

    [Fact]
    public async Task TranslateAsync_ShouldFallBackToSourceForEmptyOrRunawayOutput()
    {
        var host = new FakeLlmHost();
        host.Responder = (r, call) => UserText(r) == "Empty one." ? "" : new string('я', 5000);

        var doc = await new SavedTranscriptTranslator(host, Fast)
            .TranslateAsync(Segments("Empty one.", "Runaway one."), "ru", "en", null, null, CancellationToken.None);

        Assert.Equal("Empty one.", doc.Segments[0].Text);
        Assert.Equal("Runaway one.", doc.Segments[1].Text);
    }

    [Fact]
    public async Task TranslateAsync_ShouldRetryRunawayWithoutContext()
    {
        var host = new FakeLlmHost();
        host.Responder = (r, _) => r.Messages.Count == 4 ? new string('я', 5000) : "ok:" + UserText(r);

        var doc = await new SavedTranscriptTranslator(host, Fast)
            .TranslateAsync(Segments("First.", "Second."), "ru", "en", null, null, CancellationToken.None);

        Assert.Equal("ok:Second.", doc.Segments[1].Text);
        Assert.Equal(3, host.Requests.Count);
        Assert.Equal(2, host.Requests[2].Messages.Count);
    }

    [Fact]
    public async Task TranslateAsync_ShouldPassThroughTextAlreadyInTargetLanguage()
    {
        var host = new FakeLlmHost();

        var doc = await new SavedTranscriptTranslator(host, Fast)
            .TranslateAsync(Many(3, "ru"), "ru", "ru", null, null, CancellationToken.None);

        Assert.Empty(host.Requests);
        Assert.Equal(3, doc.SentencesDone);
        Assert.Equal("Это предложение номер 1, и в нём есть слова.", doc.Segments[1].Text);
    }

    [Theory]
    [InlineData("ru", "Russian")]
    [InlineData("de", "German")]
    [InlineData("en", "English")]
    public async Task TranslateAsync_ShouldNameTargetLanguageInPrompt(string code, string name)
    {
        var host = new FakeLlmHost();

        await new SavedTranscriptTranslator(host, Fast)
            .TranslateAsync(Segments("Ein Satz."), code, null, null, null, CancellationToken.None);

        Assert.Contains($"into {name}", host.Requests[0].Messages[0].Text);
    }

    [Fact]
    public async Task TranslateAsync_ShouldHandleEmptyTranscript()
    {
        var host = new FakeLlmHost();
        TranslationDocument? saved = null;

        var doc = await new SavedTranscriptTranslator(host, Fast)
            .TranslateAsync(Segments("", "  "), "ru", "en", null, (d, _) => saved = d, CancellationToken.None);

        Assert.Empty(host.Requests);
        Assert.Equal(0, doc.SentencesTotal);
        Assert.NotNull(saved);
    }

    [Fact]
    public async Task TranslateAsync_ShouldTranslateVeryLongSegmentInPieces()
    {
        var text = string.Join(" ", Enumerable.Range(0, 500).Select(i => "word" + i));
        var host = new FakeLlmHost();

        var doc = await new SavedTranscriptTranslator(host, Fast).TranslateAsync(Segments(text), "ru", "en", null, null, CancellationToken.None);

        Assert.True(host.Requests.Count >= 3);
        Assert.All(host.Requests, r => Assert.True(host.CountTokens(UserText(r)) <= 200));
        Assert.Single(doc.Segments);
        Assert.Equal(host.Requests.Count, doc.SentencesDone);
    }

    [Fact]
    public async Task TranslateAsync_ShouldRejectBlankTargetLanguage()
    {
        await Assert.ThrowsAsync<ArgumentException>(() => new SavedTranscriptTranslator(new FakeLlmHost(), Fast)
            .TranslateAsync(Segments("Hi."), "auto", null, null, null, CancellationToken.None));
    }
}
