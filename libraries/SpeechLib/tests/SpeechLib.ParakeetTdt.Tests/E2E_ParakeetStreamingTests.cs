using System.Buffers.Binary;
using SpeechLib.ParakeetTdt;
using Xunit;

namespace SpeechLib.ParakeetTdt.Tests;

/// <summary>
/// Real-streaming contract of <see cref="ParakeetTdtRecognizer"/> against the actual
/// ONNX export: text is previewed long before it is committed, committed text agrees
/// with offline decoding, and the trailing word is released on silence. Skipped when
/// no Parakeet model is present locally.
/// </summary>
public sealed class E2E_ParakeetStreamingTests
{
    private const int BatchSamples = 800; // 50 ms, the capture drain interval

    [ParakeetOnnxFact]
    public void Streaming_PreviewsTextBeforeCommitting()
    {
        using var recognizer = new ParakeetTdtRecognizer(ParakeetTestModel.Locate()!);
        var audio = ParakeetTestModel.LoadWav("0001.wav");

        double? firstPartialAt = null;
        double? firstCommitAt = null;
        var committed = new System.Text.StringBuilder();

        foreach (var (batch, endSeconds) in Batches(audio, recognizer.SampleRate))
        {
            var delta = recognizer.ProcessAudio(batch);
            if (!string.IsNullOrEmpty(delta))
            {
                committed.Append(delta);
                firstCommitAt ??= endSeconds;
            }

            if (!string.IsNullOrEmpty(recognizer.PartialText))
                firstPartialAt ??= endSeconds;
        }

        committed.Append(recognizer.Flush());

        Assert.NotNull(firstPartialAt);
        Assert.NotNull(firstCommitAt);
        // The preview leads the commit: the first word of an utterance is committed only
        // after the onset context, and never before the session's commit warm-up window.
        Assert.True(firstPartialAt < firstCommitAt,
            $"preview at {firstPartialAt:F2}s should precede commit at {firstCommitAt:F2}s");
        Assert.True(firstPartialAt <= 2.0, $"first preview at {firstPartialAt:F2}s is not real-time");
        var commitDeadline = Math.Max(
            recognizer.CommitWarmupSeconds,
            firstPartialAt.Value + recognizer.OnsetContextSeconds) + recognizer.ChunkSeconds + 0.5;
        Assert.True(firstCommitAt <= commitDeadline,
            $"first commit at {firstCommitAt:F2}s lags the preview at {firstPartialAt:F2}s beyond {commitDeadline:F2}s");
        Assert.False(string.IsNullOrWhiteSpace(committed.ToString()));
    }

    [ParakeetOnnxFact]
    public void Streaming_CommittedTranscriptMatchesOffline()
    {
        using var recognizer = new ParakeetTdtRecognizer(ParakeetTestModel.Locate()!);
        var audio = ParakeetTestModel.LoadWav("0002.wav");

        var offline = Normalize(recognizer.Transcribe(audio));

        recognizer.ResetStreamingState();
        var streamed = new System.Text.StringBuilder();
        foreach (var (batch, _) in Batches(audio, recognizer.SampleRate))
            streamed.Append(recognizer.ProcessAudio(batch));
        streamed.Append(recognizer.Flush());

        var streamedWords = Normalize(streamed.ToString());
        var distance = WordDistance(offline, streamedWords);
        Assert.True(distance <= Math.Max(1, offline.Length / 10),
            $"streamed transcript differs from offline by {distance} words:\n  offline : {string.Join(' ', offline)}\n  streamed: {string.Join(' ', streamedWords)}");
    }

    [ParakeetOnnxFact]
    public void Streaming_UtteranceApi_ShowsPreviewInPartialAndFinalizesOnFlush()
    {
        using var recognizer = new ParakeetTdtRecognizer(ParakeetTestModel.Locate()!);
        var audio = ParakeetTestModel.LoadWav("0003.wav");

        var sawPartial = false;
        var finals = new List<string>();
        foreach (var (batch, _) in Batches(audio, recognizer.SampleRate))
        {
            var result = recognizer.ProcessUtterance(batch);
            sawPartial |= result.Partial.Length > 0;
            if (result.Final is not null)
                finals.Add(result.Final);
        }

        var flushed = recognizer.FlushUtterance();
        if (flushed.Final is not null)
            finals.Add(flushed.Final);

        Assert.True(sawPartial, "no partial text was reported while streaming");
        Assert.NotEmpty(finals);
        Assert.Equal("", flushed.Partial);
        Assert.False(string.IsNullOrWhiteSpace(string.Join(' ', finals)));
    }

    [ParakeetOnnxFact]
    public void ResetStreamingState_DropsBufferedAudioAndPreview()
    {
        using var recognizer = new ParakeetTdtRecognizer(ParakeetTestModel.Locate()!);
        var audio = ParakeetTestModel.LoadWav("0001.wav");

        var half = audio[..(audio.Length / 2)];
        foreach (var (batch, _) in Batches(half, recognizer.SampleRate))
            recognizer.ProcessAudio(batch);

        recognizer.ResetStreamingState();

        Assert.Equal("", recognizer.PartialText);
        Assert.Null(recognizer.Flush());
        Assert.Null(recognizer.FlushUtterance().Final);
    }

    private static IEnumerable<(float[] Batch, double EndSeconds)> Batches(float[] audio, int sampleRate)
    {
        for (var offset = 0; offset < audio.Length; offset += BatchSamples)
        {
            var length = Math.Min(BatchSamples, audio.Length - offset);
            yield return (audio[offset..(offset + length)], (offset + length) / (double)sampleRate);
        }
    }

    private static string[] Normalize(string text) =>
        System.Text.RegularExpressions.Regex.Replace(text.ToLowerInvariant(), @"[^\p{L}\p{N}]+", " ")
            .Split(' ', StringSplitOptions.RemoveEmptyEntries);

    private static int WordDistance(string[] a, string[] b)
    {
        var d = new int[a.Length + 1, b.Length + 1];
        for (var i = 0; i <= a.Length; i++) d[i, 0] = i;
        for (var j = 0; j <= b.Length; j++) d[0, j] = j;
        for (var i = 1; i <= a.Length; i++)
            for (var j = 1; j <= b.Length; j++)
            {
                var cost = a[i - 1] == b[j - 1] ? 0 : 1;
                d[i, j] = Math.Min(Math.Min(d[i - 1, j] + 1, d[i, j - 1] + 1), d[i - 1, j - 1] + cost);
            }
        return d[a.Length, b.Length];
    }
}

/// <summary>Runs the test only when a Parakeet TDT ONNX export is available locally.</summary>
public sealed class ParakeetOnnxFactAttribute : FactAttribute
{
    public ParakeetOnnxFactAttribute()
    {
        if (ParakeetTestModel.Locate() is null)
            Skip = "Parakeet TDT ONNX model not found (models/parakeet-tdt-0.6b-v3-onnx/fp32 or build/parakeet-tdt-fp32).";
    }
}

internal static class ParakeetTestModel
{
    private static readonly string[] Candidates =
    [
        Path.Combine("models", "parakeet-tdt-0.6b-v3-onnx", "fp32"),
        Path.Combine("models", "parakeet-tdt-0.6b-v3-onnx", "int8"),
        Path.Combine("build", "parakeet-tdt-fp32"),
    ];

    public static string? Locate()
    {
        var root = FindRepositoryRoot();
        if (root is null)
            return null;

        foreach (var candidate in Candidates)
        {
            var dir = Path.Combine(root, candidate);
            if (File.Exists(Path.Combine(dir, "encoder-model.onnx")) && ParakeetTdtRecognizer.IsParakeetTdtModel(dir))
                return dir;
        }
        return null;
    }

    public static float[] LoadWav(string fileName)
    {
        var root = FindRepositoryRoot() ?? throw new DirectoryNotFoundException("Repository root not found.");
        var bytes = File.ReadAllBytes(Path.Combine(root, "Test-Audio", "cv17", "en", fileName));

        // 16 kHz mono PCM16 fixtures: locate the data chunk and scale to float.
        var position = 12;
        while (position + 8 <= bytes.Length)
        {
            var id = System.Text.Encoding.ASCII.GetString(bytes, position, 4);
            var size = BinaryPrimitives.ReadInt32LittleEndian(bytes.AsSpan(position + 4));
            if (id == "data")
            {
                var count = Math.Min(size, bytes.Length - position - 8) / 2;
                var samples = new float[count];
                for (var i = 0; i < count; i++)
                    samples[i] = BinaryPrimitives.ReadInt16LittleEndian(bytes.AsSpan(position + 8 + 2 * i)) / 32768f;
                return samples;
            }
            position += 8 + size + (size & 1);
        }
        throw new InvalidDataException("No data chunk.");
    }

    private static string? FindRepositoryRoot()
    {
        var directory = new DirectoryInfo(AppContext.BaseDirectory);
        while (directory is not null)
        {
            if (File.Exists(Path.Combine(directory.FullName, "NemotronSpeech.slnx")))
                return directory.FullName;
            directory = directory.Parent;
        }
        return null;
    }
}
