using SpeechLib.Qwen3;
using Xunit;

namespace SpeechLib.Tests;

public sealed class Unit_Qwen3StreamingModelTests
{
    [Fact]
    public void StreamingManifest_IsRecognizedWithoutRegularModelFiles()
    {
        var directory = CreateTemporaryDirectory();
        try
        {
            File.WriteAllText(
                Path.Combine(directory, "streaming_config.json"),
                "{\"model_type\":\"qwen3_asr_onnx_streaming\",\"chunk_seconds\":2}");

            Assert.True(Qwen3AsrStreamingRecognizer.IsQwen3AsrStreamingModel(directory));
            Assert.False(Qwen3AsrRecognizer.IsQwen3AsrModel(directory));
        }
        finally
        {
            Directory.Delete(directory, recursive: true);
        }
    }

    [Fact]
    public void InvalidStreamingManifest_IsIgnored()
    {
        var directory = CreateTemporaryDirectory();
        try
        {
            File.WriteAllText(
                Path.Combine(directory, "streaming_config.json"),
                "{\"model_type\":\"qwen3_asr_onnx_streaming\",\"chunk_seconds\":0}");

            Assert.False(Qwen3AsrStreamingRecognizer.IsQwen3AsrStreamingModel(directory));
        }
        finally
        {
            Directory.Delete(directory, recursive: true);
        }
    }

    private static string CreateTemporaryDirectory()
    {
        var directory = Path.Combine(Path.GetTempPath(), "qwen3-streaming-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(directory);
        return directory;
    }
}