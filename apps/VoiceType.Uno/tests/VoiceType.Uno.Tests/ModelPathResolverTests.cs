using VoiceType.Uno.Services;
using Xunit;

namespace VoiceType.Uno.Tests;

public class ModelPathResolverTests
{
    private const string GenAiConfig = """{"decoder":{"filename":"decoder.onnx"}}""";

    [Fact]
    public void CheckIntegrity_ShouldReportMissing_WhenTheDirectoryDoesNotExist()
    {
        var path = Path.Combine(Path.GetTempPath(), Guid.NewGuid().ToString("N"));

        Assert.Equal(ModelPathResolver.ModelIntegrity.Missing, ModelPathResolver.CheckIntegrity(path));
    }

    [Fact]
    public void CheckIntegrity_ShouldReportMissing_WhenThePathIsBlank()
    {
        Assert.Equal(ModelPathResolver.ModelIntegrity.Missing, ModelPathResolver.CheckIntegrity("  "));
    }

    [Fact]
    public void CheckIntegrity_ShouldReportComplete_WhenEveryReferencedFileIsPresent()
    {
        using var root = new TempDirectory();
        CreateGenAiModel(root, "model");

        Assert.Equal(ModelPathResolver.ModelIntegrity.Complete, ModelPathResolver.CheckIntegrity(Path.Combine(root.Path, "model")));
    }

    [Fact]
    public void CheckIntegrity_ShouldReportBroken_WhenAReferencedFileIsEmpty()
    {
        using var root = new TempDirectory();
        root.Write("model/genai_config.json", GenAiConfig);
        root.Write("model/decoder.onnx", "");

        Assert.Equal(ModelPathResolver.ModelIntegrity.Broken, ModelPathResolver.CheckIntegrity(Path.Combine(root.Path, "model")));
    }

    [Fact]
    public void CheckIntegrity_ShouldReportBroken_WhenAReferencedFileIsAbsent()
    {
        using var root = new TempDirectory();
        root.Write("model/genai_config.json", GenAiConfig);

        Assert.Equal(ModelPathResolver.ModelIntegrity.Broken, ModelPathResolver.CheckIntegrity(Path.Combine(root.Path, "model")));
    }

    [Fact]
    public void CheckIntegrity_ShouldReportBroken_WhenAStalePartFileRemains()
    {
        using var root = new TempDirectory();
        CreateGenAiModel(root, "model");
        root.Write("model/encoder.onnx.part", "partial");

        Assert.Equal(ModelPathResolver.ModelIntegrity.Broken, ModelPathResolver.CheckIntegrity(Path.Combine(root.Path, "model")));
    }

    [Fact]
    public void FindExistingModelPath_ShouldPreferTheConfiguredRootAndSelection()
    {
        using var root = new TempDirectory();
        CreateGenAiModel(root, "installed-model");
        var settings = new AppSettings { ModelsRootPath = root.Path, SelectedModel = "installed-model" };

        Assert.Equal(Path.Combine(root.Path, "installed-model"), ModelPathResolver.FindExistingModelPath(settings));
    }

    [Fact]
    public void FindExistingModelPath_ShouldIgnoreABrokenInstallation()
    {
        using var root = new TempDirectory();
        root.Write("broken/genai_config.json", GenAiConfig);
        root.Write("broken/decoder.onnx", "");
        var settings = new AppSettings { ModelsRootPath = root.Path, SelectedModel = "broken" };

        // The only candidate under the configured root is incomplete, so the resolver has
        // to fall through to whatever the machine has elsewhere (typically nothing).
        var resolved = ModelPathResolver.FindExistingModelPath(settings);

        Assert.NotEqual(Path.Combine(root.Path, "broken"), resolved);
    }

    [Fact]
    public void ApplyExistingModelPath_ShouldNormalizePathRootAndSelection()
    {
        using var root = new TempDirectory();
        CreateGenAiModel(root, "installed-model");
        var settings = new AppSettings { ModelsRootPath = root.Path };

        Assert.True(ModelPathResolver.ApplyExistingModelPath(settings));
        Assert.Equal(Path.Combine(root.Path, "installed-model"), settings.ModelPath);
        Assert.Equal(root.Path, settings.ModelsRootPath);
        Assert.Equal("installed-model", settings.SelectedModel);
    }

    [Fact]
    public void ApplyExistingModelPath_ShouldReportNoChange_WhenNothingIsStored()
    {
        using var root = new TempDirectory();
        var settings = new AppSettings { ModelsRootPath = root.Path };

        // An empty root resolves to no model at all, so there is nothing to adopt — unless
        // the machine has an installed model elsewhere, in which case the call reports it.
        var changed = ModelPathResolver.ApplyExistingModelPath(settings);

        Assert.Equal(changed, !string.IsNullOrEmpty(settings.ModelPath));
    }

    private static void CreateGenAiModel(TempDirectory root, string folderName)
    {
        root.Write($"{folderName}/genai_config.json", GenAiConfig);
        root.Write($"{folderName}/decoder.onnx", "weights");
    }
}
