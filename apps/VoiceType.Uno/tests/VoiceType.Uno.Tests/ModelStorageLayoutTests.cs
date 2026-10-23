using SpeechLib.Diarization;
using VoiceType.Uno.Services;
using Xunit;

namespace VoiceType.Uno.Tests;

/// <summary>Shared builders for model folders in a temp models root.</summary>
internal static class ModelFixtures
{
    private const string GenAiConfig = """{"decoder":{"filename":"decoder.onnx"}}""";

    /// <summary>A complete Nemotron-style speech model folder (<c>genai_config.json</c> manifest) at <paramref name="path"/>.</summary>
    public static void AsrModel(string path)
    {
        Directory.CreateDirectory(path);
        File.WriteAllText(Path.Combine(path, "genai_config.json"), GenAiConfig);
        File.WriteAllText(Path.Combine(path, "decoder.onnx"), "weights");
    }

    /// <summary>A valid diarization folder (sparse files of the sizes the locator expects).</summary>
    public static void DiarizationModel(string path, bool fp32 = false)
    {
        Directory.CreateDirectory(path);
        Sparse(Path.Combine(path, DiarizationModelLocator.FrontendFileName), 1_186_102);
        Sparse(Path.Combine(path, DiarizationModelLocator.ConfigFileName), 13_184);
        if (fp32)
            Sparse(Path.Combine(path, DiarizationModelLocator.Fp32StepFileName), 397_841_111);
        else
            Sparse(Path.Combine(path, DiarizationModelLocator.Int8StepFileName), 104_653_656);
    }

    public static void Sparse(string path, long length)
    {
        using var stream = File.Create(path);
        stream.SetLength(length);
    }
}

public class ModelStorageLayoutTests
{
    [Fact]
    public void Paths_ShouldPutEachKindInItsOwnSubfolderOfTheRoot()
    {
        using var temp = new TempDirectory();
        var layout = new ModelStorageLayout(temp.Path);

        Assert.Equal(Path.Combine(temp.Path, "asr"), layout.AsrDir);
        Assert.Equal(Path.Combine(temp.Path, "llm"), layout.LlmDir);
        Assert.Equal(Path.Combine(temp.Path, "diarization"), layout.DiarizationDir);
        Assert.Equal(Path.Combine(temp.Path, "asr", "parakeet"), layout.AsrModelPath("parakeet"));
        Assert.Equal(Path.Combine(temp.Path, "llm", "gemma.litertlm"), layout.LlmFilePath("gemma.litertlm"));
        Assert.Equal(Path.Combine(temp.Path, "diarization", "nemotron-3-diarization-onnx-int8"),
            layout.DiarizationModelPath(DiarizationModelLocator.Int8FolderName));
        Assert.Equal(layout.AsrDir, layout.DirOf(ModelKind.Speech));
        Assert.Equal(layout.LlmDir, layout.DirOf(ModelKind.Language));
        Assert.Equal(layout.DiarizationDir, layout.DirOf(ModelKind.Speaker));
    }

    [Fact]
    public void ScanRoots_ShouldListTheKindFolderBeforeTheLegacyFlatRoot()
    {
        using var temp = new TempDirectory();
        var layout = new ModelStorageLayout(temp.Path);

        Assert.Equal([layout.AsrDir, layout.Root], layout.AsrScanRoots);
        Assert.Equal([layout.DiarizationDir, layout.Root], layout.DiarizationScanRoots);
    }

    [Fact]
    public void Resolve_ShouldUseTheConfiguredRootOrTheDefault()
    {
        using var temp = new TempDirectory();

        Assert.Equal(Path.GetFullPath(temp.Path), ModelStorageLayout.Resolve(new AppSettings { ModelsRootPath = temp.Path }).Root);
        Assert.Equal(Path.GetFullPath(AppPaths.ModelsDir), ModelStorageLayout.Resolve(new AppSettings()).Root);
        Assert.Equal(Path.GetFullPath(AppPaths.ModelsDir), ModelStorageLayout.Resolve("  ", AppPaths.ModelsDir).Root);
    }

    [Fact]
    public void Constructor_ShouldRejectABlankRoot() =>
        Assert.Throws<ArgumentException>(() => new ModelStorageLayout(" "));

    [Fact]
    public void EnsureDirectories_ShouldCreateTheThreeFolders_AndBeIdempotent()
    {
        using var temp = new TempDirectory();
        var layout = new ModelStorageLayout(Path.Combine(temp.Path, "models"));

        layout.EnsureDirectories();
        layout.EnsureDirectories();

        Assert.True(Directory.Exists(layout.AsrDir));
        Assert.True(Directory.Exists(layout.LlmDir));
        Assert.True(Directory.Exists(layout.DiarizationDir));
    }

    [Fact]
    public void Contains_ShouldBeTrueOnlyInsideTheRoot()
    {
        using var temp = new TempDirectory();
        var layout = new ModelStorageLayout(temp.Path);

        Assert.True(layout.Contains(Path.Combine(temp.Path, "asr", "x")));
        Assert.False(layout.Contains(temp.Path));
        Assert.False(layout.Contains(temp.Path + "-other"));
        Assert.False(layout.Contains(null));
    }

    [Fact]
    public void Current_ShouldFollowTheRegisteredRootProvider()
    {
        using var temp = new TempDirectory();
        try
        {
            ModelStorageLayout.UseRootProvider(() => temp.Path);
            Assert.Equal(Path.GetFullPath(temp.Path), ModelStorageLayout.Current.Root);
            Assert.Equal(Path.Combine(Path.GetFullPath(temp.Path), "llm", "x.litertlm"), ModelStorageLayout.Current.LlmFilePath("x.litertlm"));
        }
        finally
        {
            ModelStorageLayout.UseRootProvider(static () => null);
        }
        Assert.Equal(Path.GetFullPath(AppPaths.ModelsDir), ModelStorageLayout.Current.Root);
    }

    [Fact]
    public void LlmModelInfo_ShouldLiveInTheLlmFolderOfTheCurrentRoot()
    {
        using var temp = new TempDirectory();
        try
        {
            ModelStorageLayout.UseRootProvider(() => temp.Path);
            Assert.Equal(Path.Combine(Path.GetFullPath(temp.Path), "llm", "gemma-4-E2B-it.litertlm"), LlmModelCatalog.Default.LocalPath);
            Assert.False(LlmModelCatalog.Default.IsInstalled);

            Directory.CreateDirectory(Path.Combine(temp.Path, "llm"));
            File.WriteAllText(LlmModelCatalog.Default.LocalPath, "model");
            Assert.True(LlmModelCatalog.Default.IsInstalled);
        }
        finally
        {
            ModelStorageLayout.UseRootProvider(static () => null);
        }
    }
}

public class ModelStorageSummaryTests
{
    [Fact]
    public void Summarize_ShouldCountAndSizeEveryKind()
    {
        using var temp = new TempDirectory();
        var layout = new ModelStorageLayout(temp.Path);
        ModelFixtures.AsrModel(layout.AsrModelPath("my-model"));
        Directory.CreateDirectory(layout.LlmDir);
        File.WriteAllText(layout.LlmFilePath("gemma.litertlm"), "0123456789");
        ModelFixtures.DiarizationModel(layout.DiarizationModelPath(DiarizationModelLocator.Int8FolderName));

        var summary = ModelStorageScanner.Summarize(layout);

        Assert.Equal(1, summary.Line(ModelKind.Speech).Count);
        Assert.Equal(1, summary.Line(ModelKind.Language).Count);
        Assert.Equal(1, summary.Line(ModelKind.Speaker).Count);
        Assert.Equal(104_653_656L + 1_186_102 + 13_184, summary.Line(ModelKind.Speaker).Bytes);
        Assert.Equal(3, summary.TotalCount);
        Assert.Equal("asr", summary.Line(ModelKind.Speech).Folder);
    }

    [Fact]
    public void Summarize_ShouldCountLegacyFlatModelsUntilTheyAreMigrated()
    {
        using var temp = new TempDirectory();
        var layout = new ModelStorageLayout(temp.Path);
        ModelFixtures.AsrModel(Path.Combine(temp.Path, "flat-model"));
        File.WriteAllText(Path.Combine(temp.Path, "gemma.litertlm"), "x");
        File.WriteAllText(Path.Combine(temp.Path, "gemma.litertlm.part"), "partial");

        var summary = ModelStorageScanner.Summarize(layout);

        Assert.Equal(1, summary.Line(ModelKind.Speech).Count);
        Assert.Equal(1, summary.Line(ModelKind.Language).Count); // the .part is not a model
        Assert.Equal("x"u8.Length + "partial"u8.Length, summary.Line(ModelKind.Language).Bytes);
    }

    [Fact]
    public void KindLine_ShouldShowTitleFolderCountAndSize()
    {
        var line = new ModelStorageKindLine(ModelKind.Speech, "asr", "x", 2, 1_500_000_000);

        Assert.Equal("Speech recognition · asr/ · 2 models · " + DownloadProgressFormatter.FormatBytes(1_500_000_000), line.Text);
        Assert.Equal("Language models (Gemma) · llm/ · none yet", new ModelStorageKindLine(ModelKind.Language, "llm", "x", 0, 0).Text);
        Assert.Equal("Speaker detection · diarization/ · 1 model · " + DownloadProgressFormatter.FormatBytes(105_000_000),
            new ModelStorageKindLine(ModelKind.Speaker, "diarization", "x", 1, 105_000_000).Text);
    }

    [Fact]
    public void Total_ShouldAddFreeSpaceWhenKnown()
    {
        Assert.Equal("Total " + DownloadProgressFormatter.FormatBytes(5_000_000_000) + " · " + DownloadProgressFormatter.FormatBytes(200_000_000_000) + " free",
            ModelStorageText.Total(5_000_000_000, 200_000_000_000));
        Assert.Equal("Total " + DownloadProgressFormatter.FormatBytes(0), ModelStorageText.Total(0, null));
    }

    [Fact]
    public void FreeBytes_ShouldWalkUpToAnExistingFolder()
    {
        using var temp = new TempDirectory();

        Assert.True(ModelStorageScanner.FreeBytes(Path.Combine(temp.Path, "not", "yet", "created")) > 0);
    }

    [Fact]
    public void Text_ShouldExplainOneRootAndTheKindFolders()
    {
        Assert.Contains("one folder", ModelStorageText.Explanation);
        Assert.Contains("sub-folder", ModelStorageText.Explanation);
        Assert.Equal("Model storage", ModelStorageText.Title);
        using var temp = new TempDirectory();
        var layout = new ModelStorageLayout(temp.Path);
        Assert.Contains(layout.DiarizationDir, ModelStorageText.PlaceFilesText(ModelKind.Speaker, layout));
        Assert.Contains(layout.AsrDir, ModelStorageText.PlaceFilesText(ModelKind.Speech, layout));
        Assert.Contains(layout.LlmDir, ModelStorageText.PlaceFilesText(ModelKind.Language, layout));
        Assert.Contains(layout.AsrDir, ModelStorageText.StoredIn(ModelKind.Speech, layout));
    }

    [Fact]
    public void MoveQuestion_ShouldOnlyBeAskedForAnotherFolderThatHoldsModels()
    {
        using var temp = new TempDirectory();
        var withModels = new ModelStorageSummary(temp.Path, [new ModelStorageKindLine(ModelKind.Speech, "asr", "x", 1, 10)], null, "");
        var empty = new ModelStorageSummary(temp.Path, [new ModelStorageKindLine(ModelKind.Speech, "asr", "x", 0, 0)], null, "");

        Assert.True(ModelStorageText.NeedsMoveQuestion(temp.Path, Path.Combine(temp.Path, "other"), withModels));
        Assert.False(ModelStorageText.NeedsMoveQuestion(temp.Path, Path.Combine(temp.Path, "other"), empty));
        Assert.False(ModelStorageText.NeedsMoveQuestion(temp.Path, temp.Path + Path.DirectorySeparatorChar, withModels));
        Assert.False(ModelStorageText.NeedsMoveQuestion(temp.Path, " ", withModels));
    }

    [Fact]
    public void MoveQuestion_ShouldNotPromiseACopyBetweenDrives()
    {
        var summary = new ModelStorageSummary("C:\\a", [new ModelStorageKindLine(ModelKind.Speech, "asr", "x", 2, 1000)], null, "");

        Assert.Contains("restart", ModelStorageText.MoveQuestion("C:\\a", "C:\\b", summary, sameDrive: true));
        var other = ModelStorageText.MoveQuestion("C:\\a", "D:\\b", summary, sameDrive: false);
        Assert.Contains("not copied", other);
        Assert.Contains("stay in C:\\a", other);
    }
}
