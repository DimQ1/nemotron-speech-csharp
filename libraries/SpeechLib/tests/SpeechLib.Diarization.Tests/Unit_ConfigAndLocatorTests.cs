namespace SpeechLib.Diarization.Tests;

public sealed class Unit_ConfigAndLocatorTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "diar-locator-" + Guid.NewGuid().ToString("N"));

    public Unit_ConfigAndLocatorTests() => Directory.CreateDirectory(_root);

    public void Dispose()
    {
        try { Directory.Delete(_root, true); } catch (IOException) { }
    }

    [Theory]
    [InlineData(DiarizationPreset.Offline, 340, 40, 40, 300, 30.4)]
    [InlineData(DiarizationPreset.LowLatency, 9, 4, 264, 222, 1.04)]
    [InlineData(DiarizationPreset.VeryLowLatency, 6, 2, 264, 222, 0.64)]
    [InlineData(DiarizationPreset.UltraLowLatency, 3, 1, 264, 222, 0.32)]
    [InlineData(DiarizationPreset.NcppV3Streaming, 13, 1, 80, 40, 1.12)]
    [InlineData(DiarizationPreset.Eco2s, 24, 2, 48, 48, 2.08)]
    public void Parse_ShouldProduceGeometryAndLatency(DiarizationPreset preset, int chunk, int right, int fifo, int update, double latency)
    {
        var g = TestConfig.Default.GetGeometry(preset);
        Assert.Equal(new StreamingGeometry(chunk, right, fifo, update), g);
        Assert.Equal(latency, g.BufferLatencySeconds, 6);
        Assert.Equal(chunk * 0.08, g.HopSeconds, 6);
    }

    [Fact]
    public void Parse_ShouldReadAoscAndSilenceEmbedding()
    {
        var c = TestConfig.Default;
        Assert.Equal(264, c.Aosc.CacheLen);
        Assert.Equal(1, c.Aosc.SilenceFramesPerSpeaker);
        Assert.Equal(0.25f, c.Aosc.PredScoreThreshold);
        Assert.Equal(512, c.SilenceEmbed.Length);
        Assert.Equal(0.97f, c.Preemphasis);
        Assert.Equal(6, c.Presets.Count);
    }

    [Fact]
    public void Parse_ShouldRejectWrongSilenceLength()
    {
        var e = Assert.Throws<DiarizationModelException>(() => DiarizationConfig.Parse(TestConfig.Json(silenceLen: 100)));
        Assert.Contains("silence_embed", e.Message);
    }

    [Fact]
    public void Parse_ShouldRejectOtherSpeakerCounts()
    {
        var e = Assert.Throws<DiarizationModelException>(() => DiarizationConfig.Parse(TestConfig.Json(numSpeakers: 4)));
        Assert.Contains("num_speakers", e.Message);
    }

    [Fact]
    public void Parse_ShouldRejectBrokenJson()
    {
        Assert.Throws<DiarizationModelException>(() => DiarizationConfig.Parse("{ not json"));
        Assert.Throws<DiarizationModelException>(() => DiarizationConfig.Parse("[]"));
    }

    [Fact]
    public void GetGeometry_ShouldThrowForPresetMissingFromFile()
    {
        var c = DiarizationConfig.Parse(TestConfig.Json(includeModes: false));
        Assert.Equal(340, c.GetGeometry(DiarizationPreset.Offline).ChunkLen);
        var e = Assert.Throws<DiarizationModelException>(() => c.GetGeometry(DiarizationPreset.Eco2s));
        Assert.Contains("eco_2s", e.Message);
    }

    private string MakeFolder(string name, bool config = true, bool frontend = true, long int8Bytes = 0, long fp32Bytes = 0,
        long frontendBytes = 1_200_000)
    {
        string dir = Path.Combine(_root, name);
        Directory.CreateDirectory(dir);
        if (config) File.WriteAllText(Path.Combine(dir, DiarizationModelLocator.ConfigFileName), TestConfig.Json());
        if (frontend) Sized(Path.Combine(dir, DiarizationModelLocator.FrontendFileName), frontendBytes);
        if (int8Bytes > 0) Sized(Path.Combine(dir, DiarizationModelLocator.Int8StepFileName), int8Bytes);
        if (fp32Bytes > 0) Sized(Path.Combine(dir, DiarizationModelLocator.Fp32StepFileName), fp32Bytes);
        return dir;
    }

    private static void Sized(string path, long bytes)
    {
        using var fs = File.Create(path);
        fs.SetLength(bytes);
    }

    [Fact]
    public void Validate_ShouldPickInt8ForAutoAndListAvailablePrecisions()
    {
        string dir = MakeFolder("full", int8Bytes: 104_653_656, fp32Bytes: 360_000_000);
        var d = DiarizationModelLocator.Validate(dir);
        Assert.Equal(DiarizationPrecision.Int8, d.Precision);
        Assert.Equal(DiarizationModelLocator.Int8StepFileName, d.StepFile);
        Assert.Equal([DiarizationPrecision.Fp32, DiarizationPrecision.Int8], d.AvailablePrecisions);
        Assert.EndsWith(DiarizationModelLocator.Int8StepFileName, d.StepPath);

        var fp32 = DiarizationModelLocator.Validate(dir, DiarizationPrecision.Fp32);
        Assert.Equal(DiarizationModelLocator.Fp32StepFileName, fp32.StepFile);
    }

    [Fact]
    public void Validate_ShouldExplainMissingFiles()
    {
        string noConfig = MakeFolder("noconfig", config: false, int8Bytes: 100_000_000);
        var e1 = Assert.Throws<DiarizationModelException>(() => DiarizationModelLocator.Validate(noConfig));
        Assert.Contains("diar_config.json", e1.Message);

        string noStep = MakeFolder("nostep");
        var e2 = Assert.Throws<DiarizationModelException>(() => DiarizationModelLocator.Validate(noStep));
        Assert.Contains("diar_step", e2.Message);

        string onlyInt8 = MakeFolder("onlyint8", int8Bytes: 100_000_000);
        var e3 = Assert.Throws<DiarizationModelException>(() => DiarizationModelLocator.Validate(onlyInt8, DiarizationPrecision.Fp32));
        Assert.Contains("diar_step.onnx", e3.Message);

        var e4 = Assert.Throws<DiarizationModelException>(() => DiarizationModelLocator.Validate(Path.Combine(_root, "nope")));
        Assert.Contains("does not exist", e4.Message);
    }

    [Fact]
    public void Validate_ShouldFlagTruncatedDownloads()
    {
        string dir = MakeFolder("partial", int8Bytes: 5_000_000);
        var e = Assert.Throws<DiarizationModelException>(() => DiarizationModelLocator.Validate(dir));
        Assert.Contains("incomplete", e.Message);
    }

    [Fact]
    public void Find_ShouldPreferInt8FolderAndSkipInvalidOnes()
    {
        string rootA = Path.Combine(_root, "a");
        string rootB = Path.Combine(_root, "b");
        Directory.CreateDirectory(rootA);
        Directory.CreateDirectory(rootB);
        // root A has a broken int8 folder, root B a good one
        Directory.Move(MakeFolder("broken", int8Bytes: 1_000), Path.Combine(rootA, DiarizationModelLocator.Int8FolderName));
        Directory.Move(MakeFolder("good", int8Bytes: 100_000_000), Path.Combine(rootB, DiarizationModelLocator.Int8FolderName));

        var found = DiarizationModelLocator.Find([rootA, rootB]);
        Assert.NotNull(found);
        Assert.StartsWith(rootB, found!.Folder);
        Assert.Null(DiarizationModelLocator.Find([Path.Combine(_root, "empty")]));
    }

    [Fact]
    public void Find_ShouldHonourAnExplicitPrecisionAndFindTheFp32Folder()
    {
        string root = Path.Combine(_root, "models");
        Directory.CreateDirectory(root);
        Directory.Move(MakeFolder("fp32only", fp32Bytes: 360_000_000), Path.Combine(root, DiarizationModelLocator.Fp32FolderName));

        Assert.Equal(DiarizationPrecision.Fp32, DiarizationModelLocator.Find([root])!.Precision);
        Assert.Null(DiarizationModelLocator.Find([root], DiarizationPrecision.Int8));
    }

    [Fact]
    public void CatalogFacts_ShouldDescribeTheDistribution()
    {
        Assert.Equal("nemotron-3-diarization-onnx", DiarizationModelLocator.FolderName);
        Assert.Equal("nemotron-3-diarization-onnx-int8", DiarizationModelLocator.Int8FolderName);
        Assert.Equal("nemotron-3-diarization-onnx-fp32", DiarizationModelLocator.Fp32FolderName);
        Assert.InRange(DiarizationModelLocator.Int8DownloadBytes, 105_000_000, 107_000_000);
        Assert.InRange(DiarizationModelLocator.Fp32DownloadBytes, 399_000_000, 400_000_000);
        Assert.Equal("OpenMDW-1.1", DiarizationModelLocator.License);
    }

    [Fact]
    public void Load_ShouldFailClearlyWhenFolderIsInvalid()
    {
        var e = Assert.Throws<DiarizationModelException>(() =>
            DiarizationModel.Load(new DiarizationModelOptions { ModelFolder = Path.Combine(_root, "missing") }));
        Assert.Contains("does not exist", e.Message);
    }
}
