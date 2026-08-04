using LVA.Core.Context;
using Microsoft.ML.OnnxRuntime;
using Microsoft.ML.OnnxRuntime.Tensors;
using Microsoft.ML.Tokenizers;

namespace LVA.Nlu.Context;

/// <summary>
/// BGE-M3 INT8 context assembler: dense_vecs (1024-dim, pre-normalized by the
/// model) cosine scoring of segments against the aggregated question.
/// Hybrid dense+sparse (α=0.6) — sparse lexical term overlap as the sparse leg.
/// </summary>
public sealed class BgeM3ContextAssembler : IContextAssembler
{
    private const int MaxTokens = 512;
    private readonly InferenceSession _session;
    private readonly SentencePieceTokenizer _tokenizer;
    private readonly float _alpha;
    private bool _disposed;

    public string ModelId { get; }

    public BgeM3ContextAssembler(string modelId, string modelPath, string tokenizerPath, float hybridAlpha = 0.6f)
    {
        ModelId = modelId;
        _alpha = hybridAlpha;
        _session = new InferenceSession(modelPath);
        using var stream = File.OpenRead(tokenizerPath);
        _tokenizer = SentencePieceTokenizer.Create(stream, addBeginningOfSentence: true, addEndOfSentence: true);
    }

    public Task<IReadOnlyList<ContextSegment>> SelectRelevantAsync(
        string question, IReadOnlyList<ContextSegment> segments, CancellationToken ct)
    {
        if (segments.Count == 0)
            return Task.FromResult<IReadOnlyList<ContextSegment>>(Array.Empty<ContextSegment>());

        var questionEmb = EmbedDense(question);
        var questionTerms = Terms(question);

        var scored = segments
            .Select(s =>
            {
                ct.ThrowIfCancellationRequested();
                var dense = ContextBuffer.CosineSimilarity(questionEmb, EmbedDense(s.Text));
                var sparse = LexicalOverlap(questionTerms, Terms(s.Text));
                var hybrid = _alpha * dense + (1 - _alpha) * sparse;
                return (Segment: s, Score: hybrid);
            })
            .OrderByDescending(x => x.Score)
            .ToArray();

        // Keep segments above a relevance floor; always keep at least one.
        var threshold = Math.Max(0.35f, scored[0].Score * 0.6f);
        var selected = scored.Where(x => x.Score >= threshold).Select(x => x.Segment).ToArray();
        return Task.FromResult<IReadOnlyList<ContextSegment>>(
            selected.Length > 0 ? selected : new[] { scored[0].Segment });
    }

    private float[] EmbedDense(string text)
    {
        var ids = _tokenizer.EncodeToIds(text, addBeginningOfSentence: true, addEndOfSentence: true)
            .Take(MaxTokens).ToArray();
        int len = ids.Length;

        var inputIds = new DenseTensor<long>(new[] { 1, MaxTokens });
        var attention = new DenseTensor<long>(new[] { 1, MaxTokens });
        for (int i = 0; i < len; i++)
        {
            inputIds[0, i] = ids[i];
            attention[0, i] = 1;
        }

        var inputs = new List<NamedOnnxValue>
        {
            NamedOnnxValue.CreateFromTensor("input_ids", inputIds),
            NamedOnnxValue.CreateFromTensor("attention_mask", attention)
        };

        using var results = _session.Run(inputs);
        return results.First(r => r.Name == "dense_vecs").AsEnumerable<float>().ToArray();
    }

    private static HashSet<string> Terms(string text)
        => text.ToLowerInvariant()
            .Split(new[] { ' ', ',', '.', '?', '!', ':', ';', '-', '—' }, StringSplitOptions.RemoveEmptyEntries)
            .Where(w => w.Length > 2)
            .ToHashSet();

    private static float LexicalOverlap(HashSet<string> a, HashSet<string> b)
    {
        if (a.Count == 0 || b.Count == 0)
            return 0f;
        var intersection = a.Intersect(b).Count();
        return (float)(2.0 * intersection / (a.Count + b.Count)); // Dice coefficient
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        _session.Dispose();
    }
}
