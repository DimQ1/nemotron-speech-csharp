using System.IO;
using Microsoft.ML.OnnxRuntime;
using Microsoft.ML.OnnxRuntime.Tensors;
using Microsoft.ML.Tokenizers;

namespace VoiceType.Services.Intents;

/// <summary>
/// L1 semantic scorer (ported from LVA MiniLmEmbeddingModel):
/// paraphrase-multilingual-MiniLM-L12-v2 INT8 ONNX.
/// Pipeline: SentencePiece tokenizer → ONNX (input_ids/attention_mask/token_type_ids)
/// → last_hidden_state → attention-masked mean-pooling → L2 normalize → cosine
/// against cached intent-exemplar embeddings. CPU-only inference.
/// </summary>
public sealed class MiniLmIntentScorer : IIntentScorer
{
    private const int MaxTokens = 128;
    private const int HiddenSize = 384;

    private readonly InferenceSession _session;
    private readonly SentencePieceTokenizer _tokenizer;
    private readonly Dictionary<string, float[]> _exemplarEmbeddings = new();
    private bool _disposed;

    public string ModelId { get; }

    /// <summary>
    /// Expected layout of <paramref name="modelDir"/>:
    ///   onnx/model_int8.onnx, sentencepiece.bpe.model
    /// </summary>
    public MiniLmIntentScorer(string modelDir, VoiceIntentTaxonomy taxonomy, string modelId = "paraphrase-multilingual-MiniLM-L12-v2-int8")
    {
        ModelId = modelId;
        var onnxPath = Path.Combine(modelDir, "onnx", "model_int8.onnx");
        var tokenizerPath = Path.Combine(modelDir, "sentencepiece.bpe.model");

        // CPU-only by design — default session options, no execution-provider config.
        _session = new InferenceSession(onnxPath);

        using (var stream = File.OpenRead(tokenizerPath))
        {
            // XLM-RoBERTa convention: emit <s> and </s> special tokens.
            _tokenizer = SentencePieceTokenizer.Create(stream, addBeginningOfSentence: true, addEndOfSentence: true);
        }

        CacheExemplars(taxonomy);
    }

    private void CacheExemplars(VoiceIntentTaxonomy taxonomy)
    {
        foreach (var intent in taxonomy.Intents)
        {
            foreach (var exemplar in intent.Exemplars)
                _exemplarEmbeddings[$"{intent.Id}::{exemplar}"] = Embed(exemplar);
        }
    }

    public Task<VoiceIntentScores> ScoreAsync(string text, CancellationToken ct)
    {
        var emb = Embed(text);
        var scores = new Dictionary<string, float>(StringComparer.OrdinalIgnoreCase);

        foreach (var (key, exemplarEmb) in _exemplarEmbeddings)
        {
            ct.ThrowIfCancellationRequested();
            var intentId = key.Split("::", 2)[0];
            var cos = CosineSimilarity(emb, exemplarEmb);
            if (!scores.TryGetValue(intentId, out var current) || cos > current)
                scores[intentId] = cos;
        }

        var top = scores.OrderByDescending(kv => kv.Value).FirstOrDefault();
        return Task.FromResult(new VoiceIntentScores(scores, top.Key ?? "", top.Value));
    }

    private float[] Embed(string text)
    {
        IReadOnlyList<int> encoded = _tokenizer.EncodeToIds(text, addBeginningOfSentence: true, addEndOfSentence: true);
        var ids = encoded.Take(MaxTokens).ToArray();
        int len = ids.Length;

        var inputIds = new DenseTensor<long>(new[] { 1, MaxTokens });
        var attention = new DenseTensor<long>(new[] { 1, MaxTokens });
        var tokenTypes = new DenseTensor<long>(new[] { 1, MaxTokens });
        for (int i = 0; i < len; i++)
        {
            inputIds[0, i] = ids[i];
            attention[0, i] = 1;
        }

        var inputs = new List<NamedOnnxValue>
        {
            NamedOnnxValue.CreateFromTensor("input_ids", inputIds),
            NamedOnnxValue.CreateFromTensor("attention_mask", attention),
            NamedOnnxValue.CreateFromTensor("token_type_ids", tokenTypes)
        };

        using var results = _session.Run(inputs);
        var hidden = results.First(r => r.Name == "last_hidden_state").AsTensor<float>();

        // Attention-masked mean pooling.
        var pooled = new float[HiddenSize];
        for (int t = 0; t < len; t++)
            for (int h = 0; h < HiddenSize; h++)
                pooled[h] += hidden[0, t, h];
        for (int h = 0; h < HiddenSize; h++)
            pooled[h] /= Math.Max(len, 1);

        // L2 normalize.
        var norm = MathF.Sqrt(pooled.Sum(v => v * v));
        if (norm > 1e-8f)
            for (int h = 0; h < HiddenSize; h++)
                pooled[h] /= norm;
        return pooled;
    }

    private static float CosineSimilarity(float[] a, float[] b)
    {
        if (a.Length != b.Length || a.Length == 0) return 0f;
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

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        _session.Dispose();
    }
}
