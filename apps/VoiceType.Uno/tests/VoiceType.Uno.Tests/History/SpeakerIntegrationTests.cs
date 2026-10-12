using SpeechLib.Agents;
using VoiceType.Uno.History;
using VoiceType.Uno.Services;
using Xunit;

namespace VoiceType.Uno.Tests.History;

/// <summary>Speaker names in exports, search, chat and settings; the service over store and queue.</summary>
public class SpeakerIntegrationTests : IDisposable
{
    private readonly SpeakerFixtures _f = new();
    private readonly JobQueue _queue;

    public SpeakerIntegrationTests() =>
        _queue = new JobQueue(Path.Combine(_f.Directory.Path, "jobs", "queue.json"), _f.Clock);

    public void Dispose() => _f.Dispose();

    /// <summary>Four segments, Anna (p1, even segments) and Mark (p2, odd ones), layer made current.</summary>
    private string LabelledRecording()
    {
        var id = _f.CreateRecording(segments: 4, decoder: true);
        var run = _f.AddRun(id, 16, SpeakerFixtures.Alternating(4));
        _f.Label(id, run.Id);
        _f.Store.RenamePerson(id, "p1", "Anna");
        _f.Store.RenamePerson(id, "p2", "Mark");
        return id;
    }

    private IReadOnlyDictionary<int, string> Names(string id) => SpeakerLabels.ForVersion(_f.Store, id, "v1")!;

    // ── Exports ─────────────────────────────────────────────────────────

    [Fact]
    public void ToText_ShouldPrefixEverySegmentWithItsSpeaker()
    {
        var id = LabelledRecording();
        var transcript = _f.Store.LoadTranscript(id, "v1");

        var text = TranscriptExport.ToText(transcript, speakers: Names(id));

        var lines = text.Split('\n', StringSplitOptions.RemoveEmptyEntries);
        Assert.Equal("[00:00:00] Anna: w0x0 w0x1 w0x2 w0x3 w0x4 w0x5 w0x6 w0x7 w0x8 w0x9", lines[0]);
        Assert.StartsWith("[00:00:04] Mark: w1x0", lines[1]);
        Assert.StartsWith("[00:00:08] Anna: ", lines[2]);
    }

    [Fact]
    public void ToText_ShouldStayThePlainFormatWithoutSpeakers()
    {
        var id = LabelledRecording();
        var transcript = _f.Store.LoadTranscript(id, "v1");

        Assert.Equal(TranscriptExport.ToText(transcript), TranscriptExport.ToText(transcript, speakers: null));
        Assert.StartsWith("[00:00:00] w0x0", TranscriptExport.ToText(transcript));
    }

    [Fact]
    public void ToSrtAndToLrc_ShouldPrefixTheName()
    {
        var id = LabelledRecording();
        var transcript = _f.Store.LoadTranscript(id, "v1");

        var srt = TranscriptExport.ToSrt(transcript, speakers: Names(id));
        var lrc = TranscriptExport.ToLrc(transcript, "Title", 16, Names(id));

        Assert.Contains("00:00:00,000 --> 00:00:03,600\nAnna: w0x0", srt);
        Assert.Contains("00:00:04,000 --> 00:00:07,600\nMark: w1x0", srt);
        Assert.Contains("[00:00.00]Anna: w0x0", lrc);
        Assert.Contains("[00:04.00]Mark: w1x0", lrc);
    }

    [Fact]
    public void ToMarkdown_ShouldBoldTimeAndSpeaker()
    {
        var id = LabelledRecording();
        var manifest = _f.Store.Get(id)!.Manifest;
        manifest.Title = "Weekly sync";

        var markdown = TranscriptExport.ToMarkdown(manifest, _f.Store.LoadTranscript(id, "v1"), speakers: Names(id));

        Assert.StartsWith("# Weekly sync\n", markdown);
        Assert.Contains("**[00:00:00] Anna:** w0x0", markdown);
        Assert.Contains("**[00:00:04] Mark:** w1x0", markdown);
        Assert.DoesNotContain("Speaker", TranscriptExport.ToMarkdown(manifest, _f.Store.LoadTranscript(id, "v1")));
    }

    [Fact]
    public void ExportPlan_ShouldUseSpeakerNamesOnlyWhenAskedAndAvailable()
    {
        var id = LabelledRecording();
        var manifest = _f.Store.Get(id)!.Manifest;
        var transcript = _f.Store.LoadTranscript(id, "v1");
        var folder = _f.Store.TryGetFolder(id)!;
        File.WriteAllBytes(Path.Combine(folder, "audio.wav"), new byte[10]);
        var audio = _f.Store.GetAudioPath(id);

        var withNames = HistoryUiExport.Plan(manifest, transcript, new HistoryExportOptions(true, false, true, SpeakerNames: true, Markdown: true), audio, 10, speakerNames: Names(id));
        var without = HistoryUiExport.Plan(manifest, transcript, new HistoryExportOptions(true, false, true), audio, 10, speakerNames: Names(id));
        var noLayer = HistoryUiExport.Plan(manifest, transcript, new HistoryExportOptions(true, false, true, SpeakerNames: true), audio, 10, speakerNames: null);

        Assert.Equal(5, withNames.Count); // txt, md, audio, lrc, srt
        Assert.All(withNames.Where(f => f.Content is not null), f => Assert.Contains("Anna", f.Content));
        Assert.All(without.Where(f => f.Content is not null), f => Assert.DoesNotContain("Anna", f.Content));
        Assert.All(noLayer.Where(f => f.Content is not null), f => Assert.DoesNotContain("Anna", f.Content));
        Assert.EndsWith(".md", withNames[1].FileName);
    }

    [Fact]
    public void SpeakerLabels_ShouldFollowRenamesMergesAndOverrides()
    {
        var id = LabelledRecording();
        _f.Store.AddSpeakerOverride(id, 8, 12, "p2");
        Assert.Equal("Mark", Names(id)[2]);

        _f.Store.MergePeople(id, "p1", "p2");

        Assert.All(Names(id).Values, n => Assert.Equal("Anna", n));
        Assert.Null(SpeakerLabels.ForVersion(_f.Store, id, "v9"));
    }

    // ── Search ──────────────────────────────────────────────────────────

    [Fact]
    public void Search_ShouldFindARecordingByTheNameOfASpeakerAndPointAtTheirSegment()
    {
        var id = LabelledRecording();
        var documents = HistorySearch.BuildIndex(_f.Store);

        var hits = HistorySearch.Search(documents, "mark");

        var hit = Assert.Single(hits);
        Assert.True(hit.MatchedIn.HasFlag(SearchField.Speaker));
        Assert.Equal(1, hit.Segment!.Index);
        Assert.Equal("Mark", hit.Segment.Speaker);
        Assert.Equal(2, hit.Document.SpeakerCount);
        Assert.Equal(["Anna", "Mark"], hit.Document.SpeakerNames);
        Assert.Empty(HistorySearch.Search(documents, "nobody"));
        // A name and a word of the text together.
        Assert.Single(HistorySearch.Search(documents, "anna w0x3"));
        Assert.Equal(id, hit.Document.RecordingId);
    }

    [Fact]
    public void Search_ShouldFilterRecordingsWithSpeakers()
    {
        LabelledRecording();
        _f.CreateRecording();
        var documents = HistorySearch.BuildIndex(_f.Store);

        Assert.Single(HistorySearch.Search(documents, null, new HistoryFilter { HasSpeakers = true }));
        Assert.Single(HistorySearch.Search(documents, null, new HistoryFilter { HasSpeakers = false }));
        Assert.Equal(2, HistorySearch.Search(documents, null).Count);
    }

    [Fact]
    public void Search_ShouldNotTreatDefaultSpeakerNamesAsSearchableNames()
    {
        var id = _f.CreateRecording(segments: 4);
        var run = _f.AddRun(id, 16, SpeakerFixtures.Alternating(4));
        _f.Label(id, run.Id);
        var documents = HistorySearch.BuildIndex(_f.Store);

        Assert.Empty(HistorySearch.Search(documents, "speaker"));
        Assert.Equal(2, documents[0].SpeakerCount);
    }

    // ── Chat ────────────────────────────────────────────────────────────

    [Fact]
    public void ChatSources_ShouldCarryTheSpeakerOnEveryLineAndTheSystemMessageSaysSo()
    {
        var id = LabelledRecording();
        var chats = new ChatStore(Path.Combine(_f.Directory.Path, "chats"));
        var chat = chats.Create("t", id, "v1");
        var sources = new ChatSourceCatalog(chat.Id, chats, _f.Store).GetSources();

        var lines = sources[0].LoadLines();

        Assert.Equal(["Anna", "Mark", "Anna", "Mark"], lines.Select(l => l.Speaker));
        Assert.StartsWith("[S1 00:04] Mark: w1x0", SpeechLib.Agents.TranscriptChunker.RenderLine("S1", lines[1]));
        var system = ChatPrompts.BuildSystemMessage(sources);
        Assert.Contains("speaker name", system);
        Assert.DoesNotContain("without speaker labels", system);
        Assert.Contains("When transcript lines start with a speaker name", ChatPrompts.QuickPrompts.Single(p => p.Id == "speakers").Prompt);
    }

    [Fact]
    public void ChatSources_ShouldKeepTheOldRuleWithoutSpeakersAndRefreshAfterARename()
    {
        var plain = _f.CreateRecording(segments: 2);
        var chats = new ChatStore(Path.Combine(_f.Directory.Path, "chats"));
        var plainSources = new ChatSourceCatalog(chats.Create("t", plain, "v1").Id, chats, _f.Store).GetSources();
        Assert.Contains("without speaker labels", ChatPrompts.BuildSystemMessage(plainSources));
        Assert.All(plainSources[0].LoadLines(), l => Assert.Null(l.Speaker));

        var id = LabelledRecording();
        var catalog = new ChatSourceCatalog(chats.Create("t2", id, "v1").Id, chats, _f.Store);
        Assert.Equal("Anna", catalog.GetSources()[0].LoadLines()[0].Speaker);
        _f.Store.RenamePerson(id, "p1", "Olga");
        Assert.Equal("Olga", catalog.GetSources()[0].LoadLines()[0].Speaker);
    }

    [Fact]
    public void Citations_ShouldCarryTheSpeakerAndShowItInTheChipAndMarkdown()
    {
        var id = LabelledRecording();
        var chats = new ChatStore(Path.Combine(_f.Directory.Path, "chats"));
        var chat = chats.Create("t", id, "v1");
        var parsed = CitationParser.Parse("Mark asked something [S1 00:04].", new ChatSourceCatalog(chat.Id, chats, _f.Store).GetSources());

        var citation = Assert.Single(parsed.Citations);
        Assert.Equal("Mark", citation.Speaker);

        var stored = new ChatCitation { RecordingId = id, StartSec = 4, Speaker = "Mark" };
        Func<string, string?> title = _ => "Weekly sync";
        Assert.Equal("Weekly sync · Mark 00:04", ChatUiFormat.CitationLabel(stored, title));
        Assert.Equal("Mark 00:04", ChatUiFormat.CitationLabel(stored, title, singleRecording: true));
        Assert.Equal("Weekly sync 00:04", ChatUiFormat.CitationLabel(new ChatCitation { RecordingId = id, StartSec = 4 }, title));
        var manifests = new Dictionary<string, RecordingManifest> { [id] = _f.Store.Get(id)!.Manifest };
        Assert.Equal("[Recording " + id + " · Mark 00:04]", ChatExport.FormatCitation(stored, new Dictionary<string, RecordingManifest>()));
        Assert.Contains("· Mark 00:04]", ChatExport.FormatCitation(stored, manifests));
    }

    // ── Settings ────────────────────────────────────────────────────────

    [Fact]
    public void SpeakerSettings_ShouldDefaultToBestOnDesktopWithModelAndOffOnPhones()
    {
        var settings = new HistorySettings().Speakers;

        Assert.Equal(SpeakerAutoMode.Best, settings.Effective(isPhone: false, modelInstalled: true));
        Assert.Equal(SpeakerAutoMode.Off, settings.Effective(isPhone: false, modelInstalled: false));
        Assert.Equal(SpeakerAutoMode.Off, settings.Effective(isPhone: true, modelInstalled: true));
        settings.AutoAfterStop = SpeakerAutoMode.Best;
        Assert.Equal(SpeakerAutoMode.Best, settings.Effective(isPhone: true, modelInstalled: true));
        settings.AutoAfterStop = SpeakerAutoMode.Off;
        Assert.Equal(SpeakerAutoMode.Off, settings.Effective(isPhone: false, modelInstalled: true));
        Assert.Equal(4, new SpeakerSettings().EffectiveThreads(false));
        Assert.Equal(2, new SpeakerSettings().EffectiveThreads(true));
        Assert.Equal(3, new SpeakerSettings { Threads = 3 }.EffectiveThreads(true));
    }

    [Fact]
    public void Settings_ShouldRoundTripSpeakerAndReprocessBlocks()
    {
        var path = Path.Combine(_f.Directory.Path, "settings.json");
        var settings = new AppSettings();
        settings.History.Speakers.AutoAfterStop = SpeakerAutoMode.Best;
        settings.History.Speakers.Threads = 3;
        settings.History.Reprocess.AutoNew = true;
        settings.History.Reprocess.Profile.AsrModelFolder = "parakeet-fp32";
        settings.History.Reprocess.AutoCondition.WindowStart = new TimeOnly(2, 30);
        new SettingsService(path).Save(settings);

        var loaded = new SettingsService(path).Load().History;

        Assert.Equal(SpeakerAutoMode.Best, loaded.Speakers.AutoAfterStop);
        Assert.Equal(3, loaded.Speakers.Threads);
        Assert.True(loaded.Reprocess.AutoNew);
        Assert.Equal("parakeet-fp32", loaded.Reprocess.Profile.AsrModelFolder);
        Assert.Equal(new TimeOnly(2, 30), loaded.Reprocess.AutoCondition.WindowStart);
        Assert.True(loaded.Reprocess.AutoCondition.RequireCharging);
        Assert.Equal(20, loaded.Reprocess.MinDurationSec);
    }

    [Fact]
    public void Settings_ShouldLoadOldFilesAndCloneDeeply()
    {
        _f.Directory.Write("old.json", """{ "History": { "AudioRetentionDays": 7 } }""");

        var loaded = new SettingsService(Path.Combine(_f.Directory.Path, "old.json")).Load().History;

        Assert.Null(loaded.Speakers.AutoAfterStop);
        Assert.False(loaded.Reprocess.AutoNew);
        var clone = loaded.Clone();
        clone.Speakers.Threads = 9;
        clone.Reprocess.Profile.Analyze = false;
        clone.Reprocess.AutoCondition.RequireCharging = false;
        Assert.Equal(0, loaded.Speakers.Threads);
        Assert.True(loaded.Reprocess.Profile.Analyze);
        Assert.True(loaded.Reprocess.AutoCondition.RequireCharging);
    }

    // ── Service ─────────────────────────────────────────────────────────

    private SpeakerService Service(ReprocessChain? chain = null, bool installed = true) =>
        new(_f.Store, _queue, new Stub(installed), () => _settings, poke: () => Pokes++);

    private readonly HistorySettings _settings = new();
    public int Pokes;

    private sealed class Stub(bool installed) : IDiarizerFactory
    {
        public DiarizerModelRef? Find(string? preferredFolder = null) =>
            installed ? new DiarizerModelRef(@"C:\m\nemotron-3-diarization-onnx-int8", "Nemotron 3 Diarization INT8", "Int8", 105_852_942) : null;

        public IJobDiarizer Create(DiarizerModelRef model, DiarizerRunOptions options) => throw new NotSupportedException();
    }

    [Fact]
    public void ModelStatus_ShouldSayWhetherTheModelIsInstalled()
    {
        Assert.True(Service().IsModelAvailable);
        Assert.Equal("Nemotron 3 Diarization INT8 · 106 MB", Service().ModelStatus);
        Assert.False(Service(installed: false).IsModelAvailable);
        Assert.Equal("Model not installed", Service(installed: false).ModelStatus);
    }

    [Fact]
    public void GetLayer_ShouldReturnTheCurrentLayerAsPeople()
    {
        var id = LabelledRecording();

        var view = Service().GetLayer(id, "v1")!;

        Assert.Equal("s1", view.LayerId);
        Assert.Equal("r1", view.RunId);
        Assert.Equal(["Anna", "Mark"], view.People.Select(p => p.Name));
        Assert.Equal(4, view.Segments.Count);
        Assert.Equal(["p1", "p2", "p1", "p2"], view.Segments.Select(s => s.PersonId));
        Assert.All(view.Segments, s => Assert.Equal(10, s.WordPersonIds.Count));
        Assert.Equal([50.0, 50.0], view.Stats.Select(s => s.Percent));
        Assert.Null(Service().GetLayer(id, "v7"));
        Assert.Null(Service().GetLayer("nothing", "v1"));
    }

    [Fact]
    public void EnqueueDiarize_ShouldQueueOnceAndRunNowUpgradesTheCondition()
    {
        var id = _f.CreateRecording();
        var service = Service();

        var background = service.EnqueueDiarize(id, "v1", runNow: false);
        var duplicate = service.EnqueueDiarize(id, "v1", runNow: true);

        Assert.NotNull(background);
        Assert.Null(duplicate);
        var job = Assert.Single(_queue.Jobs);
        Assert.Equal(HistoryJobKind.Diarize, job.Kind);
        Assert.Equal("v1", job.Parameters.SourceVersion);
        Assert.Equal(JobConditionKind.Now, job.Condition.Kind); // upgraded by the second call
        Assert.Equal(2, Pokes);
    }

    [Fact]
    public void EnqueueDiarize_ShouldWaitForIdleInTheBackground()
    {
        var id = _f.CreateRecording();

        Service().EnqueueDiarize(id, "v1", runNow: false);

        var job = Assert.Single(_queue.Jobs);
        Assert.Equal(JobConditionKind.WhenIdle, job.Condition.Kind);
        Assert.Equal(JobPriorities.Low, job.Priority);
        Assert.Equal(ReprocessOrigins.LiveFallback, job.Parameters.Origin);
    }

    [Fact]
    public void EnqueueDiarize_ShouldReturnNullWithoutModelAudioOrVersion()
    {
        var id = _f.CreateRecording();
        var noAudio = _f.Store.CreateRecording(HistoryFixtures.T0).Id;

        Assert.Null(Service(installed: false).EnqueueDiarize(id, "v1", true));
        Assert.Null(Service().EnqueueDiarize(id, "v9", true));
        Assert.Null(Service().EnqueueDiarize(noAudio, "v1", true));
        Assert.Null(Service().EnqueueDiarize("missing", "v1", true));
        Assert.Empty(_queue.Jobs);
    }

    [Fact]
    public void PeopleEdits_ShouldGoThroughTheStoreAndRaiseChanged()
    {
        var id = LabelledRecording();
        var service = Service();
        var changed = new List<string>();
        service.Changed += changed.Add;

        service.RenamePerson(id, "p1", "Olga");
        service.SetPersonColor(id, "p2", 5);
        service.MergePeople(id, "p1", "p2");
        service.SplitPerson(id, "p1");
        service.RenamePerson(id, "p9", "nobody"); // gone: ignored, no throw

        var people = _f.Store.Get(id)!.Manifest.Speakers!.People;
        Assert.Equal(["Olga", "Mark"], people.Select(p => p.Name));
        Assert.Equal(5, people[1].Color);
        Assert.True(changed.Count >= 4);
        Assert.All(changed, c => Assert.Equal(id, c));
    }

    [Fact]
    public async Task ANewCurrentVersion_ShouldGetItsLayerFromTheLatestRunWithoutTheModel()
    {
        var id = LabelledRecording();
        using var service = new DisposableService(Service());

        _f.Store.SaveEdit(id, "v1", Enumerable.Range(0, 4).Select(i => $"edited text number {i}").ToList());

        for (var i = 0; i < 100 && _f.Store.Get(id)!.Manifest.CurrentSpeakerLayer("v2") is null; i++)
            await Task.Delay(50);
        var layer = _f.Store.Get(id)!.Manifest.CurrentSpeakerLayer("v2");
        Assert.NotNull(layer);
        Assert.Equal("r1", layer!.Run);
        Assert.Equal("p1", service.Inner.GetLayer(id, "v2")!.Segments[0].PersonId);
    }

    private sealed class DisposableService(SpeakerService inner) : IDisposable
    {
        public SpeakerService Inner => inner;
        public void Dispose() { }
    }

    [Fact]
    public void AfterSave_ShouldQueueDiarizationOnlyWhenBestModelAndLongEnough()
    {
        var id = _f.CreateRecording(segments: 10); // 40 s
        var shortOne = _f.CreateRecording(segments: 2); // 8 s
        var service = Service();

        Assert.True(SpeakerAutomation.TryEnqueueAfterSave(service, null, _f.Store, id, _settings, isPhone: false));
        Assert.False(SpeakerAutomation.TryEnqueueAfterSave(service, null, _f.Store, shortOne, _settings, isPhone: false));
        Assert.False(SpeakerAutomation.TryEnqueueAfterSave(service, null, _f.Store, id, _settings, isPhone: true)); // phones: Off by default
        Assert.False(SpeakerAutomation.TryEnqueueAfterSave(Service(installed: false), null, _f.Store, shortOne, _settings, isPhone: false));
        _settings.Speakers.AutoAfterStop = SpeakerAutoMode.Off;
        Assert.False(SpeakerAutomation.TryEnqueueAfterSave(service, null, _f.Store, _f.CreateRecording(segments: 10), _settings, isPhone: false));

        Assert.Single(_queue.Jobs);
    }
}
