namespace LVA.Core.Context;

/// <summary>A single speech segment kept in the context buffer.</summary>
public sealed record ContextSegment(
    string Text,
    string Classification,       // question_part | smalltalk | noise | addressing
    DateTimeOffset Timestamp,
    double IntentScore,
    float[]? Embedding = null);

/// <summary>
/// In-memory ring buffer of speech segments (TZ §4.6).
/// Never persisted; cleared on cancel, timeout, and shutdown.
/// </summary>
public sealed class ContextBuffer
{
    private readonly List<ContextSegment> _segments = new();
    private readonly int _capacity;

    public ContextBuffer(int capacity = 64)
    {
        _capacity = capacity;
    }

    public IReadOnlyList<ContextSegment> Segments => _segments;

    /// <summary>
    /// Adds a segment unless it is a near-duplicate of an existing one
    /// (cosine similarity > 0.9 against stored embeddings).
    /// </summary>
    public bool TryAdd(ContextSegment segment)
    {
        if (string.IsNullOrWhiteSpace(segment.Text))
            return false;

        if (segment.Embedding is not null)
        {
            foreach (var existing in _segments)
            {
                if (existing.Embedding is not null &&
                    CosineSimilarity(existing.Embedding, segment.Embedding) > 0.9f)
                    return false; // duplicate — skip
            }
        }

        _segments.Add(segment);
        if (_segments.Count > _capacity)
            _segments.RemoveAt(0);
        return true;
    }

    /// <summary>Concatenates question-relevant segments into the final context.</summary>
    public string AssembleContext()
        => string.Join(' ', _segments
            .Where(s => s.Classification is "question_part" or "addressing")
            .Select(s => s.Text.Trim()));

    public void Clear() => _segments.Clear();

    public static float CosineSimilarity(float[] a, float[] b)
    {
        if (a.Length != b.Length || a.Length == 0)
            return 0f;
        float dot = 0, na = 0, nb = 0;
        for (int i = 0; i < a.Length; i++)
        {
            dot += a[i] * b[i];
            na += a[i] * a[i];
            nb += b[i] * b[i];
        }
        var denom = MathF.Sqrt(na) * MathF.Sqrt(nb);
        return denom < 1e-8f ? 0f : dot / denom;
    }
}
