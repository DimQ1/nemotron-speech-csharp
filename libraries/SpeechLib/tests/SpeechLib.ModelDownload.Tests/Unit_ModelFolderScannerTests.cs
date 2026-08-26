using SpeechLib.ModelDownload;
using Xunit;

namespace SpeechLib.ModelDownload.Tests;

public sealed class Unit_ModelFolderScannerTests : IDisposable
{
    private readonly string _root;

    public Unit_ModelFolderScannerTests()
    {
        _root = Path.Combine(Path.GetTempPath(), "voicetype-modelscan-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_root);
    }

    public void Dispose()
    {
        try { Directory.Delete(_root, recursive: true); }
        catch (IOException) { }
        catch (UnauthorizedAccessException) { }
    }

    // ---- IsModelDirectory ----

    [Fact]
    public void IsModelDirectory_NemotronGenAiExport_ReturnsTrue()
    {
        var dir = CreateFolder("nemotron-3.5-asr-streaming-0.6b-onnx-int4-cpu");
        File.WriteAllText(Path.Combine(dir, "genai_config.json"), "{}");

        Assert.True(ModelFolderScanner.IsModelDirectory(dir));
    }

    [Fact]
    public void IsModelDirectory_ParakeetTdtExport_ReturnsTrue()
    {
        var dir = CreateFolder("parakeet-tdt-0.6b-v3-onnx-int4");
        File.WriteAllText(Path.Combine(dir, "config.json"),
            """{"model_type": "nemo-conformer-tdt", "sample_rate": 16000}""");

        Assert.True(ModelFolderScanner.IsModelDirectory(dir));
    }

    [Fact]
    public void IsModelDirectory_ConfigWithOtherModelType_ReturnsFalse()
    {
        var dir = CreateFolder("some-other-model");
        File.WriteAllText(Path.Combine(dir, "config.json"),
            """{"model_type": "whisper"}""");

        Assert.False(ModelFolderScanner.IsModelDirectory(dir));
    }

    [Fact]
    public void IsModelDirectory_ConfigWithoutModelType_ReturnsFalse()
    {
        var dir = CreateFolder("no-model-type");
        File.WriteAllText(Path.Combine(dir, "config.json"), """{"sample_rate": 16000}""");

        Assert.False(ModelFolderScanner.IsModelDirectory(dir));
    }

    [Fact]
    public void IsModelDirectory_InvalidJson_ReturnsFalse()
    {
        var dir = CreateFolder("broken-config");
        File.WriteAllText(Path.Combine(dir, "config.json"), "{ not valid json");

        Assert.False(ModelFolderScanner.IsModelDirectory(dir));
    }

    [Fact]
    public void IsModelDirectory_EmptyFolder_ReturnsFalse()
    {
        var dir = CreateFolder("empty-folder");

        Assert.False(ModelFolderScanner.IsModelDirectory(dir));
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    public void IsModelDirectory_NullOrWhitespace_ReturnsFalse(string? path)
    {
        Assert.False(ModelFolderScanner.IsModelDirectory(path));
    }

    [Fact]
    public void IsModelDirectory_MissingFolder_ReturnsFalse()
    {
        Assert.False(ModelFolderScanner.IsModelDirectory(Path.Combine(_root, "does-not-exist")));
    }

    // ---- ScanModelFolderNames ----

    [Fact]
    public void ScanModelFolderNames_MixedRoot_ReturnsOnlyModelFoldersSorted()
    {
        WriteNemotron("nemotron-b");
        WriteParakeet("parakeet-int4");
        WriteNemotron("Nemotron-a");
        CreateFolder("Translation");
        File.WriteAllText(Path.Combine(_root, "readme.txt"), "not a folder");

        var names = ModelFolderScanner.ScanModelFolderNames(_root);

        Assert.Equal(new[] { "Nemotron-a", "nemotron-b", "parakeet-int4" }, names);
    }

    [Fact]
    public void ScanModelFolderNames_ParakeetOnly_ReturnsParakeet()
    {
        WriteParakeet("parakeet-tdt-0.6b-v3-onnx-int4");

        var names = ModelFolderScanner.ScanModelFolderNames(_root);

        Assert.Equal(new[] { "parakeet-tdt-0.6b-v3-onnx-int4" }, names);
    }

    [Fact]
    public void ScanModelFolderNames_EmptyRoot_ReturnsEmpty()
    {
        Assert.Empty(ModelFolderScanner.ScanModelFolderNames(_root));
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    public void ScanModelFolderNames_NullOrEmpty_ReturnsEmpty(string? root)
    {
        Assert.Empty(ModelFolderScanner.ScanModelFolderNames(root));
    }

    [Fact]
    public void ScanModelFolderNames_MissingRoot_ReturnsEmpty()
    {
        Assert.Empty(ModelFolderScanner.ScanModelFolderNames(Path.Combine(_root, "missing")));
    }

    // ---- ParakeetModelDetector ----

    [Fact]
    public void ParakeetModelDetector_ValidExport_ReturnsTrue()
    {
        var dir = CreateFolder("parakeet");
        File.WriteAllText(Path.Combine(dir, "config.json"),
            """{"model_type": "nemo-conformer-tdt"}""");

        Assert.True(ParakeetModelDetector.IsParakeetTdtModel(dir));
    }

    [Fact]
    public void ParakeetModelDetector_NoConfig_ReturnsFalse()
    {
        var dir = CreateFolder("parakeet-no-config");

        Assert.False(ParakeetModelDetector.IsParakeetTdtModel(dir));
    }

    // ---- helpers ----

    private string CreateFolder(string name)
    {
        var dir = Path.Combine(_root, name);
        Directory.CreateDirectory(dir);
        return dir;
    }

    private void WriteNemotron(string name)
        => File.WriteAllText(Path.Combine(CreateFolder(name), "genai_config.json"), "{}");

    private void WriteParakeet(string name)
        => File.WriteAllText(Path.Combine(CreateFolder(name), "config.json"),
            """{"model_type": "nemo-conformer-tdt"}""");
}
