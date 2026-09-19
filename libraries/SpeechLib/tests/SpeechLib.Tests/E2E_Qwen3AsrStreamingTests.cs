using System.Buffers.Binary;
using SpeechLib.Providers;
using SpeechLib.Qwen3;
using Xunit;

namespace SpeechLib.Tests;

public sealed class E2E_Qwen3AsrStreamingTests
{
    [Qwen3OnnxFact]
    public void StreamingProvider_EmitsChunkDeltasAndFlushesTail()
    {
        var root = FindRepositoryRoot();
        var modelPath = Path.Combine(root, "models", "qwen3-asr-1.7b-onnx-block-streaming");
        var audioPath = Path.Combine(root, "Test-Audio", "cv17", "en", "0002.wav");

        using var recognizer = RecognizerFactory.Create(new RecognizerFactoryOptions
        {
            ModelPath = modelPath,
            ExecutionProvider = "cpu",
        });

        Assert.IsType<Qwen3AsrStreamingRecognizer>(recognizer);

        var audio = LoadPcm16MonoWave(audioPath);
        var parts = new List<string>();
        for (int offset = 0; offset < audio.Length; offset += recognizer.ChunkSamples)
        {
            int length = Math.Min(recognizer.ChunkSamples, audio.Length - offset);
            var result = recognizer.ProcessAudio(audio[offset..(offset + length)]);
            if (!string.IsNullOrEmpty(result))
                parts.Add(result);
        }

        var flush = recognizer.Flush();
        if (!string.IsNullOrEmpty(flush))
            parts.Add(flush);

        var transcript = string.Concat(parts).Trim();
        Assert.True(transcript.Contains("She'll be all right.", StringComparison.OrdinalIgnoreCase), transcript);
        Assert.DoesNotContain("language", transcript, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("asr_text", transcript, StringComparison.OrdinalIgnoreCase);
        Assert.Equal(1, CountOccurrences(transcript, "She'll be all right."));
    }

    [Qwen3OnnxFact]
    public void StreamingProvider_PreservesContextAcrossMultipleModelChunks()
    {
        var root = FindRepositoryRoot();
        var modelPath = Path.Combine(root, "models", "qwen3-asr-1.7b-onnx-block-streaming");
        var audioPath = Path.Combine(root, "Test-Audio", "cv17", "en", "0003.wav");

        using var recognizer = RecognizerFactory.Create(new RecognizerFactoryOptions
        {
            ModelPath = modelPath,
            ExecutionProvider = "cpu",
        });

        var audio = LoadPcm16MonoWave(audioPath);
        var parts = new List<string>();
        for (int offset = 0; offset < audio.Length; offset += recognizer.ChunkSamples)
        {
            int length = Math.Min(recognizer.ChunkSamples, audio.Length - offset);
            var result = recognizer.ProcessAudio(audio[offset..(offset + length)]);
            if (!string.IsNullOrEmpty(result))
                parts.Add(result);
        }

        var flush = recognizer.Flush();
        if (!string.IsNullOrEmpty(flush))
            parts.Add(flush);

        var transcript = string.Concat(parts).Trim();
        Assert.Equal("Six.", transcript);
    }

    [Qwen3OnnxFact]
    public void StreamingProvider_PromptTranslationUsesTargetLanguage()
    {
        var root = FindRepositoryRoot();
        var modelPath = Path.Combine(root, "models", "qwen3-asr-1.7b-onnx-block-streaming");
        var audioPath = Path.Combine(root, "Test-Audio", "cv17", "en", "0002.wav");

        using var recognizer = RecognizerFactory.Create(new RecognizerFactoryOptions
        {
            ModelPath = modelPath,
            ExecutionProvider = "cpu",
            TranslationEnabled = true,
            TranslationLanguage = "ru",
        });

        var audio = LoadPcm16MonoWave(audioPath);
        var parts = new List<string>();
        for (int offset = 0; offset < audio.Length; offset += recognizer.ChunkSamples)
        {
            int length = Math.Min(recognizer.ChunkSamples, audio.Length - offset);
            var result = recognizer.ProcessAudio(audio[offset..(offset + length)]);
            if (!string.IsNullOrEmpty(result))
                parts.Add(result);
        }

        var flush = recognizer.Flush();
        if (!string.IsNullOrEmpty(flush))
            parts.Add(flush);

        var translation = string.Concat(parts).Trim();
        Assert.NotEmpty(translation);
        Assert.True(translation.Any(character => character is >= '\u0400' and <= '\u04FF'), translation);
        Assert.DoesNotContain("language", translation, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("asr_text", translation, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("She'll be all right.", translation, StringComparison.OrdinalIgnoreCase);
    }

    private static int CountOccurrences(string text, string value)
    {
        int count = 0;
        int start = 0;
        while ((start = text.IndexOf(value, start, StringComparison.OrdinalIgnoreCase)) >= 0)
        {
            count++;
            start += value.Length;
        }

        return count;
    }

    private static float[] LoadPcm16MonoWave(string path)
    {
        var bytes = File.ReadAllBytes(path);
        short channels = BinaryPrimitives.ReadInt16LittleEndian(bytes.AsSpan(22, 2));
        short bitsPerSample = BinaryPrimitives.ReadInt16LittleEndian(bytes.AsSpan(34, 2));
        Assert.Equal(1, channels);
        Assert.Equal(16, bitsPerSample);

        int dataOffset = FindChunk(bytes, "data");
        int dataLength = BinaryPrimitives.ReadInt32LittleEndian(bytes.AsSpan(dataOffset + 4, 4));
        var samples = new float[dataLength / 2];
        for (int i = 0; i < samples.Length; i++)
        {
            short sample = BinaryPrimitives.ReadInt16LittleEndian(bytes.AsSpan(dataOffset + 8 + i * 2, 2));
            samples[i] = sample / 32768f;
        }

        return samples;
    }

    private static int FindChunk(byte[] bytes, string name)
    {
        for (int offset = 12; offset + 8 <= bytes.Length;)
        {
            var chunkName = System.Text.Encoding.ASCII.GetString(bytes, offset, 4);
            int length = BinaryPrimitives.ReadInt32LittleEndian(bytes.AsSpan(offset + 4, 4));
            if (chunkName == name) return offset;
            offset += 8 + length + (length & 1);
        }

        throw new InvalidDataException($"WAV data chunk not found: {name}");
    }

    private static string FindRepositoryRoot()
    {
        var directory = new DirectoryInfo(AppContext.BaseDirectory);
        while (directory is not null)
        {
            if (File.Exists(Path.Combine(directory.FullName, "NemotronSpeech.slnx")))
                return directory.FullName;

            directory = directory.Parent;
        }

        throw new DirectoryNotFoundException("Repository root not found.");
    }

    private sealed class Qwen3OnnxFactAttribute : FactAttribute
    {
        public Qwen3OnnxFactAttribute()
        {
            if (!string.Equals(Environment.GetEnvironmentVariable("RUN_QWEN_ONNX_E2E"), "1", StringComparison.Ordinal))
                Skip = "Set RUN_QWEN_ONNX_E2E=1 to run the local Qwen3 ONNX model test.";
        }
    }
}