using VoiceType.Uno.History;
using Xunit;

namespace VoiceType.Uno.Tests.History;

public class HistoryUiStorageTests : IDisposable
{
    private readonly TempDirectory _directory = new();

    public void Dispose() => _directory.Dispose();

    [Fact]
    public void Choices_ShouldMatchTheAgreedSettings()
    {
        Assert.Equal(["7 days", "30 days", "90 days", "180 days", "1 year", "Forever"], HistoryUiStorage.RetentionChoices.Select(c => c.Label));
        Assert.Equal([512, 1024, 2048, 5120, 10240, 0], HistoryUiStorage.StorageCapChoices.Select(c => c.Value));
        Assert.Equal([AudioQuality.Standard, AudioQuality.High, AudioQuality.Compact], HistoryUiStorage.QualityChoices.Select(c => c.Value));
        Assert.Contains("48 kbps", HistoryUiStorage.QualityChoices[0].Detail);
    }

    [Fact]
    public void IndexOf_ShouldAppendAnUnknownValue()
    {
        var choices = HistoryUiStorage.StorageCapChoices;
        Assert.Equal(2, HistoryUiStorage.IndexOf(ref choices, 2048, HistoryUiStorage.StorageCapLabel));
        Assert.Equal(6, HistoryUiStorage.IndexOf(ref choices, 3000, HistoryUiStorage.StorageCapLabel));
        Assert.Equal("2.9 GB", choices[6].Label);
        Assert.Equal(6, HistoryUiStorage.StorageCapChoices.Count);
    }

    [Fact]
    public void UsageTexts_ShouldDescribeAudioCapTextAndFreeSpace()
    {
        var usage = new HistoryStorageUsage(1_181_116_006, 100_663_296, 24);
        var settings = new HistorySettings();

        Assert.Equal("Audio 1.1 GB of 2 GB · text and chats 96 MB · 41 GB free on device",
            HistoryUiStorage.UsageText(usage, settings, 44_023_414_784));
        Assert.Equal(0.55, HistoryUiStorage.UsageFraction(usage, settings, null), 2);
        Assert.Equal("Storage · 1.2 GB of 2 GB", HistoryUiStorage.CardTitle(usage, settings));
        Assert.Equal("Audio kept 30 days, transcripts forever", HistoryUiStorage.CardDetail(settings));
        Assert.Equal("Audio kept forever, transcripts forever", HistoryUiStorage.CardDetail(new HistorySettings { AudioRetentionDays = 0 }));
        Assert.Equal("Audio 1.1 GB · text and chats 96 MB", HistoryUiStorage.UsageText(usage, new HistorySettings { MaxAudioStorageMb = 0 }, null));
    }

    [Fact]
    public void Measure_ShouldSplitAudioFromText()
    {
        _directory.Write("sessions/2026-10-12_14-02-11_aaaaaa/audio.ogg", new string('x', 100));
        _directory.Write("sessions/2026-10-12_14-02-11_aaaaaa/manifest.json", new string('x', 10));
        _directory.Write("sessions/2026-10-12_14-02-11_aaaaaa/transcripts/v1.json", new string('x', 5));
        _directory.Write("chats/c1/chat.json", new string('x', 7));

        var usage = HistoryUiStorage.Measure(Path.Combine(_directory.Path, "sessions"), Path.Combine(_directory.Path, "chats"));

        Assert.Equal(new HistoryStorageUsage(100, 22, 1), usage);
        Assert.Equal(default, HistoryUiStorage.Measure(Path.Combine(_directory.Path, "missing")));
    }
}

public class HistoryUiEditSessionTests
{
    [Fact]
    public void Set_ShouldTrackChangedSegmentsAndUndo()
    {
        var edit = new HistoryUiEditSession("v2", ["Okay let's start.", "Second line."], "v3");

        Assert.False(edit.HasChanges);
        Assert.False(edit.Set(0, "Okay let's start."));
        Assert.True(edit.Set(1, "Second  line."));
        Assert.False(edit.IsChanged(1)); // whitespace-only
        Assert.True(edit.Set(1, "Second line, edited."));
        Assert.Equal("1 segment changed", edit.ChangedText);

        Assert.Equal(1, edit.Undo());
        Assert.Equal("Second  line.", edit.Texts[1]);
        Assert.Equal(1, edit.Undo());
        Assert.Equal("Second line.", edit.Texts[1]);
        Assert.Equal(-1, edit.Undo());
        Assert.Equal("No changes", edit.ChangedText);
    }

    [Fact]
    public void Caption_ShouldNameSourceAndNextVersion()
    {
        var manifest = new RecordingManifest { Versions = [new() { Id = "v1" }, new() { Id = "v2" }] };
        var edit = new HistoryUiEditSession("v2", ["a"], HistoryUiEditSession.NextVersionIdOf(manifest));
        Assert.Equal("Editing v2 · Save creates v3 · edited; v2 stays as it is.", edit.Caption);
    }
}

public class HistoryUiExportTests
{
    private static RecordingManifest Manifest() => new()
    {
        Id = "7f3a9c",
        Title = "Weekly sync",
        StartedAt = HistoryFixtures.T0,
        Audio = new AudioInfo { File = "audio.ogg", DurationSec = 10 },
        CurrentVersion = "v1",
        Versions = [new TranscriptVersion { Id = "v1" }],
        Translations =
        [
            new TranslationInfo { Id = "t1", SourceVersion = "v1", TargetLanguage = "ru", Status = TranslationStatuses.Partial, CreatedAt = HistoryFixtures.T0 },
            new TranslationInfo { Id = "t2", SourceVersion = "v1", TargetLanguage = "de", Status = TranslationStatuses.Complete, CreatedAt = HistoryFixtures.T0 },
            new TranslationInfo { Id = "t3", SourceVersion = "v0", TargetLanguage = "fr" },
        ],
    };

    [Fact]
    public void Plan_ShouldWriteTextAndAudioWithSubtitlesUnderOneBaseName()
    {
        var transcript = HistoryFixtures.Transcript((0, 4, "Hello world"), (4, 8, "Second"));
        var files = HistoryUiExport.Plan(Manifest(), transcript, new HistoryExportOptions(true, false, true), "/x/audio.ogg", 2_400_000);

        Assert.Equal(
            ["2026-10-12 14-02 Weekly sync.txt", "2026-10-12 14-02 Weekly sync.ogg", "2026-10-12 14-02 Weekly sync.lrc", "2026-10-12 14-02 Weekly sync.srt"],
            files.Select(f => f.FileName));
        Assert.Equal("/x/audio.ogg", files[1].SourcePath);
        Assert.StartsWith("[00:00:00] Hello world", files[0].Content);
        Assert.Equal("4 files · 2.3 MB", HistoryUiExport.Summary(files));
    }

    [Fact]
    public void Plan_ShouldSkipAudioWhenThereIsNoneAndAddTheTranslation()
    {
        var transcript = HistoryFixtures.Transcript((0, 4, "Hello"));
        var translation = new TranslationDocument { Segments = [new TranslationSegment { Index = 0, Text = "Привет" }] };

        var files = HistoryUiExport.Plan(Manifest(), transcript, new HistoryExportOptions(true, true, true), null, 0, translation);

        Assert.Single(files);
        Assert.Contains("Привет", files[0].Content);
        Assert.Equal("Nothing selected", HistoryUiExport.Summary([]));
    }

    [Fact]
    public void PickTranslation_ShouldPreferCompleteOfThatVersion()
    {
        Assert.Equal("t2", HistoryUiExport.PickTranslation(Manifest(), "v1")?.Id);
        Assert.Null(HistoryUiExport.PickTranslation(Manifest(), "v9"));
    }

    [Fact]
    public void DeleteTexts_ShouldDescribeWhatGoesAndWhatStays()
    {
        var one = HistoryUiDeleteText.Recordings(1, 0, 0);
        Assert.Equal("Delete this recording?", one.Title);
        Assert.DoesNotContain("Chats", one.Body);

        var many = HistoryUiDeleteText.Recordings(3, 43_000_000, 2);
        Assert.Equal("Delete 3 recordings?", many.Title);
        Assert.StartsWith("Audio (41 MB), transcripts, versions and translations", many.Body);
        Assert.Contains("lose these sources", many.Body);

        var version = HistoryUiDeleteText.Version("v1", "v3", ["ru"], 2);
        Assert.Equal("Delete version v1?", version.Title);
        Assert.Contains("Its translation RU is deleted too.", version.Body);
        Assert.Contains("2 chats read this version; they switch to v3 (current).", version.Body);
        Assert.Equal("3 recordings deleted", HistoryUiDeleteText.Snackbar(3));
    }
}
