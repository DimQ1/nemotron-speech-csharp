using System.Diagnostics;
using LVA.Core.Configuration;
using LVA.Core.Intents;
using LVA.Nlu.Intents;
using LVA.Nlu.Vad;
using Xunit;

namespace LVA.Tests.Nlu;

/// <summary>
/// Integration tests against the real ONNX models in build/lva-models.
/// Skipped automatically when models are absent (CI without downloads).
/// </summary>
public class E2E_ModelInferenceTests
{
    private static string RepoRoot
        => Path.GetFullPath(Path.Combine(AppContext.BaseDirectory, "..", "..", "..", "..", ".."));

    private static string VadModel => Path.Combine(RepoRoot, "build", "lva-models", "vad", "onnx", "model.onnx");
    private static string MiniLmModel => Path.Combine(RepoRoot, "build", "lva-models", "l1-minilm", "onnx", "model_int8.onnx");
    private static string MiniLmTokenizer => Path.Combine(RepoRoot, "build", "lva-models", "l1-minilm", "sentencepiece.bpe.model");

    [Fact]
    public void Vad_Silence_ShouldProduceLowProbability()
    {
        if (!File.Exists(VadModel)) return; // skip without models

        using var vad = new SileroVadService(new VadOptions { ModelPath = VadModel });
        float lastProb = 1f;
        vad.SpeechFrame += p => lastProb = p;

        var silence = new float[SileroVadService.FrameSamples];
        for (int i = 0; i < 10; i++)
            vad.AcceptFrame(silence);

        Assert.True(lastProb < 0.2f, $"Expected low speech probability on silence, got {lastProb}");
        Assert.False(vad.IsSpeechActive);
    }

    [Fact]
    public void Vad_LoudNoiseFrame_ShouldNotThrow()
    {
        if (!File.Exists(VadModel)) return;

        using var vad = new SileroVadService(new VadOptions { ModelPath = VadModel });
        var rng = new Random(42);
        var noise = new float[SileroVadService.FrameSamples];
        for (int i = 0; i < noise.Length; i++)
            noise[i] = (float)(rng.NextDouble() * 2 - 1) * 0.3f;

        vad.AcceptFrame(noise); // must not throw; probability may be anything
    }

    [Fact]
    public async Task MiniLm_AddressingShouldScoreHigherThanBackground()
    {
        if (!File.Exists(MiniLmModel) || !File.Exists(MiniLmTokenizer)) return;

        using var model = new MiniLmEmbeddingModel("test", MiniLmModel, MiniLmTokenizer, IntentTaxonomy.Default);

        var addr = await model.ScoreAsync(new IntentInput("ассистент, слушай меня"), CancellationToken.None);
        var bg = await model.ScoreAsync(new IntentInput("вчера шёл сильный дождь и гремел гром"), CancellationToken.None);

        Assert.True(addr.Scores[IntentIds.Addressing] > bg.Scores[IntentIds.Addressing],
            $"addressing score {addr.Scores[IntentIds.Addressing]:F3} should exceed background {bg.Scores[IntentIds.Addressing]:F3}");
    }

    [Fact]
    public async Task MiniLm_Latency_ShouldBeWithinBudget()
    {
        if (!File.Exists(MiniLmModel) || !File.Exists(MiniLmTokenizer)) return;

        using var model = new MiniLmEmbeddingModel("test", MiniLmModel, MiniLmTokenizer, IntentTaxonomy.Default);

        // warmup (JIT + model pages)
        for (int i = 0; i < 3; i++)
            await model.ScoreAsync(new IntentInput("прогрев"), CancellationToken.None);

        var sw = Stopwatch.StartNew();
        const int runs = 10;
        for (int i = 0; i < runs; i++)
            await model.ScoreAsync(new IntentInput("ассистент, как дела сегодня"), CancellationToken.None);
        sw.Stop();

        var avgMs = sw.Elapsed.TotalMilliseconds / runs;
        // Budget is generous: under parallel test load (BGE-M3 E2E) CPU is contended.
        Assert.True(avgMs < 150, $"L1 scoring too slow: {avgMs:F1} ms/call (budget 150 ms incl. tokenization)");
    }

    [Fact]
    public async Task MiniLm_RussianAndEnglish_ShouldShareSemanticSpace()
    {
        if (!File.Exists(MiniLmModel) || !File.Exists(MiniLmTokenizer)) return;

        using var model = new MiniLmEmbeddingModel("test", MiniLmModel, MiniLmTokenizer, IntentTaxonomy.Default);

        // "assistant" exemplar should match both "ассистент" (ru) and "hey assistant" (en)
        var ru = await model.ScoreAsync(new IntentInput("ассистент"), CancellationToken.None);
        var en = await model.ScoreAsync(new IntentInput("hey assistant"), CancellationToken.None);

        Assert.True(ru.Scores[IntentIds.Addressing] > 0.6f, $"ru addressing {ru.Scores[IntentIds.Addressing]:F3} too low");
        Assert.True(en.Scores[IntentIds.Addressing] > 0.6f, $"en addressing {en.Scores[IntentIds.Addressing]:F3} too low");
    }
}
