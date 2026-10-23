using VoiceType.Uno.Services;
using Xunit;

namespace VoiceType.Uno.Tests.Settings;

public class StorageFolderTextTests
{
    [Fact]
    public void Badge_ShouldSayDefaultOrCustom()
    {
        var sessions = Path.Combine(Path.GetTempPath(), "Data", "sessions");
        Assert.Equal("Default", StorageFolderText.Badge(sessions, sessions + Path.DirectorySeparatorChar));
        Assert.Equal("Custom", StorageFolderText.Badge(Path.Combine(Path.GetTempPath(), "Rec"), sessions));
    }

    [Fact]
    public void Titles_AndQuestions_ShouldNameTheKind()
    {
        Assert.Equal("Move models to the new folder?", StorageFolderText.QuestionTitle(StorageFolderKind.Models));
        Assert.Equal("Move recordings to the new folder?", StorageFolderText.QuestionTitle(StorageFolderKind.Recordings));
        Assert.Equal("Move app data to the new folder?", StorageFolderText.QuestionTitle(StorageFolderKind.Data));
        Assert.Equal("Recordings", StorageFolderText.Title(StorageFolderKind.Recordings));
        Assert.Equal("App data", StorageFolderText.Title(StorageFolderKind.Data));
    }

    [Fact]
    public void Subject_ShouldCountWhatMoves()
    {
        Assert.Equal("4 models (3.00 GB)", StorageFolderText.Subject(StorageFolderKind.Models, 4, 3L << 30));
        Assert.Equal("24 recordings (1.1 GB)", StorageFolderText.Subject(StorageFolderKind.Recordings, 24, (long)(1.1 * (1L << 30))));
        Assert.Equal("App data (96 MB)", StorageFolderText.Subject(StorageFolderKind.Data, 0, 96L << 20));
    }

    [Fact]
    public void Question_SameDrive_ShouldOfferTheRestartMove()
    {
        var parts = StorageFolderText.Question(StorageFolderKind.Recordings, "24 recordings (1.1 GB)", @"C:\old", @"C:\new", FolderRelation.SameDrive);

        Assert.Equal("24 recordings (1.1 GB) are in", parts[0].Text);
        Assert.Equal((@"C:\old", true), (parts[1].Text, parts[1].IsPath));
        Assert.Equal((@"C:\new", true), (parts[3].Text, parts[3].IsPath));
        Assert.Contains("leave History until you choose the old folder again", parts[4].Text);
    }

    [Fact]
    public void Question_OtherDrive_ShouldExplainThereIsNoAutomaticCopy()
    {
        var models = StorageFolderText.Question(StorageFolderKind.Models, "4 models (3.00 GB)", @"C:\m", @"E:\m", FolderRelation.OtherDrive);
        var recordings = StorageFolderText.Question(StorageFolderKind.Recordings, "2 recordings (1 MB)", @"C:\r", @"E:\r", FolderRelation.OtherDrive);

        Assert.Equal("a different drive than", models[2].Text);
        Assert.Contains("not copied between drives automatically", models[4].Text);
        Assert.Contains("(asr, llm, diarization)", models[4].Text);
        Assert.Contains("Recordings are not copied between drives automatically", recordings[4].Text);
    }

    [Fact]
    public void Question_Data_UsesIsAndNoKeepChoice()
    {
        var parts = StorageFolderText.Question(StorageFolderKind.Data, "App data (96 MB)", @"C:\d", @"D:\d", FolderRelation.SameDrive);

        Assert.Equal("App data (96 MB) is in", parts[0].Text);
        Assert.Contains("Models and recordings stay in their own folders", parts[4].Text);
        Assert.False(StorageFolderText.OffersKeep(StorageFolderKind.Data, FolderRelation.SameDrive));
    }

    [Fact]
    public void Question_Nested_IsOnlyAWarning()
    {
        var parts = StorageFolderText.Question(StorageFolderKind.Models, "x", "a", "b", FolderRelation.Nested);

        Assert.Equal(StorageFolderText.NestedWarning, Assert.Single(parts).Text);
        Assert.False(StorageFolderText.OffersMove(StorageFolderKind.Models, FolderRelation.Nested));
        Assert.False(StorageFolderText.OffersKeep(StorageFolderKind.Models, FolderRelation.Nested));
    }

    [Theory]
    [InlineData(StorageFolderKind.Models, FolderRelation.SameDrive, true, true)]
    [InlineData(StorageFolderKind.Models, FolderRelation.OtherDrive, false, true)]
    [InlineData(StorageFolderKind.Recordings, FolderRelation.SameDrive, true, true)]
    [InlineData(StorageFolderKind.Recordings, FolderRelation.OtherDrive, false, true)]
    [InlineData(StorageFolderKind.Data, FolderRelation.SameDrive, true, false)]
    [InlineData(StorageFolderKind.Data, FolderRelation.OtherDrive, true, false)]
    public void Offers_ShouldFollowTheDesign(StorageFolderKind kind, FolderRelation relation, bool move, bool keep)
    {
        Assert.Equal(move, StorageFolderText.OffersMove(kind, relation));
        Assert.Equal(keep, StorageFolderText.OffersKeep(kind, relation));
    }

    [Fact]
    public void Relation_ShouldDetectNestedAndDrives()
    {
        var a = Path.Combine(Path.GetTempPath(), "a");
        Assert.Equal(FolderRelation.Nested, StorageFolderText.Relation(a, Path.Combine(a, "b")));
        Assert.Equal(FolderRelation.Nested, StorageFolderText.Relation(Path.Combine(a, "b"), a));
        Assert.Equal(FolderRelation.SameDrive, StorageFolderText.Relation(a, Path.Combine(Path.GetTempPath(), "b")));
    }

    [Fact]
    public void PendingNote_ShouldNameTheFolderAndKind()
    {
        Assert.Equal(@"Models will move to D:\Models when the app restarts.", StorageFolderText.PendingNote(StorageFolderKind.Models, @"D:\Models"));
        Assert.Equal(@"Recordings will move to D:\R when the app restarts.", StorageFolderText.PendingNote(StorageFolderKind.Recordings, @"D:\R"));
        Assert.Equal(@"App data will move to D:\V when the app restarts.", StorageFolderText.PendingNote(StorageFolderKind.Data, @"D:\V"));
    }

    [Fact]
    public void Notes_ShouldGiveTheReason()
    {
        var drive = new FolderMoveSkip("a", "b", ModelMoveSkipReason.OtherDrive);
        var used = new FolderMoveSkip("a", "b", ModelMoveSkipReason.InUse);

        Assert.Equal(@"Recordings were not moved to E:\R: it is on another drive and recordings are not copied automatically. They stay in C:\R.",
            StorageFolderText.RecordingsIncompleteNote(@"E:\R", @"C:\R", [drive]));
        Assert.Contains("some files were in use", StorageFolderText.RecordingsIncompleteNote("n", "o", [used]));
        Assert.Equal(@"App data was not moved to D:\V: some files were in use. It stays in C:\V.", StorageFolderText.DataIncompleteNote(@"D:\V", @"C:\V", [used]));
        Assert.Equal(@"The app data folder E:\V is not available. VoiceType uses the default folder C:\V instead.", StorageFolderText.DataUnavailableNote(@"E:\V", @"C:\V"));
    }

    [Fact]
    public void Facts_ShouldBeShort()
    {
        Assert.Equal("1.1 GB · 24 recordings", StorageFolderText.RecordingsFacts((long)(1.1 * (1L << 30)), 24));
        Assert.Equal("1 KB · 1 recording", StorageFolderText.RecordingsFacts(1024, 1));
        Assert.Equal("96 MB", StorageFolderText.DataFacts(96L << 20));
    }

    [Fact]
    public void Footer_ShouldDifferForAndroid()
    {
        Assert.Contains("private storage", StorageFolderText.Footer(true));
        Assert.Contains("never deletes or overwrites", StorageFolderText.Footer(false));
    }
}

public class StorageOverviewTests
{
    [Fact]
    public void FreeText_ShouldListDrivesOrOneSummary()
    {
        var c = new DriveSpace(@"C:\", 41L << 30, 256L << 30);
        var d = new DriveSpace(@"D:\", 212L << 30, 512L << 30);

        Assert.Equal("41 GB free on C: · 212 GB free on D:", StorageOverview.FreeText([c, d], android: false));
        Assert.Equal("41 GB free", StorageOverview.FreeText([c], android: false));
        Assert.Equal("41 GB free of 256 GB", StorageOverview.FreeText([c], android: true));
        Assert.Equal("", StorageOverview.FreeText([], android: false));
    }

    [Fact]
    public void DriveLabel_ShouldTrimTheSeparator()
    {
        Assert.Equal("C:", StorageOverview.DriveLabel(@"C:\"));
        Assert.Equal("/", StorageOverview.DriveLabel("/"));
    }

    private static StorageUsage Usage(long models, long recordings, long data, params DriveSpace[] drives) =>
        new(models, recordings, 3, data, drives, @"C:\v\models", @"D:\rec", @"C:\v");

    [Fact]
    public void TotalText_ShouldSumTheThreeFolders()
        => Assert.Equal("4.2 GB in total", StorageOverview.TotalText(Usage(3L << 30, (long)(1.1 * (1L << 30)), 96L << 20)));

    [Fact]
    public void Fractions_ShouldBeBytesOverTheBiggestDrive_WithAVisibleMinimum()
    {
        var usage = Usage(30L << 30, 10L << 30, 1L << 20, new DriveSpace(@"C:\", 1, 100L << 30));

        var (models, recordings, data) = StorageOverview.Fractions(usage);

        Assert.Equal(0.30, models, 3);
        Assert.Equal(0.10, recordings, 3);
        Assert.Equal(0.012, data, 3);
    }

    [Fact]
    public void Fractions_ShouldNeverExceedTheFullBar_AndHandleEmptyStorage()
    {
        var full = StorageOverview.Fractions(Usage(90L << 30, 90L << 30, 90L << 30, new DriveSpace(@"C:\", 1, 100L << 30)));
        Assert.Equal(1.0, full.Models + full.Recordings + full.Data, 6);

        Assert.Equal((0.0, 0.0, 0.0), StorageOverview.Fractions(Usage(0, 0, 0, new DriveSpace(@"C:\", 1, 100L << 30))));
        Assert.Equal((0.0, 0.0, 0.0), StorageOverview.Fractions(Usage(0, 0, 0)));
    }

    [WindowsFact("Windows only: the hub line names drive letters, which have no equivalent in a Linux path.")]
    public void HubLine_ShouldNameDrivesOnlyWhenTheyDiffer()
    {
        var split = Usage(3L << 30, 1L << 30, 1L << 20);
        var together = split with { SessionsDir = @"C:\rec" };

        Assert.Equal("Models 3 GB on C: · recordings on D:", StorageOverview.HubLine(split));
        Assert.Equal("Models 3 GB · recordings 1 GB", StorageOverview.HubLine(together));
    }

    [Fact]
    public void FolderSize_ShouldSkipExcludedFolders()
    {
        using var temp = new TempDirectory();
        temp.Write("data/settings.json", new string('x', 100));
        temp.Write("data/models/m.bin", new string('x', 1000));
        temp.Write("data/sessions/a/audio.opus", new string('x', 5000));

        Assert.Equal(6100, StorageOverview.FolderSize(Path.Combine(temp.Path, "data"), []));
        Assert.Equal(100, StorageOverview.FolderSize(Path.Combine(temp.Path, "data"), [Path.Combine(temp.Path, "data", "models"), Path.Combine(temp.Path, "data", "sessions")]));
        Assert.Equal(0, StorageOverview.FolderSize(Path.Combine(temp.Path, "missing"), []));
    }

    [Fact]
    public void Measure_ShouldCountModelsRecordingsAndDataSeparately()
    {
        using var temp = new TempDirectory();
        temp.Write("data/settings.json", new string('x', 100));
        temp.Write("data/chats/c/chat.json", new string('x', 50));
        temp.Write("data/sessions/2026_a/audio.opus", new string('x', 4000));
        temp.Write("data/sessions/2026_a/manifest.json", new string('x', 10));
        temp.Write("data/models/asr/m/x", "x");
        var settings = new AppSettings { ModelsRootPath = Path.Combine(temp.Path, "data", "models") };

        var usage = StorageOverview.Measure(settings, Path.Combine(temp.Path, "data"), Path.Combine(temp.Path, "data", "sessions"));

        Assert.Equal(4010, usage.RecordingsBytes);
        Assert.Equal(1, usage.Recordings);
        Assert.Equal(150, usage.DataBytes);
        Assert.Equal(Path.Combine(temp.Path, "data", "sessions"), usage.SessionsDir);
        Assert.NotEmpty(usage.Drives);
    }
}

public class DeferredSettingsApplierTests
{
    private sealed class Harness
    {
        public AppSettings Live { get; set; } = new() { SelectedModel = "a", ModelPath = "pa", ExecutionProvider = "cpu" };
        public bool Recording { get; set; }
        public int Applies { get; private set; }
        public DeferredSettingsApplier Applier { get; }

        public Harness() => Applier = new DeferredSettingsApplier(
            s => { Applies++; Live = s.Clone(); return Task.CompletedTask; },
            () => Recording,
            () => Live.Clone());
    }

    [Fact]
    public async Task Change_WhenIdle_ShouldApplyEverythingAtOnce()
    {
        var h = new Harness();

        await h.Applier.ChangeAsync(s => { s.ExecutionProvider = "webgpu:hp"; s.UseVad = false; });

        Assert.Equal("webgpu:hp", h.Live.ExecutionProvider);
        Assert.False(h.Live.UseVad);
        Assert.False(h.Applier.HasPending);
    }

    [Fact]
    public async Task Change_WhileRecording_ShouldHoldBackTheComputeButApplyTheRest()
    {
        var h = new Harness { Recording = true };
        var raised = 0;
        h.Applier.PendingChanged += () => raised++;

        await h.Applier.ChangeAsync(s => { s.ExecutionProvider = "webgpu:hp"; s.UseVad = false; s.Language = "de"; });

        Assert.Equal("cpu", h.Live.ExecutionProvider);
        Assert.False(h.Live.UseVad);
        Assert.Equal("de", h.Live.Language);
        Assert.True(h.Applier.HasPending);
        Assert.Equal("webgpu:hp", h.Applier.PendingExecutionProvider);
        Assert.Equal(1, raised);
    }

    [Fact]
    public async Task Change_WhileRecording_ShouldKeepThePendingComputeAcrossOtherEdits()
    {
        var h = new Harness { Recording = true };
        await h.Applier.ChangeAsync(s => s.ExecutionProvider = "webgpu:lp");

        await h.Applier.ChangeAsync(s => s.UseVad = false);

        Assert.True(h.Applier.HasPending);
        Assert.Equal("webgpu:lp", h.Applier.PendingExecutionProvider);
        Assert.Equal("cpu", h.Live.ExecutionProvider);
    }

    [Fact]
    public async Task Flush_AfterStop_ShouldApplyTheHeldBackChange()
    {
        var h = new Harness { Recording = true };
        await h.Applier.ChangeAsync(s => s.ExecutionProvider = "webgpu:hp");

        await h.Applier.FlushAsync(); // still recording: nothing happens
        Assert.Equal("cpu", h.Live.ExecutionProvider);

        h.Recording = false;
        await h.Applier.FlushAsync();

        Assert.Equal("webgpu:hp", h.Live.ExecutionProvider);
        Assert.False(h.Applier.HasPending);
    }

    [Fact]
    public async Task Flush_ShouldKeepSettingsChangedMeanwhile()
    {
        var h = new Harness { Recording = true };
        await h.Applier.ChangeAsync(s => s.ExecutionProvider = "webgpu:hp");
        h.Live.Language = "fr"; // changed elsewhere during the recording
        h.Recording = false;

        await h.Applier.FlushAsync();

        Assert.Equal("fr", h.Live.Language);
        Assert.Equal("webgpu:hp", h.Live.ExecutionProvider);
    }

    [Fact]
    public async Task Change_BackToTheLiveCompute_ShouldDropThePendingChange()
    {
        var h = new Harness { Recording = true };
        await h.Applier.ChangeAsync(s => s.ExecutionProvider = "webgpu:hp");
        Assert.True(h.Applier.HasPending);

        await h.Applier.ChangeAsync(s => s.ExecutionProvider = "cpu");

        Assert.False(h.Applier.HasPending);
        h.Recording = false;
        var applies = h.Applies;
        await h.Applier.FlushAsync();
        Assert.Equal(applies, h.Applies);
        Assert.Equal("cpu", h.Live.ExecutionProvider);
    }

    [Fact]
    public async Task Changes_ShouldRunOneAtATime_EachOnTopOfTheLast()
    {
        var h = new Harness();

        await Task.WhenAll(
            h.Applier.ChangeAsync(s => s.UseVad = false),
            h.Applier.ChangeAsync(s => s.Language = "de"),
            h.Applier.ChangeAsync(s => s.AudioSource = "Mix"));

        Assert.False(h.Live.UseVad);
        Assert.Equal("de", h.Live.Language);
        Assert.Equal("Mix", h.Live.AudioSource);
    }

    [Fact]
    public void IsHeavy_ShouldBeTrueOnlyForTheCompute()
    {
        var a = new AppSettings();
        var other = a.Clone();
        other.UseVad = false;
        other.AudioSource = "Mix";
        Assert.False(DeferredSettingsApplier.IsHeavy(a, other));

        other.ExecutionProvider = "webgpu:lp";
        Assert.True(DeferredSettingsApplier.IsHeavy(a, other));

        var stale = a.Clone();
        stale.ExecutionProvider = "cuda"; // runs on the processor, same as cpu
        Assert.False(DeferredSettingsApplier.IsHeavy(a, stale));
    }
}

public class RestartCommandTests
{
    [Fact]
    public void Build_NativeHost_ShouldSkipTheProgramName()
    {
        var command = RestartCommand.Build(@"C:\app\VoiceType.Uno.exe", [@"C:\app\VoiceType.Uno.dll", "--x"]);

        Assert.Equal(@"C:\app\VoiceType.Uno.exe", command!.Value.FileName);
        Assert.Equal(["--x"], command.Value.Arguments);
    }

    [Fact]
    public void Build_UnderDotnet_ShouldPassTheDllAsFirstArgument()
    {
        var dll = Path.Combine(Path.GetTempPath(), "app", "VoiceType.Uno.dll");
        var command = RestartCommand.Build(Path.Combine(Path.GetTempPath(), "dotnet", "dotnet.exe"), [dll, "--x"]);

        Assert.Equal([dll, "--x"], command!.Value.Arguments);
    }

    [Fact]
    public void Build_WithoutAProcessPath_ShouldBeNull()
        => Assert.Null(RestartCommand.Build(null, []));
}
