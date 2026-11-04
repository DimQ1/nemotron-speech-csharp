using System.Text.Json.Nodes;
using VoiceType.Uno.Services;
using Xunit;

namespace VoiceType.Uno.Tests.Settings;

public class WinUiDataImportTests
{
    private const string Gemma = "gemma-4-E2B-it.litertlm";

    /// <summary>A settings.json as VoiceType.WinUI 1.3.4 writes it (enum as a number, WinUI-only keys, no History).</summary>
    private static string WinUiSettings(string dataRoot, string modelsRoot, string modelPath, int injection = 0) => new JsonObject
    {
        ["ModelsRootPath"] = modelsRoot,
        ["SelectedModel"] = Path.GetFileName(modelPath),
        ["ModelPath"] = modelPath,
        ["ExecutionProvider"] = "webgpu:hp",
        ["Language"] = "ru",
        ["UseVad"] = false,
        ["AudioSource"] = "Mix",
        ["FirstRunCompleted"] = true,
        ["TextInjectionMethod"] = injection,
        ["StopOnAnyInput"] = false,
        ["IsTextInjectionEnabled"] = false,
        ["AlwaysOnTop"] = false,
        ["SaveSessions"] = true,
        ["SessionsPath"] = Path.Combine(dataRoot, "Sessions"),
        ["SaveAudioMp3"] = true,
        ["PostProcessingEnabled"] = true,
        ["PostProcessingRules"] = new JsonArray(new JsonObject { ["Name"] = "tags", ["Pattern"] = "<..>", ["Replacement"] = "", ["Enabled"] = true }),
        ["ToggleHotkey"] = "Ctrl+Alt+D",
        ["MuteHotkey"] = "Ctrl+Shift+M",
        ["InjectTextHotkey"] = "Ctrl+Shift+I",
        ["DownloaderModelsRootPath"] = modelsRoot,
        ["DownloaderSelectedFolders"] = new JsonArray(),
        ["MicVolume"] = 1,
        ["LoopbackVolume"] = 1,
        ["TranslationEnabled"] = true,
        ["TranslationTargetLanguage"] = "en",
        ["TranslationComputeBackend"] = "gpu",
    }.ToJsonString();

    private static string SettingsPath(TempDirectory temp) => Path.Combine(temp.Path, "settings.json");

    private static AppSettings Load(TempDirectory temp) => new SettingsService(SettingsPath(temp)).Load();

    [Fact]
    public void RunAtStartup_ShouldTakeOverTheWinUiSettings()
    {
        using var temp = new TempDirectory();
        var models = Path.Combine(temp.Path, "models");
        temp.Write("settings.json", WinUiSettings(temp.Path, models, Path.Combine(models, "my-asr")));

        var report = WinUiDataImport.RunAtStartup(temp.Path);

        Assert.True(report.SettingsImported);
        Assert.Empty(report.DroppedSettings);
        var settings = Load(temp);
        Assert.Equal("webgpu:hp", settings.ExecutionProvider);
        Assert.Equal("ru", settings.Language);
        Assert.False(settings.UseVad);
        Assert.Equal("Mix", settings.AudioSource);
        Assert.True(settings.FirstRunCompleted);
        Assert.False(settings.IsTextInjectionEnabled);
        Assert.False(settings.AlwaysOnTop);
        Assert.True(settings.SaveAudioMp3);
        Assert.Equal("Ctrl+Alt+D", settings.ToggleHotkey);
        Assert.True(settings.TranslationEnabled);
        Assert.Equal("en", settings.TranslationTargetLanguage);
        Assert.Equal("gpu", settings.TranslationComputeBackend);
        Assert.Equal("my-asr", settings.SelectedModel);
        Assert.Equal(Path.Combine(models, "my-asr"), settings.ModelPath);
        Assert.Equal("type", settings.TextInjectionMethod);
        // The default models folder is the Uno default; WinUI sessions stay where they are, recordings use the Uno default.
        Assert.Equal("", settings.ModelsRootPath);
        Assert.Equal("", settings.SessionsPath);
        // The file is now a Uno settings file.
        Assert.False(WinUiDataImport.IsWinUiSettings((JsonObject)JsonNode.Parse(File.ReadAllText(SettingsPath(temp)))!));
    }

    [Fact]
    public void RunAtStartup_ShouldKeepTheWinUiOriginalAsABackup()
    {
        using var temp = new TempDirectory();
        var original = WinUiSettings(temp.Path, "", "");
        temp.Write("settings.json", original);

        WinUiDataImport.RunAtStartup(temp.Path);

        Assert.Equal(original, File.ReadAllText(Path.Combine(temp.Path, WinUiDataImport.BackupFileName)));
    }

    [Fact]
    public void RunAtStartup_ShouldMapClipboardInjectionToPaste()
    {
        using var temp = new TempDirectory();
        temp.Write("settings.json", WinUiSettings(temp.Path, "", "", injection: 2));

        WinUiDataImport.RunAtStartup(temp.Path);

        Assert.Equal("paste", Load(temp).TextInjectionMethod);
    }

    [Fact]
    public void RunAtStartup_ShouldKeepACustomModelsFolder()
    {
        using var temp = new TempDirectory();
        var custom = temp.CreateSubdirectory("my-models");
        temp.Write("settings.json", WinUiSettings(temp.Path, custom, Path.Combine(custom, "my-asr")));

        WinUiDataImport.RunAtStartup(temp.Path);

        Assert.Equal(custom, Load(temp).ModelsRootPath);
    }

    [Fact]
    public void RunAtStartup_ShouldUseDefaultsForValuesItCannotRead()
    {
        using var temp = new TempDirectory();
        var json = (JsonObject)JsonNode.Parse(WinUiSettings(temp.Path, "", ""))!;
        json["Language"] = 5;
        temp.Write("settings.json", json.ToJsonString());

        var report = WinUiDataImport.RunAtStartup(temp.Path);

        Assert.Equal(["Language"], report.DroppedSettings);
        var settings = Load(temp);
        Assert.Equal(new AppSettings().Language, settings.Language);
        Assert.Equal("Ctrl+Alt+D", settings.ToggleHotkey);
    }

    [Fact]
    public void RunAtStartup_ShouldRewritePathsUnderTheVirtualDataRoot()
    {
        using var temp = new TempDirectory();
        var real = temp.CreateSubdirectory("real");
        var virtualRoot = Path.Combine(temp.Path, "virtual");
        var settingsPath = Path.Combine(real, "settings.json");
        File.WriteAllText(settingsPath, WinUiSettings(virtualRoot, Path.Combine(virtualRoot, "models"), Path.Combine(virtualRoot, "models", "my-asr")));

        WinUiDataImport.RunAtStartup(real, virtualRoot);

        var settings = new SettingsService(settingsPath).Load();
        Assert.Equal("", settings.ModelsRootPath);
        Assert.Equal(Path.Combine(real, "models", "my-asr"), settings.ModelPath);
    }

    [Fact]
    public void RunAtStartup_ShouldLeaveUnoSettingsAlone()
    {
        using var temp = new TempDirectory();
        var service = new SettingsService(SettingsPath(temp));
        service.Save(new AppSettings { Language = "de", TextInjectionMethod = "paste" });
        var before = File.ReadAllText(SettingsPath(temp));

        var report = WinUiDataImport.RunAtStartup(temp.Path);

        Assert.False(report.SettingsImported);
        Assert.Equal(before, File.ReadAllText(SettingsPath(temp)));
        Assert.False(File.Exists(Path.Combine(temp.Path, WinUiDataImport.BackupFileName)));
    }

    [Fact]
    public void RunAtStartup_ShouldDoNothingWithoutData()
    {
        using var temp = new TempDirectory();

        var report = WinUiDataImport.RunAtStartup(temp.Path);

        Assert.Equal(WinUiImportReport.Empty, report);
        Assert.False(File.Exists(SettingsPath(temp)));
    }

    [Fact]
    public void RunAtStartup_ShouldMoveTheTranslationModelIntoLlm()
    {
        using var temp = new TempDirectory();
        temp.Write("settings.json", WinUiSettings(temp.Path, "", ""));
        temp.Write($"models/Translation/{Gemma}", "gemma");
        temp.Write($"models/Translation/{Gemma}.xnnpack_cache_1_2", "cache");

        var report = WinUiDataImport.RunAtStartup(temp.Path);

        var llm = new ModelStorageLayout(Path.Combine(temp.Path, "models")).LlmDir;
        Assert.Equal(2, report.Models.Moved.Count);
        Assert.Equal("gemma", File.ReadAllText(Path.Combine(llm, Gemma)));
        Assert.True(File.Exists(Path.Combine(llm, $"{Gemma}.xnnpack_cache_1_2")));
        Assert.False(Directory.Exists(Path.Combine(temp.Path, "models", WinUiDataImport.TranslationFolderName)));
        Assert.Equal("", report.Note);
    }

    [Fact]
    public void RunAtStartup_ShouldMoveTheTranslationModelIntoACustomModelsFolderOnTheSameDrive()
    {
        using var temp = new TempDirectory();
        var custom = temp.CreateSubdirectory("my-models");
        temp.Write("settings.json", WinUiSettings(temp.Path, custom, ""));
        temp.Write($"models/Translation/{Gemma}", "gemma");

        WinUiDataImport.RunAtStartup(temp.Path);

        Assert.True(File.Exists(new ModelStorageLayout(custom).LlmFilePath(Gemma)));
    }

    [Fact]
    public void RunAtStartup_ShouldNeverOverwriteAModelThatIsAlreadyThere()
    {
        using var temp = new TempDirectory();
        temp.Write($"models/Translation/{Gemma}", "old");
        temp.Write($"models/llm/{Gemma}", "new");

        var report = WinUiDataImport.RunAtStartup(temp.Path);

        Assert.Single(report.Models.Conflicts);
        Assert.Equal("new", File.ReadAllText(Path.Combine(temp.Path, "models", "llm", Gemma)));
        Assert.Equal("old", File.ReadAllText(Path.Combine(temp.Path, "models", "Translation", Gemma)));
    }

    [Fact]
    public void RunAtStartup_ShouldBeIdempotent()
    {
        using var temp = new TempDirectory();
        temp.Write("settings.json", WinUiSettings(temp.Path, "", ""));
        temp.Write($"models/Translation/{Gemma}", "gemma");
        WinUiDataImport.RunAtStartup(temp.Path);
        var settings = File.ReadAllText(SettingsPath(temp));

        var again = WinUiDataImport.RunAtStartup(temp.Path);

        Assert.False(again.SettingsImported);
        Assert.True(again.Models.IsEmpty);
        Assert.Equal(settings, File.ReadAllText(SettingsPath(temp)));
    }

    [Fact]
    public void RunAtStartup_ShouldClearThePreviousVersionNoteOnceTheModelWasBroughtOver()
    {
        using var temp = new TempDirectory();
        var service = new SettingsService(SettingsPath(temp));
        service.Save(new AppSettings { ModelStorageNote = ModelStorageText.PreviousVersionModelNote("C:\\a\\llm", "D:\\b\\llm") });
        temp.Write($"models/llm/{Gemma}", "gemma");

        WinUiDataImport.RunAtStartup(temp.Path);
        Assert.NotEqual("", service.Load().ModelStorageNote);

        File.Delete(Path.Combine(temp.Path, "models", "llm", Gemma));
        WinUiDataImport.RunAtStartup(temp.Path);
        Assert.Equal("", service.Load().ModelStorageNote);
    }

    [WindowsFact("Windows only: WinUI writes Models\\ and the Uno app reads models\\, one folder only on a case-insensitive file system.")]
    public void RunAtStartup_ThenModelStorageMigration_ShouldSortTheWinUiModelsFolder()
    {
        using var temp = new TempDirectory();
        var winUiModels = Path.Combine(temp.Path, "Models");
        ModelFixtures.AsrModel(Path.Combine(winUiModels, "my-asr"));
        temp.Write($"Models/Translation/{Gemma}", "gemma");
        temp.Write("Sessions/2026-10-01_120000.json", "{}");
        temp.Write("settings.json", WinUiSettings(temp.Path, winUiModels, Path.Combine(winUiModels, "my-asr")));

        WinUiDataImport.RunAtStartup(temp.Path);
        var settings = Load(temp);
        // What App.PrepareModelStorage does next, with the Uno default root of this data folder.
        settings.ModelsRootPath = Path.Combine(temp.Path, "models");
        var report = ModelStorageMigrator.RunAtStartup(settings);

        var layout = new ModelStorageLayout(Path.Combine(temp.Path, "models"));
        Assert.True(report.IsComplete);
        Assert.True(File.Exists(Path.Combine(layout.AsrModelPath("my-asr"), "genai_config.json")));
        Assert.True(File.Exists(layout.LlmFilePath(Gemma)));
        Assert.Equal(layout.AsrModelPath("my-asr"), settings.ModelPath);
        Assert.True(File.Exists(Path.Combine(temp.Path, "Sessions", "2026-10-01_120000.json")));
    }
}
