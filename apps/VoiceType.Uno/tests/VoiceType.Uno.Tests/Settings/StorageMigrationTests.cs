using VoiceType.Uno.Services;
using Xunit;

namespace VoiceType.Uno.Tests.Settings;

public class FolderMoverTests
{
    [Fact]
    public void MoveChildren_ShouldRenameEveryChild_AndKeepTheContent()
    {
        using var temp = new TempDirectory();
        temp.Write("from/a/audio.opus", "A");
        temp.Write("from/b/manifest.json", "B");
        temp.Write("from/loose.txt", "L");

        var report = FolderMover.MoveChildren(Path.Combine(temp.Path, "from"), Path.Combine(temp.Path, "to"));

        Assert.True(report.IsComplete);
        Assert.Equal(3, report.Moved.Count);
        Assert.Equal("A", File.ReadAllText(Path.Combine(temp.Path, "to", "a", "audio.opus")));
        Assert.Equal("L", File.ReadAllText(Path.Combine(temp.Path, "to", "loose.txt")));
        Assert.False(Directory.Exists(Path.Combine(temp.Path, "from", "a")));
    }

    [Fact]
    public void MoveChildren_ShouldNeverOverwrite_AndReportTheConflict()
    {
        using var temp = new TempDirectory();
        temp.Write("from/same/new.txt", "new");
        temp.Write("from/other/x.txt", "x");
        temp.Write("to/same/old.txt", "old");

        var report = FolderMover.MoveChildren(Path.Combine(temp.Path, "from"), Path.Combine(temp.Path, "to"));

        Assert.False(report.IsComplete);
        var skip = Assert.Single(report.Skipped);
        Assert.Equal(ModelMoveSkipReason.DestinationExists, skip.Reason);
        Assert.Equal("new", File.ReadAllText(Path.Combine(temp.Path, "from", "same", "new.txt")));
        Assert.Equal("old", File.ReadAllText(Path.Combine(temp.Path, "to", "same", "old.txt")));
        Assert.False(File.Exists(Path.Combine(temp.Path, "to", "same", "new.txt")));
        Assert.True(File.Exists(Path.Combine(temp.Path, "to", "other", "x.txt")));
    }

    [Fact]
    public void MoveChildren_SecondRun_ShouldDoNothing()
    {
        using var temp = new TempDirectory();
        temp.Write("from/a/f.txt", "A");
        var from = Path.Combine(temp.Path, "from");
        var to = Path.Combine(temp.Path, "to");

        FolderMover.MoveChildren(from, to);
        var again = FolderMover.MoveChildren(from, to);

        Assert.True(again.IsEmpty);
        Assert.Equal("A", File.ReadAllText(Path.Combine(to, "a", "f.txt")));
    }

    [Fact]
    public void MoveChildren_ShouldLeaveLockedItemsForTheNextStart()
    {
        using var temp = new TempDirectory();
        temp.Write("from/locked/audio.opus", "A");
        temp.Write("from/free/audio.opus", "B");
        var path = Path.Combine(temp.Path, "from", "locked", "audio.opus");

        FolderMoveReport report;
        using (new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.None))
            report = FolderMover.MoveChildren(Path.Combine(temp.Path, "from"), Path.Combine(temp.Path, "to"));

        var skip = Assert.Single(report.Skipped);
        Assert.Equal(ModelMoveSkipReason.InUse, skip.Reason);
        Assert.True(File.Exists(path));
        Assert.True(File.Exists(Path.Combine(temp.Path, "to", "free", "audio.opus")));

        // Next start: the file is free, the leftover moves.
        var retry = FolderMover.MoveChildren(Path.Combine(temp.Path, "from"), Path.Combine(temp.Path, "to"));
        Assert.True(retry.IsComplete);
        Assert.True(File.Exists(Path.Combine(temp.Path, "to", "locked", "audio.opus")));
    }

    [Fact]
    public void Revert_ShouldPutMovedItemsBack_AndNotTouchWhatTookTheirPlace()
    {
        using var temp = new TempDirectory();
        temp.Write("from/a/f.txt", "A");
        temp.Write("from/b/f.txt", "B");
        var from = Path.Combine(temp.Path, "from");
        var report = FolderMover.MoveChildren(from, Path.Combine(temp.Path, "to"));
        temp.Write("from/b/new.txt", "someone else");

        var restored = FolderMover.Revert(report);

        Assert.Equal(1, restored);
        Assert.Equal("A", File.ReadAllText(Path.Combine(from, "a", "f.txt")));
        Assert.True(File.Exists(Path.Combine(temp.Path, "to", "b", "f.txt")));
        Assert.Equal("someone else", File.ReadAllText(Path.Combine(from, "b", "new.txt")));
    }

    [Fact]
    public void MoveChildren_ShouldRefuseNestedFolders()
    {
        using var temp = new TempDirectory();
        temp.Write("root/a/f.txt", "A");
        var root = Path.Combine(temp.Path, "root");

        var inside = FolderMover.MoveChildren(root, Path.Combine(root, "new"));
        var outside = FolderMover.MoveChildren(Path.Combine(root, "a"), temp.Path);

        Assert.All(inside.Skipped, s => Assert.Equal(ModelMoveSkipReason.Nested, s.Reason));
        Assert.Contains(inside.Skipped, s => s.From.EndsWith("a"));
        Assert.All(outside.Skipped, s => Assert.Equal(ModelMoveSkipReason.Nested, s.Reason));
        Assert.True(File.Exists(Path.Combine(root, "a", "f.txt")));
    }

    [Fact]
    public void MoveChildren_ShouldHonourTheExclusion()
    {
        using var temp = new TempDirectory();
        temp.Write("from/keep/f.txt", "K");
        temp.Write("from/go/f.txt", "G");

        var report = FolderMover.MoveChildren(Path.Combine(temp.Path, "from"), Path.Combine(temp.Path, "to"),
            exclude: entry => Path.GetFileName(entry) == "keep");

        Assert.True(report.IsComplete);
        Assert.True(File.Exists(Path.Combine(temp.Path, "from", "keep", "f.txt")));
        Assert.True(File.Exists(Path.Combine(temp.Path, "to", "go", "f.txt")));
    }

    private static FolderMoveReport CrossDrive(string from, string to, bool allowCopy = true) =>
        FolderMover.MoveChildren(from, to, allowCopy, exclude: null, log: null, sameDriveTest: (_, _) => false);

    [Fact]
    public void MoveChildren_AcrossDrives_ShouldNotCopyUnlessAllowed()
    {
        using var temp = new TempDirectory();
        temp.Write("from/a/f.txt", "A");

        var report = CrossDrive(Path.Combine(temp.Path, "from"), Path.Combine(temp.Path, "to"), allowCopy: false);

        var skip = Assert.Single(report.Skipped);
        Assert.Equal(ModelMoveSkipReason.OtherDrive, skip.Reason);
        Assert.True(File.Exists(Path.Combine(temp.Path, "from", "a", "f.txt")));
        Assert.False(Directory.Exists(Path.Combine(temp.Path, "to", "a")));
    }

    [Fact]
    public void MoveChildren_AcrossDrives_WhenAllowed_ShouldCopyThenRemoveTheSource()
    {
        using var temp = new TempDirectory();
        temp.Write("from/dir/sub/f.txt", "deep");
        temp.Write("from/loose.txt", "L");

        var report = CrossDrive(Path.Combine(temp.Path, "from"), Path.Combine(temp.Path, "to"));

        Assert.True(report.IsComplete);
        Assert.Equal("deep", File.ReadAllText(Path.Combine(temp.Path, "to", "dir", "sub", "f.txt")));
        Assert.Equal("L", File.ReadAllText(Path.Combine(temp.Path, "to", "loose.txt")));
        Assert.False(Directory.Exists(Path.Combine(temp.Path, "from", "dir")));
        Assert.False(File.Exists(Path.Combine(temp.Path, "from", "loose.txt")));
    }

    [Fact]
    public void MoveChildren_AcrossDrives_ShouldNeverOverwrite()
    {
        using var temp = new TempDirectory();
        temp.Write("from/same/new.txt", "new");
        temp.Write("to/same/old.txt", "old");

        var report = CrossDrive(Path.Combine(temp.Path, "from"), Path.Combine(temp.Path, "to"));

        Assert.Equal(ModelMoveSkipReason.DestinationExists, Assert.Single(report.Skipped).Reason);
        Assert.Equal("new", File.ReadAllText(Path.Combine(temp.Path, "from", "same", "new.txt")));
        Assert.Equal("old", File.ReadAllText(Path.Combine(temp.Path, "to", "same", "old.txt")));
    }

    [Fact]
    public void MoveChildren_AcrossDrives_WhenTheSourceCannotBeRemoved_ShouldUndoItsCopy()
    {
        using var temp = new TempDirectory();
        temp.Write("from/held/f.txt", "A");
        temp.Write("from/free/f.txt", "B");
        var held = Path.Combine(temp.Path, "from", "held", "f.txt");

        FolderMoveReport report;
        // Readable (the copy works) but not deletable: the copy is made, the removal fails, the copy is undone.
        using (new FileStream(held, FileMode.Open, FileAccess.Read, FileShare.Read))
            report = CrossDrive(Path.Combine(temp.Path, "from"), Path.Combine(temp.Path, "to"));

        var skip = Assert.Single(report.Skipped);
        Assert.Equal(ModelMoveSkipReason.InUse, skip.Reason);
        Assert.True(File.Exists(held));
        Assert.False(Directory.Exists(Path.Combine(temp.Path, "to", "held")));
        Assert.True(File.Exists(Path.Combine(temp.Path, "to", "free", "f.txt")));
    }

    [Fact]
    public void MoveChildren_AcrossDrives_WhenTheSourceCannotBeRead_ShouldLeaveItAndLeaveNoPartialCopy()
    {
        using var temp = new TempDirectory();
        temp.Write("from/locked/f.txt", "A");
        var locked = Path.Combine(temp.Path, "from", "locked", "f.txt");

        FolderMoveReport report;
        using (new FileStream(locked, FileMode.Open, FileAccess.Read, FileShare.None))
            report = CrossDrive(Path.Combine(temp.Path, "from"), Path.Combine(temp.Path, "to"));

        Assert.Equal(ModelMoveSkipReason.InUse, Assert.Single(report.Skipped).Reason);
        Assert.True(File.Exists(locked));
        Assert.False(Directory.Exists(Path.Combine(temp.Path, "to", "locked")));
    }

    [Fact]
    public void Revert_AcrossDrives_ShouldCopyBackWhenAllowed()
    {
        using var temp = new TempDirectory();
        temp.Write("from/a/f.txt", "A");
        var from = Path.Combine(temp.Path, "from");
        var report = CrossDrive(from, Path.Combine(temp.Path, "to"));
        Assert.False(File.Exists(Path.Combine(from, "a", "f.txt")));

        // Revert tests the drives itself (one drive here, so a plain rename back).
        var restored = FolderMover.Revert(report, allowCopyAcrossDrives: true);

        Assert.Equal(1, restored);
        Assert.Equal("A", File.ReadAllText(Path.Combine(from, "a", "f.txt")));
    }

    [Fact]
    public void MoveChildren_FromAMissingFolder_ShouldBeEmpty()
    {
        using var temp = new TempDirectory();

        Assert.True(FolderMover.MoveChildren(Path.Combine(temp.Path, "nope"), Path.Combine(temp.Path, "to")).IsEmpty);
    }
}

public class RecordingStorageMigratorTests
{
    private static string Rec(TempDirectory temp, string folder, string id)
    {
        temp.Write($"{folder}/2026-10-01_{id}/manifest.json", id);
        return $"2026-10-01_{id}";
    }

    [Fact]
    public void RunAtStartup_ShouldMoveTheRecordings_AndAdoptTheNewFolder()
    {
        using var temp = new TempDirectory();
        var one = Rec(temp, "old", "a1");
        var two = Rec(temp, "old", "b2");
        var settings = new AppSettings { SessionsPath = Path.Combine(temp.Path, "old"), PendingSessionsPath = Path.Combine(temp.Path, "new"), SessionsStorageNote = "stale" };

        var changed = RecordingStorageMigrator.RunAtStartup(settings, Path.Combine(temp.Path, "default"));

        Assert.True(changed);
        Assert.Equal(Path.Combine(temp.Path, "new"), settings.SessionsPath);
        Assert.Equal("", settings.PendingSessionsPath);
        Assert.Equal("", settings.SessionsStorageNote);
        Assert.True(File.Exists(Path.Combine(temp.Path, "new", one, "manifest.json")));
        Assert.True(File.Exists(Path.Combine(temp.Path, "new", two, "manifest.json")));
        Assert.False(Directory.Exists(Path.Combine(temp.Path, "old", one)));
    }

    [Fact]
    public void RunAtStartup_WithTheDefaultFolderAsSource_ShouldMoveFromIt()
    {
        using var temp = new TempDirectory();
        var one = Rec(temp, "default", "a1");
        var settings = new AppSettings { PendingSessionsPath = Path.Combine(temp.Path, "new") };

        RecordingStorageMigrator.RunAtStartup(settings, Path.Combine(temp.Path, "default"));

        Assert.True(File.Exists(Path.Combine(temp.Path, "new", one, "manifest.json")));
        Assert.Equal(Path.Combine(temp.Path, "new"), settings.SessionsPath);
    }

    [Fact]
    public void RunAtStartup_ShouldKeepTheOldFolderAndNote_WhenSomethingIsLocked()
    {
        using var temp = new TempDirectory();
        var one = Rec(temp, "old", "a1");
        var two = Rec(temp, "old", "b2");
        var settings = new AppSettings { SessionsPath = Path.Combine(temp.Path, "old"), PendingSessionsPath = Path.Combine(temp.Path, "new") };

        using (new FileStream(Path.Combine(temp.Path, "old", one, "manifest.json"), FileMode.Open, FileAccess.Read, FileShare.None))
            RecordingStorageMigrator.RunAtStartup(settings, Path.Combine(temp.Path, "default"));

        Assert.Equal(Path.Combine(temp.Path, "old"), settings.SessionsPath);
        Assert.Equal("", settings.PendingSessionsPath);
        Assert.Contains("some files were in use", settings.SessionsStorageNote);
        Assert.Contains(Path.Combine(temp.Path, "old"), settings.SessionsStorageNote);
        // All or nothing: the other recording went back too.
        Assert.False(Directory.Exists(Path.Combine(temp.Path, "new", two)));
        Assert.True(File.Exists(Path.Combine(temp.Path, "old", two, "manifest.json")));
        Assert.True(File.Exists(Path.Combine(temp.Path, "old", one, "manifest.json")));
    }

    [Fact]
    public void RunAtStartup_ShouldNeverOverwriteARecordingOfTheSameName()
    {
        using var temp = new TempDirectory();
        var one = Rec(temp, "old", "a1");
        temp.Write($"new/{one}/manifest.json", "already here");
        var settings = new AppSettings { SessionsPath = Path.Combine(temp.Path, "old"), PendingSessionsPath = Path.Combine(temp.Path, "new") };

        RecordingStorageMigrator.RunAtStartup(settings, Path.Combine(temp.Path, "default"));

        Assert.Equal("already here", File.ReadAllText(Path.Combine(temp.Path, "new", one, "manifest.json")));
        Assert.Equal("a1", File.ReadAllText(Path.Combine(temp.Path, "old", one, "manifest.json")));
        Assert.Equal(Path.Combine(temp.Path, "old"), settings.SessionsPath);
        Assert.Contains("same name already exist", settings.SessionsStorageNote);
    }

    [Fact]
    public void RunAtStartup_ShouldBeIdempotent_AndIgnoreNoPendingMove()
    {
        using var temp = new TempDirectory();
        Rec(temp, "old", "a1");
        var settings = new AppSettings { SessionsPath = Path.Combine(temp.Path, "old") };

        Assert.False(RecordingStorageMigrator.RunAtStartup(settings, Path.Combine(temp.Path, "default")));
        Assert.Equal(Path.Combine(temp.Path, "old"), settings.SessionsPath);

        settings.PendingSessionsPath = Path.Combine(temp.Path, "new");
        RecordingStorageMigrator.RunAtStartup(settings, Path.Combine(temp.Path, "default"));
        var again = RecordingStorageMigrator.RunAtStartup(settings, Path.Combine(temp.Path, "default"));

        Assert.False(again);
        Assert.Equal(Path.Combine(temp.Path, "new"), settings.SessionsPath);
    }

    [Fact]
    public void RunAtStartup_WhenTheNewFolderIsInsideTheOldOne_ShouldStayWithANote()
    {
        using var temp = new TempDirectory();
        Rec(temp, "old", "a1");
        var settings = new AppSettings { SessionsPath = Path.Combine(temp.Path, "old"), PendingSessionsPath = Path.Combine(temp.Path, "old", "inner") };

        RecordingStorageMigrator.RunAtStartup(settings, Path.Combine(temp.Path, "default"));

        Assert.Equal(Path.Combine(temp.Path, "old"), settings.SessionsPath);
        Assert.Contains("overlap", settings.SessionsStorageNote);
    }

    [Fact]
    public void ApplyChoice_ShouldSetPending_Switch_OrDoNothing()
    {
        var move = new AppSettings { SessionsPath = "old" };
        RecordingStorageMigrator.ApplyChoice(move, "new", ModelRootChoice.MoveOnRestart);
        Assert.Equal("new", move.PendingSessionsPath);
        Assert.Equal("old", move.SessionsPath);

        var keep = new AppSettings { SessionsPath = "old", PendingSessionsPath = "x", SessionsStorageNote = "n" };
        RecordingStorageMigrator.ApplyChoice(keep, "new", ModelRootChoice.Keep);
        Assert.Equal("new", keep.SessionsPath);
        Assert.Equal("", keep.PendingSessionsPath);
        Assert.Equal("", keep.SessionsStorageNote);

        var cancel = new AppSettings { SessionsPath = "old" };
        RecordingStorageMigrator.ApplyChoice(cancel, "new", ModelRootChoice.Cancel);
        Assert.Equal("old", cancel.SessionsPath);
        Assert.Equal("", cancel.PendingSessionsPath);
    }

    [Fact]
    public void CancelPending_ShouldDropThePendingMove()
    {
        var settings = new AppSettings { PendingSessionsPath = "new" };
        RecordingStorageMigrator.CancelPending(settings);
        Assert.Equal("", settings.PendingSessionsPath);
    }

    [Fact]
    public void NeedsQuestion_ShouldRequireDifferentFoldersAndExistingRecordings()
    {
        Assert.True(RecordingStorageMigrator.NeedsQuestion(@"C:\a", @"C:\b", 3));
        Assert.False(RecordingStorageMigrator.NeedsQuestion(@"C:\a", @"C:\a\", 3));
        Assert.False(RecordingStorageMigrator.NeedsQuestion(@"C:\a", @"C:\b", 0));
        Assert.False(RecordingStorageMigrator.NeedsQuestion(@"C:\a", "", 3));
    }

    [Fact]
    public void Resolve_ShouldUseTheDefaultWhenTheSettingIsEmpty()
    {
        using var temp = new TempDirectory();
        Assert.Equal(Path.Combine(temp.Path, "d"), RecordingStorageMigrator.Resolve(new AppSettings(), Path.Combine(temp.Path, "d")));
        Assert.Equal(Path.Combine(temp.Path, "x"), RecordingStorageMigrator.Resolve(new AppSettings { SessionsPath = Path.Combine(temp.Path, "x") }, "ignored"));
    }
}

public class DataLocationTests
{
    [Fact]
    public void Resolve_WithoutAPointer_ShouldUseTheDefaultRoot()
    {
        using var temp = new TempDirectory();
        var resolution = DataLocation.Resolve(Path.Combine(temp.Path, "default"));

        Assert.Equal(Path.Combine(temp.Path, "default"), resolution.Root);
        Assert.False(resolution.IsCustom);
        Assert.Equal("", resolution.Note);
    }

    [Fact]
    public void Resolve_ShouldFollowThePointer()
    {
        using var temp = new TempDirectory();
        var def = Path.Combine(temp.Path, "default");
        Directory.CreateDirectory(def);
        File.WriteAllText(Path.Combine(def, DataLocation.PointerFileName), Path.Combine(temp.Path, "custom") + Environment.NewLine);

        var resolution = DataLocation.Resolve(def);

        Assert.Equal(Path.Combine(temp.Path, "custom"), resolution.Root);
        Assert.True(resolution.IsCustom);
    }

    [Fact]
    public void Resolve_WithAnUnusablePointer_ShouldFallBackToTheDefaultWithANote()
    {
        using var temp = new TempDirectory();
        var def = Path.Combine(temp.Path, "default");
        Directory.CreateDirectory(def);
        // A path whose parent is a file cannot be created.
        File.WriteAllText(Path.Combine(temp.Path, "afile"), "x");
        var bad = Path.Combine(temp.Path, "afile", "data");
        File.WriteAllText(Path.Combine(def, DataLocation.PointerFileName), bad);

        var resolution = DataLocation.Resolve(def);

        Assert.Equal(def, resolution.Root);
        Assert.False(resolution.IsCustom);
        Assert.Contains(bad, resolution.Note);
        Assert.Contains(def, resolution.Note);
    }

    [Fact]
    public void Resolve_WithARelativePointer_ShouldFallBack()
    {
        using var temp = new TempDirectory();
        var def = Path.Combine(temp.Path, "default");
        Directory.CreateDirectory(def);
        File.WriteAllText(Path.Combine(def, DataLocation.PointerFileName), "relative/folder");

        Assert.Equal(def, DataLocation.Resolve(def).Root);
    }

    private static string SeedData(TempDirectory temp, string root)
    {
        temp.Write(root + "/settings.json", "{}");
        temp.Write(root + "/chats/c1/chat.json", "chat");
        temp.Write(root + "/jobs/queue.json", "queue");
        temp.Write(root + "/errors.log", "log");
        return Path.Combine(temp.Path, root);
    }

    [Fact]
    public void RunAtStartup_ShouldMoveTheDataAndFlipThePointer()
    {
        using var temp = new TempDirectory();
        var def = SeedData(temp, "default");
        var target = Path.Combine(temp.Path, "moved");
        DataLocation.RequestMove(def, target);
        Assert.Equal(target, DataLocation.PendingTarget(def));

        var report = DataLocation.RunAtStartup(def);

        Assert.True(report.IsComplete);
        Assert.True(File.Exists(Path.Combine(target, "settings.json")));
        Assert.True(File.Exists(Path.Combine(target, "chats", "c1", "chat.json")));
        Assert.True(File.Exists(Path.Combine(target, "jobs", "queue.json")));
        Assert.False(File.Exists(Path.Combine(def, "settings.json")));
        Assert.Null(DataLocation.PendingTarget(def));
        Assert.Equal(target, DataLocation.Resolve(def).Root);
        Assert.True(DataLocation.Resolve(def).IsCustom);
    }

    [Fact]
    public void RunAtStartup_ShouldPinModelsAndRecordingsInsideTheOldRoot_AndLeaveThemThere()
    {
        using var temp = new TempDirectory();
        var def = SeedData(temp, "default");
        temp.Write("default/models/asr/m/model.onnx", "model");
        temp.Write("default/sessions/2026_a/manifest.json", "rec");
        var target = Path.Combine(temp.Path, "moved");
        DataLocation.RequestMove(def, target);

        DataLocation.RunAtStartup(def);

        // They stay: the data move leaves them behind.
        Assert.True(File.Exists(Path.Combine(def, "models", "asr", "m", "model.onnx")));
        Assert.True(File.Exists(Path.Combine(def, "sessions", "2026_a", "manifest.json")));
        // The settings that moved name them explicitly, so they are still found.
        var moved = new SettingsService(Path.Combine(target, "settings.json")).Load();
        Assert.Equal(Path.Combine(def, "models"), moved.ModelsRootPath);
        Assert.Equal(Path.Combine(def, "sessions"), moved.SessionsPath);
    }

    [Fact]
    public void RunAtStartup_ShouldNotPinEmptyFolders_SoDefaultsFollowTheNewRoot()
    {
        using var temp = new TempDirectory();
        var def = SeedData(temp, "default");
        Directory.CreateDirectory(Path.Combine(def, "models"));
        var target = Path.Combine(temp.Path, "moved");
        DataLocation.RequestMove(def, target);

        DataLocation.RunAtStartup(def);

        var moved = new SettingsService(Path.Combine(target, "settings.json")).Load();
        Assert.Equal("", moved.ModelsRootPath);
        Assert.Equal("", moved.SessionsPath);
    }

    [Fact]
    public void RunAtStartup_ShouldKeepCustomModelAndRecordingFoldersWhereTheyAre()
    {
        using var temp = new TempDirectory();
        var def = SeedData(temp, "default");
        temp.Write("elsewhere/models/asr/m/model.onnx", "model");
        var settings = new AppSettings { ModelsRootPath = Path.Combine(temp.Path, "elsewhere", "models") };
        new SettingsService(Path.Combine(def, "settings.json")).Save(settings);
        var target = Path.Combine(temp.Path, "moved");
        DataLocation.RequestMove(def, target);

        DataLocation.RunAtStartup(def);

        Assert.True(File.Exists(Path.Combine(temp.Path, "elsewhere", "models", "asr", "m", "model.onnx")));
        Assert.Equal(Path.Combine(temp.Path, "elsewhere", "models"), new SettingsService(Path.Combine(target, "settings.json")).Load().ModelsRootPath);
    }

    [Fact]
    public void RunAtStartup_ShouldNeverOverwrite_AndStayWithANote()
    {
        using var temp = new TempDirectory();
        var def = SeedData(temp, "default");
        temp.Write("moved/settings.json", "theirs");
        var target = Path.Combine(temp.Path, "moved");
        DataLocation.RequestMove(def, target);

        var report = DataLocation.RunAtStartup(def);

        Assert.False(report.IsComplete);
        Assert.Equal("theirs", File.ReadAllText(Path.Combine(target, "settings.json")));
        Assert.True(File.Exists(Path.Combine(def, "settings.json")));
        // All or nothing: what could move was put back, so the app finds its data in one place.
        Assert.True(File.Exists(Path.Combine(def, "chats", "c1", "chat.json")));
        Assert.False(Directory.Exists(Path.Combine(target, "chats")));
        var resolution = DataLocation.Resolve(def);
        Assert.False(resolution.IsCustom);
        Assert.Contains("App data was not moved", resolution.Note);
    }

    [Fact]
    public void RunAtStartup_WithLockedFile_ShouldStayWithANote_AndRetryOnlyWhenAskedAgain()
    {
        using var temp = new TempDirectory();
        var def = SeedData(temp, "default");
        var target = Path.Combine(temp.Path, "moved");
        DataLocation.RequestMove(def, target);

        using (new FileStream(Path.Combine(def, "errors.log"), FileMode.Open, FileAccess.Read, FileShare.None))
        {
            var report = DataLocation.RunAtStartup(def);
            Assert.Single(report.Skipped);
        }

        Assert.Null(DataLocation.PendingTarget(def));
        Assert.False(DataLocation.Resolve(def).IsCustom);
        Assert.Contains("some files were in use", DataLocation.ReadNote(def));
        Assert.True(File.Exists(Path.Combine(def, "settings.json")));
        Assert.True(File.Exists(Path.Combine(def, "chats", "c1", "chat.json")));

        // Asking again clears the note and finishes the job.
        DataLocation.RequestMove(def, target);
        Assert.Equal("", DataLocation.ReadNote(def));
        Assert.True(DataLocation.RunAtStartup(def).IsComplete);
        Assert.True(File.Exists(Path.Combine(target, "errors.log")));
    }

    [Fact]
    public void RunAtStartup_ShouldBeIdempotent()
    {
        using var temp = new TempDirectory();
        var def = SeedData(temp, "default");
        DataLocation.RequestMove(def, Path.Combine(temp.Path, "moved"));
        DataLocation.RunAtStartup(def);

        Assert.True(DataLocation.RunAtStartup(def).IsEmpty);
        Assert.Equal(Path.Combine(temp.Path, "moved"), DataLocation.Resolve(def).Root);
    }

    [Fact]
    public void RunAtStartup_MovingBackToTheDefault_ShouldRemoveThePointer()
    {
        using var temp = new TempDirectory();
        var def = SeedData(temp, "default");
        var custom = Path.Combine(temp.Path, "moved");
        DataLocation.RequestMove(def, custom);
        DataLocation.RunAtStartup(def);
        Assert.Equal(custom, DataLocation.Resolve(def).Root);

        DataLocation.RequestMove(def, def);
        var report = DataLocation.RunAtStartup(def);

        Assert.True(report.IsComplete);
        Assert.False(File.Exists(Path.Combine(def, DataLocation.PointerFileName)));
        Assert.False(DataLocation.Resolve(def).IsCustom);
        Assert.True(File.Exists(Path.Combine(def, "settings.json")));
        Assert.True(File.Exists(Path.Combine(def, "chats", "c1", "chat.json")));
    }

    [Fact]
    public void RunAtStartup_ToAFolderInsideTheCurrentOne_ShouldStayWithANote()
    {
        using var temp = new TempDirectory();
        var def = SeedData(temp, "default");
        DataLocation.RequestMove(def, Path.Combine(def, "inner"));

        var report = DataLocation.RunAtStartup(def);

        Assert.False(report.IsComplete);
        Assert.True(File.Exists(Path.Combine(def, "settings.json")));
        Assert.Contains("overlap", DataLocation.ReadNote(def));
    }

    [Fact]
    public void CancelMove_ShouldForgetThePendingMove()
    {
        using var temp = new TempDirectory();
        var def = Path.Combine(temp.Path, "default");
        DataLocation.RequestMove(def, Path.Combine(temp.Path, "moved"));

        DataLocation.CancelMove(def);

        Assert.Null(DataLocation.PendingTarget(def));
        Assert.True(DataLocation.RunAtStartup(def).IsEmpty);
    }

    [Fact]
    public void IsPointerFile_ShouldRecogniseTheKeptFiles()
    {
        Assert.True(DataLocation.IsPointerFile(@"C:\x\data-location.txt"));
        Assert.True(DataLocation.IsPointerFile(@"C:\x\Data-Location.pending.txt"));
        Assert.False(DataLocation.IsPointerFile(@"C:\x\settings.json"));
    }
}
