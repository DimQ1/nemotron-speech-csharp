using VoiceType.Services.Intents;
using Xunit;

namespace VoiceType.Tests;

/// <summary>
/// E2E tests for the MiniLM semantic intent scorer against the real INT8 ONNX
/// model. Skips automatically when the model folder is not present locally
/// (e.g. CI). Model source: LVA repo models/lva/embeddings/l1-minilm.
/// </summary>
public sealed class E2E_VoiceIntentScorerTests
{
    // Candidates: VoiceType data dir first, then the LVA checkout used for dev.
    private static readonly string[] CandidateModelDirs =
    {
        Path.Combine(AppContext.BaseDirectory, "data", "Models", "voice-intents", "l1-minilm"),
        @"e:\Learn\LVA\models\lva\embeddings\l1-minilm",
    };

    private static string? FindModelDir()
        => CandidateModelDirs.FirstOrDefault(d =>
            File.Exists(Path.Combine(d, "onnx", "model_int8.onnx"))
            && File.Exists(Path.Combine(d, "sentencepiece.bpe.model")));

    private static MiniLmIntentScorer? CreateScorer()
    {
        var dir = FindModelDir();
        return dir is null ? null : new MiniLmIntentScorer(dir, VoiceIntentTaxonomy.Default);
    }

    [Fact]
    public async Task MiniLM_ParaphraseCommand_TopCommandIntent_IsStartTyping()
    {
        using var scorer = CreateScorer();
        if (scorer is null) return; // model not available locally — skip

        // A paraphrase NOT covered by L0 lexical triggers — semantics must catch it.
        // Multilingual MiniLM has a high in-language cosine baseline, so the routing-
        // relevant assertion is relative: the paraphrase must classify as start_typing
        // among all command intents, at a moderate confidence.
        var result = await scorer.ScoreAsync("мог бы ты начать вводить текст", CancellationToken.None);

        var dump = string.Join(", ", result.Scores.OrderByDescending(kv => kv.Value).Select(kv => $"{kv.Key}={kv.Value:F3}"));
        var topCommand = result.Scores
            .Where(kv => kv.Key != VoiceIntentIds.Dictation)
            .OrderByDescending(kv => kv.Value)
            .First();

        Assert.Equal(VoiceIntentIds.StartTyping, topCommand.Key);
        Assert.True(topCommand.Value > 0.6f, $"paraphrase start_typing score too low: {dump}");
    }

    [Fact]
    public async Task MiniLM_ExactExemplar_ScoresHigh()
    {
        using var scorer = CreateScorer();
        if (scorer is null) return; // skip

        var result = await scorer.ScoreAsync("начни печатать", CancellationToken.None);
        var startScore = result.Scores.GetValueOrDefault(VoiceIntentIds.StartTyping);

        Assert.True(startScore > 0.7f, $"exact exemplar should score high, got {startScore:F3}");
    }

    [Fact]
    public async Task MiniLM_UnrelatedText_DoesNotConfidentlyMatchCommands()
    {
        using var scorer = CreateScorer();
        if (scorer is null) return; // skip

        var result = await scorer.ScoreAsync("курс доллара вырос на фоне новостей", CancellationToken.None);

        foreach (var (intent, score) in result.Scores)
            Assert.True(score < 0.85f, $"unrelated text matched {intent} at {score:F3} >= threshold");
    }
}
