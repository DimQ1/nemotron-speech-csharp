using VoiceType.Uno.History;
using VoiceType.Uno.Tests.Fakes;
using Xunit;

namespace VoiceType.Uno.Tests.History;

public class RecordingStoreTests : IDisposable
{
    private readonly TempDirectory _directory = new();
    private readonly ManualTimeProvider _clock = new(HistoryFixtures.T0);
    private readonly RecordingStore _store;

    public RecordingStoreTests() =>
        _store = new RecordingStore(Path.Combine(_directory.Path, "sessions"), _clock);

    public void Dispose() => _directory.Dispose();

    private RecordingEntry CreateFinished(DateTimeOffset? startedAt = null, string text = "Okay let's start the meeting")
    {
        var entry = _store.CreateRecording(startedAt ?? HistoryFixtures.T0);
        _store.AddVersion(entry.Id, HistoryFixtures.LiveSpec(), HistoryFixtures.Transcript((0, 3, text)));
        _store.FinishRecording(entry.Id, (startedAt ?? HistoryFixtures.T0).AddMinutes(1));
        return _store.Get(entry.Id)!;
    }

    [Fact]
    public void CreateRecording_ShouldCreateTimestampedFolderWithLockAndManifest()
    {
        var entry = _store.CreateRecording(HistoryFixtures.T0, new RecordingMode { InjectText = true });

        var name = Path.GetFileName(entry.FolderPath);
        Assert.Matches("^2026-10-12_14-02-11_[0-9a-f]{6}$", name);
        Assert.EndsWith(entry.Id, name);
        Assert.True(File.Exists(Path.Combine(entry.FolderPath, RecordingStore.LockFileName)));
        Assert.True(File.Exists(Path.Combine(entry.FolderPath, RecordingStore.ManifestFileName)));
        Assert.Equal(RecordingStates.Recording, entry.Manifest.State);
        Assert.True(_store.Get(entry.Id)!.Manifest.Mode.InjectText);
    }

    [Fact]
    public void FinishRecording_ShouldCompleteAndRemoveLock()
    {
        var entry = _store.CreateRecording(HistoryFixtures.T0);

        var manifest = _store.FinishRecording(entry.Id, HistoryFixtures.T0.AddMinutes(5),
            new AudioInfo { File = "audio.ogg", Codec = "opus", Bytes = 10 });

        Assert.Equal(RecordingStates.Complete, manifest.State);
        Assert.Equal(HistoryFixtures.T0.AddMinutes(5), manifest.EndedAt);
        Assert.False(File.Exists(Path.Combine(entry.FolderPath, RecordingStore.LockFileName)));
        Assert.Equal("audio.ogg", manifest.Audio!.File);
    }

    [Fact]
    public void AddVersion_ShouldWriteTranscriptAndFillStatsAndExcerpt()
    {
        var entry = _store.CreateRecording(HistoryFixtures.T0);
        var spec = HistoryFixtures.LiveSpec();
        spec.Stats = new VersionStats { Rtf = 0.2 };

        var version = _store.AddVersion(entry.Id, spec,
            HistoryFixtures.Transcript((0, 2, "Hello world."), (2, 4, "Second part here.")));

        Assert.Equal("v1", version.Id);
        Assert.Equal("transcripts/v1.json", version.File);
        Assert.Equal(5, version.Stats!.Words);
        Assert.Equal(2, version.Stats.Segments);
        Assert.Equal(0.2, version.Stats.Rtf);
        Assert.Equal("Hello world. Second part here.", version.Excerpt);
        Assert.Equal(HistoryFixtures.T0, version.CreatedAt);
        Assert.Equal("v1", _store.Get(entry.Id)!.Manifest.CurrentVersion);
        var loaded = _store.LoadTranscript(entry.Id, "v1");
        Assert.Equal("v1", loaded.Version);
        Assert.Equal("Hello world. Second part here.", loaded.Text);
    }

    [Fact]
    public void AddVersion_ShouldNotMoveCurrentWhenAskedNotTo()
    {
        var entry = CreateFinished();

        var v2 = _store.AddVersion(entry.Id, new TranscriptVersion { Kind = VersionKinds.Rerecognized },
            HistoryFixtures.Transcript((0, 3, "other")), makeCurrent: false);

        Assert.Equal("v2", v2.Id);
        Assert.Equal("v1", _store.Get(entry.Id)!.Manifest.CurrentVersion);
    }

    [Fact]
    public void LoadAll_ShouldSkipAndReportCorruptedFoldersAndSortNewestFirst()
    {
        var older = CreateFinished(HistoryFixtures.T0);
        var newer = CreateFinished(HistoryFixtures.T0.AddHours(1));
        var sessions = _store.SessionsDir;
        var broken = Path.Combine(sessions, "2026-10-01_10-00-00_badbad");
        Directory.CreateDirectory(broken);
        File.WriteAllText(Path.Combine(broken, "manifest.json"), "{ not json");
        Directory.CreateDirectory(Path.Combine(sessions, "random-folder"));

        var result = new RecordingStore(sessions).LoadAll();

        Assert.Equal([newer.Id, older.Id], result.Recordings.Select(r => r.Id));
        Assert.Equal(2, result.Errors.Count);
        Assert.Contains(result.Errors, e => e.Path == broken);
    }

    [Fact]
    public void LoadAll_ShouldReturnEmptyWhenFolderMissing()
    {
        var result = new RecordingStore(Path.Combine(_directory.Path, "nope")).LoadAll();

        Assert.Empty(result.Recordings);
        Assert.Empty(result.Errors);
    }

    [Fact]
    public void Rename_ShouldSetAndClearUserTitle()
    {
        var entry = CreateFinished(text: "one two three four five six seven eight nine ten");

        Assert.Equal("Weekly sync", _store.Rename(entry.Id, "  Weekly sync ").EffectiveTitle);
        Assert.Equal("one two three four five six seven eight…", _store.Rename(entry.Id, " ").EffectiveTitle);
    }

    [Fact]
    public void SetAutoTitle_ShouldNeverOverrideUserTitle()
    {
        var entry = CreateFinished();
        _store.Rename(entry.Id, "Mine");

        var manifest = _store.SetAutoTitle(entry.Id, "Gemma title");

        Assert.Equal("Mine", manifest.Title);
        Assert.Equal("Gemma title", manifest.AutoTitle);
        Assert.Equal("Mine", manifest.EffectiveTitle);
        Assert.Equal("Gemma title", _store.Rename(entry.Id, null).EffectiveTitle);
    }

    [Fact]
    public void SaveEdit_ShouldCreateEditedVersionWithParentAndMakeItCurrent()
    {
        var entry = _store.CreateRecording(HistoryFixtures.T0);
        _store.AddVersion(entry.Id, HistoryFixtures.LiveSpec(),
            HistoryFixtures.Transcript((0, 4, "meeting moves to nine"), (5, 7, "thanks all")));

        var edited = _store.SaveEdit(entry.Id, "v1", ["meeting moves to ten", "thanks all"]);

        Assert.Equal("v2", edited.Id);
        Assert.Equal(VersionKinds.Edited, edited.Kind);
        Assert.Equal("v1", edited.Parent);
        Assert.Equal("parakeet-int4", edited.Model!.Folder);
        var manifest = _store.Get(entry.Id)!.Manifest;
        Assert.Equal("v2", manifest.CurrentVersion);
        var original = _store.LoadTranscript(entry.Id, "v1");
        var copy = _store.LoadTranscript(entry.Id, "v2");
        Assert.Equal("meeting moves to nine thanks all", original.Text);
        Assert.Equal("meeting moves to ten thanks all", copy.Text);
        Assert.Equal(original.Segments[0].Words[0].Start, copy.Segments[0].Words[0].Start);
        Assert.Equal(original.Segments[1].Words[1].End, copy.Segments[1].Words[1].End);
        Assert.Equal(original.Segments[0].Words[3].Start, copy.Segments[0].Words[3].Start);
    }

    [Fact]
    public void SetCurrentVersion_ShouldSwitchBackAndRejectUnknown()
    {
        var entry = CreateFinished();
        _store.SaveEdit(entry.Id, "v1", "edited text");

        Assert.Equal("v1", _store.SetCurrentVersion(entry.Id, "v1").CurrentVersion);
        Assert.Throws<KeyNotFoundException>(() => _store.SetCurrentVersion(entry.Id, "v9"));
    }

    [Fact]
    public void DeleteVersion_ShouldRefuseTheCurrentVersion()
    {
        var entry = CreateFinished();

        Assert.Throws<InvalidOperationException>(() => _store.DeleteVersion(entry.Id, "v1"));
        Assert.Single(_store.Get(entry.Id)!.Manifest.Versions);
    }

    [Fact]
    public void DeleteVersion_ShouldDeleteDependentTranslationsAndFiles()
    {
        var entry = CreateFinished();
        _store.SaveEdit(entry.Id, "v1", "edited text");
        var t1 = _store.SaveTranslation(entry.Id, new TranslationInfo { SourceVersion = "v1", TargetLanguage = "ru" },
            new TranslationDocument { Segments = [new TranslationSegment { Index = 0, Text = "Привет" }] });
        var t2 = _store.SaveTranslation(entry.Id, new TranslationInfo { SourceVersion = "v2", TargetLanguage = "de" },
            new TranslationDocument());

        Assert.Equal([t1.Id], _store.GetDependentTranslations(entry.Id, "v1").Select(t => t.Id));
        var deletion = _store.DeleteVersion(entry.Id, "v1");

        Assert.Equal([t1.Id], deletion.DeletedTranslationIds);
        var manifest = _store.Get(entry.Id)!.Manifest;
        Assert.Equal(["v2"], manifest.Versions.Select(v => v.Id));
        Assert.Null(manifest.Versions[0].Parent);
        Assert.Equal([t2.Id], manifest.Translations.Select(t => t.Id));
        Assert.False(File.Exists(Path.Combine(entry.FolderPath, "transcripts", "v1.json")));
        Assert.False(File.Exists(Path.Combine(entry.FolderPath, t1.File)));
    }

    [Fact]
    public void SaveTranslation_ShouldNameFileAndUpdatePartialInPlace()
    {
        var entry = CreateFinished();

        var partial = _store.SaveTranslation(entry.Id,
            new TranslationInfo { SourceVersion = "v1", TargetLanguage = "ru", Status = TranslationStatuses.Partial },
            new TranslationDocument { SentencesDone = 1, SentencesTotal = 2 });
        partial.Status = TranslationStatuses.Complete;
        var complete = _store.SaveTranslation(entry.Id, partial, new TranslationDocument { SentencesDone = 2, SentencesTotal = 2 });

        Assert.Equal("t1", partial.Id);
        Assert.Equal("translations/t1.ru.v1.json", partial.File);
        Assert.Equal(partial.File, complete.File);
        var manifest = _store.Get(entry.Id)!.Manifest;
        Assert.Equal(TranslationStatuses.Complete, Assert.Single(manifest.Translations).Status);
        Assert.Equal(2, _store.LoadTranslation(entry.Id, "t1").SentencesDone);
    }

    [Fact]
    public void DeleteAudio_ShouldRemoveAudioFilesKeepTextAndSetRemovedAt()
    {
        var entry = CreateFinished();
        File.WriteAllBytes(Path.Combine(entry.FolderPath, "audio.ogg"), new byte[100]);
        _store.Update(entry.Id, m => m.Audio = new AudioInfo { File = "audio.ogg", Bytes = 100 });
        _clock.Advance(TimeSpan.FromDays(1));

        Assert.Equal(100, RecordingStore.MeasureAudioBytes(entry.FolderPath));
        Assert.NotNull(_store.GetAudioPath(entry.Id));
        Assert.True(_store.DeleteAudio(entry.Id));

        var manifest = _store.Get(entry.Id)!.Manifest;
        Assert.Equal(HistoryFixtures.T0.AddDays(1), manifest.Audio!.RemovedAt);
        Assert.False(manifest.HasAudio);
        Assert.Null(_store.GetAudioPath(entry.Id));
        Assert.False(File.Exists(Path.Combine(entry.FolderPath, "audio.ogg")));
        Assert.NotNull(_store.LoadCurrentTranscript(entry.Id));
    }

    [Fact]
    public void DeleteRecording_ShouldRemoveTheFolder()
    {
        var entry = CreateFinished();

        _store.DeleteRecording(entry.Id);

        Assert.False(Directory.Exists(entry.FolderPath));
        Assert.Null(_store.Get(entry.Id));
        Assert.Throws<KeyNotFoundException>(() => _store.Rename(entry.Id, "x"));
    }

    [Fact]
    public void RecoverIncomplete_ShouldFinalizePartAudioAndMarkRecovered()
    {
        var entry = _store.CreateRecording(HistoryFixtures.T0);
        File.WriteAllBytes(Path.Combine(entry.FolderPath, "audio.ogg.part"), new byte[64]);
        var finalized = new List<string>();

        var result = new RecordingStore(_store.SessionsDir).RecoverIncomplete(
            finalizeAudio: (part, final) => { finalized.Add(Path.GetFileName(part)); File.Move(part, final); return true; });

        Assert.Equal([Path.GetFileName(entry.FolderPath)], result.Recovered);
        Assert.Equal(["audio.ogg.part"], finalized);
        var manifest = _store.Get(entry.Id)!.Manifest;
        Assert.Equal(RecordingStates.Recovered, manifest.State);
        Assert.Equal("audio.ogg", manifest.Audio!.File);
        Assert.Equal("opus", manifest.Audio.Codec);
        Assert.Equal(64, manifest.Audio.Bytes);
        Assert.NotNull(manifest.EndedAt);
        Assert.False(File.Exists(Path.Combine(entry.FolderPath, RecordingStore.LockFileName)));
    }

    [Fact]
    public void RecoverIncomplete_ShouldMarkIncompleteWhenNothingSurvivedAndSkipActive()
    {
        var empty = _store.CreateRecording(HistoryFixtures.T0);
        var active = _store.CreateRecording(HistoryFixtures.T0.AddMinutes(1));
        var done = CreateFinished(HistoryFixtures.T0.AddMinutes(2));

        var result = _store.RecoverIncomplete(new HashSet<string> { active.Id });

        Assert.Equal([Path.GetFileName(empty.FolderPath)], result.MarkedIncomplete);
        Assert.Empty(result.Recovered);
        Assert.Equal(RecordingStates.Incomplete, _store.Get(empty.Id)!.Manifest.State);
        Assert.Equal(RecordingStates.Recording, _store.Get(active.Id)!.Manifest.State);
        Assert.Equal(RecordingStates.Complete, _store.Get(done.Id)!.Manifest.State);
    }

    [Fact]
    public void RecoverIncomplete_ShouldCreateManifestForFolderWithOnlyPartialAudio()
    {
        var folder = Path.Combine(_store.SessionsDir, "2026-10-11_09-30-00_abcdef");
        Directory.CreateDirectory(folder);
        File.WriteAllBytes(Path.Combine(folder, "audio.wav.part"), new byte[10]);

        var result = _store.RecoverIncomplete();

        Assert.Single(result.Recovered);
        var manifest = _store.Get("abcdef")!.Manifest;
        Assert.Equal(RecordingStates.Recovered, manifest.State);
        Assert.Equal("audio.wav", manifest.Audio!.File);
        Assert.Equal(new DateTime(2026, 10, 11, 9, 30, 0), manifest.StartedAt.DateTime);
    }

    [Fact]
    public void RecoverIncomplete_ShouldReportCorruptedManifestWithoutTouchingIt()
    {
        var folder = Path.Combine(_store.SessionsDir, "2026-10-11_09-30-00_abcdef");
        Directory.CreateDirectory(folder);
        File.WriteAllText(Path.Combine(folder, "manifest.json"), "garbage");

        var result = _store.RecoverIncomplete();

        Assert.Single(result.Errors);
        Assert.Equal("garbage", File.ReadAllText(Path.Combine(folder, "manifest.json")));
    }

    [Fact]
    public void Mutations_ShouldKeepEveryConcurrentChange()
    {
        var entry = CreateFinished();

        Parallel.For(0, 20, i =>
        {
            if (i % 2 == 0)
                _store.AddVersion(entry.Id, new TranscriptVersion { Kind = VersionKinds.Rerecognized },
                    HistoryFixtures.Transcript((0, 1, "text " + i)), makeCurrent: false);
            else
                _store.Update(entry.Id, m => m.Audio = new AudioInfo { File = "audio.ogg", Bytes = i });
        });

        var manifest = _store.Get(entry.Id)!.Manifest;
        Assert.Equal(11, manifest.Versions.Count);
        Assert.Equal(11, manifest.Versions.Select(v => v.Id).Distinct().Count());
        Assert.All(manifest.Versions, v => Assert.True(File.Exists(Path.Combine(entry.FolderPath, v.File))));
        Assert.Empty(Directory.EnumerateFiles(entry.FolderPath, "*.tmp", SearchOption.AllDirectories));
    }

    [Fact]
    public void Changed_ShouldFireWithRecordingId()
    {
        var ids = new List<string>();
        _store.Changed += ids.Add;

        var entry = _store.CreateRecording(HistoryFixtures.T0);
        _store.Rename(entry.Id, "x");

        Assert.Equal([entry.Id, entry.Id], ids);
    }
}
