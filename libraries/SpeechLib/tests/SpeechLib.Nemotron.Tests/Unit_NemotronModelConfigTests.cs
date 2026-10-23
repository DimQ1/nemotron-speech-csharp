using System.Text.Json.Nodes;
using SpeechLib.Nemotron;
using Xunit;

namespace SpeechLib.Nemotron.Tests;

public sealed class Unit_NemotronModelConfigTests : IDisposable
{
    private readonly List<string> _temp = new();

    public void Dispose()
    {
        foreach (var directory in _temp)
            Directory.Delete(directory, recursive: true);
    }

    [Theory]
    [InlineData("int4-c056", 8960, 7, 65)]
    [InlineData("fp32-c056", 8960, 7, 65)]
    [InlineData("int4-c112", 17920, 14, 121)]
    [InlineData("fp32-c112", 17920, 14, 121)]
    public void Parse_ShippedVariant_ShouldGiveTheStreamingGeometry(string variant, int chunkSamples, int frames, int encoderRows)
    {
        var config = NemotronTestEnvironment.LoadConfig(variant);

        Assert.Equal("nemotron_speech", config.ModelType);
        Assert.Equal(13088, config.VocabSize);
        Assert.Equal(128, config.NumMels);
        Assert.Equal(512, config.FftSize);
        Assert.Equal(160, config.HopLength);
        Assert.Equal(400, config.WinLength);
        Assert.Equal(0.97f, config.Preemph);
        Assert.Equal(5.96046448e-08f, config.LogEps);
        Assert.Equal(8, config.SubsamplingFactor);
        Assert.Equal(56, config.LeftContext);
        Assert.Equal(8, config.ConvContext);
        Assert.Equal(9, config.PreEncodeCacheSize);
        Assert.Equal(16000, config.SampleRate);
        Assert.Equal(chunkSamples, config.ChunkSamples);
        Assert.Equal(13087, config.BlankId);
        Assert.Equal(10, config.MaxSymbolsPerStep);

        Assert.Equal(chunkSamples / 160, config.MelFramesPerChunk);
        Assert.Equal(frames, config.EncoderFramesPerChunk);
        Assert.Equal(encoderRows, config.EncoderInputFrames);
        Assert.Equal(1280, config.SamplesPerEncoderFrame);
        Assert.Equal(0f, config.BlankPenalty);
    }

    [Fact]
    public void Parse_ShippedVariant_ShouldGiveGraphNamesAndSizes()
    {
        var config = NemotronTestEnvironment.LoadConfig("int4-c056");

        Assert.Equal("encoder.onnx", config.Encoder.FileName);
        Assert.Equal(1024, config.Encoder.HiddenSize);
        Assert.Equal(24, config.Encoder.NumLayers);
        Assert.Equal("audio_signal", config.Encoder.AudioInput);
        Assert.Equal("length", config.Encoder.LengthInput);
        Assert.Equal("cache_last_channel", config.Encoder.CacheChannelInput);
        Assert.Equal("cache_last_time", config.Encoder.CacheTimeInput);
        Assert.Equal("cache_last_channel_len", config.Encoder.CacheChannelLengthInput);
        Assert.Equal("lang_id", config.Encoder.LanguageInput);
        Assert.Equal("outputs", config.Encoder.EncodedOutput);
        Assert.Equal("encoded_lengths", config.Encoder.LengthOutput);
        Assert.Equal("cache_last_channel_next", config.Encoder.CacheChannelOutput);
        Assert.Equal("cache_last_time_next", config.Encoder.CacheTimeOutput);
        Assert.Equal("cache_last_channel_len_next", config.Encoder.CacheChannelLengthOutput);

        Assert.Equal("decoder.onnx", config.Decoder.FileName);
        Assert.Equal(640, config.Decoder.HiddenSize);
        Assert.Equal(2, config.Decoder.NumLayers);
        Assert.Equal(("targets", "h_in", "c_in"), (config.Decoder.TargetsInput, config.Decoder.HiddenStateInput, config.Decoder.CellStateInput));
        Assert.Equal(("decoder_output", "h_out", "c_out"), (config.Decoder.Output, config.Decoder.HiddenStateOutput, config.Decoder.CellStateOutput));

        Assert.Equal("joint.onnx", config.Joiner.FileName);
        Assert.Equal(("encoder_output", "decoder_output", "joint_output"), (config.Joiner.EncoderInput, config.Joiner.DecoderInput, config.Joiner.Output));
    }

    [Theory]
    [InlineData("int4-c056")]
    [InlineData("int4-c112")]
    public void Parse_ShippedVariant_ShouldGiveTheVadBlock(string variant)
    {
        var vad = NemotronTestEnvironment.LoadConfig(variant).Vad;

        Assert.NotNull(vad);
        Assert.Equal("silero_vad.onnx", vad!.FileName);
        Assert.Equal(0.3f, vad.Threshold);
        Assert.Equal(3360, vad.SilenceDurationMs);
        Assert.Equal(560, vad.PrefixPaddingMs);
    }

    [Fact]
    public void Parse_Int4AndFp32_ShouldBeTheSameConfig()
    {
        Assert.Equal(NemotronTestEnvironment.LoadConfig("int4-c056"), NemotronTestEnvironment.LoadConfig("fp32-c056"));
        Assert.Equal(NemotronTestEnvironment.LoadConfig("int4-c112"), NemotronTestEnvironment.LoadConfig("fp32-c112"));
        Assert.NotEqual(NemotronTestEnvironment.LoadConfig("int4-c056"), NemotronTestEnvironment.LoadConfig("int4-c112"));
    }

    [Fact]
    public void Parse_BlankPenalty_ShouldComeFromTheSearchSection()
    {
        var config = NemotronModelConfig.Parse(Mutate(root => root["search"] = new JsonObject { ["blank_penalty"] = 1.5, ["num_beams"] = 1 }));

        Assert.Equal(1.5f, config.BlankPenalty);
    }

    [Fact]
    public void Parse_WithoutVadBlock_ShouldGiveNoVad()
    {
        var config = NemotronModelConfig.Parse(Mutate(root => root["model"]!.AsObject().Remove("vad")));

        Assert.Null(config.Vad);
    }

    [Fact]
    public void Parse_PartialVadBlock_ShouldUseTheUpstreamDefaults()
    {
        var config = NemotronModelConfig.Parse(Mutate(root => root["model"]!["vad"] = new JsonObject { ["filename"] = "vad.onnx" }));

        Assert.Equal("vad.onnx", config.Vad!.FileName);
        Assert.Equal(0.5f, config.Vad.Threshold);
        Assert.Equal(500, config.Vad.SilenceDurationMs);
        Assert.Equal(300, config.Vad.PrefixPaddingMs);
    }

    [Fact]
    public void Parse_EncoderWithoutLanguageInput_ShouldBeSingleLanguage()
    {
        var config = NemotronModelConfig.Parse(Mutate(root => root["model"]!["encoder"]!["inputs"]!.AsObject().Remove("lang_id")));

        Assert.Equal("", config.Encoder.LanguageInput);
    }

    [Fact]
    public void Parse_AbsentNames_ShouldFallBackToTheGraphNames()
    {
        var config = NemotronModelConfig.Parse(Mutate(root =>
        {
            var model = root["model"]!.AsObject();
            model["encoder"]!.AsObject().Remove("inputs");
            model["encoder"]!.AsObject().Remove("outputs");
            model["decoder"]!.AsObject().Remove("inputs");
            model.Remove("joiner");
        }));

        Assert.Equal("audio_signal", config.Encoder.AudioInput);
        Assert.Equal("cache_last_channel_len_next", config.Encoder.CacheChannelLengthOutput);
        Assert.Equal("h_in", config.Decoder.HiddenStateInput);
        Assert.Equal("joiner.onnx", config.Joiner.FileName); // upstream default; shipped folders name joint.onnx explicitly
        Assert.Equal("joint_output", config.Joiner.Output);
    }

    [Theory]
    [InlineData("type", "\"whisper\"")]
    [InlineData("chunk_samples", "8961")]                // not a multiple of the hop
    [InlineData("chunk_samples", "0")]
    [InlineData("blank_id", "13088")]                    // outside the vocabulary
    [InlineData("blank_id", "-1")]
    [InlineData("win_length", "1024")]                   // longer than the FFT
    [InlineData("fft_size", "500")]                      // not a power of two
    [InlineData("vocab_size", "0")]
    [InlineData("pre_encode_cache_size", "0")]
    [InlineData("max_symbols_per_step", "0")]
    [InlineData("preemph", "1.5")]
    [InlineData("log_eps", "0")]
    [InlineData("subsampling_factor", "9")]              // 56 mel frames do not split into groups of 9
    public void Parse_InvalidModelValue_ShouldThrow(string key, string jsonValue)
    {
        var json = Mutate(root => root["model"]![key] = JsonNode.Parse(jsonValue));

        Assert.Throws<InvalidDataException>(() => NemotronModelConfig.Parse(json));
    }

    [Theory]
    [InlineData("")]
    [InlineData("not json")]
    [InlineData("[]")]
    [InlineData("{}")]
    [InlineData("{\"model\": 3}")]
    public void Parse_NotAConfig_ShouldThrow(string json)
    {
        Assert.Throws<InvalidDataException>(() => NemotronModelConfig.Parse(json));
    }

    [Fact]
    public void Parse_MissingEncoderSection_ShouldThrow()
    {
        var json = Mutate(root => root["model"]!.AsObject().Remove("encoder"));

        Assert.Throws<InvalidDataException>(() => NemotronModelConfig.Parse(json));
    }

    [Fact]
    public void Parse_VadBlockWithEmptyFilename_ShouldMeanNoVadModelLikeUpstream()
    {
        // upstream (StreamingProcessor::InitVadFromConfig): VAD is on only when vad.filename is non-empty; an empty one is no error
        var config = NemotronModelConfig.Parse(Mutate(root => root["model"]!["vad"]!["filename"] = ""));

        Assert.NotNull(config.Vad);
        Assert.False(config.Vad!.HasModel);
        Assert.Equal(0.3f, config.Vad.Threshold);            // the block's other values stay readable for an external VAD filter
        Assert.Equal(3360, config.Vad.SilenceDurationMs);
        Assert.True(NemotronModelConfig.Parse(Mutate(_ => { })).Vad!.HasModel);
        Assert.False(NemotronModelConfig.Parse(Mutate(root => root["model"]!["vad"] = new JsonObject { ["threshold"] = 0.4 })).Vad!.HasModel);
        Assert.False(NemotronModelConfig.Parse(Mutate(root => root["model"]!["vad"]!["filename"] = "  ")).Vad!.HasModel);
    }

    [Fact]
    public void Parse_VadValuesOutsideTheUsualRange_ShouldParseLikeUpstream()
    {
        // upstream does not range check: the Silero gate clamps the threshold, the chunk counts are max(1, ms / chunk ms)
        var config = NemotronModelConfig.Parse(Mutate(root =>
        {
            root["model"]!["vad"]!["threshold"] = 1.5;
            root["model"]!["vad"]!["silence_duration_ms"] = -1;
            root["model"]!["vad"]!["prefix_padding_ms"] = -560;
        }));

        Assert.Equal(1.5f, config.Vad!.Threshold);
        Assert.Equal((1, 1), SilenceDropRule.ChunkCounts(config.Vad, config.ChunkSamples, config.SampleRate));
        Assert.Equal(-1, config.Vad.SilenceDurationMs);
    }

    [Theory]
    [InlineData(8000)]
    [InlineData(44100)]
    public void Parse_OtherSampleRates_ShouldParseAndTheNativeGateShouldSayItIsUnsupported(int rate)
    {
        var config = NemotronModelConfig.Parse(Mutate(root => root["model"]!["sample_rate"] = rate));

        Assert.Equal(rate, config.SampleRate);
        var error = Assert.Throws<NotSupportedException>(() => NemotronRnntRecognizer.CreateGate("nowhere", config, new NemotronRecognizerOptions()));
        Assert.Contains("16 kHz", error.Message);
        Assert.Contains(rate.ToString(), error.Message);
    }

    [Fact]
    public void CreateGate_WithoutAVadModelName_ShouldFailClearlyUnlessAPathIsGiven()
    {
        var config = NemotronModelConfig.Parse(Mutate(root => root["model"]!["vad"]!["filename"] = ""));

        var error = Assert.Throws<FileNotFoundException>(() => NemotronRnntRecognizer.CreateGate("nowhere", config, new NemotronRecognizerOptions()));
        Assert.Contains("vad", error.Message);
        Assert.Throws<FileNotFoundException>(() =>
            NemotronRnntRecognizer.CreateGate("nowhere", config, new NemotronRecognizerOptions { VadModelPath = Path.Combine(NewDirectory(), "silero.onnx") }));
    }

    [Fact]
    public void Load_ShouldReadTheConfigFileOfAFolder()
    {
        var directory = NewDirectory();
        File.WriteAllText(Path.Combine(directory, "genai_config.json"), File.ReadAllText(NemotronTestEnvironment.DataPath("config", "genai_config.int4-c112.json")));

        Assert.Equal(17920, NemotronModelConfig.Load(directory).ChunkSamples);
        Assert.Throws<FileNotFoundException>(() => NemotronModelConfig.Load(Path.Combine(directory, "missing")));
    }

    [Fact]
    public void IsNemotronModel_FolderWithConfigAndGraphs_ShouldBeTrue()
    {
        var directory = NewModelFolder();

        Assert.True(NemotronModelConfig.IsNemotronModel(directory));
    }

    [Theory]
    [InlineData("encoder.onnx")]
    [InlineData("decoder.onnx")]
    [InlineData("joint.onnx")]
    public void IsNemotronModel_MissingGraph_ShouldBeFalse(string missing)
    {
        var directory = NewModelFolder();
        File.Delete(Path.Combine(directory, missing));

        Assert.False(NemotronModelConfig.IsNemotronModel(directory));
    }

    [Fact]
    public void IsNemotronModel_OtherModelTypeOrNoConfig_ShouldBeFalse()
    {
        var other = NewModelFolder();
        File.WriteAllText(Path.Combine(other, "genai_config.json"), Mutate(root => root["model"]!["type"] = "phi3"));
        var garbage = NewModelFolder();
        File.WriteAllText(Path.Combine(garbage, "genai_config.json"), "{ nope");
        var empty = NewDirectory();

        Assert.False(NemotronModelConfig.IsNemotronModel(other));
        Assert.False(NemotronModelConfig.IsNemotronModel(garbage));
        Assert.False(NemotronModelConfig.IsNemotronModel(empty));
        Assert.False(NemotronModelConfig.IsNemotronModel(Path.Combine(empty, "missing")));
        Assert.False(NemotronModelConfig.IsNemotronModel(null));
        Assert.False(NemotronModelConfig.IsNemotronModel(""));
    }

    [Fact]
    public void IsNemotronModel_ShouldFollowTheFileNamesOfTheConfig()
    {
        var directory = NewModelFolder();
        File.Move(Path.Combine(directory, "joint.onnx"), Path.Combine(directory, "joiner.onnx"));
        Assert.False(NemotronModelConfig.IsNemotronModel(directory)); // config still says joint.onnx

        File.WriteAllText(Path.Combine(directory, "genai_config.json"), Mutate(root => root["model"]!["joiner"]!["filename"] = "joiner.onnx"));
        Assert.True(NemotronModelConfig.IsNemotronModel(directory));
    }

    private static string Mutate(Action<JsonNode> change)
    {
        var root = JsonNode.Parse(File.ReadAllText(NemotronTestEnvironment.DataPath("config", "genai_config.int4-c056.json")))!;
        change(root);
        return root.ToJsonString();
    }

    private string NewDirectory()
    {
        var directory = Path.Combine(Path.GetTempPath(), "nemotron-config-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(directory);
        _temp.Add(directory);
        return directory;
    }

    private string NewModelFolder()
    {
        var directory = NewDirectory();
        File.Copy(NemotronTestEnvironment.DataPath("config", "genai_config.int4-c056.json"), Path.Combine(directory, "genai_config.json"));
        foreach (var graph in new[] { "encoder.onnx", "decoder.onnx", "joint.onnx" })
            File.WriteAllBytes(Path.Combine(directory, graph), [0]);
        return directory;
    }
}
