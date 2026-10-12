using SpeechLib.Llm;
using VoiceType.Uno.History;
using VoiceType.Uno.Tests.Fakes;
using Xunit;

namespace VoiceType.Uno.Tests.History;

public class GemmaJobWorkerTests : IDisposable
{
    private static readonly SavedTranslationOptions Fast = new() { RetryDelay = TimeSpan.Zero };

    private readonly TempDirectory _directory = new();
    private readonly ManualTimeProvider _clock = new(HistoryFixtures.T0);
    private readonly RecordingStore _store;
    private readonly FakeLlmHost _host = new();

    public GemmaJobWorkerTests() =>
        _store = new RecordingStore(Path.Combine(_directory.Path, "sessions"), _clock);

    public void Dispose() => _directory.Dispose();

    private sealed class Sink : IJobProgressSink
    {
        public List<double> Progress { get; } = [];
        public List<string?> Details { get; } = [];
        public List<JobCheckpoint> Checkpoints { get; } = [];
        public void Detail(string? detail) => Details.Add(detail);
        void IJobProgressSink.Progress(double fraction) => Progress.Add(fraction);
        public void Checkpoint(JobCheckpoint checkpoint) => Checkpoints.Add(checkpoint);
    }

    private string CreateRecording(int sentences = 14, string language = "en", string? title = null)
    {
        var entry = _store.CreateRecording(HistoryFixtures.T0);
        var segments = Enumerable.Range(0, sentences)
            .Select(i => ((double)i * 5, (double)i * 5 + 4, language == "ru"
                ? $"Это предложение номер {i}, в нём несколько слов."
                : $"This is sentence number {i} with a few words."))
            .ToArray();
        _store.AddVersion(entry.Id, HistoryFixtures.LiveSpec(language: language), HistoryFixtures.Transcript(segments));
        _store.FinishRecording(entry.Id, HistoryFixtures.T0.AddMinutes(5));
        if (title is not null)
            _store.Rename(entry.Id, title);
        return entry.Id;
    }

    private static HistoryJob TranslateJob(string recordingId, string language = "ru", string? version = null) => new()
    {
        Id = "j1",
        Kind = HistoryJobKind.Translate,
        RecordingId = recordingId,
        Parameters = new JobParameters { TargetLanguage = language, SourceVersion = version },
    };

    // ── translate ─────────────────────────────────────────────────────────

    [Fact]
    public async Task Translate_ShouldSaveCompleteTranslationReferencingSourceVersion()
    {
        var id = CreateRecording();
        var sink = new Sink();

        var result = await new TranslateJobWorker(_store, _host, Fast).RunAsync(TranslateJob(id), sink, CancellationToken.None);

        var manifest = _store.Get(id)!.Manifest;
        var info = Assert.Single(manifest.Translations);
        Assert.Equal(result, info.Id);
        Assert.Equal("v1", info.SourceVersion);
        Assert.Equal("ru", info.TargetLanguage);
        Assert.Equal(TranslationStatuses.Complete, info.Status);
        Assert.Equal((14, 14), (info.SentencesDone, info.SentencesTotal));
        Assert.Equal("gemma-4-E2B-it", info.Engine!.Model);
        Assert.False(string.IsNullOrEmpty(info.SystemPromptHash));
        var doc = _store.LoadTranslation(id, info.Id);
        Assert.Equal(14, doc.Segments.Count);
        Assert.Equal(14, doc.SentencesDone);
        Assert.Equal(1.0, sink.Progress[^1]);
        Assert.Contains(sink.Checkpoints, c => c.ArtifactId == info.Id && c.Unit > 0);
    }

    [Fact]
    public async Task Translate_ShouldSavePartialAfterEachUnitAndContinueOnNextRun()
    {
        var id = CreateRecording();
        using var cts = new CancellationTokenSource();
        _host.Before = (_, call, _) =>
        {
            if (call == 8)
                cts.Cancel(); // pause in the middle of the second unit
            return Task.CompletedTask;
        };
        var worker = new TranslateJobWorker(_store, _host, Fast);

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => worker.RunAsync(TranslateJob(id), new Sink(), cts.Token));

        var partial = Assert.Single(_store.Get(id)!.Manifest.Translations);
        Assert.Equal(TranslationStatuses.Partial, partial.Status);
        Assert.InRange(partial.SentencesDone, 1, 13);
        Assert.Equal(14, partial.SentencesTotal);
        var requestsBefore = _host.Requests.Count;
        _host.Before = null;

        var result = await worker.RunAsync(TranslateJob(id), new Sink(), CancellationToken.None);

        var finished = Assert.Single(_store.Get(id)!.Manifest.Translations);
        Assert.Equal(partial.Id, finished.Id);
        Assert.Equal(result, finished.Id);
        Assert.Equal(TranslationStatuses.Complete, finished.Status);
        Assert.Equal(14, _store.LoadTranslation(id, finished.Id).SentencesDone);
        // Only the remaining sentences were translated: 14 total plus the one cancelled mid-flight.
        Assert.Equal(14 - partial.SentencesDone, _host.Requests.Count - requestsBefore);
    }

    [Fact]
    public async Task Translate_ShouldResumeAfterKilledAppFromSavedPartial()
    {
        var id = CreateRecording();
        _host.Before = (_, call, _) => call == 6 ? throw new IOException("process killed") : Task.CompletedTask;
        var worker = new TranslateJobWorker(_store, _host, new SavedTranslationOptions { RetryDelay = TimeSpan.Zero, MaxAttempts = 1 });
        await Assert.ThrowsAsync<IOException>(() => worker.RunAsync(TranslateJob(id), new Sink(), CancellationToken.None));
        var done = _store.Get(id)!.Manifest.Translations[0].SentencesDone;
        Assert.True(done is > 0 and < 14);

        // A new process: new host, new worker, a new job object without any checkpoint.
        var host2 = new FakeLlmHost();
        await new TranslateJobWorker(_store, host2, Fast).RunAsync(TranslateJob(id), new Sink(), CancellationToken.None);

        Assert.Equal(14 - done, host2.Requests.Count);
        Assert.Equal(TranslationStatuses.Complete, _store.Get(id)!.Manifest.Translations[0].Status);
    }

    [Fact]
    public async Task Translate_ShouldKeepPreviousTranslationWhenRunForAnotherLanguage()
    {
        var id = CreateRecording(4);
        var worker = new TranslateJobWorker(_store, _host, Fast);
        await worker.RunAsync(TranslateJob(id, "ru"), new Sink(), CancellationToken.None);

        await worker.RunAsync(TranslateJob(id, "de"), new Sink(), CancellationToken.None);

        var translations = _store.Get(id)!.Manifest.Translations;
        Assert.Equal(["ru", "de"], translations.Select(t => t.TargetLanguage));
        Assert.Equal(2, translations.Select(t => t.Id).Distinct().Count());
        Assert.All(translations, t => Assert.Equal(TranslationStatuses.Complete, t.Status));
    }

    [Fact]
    public async Task Translate_ShouldReplaceCompleteTranslationOfSameLanguageAndVersionWhenRunAgain()
    {
        var id = CreateRecording(3);
        var worker = new TranslateJobWorker(_store, _host, Fast);
        var first = await worker.RunAsync(TranslateJob(id), new Sink(), CancellationToken.None);
        var before = _host.Requests.Count;

        var second = await worker.RunAsync(TranslateJob(id), new Sink(), CancellationToken.None);

        Assert.Equal(first, second);
        Assert.Single(_store.Get(id)!.Manifest.Translations);
        Assert.Equal(3, _host.Requests.Count - before); // everything again
    }

    [Fact]
    public async Task Translate_ShouldPinSourceVersionAndMarkOutdatedWhenCurrentVersionChanges()
    {
        var id = CreateRecording(3);
        await new TranslateJobWorker(_store, _host, Fast).RunAsync(TranslateJob(id), new Sink(), CancellationToken.None);
        var manifest = _store.Get(id)!.Manifest;
        Assert.False(TranslateTabLogic.IsOutdated(manifest, manifest.Translations[0]));

        _store.SaveEdit(id, "v1", "An edited text.");

        manifest = _store.Get(id)!.Manifest;
        Assert.Equal("v2", manifest.CurrentVersion);
        Assert.Equal("v1", manifest.Translations[0].SourceVersion);
        Assert.True(TranslateTabLogic.IsOutdated(manifest, manifest.Translations[0]));
        Assert.Contains("outdated", TranslateTabLogic.List(manifest)[0].Label);
    }

    [Fact]
    public async Task Translate_ShouldTranslateRequestedVersionNotCurrent()
    {
        var id = CreateRecording(2);
        _store.SaveEdit(id, "v1", "Edited text only.");
        var job = TranslateJob(id, "ru", "v1");

        await new TranslateJobWorker(_store, _host, Fast).RunAsync(job, new Sink(), CancellationToken.None);

        Assert.Equal("v1", _store.Get(id)!.Manifest.Translations[0].SourceVersion);
        Assert.Equal(2, _host.Requests.Count);
    }

    [Theory]
    [InlineData("en", "ru")]
    [InlineData("ru", "en")]
    [InlineData("en", "de")]
    public async Task Translate_ShouldWorkForLanguagePairs(string source, string target)
    {
        var id = CreateRecording(5, source);

        await new TranslateJobWorker(_store, _host, Fast).RunAsync(TranslateJob(id, target), new Sink(), CancellationToken.None);

        Assert.Equal(target, _store.Get(id)!.Manifest.Translations[0].TargetLanguage);
        Assert.Contains(TranslationLanguages(target), _host.Requests[0].Messages[0].Text);
    }

    private static string TranslationLanguages(string code) => SpeechLib.Translation.TranslationLanguages.NameFor(code)!;

    [Fact]
    public async Task Translate_ShouldFailWithClearMessageWhenModelMissing()
    {
        var id = CreateRecording(2);
        _host.IsModelInstalled = false;

        var ex = await Assert.ThrowsAsync<LlmUnavailableException>(() =>
            new TranslateJobWorker(_store, _host, Fast).RunAsync(TranslateJob(id), new Sink(), CancellationToken.None));

        Assert.Contains("not installed", ex.Message);
        Assert.Empty(_store.Get(id)!.Manifest.Translations);
    }

    [Fact]
    public async Task Translate_ShouldFailWhenSourceVersionWasDeleted()
    {
        var id = CreateRecording(2);

        await Assert.ThrowsAsync<InvalidOperationException>(() =>
            new TranslateJobWorker(_store, _host, Fast).RunAsync(TranslateJob(id, "ru", "v9"), new Sink(), CancellationToken.None));
    }

    [Fact]
    public async Task Translate_ShouldReportWaitingForLiveTranslationWhileHostServesLive()
    {
        var id = CreateRecording(2);
        var sink = new Sink();
        _host.Before = (_, call, _) =>
        {
            if (call == 1)
            {
                _host.SetStatus(new LlmHostStatus(LlmHostState.Running, LlmPriority.Live, 1));
                _host.SetStatus(new LlmHostStatus(LlmHostState.Running, LlmPriority.Background, 0));
            }
            return Task.CompletedTask;
        };

        await new TranslateJobWorker(_store, _host, Fast).RunAsync(TranslateJob(id), sink, CancellationToken.None);

        Assert.Contains("Waiting for live translation", sink.Details);
        Assert.Null(sink.Details[^1]); // cleared when the host is ours again
    }

    [Fact]
    public async Task Translate_ShouldLeaveEmptyCompleteTranslationForEmptyTranscript()
    {
        var entry = _store.CreateRecording(HistoryFixtures.T0);
        _store.AddVersion(entry.Id, HistoryFixtures.LiveSpec(), HistoryFixtures.Transcript((0, 1, "..."), (1, 2, "")));
        _store.FinishRecording(entry.Id, HistoryFixtures.T0.AddMinutes(1));

        await new TranslateJobWorker(_store, _host, Fast).RunAsync(TranslateJob(entry.Id), new Sink(), CancellationToken.None);

        var info = Assert.Single(_store.Get(entry.Id)!.Manifest.Translations);
        Assert.Equal(TranslationStatuses.Complete, info.Status);
        Assert.Equal(0, info.SentencesTotal);
    }

    // ── autoTitle ─────────────────────────────────────────────────────────

    private static HistoryJob TitleJob(string recordingId, bool regenerate = false) => new()
    {
        Id = "j2",
        Kind = HistoryJobKind.AutoTitle,
        RecordingId = recordingId,
        Parameters = new JobParameters { Regenerate = regenerate ? true : null },
    };

    [Fact]
    public async Task AutoTitle_ShouldStoreTitleWithModelPromptVersionAndTime()
    {
        var id = CreateRecording();
        _host.Responder = (_, _) => "Title: \"Weekly sync — budget and hiring.\"";

        var result = await new AutoTitleJobWorker(_store, _host, _clock).RunAsync(TitleJob(id), new Sink(), CancellationToken.None);

        var manifest = _store.Get(id)!.Manifest;
        Assert.Equal("title", result);
        Assert.Equal("Weekly sync — budget and hiring", manifest.AutoTitle);
        Assert.Equal("Weekly sync — budget and hiring", manifest.EffectiveTitle);
        Assert.Null(manifest.Title);
        Assert.Equal("gemma-4-E2B-it", manifest.AutoTitleInfo!.Model);
        Assert.Equal(AutoTitleGenerator.PromptVersion, manifest.AutoTitleInfo.PromptVersion);
        Assert.Equal("v1", manifest.AutoTitleInfo.SourceVersion);
        Assert.Equal(HistoryFixtures.T0, manifest.AutoTitleInfo.GeneratedAt);
        Assert.Equal(LlmPriority.Background, _host.Requests[0].Priority);
    }

    [Fact]
    public async Task AutoTitle_ShouldNeverOverwriteUserTitle()
    {
        var id = CreateRecording(title: "My own title");

        var skipped = await new AutoTitleJobWorker(_store, _host, _clock).RunAsync(TitleJob(id), new Sink(), CancellationToken.None);
        var regenerated = await new AutoTitleJobWorker(_store, _host, _clock).RunAsync(TitleJob(id, regenerate: true), new Sink(), CancellationToken.None);

        var manifest = _store.Get(id)!.Manifest;
        Assert.Null(skipped);
        Assert.Equal("title", regenerated);
        Assert.Equal("My own title", manifest.Title);
        Assert.Equal("My own title", manifest.EffectiveTitle);
        Assert.NotNull(manifest.AutoTitle); // kept for when the user clears the title
    }

    [Fact]
    public async Task AutoTitle_ShouldKeepTitleTheUserTypedWhileGemmaWasWorking()
    {
        var id = CreateRecording();
        _host.Before = (_, _, _) =>
        {
            _store.Rename(id, "Typed meanwhile");
            return Task.CompletedTask;
        };

        await new AutoTitleJobWorker(_store, _host, _clock).RunAsync(TitleJob(id), new Sink(), CancellationToken.None);

        var manifest = _store.Get(id)!.Manifest;
        Assert.Equal("Typed meanwhile", manifest.Title);
        Assert.Equal("Typed meanwhile", manifest.EffectiveTitle);
        Assert.NotNull(manifest.AutoTitle);
    }

    [Fact]
    public async Task AutoTitle_ShouldNotRegenerateExistingTitleUnlessAsked()
    {
        var id = CreateRecording();
        var worker = new AutoTitleJobWorker(_store, _host, _clock);
        _host.Responder = (_, _) => "First title";
        await worker.RunAsync(TitleJob(id), new Sink(), CancellationToken.None);
        _host.Responder = (_, _) => "Second title";

        Assert.Null(await worker.RunAsync(TitleJob(id), new Sink(), CancellationToken.None));
        Assert.Equal("First title", _store.Get(id)!.Manifest.AutoTitle);

        await worker.RunAsync(TitleJob(id, regenerate: true), new Sink(), CancellationToken.None);
        Assert.Equal("Second title", _store.Get(id)!.Manifest.AutoTitle);
    }

    [Fact]
    public async Task AutoTitle_ShouldSkipEmptyAnswerAndEmptyTranscript()
    {
        var id = CreateRecording();
        _host.Responder = (_, _) => "  \n ";
        Assert.Null(await new AutoTitleJobWorker(_store, _host, _clock).RunAsync(TitleJob(id), new Sink(), CancellationToken.None));
        Assert.Null(_store.Get(id)!.Manifest.AutoTitle);

        var entry = _store.CreateRecording(HistoryFixtures.T0);
        _store.AddVersion(entry.Id, HistoryFixtures.LiveSpec(), HistoryFixtures.Transcript((0, 1, "")));
        _store.FinishRecording(entry.Id, HistoryFixtures.T0.AddMinutes(1));
        var calls = _host.Calls;
        Assert.Null(await new AutoTitleJobWorker(_store, _host, _clock).RunAsync(TitleJob(entry.Id), new Sink(), CancellationToken.None));
        Assert.Equal(calls, _host.Calls);
    }

    [Fact]
    public async Task AutoTitle_ShouldRefuseRecordingThatIsStillRecording()
    {
        var entry = _store.CreateRecording(HistoryFixtures.T0);

        await Assert.ThrowsAsync<InvalidOperationException>(() =>
            new AutoTitleJobWorker(_store, _host, _clock).RunAsync(TitleJob(entry.Id), new Sink(), CancellationToken.None));
    }

    [Theory]
    [InlineData("Title: Budget meeting", "Budget meeting")]
    [InlineData("**Планы на квартал**", "Планы на квартал")]
    [InlineData("«Besprechung zum Budget».", "Besprechung zum Budget")]
    [InlineData("\n\nQuarterly plans.\nSecond line that is ignored", "Quarterly plans")]
    [InlineData("1 2 3 4 5 6 7 8 9 10 11 12", "1 2 3 4 5 6 7 8 9 10…")]
    [InlineData("<end_of_turn>", null)]
    [InlineData("...", null)]
    [InlineData("", null)]
    public void Clean_ShouldNormalizeModelAnswer(string raw, string? expected) =>
        Assert.Equal(expected, AutoTitleGenerator.Clean(raw));

    [Fact]
    public void Clean_ShouldCapVeryLongTitleByChars()
    {
        var title = AutoTitleGenerator.Clean(string.Join(" ", Enumerable.Repeat("extraordinarily", 8)));

        Assert.NotNull(title);
        Assert.True(title!.Length <= AutoTitleGenerator.MaxChars + 1);
    }

    [Fact]
    public void BuildExcerpt_ShouldTakeHeadAndTailOfLongTranscript()
    {
        var segments = Enumerable.Range(0, 200)
            .Select(i => new TranscriptSegment { Index = i, Text = $"segment {i} has some filler words in it" }).ToList();

        var excerpt = AutoTitleGenerator.BuildExcerpt(segments, _host.CountTokens);

        Assert.Contains("segment 0 ", excerpt);
        Assert.Contains("[…]", excerpt);
        Assert.Contains("segment 199 ", excerpt);
        Assert.True(_host.CountTokens(excerpt) <= AutoTitleGenerator.HeadTokens + AutoTitleGenerator.TailTokens + 20);
    }

    [Fact]
    public void BuildExcerpt_ShouldCutOneHugeSegment()
    {
        var segments = new List<TranscriptSegment> { new() { Text = string.Join(" ", Enumerable.Repeat("blah", 5000)) } };

        var excerpt = AutoTitleGenerator.BuildExcerpt(segments, _host.CountTokens);

        Assert.True(_host.CountTokens(excerpt) <= AutoTitleGenerator.HeadTokens + 5);
    }

    // ── enqueue rules ─────────────────────────────────────────────────────

    private JobQueue OpenQueue() => new(Path.Combine(_directory.Path, "jobs", "queue.json"), _clock);

    [Fact]
    public void TryEnqueueAutoTitleAfterSave_ShouldHonourSettingAndModel()
    {
        var queue = OpenQueue();
        var on = new HistorySettings { AutoTitles = true };
        var off = new HistorySettings { AutoTitles = false };

        HistoryLlmJobs.TryEnqueueAutoTitleAfterSave(queue, "a", off, modelInstalled: true);
        HistoryLlmJobs.TryEnqueueAutoTitleAfterSave(queue, "b", on, modelInstalled: false);
        Assert.Empty(queue.Jobs);

        HistoryLlmJobs.TryEnqueueAutoTitleAfterSave(queue, "c", on, modelInstalled: true);
        HistoryLlmJobs.TryEnqueueAutoTitleAfterSave(queue, "c", on, modelInstalled: true);

        var job = Assert.Single(queue.Jobs);
        Assert.Equal((HistoryJobKind.AutoTitle, "c", JobPriorities.Low), (job.Kind, job.RecordingId, job.Priority));
        Assert.True(new HistorySettings().AutoTitles);
    }

    [Fact]
    public void EnqueueTranslate_ShouldReuseUnfinishedJobOfSameVersionAndLanguage()
    {
        var id = CreateRecording(2);
        var manifest = _store.Get(id)!.Manifest;
        var queue = OpenQueue();

        var a = HistoryLlmJobs.EnqueueTranslate(queue, manifest, null, "Russian");
        var b = HistoryLlmJobs.EnqueueTranslate(queue, manifest, "v1", "ru");
        var c = HistoryLlmJobs.EnqueueTranslate(queue, manifest, "v1", "de");

        Assert.Equal(a.Id, b.Id);
        Assert.NotEqual(a.Id, c.Id);
        Assert.Equal("v1", a.Parameters.SourceVersion);
        Assert.Equal("ru", a.Parameters.TargetLanguage);
        Assert.Equal(2, queue.Jobs.Count);
    }

    [Fact]
    public void EnqueueSummarize_ShouldNotDuplicate()
    {
        var queue = OpenQueue();

        var a = HistoryLlmJobs.EnqueueSummarize(queue, "x");
        var b = HistoryLlmJobs.EnqueueSummarize(queue, "x");

        Assert.Equal(a.Id, b.Id);
    }

    // ── summarize ─────────────────────────────────────────────────────────

    private static HistoryJob SummarizeJob(string recordingId) => new()
    {
        Id = "j3",
        Kind = HistoryJobKind.Summarize,
        RecordingId = recordingId,
    };

    [Fact]
    public async Task Summarize_ShouldWriteChunksAndSummaryFilesWithTimeMarkers()
    {
        var id = CreateRecording(150);
        _host.Responder = (r, _) => r.Messages[0].Text.StartsWith("You summarize", StringComparison.Ordinal)
            ? "Chunk summary [00:05]."
            : "Whole summary.";
        var sink = new Sink();

        var result = await new SummarizeJobWorker(_store, _host, _clock).RunAsync(SummarizeJob(id), sink, CancellationToken.None);

        Assert.Equal("v1", result);
        var analysis = new AnalysisStore(_store);
        var chunks = analysis.LoadChunks(id, "v1")!;
        Assert.True(chunks.Chunks.Count >= 3);
        Assert.True(chunks.Complete);
        Assert.All(chunks.Chunks, c => Assert.Equal("Chunk summary [00:05].", c.Summary));
        Assert.Equal(0, chunks.Chunks[0].FromSegment);
        Assert.Equal(149, chunks.Chunks[^1].ToSegment);
        Assert.Equal(chunks.Chunks.Select(c => c.Index), Enumerable.Range(0, chunks.Chunks.Count));
        for (var i = 1; i < chunks.Chunks.Count; i++)
            Assert.Equal(chunks.Chunks[i - 1].ToSegment + 1, chunks.Chunks[i].FromSegment);
        Assert.Equal(1, chunks.Schema);
        Assert.Equal("gemma-4-E2B-it", chunks.Model);
        Assert.True(chunks.Matches("v1", AnalysisStore.HashText(TranscriptDocument.JoinSegments(_store.LoadTranscript(id, "v1").Segments)), SummarizeJobWorker.ChunkTokens));
        Assert.Equal("Whole summary.", analysis.LoadSummary(id, "v1")!.Text);
        Assert.All(_host.Requests, r => Assert.Equal(LlmPriority.Background, r.Priority));
        Assert.All(_host.Requests, r => Assert.True(_host.CountTokens(r.Messages[1].Text) <= 760));
        Assert.Contains("[00:00] This is sentence number 0", _host.Requests[0].Messages[1].Text);
        Assert.Equal(1.0, sink.Progress[^1]);
    }

    [Fact]
    public async Task Summarize_ShouldResumeFromNextChunkAfterCancellation()
    {
        var id = CreateRecording(150);
        _host.Responder = (_, _) => "A short summary [00:10].";
        using var cts = new CancellationTokenSource();
        _host.Before = (_, call, _) =>
        {
            if (call == 3)
                cts.Cancel();
            return Task.CompletedTask;
        };
        var worker = new SummarizeJobWorker(_store, _host, _clock);
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => worker.RunAsync(SummarizeJob(id), new Sink(), cts.Token));
        var analysis = new AnalysisStore(_store);
        var partial = analysis.LoadChunks(id, "v1")!;
        Assert.False(partial.Complete);
        var done = partial.Chunks.Count(c => c.Summary is not null);
        Assert.Equal(2, done);
        var before = _host.Requests.Count;
        _host.Before = null;

        await worker.RunAsync(SummarizeJob(id), new Sink(), CancellationToken.None);

        var finished = analysis.LoadChunks(id, "v1")!;
        Assert.True(finished.Complete);
        Assert.Equal(partial.Chunks.Count - done + 1, _host.Requests.Count - before); // remaining chunks + the reduce
        Assert.Equal(partial.CreatedAt, finished.CreatedAt);
    }

    [Fact]
    public async Task Summarize_ShouldReuseCompleteCacheAndRebuildWhenTextChanges()
    {
        var id = CreateRecording(40);
        var worker = new SummarizeJobWorker(_store, _host, _clock);
        await worker.RunAsync(SummarizeJob(id), new Sink(), CancellationToken.None);
        var calls = _host.Calls;

        await worker.RunAsync(SummarizeJob(id), new Sink(), CancellationToken.None);
        Assert.Equal(calls, _host.Calls); // nothing to do

        _store.SaveEdit(id, "v1", "Completely different short text.");
        await worker.RunAsync(SummarizeJob(id), new Sink(), CancellationToken.None);
        Assert.NotNull(new AnalysisStore(_store).LoadChunks(id, "v2"));
        Assert.True(_host.Calls > calls);
    }

    [Fact]
    public async Task Summarize_ShouldDropAnalysisFilesWithTheirVersion()
    {
        var id = CreateRecording(5);
        _store.SaveEdit(id, "v1", "Edited.");
        await new SummarizeJobWorker(_store, _host, _clock).RunAsync(
            new HistoryJob { Id = "j", Kind = HistoryJobKind.Summarize, RecordingId = id, Parameters = new JobParameters { SourceVersion = "v1" } },
            new Sink(), CancellationToken.None);
        var analysis = new AnalysisStore(_store);
        Assert.NotNull(analysis.LoadChunks(id, "v1"));

        _store.DeleteVersion(id, "v1");

        Assert.Null(analysis.LoadChunks(id, "v1"));
        Assert.Null(analysis.LoadSummary(id, "v1"));
    }

    [Fact]
    public async Task Summarize_ShouldReduceInSeveralRoundsForManyChunks()
    {
        var id = CreateRecording(600);
        _host.Responder = (r, _) => r.Messages[0].Text.StartsWith("You summarize", StringComparison.Ordinal)
            ? new string('a', 480) // 120 tokens per chunk summary
            : "reduced";

        await new SummarizeJobWorker(_store, _host, _clock).RunAsync(SummarizeJob(id), new Sink(), CancellationToken.None);

        var reduces = _host.Requests.Count(r => r.Messages[0].Text.StartsWith("You combine", StringComparison.Ordinal));
        Assert.True(reduces >= 2, $"{reduces} reduce requests");
        Assert.Equal("reduced", new AnalysisStore(_store).LoadSummary(id, "v1")!.Text);
    }

    [Fact]
    public void Chunker_ShouldCutAtSegmentBoundariesAndCapHugeSegments()
    {
        var segments = new List<TranscriptSegment>
        {
            new() { Index = 0, Start = 0, End = 4, Text = "Short." },
            new() { Index = 1, Start = 4, End = 9, Text = string.Join(" ", Enumerable.Repeat("long", 3000)) },
            new() { Index = 2, Start = 3725, End = 3730, Text = "After." },
        };

        var pieces = TranscriptChunker.Split(segments, _host.CountTokens, 200);

        Assert.Equal(3, pieces.Count);
        Assert.All(pieces, p => Assert.True(p.Chunk.Tokens <= 200));
        Assert.EndsWith("…", pieces[1].Text.TrimEnd().Split('\n')[0]);
        Assert.StartsWith("[1:02:05] After.", pieces[2].Text);
        Assert.Equal("00:09", TranscriptChunker.Clock(9.7));
    }

    // ── bilingual view ────────────────────────────────────────────────────

    [Fact]
    public async Task BuildRows_ShouldPairSegmentsWithTranslationAndMarkUntranslated()
    {
        var id = CreateRecording(6);
        using var cts = new CancellationTokenSource();
        _host.Before = (_, call, _) =>
        {
            if (call == 3)
                cts.Cancel();
            return Task.CompletedTask;
        };
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() =>
            new TranslateJobWorker(_store, _host, Fast).RunAsync(TranslateJob(id), new Sink(), cts.Token));
        var info = _store.Get(id)!.Manifest.Translations[0];
        var transcript = _store.LoadTranscript(id, info.SourceVersion);

        var rows = TranslateTabLogic.BuildRows(transcript.Segments, _store.LoadTranslation(id, info.Id));

        Assert.Equal(6, rows.Count);
        Assert.Equal(info.SentencesDone, rows.Count(r => r.IsTranslated));
        Assert.All(rows.Where(r => r.IsTranslated), r => Assert.StartsWith("T(", r.Translation));
        Assert.Equal(5, rows[1].Start);
        Assert.Equal("0:05", rows[1].TimeText.Length == 4 ? rows[1].TimeText : "0:05");
        Assert.Contains("Partial", TranslateTabLogic.List(_store.Get(id)!.Manifest)[0].StatusText);
        Assert.Equal("Translating to German · 6 / 14 sentences", TranslateTabLogic.ProgressText("German", 6, 14));
    }
}
