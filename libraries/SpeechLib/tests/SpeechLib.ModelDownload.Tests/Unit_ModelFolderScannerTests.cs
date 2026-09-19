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
    public void IsModelDirectory_VibeVoiceExport_ReturnsTrue()
    {
        var dir = CreateFolder("vibevoice-asr-streaming-1.5b-onnx-int4");
        File.WriteAllText(
            Path.Combine(dir, "vibevoice_onnx_config.json"),
            """{"model_type":"vibevoice_asr_onnx_streaming"}""");

        foreach (var file in new[]
        {
            "speech_features.onnx",
            "decoder_prefill.int4.onnx",
            "decoder_prefill.int4.onnx.data",
            "decoder_audio.int4.onnx",
            "decoder_audio.int4.onnx.data",
            "decoder_step.int4.onnx",
            "decoder_step.int4.onnx.data",
            "embed_tokens.float32.bin",
            "tokenizer.json",
            "tokenizer_config.json",
            "vocab.json",
        })
        {
            File.WriteAllBytes(Path.Combine(dir, file), [1]);
        }

        Assert.True(ModelFolderScanner.IsModelDirectory(dir));
    }

    [Fact]
    public void IsModelDirectory_Qwen3StreamingManifest_ReturnsTrue()
    {
        var dir = CreateFolder("qwen3-asr-1.7b-onnx-block-streaming");
        File.WriteAllText(Path.Combine(dir, "streaming_config.json"),
            """{"model_type":"qwen3_asr_onnx_streaming","chunk_seconds":2}""");

        Assert.True(ModelFolderScanner.IsModelDirectory(dir));
    }

    [Fact]
    public void IsModelDirectory_Qwen3StreamingManifestWithoutChunkSeconds_UsesProviderDefault()
    {
        var dir = CreateFolder("qwen3-default-streaming");
        File.WriteAllText(Path.Combine(dir, "streaming_config.json"),
            """{"model_type":"qwen3_asr_onnx_streaming"}""");

        Assert.True(ModelFolderScanner.IsModelDirectory(dir));
    }

    [Fact]
    public void IsModelDirectory_Qwen3Package_ReturnsTrue()
    {
        var dir = CreateFolder("qwen3-asr-1.7b-onnx");
        foreach (var file in new[]
        {
            "encoder.int4.onnx",
            "decoder_init.int4.onnx",
            "decoder_step.int4.onnx",
            "decoder_weights.int4.data",
            "embed_tokens.bin",
            "vocab.json",
        })
        {
            File.WriteAllBytes(Path.Combine(dir, file), [1]);
        }

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
    public void IsModelDirectory_InvalidQwen3StreamingManifest_ReturnsFalse()
    {
        var dir = CreateFolder("qwen3-invalid-streaming");
        File.WriteAllText(Path.Combine(dir, "streaming_config.json"),
            """{"model_type":"qwen3_asr_onnx_streaming","chunk_seconds":0}""");

        Assert.False(ModelFolderScanner.IsModelDirectory(dir));
    }

    [Fact]
    public void IsModelDirectory_Qwen3StreamingWindowMustOverlapTwoChunks()
    {
        var dir = CreateFolder("qwen3-short-window");
        File.WriteAllText(Path.Combine(dir, "streaming_config.json"),
            """{"model_type":"qwen3_asr_onnx_streaming","chunk_seconds":2,"window_seconds":3}""");

        Assert.False(ModelFolderScanner.IsModelDirectory(dir));
    }

    [Fact]
    public void Qwen3ModelDetector_CompleteManifestRequiresBaseModelFiles()
    {
        var streamingDir = CreateFolder("qwen3-streaming");
        var baseDir = CreateFolder("qwen3-base");
        File.WriteAllText(Path.Combine(streamingDir, "streaming_config.json"),
            """{"model_type":"qwen3_asr_onnx_streaming","base_model_dir":"../qwen3-base","chunk_seconds":2}""");

        Assert.False(Qwen3ModelDetector.IsCompleteQwen3AsrStreamingModel(streamingDir));

        foreach (var file in new[]
        {
            "encoder.int4.onnx",
            "decoder_init.int4.onnx",
            "decoder_step.int4.onnx",
            "decoder_weights.int4.data",
            "embed_tokens.bin",
            "vocab.json",
        })
        {
            File.WriteAllBytes(Path.Combine(baseDir, file), [1]);
        }

        Assert.True(Qwen3ModelDetector.IsCompleteQwen3AsrStreamingModel(streamingDir));
    }

    [Fact]
    public void Qwen3ModelDetector_CompleteSelfContainedManifestUsesStreamingFolder()
    {
        var streamingDir = CreateFolder("qwen3-self-contained-streaming");
        File.WriteAllText(Path.Combine(streamingDir, "streaming_config.json"),
            """{"model_type":"qwen3_asr_onnx_streaming","base_model_dir":"../missing-base","chunk_seconds":2}""");

        foreach (var file in new[]
        {
            "encoder.int4.onnx",
            "decoder_init.int4.onnx",
            "decoder_step.int4.onnx",
            "decoder_weights.int4.data",
            "embed_tokens.bin",
            "vocab.json",
        })
        {
            File.WriteAllBytes(Path.Combine(streamingDir, file), [1]);
        }

        Assert.True(Qwen3ModelDetector.IsCompleteQwen3AsrStreamingModel(streamingDir));
        Assert.True(ModelFolderScanner.IsModelDirectory(streamingDir));
    }

    [Fact]
    public void IsModelDirectory_BlockStreamingManifestRequiresEncoderGraph()
    {
        var streamingDir = CreateFolder("qwen3-block-streaming");
        var baseDir = CreateFolder("qwen3-block-base");
        File.WriteAllText(Path.Combine(streamingDir, "streaming_config.json"),
            """{"model_type":"qwen3_asr_onnx_streaming","base_model_dir":"../qwen3-block-base","encoder_file":"encoder_stream.onnx","block_seconds":8,"chunk_seconds":8,"window_seconds":16}""");

        foreach (var file in new[]
        {
            "encoder.int4.onnx",
            "decoder_init.int4.onnx",
            "decoder_step.int4.onnx",
            "decoder_weights.int4.data",
            "embed_tokens.bin",
            "vocab.json",
        })
        {
            File.WriteAllBytes(Path.Combine(baseDir, file), [1]);
        }

        Assert.False(Qwen3ModelDetector.IsCompleteQwen3AsrStreamingModel(streamingDir));

        File.WriteAllBytes(Path.Combine(streamingDir, "encoder_stream.onnx"), [1]);
        Assert.True(Qwen3ModelDetector.IsCompleteQwen3AsrStreamingModel(streamingDir));
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
