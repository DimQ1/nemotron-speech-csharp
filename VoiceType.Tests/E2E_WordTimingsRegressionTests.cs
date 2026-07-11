using NemotronSpeech;
using SpeechLib;
using SpeechLib.Models;
using Xunit;

namespace VoiceType.Tests;

/// <summary>
/// End-to-end regression test for word-level timestamp feature.
/// Requires the ONNX model and test audio file to be present on disk.
/// Run selectively: dotnet test --filter "FullyQualifiedName~E2E_WordTimings"
/// </summary>
public sealed class E2E_WordTimingsRegressionTests
{
    private static readonly string RepoRoot = Path.GetFullPath(
        Path.Combine(AppContext.BaseDirectory, "..", "..", "..", "..", ".."));

    private static string ModelPath => Path.Combine(RepoRoot, "Models", "nemotron-3.5-asr-streaming-0.6b-onnx-fp32-cpu");
    private static string AudioPath => Path.Combine(RepoRoot, "Test-Audio", "sample-0.mp3");
    private static string BaselinePath => Path.Combine(
        AppContext.BaseDirectory, "..", "..", "..", "Data", "sample-0-wordtimings-baseline.txt");

    private static bool CanRun => Directory.Exists(ModelPath) && File.Exists(AudioPath);

    // ── Full pipeline regression ────────────────────────────────

    [Fact]
    public void RunFile_WithWordTimestamps_ShouldMatchBaseline()
    {
        if (!CanRun) return; // Skip: model or audio not available

        // Act
        using var session = new ModelSession(ModelPath, "cpu", langId: null, useVad: false);
        var transcript = Transcriber.RunFile(AudioPath, session, wordTimestamps: true, out var timings);

        // Assert — transcript
        var baselineLines = File.ReadAllLines(BaselinePath);
        var expectedTranscript = baselineLines[0];
        Assert.Equal(expectedTranscript, transcript.Trim());

        // Assert — timing count
        var expectedTimingCount = baselineLines.Length - 1;
        Assert.Equal(expectedTimingCount, timings.Count);

        // Assert — each timing line matches
        for (int i = 0; i < timings.Count; i++)
        {
            var expectedLine = baselineLines[i + 1];
            var actual = timings[i];

            var actualLine = $"[{actual.StartSeconds:F2}s -> {actual.EndSeconds:F2}s] {actual.Word}";
            Assert.Equal(expectedLine, actualLine);
        }
    }

    // ── Consistency checks ──────────────────────────────────────

    [Fact]
    public void RunFile_WithAndWithoutTimestamps_SameTranscript()
    {
        if (!CanRun) return;

        using var session1 = new ModelSession(ModelPath, "cpu", langId: null, useVad: false);
        using var session2 = new ModelSession(ModelPath, "cpu", langId: null, useVad: false);

        var transcriptWithTimings = Transcriber.RunFile(AudioPath, session1, wordTimestamps: true, out _);
        var transcriptWithoutTimings = Transcriber.RunFile(AudioPath, session2);

        Assert.Equal(transcriptWithoutTimings.Trim(), transcriptWithTimings.Trim());
    }

    [Fact]
    public void RunFile_Timings_AreMonotonicallyIncreasing()
    {
        if (!CanRun) return;

        using var session = new ModelSession(ModelPath, "cpu", langId: null, useVad: false);
        Transcriber.RunFile(AudioPath, session, wordTimestamps: true, out var timings);

        Assert.NotEmpty(timings);

        for (int i = 1; i < timings.Count; i++)
        {
            Assert.True(timings[i].StartSeconds >= timings[i - 1].EndSeconds - 0.001,
                $"Timing at index {i} ('{timings[i].Word}') starts before previous ends. " +
                $"Prev end: {timings[i - 1].EndSeconds:F3}s, Curr start: {timings[i].StartSeconds:F3}s");
        }
    }

    [Fact]
    public void RunFile_Timings_AllHaveNonEmptyWords()
    {
        if (!CanRun) return;

        using var session = new ModelSession(ModelPath, "cpu", langId: null, useVad: false);
        Transcriber.RunFile(AudioPath, session, wordTimestamps: true, out var timings);

        Assert.All(timings, t => Assert.False(string.IsNullOrEmpty(t.Word),
            $"Empty word at [{t.StartSeconds:F2}s -> {t.EndSeconds:F2}s]"));
    }
}
