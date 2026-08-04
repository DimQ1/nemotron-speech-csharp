using LVA.Core.Context;
using Xunit;

namespace LVA.Tests.Core;

public class Unit_ContextBufferTests
{
    private static ContextSegment Seg(string text, string cls = "question_part", float[]? emb = null)
        => new(text, cls, DateTimeOffset.UtcNow, 0.9, emb);

    [Fact]
    public void TryAdd_ShouldRejectEmpty()
    {
        var buf = new ContextBuffer();
        Assert.False(buf.TryAdd(Seg("   ")));
    }

    [Fact]
    public void TryAdd_ShouldDeduplicateByEmbedding()
    {
        var buf = new ContextBuffer();
        var emb = new float[] { 1f, 0f, 0f };
        Assert.True(buf.TryAdd(Seg("как обновить драйвер", emb: emb)));
        // nearly identical embedding (cosine > 0.9)
        Assert.False(buf.TryAdd(Seg("как обновить драйвер повтор", emb: new float[] { 0.99f, 0.05f, 0f })));
        // orthogonal — not a duplicate
        Assert.True(buf.TryAdd(Seg("погода завтра", emb: new float[] { 0f, 1f, 0f })));
        Assert.Equal(2, buf.Segments.Count);
    }

    [Fact]
    public void AssembleContext_ShouldKeepOnlyQuestionAndAddressing()
    {
        var buf = new ContextBuffer();
        buf.TryAdd(Seg("ассистент", "addressing"));
        buf.TryAdd(Seg("как обновить .NET", "question_part"));
        buf.TryAdd(Seg("ха-ха смешно", "smalltalk"));
        buf.TryAdd(Seg("шум телевизора", "noise"));

        var ctx = buf.AssembleContext();
        Assert.Contains("ассистент", ctx);
        Assert.Contains("как обновить .NET", ctx);
        Assert.DoesNotContain("ха-ха", ctx);
        Assert.DoesNotContain("телевизора", ctx);
    }

    [Fact]
    public void RingCapacity_ShouldDropOldest()
    {
        var buf = new ContextBuffer(capacity: 3);
        for (int i = 0; i < 5; i++)
            buf.TryAdd(Seg($"segment {i}"));
        Assert.Equal(3, buf.Segments.Count);
        Assert.Equal("segment 2", buf.Segments[0].Text);
    }

    [Fact]
    public void Clear_ShouldEmptyBuffer()
    {
        var buf = new ContextBuffer();
        buf.TryAdd(Seg("x"));
        buf.Clear();
        Assert.Empty(buf.Segments);
    }

    [Fact]
    public void CosineSimilarity_ShouldBeCorrect()
    {
        Assert.Equal(1f, ContextBuffer.CosineSimilarity(new[] { 1f, 0f }, new[] { 1f, 0f }), 3);
        Assert.Equal(0f, ContextBuffer.CosineSimilarity(new[] { 1f, 0f }, new[] { 0f, 1f }), 3);
    }
}
