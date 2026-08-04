using LVA.Core.Context;
using LVA.Nlu.Context;
using Xunit;

namespace LVA.Tests.Nlu;

/// <summary>BGE-M3 L3 context assembly tests (TZ §9 DoD): on a long
/// "thinking aloud" dialog the assembled context excludes off-topic segments.</summary>
public class E2E_BgeM3Tests
{
    private static string RepoRoot
        => Path.GetFullPath(Path.Combine(AppContext.BaseDirectory, "..", "..", "..", "..", ".."));

    private static string BgeModel => Path.Combine(RepoRoot, "build", "lva-models", "l3-bgem3", "model_quantized.onnx");
    private static string BgeTokenizer => Path.Combine(RepoRoot, "build", "lva-models", "l3-bgem3", "sentencepiece.bpe.model");

    private static bool Available => File.Exists(BgeModel) && File.Exists(BgeTokenizer);

    [Fact]
    public async Task SelectRelevant_ShouldDropOffTopicSegments()
    {
        if (!Available) return;

        using var assembler = new BgeM3ContextAssembler("bge-m3", BgeModel, BgeTokenizer);

        var segments = new List<ContextSegment>
        {
            new("как обновить драйвер видеокарты NVIDIA", "question_part", DateTimeOffset.UtcNow, 0.9),
            new("драйвер скачивается с официального сайта", "question_part", DateTimeOffset.UtcNow, 0.9),
            new("кстати вчера был хороший фильм про космос", "question_part", DateTimeOffset.UtcNow, 0.9),
            new("после установки нужно перезагрузить компьютер", "question_part", DateTimeOffset.UtcNow, 0.9),
            new("рецепт борща со свежей капустой", "question_part", DateTimeOffset.UtcNow, 0.9),
        };

        var selected = await assembler.SelectRelevantAsync(
            "как обновить драйвер видеокарты и что делать после установки",
            segments, CancellationToken.None);

        var texts = selected.Select(s => s.Text).ToArray();
        Assert.Contains(texts, t => t.Contains("драйвер"));
        Assert.DoesNotContain(texts, t => t.Contains("борща"));
        Assert.DoesNotContain(texts, t => t.Contains("фильм"));
    }

    [Fact]
    public async Task SelectRelevant_EmptyBuffer_ShouldReturnEmpty()
    {
        if (!Available) return;

        using var assembler = new BgeM3ContextAssembler("bge-m3", BgeModel, BgeTokenizer);
        var selected = await assembler.SelectRelevantAsync("вопрос",
            Array.Empty<ContextSegment>(), CancellationToken.None);
        Assert.Empty(selected);
    }
}
