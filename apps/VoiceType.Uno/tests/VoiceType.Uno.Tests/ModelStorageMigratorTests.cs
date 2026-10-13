using System.Text.Json;
using SpeechLib.Diarization;
using VoiceType.Uno.Services;
using Xunit;

namespace VoiceType.Uno.Tests;

public class ModelStorageMigratorTests
{
    private static readonly string Int8Folder = DiarizationModelLocator.Int8FolderName;

    /// <summary>A flat (older version) models root: a speech model, a Gemma file with its leftovers, a speaker model and a stranger.</summary>
    private static ModelStorageLayout LegacyRoot(TempDirectory temp)
    {
        ModelFixtures.AsrModel(Path.Combine(temp.Path, "my-asr"));
        temp.Write("gemma-4-E2B-it.litertlm", "gemma");
        temp.Write("gemma-4-E2B-it.litertlm.part", "partial");
        temp.Write("gemma-4-E2B-it.litertlm_123_456.xnnpack_cache", "cache");
        ModelFixtures.DiarizationModel(Path.Combine(temp.Path, Int8Folder));
        temp.Write("notes/readme.txt", "mine");
        temp.Write("random.bin", "mine");
        return new ModelStorageLayout(temp.Path);
    }

    [Fact]
    public void Migrate_ShouldSortLegacyContentIntoTheKindFolders()
    {
        using var temp = new TempDirectory();
        var layout = LegacyRoot(temp);

        var report = ModelStorageMigrator.Migrate(layout);

        Assert.True(report.IsComplete);
        Assert.Equal(5, report.Moved.Count);
        Assert.True(File.Exists(Path.Combine(layout.AsrDir, "my-asr", "genai_config.json")));
        Assert.True(File.Exists(layout.LlmFilePath("gemma-4-E2B-it.litertlm")));
        Assert.True(File.Exists(layout.LlmFilePath("gemma-4-E2B-it.litertlm.part")));
        Assert.True(File.Exists(layout.LlmFilePath("gemma-4-E2B-it.litertlm_123_456.xnnpack_cache")));
        Assert.True(File.Exists(Path.Combine(layout.DiarizationDir, Int8Folder, DiarizationModelLocator.Int8StepFileName)));
        Assert.False(Directory.Exists(Path.Combine(temp.Path, "my-asr")));
        Assert.False(File.Exists(Path.Combine(temp.Path, "gemma-4-E2B-it.litertlm")));
        // The diarization folder validates at its new place.
        Assert.NotNull(DiarizationModelLocator.Find(layout.DiarizationScanRoots));
    }

    [Fact]
    public void Migrate_ShouldLeaveEverythingElseAlone()
    {
        using var temp = new TempDirectory();
        var layout = LegacyRoot(temp);

        ModelStorageMigrator.Migrate(layout);

        Assert.Equal("mine", File.ReadAllText(Path.Combine(temp.Path, "notes", "readme.txt")));
        Assert.Equal("mine", File.ReadAllText(Path.Combine(temp.Path, "random.bin")));
    }

    [Fact]
    public void Migrate_ShouldBeIdempotent()
    {
        using var temp = new TempDirectory();
        var layout = LegacyRoot(temp);
        ModelStorageMigrator.Migrate(layout);

        var second = ModelStorageMigrator.Migrate(layout);

        Assert.True(second.IsEmpty);
        Assert.Equal("weights", File.ReadAllText(Path.Combine(layout.AsrDir, "my-asr", "decoder.onnx")));
    }

    [Fact]
    public void Migrate_ShouldNeverOverwrite_AndReportTheConflict()
    {
        using var temp = new TempDirectory();
        var layout = LegacyRoot(temp);
        temp.Write("asr/my-asr/genai_config.json", """{"decoder":{"filename":"other.onnx"}}""");
        temp.Write("llm/gemma-4-E2B-it.litertlm", "already here");

        var report = ModelStorageMigrator.Migrate(layout);

        Assert.False(report.IsComplete);
        Assert.Equal(2, report.Conflicts.Count);
        Assert.Contains(report.Conflicts, c => c.Kind == ModelKind.Speech && c.From.EndsWith("my-asr"));
        // Both sides are untouched.
        Assert.Equal("already here", File.ReadAllText(layout.LlmFilePath("gemma-4-E2B-it.litertlm")));
        Assert.Equal("gemma", File.ReadAllText(Path.Combine(temp.Path, "gemma-4-E2B-it.litertlm")));
        Assert.True(File.Exists(Path.Combine(temp.Path, "my-asr", "decoder.onnx")));
        Assert.Contains("other.onnx", File.ReadAllText(Path.Combine(layout.AsrDir, "my-asr", "genai_config.json")));
        // The rest still moved.
        Assert.True(File.Exists(layout.LlmFilePath("gemma-4-E2B-it.litertlm.part")));
    }

    [Fact]
    public void Migrate_ShouldRetryAtTheNextStart_WhenAFileIsLocked()
    {
        if (!OperatingSystem.IsWindows())
            return; // a rename of a folder with an open file only fails on Windows

        using var temp = new TempDirectory();
        var layout = LegacyRoot(temp);

        using (var held = new FileStream(Path.Combine(temp.Path, "my-asr", "decoder.onnx"), FileMode.Open, FileAccess.Read, FileShare.Read))
        {
            var locked = ModelStorageMigrator.Migrate(layout);

            Assert.Single(locked.Locked);
            Assert.True(Directory.Exists(Path.Combine(temp.Path, "my-asr")));
            Assert.False(Directory.Exists(Path.Combine(layout.AsrDir, "my-asr")));
        }

        var retry = ModelStorageMigrator.Migrate(layout);

        Assert.True(retry.IsComplete);
        Assert.Single(retry.Moved);
        Assert.True(Directory.Exists(Path.Combine(layout.AsrDir, "my-asr")));
    }

    [Fact]
    public void Migrate_ShouldMoveACatalogFolderThatIsStillDownloading()
    {
        using var temp = new TempDirectory();
        temp.Write("parakeet-tdt-0.6b-v3-onnx-int4/encoder-model.onnx.part", "half");
        var layout = new ModelStorageLayout(temp.Path);

        var report = ModelStorageMigrator.Migrate(layout);

        Assert.Single(report.Moved);
        Assert.True(File.Exists(Path.Combine(layout.AsrDir, "parakeet-tdt-0.6b-v3-onnx-int4", "encoder-model.onnx.part")));
    }

    [Fact]
    public void Migrate_ShouldFollowAModelPathIntoTheMovedFolder_AndKeepTheRoot()
    {
        using var temp = new TempDirectory();
        var layout = LegacyRoot(temp);
        var settings = new AppSettings
        {
            ModelsRootPath = temp.Path,
            SelectedModel = "my-asr",
            ModelPath = Path.Combine(temp.Path, "my-asr"),
        };

        var report = ModelStorageMigrator.Migrate(layout, settings);

        Assert.True(report.SettingsChanged);
        Assert.Equal(layout.AsrModelPath("my-asr"), settings.ModelPath);
        Assert.Equal(temp.Path, settings.ModelsRootPath);
        Assert.Equal("my-asr", settings.SelectedModel);
        Assert.Equal(layout.AsrModelPath("my-asr"), ModelPathResolver.FindExistingModelPath(settings));
    }

    [Fact]
    public void Migrate_ShouldNotTouchAModelPathOutsideTheRoot()
    {
        using var temp = new TempDirectory();
        using var elsewhere = new TempDirectory();
        var layout = LegacyRoot(temp);
        var settings = new AppSettings { ModelsRootPath = temp.Path, ModelPath = elsewhere.Path };

        var report = ModelStorageMigrator.Migrate(layout, settings);

        Assert.False(report.SettingsChanged);
        Assert.Equal(elsewhere.Path, settings.ModelPath);
    }

    [Fact]
    public void Migrate_ShouldDoNothingForAMissingRoot()
    {
        using var temp = new TempDirectory();

        var report = ModelStorageMigrator.Migrate(new ModelStorageLayout(Path.Combine(temp.Path, "nope")));

        Assert.True(report.IsEmpty);
        Assert.False(Directory.Exists(Path.Combine(temp.Path, "nope")));
    }

    [Fact]
    public void Migrate_ShouldLogWhatItDid()
    {
        using var temp = new TempDirectory();
        var layout = LegacyRoot(temp);
        var log = new List<string>();

        ModelStorageMigrator.Migrate(layout, null, log.Add);

        Assert.Contains(log, l => l.StartsWith("moved ") && l.Contains("my-asr"));
        Assert.Contains(log, l => l.StartsWith("migration of ") && l.Contains("5 moved"));
    }

    // ── Root move ────────────────────────────────────────────────────────

    [Fact]
    public void MoveRoot_ShouldMoveTheThreeFoldersToTheNewRoot()
    {
        using var temp = new TempDirectory();
        var oldRoot = new ModelStorageLayout(Path.Combine(temp.Path, "old"));
        var newRoot = new ModelStorageLayout(Path.Combine(temp.Path, "new"));
        ModelFixtures.AsrModel(oldRoot.AsrModelPath("a"));
        ModelFixtures.AsrModel(oldRoot.AsrModelPath("b"));
        Directory.CreateDirectory(oldRoot.LlmDir);
        File.WriteAllText(oldRoot.LlmFilePath("gemma.litertlm"), "g");
        ModelFixtures.DiarizationModel(oldRoot.DiarizationModelPath(Int8Folder));
        var settings = new AppSettings { ModelPath = oldRoot.AsrModelPath("a") };

        var report = ModelStorageMigrator.MoveRoot(oldRoot, newRoot, settings);

        Assert.True(report.IsComplete);
        Assert.Equal(4, report.Moved.Count);
        Assert.True(Directory.Exists(newRoot.AsrModelPath("a")));
        Assert.True(Directory.Exists(newRoot.AsrModelPath("b")));
        Assert.True(File.Exists(newRoot.LlmFilePath("gemma.litertlm")));
        Assert.NotNull(DiarizationModelLocator.Find(newRoot.DiarizationScanRoots));
        Assert.Empty(Directory.GetFileSystemEntries(oldRoot.AsrDir)); // the old folders stay, empty: nothing is deleted
        Assert.Equal(newRoot.AsrModelPath("a"), settings.ModelPath);
    }

    [Fact]
    public void MoveRoot_ShouldSkipNameClashes()
    {
        using var temp = new TempDirectory();
        var oldRoot = new ModelStorageLayout(Path.Combine(temp.Path, "old"));
        var newRoot = new ModelStorageLayout(Path.Combine(temp.Path, "new"));
        ModelFixtures.AsrModel(oldRoot.AsrModelPath("same"));
        ModelFixtures.AsrModel(oldRoot.AsrModelPath("only-old"));
        temp.Write("new/asr/same/keep.txt", "new side");

        var report = ModelStorageMigrator.MoveRoot(oldRoot, newRoot);

        Assert.Single(report.Moved);
        Assert.Single(report.Conflicts);
        Assert.True(File.Exists(newRoot.AsrModelPath("same") + Path.DirectorySeparatorChar + "keep.txt"));
        Assert.True(Directory.Exists(oldRoot.AsrModelPath("same")));
    }

    [Fact]
    public void MoveRoot_ShouldRefuseNestedRoots()
    {
        using var temp = new TempDirectory();
        var oldRoot = new ModelStorageLayout(Path.Combine(temp.Path, "old"));
        ModelFixtures.AsrModel(oldRoot.AsrModelPath("a"));
        var inside = new ModelStorageLayout(Path.Combine(oldRoot.Root, "sub"));

        var report = ModelStorageMigrator.MoveRoot(oldRoot, inside);

        Assert.Empty(report.Moved);
        Assert.Contains(report.Skipped, s => s.Reason == ModelMoveSkipReason.Nested);
        Assert.True(Directory.Exists(oldRoot.AsrModelPath("a")));
    }

    [Fact]
    public void MoveRoot_ShouldDoNothingForTheSameRoot()
    {
        using var temp = new TempDirectory();
        var layout = new ModelStorageLayout(temp.Path);
        ModelFixtures.AsrModel(layout.AsrModelPath("a"));

        Assert.True(ModelStorageMigrator.MoveRoot(layout, new ModelStorageLayout(temp.Path + Path.DirectorySeparatorChar)).IsEmpty);
    }

    // ── Start-up (pending move + legacy sort) ────────────────────────────

    [Fact]
    public void RunAtStartup_ShouldFinishAPendingMoveAndSwitchTheRoot()
    {
        using var temp = new TempDirectory();
        var oldRoot = new ModelStorageLayout(Path.Combine(temp.Path, "old"));
        var newPath = Path.Combine(temp.Path, "new");
        ModelFixtures.AsrModel(Path.Combine(oldRoot.Root, "flat-asr")); // still flat: sorted first, then moved
        ModelFixtures.AsrModel(oldRoot.AsrModelPath("sorted"));
        var settings = new AppSettings { ModelsRootPath = oldRoot.Root, PendingModelsRoot = newPath, ModelStorageNote = "old note" };

        var report = ModelStorageMigrator.RunAtStartup(settings);

        Assert.True(report.SettingsChanged);
        Assert.Equal(Path.GetFullPath(newPath), settings.ModelsRootPath);
        Assert.Equal("", settings.PendingModelsRoot);
        Assert.Equal("", settings.ModelStorageNote);
        Assert.True(Directory.Exists(Path.Combine(newPath, "asr", "flat-asr")));
        Assert.True(Directory.Exists(Path.Combine(newPath, "asr", "sorted")));
    }

    [Fact]
    public void RunAtStartup_ShouldSortALegacyFlatRootWithoutAPendingMove()
    {
        using var temp = new TempDirectory();
        LegacyRoot(temp);
        var settings = new AppSettings { ModelsRootPath = temp.Path };

        var report = ModelStorageMigrator.RunAtStartup(settings);

        Assert.Equal(5, report.Moved.Count);
        Assert.Equal(temp.Path, settings.ModelsRootPath);
        Assert.True(ModelStorageMigrator.RunAtStartup(settings).IsEmpty);
    }

    [Fact]
    public void RunAtStartup_ShouldKeepTheOldRootAndSayWhy_WhenTheMoveCannotFinish()
    {
        using var temp = new TempDirectory();
        var oldRoot = new ModelStorageLayout(Path.Combine(temp.Path, "old"));
        ModelFixtures.AsrModel(oldRoot.AsrModelPath("a"));
        var newPath = Path.Combine(temp.Path, "new");
        temp.Write("new/asr/a/clash.txt", "x");
        var settings = new AppSettings { ModelsRootPath = oldRoot.Root, PendingModelsRoot = newPath };

        ModelStorageMigrator.RunAtStartup(settings);

        Assert.Equal(oldRoot.Root, settings.ModelsRootPath);
        Assert.Equal("", settings.PendingModelsRoot);
        Assert.Contains("not moved", settings.ModelStorageNote);
        Assert.Contains(oldRoot.Root, settings.ModelStorageNote);
        Assert.True(Directory.Exists(oldRoot.AsrModelPath("a")));
    }

    [Fact]
    public void RunAtStartup_ShouldDropAnUnusablePendingFolder()
    {
        var settings = new AppSettings { PendingModelsRoot = "\0bad" };

        var report = ModelStorageMigrator.RunAtStartup(settings);

        Assert.True(report.SettingsChanged);
        Assert.Equal("", settings.PendingModelsRoot);
    }

    // ── Folder change choice ─────────────────────────────────────────────

    [Fact]
    public void ApplyRootChoice_ShouldMoveOnRestart_ByKeepingTheOldRootNow()
    {
        var original = new AppSettings { ModelsRootPath = "C:\\old" };
        var built = new AppSettings { ModelsRootPath = "C:\\new" };

        ModelStorageMigrator.ApplyRootChoice(built, original, ModelRootChoice.MoveOnRestart);

        Assert.Equal("C:\\old", built.ModelsRootPath);
        Assert.Equal("C:\\new", built.PendingModelsRoot);
    }

    [Fact]
    public void ApplyRootChoice_ShouldKeepTheModelsWhereTheyAre_BySwitchingAtOnce()
    {
        var original = new AppSettings { ModelsRootPath = "C:\\old", PendingModelsRoot = "C:\\older-plan", ModelStorageNote = "note" };
        var built = original.Clone();
        built.ModelsRootPath = "C:\\new";

        ModelStorageMigrator.ApplyRootChoice(built, original, ModelRootChoice.Keep);

        Assert.Equal("C:\\new", built.ModelsRootPath);
        Assert.Equal("", built.PendingModelsRoot);
        Assert.Equal("", built.ModelStorageNote);
    }

    [Fact]
    public void ApplyRootChoice_ShouldRestoreTheOldRoot_OnCancel()
    {
        var original = new AppSettings { ModelsRootPath = "C:\\old", PendingModelsRoot = "C:\\plan" };
        var built = new AppSettings { ModelsRootPath = "C:\\new" };

        ModelStorageMigrator.ApplyRootChoice(built, original, ModelRootChoice.Cancel);

        Assert.Equal("C:\\old", built.ModelsRootPath);
        Assert.Equal("C:\\plan", built.PendingModelsRoot);
    }

    [Fact]
    public void ApplyRootChoice_ShouldKeepAPendingMove_WhenTheFolderWasNotChanged()
    {
        var original = new AppSettings { ModelsRootPath = "C:\\old", PendingModelsRoot = "C:\\plan" };
        var built = original.Clone();

        ModelStorageMigrator.ApplyRootChoice(built, original, ModelRootChoice.Keep);

        Assert.Equal("C:\\plan", built.PendingModelsRoot);
    }

    // ── Settings compatibility ───────────────────────────────────────────

    [Fact]
    public void OldSettingsFiles_ShouldStillLoad_WithTheNewFieldsEmpty()
    {
        using var temp = new TempDirectory();
        var file = Path.Combine(temp.Path, "settings.json");
        File.WriteAllText(file, JsonSerializer.Serialize(new { ModelsRootPath = temp.Path, SelectedModel = "m", ModelPath = Path.Combine(temp.Path, "m"), Language = "ru" }));

        var settings = new SettingsService(file).Load();

        Assert.Equal(temp.Path, settings.ModelsRootPath);
        Assert.Equal("m", settings.SelectedModel);
        Assert.Equal("ru", settings.Language);
        Assert.Equal("", settings.PendingModelsRoot);
        Assert.Equal("", settings.ModelStorageNote);
    }

    [Fact]
    public void NewSettingsFields_ShouldRoundTrip()
    {
        using var temp = new TempDirectory();
        var service = new SettingsService(Path.Combine(temp.Path, "settings.json"));

        service.Save(new AppSettings { PendingModelsRoot = "D:\\models", ModelStorageNote = "n" });
        var loaded = service.Load();

        Assert.Equal("D:\\models", loaded.PendingModelsRoot);
        Assert.Equal("n", loaded.ModelStorageNote);
    }
}

public class ModelPathResolverLayoutTests
{
    [Fact]
    public void FindExistingModelPath_ShouldFindAModelInTheAsrFolder()
    {
        using var temp = new TempDirectory();
        var layout = new ModelStorageLayout(temp.Path);
        ModelFixtures.AsrModel(layout.AsrModelPath("installed"));
        var settings = new AppSettings { ModelsRootPath = temp.Path, SelectedModel = "installed" };

        Assert.Equal(layout.AsrModelPath("installed"), ModelPathResolver.FindExistingModelPath(settings));
    }

    [Fact]
    public void FindExistingModelPath_ShouldFindAFolderTheUserCopiedIntoAsr()
    {
        using var temp = new TempDirectory();
        var layout = new ModelStorageLayout(temp.Path);
        ModelFixtures.AsrModel(layout.AsrModelPath("copied-by-hand"));

        Assert.Equal(layout.AsrModelPath("copied-by-hand"), ModelPathResolver.FindExistingModelPath(new AppSettings { ModelsRootPath = temp.Path }));
    }

    [Fact]
    public void FindExistingModelPath_ShouldStillAcceptTheFlatLayoutUntilMigrated()
    {
        using var temp = new TempDirectory();
        ModelFixtures.AsrModel(Path.Combine(temp.Path, "flat"));

        Assert.Equal(Path.Combine(temp.Path, "flat"),
            ModelPathResolver.FindExistingModelPath(new AppSettings { ModelsRootPath = temp.Path, SelectedModel = "flat" }));
    }

    [Fact]
    public void LocateModelFolder_ShouldPreferAsr_ThenFlat_ThenTheAsrPathForANewModel()
    {
        using var temp = new TempDirectory();
        ModelFixtures.AsrModel(Path.Combine(temp.Path, "both"));
        ModelFixtures.AsrModel(Path.Combine(temp.Path, "asr", "both"));
        ModelFixtures.AsrModel(Path.Combine(temp.Path, "only-flat"));

        Assert.Equal(Path.Combine(temp.Path, "asr", "both"), ModelPathResolver.LocateModelFolder(temp.Path, "both"));
        Assert.Equal(Path.Combine(temp.Path, "only-flat"), ModelPathResolver.LocateModelFolder(temp.Path, "only-flat"));
        Assert.Equal(Path.Combine(temp.Path, "asr", "new"), ModelPathResolver.LocateModelFolder(temp.Path, "new"));
    }

    [Fact]
    public void ApplyExistingModelPath_ShouldKeepTheRootOfAModelInAsr()
    {
        using var temp = new TempDirectory();
        var layout = new ModelStorageLayout(temp.Path);
        ModelFixtures.AsrModel(layout.AsrModelPath("m"));
        var settings = new AppSettings { ModelsRootPath = temp.Path };

        Assert.True(ModelPathResolver.ApplyExistingModelPath(settings));

        Assert.Equal(layout.AsrModelPath("m"), settings.ModelPath);
        Assert.Equal(temp.Path, settings.ModelsRootPath); // never <root>/asr
        Assert.Equal("m", settings.SelectedModel);
    }

    [Fact]
    public void StorageRootOf_ShouldSkipTheAsrFolder()
    {
        using var temp = new TempDirectory();

        Assert.Equal(temp.Path, ModelPathResolver.StorageRootOf(Path.Combine(temp.Path, "asr", "m")));
        Assert.Equal(temp.Path, ModelPathResolver.StorageRootOf(Path.Combine(temp.Path, "m")));
    }

    [Fact]
    public void DefaultRoots_ShouldListAsrBeforeTheFlatRoot_AndScanFindsBoth()
    {
        using var temp = new TempDirectory();
        var layout = new ModelStorageLayout(temp.Path);
        ModelFixtures.AsrModel(layout.AsrModelPath("in-asr"));
        ModelFixtures.AsrModel(Path.Combine(temp.Path, "flat"));
        var settings = new AppSettings { ModelsRootPath = temp.Path };

        var roots = InstalledAsrModels.DefaultRoots(settings);

        Assert.Equal(layout.AsrDir, roots[0]);
        Assert.Equal(layout.Root, roots[1]);
        var found = InstalledAsrModels.Scan(roots).Select(m => m.Folder).ToList();
        Assert.Contains("in-asr", found);
        Assert.Contains("flat", found);
        Assert.NotNull(InstalledAsrModels.Find("in-asr", roots));
    }

    [Fact]
    public void DiarizationRoots_ShouldPreferTheDiarizationFolderThenTheFlatRoot()
    {
        using var temp = new TempDirectory();
        var layout = new ModelStorageLayout(temp.Path);
        var settings = new AppSettings { ModelsRootPath = temp.Path };

        var roots = DiarizationModels.Roots(settings);

        Assert.Equal(layout.DiarizationDir, roots[0]);
        Assert.Equal(layout.Root, roots[1]);

        // Legacy flat install is found until migrated, then at its new place.
        ModelFixtures.DiarizationModel(Path.Combine(temp.Path, DiarizationModelLocator.Int8FolderName));
        Assert.Single(DiarizationModels.Installed(layout));
        ModelStorageMigrator.Migrate(layout);
        var installed = Assert.Single(DiarizationModels.Installed(layout));
        Assert.Equal(Path.GetFullPath(layout.DiarizationModelPath(DiarizationModelLocator.Int8FolderName)), installed.Folder);
        Assert.NotNull(DiarizationModelLocator.Find(roots));
    }

    [Fact]
    public void DiarizationInstalled_ShouldIgnoreAnIncompleteFolder()
    {
        using var temp = new TempDirectory();
        var layout = new ModelStorageLayout(temp.Path);
        var folder = layout.DiarizationModelPath(DiarizationModelLocator.Int8FolderName);
        ModelFixtures.DiarizationModel(folder);
        File.Delete(Path.Combine(folder, DiarizationModelLocator.ConfigFileName));

        Assert.Empty(DiarizationModels.Installed(layout));
    }
}
